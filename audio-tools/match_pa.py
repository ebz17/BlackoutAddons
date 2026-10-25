# Match our PA lines to the *sound of the Labs PA system* (speaker EQ, darkness, loudness, pace) - not to the
# announcer's voice itself: the voice stays our own TTS voice.
import numpy as np, soundfile as sf, glob
from scipy import signal
import make_audio as A, faults as F
SR = A.SR
SCR = "./out"

def ltas(x):
    f, P = signal.welch(x, SR, nperseg=8192)
    return f, P

def band_db(f, P, centers):
    out = []
    for c in centers:
        b = (f >= c / 2 ** (1 / 6)) & (f < c * 2 ** (1 / 6))
        out.append(10 * np.log10(P[b].mean() + 1e-20))
    return np.array(out)

CENTERS = 100 * 2 ** (np.arange(0, 21) / 3)           # 100 Hz .. ~10 kHz, 1/3 octave

# target: the five recorded Labs announcements (voice part only)
H = []
for i in range(1, 6):
    x, sr = sf.read(f"{SCR}/ann{i}_44.wav")
    H.append(x[int(2.6 * sr):int((len(x) / sr - 0.8) * sr)])
fh, Ph = ltas(np.concatenate(H))
target = band_db(fh, Ph, CENTERS)

def load_raw(path):
    v, sr = sf.read(path)
    return signal.resample_poly(v, SR, sr)

raws = [load_raw(p) for p in sorted(glob.glob("./raw/vo_*_ru.wav"))]
fo, Po = ltas(np.concatenate(raws))
ours = band_db(fo, Po, CENTERS)
# shape difference only (overall level is set later)
diff = (target - target.mean()) - (ours - ours.mean())
diff = np.clip(diff, -24, 12)
diff[CENTERS < 200] = np.minimum(diff[CENTERS < 200], -4)    # below 200 Hz her recording is mostly game ambience
diff = np.convolve(np.pad(diff, 1, mode="edge"), [0.25, 0.5, 0.25], mode="valid")   # smooth
freqs = np.concatenate([[0], CENTERS, [SR / 2]]) / (SR / 2)
gains = 10 ** (np.concatenate([[diff[0] - 6], diff, [diff[-1] - 6]]) / 20)
EQ = signal.firwin2(2049, freqs, gains)
print("EQ (dB) per band:", " ".join(f"{int(c)}:{d:+.0f}" for c, d in zip(CENTERS, diff)))

def labs_pa(path):
    v = load_raw(path)
    v = signal.fftconvolve(v, EQ, mode="same")
    env = np.sqrt(A.smooth(v ** 2, int(0.03 * SR))) + 1e-6
    thr = np.percentile(env[env > env.max() * 0.05], 50)
    v = v * A.smooth(np.where(env > thr, (thr / env) ** 0.5, 1.0), int(0.01 * SR))      # even, flat delivery
    v = np.tanh(A.norm(v, 1.0) * 1.3) / np.tanh(1.3)
    out = np.zeros(len(v) + int(0.6 * SR))
    for d, g in [(0.0, 1.0), (0.045, 0.3), (0.11, 0.2), (0.21, 0.12), (0.33, 0.07)]:    # several ceiling speakers
        k = int(d * SR); out[k:k + len(v)] += v * g
    out = A.reverb(out, rt60=1.6, wet=0.2, bright=2600)
    return A.trim_tail(A.fade(out, 0.004, 0.25))

if __name__ == "__main__":
    import sys
    names = sys.argv[1:] or [p.split("/")[-1][:-4] for p in sorted(glob.glob("./raw/vo_*.wav"))]
    for n in names:
        F.write(n, labs_pa(f"./raw/{n}.wav"), 0.8)
