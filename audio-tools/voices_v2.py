# All PA lines redone in the style of the Labs announcer: no chime, "Attention, personnel/security!",
# calm and flat, narrow ceiling-speaker sound with a bit of hall.
import numpy as np, soundfile as sf
from scipy import signal
import faults as F
import make_audio as A
SR = A.SR

LINES = {
    "vo_start": ("Attention, personnel! Main generator start sequence initiated. Stand by for power.",
                 "Внимание, персонал! Запуск главного генератора. Ожидайте восстановления питания."),
    "vo_restored": ("Attention, personnel! Main power restored. All facility systems are back in service.",
                    "Внимание, персонал! Основное питание восстановлено. Все системы комплекса введены в строй."),
    "vo_fail": ("Attention, personnel! Generator start failure. Please stand by.",
                "Внимание, персонал! Сбой запуска генератора. Просьба ожидать."),
    "vo_partial": ("Attention, personnel! Generator B synchronisation failure. Sector B is out of service. Reset the breakers in the switchgear room.",
                   "Внимание, персонал! Сбой синхронизации генератора Б. Сектор Б выведен из строя. Сбросьте автоматы в распределительной."),
    "vo_breakers_reset": ("Attention, personnel! Breakers reset. Generator B ready to start.",
                          "Внимание, персонал! Автоматы сброшены. Генератор Б готов к запуску."),
    "vo_fault_denied": ("Attention, personnel! Generator B start blocked. Distribution fault.",
                        "Внимание, персонал! Запуск генератора Б заблокирован. Неисправность распределительной сети."),
    "vo_reclose_fault": ("Attention, personnel! Fault in sector B. Automatic reclosing in progress.",
                         "Внимание, персонал! Авария в секторе Б. Выполняется автоматическое повторное включение."),
    "vo_distribution_ok": ("Attention, personnel! Power to sector B restored.",
                           "Внимание, персонал! Питание сектора Б восстановлено."),
    "vo_control_failure": ("Attention, personnel! Main generator control failure. Due to technical problems, the facility will remain without main power.",
                           "Внимание, персонал! Отказ управления главным генератором. По техническим причинам комплекс останется без основного питания."),
}

def ceiling_pa(path):
    v, sr = sf.read(path)
    v = signal.resample_poly(v, SR, sr)
    v = A.butter(v, lo=230, hi=2700, order=4)                     # small ceiling horns
    v = v + 0.25 * A.butter(v, lo=900, hi=2200, order=2)          # nasal speaker bump
    env = np.sqrt(A.smooth(v ** 2, int(0.03 * SR))) + 1e-5
    thr = np.percentile(env[env > env.max() * 0.05], 50)
    v = v * A.smooth(np.where(env > thr, (thr / env) ** 0.55, 1.0), int(0.01 * SR))   # flat, even level
    v = np.tanh(A.norm(v, 1.0) * 1.5) / np.tanh(1.5)
    out = np.zeros(len(v) + int(0.5 * SR))
    for d, g in [(0.0, 1.0), (0.06, 0.28), (0.13, 0.18), (0.24, 0.1)]:   # neighbouring speakers / corridor
        k = int(d * SR); out[k:k + len(v)] += v * g
    out = A.reverb(out, rt60=1.9, wet=0.22, bright=3200)
    return A.trim_tail(A.fade(out, 0.004, 0.25))

EN = "./voices/en_US-lessac-high.onnx"
RU = "./voices/ru_RU-irina-medium.onnx"
if __name__ == "__main__":
    for name, (en, ru) in LINES.items():
        for lang, text, model, scale in (("en", en, EN, 1.12), ("ru", ru, RU, 1.12)):
            raw = f"./raw/{name}_{lang}.wav"
            F.tts(text, model, raw, scale)
            F.write(f"{name}_{lang}", ceiling_pa(raw), 0.8)
