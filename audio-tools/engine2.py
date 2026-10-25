"""Diesel generator v2: event-based combustion model.

Every cylinder firing is an individual transient (body thump + injector clatter), timed from the crankshaft
angle, so cranking, misfires, catching, governor overshoot and load sag all sound physically driven.
Six-cylinder four-stroke: 3 firings per crank revolution. 25 Hz crank (1500 rpm) = 75 firings/s.
"""
import numpy as np
from scipy import signal
import make_audio as A

SR = A.SR
rng = np.random.default_rng(2026)
RUN_HZ = 25.0


def bp(x, lo, hi, order=2):
    return A.butter(x, lo=lo, hi=hi, order=order)


# ---------------------------------------------------------------- transients
def kernel_fire(strength=1.0):
    n = int(0.06 * SR)
    t = np.arange(n) / SR
    body_f = rng.uniform(85, 130)
    thump = np.sin(2 * np.pi * body_f * t) * np.exp(-t / 0.011)
    knock = np.sin(2 * np.pi * rng.uniform(620, 900) * t) * np.exp(-t / 0.005) * 0.35
    clatter = bp(rng.standard_normal(n), 1800, 5200) * np.exp(-t / 0.0035) * 0.55
    return strength * (thump + knock + clatter)


def kernel_compress():
    """Cylinder compressing without firing: soft puff/creak."""
    n = int(0.05 * SR)
    t = np.arange(n) / SR
    puff = bp(rng.standard_normal(n), 120, 900) * np.exp(-t / 0.012) * 0.7
    return puff + np.sin(2 * np.pi * 70 * t) * np.exp(-t / 0.015) * 0.55


def kernel_backfire():
    n = int(0.35 * SR)
    t = np.arange(n) / SR
    bang = A.butter(rng.standard_normal(n), hi=1800) * np.exp(-t / 0.035) * 2.2
    bang += np.sin(2 * np.pi * 55 * t) * np.exp(-t / 0.06) * 1.6
    return bang


def add(buf, k, at):
    if at >= len(buf):
        return
    m = min(len(k), len(buf) - at)
    buf[at : at + m] += k[:m]


# ---------------------------------------------------------------- engine render
def render(rpm, fire_prob, fire_gain=None, backfires=(), alt_env=None, starter_env=None, load_noise=1.0):
    """rpm: crank Hz per sample. fire_prob: chance each firing event actually fires."""
    n = len(rpm)
    t = np.arange(n) / SR
    fire_gain = np.ones(n) if fire_gain is None else fire_gain
    events = np.zeros(n)

    # firing events from crank angle (3 per revolution)
    angle = np.cumsum(rpm) / SR * 3.0
    idx = np.where(np.diff(np.floor(angle)) > 0)[0]
    for i in idx:
        if rng.random() < fire_prob[i]:
            add(events, kernel_fire(fire_gain[i] * rng.uniform(0.8, 1.15)), i)
        else:
            add(events, kernel_compress() * min(1.0, rpm[i] / 4.0), i)
    for bt in backfires:
        add(events, kernel_backfire(), int(bt * SR))

    # engine block + exhaust resonances
    block = bp(events, 140, 260) * 0.8
    exhaust = A.butter(events, hi=220, order=2) * 1.1
    body = events * 0.55 + block + exhaust

    # continuous mechanical noise, scales with speed
    mech = bp(rng.standard_normal(n), 300, 3000) * 0.12 * (rpm / RUN_HZ) ** 1.3 * load_noise
    out = body + mech

    # alternator: 50 Hz locked to crank (25 Hz -> 50 Hz), + transformer buzz
    if alt_env is not None:
        ph = 2 * np.pi * np.cumsum(rpm * 2.0) / SR
        alt = (np.sin(ph) + 0.55 * np.sin(2 * ph) + 0.3 * np.sin(3 * ph) + 0.2 * np.sin(5 * ph)
               + 0.08 * signal.square(2 * ph))
        out += alt * 0.32 * alt_env

    # cooling fan / turbo whine
    fan_ph = 2 * np.pi * np.cumsum(rpm * 70.0) / SR
    out += np.sin(fan_ph) * 0.05 * np.clip((rpm - 6) / (RUN_HZ - 6), 0, 1.3)

    # starter motor: ~11x crank speed, groans on each compression stroke
    if starter_env is not None:
        comp = 0.5 + 0.5 * np.cos(2 * np.pi * angle)             # compression load per firing interval
        st_f = np.clip(rpm, 0, None) * 11.0 * (1 - 0.18 * comp) + 60
        st_ph = 2 * np.pi * np.cumsum(st_f) / SR
        starter = (signal.sawtooth(st_ph) * 0.5 + np.sin(2 * st_ph) * 0.25)
        starter = A.butter(starter, hi=2400, order=2) * (0.75 + 0.25 * (1 - comp))
        grind = bp(rng.standard_normal(n), 2200, 6000) * 0.08
        out += (starter * 1.25 + grind * 2.2) * starter_env

    out = A.butter(out, lo=25, order=2)
    return out


def second_order(t, start, target, t0, zeta=0.45, wn=2.4):
    """Governor-like response with overshoot."""
    tau = np.clip(t - t0, 0, None)
    wd = wn * np.sqrt(1 - zeta ** 2)
    step = 1 - np.exp(-zeta * wn * tau) * (np.cos(wd * tau) + zeta / np.sqrt(1 - zeta ** 2) * np.sin(wd * tau))
    return start + (target - start) * step


def cranking_rpm(t, t_on, t_off, crank_hz=3.4):
    """Starter speed: spins up quickly, slows on each compression, stops after disengaging."""
    up = np.clip((t - t_on) / 0.3, 0, 1) ** 0.7
    down = np.where(t > t_off, np.exp(-(t - t_off) / 0.35), 1.0)
    return crank_hz * up * down


def env_between(t, a, b, fin=0.05, fout=0.15):
    e = np.clip((t - a) / fin, 0, 1) * np.clip((b - t) / fout + 1, 0, 1)
    e[t > b + fout] = 0
    return e


def solenoid(at):
    return A.breaker_clunk(0.6, 0.0) * 0.7, at


# ---------------------------------------------------------------- sounds
def start_success(dur=10.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    t_on, t_catch = 0.25, 2.3
    crank = cranking_rpm(t, t_on, 99)
    run = second_order(t, 3.4, RUN_HZ, t_catch, zeta=0.42, wn=2.3)
    rpm = np.where(t < t_catch, crank, run)
    # load applied (alternator excitation) -> governor sag and recovery
    t_load = t_catch + 3.4
    sag = np.where(t > t_load, -2.2 * np.exp(-(t - t_load) / 0.55) * np.sin(np.clip((t - t_load) * 3.0, 0, np.pi)), 0)
    rpm = A.smooth(rpm + sag, 600)

    fire_prob = np.interp(t, [0, 0.9, 1.6, 2.1, t_catch, t_catch + 0.4], [0, 0, 0.18, 0.35, 0.8, 1.0])
    fire_gain = np.interp(t, [0, t_catch, t_catch + 1.0, dur], [0.7, 0.9, 1.15, 1.0])
    starter_env = env_between(t, t_on, t_catch + 0.35, fin=0.03, fout=0.25)
    alt_env = np.clip((t - t_load) / 0.9, 0, 1)

    x = render(rpm, fire_prob, fire_gain, alt_env=alt_env, starter_env=starter_env)
    s, _ = solenoid(0)
    add(x, s, 0)
    add(x, A.breaker_clunk(0.9, 0.0) * 0.8, int(t_load * SR))          # excitation contactor
    add(x, A.breaker_clunk(0.4, 0.0) * 0.35, int((t_catch + 0.35) * SR))  # starter pinion out
    x = A.reverb(x, rt60=1.1, wet=0.22, bright=5500)
    return A.fade(x[:n], 0.002, 0.02)


def start_fail_crank(dur=5.2):
    """Cranks, a few weak pops, never catches."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    t_on, t_off = 0.25, 3.4
    rpm = A.smooth(cranking_rpm(t, t_on, t_off, 3.2), 400)
    fire_prob = np.interp(t, [0, 1.2, 2.2, 3.0, t_off], [0, 0.05, 0.22, 0.12, 0])
    fire_gain = np.full(n, 0.6)
    starter_env = env_between(t, t_on, t_off, fin=0.03, fout=0.3)
    x = render(rpm, fire_prob, fire_gain, starter_env=starter_env, load_noise=0.6)
    s, _ = solenoid(0)
    add(x, s, 0)
    add(x, A.breaker_clunk(0.4, 0.0) * 0.4, int(t_off * SR))
    x = A.reverb(x, rt60=1.1, wet=0.22, bright=5500)
    return A.trim_tail(A.fade(x[:n], 0.002, 0.3), 2e-3)


def start_fail_sputter(dur=7.0):
    """Catches for a moment, coughs, backfires and dies."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    t_on, t_catch = 0.25, 1.9
    crank = cranking_rpm(t, t_on, t_catch + 0.3, 3.4)
    rise = second_order(t, 3.4, 11.0, t_catch, zeta=0.6, wn=2.8)
    t_die = 3.6
    fall = np.where(t > t_die, rise * np.exp(-(t - t_die) / 0.9), rise)
    rpm = np.where(t < t_catch, crank, fall)
    rpm = A.smooth(rpm, 600)
    fire_prob = np.interp(t, [0, 1.0, 1.7, t_catch, 2.6, 3.1, t_die, t_die + 0.6, dur],
                          [0, 0.1, 0.35, 0.85, 0.7, 0.4, 0.25, 0.0, 0.0])
    fire_gain = np.interp(t, [0, t_catch, 3.0, dur], [0.7, 1.05, 0.8, 0.5])
    starter_env = env_between(t, t_on, t_catch + 0.3, fin=0.03, fout=0.25)
    x = render(rpm, fire_prob, fire_gain, backfires=(3.25,), starter_env=starter_env)
    s, _ = solenoid(0)
    add(x, s, 0)
    x = A.reverb(x, rt60=1.1, wet=0.24, bright=5500)
    return A.trim_tail(A.fade(x[:n], 0.002, 0.5), 2e-3)


def running_loop(dur=8.0, xfade=1.0):
    total = dur + xfade
    n = int(total * SR)
    t = np.arange(n) / SR
    rpm = RUN_HZ + 0.18 * np.sin(2 * np.pi * 0.5 * t)       # 4 governor cycles per loop
    x = render(rpm, np.ones(n), np.ones(n), alt_env=np.ones(n))
    m, k = int(dur * SR), int(xfade * SR)
    w = np.linspace(0, 1, k)
    loop = x[:m].copy()
    loop[:k] = x[:k] * w + x[m : m + k] * (1 - w)
    return A.reverb(loop, rt60=1.1, wet=0.22, bright=5500)[:m]


def spindown(dur=6.5):
    n = int(dur * SR)
    t = np.arange(n) / SR
    rpm = RUN_HZ * np.exp(-t / 1.5)
    fire_prob = np.interp(t, [0, 0.25, 0.6], [1, 0.3, 0])        # fuel cut
    alt_env = np.clip(1 - t / 0.5, 0, 1)
    x = render(rpm, fire_prob, np.ones(n), alt_env=alt_env)
    add(x, A.breaker_clunk(0.8, 0.0) * 0.6, 0)                      # breaker opens
    x = A.reverb(x, rt60=1.1, wet=0.22, bright=5500)
    return A.fade(x[:n], 0.002, 0.6)


if __name__ == "__main__":
    import soundfile as sf
    A.OUT = "./out2"
    import os
    os.makedirs(A.OUT, exist_ok=True)

    loop = running_loop()
    target = np.sqrt(np.mean(loop ** 2))

    def match_tail(x, seconds=0.6):
        r = np.sqrt(np.mean(x[-int(seconds * SR):] ** 2))
        return x * (target / r)

    def match_head(x, seconds=0.3):
        r = np.sqrt(np.mean(x[: int(seconds * SR)] ** 2))
        return x * (target / r)

    loop_peak = 0.6
    scale = loop_peak / np.max(np.abs(loop))       # common scale so all generator files match the loop
    up = match_tail(start_success()) * scale
    down = match_head(spindown()) * scale
    f1 = start_fail_crank()
    f2 = start_fail_sputter()
    # fails: same perceived loudness as the start's cranking section
    crank_rms = np.sqrt(np.mean(up[int(0.5 * SR):int(2.0 * SR)] ** 2))
    f1 *= crank_rms / np.sqrt(np.mean(f1[int(0.5 * SR):int(2.0 * SR)] ** 2))
    f2 *= crank_rms / np.sqrt(np.mean(f2[int(0.5 * SR):int(1.6 * SR)] ** 2))

    def write(name, x):
        peak = np.max(np.abs(x))
        if peak > 0.98:
            x = np.tanh(x / peak * 1.2) / np.tanh(1.2) * 0.95
        sf.write(f"{A.OUT}/{name}.wav", x.astype(np.float32), SR, subtype="PCM_16")
        r = lambda s: np.sqrt(np.mean(s ** 2))
        print(f"{name:22s} {len(x)/SR:5.2f}s peak {np.max(np.abs(x)):.2f} rms {r(x):.3f} head {r(x[:SR//4]):.3f} tail {r(x[-SR//2:]):.3f}")

    write("generator_loop", loop * scale)
    write("generator_spinup", up)
    write("generator_spindown", down)
    write("generator_fail1", f1)
    write("generator_fail2", f2)
