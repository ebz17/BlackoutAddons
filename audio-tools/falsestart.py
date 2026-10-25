"""False start of the generators (burnt-out control): previews.
Machine side (heard near the generators) and facility side (heard everywhere)."""
import numpy as np, soundfile as sf
import make_audio as A
import engine4 as E
from engine2 import second_order, add, bp
import faults as F
SR = A.SR
rng = np.random.default_rng(31)
OUT = "./out/falsestart"
ON = 11.0                      # lights up this long
MISFIRES = [2.6, 5.0, 7.3, 9.4]  # seconds after the engine catches

def relay_chatter(n, t0, dur, rate=14.0, amp=0.25):
    """contactor coils chattering as the voltage swings"""
    x = np.zeros(n)
    tt = t0
    while tt < t0 + dur:
        c = A.breaker_clunk(0.25 + 0.15 * rng.random(), 0.3 + 0.4 * rng.random()) * amp * rng.uniform(0.4, 1.0)
        add(x, c, int(tt * SR))
        tt += rng.exponential(1.0 / rate)
    return x

def engine_part(ending, ON=ON, MISFIRES=MISFIRES):
    dur = 2.0 + ON + 6.0
    n = int(dur * SR); t = np.arange(n) / SR
    t_air, t_catch = 0.3, 1.5
    t_end = t_catch + ON
    crank = 3.6 * np.clip((t - t_air) / 0.22, 0, 1) ** 0.6
    rise = second_order(t, 3.6, E.RUN_HZ, t_catch, zeta=0.55, wn=1.6)
    # hunting governor: rpm swings, uneven, getting worse
    hunt = (1.6 + 1.4 * np.clip((t - t_catch) / ON, 0, 1)) * np.sin(2 * np.pi * 0.55 * t + 0.6 * np.sin(2 * np.pi * 0.13 * t))
    hunt *= np.clip((t - t_catch - 1.5) / 1.0, 0, 1)
    rpm = np.where(t < t_catch, crank, rise + hunt)
    fire = np.interp(t, [0, 1.0, t_catch, t_catch + 1.0, t_end - 2.5, t_end], [0, 0.2, 0.75, 0.93, 0.9, 0.7])
    # bursts of misfire (fixed times: the lights flicker on exactly these in the game)
    for m in [t_catch + o for o in MISFIRES]:
        fire = np.where(np.abs(t - m) < 0.25, fire * 0.45, fire)
    whumps = [t_catch - 0.05]
    alt = np.clip((t - t_catch - 0.6) / 0.4, 0, 1)
    if ending == "none":
        pass                         # keeps running: the game cuts over to an ending clip
    elif ending == "stall":
        # coughs, backfire, dies
        die = t_end
        rpm = np.where(t > die - 1.6, rpm * np.interp(t, [die - 1.6, die - 0.6, die], [1.0, 0.8, 0.65]), rpm)
        rpm = np.where(t > die, rpm[int(die * SR)] * np.exp(-(t - die) / 1.1), rpm)
        fire = np.where(t > die - 1.6, fire * np.interp(t, [die - 1.6, die, die + 0.4], [0.7, 0.25, 0.0]), fire)
        whumps += [die - 1.2, die - 0.5]
        alt = alt * np.clip(1 - (t - die) / 0.6, 0, 1)
    else:
        # overload: engine bogs under the load, breaker trips, engine flares up then shuts down
        trip = t_end
        bog = np.interp(t, [trip - 2.0, trip - 0.2, trip], [0, -4.5, -5.5])
        flare = np.where(t > trip, 6.0 * np.exp(-(t - trip) / 0.7) * np.clip((t - trip) / 0.15, 0, 1), 0)
        rpm = np.where(t > trip - 2.0, rpm + bog * (t < trip + 0.01) + flare, rpm)
        shut = trip + 1.6
        rpm = np.where(t > shut, rpm[int(shut * SR)] * np.exp(-(t - shut) / 1.6), rpm)
        fire = np.where(t > shut, fire * np.clip(1 - (t - shut) / 0.4, 0, 1), fire)
        alt = alt * np.where(t > trip, 0.15, 1.0)
    rpm = A.smooth(np.clip(rpm, 0, None), 700)
    x = E.render(rpm, fire, np.interp(t, [0, t_catch, dur], [0.7, 1.05, 0.9]), alt_env=alt,
                 air=(E.air_env(t, t_air, t_catch + 0.3), t_catch + 0.3), whumps=tuple(whumps))
    add(x, E.valve_pop() * 0.8, int(t_air * SR))
    if ending == "none":
        pass
    elif ending == "stall":
        add(x, E.valve_pop() * 1.1, int((t_end - 0.9) * SR))          # backfire
        add(x, A.breaker_clunk(1.0, 0.0) * 0.6, int((t_end + 0.3) * SR))
    else:
        add(x, F.breaker_trip() * 0.9, int(t_end * SR))
    x += relay_chatter(n, t_catch + 2.0, ON - 2.5, rate=6.0, amp=0.18)
    return A.trim_tail(A.fade(E.room(x)[:n], 0.002, 0.6), 2e-3), t_catch, t_end

def facility_part(t_catch, t_end, ending, n):
    """what the rest of Labs hears: transformer hum swells with the rpm, ballasts buzz, contactors, then collapse"""
    t = np.arange(n) / SR
    on = np.clip((t - t_catch - 0.3) / 0.5, 0, 1) * np.clip(1 - (t - t_end) / (0.35 if ending == "trip" else 0.9), 0, 1)
    wob = 1 + 0.03 * np.sin(2 * np.pi * 0.55 * t)                         # follows the hunting
    f = 50.0 * wob * np.where(t > t_end, np.clip(1 - (t - t_end) * 0.6, 0.4, 1), 1)   # hum sags as it dies
    ph = 2 * np.pi * np.cumsum(f) / SR
    hum = (np.sin(2 * ph) * 0.5 + np.sin(4 * ph) * 0.25 + np.sin(6 * ph) * 0.12 + np.sign(np.sin(2 * ph)) * 0.05) * on
    buzz = bp(rng.standard_normal(n), 2000, 6000) * (0.5 + 0.5 * np.sign(np.sin(2 * ph))) * 0.05 * on
    x = hum * 0.5 + buzz
    # power arriving: big contactor + ballast "tink" chorus; leaving: drop-out clunk + ticking tubes
    add(x, A.breaker_clunk(1.0, 0.4) * 0.9, int((t_catch + 0.25) * SR))
    for k in range(10):
        add(x, A.breaker_clunk(0.15, 0.9) * 0.15, int((t_catch + 0.3 + rng.uniform(0, 1.2)) * SR))
    add(x, A.breaker_clunk(1.1, 0.4) * 0.9, int((t_end + (0.0 if ending == "trip" else 0.4)) * SR))
    for k in range(14):
        add(x, A.breaker_clunk(0.12, 0.9) * 0.12, int((t_end + 0.3 + rng.uniform(0, 1.0)) * SR))
    return A.reverb(x, rt60=2.2, wet=0.35, bright=4000)[:n]

BODY_ON = 31.0
END_PRE = 2.4          # ending clips start this long before the lights die

def export_variable():
    """1.18.3: lights stay 15-30 s. One long running 'body' + two ending clips the game cuts over to."""
    import faults as F2
    r = np.random.default_rng(5)
    mis, tt = [], 1.1
    while tt < BODY_ON - 0.5:
        mis.append(round(tt, 2)); tt += r.uniform(1.8, 3.4)
    body, tc, te = engine_part("none", ON=BODY_ON + 3.0, MISFIRES=mis)
    n = int((tc + BODY_ON + 1.0) * SR)
    body = A.fade(body[:n], 0.002, 0.8)
    F2.write("fs_body", body, 0.85)
    for ending in ("stall", "trip"):
        x, tc2, te2 = engine_part(ending, ON=6.0, MISFIRES=[])
        clip = x[int((te2 - END_PRE) * SR):]
        clip = A.fade(clip, 0.25, 0.6)
        F2.write(f"fs_end_{ending}", clip, 0.85)
    print("catch", tc, "misfires", mis)

def export_game():
    import faults as F2
    for ending in ("stall", "trip"):
        eng, tc, te = engine_part(ending)
        fac = facility_part(tc, te, ending, len(eng))
        F2.write(f"fs_engine_{ending}", eng, 0.85)        # 3D, at the generators
        F2.write(f"fs_facility_{ending}", fac, 0.7)                     # 2D, everywhere
    print("catch", 1.5, "end", 1.5 + ON, "misfires", MISFIRES)

if __name__ == "__main__" and "variable" in __import__("sys").argv:
    export_variable()
elif __name__ == "__main__" and "game" in __import__("sys").argv:
    export_game()
elif __name__ == "__main__":
    for ending in ("stall", "trip"):
        eng, tc, te = engine_part(ending)
        fac = facility_part(tc, te, ending, len(eng))
        name = "A_engine_stalls" if ending == "stall" else "B_overload_trip"
        sf.write(f"{OUT}/{name}_near_generators.wav", (A.norm(eng * 1.0 + fac * 0.35, 0.85)).astype(np.float32), SR, subtype="PCM_16")
        sf.write(f"{OUT}/{name}_rest_of_labs.wav", (A.norm(fac + A.butter(eng, hi=300, order=2) * 0.25, 0.7)).astype(np.float32), SR, subtype="PCM_16")
        print(name, round(len(eng) / SR, 1), "s")
