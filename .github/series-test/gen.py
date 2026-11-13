"""Fabrique 3 faux épisodes : même intro (à des moments différents), même générique de fin, contenu différent."""
import numpy as np, subprocess, sys, os
R = 48000
out = sys.argv[1]
os.makedirs(out, exist_ok=True)

def melody(seed, secs):
    rng = np.random.default_rng(seed)
    t = np.arange(int(secs * R)) / R
    sig = np.zeros_like(t)
    step = 0.3
    for k in range(int(secs / step)):
        a, b = int(k * step * R), int((k + 1) * step * R)
        tt = t[a:b] - t[a]
        env = np.exp(-tt * 4)
        for f in rng.choice([220, 247, 262, 294, 330, 349, 392, 440, 494, 523, 587, 659, 698, 784], 3):
            sig[a:b] += np.sin(2 * np.pi * f * tt) * env
        if k % 2 == 0:
            n = min(b - a, int(0.05 * R))
            sig[a:a + n] += rng.normal(0, 0.6, n) * np.exp(-np.arange(n) / (0.01 * R))
    return sig * 0.2

def content(seed, secs):
    """« dialogues » : bruit filtré modulé + tons glissants, différents à chaque épisode"""
    rng = np.random.default_rng(seed)
    n = int(secs * R)
    noise = np.convolve(rng.normal(0, 1, n), np.ones(30) / 30, mode="same")
    env = np.repeat(rng.uniform(0, 1, n // 4800 + 1), 4800)[:n] > 0.4
    t = np.arange(n) / R
    f = 150 + 100 * np.sin(2 * np.pi * rng.uniform(0.05, 0.3) * t)
    tone = np.sin(2 * np.pi * np.cumsum(f) / R) * 0.3
    return (noise * 2 + tone) * env * 0.25

intro, credits = melody(1, 50), melody(2, 40)
plans = {  # (début de l'intro, durée totale, début du générique)
    "Ma Serie S01E01": (35, 560, 520, "aac", "48000"),
    "Ma Serie S01E02": (72, 560, 515, "ac3", "44100"),
    "Ma Serie S01E03": (20, 560, 520, "libopus", "48000"),
}
for i, (name, (a, total, c, codec, rate)) in enumerate(plans.items()):
    sig = content(100 + i, total)
    sig[int(a * R):int(a * R) + len(intro)] = intro
    sig[int(c * R):int(c * R) + len(credits)] = credits
    pcm = (np.clip(sig, -1, 1) * 32000).astype("<i2")
    raw = os.path.join(out, "tmp.raw")
    pcm.tofile(raw)
    subprocess.check_call(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", f"color=c=0x202030:s=320x180:r=10:d={total}",
        "-f", "s16le", "-ar", str(R), "-ac", "1", "-i", raw, "-c:v", "libx264", "-preset", "ultrafast",
        "-c:a", codec, "-ar", rate, "-ac", "2", "-b:a", "128k", "-shortest", os.path.join(out, name + ".mkv")])
    os.remove(raw)
print("ok")
