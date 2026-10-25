"""Killa breaking in through the sewage conduit hatch: two kicks, the hatch gives way, the grate
crashes down and bounces, all echoing down the concrete tunnel."""
import numpy as np, soundfile as sf
from scipy import signal
import make_audio as A
SR = A.SR
rng = np.random.default_rng(7)
OUT = "./out/killa"

def add(x, y, at):
    at = int(at)
    if at >= len(x): return
    m = min(len(y), len(x) - at)
    x[at:at + m] += y[:m]

def modal(dur, modes, decays, amps, detune=0.0):
    """steel plate: inharmonic partials, each with its own decay"""
    n = int(dur * SR); t = np.arange(n) / SR
    y = np.zeros(n)
    for f, d, a in zip(modes, decays, amps):
        f2 = f * (1 + detune * rng.uniform(-1, 1))
        beat = 1 + 0.08 * np.sin(2 * np.pi * rng.uniform(0.7, 3.0) * t)       # plate wobble
        y += a * np.sin(2 * np.pi * f2 * t + rng.uniform(0, 6.28)) * np.exp(-t / d) * beat
    return y

PLATE = [143, 231, 337, 468, 612, 789, 1004, 1262, 1580, 1957, 2440, 3110]

def impact(power=1.0, ring=1.0):
    dur = 3.0
    n = int(dur * SR); t = np.arange(n) / SR
    # body thud (boot on steel, the hatch frame flexing)
    thud = np.sin(2 * np.pi * (48 + 40 * np.exp(-t / 0.03)) * t) * np.exp(-t / 0.16) * 1.4
    thud += np.sin(2 * np.pi * 92 * t) * np.exp(-t / 0.09) * 0.6
    # transient crack
    crack = rng.normal(0, 1, n) * np.exp(-t / 0.006)
    crack = A.butter(crack, lo=900) * 1.6 + A.butter(rng.normal(0, 1, n) * np.exp(-t / 0.025), hi=1800) * 0.7
    # steel ringing
    decays = [1.4, 1.1, 0.9, 0.8, 0.65, 0.55, 0.45, 0.38, 0.3, 0.24, 0.18, 0.12]
    amps = [0.5, 0.6, 0.55, 0.5, 0.5, 0.5, 0.48, 0.45, 0.4, 0.34, 0.28, 0.22]
    rings = modal(dur, PLATE, [d * ring for d in decays], amps, detune=0.015)
    y = thud * power + crack * power + rings * (0.5 + 0.6 * power)
    return y

def snap():
    """hinge / bolts shearing"""
    n = int(0.4 * SR); t = np.arange(n) / SR
    y = A.butter(rng.normal(0, 1, n), lo=2500) * np.exp(-t / 0.004) * 1.2
    y += modal(0.4, [2900, 3700, 4630], [0.05, 0.04, 0.03], [0.4, 0.3, 0.2])
    return y

def clatter(start_gap=0.32, bounces=7):
    """the grate falling on concrete and bouncing / rattling to a stop"""
    n = int(2.5 * SR)
    y = np.zeros(n)
    tt, amp, gap = 0.0, 1.0, start_gap
    for i in range(bounces):
        hit = impact(power=0.55 * amp, ring=0.35 + 0.3 * amp)
        hit = A.butter(hit, lo=110)
        add(y, hit * amp, tt * SR)
        # small rattles between bounces
        for _ in range(int(3 * amp) + 1):
            r = modal(0.15, rng.choice(PLATE[4:], 3), [0.04, 0.03, 0.03], [0.3, 0.2, 0.15], 0.03)
            add(y, r * amp * 0.5, (tt + rng.uniform(0.02, gap * 0.8)) * SR)
        tt += gap
        gap *= 0.68
        amp *= 0.62
    # scrape as it slides to a stop
    m = int(0.7 * SR); t = np.arange(m) / SR
    scrape = A.butter(rng.normal(0, 1, m), lo=1200, hi=5000) * (1 + 0.6 * np.sin(2 * np.pi * 23 * t)) * np.exp(-t / 0.25) * 0.18
    add(y, scrape, (tt - 0.1) * SR)
    return y

def tunnel(x, wet=0.4):
    """long concrete pipe: slap echoes + long dark reverb"""
    y = x.copy()
    for d, g in [(0.11, 0.45), (0.23, 0.32), (0.37, 0.22), (0.58, 0.14), (0.86, 0.09)]:
        e = A.butter(x, hi=5200 - d * 3000) * g
        add(y, e, d * SR)
    rev = A.reverb(y, rt60=3.4, wet=1.0, bright=4500)
    return y * (1 - wet * 0.4) + rev[:len(y)] * wet

def sub_boom(dur=2.5):
    n = int(dur * SR); t = np.arange(n) / SR
    return np.sin(2 * np.pi * (34 + 22 * np.exp(-t / 0.08)) * t) * np.exp(-t / 0.45)

def build():
    n = int(9.0 * SR)
    x = np.zeros(n)
    add(x, impact(0.75, 0.55), 0.15 * SR)                  # kick 1
    add(x, impact(0.95, 0.6), 1.05 * SR)                  # kick 2, harder
    t3 = 1.85
    add(x, impact(1.6, 1.3) * 1.2, t3 * SR)               # the hatch gives way
    add(x, snap(), (t3 + 0.012) * SR)
    add(x, snap() * 0.7, (t3 + 0.05) * SR)
    add(x, sub_boom() * 1.3, t3 * SR)
    add(x, clatter(), (t3 + 0.55) * SR)                  # grate hits the floor and bounces
    y = tunnel(x)
    y = np.tanh(y / np.max(np.abs(y)) * 1.4)              # push it: dense and loud
    y = A.fade(A.trim_tail(y, 2e-3), 0.002, 0.6)
    return y / np.max(np.abs(y)) * 0.97

if __name__ == "__main__":
    import os, sys
    os.makedirs(OUT, exist_ok=True)
    y = build()
    sf.write(os.path.join(OUT, "killa_hatch.wav"), y, SR, subtype="PCM_16")
    if len(sys.argv) > 1 and sys.argv[1] == "game":
        sf.write("final/killa_hatch.wav", y, SR, subtype="PCM_16")
    print("len", len(y) / SR)
