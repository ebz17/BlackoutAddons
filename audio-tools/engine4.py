"""600 kVA V12 genset, v4 - more 'alive'.

On top of engine3:
  * every cylinder is slightly different (strength, timing, tone) -> half-order 'lope' at 12.5 Hz,
    the uneven pulse real engines have and synthesized ones usually lack
  * the two cylinder banks exhaust through separate ducts with different resonances
  * valve-train ticking, fan roar that pulses with the blades, turbo whoosh + whine
  * air starter: valve pop, air-motor whine, pinion 'brrr' on each compression, exhaust-air burst
  * room: early reflections, low room-mode boost, long concrete tail
"""
import numpy as np
from scipy import signal
import make_audio as A
from engine2 import second_order, add, bp

SR = A.SR
RUN_HZ = 25.0
CYL = 12
FIRE_ORDER_BANK = [0, 1] * 6                    # alternating banks A/B
rng = np.random.default_rng(4012)

# fixed per-cylinder character (same engine every time it runs)
CYL_GAIN = 1.0 + rng.normal(0, 0.13, CYL)
CYL_JIT = rng.normal(0, 0.00035, CYL)          # firing timing error, seconds
CYL_TONE = rng.uniform(0.9, 1.12, CYL)


def k_fire(strength, tone):
    n = int(0.055 * SR)
    t = np.arange(n) / SR
    attack = 1 - np.exp(-t / 0.0012)
    thump = np.sin(2 * np.pi * 68 * tone * t) * np.exp(-t / 0.017) * attack
    body = np.sin(2 * np.pi * 165 * tone * t + 0.6) * np.exp(-t / 0.009) * 0.45 * attack
    knock = bp(rng.standard_normal(n), 1300, 3800) * np.exp(-t / 0.0022) * 0.16
    return strength * (thump + body + knock)


def k_compress(level):
    n = int(0.06 * SR)
    t = np.arange(n) / SR
    return level * (bp(rng.standard_normal(n), 80, 650) * np.exp(-t / 0.016) * 0.6
                    + np.sin(2 * np.pi * 48 * t) * np.exp(-t / 0.02) * 0.5)


def k_tick():
    n = int(0.01 * SR)
    t = np.arange(n) / SR
    return bp(rng.standard_normal(n), 2800, 7000) * np.exp(-t / 0.0012)


def k_whump():
    n = int(0.7 * SR)
    t = np.arange(n) / SR
    return (A.butter(rng.standard_normal(n), hi=380) * np.exp(-t / 0.09) * 2.0
            + np.sin(2 * np.pi * 36 * t) * np.exp(-t / 0.14) * 1.9)


def peak_eq(x, f0, gain_db, q=1.0):
    b, a = signal.iirpeak(f0, q, fs=SR)
    g = 10 ** (gain_db / 20) - 1
    return x + g * signal.lfilter(b, a, x)


def room(x, wet=0.27):
    out = np.zeros(len(x) + int(0.5 * SR))
    for d, g in [(0, 1.0), (0.023, 0.30), (0.041, 0.22), (0.067, 0.16)]:
        k = int(d * SR)
        out[k:k + len(x)] += x * g
    out = peak_eq(out, 55, 4.0, 0.9)                 # concrete room mode
    return A.reverb(out, rt60=1.8, wet=wet, bright=4800)


def render(rpm, fire_prob, fire_gain, alt_env=None, air=None, fan_scale=1.0, whumps=()):
    n = len(rpm)
    t = np.arange(n) / SR
    bank = [np.zeros(n), np.zeros(n)]
    ticks = np.zeros(n)

    angle = np.cumsum(rpm) / SR * (CYL / 2)          # 6 firings per crank rev
    idx = np.where(np.diff(np.floor(angle)) > 0)[0]
    for j, i in enumerate(idx):
        c = j % CYL
        at = int(i + CYL_JIT[c] * SR)
        if at < 0 or at >= n:
            continue
        b = FIRE_ORDER_BANK[c]
        if rng.random() < fire_prob[i]:
            add(bank[b], k_fire(fire_gain[i] * CYL_GAIN[c] * rng.uniform(0.94, 1.06), CYL_TONE[c]), at)
        else:
            add(bank[b], k_compress(min(1.0, rpm[i] / 3.0) * CYL_GAIN[c]), at)
        if rpm[i] > 2:
            add(ticks, k_tick() * rng.uniform(0.5, 1.0), at + int(0.25 / max(rpm[i], 1) / 6 * SR))
    for w in whumps:
        add(bank[0], k_whump(), int(w * SR))

    # two exhaust ducts with different resonances
    ex_a = A.butter(bank[0], hi=150, order=3) * 1.5 + bp(bank[0], 85, 120) * 0.8
    ex_b = A.butter(bank[1], hi=165, order=3) * 1.5 + bp(bank[1], 100, 140) * 0.8
    block = bp(bank[0] + bank[1], 120, 340) * 0.65
    out = (bank[0] + bank[1]) * 0.3 + ex_a + ex_b + block + ticks * 0.06 * np.clip(rpm / RUN_HZ, 0, 1)

    speed = np.clip(rpm / RUN_HZ, 0, 1.3)

    # radiator fan: broadband roar pulsing at blade-pass + soft tone
    bpf = rpm * 0.75 * 8
    fan_ph = 2 * np.pi * np.cumsum(bpf) / SR
    roar = A.butter(bp(rng.standard_normal(n), 110, 2600, order=2), hi=1600, order=1)
    roar *= 1.0 + 0.35 * np.sin(fan_ph)
    out += (roar * 0.55 + np.sin(fan_ph) * 0.05) * speed ** 2.2 * fan_scale

    # twin turbos: whoosh + slightly detuned whine
    spool = np.clip((speed - 0.45) / 0.55, 0, 1) ** 1.6
    out += bp(rng.standard_normal(n), 1200, 4200) * 0.07 * spool
    for ratio, amp in ((118.0, 0.035), (120.7, 0.03)):
        out += np.sin(2 * np.pi * np.cumsum(rpm * ratio * (0.6 + 0.4 * speed)) / SR) * amp * spool

    out += bp(rng.standard_normal(n), 250, 2000) * 0.08 * speed ** 1.4

    if alt_env is not None:
        ph = 2 * np.pi * np.cumsum(rpm * 2.0) / SR
        alt = 0.7 * np.sin(ph) + 0.9 * np.sin(2 * ph) + 0.3 * np.sin(4 * ph) + 0.18 * np.sin(6 * ph)
        out += alt * 0.24 * alt_env

    if air is not None:
        air_env, release_at = air
        comp = 0.5 + 0.5 * np.cos(2 * np.pi * angle)            # load on each compression
        motor_f = (2200 + 3800 * np.clip(rpm / 4.0, 0, 1)) * (1 - 0.08 * comp)
        whine = np.sin(2 * np.pi * np.cumsum(motor_f) / SR) * 0.06
        hiss = bp(rng.standard_normal(n), 1500, 10000) * 0.45
        brrr = bp(rng.standard_normal(n), 300, 1800) * 0.35 * (0.4 + 0.6 * comp)   # pinion/gear rattle
        out += (whine + hiss + brrr) * air_env
        if release_at is not None:                              # starter exhaust burst when it lets go
            k = int(0.9 * SR)
            tt = np.arange(k) / SR
            burst = bp(rng.standard_normal(k), 900, 9000) * np.exp(-tt / 0.25) * 0.7
            add(out, burst, int(release_at * SR))

    out = A.butter(out, lo=22, order=2)
    return np.tanh(out * 0.9) / 0.9                                   # a little weight/saturation


def valve_pop():
    n = int(0.3 * SR)
    t = np.arange(n) / SR
    return (bp(rng.standard_normal(n), 2000, 9500) * np.exp(-t / 0.012) * 1.2
            + np.sin(2 * np.pi * 850 * t) * np.exp(-t / 0.025) * 0.35)


def air_env(t, on, off):
    return np.clip((t - on) / 0.03, 0, 1) * np.clip(1 - (t - off) / 0.25, 0, 1)


def start_success(dur=11.5):
    n = int(dur * SR); t = np.arange(n) / SR
    t_air, t_catch = 0.4, 1.55
    crank = 3.8 * np.clip((t - t_air) / 0.22, 0, 1) ** 0.6
    run = second_order(t, 3.8, RUN_HZ, t_catch, zeta=0.48, wn=1.85)
    t_load = t_catch + 4.2
    sag = np.where(t > t_load, -1.7 * np.exp(-(t - t_load) / 0.7) * np.sin(np.clip((t - t_load) * 2.6, 0, np.pi)), 0)
    rpm = A.smooth(np.where(t < t_catch, crank, run) + sag, 900)
    fire_prob = np.interp(t, [0, 1.05, 1.35, t_catch, t_catch + 0.3], [0, 0, 0.3, 0.9, 1.0])
    fire_gain = np.interp(t, [0, t_catch, t_catch + 1.6, dur], [0.7, 1.0, 1.15, 1.0])
    off = t_catch + 0.3
    x = render(rpm, fire_prob, fire_gain, alt_env=np.clip((t - t_load) / 1.0, 0, 1),
               air=(air_env(t, t_air, off), off), whumps=(t_catch - 0.05,))
    add(x, valve_pop() * 0.8, int(t_air * SR))
    add(x, A.breaker_clunk(1.0, 0.0) * 0.8, int(t_load * SR))
    return A.fade(room(x)[:n], 0.002, 0.02)


def start_fail_air(dur=5.2):
    n = int(dur * SR); t = np.arange(n) / SR
    t_air, t_off = 0.4, 3.3
    rpm = 3.5 * np.clip((t - t_air) / 0.22, 0, 1) ** 0.6
    rpm = A.smooth(np.where(t > t_off, rpm * np.exp(-(t - t_off) / 0.45), rpm), 500)
    fire_prob = np.interp(t, [0, 1.0, 2.0, 2.9, t_off], [0, 0.04, 0.16, 0.08, 0])
    x = render(rpm, fire_prob, np.full(n, 0.6), air=(air_env(t, t_air, t_off), t_off), fan_scale=0.5)
    add(x, valve_pop() * 0.8, int(t_air * SR))
    return A.trim_tail(A.fade(room(x)[:n], 0.002, 0.3), 2e-3)


def start_fail_stall(dur=7.2):
    n = int(dur * SR); t = np.arange(n) / SR
    t_air, t_catch, t_die = 0.4, 1.65, 3.9
    crank = 3.8 * np.clip((t - t_air) / 0.22, 0, 1) ** 0.6
    rise = second_order(t, 3.8, 12.0, t_catch, zeta=0.65, wn=2.4)
    rpm = A.smooth(np.where(t < t_catch, crank, np.where(t > t_die, rise * np.exp(-(t - t_die) / 1.0), rise)), 700)
    fire_prob = np.interp(t, [0, 1.25, t_catch, 2.4, 3.0, t_die, t_die + 0.5, dur], [0, 0.25, 0.85, 0.55, 0.3, 0.15, 0, 0])
    fire_gain = np.interp(t, [0, t_catch, 3.0, dur], [0.7, 1.1, 0.8, 0.5])
    off = t_catch + 0.3
    x = render(rpm, fire_prob, fire_gain, air=(air_env(t, t_air, off), off), whumps=(t_catch - 0.05, 3.35))
    add(x, valve_pop() * 0.8, int(t_air * SR))
    return A.trim_tail(A.fade(room(x)[:n], 0.002, 0.5), 2e-3)


def running_loop(dur=8.0, xfade=1.0):
    n = int((dur + xfade) * SR); t = np.arange(n) / SR
    rpm = RUN_HZ + 0.12 * np.sin(2 * np.pi * 0.375 * t)
    x = render(rpm, np.ones(n), np.ones(n), alt_env=np.ones(n))
    m, k = int(dur * SR), int(xfade * SR)
    w = np.linspace(0, 1, k)
    loop = x[:m].copy()
    loop[:k] = x[:k] * w + x[m:m + k] * (1 - w)
    return room(loop)[:m]


def shutdown(dur=8.5):
    n = int(dur * SR); t = np.arange(n) / SR
    rpm = RUN_HZ * np.exp(-t / 2.2)
    x = render(rpm, np.interp(t, [0, 0.2, 0.5], [1, 0.3, 0]), np.ones(n), alt_env=np.clip(1 - t / 0.4, 0, 1))
    env = np.sqrt(A.smooth(x ** 2, int(0.25 * SR))) + 1e-5
    gain = np.minimum(1.0, env[int(0.05 * SR)] * np.exp(-t / 2.0) / env)
    x *= A.smooth(gain, int(0.1 * SR))
    add(x, A.breaker_clunk(1.0, 0.0) * 0.7, 0)
    return A.fade(room(x)[:n], 0.002, 0.8)


if __name__ == "__main__":
    import os
    import soundfile as sf
    OUT = "./v12b"
    os.makedirs(OUT, exist_ok=True)
    r = lambda s: float(np.sqrt(np.mean(s ** 2)))

    loop = running_loop()
    scale = 0.6 / np.max(np.abs(loop))
    target = r(loop)
    up = start_success(); up *= target / r(up[-int(0.6 * SR):])
    down = shutdown(); down *= target / r(down[: int(0.3 * SR)])
    f1, f2 = start_fail_air(), start_fail_stall()
    crank = r(up[int(0.6 * SR):int(1.4 * SR)])
    f1 *= crank / r(f1[int(0.6 * SR):int(1.4 * SR)])
    f2 *= crank / r(f2[int(0.6 * SR):int(1.4 * SR)])

    def write(name, x):
        x = np.asarray(x) * scale
        pk = np.max(np.abs(x))
        if pk > 0.97:
            x = np.tanh(x / pk * 1.2) / np.tanh(1.2) * 0.95
        sf.write(f"{OUT}/{name}.wav", x.astype(np.float32), SR, subtype="PCM_16")
        w = SR // 2
        print(f"{name:22s} {len(x)/SR:5.2f}s peak {np.max(np.abs(x)):.2f} | " +
              " ".join(f"{r(x[i:i+w]):.2f}" for i in range(0, len(x) - w, w)))
        return x

    L = write("generator_loop", loop)
    U = write("generator_spinup", up)
    write("generator_spindown", down)
    write("generator_fail1", f1)
    write("generator_fail2", f2)

    # two-unit preview (A left, B right, B 0.4% fast until the sync breaker)
    total = int(22 * SR)
    a = np.zeros(total); b = np.zeros(total)
    a[:len(U)] += U
    a[len(U):] = np.tile(L, (total - len(U)) // len(L) + 1)[: total - len(U)]
    off = int(4.5 * SR)
    U2 = U[: total - off]
    b[off:off + len(U2)] += U2
    start_b = off + len(U2)
    sync = int(16.5 * SR)
    if start_b < total:
        Lb = signal.resample(L, int(len(L) / 1.004))
        free = np.tile(Lb, (total - start_b) // len(Lb) + 1)[: total - start_b]
        locked = np.tile(L, (total - start_b) // len(L) + 1)[: total - start_b]
        w = np.clip((np.arange(total - start_b) - (sync - start_b)) / int(0.4 * SR), 0, 1)
        b[start_b:] = free * (1 - w) + locked * w
    clunk = A.breaker_clunk(1.1, 0.15) * 0.9 / scale
    b[sync:sync + len(clunk)] += clunk[: total - sync]
    st = np.stack([a * 0.85 + b * 0.35, b * 0.85 + a * 0.35], axis=1)
    sf.write(f"{OUT}/preview_two_generators.wav", (st / np.max(np.abs(st)) * 0.9).astype(np.float32), SR, subtype="PCM_16")
    print("preview_two_generators  22.00s stereo")
