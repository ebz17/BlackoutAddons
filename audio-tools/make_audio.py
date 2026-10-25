"""Builds all sound files for Blackout Power Restore.

Voice lines (Piper TTS) -> facility PA style (band-limited, light drive, multi-speaker echo, hall reverb),
plus procedurally generated generator spin-up / running loop / spin-down, breaker clunks and a PA chime.
Output: 44.1 kHz mono 16-bit WAV.
"""
import numpy as np
import soundfile as sf
from scipy import signal

SR = 44100
OUT = "./out"
rng = np.random.default_rng(1234)


# ------------------------------------------------------------------ helpers
def butter(x, lo=None, hi=None, order=4):
    if lo and hi:
        sos = signal.butter(order, [lo, hi], "bandpass", fs=SR, output="sos")
    elif lo:
        sos = signal.butter(order, lo, "highpass", fs=SR, output="sos")
    else:
        sos = signal.butter(order, hi, "lowpass", fs=SR, output="sos")
    return signal.sosfilt(sos, x)


def reverb_ir(rt60, length=None, bright=6000, predelay=0.012):
    length = length or rt60 * 1.2
    n = int(length * SR)
    t = np.arange(n) / SR
    ir = rng.standard_normal(n) * np.exp(-6.91 * t / rt60)
    ir = butter(ir, hi=bright, order=2)
    ir[: int(predelay * SR)] = 0
    return ir / np.sqrt(np.sum(ir ** 2))


def reverb(x, rt60=1.6, wet=0.3, bright=6000):
    ir = reverb_ir(rt60, bright=bright)
    w = signal.fftconvolve(x, ir)[: len(x) + len(ir)]
    dry = np.concatenate([x, np.zeros(len(w) - len(x))])
    return dry * (1 - wet) + w * wet


def fade(x, fin=0.005, fout=0.05):
    a, b = int(fin * SR), int(fout * SR)
    if a:
        x[:a] *= np.linspace(0, 1, a)
    if b:
        x[-b:] *= np.linspace(1, 0, b)
    return x


def norm(x, peak=0.85):
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 0 else x


def trim_tail(x, thresh=1e-3):
    idx = np.where(np.abs(x) > thresh)[0]
    return x[: idx[-1] + int(0.05 * SR)] if len(idx) else x


def save(name, x, peak=0.85):
    x = norm(np.asarray(x, dtype=np.float64), peak)
    sf.write(f"{OUT}/{name}.wav", x.astype(np.float32), SR, subtype="PCM_16")
    print(f"{name:22s} {len(x)/SR:5.2f}s  peak {np.max(np.abs(x)):.2f}  rms {np.sqrt(np.mean(x**2)):.3f}")


def phase_from_freq(f):
    return 2 * np.pi * np.cumsum(f) / SR


# ------------------------------------------------------------------ PA voice
def pa_voice(path):
    v, sr = sf.read(path)
    v = signal.resample_poly(v, SR, sr)
    v = butter(v, lo=320, hi=3600, order=4)              # PA horn band
    v = np.tanh(norm(v, 1.0) * 2.2) / np.tanh(2.2)       # light speaker drive
    # several ceiling speakers at different distances
    out = np.zeros(len(v) + int(0.5 * SR))
    for delay, gain in [(0.0, 1.0), (0.085, 0.38), (0.19, 0.22), (0.31, 0.12)]:
        d = int(delay * SR)
        out[d : d + len(v)] += v * gain
    out = reverb(out, rt60=1.9, wet=0.32, bright=4500)  # big concrete facility
    return trim_tail(out)


def chime():
    def bell(f, dur):
        t = np.arange(int(dur * SR)) / SR
        env = np.exp(-t * 3.2) * (1 - np.exp(-t * 400))
        tone = (np.sin(2 * np.pi * f * t) + 0.35 * np.sin(2 * np.pi * 2 * f * t)
                + 0.12 * np.sin(2 * np.pi * 3.01 * f * t))
        return tone * env
    a = bell(659.25, 0.9)
    b = bell(523.25, 1.3)
    x = np.zeros(int(0.45 * SR) + len(b))
    x[: len(a)] += a
    x[int(0.45 * SR) :] += b
    x = butter(x, lo=300, hi=4000, order=2)
    return reverb(x, rt60=1.9, wet=0.3, bright=4500)


def with_chime(voice, gap=0.15):
    c = norm(chime(), 0.55)
    v = norm(voice, 0.85)
    start = int((len(c) / SR - 0.9 + gap) * SR)  # voice starts while the chime tail rings
    out = np.zeros(max(len(c), start + len(v)))
    out[: len(c)] += c
    out[start : start + len(v)] += v
    return out


# ------------------------------------------------------------------ generator
ENGINE_HZ = 25.0   # 1500 rpm diesel
GRID_HZ = 50.0


def engine(f, amp, t):
    """Diesel engine: combustion harmonics of the crank frequency + mechanical noise."""
    ph = phase_from_freq(f)
    x = np.zeros_like(f)
    for k, a in [(1, 1.0), (2, 0.8), (3, 0.55), (4, 0.6), (6, 0.35), (8, 0.25), (12, 0.12), (16, 0.06)]:
        x += a * np.sin(k * ph + rng.uniform(0, 2 * np.pi))
    # firing pulses -> slightly "chuggy" timbre
    x *= 1 + 0.35 * np.sin(2 * ph)
    noise = butter(rng.standard_normal(len(f)), lo=60, hi=1400, order=2)
    noise *= 0.5 * (f / ENGINE_HZ) ** 1.2
    return amp * (x * 0.6 + noise * 0.9)


def alternator(f_engine, amp, t):
    """Mains hum + transformer buzz locked to the engine speed."""
    f = f_engine * (GRID_HZ / ENGINE_HZ)
    ph = phase_from_freq(f)
    x = (np.sin(ph) + 0.6 * np.sin(2 * ph) + 0.35 * np.sin(3 * ph) + 0.25 * np.sin(5 * ph)
         + 0.12 * np.sign(np.sin(2 * ph)) * 0.5)
    return amp * x


def fan_whine(f_engine, amp):
    f = f_engine * 72.0   # cooling fan / turbo, ~1800 Hz at speed
    return amp * np.sin(phase_from_freq(f)) * 0.5


def smooth(x, n):
    return np.convolve(x, np.ones(n) / n, mode="same")


def generator_spinup(dur=7.5):
    n = int(dur * SR)
    t = np.arange(n) / SR
    # Starter cranks for ~1.6 s (5-6 Hz chug), engine catches and accelerates to 25 Hz.
    crank_end = 1.6
    rpm = np.where(t < crank_end, 4.0 + 1.5 * t / crank_end,
                   5.5 + (ENGINE_HZ - 5.5) * (1 - np.exp(-(t - crank_end) / 1.3)))
    rpm = smooth(rpm, 2000)
    amp = np.clip(0.35 + 0.65 * (rpm - 4) / (ENGINE_HZ - 4), 0, 1)

    x = engine(rpm, amp, t)

    # starter motor whine + gear grind while cranking
    starter_f = 380 + 260 * np.clip(t / crank_end, 0, 1)
    st_env = np.clip(1 - (t - crank_end) / 0.25, 0, 1) * np.clip(t / 0.08, 0, 1)
    starter = (signal.sawtooth(phase_from_freq(starter_f)) * 0.25
               + butter(rng.standard_normal(n), lo=900, hi=3500, order=2) * 0.15) * st_env
    starter = butter(starter, hi=2600, order=2)
    x += starter

    # "catch" bang when the engine fires
    bang_t = int(crank_end * SR)
    bang = np.exp(-np.arange(int(0.4 * SR)) / SR * 12) * butter(rng.standard_normal(int(0.4 * SR)), hi=500)
    x[bang_t : bang_t + len(bang)] += bang * 1.4

    # alternator excitation comes in once at speed
    gen_env = np.clip((t - 3.6) / 2.0, 0, 1) ** 1.5
    x += alternator(rpm, 0.45 * gen_env, t)
    x += fan_whine(rpm, 0.10 * np.clip((t - 2.0) / 3.0, 0, 1))

    x = butter(x, lo=28, order=2)
    x = reverb(x, rt60=1.2, wet=0.22, bright=5000)
    return fade(x[:n], 0.01, 0.02)


def generator_loop(dur=8.0, xfade=1.0):
    total = dur + xfade
    n = int(total * SR)
    t = np.arange(n) / SR
    rpm = ENGINE_HZ + 0.25 * np.sin(2 * np.pi * 0.5 * t)    # tiny governor hunt
    x = engine(rpm, np.ones(n), t)
    x += alternator(rpm, 0.45 * np.ones(n), t)
    x += fan_whine(rpm, 0.10 * np.ones(n))
    x = butter(x, lo=28, order=2)
    # seamless loop: crossfade the extra second back over the start
    m = int(dur * SR)
    k = int(xfade * SR)
    w = np.linspace(0, 1, k)
    loop = x[:m].copy()
    loop[:k] = x[:k] * w + x[m : m + k] * (1 - w)
    return loop


def generator_spindown(dur=6.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    rpm = ENGINE_HZ * np.exp(-t / 1.6)
    amp = np.clip(rpm / ENGINE_HZ, 0, 1) ** 0.8
    x = engine(rpm, amp, t)
    x += alternator(rpm, 0.45 * np.clip(1 - t / 0.6, 0, 1), t)
    x += fan_whine(rpm, 0.10 * amp)
    x = butter(x, lo=28, order=2)
    x = reverb(x, rt60=1.2, wet=0.22, bright=5000)
    return fade(x[:n], 0.01, 0.4)


# ------------------------------------------------------------------ breakers
def breaker_clunk(heavy=1.0, distance=0.0):
    n = int(0.9 * SR)
    t = np.arange(n) / SR
    click = butter(rng.standard_normal(n), lo=1500, order=2) * np.exp(-t * 900) * 0.9
    thump = np.sin(2 * np.pi * 72 * t) * np.exp(-t * 22) * 1.2 * heavy
    ring = (np.sin(2 * np.pi * 1180 * t) * 0.25 + np.sin(2 * np.pi * 2310 * t) * 0.15) * np.exp(-t * 16)
    zap_env = np.clip((t - 0.012) / 0.003, 0, 1) * np.exp(-np.clip(t - 0.012, 0, None) * 35)
    zap = signal.square(2 * np.pi * 100 * t) * 0.18 * zap_env
    x = click + thump + ring + zap
    if distance > 0:   # further away: duller and wetter
        x = butter(x, hi=max(600, 4000 - 3000 * distance), order=2)
        x = reverb(x, rt60=1.8, wet=0.35 + 0.35 * distance, bright=3000)
    else:
        x = reverb(x, rt60=1.0, wet=0.2, bright=6000)
    return trim_tail(fade(x, 0.0005, 0.05))


def tube_flicker_buzz(dur=1.2):
    """Fluorescent ballast start: ticks + 100 Hz buzz that settles."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    buzz = (signal.sawtooth(2 * np.pi * 100 * t) * 0.3 + np.sin(2 * np.pi * 200 * t) * 0.2)
    gate = np.zeros(n)
    pos = 0.0
    while pos < dur * 0.7:
        on = rng.uniform(0.03, 0.09)
        gate[int(pos * SR) : int((pos + on) * SR)] = 1
        pos += on + rng.uniform(0.04, 0.12)
    gate[int(dur * 0.7 * SR) :] = np.linspace(1, 0.25, n - int(dur * 0.7 * SR))
    gate = smooth(gate, 80)
    ticks = np.abs(np.diff(gate, prepend=0)) * 8 * butter(rng.standard_normal(n), lo=2000, order=2)
    x = buzz * gate * 0.5 + ticks
    x = butter(x, lo=90, hi=6000, order=2)
    return reverb(x, rt60=1.0, wet=0.25)


# ------------------------------------------------------------------ build
if __name__ == "__main__":
    import os
    os.makedirs(OUT, exist_ok=True)

    save("vo_start", with_chime(pa_voice("./raw/vo_start.wav")), 0.8)
    save("vo_restored", with_chime(pa_voice("./raw/vo_restored.wav")), 0.8)

    save("generator_spinup", generator_spinup(), 0.9)
    save("generator_loop", generator_loop(), 0.6)        # quieter steady state, loops seamlessly
    save("generator_spindown", generator_spindown(), 0.9)

    save("breaker_near", breaker_clunk(1.0, 0.0), 0.9)
    for i, d in enumerate([0.25, 0.5, 0.8], start=1):
        save(f"breaker_far{i}", breaker_clunk(1.2, d), 0.9 - 0.15 * i)

    save("tube_flicker", tube_flicker_buzz(), 0.5)
