# Final PA voice: style "A" (slightly younger: +6% formants, +10% pitch, livelier, a bit quicker)
# through the Labs PA sound (EQ matched to the facility speakers).
import sys, numpy as np, soundfile as sf, parselmouth
from parselmouth.praat import call
import importlib.util
import faults as F, make_audio as A
from voices_v2 import LINES, EN, RU
sys.argv = ['x', 'none']
spec = importlib.util.spec_from_file_location("m", "./match_pa.py"); M = importlib.util.module_from_spec(spec); spec.loader.exec_module(M)

def younger(path_in, path_out):
    snd = parselmouth.Sound(path_in); snd.scale_peak(0.7)
    f0 = call(snd.to_pitch(), "Get quantile", 0, 0, 0.5, "Hertz")
    out = call(snd, "Change gender", 75, 600, 1.06, f0 * 1.10, 1.15, 1.0)
    out.save(path_out, "WAV")

for name, (en, ru) in LINES.items():
    for lang, text, model, scale in (("en", en, EN, 1.02), ("ru", ru, RU, 0.98)):
        raw = f"./raw/{name}_{lang}.wav"
        F.tts(text, model, raw, scale)
        young = f"./raw/{name}_{lang}_young.wav"
        younger(raw, young)
        F.write(f"{name}_{lang}", M.labs_pa(young), 0.8)
