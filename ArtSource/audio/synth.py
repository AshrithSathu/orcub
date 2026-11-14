"""
Orcub - procedural sound effects (wooden / clay toy palette).

    uv run --no-project --with numpy python ArtSource/audio/synth.py

Writes 44.1 kHz mono 16-bit WAVs to Assets/Orcub/Audio/.
"""
import os
import wave

import numpy as np

SR = 44100
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Assets", "Orcub", "Audio")
rng = np.random.default_rng(7)


def t_axis(seconds):
    return np.arange(int(SR * seconds)) / SR


def env_exp(t, decay):
    return np.exp(-t / decay)


def lowpass(x, cutoff):
    """One-pole low-pass, cheap and warm."""
    a = np.exp(-2 * np.pi * cutoff / SR)
    y = np.zeros_like(x)
    acc = 0.0
    for i, v in enumerate(x):
        acc = (1 - a) * v + a * acc
        y[i] = acc
    return y


def highpass(x, cutoff):
    return x - lowpass(x, cutoff)


def resonator(x, freq, q):
    """Two-pole resonant band-pass: turns a click into a wooden 'tock'."""
    w = 2 * np.pi * freq / SR
    r = np.exp(-w / (2 * q))
    b1, b2 = 2 * r * np.cos(w), -r * r
    y = np.zeros_like(x)
    y1 = y2 = 0.0
    for i, v in enumerate(x):
        y0 = v + b1 * y1 + b2 * y2
        y[i] = y0
        y2, y1 = y1, y0
    return y * (1 - r)


def fade(x, ms_in=1.0, ms_out=8.0):
    n_in, n_out = int(SR * ms_in / 1000), int(SR * ms_out / 1000)
    x = x.copy()
    if n_in:
        x[:n_in] *= np.linspace(0, 1, n_in)
    if n_out:
        x[-n_out:] *= np.linspace(1, 0, n_out)
    return x


def normalize(x, peak=0.89):
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 0 else x


def save(name, x):
    os.makedirs(OUT, exist_ok=True)
    data = (np.clip(x, -1, 1) * 32767).astype(np.int16)
    with wave.open(os.path.join(OUT, name), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(data.tobytes())
    print("wrote", name, f"{len(x) / SR:.3f}s")


# ------------------------------------------------------------------ sounds

def tick():
    """Ratchet tooth: tiny wooden click. Pitched per ring in game."""
    t = t_axis(0.045)
    burst = rng.standard_normal(len(t)) * env_exp(t, 0.0018)
    body = resonator(burst, 2100, 9) * 2.2 + resonator(burst, 3400, 12) * 1.0
    snap = highpass(burst, 4000) * 0.25
    return normalize(fade(body + snap, 0.2, 10), 0.7)


def pick():
    """Finger grabs a ring: soft muted tap."""
    t = t_axis(0.07)
    burst = rng.standard_normal(len(t)) * env_exp(t, 0.003)
    body = resonator(burst, 900, 6) + resonator(burst, 1500, 8) * 0.5
    return normalize(fade(lowpass(body, 5000), 0.3, 20), 0.55)


def clunk():
    """Clamp pops off the tube: chunky clay-wood knock with a little snap on top. The reward sound."""
    t = t_axis(0.32)
    knock = rng.standard_normal(len(t)) * env_exp(t, 0.004)
    body = resonator(knock, 240, 7) * 3.0 + resonator(knock, 520, 9) * 1.6 + resonator(knock, 1150, 10) * 0.6
    tone = (np.sin(2 * np.pi * 180 * t) * 0.35 + np.sin(2 * np.pi * 372 * t) * 0.15) * env_exp(t, 0.06)
    snap_t = t_axis(0.03)
    snap = resonator(rng.standard_normal(len(snap_t)) * env_exp(snap_t, 0.0015), 3000, 10) * 1.2
    out = body + tone
    out[: len(snap)] += snap
    # second, smaller bounce 45 ms later (the clamp settling)
    d = int(0.045 * SR)
    out[d:] += body[: len(out) - d] * 0.28
    return normalize(fade(lowpass(out, 6500), 0.3, 40), 0.9)


def thud():
    """Touched a locked ring: dull, muffled 'no'."""
    t = t_axis(0.22)
    knock = rng.standard_normal(len(t)) * env_exp(t, 0.006)
    body = resonator(knock, 120, 4) * 4.0 + resonator(knock, 260, 5) * 1.5
    tone = np.sin(2 * np.pi * 95 * t) * env_exp(t, 0.05) * 0.5
    return normalize(fade(lowpass(body + tone, 1800), 0.5, 40), 0.85)


def whoosh():
    """Ring pulled out: airy rising sweep."""
    t = t_axis(0.42)
    noise = rng.standard_normal(len(t))
    sweep_env = np.sin(np.pi * np.clip(t / 0.42, 0, 1)) ** 1.6
    # time-varying band: crossfade between low and high band-passed noise
    low = resonator(noise, 700, 1.4)
    high = resonator(noise, 2600, 1.4)
    mix = np.clip(t / 0.42, 0, 1)
    out = (low * (1 - mix) + high * mix) * sweep_env
    return normalize(fade(out, 2, 30), 0.6)


def marimba_note(freq, seconds, amp=1.0):
    t = t_axis(seconds)
    tone = (np.sin(2 * np.pi * freq * t) * env_exp(t, 0.55)
            + np.sin(2 * np.pi * freq * 3.93 * t) * env_exp(t, 0.09) * 0.35
            + np.sin(2 * np.pi * freq * 9.2 * t) * env_exp(t, 0.025) * 0.12)
    mallet = resonator(rng.standard_normal(len(t)) * env_exp(t, 0.0015), freq * 2, 4) * 0.4
    return (tone + mallet) * amp


def chime():
    """Board cleared: little marimba arpeggio."""
    total = t_axis(1.6)
    out = np.zeros_like(total)
    notes = [(523.25, 0.00, 0.9), (659.25, 0.09, 0.85), (783.99, 0.18, 0.85), (1046.5, 0.30, 1.0)]
    for f, start, amp in notes:
        n = marimba_note(f, 1.3, amp)
        s = int(start * SR)
        out[s:s + len(n)] += n[: len(out) - s]
    return normalize(fade(out, 0.5, 200), 0.85)


def main():
    save("tick.wav", tick())
    save("pick.wav", pick())
    save("clunk.wav", clunk())
    save("thud.wav", thud())
    save("whoosh.wav", whoosh())
    save("chime.wav", chime())


main()
