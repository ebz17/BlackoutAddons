# 1.20.0 lines: emergency grid + generator control repair. Same voice/PA chain as voices_v3 (style A).
import sys, importlib.util
import faults as F
from voices_v2 import EN, RU
sys.argv = ['x', 'none']
spec = importlib.util.spec_from_file_location("m", "./match_pa.py"); M = importlib.util.module_from_spec(spec); spec.loader.exec_module(M)
spec3 = importlib.util.spec_from_file_location("v3", "./voices_v3.py")
LINES = {
    "vo_emergency_on": ("Attention, personnel! Emergency power activated. Hangar and parking gates are open. Battery reserve is limited.",
                        "Внимание, персонал! Включено аварийное питание. Ворота ангара и парковки открыты. Резерв батарей ограничен."),
    "vo_emergency_low": ("Warning! Emergency battery reserve: one minute remaining.",
                         "Внимание! Резерв аварийных батарей: осталась одна минута."),
    "vo_emergency_out": ("Attention, personnel! Emergency power depleted. All gates are closed.",
                         "Внимание, персонал! Аварийное питание исчерпано. Все ворота закрыты."),
    "vo_control_repaired": ("Attention, personnel! Generator control repaired. Main generator ready to start.",
                            "Внимание, персонал! Управление генератором восстановлено. Главный генератор готов к запуску."),
}
import parselmouth
from parselmouth.praat import call
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
