import numpy as np
import make_audio as A
SR=A.SR; rng=np.random.default_rng(7)
def ph(f): return 2*np.pi*np.cumsum(f)/SR
def smooth(x,n): return np.convolve(x,np.ones(max(1,n))/max(1,n),mode="same")
def saw(p): return 2*((p/(2*np.pi))%1.0)-1
def env_ar(n, a, r, sus=None):
    t=np.arange(n)/SR; e=np.clip(t/a,0,1)
    if r>0: e*=np.clip((n/SR-t)/r,0,1)
    return e
def corridor(x, rt=2.4, wet=0.42, bright=4000):
    n=len(x); out=np.zeros(n+int(0.5*SR))
    for d,g in [(0,1.0),(0.041,0.35),(0.077,0.28),(0.141,0.22),(0.27,0.16),(0.41,0.09)]:
        k=int(d*SR); out[k:k+n]+=x*g
    return A.reverb(out,rt60=rt,wet=wet,bright=bright)

# 1. Containment horn: three long blasts of a deep facility horn
def horn():
    total=11.5; n=int(total*SR); out=np.zeros(n)
    for start in (0.3,3.9,7.5):
        d=2.9; m=int(d*SR); t=np.arange(m)/SR
        droop=1-0.06*np.exp(-t/0.12)            # air pressure builds: pitch rises into the note
        x=np.zeros(m)
        for f,a in [(98,1.0),(98*1.335,0.8),(196,0.45),(98*0.5,0.35)]:
            for det in (-0.35,0.0,0.4):
                x+=a*saw(ph(np.full(m,f+det)*droop))/3
        x=A.butter(x,lo=45,hi=1400,order=2)
        breath=A.butter(rng.standard_normal(m),lo=200,hi=2500,order=2)*0.05
        e=np.clip(t/0.18,0,1)**1.5*np.clip((d-t)/0.35,0,1)
        x=(x+breath)*e
        k=int(start*SR); out[k:k+m]+=x
    out=np.tanh(out*1.8)/np.tanh(1.8)
    return A.trim_tail(A.fade(corridor(out,3.2,0.45,3000),0.002,0.8),2e-3)

# 2. Lab alert: electronic two-tone pulses through ceiling speakers
def lab_alert():
    total=11.0; n=int(total*SR); out=np.zeros(n)
    t0=0.2
    while t0<total-1.2:
        for f,dd,off in [(988,0.32,0.0),(740,0.42,0.36)]:
            m=int(dd*SR); t=np.arange(m)/SR
            p=ph(np.full(m,f))
            x=np.sign(np.sin(p))*0.5+np.sin(p)*0.5+0.25*np.sin(2*p)
            x*=np.clip(t/0.01,0,1)*np.clip((dd-t)/0.04,0,1)
            k=int((t0+off)*SR); out[k:k+m]+=x[:min(m,n-k)]
        # sub thump on each cycle
        m=int(0.5*SR); t=np.arange(m)/SR
        th=np.sin(ph(55+40*np.exp(-t/0.05)))*np.exp(-t/0.12)*0.6
        k=int(t0*SR); out[k:k+m]+=th[:min(m,n-k)]
        t0+=1.55
    spk=A.butter(out,lo=350,hi=4200,order=3)          # PA horn speakers
    spk=np.tanh(spk*2.2)/np.tanh(2.2)
    out=spk+A.butter(out,hi=120,order=2)*0.8
    return A.trim_tail(A.fade(corridor(out,2.6,0.48,3500),0.002,0.8),2e-3)

# 3. Systems online: relays clack down the corridors, deep power swell, rising tones, big final thump
def online():
    total=11.0; n=int(total*SR); t=np.arange(n)/SR; out=np.zeros(n)
    # relay cascade (getting closer)
    for i,tt in enumerate(np.linspace(0.2,3.2,9)):
        c=A.breaker_clunk(0.5+0.06*i, max(0.0,0.8-0.1*i))
        k=int(tt*SR); out[k:k+len(c)]+=c[:min(len(c),n-k)]*(0.35+0.05*i)
    # power swell: sub + pad rising
    sw=np.clip((t-1.0)/5.0,0,1)**2*np.clip((10.5-t)/1.5,0,1)
    f=38+18*np.clip((t-1.0)/6.0,0,1)
    pad=np.zeros(n)
    for mul,a in [(1,1.0),(2,0.5),(3,0.3),(4.01,0.2),(6,0.1)]:
        pad+=a*np.sin(ph(f*mul))
    hum=np.sin(ph(np.full(n,100.0)))*0.15+np.sin(ph(np.full(n,150.0)))*0.08
    out+=(pad*0.55+hum)*sw
    # three rising tones (C - E - G, bell-like synth)
    for k_,(st,fr) in enumerate([(5.2,261.6),(6.0,329.6),(6.8,392.0)]):
        m=int(2.8*SR); tt=np.arange(m)/SR
        b=(np.sin(ph(np.full(m,fr)))+0.5*np.sin(ph(np.full(m,fr*2)))+0.25*np.sin(ph(np.full(m,fr*3.01))))
        b*=np.clip(tt/0.02,0,1)*np.exp(-tt/1.1)*0.55
        k=int(st*SR); out[k:k+m]+=b[:min(m,n-k)]
    # final power-on thump + sparkle
    m=int(2.5*SR); tt=np.arange(m)/SR
    th=np.sin(ph(48+70*np.exp(-tt/0.06)))*np.exp(-tt/0.5)*1.2
    th+=A.butter(rng.standard_normal(m),lo=3000,hi=9000,order=2)*np.exp(-tt/0.25)*0.06
    k=int(7.7*SR); out[k:k+m]+=th[:min(m,n-k)]
    out=np.tanh(out*1.4)/np.tanh(1.4)
    return A.trim_tail(A.fade(corridor(out,3.0,0.4,4500),0.002,0.8),2e-3)

# 4. Old fire bell: electric striker bell, two long rings
def bell():
    total=10.5; n=int(total*SR); out=np.zeros(n)
    partials=[(1.0,1.0,1.4),(2.76,0.55,0.9),(5.40,0.35,0.5),(8.93,0.18,0.3),(13.3,0.08,0.2)]
    f0=820.0
    for (st,dur) in [(0.2,4.2),(5.4,4.2)]:
        hits=np.arange(st,st+dur,1/19.0)                       # striker ~19 hits/s
        for h in hits:
            m=int(0.9*SR); tt=np.arange(m)/SR
            s=np.zeros(m)
            for mul,a,dec in partials:
                s+=a*np.sin(2*np.pi*f0*mul*tt+rng.uniform(0,6.28))*np.exp(-tt/dec*3)
            s*=0.12*(0.85+0.3*rng.random())
            k=int(h*SR); out[k:k+m]+=s[:min(m,n-k)]
        # mechanical buzz of the striker
        m=int(dur*SR); k=int(st*SR)
        buzz=np.sign(np.sin(ph(np.full(m,19.0))))*A.butter(rng.standard_normal(m),lo=1500,hi=6000,order=2)*0.03
        out[k:k+m]+=buzz[:min(m,n-k)]
    out=A.butter(out,lo=250,hi=9000,order=2)
    out=np.tanh(out*1.5)/np.tanh(1.5)
    return A.trim_tail(A.fade(corridor(out,2.2,0.4,5000),0.002,0.6),2e-3)

A.OUT="./sirens3"
A.save("alarm_1_containment_horn", horn(), 0.8)
A.save("alarm_2_lab_alert", lab_alert(), 0.8)
A.save("alarm_3_systems_online", online(), 0.8)
A.save("alarm_4_fire_bell", bell(), 0.8)
