import numpy as np
import make_audio as A
import faults as F
SR=A.SR
def tone(f, d, kind="sq"):
    t=np.arange(int(d*SR))/SR
    x=np.sign(np.sin(2*np.pi*f*t))*0.45+np.sin(2*np.pi*f*t)*0.55 if kind=="sq" else np.sin(2*np.pi*f*t)+0.3*np.sin(4*np.pi*f*t)
    return x*np.clip(t/0.004,0,1)*np.clip((d-t)/0.02,0,1)
def seq(parts):
    out=np.zeros(int(sum(p[1] for p in parts)*SR)+SR)
    k=0
    for f,d,gap,kind in parts:
        if f>0:
            x=tone(f,d,kind); out[k:k+len(x)]+=x
        k+=int((d+gap)*SR)
    return out
# start blocked: two low, harsh buzzes from the control panel
denied=seq([(220,0.28,0.10,"sq"),(220,0.42,0.0,"sq")])
denied=A.butter(denied,lo=150,hi=3500,order=2)
denied=A.reverb(denied,rt60=0.9,wet=0.2,bright=4000)
# breakers reset / ready: relay clack + two rising clean beeps
ready=seq([(0,0.12,0.0,"sn"),(660,0.14,0.06,"sn"),(990,0.22,0.0,"sn")])
cl=A.breaker_clunk(0.5,0.0); m=min(len(cl),len(ready)); ready[:m]+=cl[:m]*0.6
ready=A.reverb(A.butter(ready,lo=200,hi=6000,order=2),rt60=0.9,wet=0.2,bright=5000)
F.write("cue_denied", A.trim_tail(A.fade(denied,0.002,0.1)), 0.7)
F.write("cue_ready", A.trim_tail(A.fade(ready,0.002,0.1)), 0.7)
