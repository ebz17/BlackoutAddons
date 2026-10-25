"""Switchgear fault sounds, breaker trip, intro sting and new PA lines."""
import os
import subprocess
import numpy as np
import soundfile as sf
from scipy import signal
import make_audio as A

SR = A.SR
OUT = "./final"
rng = np.random.default_rng(1612)
bp = lambda x, lo, hi, o=2: A.butter(x, lo=lo, hi=hi, order=o)


def write(name, x, peak):
    x = A.norm(np.asarray(x, dtype=np.float64), peak)
    sf.write(f"{OUT}/{name}.wav", x.astype(np.float32), SR, subtype="PCM_16")
    print(f"{name:22s} {len(x)/SR:5.2f}s peak {np.max(np.abs(x)):.2f} rms {np.sqrt(np.mean(x**2)):.3f}")


def fault_buzz(dur=4.0, xfade=0.5):
    """Faulty breaker: 100 Hz magnetic buzz + loose-contact chatter + hiss. Seamless loop."""
    n = int((dur + xfade) * SR)
    t = np.arange(n) / SR
    buzz = signal.sawtooth(2 * np.pi * 100 * t) * 0.35 + np.sin(2 * np.pi * 200 * t) * 0.25 + np.sin(2 * np.pi * 300 * t) * 0.12
    # loose contact: buzz level jitters, with little arc ticks
    chatter = np.ones(n)
    pos = 0.0
    while pos < dur + xfade:
        seg = rng.uniform(0.02, 0.25)
        chatter[int(pos * SR):int((pos + seg) * SR)] = rng.uniform(0.35, 1.0)
        pos += seg
    chatter = A.smooth(chatter, 120)
    ticks = np.zeros(n)
    for _ in range(int(14 * dur)):
        i = int(rng.uniform(0, n - 400))
        k = np.arange(300)
        ticks[i:i + 300] += bp(rng.standard_normal(300), 2500, 9000) * np.exp(-k / 40) * rng.uniform(0.3, 1.0)
    hiss = bp(rng.standard_normal(n), 3000, 9000) * 0.03
    x = buzz * chatter + ticks * 0.5 + hiss
    x = bp(x, 80, 9000)
    m, k = int(dur * SR), int(xfade * SR)
    w = np.linspace(0, 1, k)
    loop = x[:m].copy()
    loop[:k] = x[:k] * w + x[m:m + k] * (1 - w)
    return loop


def spark(seed):
    r = np.random.default_rng(seed)
    n = int(1.2 * SR)
    x = np.zeros(n)
    t0 = 0
    for _ in range(r.integers(3, 7)):              # a crackling burst of arcs
        length = int(r.uniform(0.01, 0.06) * SR)
        k = np.arange(length)
        arc = r.standard_normal(length) * np.exp(-k / (length / 3))
        arc += signal.square(2 * np.pi * 100 * k / SR) * 0.3 * np.exp(-k / (length / 2))
        x[t0:t0 + length] += arc * r.uniform(0.5, 1.0)
        t0 += length + int(r.uniform(0.01, 0.12) * SR)
        if t0 >= n - length:
            break
    x = bp(x, 400, 11000)
    return A.trim_tail(A.reverb(x, rt60=0.9, wet=0.2, bright=7000), 2e-3)


def breaker_trip():
    """Generator breaker tripping under fault: heavy bang + arc flash crackle + contactor rattle."""
    n = int(2.2 * SR)
    t = np.arange(n) / SR
    bang = A.butter(rng.standard_normal(n), hi=900) * np.exp(-t / 0.05) * 2.0
    bang += np.sin(2 * np.pi * 55 * t) * np.exp(-t / 0.12) * 1.8
    clunk = np.zeros(n)
    c = A.breaker_clunk(1.4, 0.0)
    clunk[:len(c)] += c[:n] * 1.2
    arc = np.zeros(n)
    s = spark(77)
    arc[int(0.02 * SR):int(0.02 * SR) + len(s)] += s[: n - int(0.02 * SR)] * 1.4
    hum_die = np.sin(2 * np.pi * 100 * t) * np.exp(-t / 0.35) * 0.5     # magnetic thump as it drops out
    x = bang + clunk + arc + hum_die
    return A.trim_tail(A.reverb(x, rt60=1.6, wet=0.3, bright=5000), 2e-3)


def intro_sting(dur=7.0):
    """Ominous low drone swell + two-tone facility warning beeps."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    env = np.clip(t / 2.5, 0, 1) ** 2 * np.clip((dur - t) / 2.0, 0, 1)
    drone = (np.sin(2 * np.pi * 41 * t) + 0.6 * np.sin(2 * np.pi * 61.5 * t + 0.5 * np.sin(2 * np.pi * 0.3 * t))
             + 0.3 * signal.sawtooth(2 * np.pi * 82 * t)) * 0.45
    drone = A.butter(drone, hi=600) * env
    rumble = A.butter(rng.standard_normal(n), hi=120) * 0.6 * env
    beeps = np.zeros(n)
    for start, f in ((1.6, 880), (2.0, 660), (3.6, 880), (4.0, 660)):
        k = int(0.32 * SR)
        tt = np.arange(k) / SR
        tone = (signal.square(2 * np.pi * f * tt) * 0.3 + np.sin(2 * np.pi * f * tt) * 0.4)
        tone *= np.clip(tt / 0.01, 0, 1) * np.clip((0.32 - tt) / 0.02, 0, 1)
        i = int(start * SR)
        beeps[i:i + k] += A.butter(tone, lo=300, hi=3800)
    hit = np.zeros(n)
    k = int(1.5 * SR)
    tt = np.arange(k) / SR
    hit[:k] = (np.sin(2 * np.pi * 36 * tt) * np.exp(-tt / 0.5) + A.butter(rng.standard_normal(k), hi=300) * np.exp(-tt / 0.2)) * 1.4
    x = drone + rumble + A.reverb(beeps, rt60=2.2, wet=0.35, bright=4000)[:n] * 0.9 + hit
    return A.reverb(x, rt60=2.6, wet=0.3, bright=3000)


LINES = {
    "vo_partial": (
        "Warning. Generator B synchronisation failure. Facility on partial power. Reset the tripped breakers in the switchgear room.",
        "Внимание. Сбой синхронизации генератора Б. Комплекс работает на частичном питании. Сбросьте сработавшие автоматы в распределительной.",
    ),
    "vo_breakers_reset": (
        "Breakers reset. Generator B ready for restart.",
        "Автоматы сброшены. Генератор Б готов к повторному запуску.",
    ),
    "vo_fault_denied": (
        "Distribution fault. Generator B start blocked.",
        "Неисправность распределительной сети. Запуск генератора Б заблокирован.",
    ),
}


def tts(text, model, path, scale):
    subprocess.run(["python3", "-m", "piper", "-m", model, "--length-scale", str(scale), "--sentence-silence", "0.35", "-f", path],
                   input=text.encode("utf-8"), check=True, capture_output=True)


def pa_voice_clear(path):
    v, sr = sf.read(path)
    v = signal.resample_poly(v, SR, sr)
    v = A.butter(v, lo=280, hi=4200, order=4)
    env = np.sqrt(A.smooth(v ** 2, int(0.03 * SR))) + 1e-5
    thr = np.percentile(env[env > env.max() * 0.05], 50)
    v = v * A.smooth(np.where(env > thr, (thr / env) ** 0.5, 1.0), int(0.01 * SR))
    v = np.tanh(A.norm(v, 1.0) * 1.8) / np.tanh(1.8)
    out = np.zeros(len(v) + int(0.3 * SR))
    for d, g in [(0.0, 1.0), (0.09, 0.22), (0.2, 0.1)]:
        k = int(d * SR)
        out[k:k + len(v)] += v * g
    return A.trim_tail(A.reverb(out, rt60=1.4, wet=0.16, bright=5000))


def error_tone():
    seg = []
    for f in (392, 392):
        t = np.arange(int(0.22 * SR)) / SR
        tone = signal.square(2 * np.pi * f * t, 0.5) * 0.35 + np.sin(2 * np.pi * f * t) * 0.3
        tone *= np.clip(t / 0.005, 0, 1) * np.clip((0.22 - t) / 0.01, 0, 1)
        seg += [tone, np.zeros(int(0.08 * SR))]
    return A.reverb(A.butter(np.concatenate(seg), lo=300, hi=3800, order=2), rt60=1.9, wet=0.3, bright=4500)


def with_tone(tone, voice, overlap=0.5):
    c, v = A.norm(tone, 0.55), A.norm(voice, 0.85)
    start = max(0, len(c) - int(overlap * SR))
    out = np.zeros(max(len(c), start + len(v)))
    out[:len(c)] += c
    out[start:start + len(v)] += v
    return out


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    os.makedirs("./raw", exist_ok=True)
    write("fault_buzz", fault_buzz(), 0.6)
    write("spark1", spark(11), 0.8)
    write("spark2", spark(23), 0.8)
    write("breaker_trip", breaker_trip(), 0.95)
    write("intro_sting", intro_sting(), 0.85)

    EN = "./voices/en_US-lessac-high.onnx"
    RU = "./voices/ru_RU-irina-medium.onnx"
    for name, (en, ru) in LINES.items():
        for lang, text, model, scale in (("en", en, EN, 1.12), ("ru", ru, RU, 1.08)):
            raw = f"./raw/{name}_{lang}.wav"
            tts(text, model, raw, scale)
            voice = pa_voice_clear(raw)
            # warnings get the error tone, the good news gets the chime
            x = A.with_chime(voice) if name == "vo_breakers_reset" else with_tone(error_tone(), voice)
            write(f"{name}_{lang}", x, 0.8)
