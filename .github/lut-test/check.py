"""Vérifie les captures : LUT (r,g,b)->(b,g,r) ; 100 %, 0 %, 50 % à droite de la ligne."""
import sys
import numpy as np
from PIL import Image

d = sys.argv[1]
im = lambda n: np.asarray(Image.open(f"{d}/{n}.png").convert("RGB")).astype(float)
k0, k100, sp = im("k0"), im("k100"), im("split")
print("taille", k0.shape)
exp100 = k0[..., ::-1]
w = k0.shape[1]
e1 = np.abs(k100 - exp100).mean()
diff0 = np.abs(k100 - k0).mean()
left = np.abs(sp[:, : w // 2 - 4] - k0[:, : w // 2 - 4]).mean()
right = np.abs(sp[:, w // 2 + 4 :] - (k0[:, w // 2 + 4 :] + exp100[:, w // 2 + 4 :]) / 2).mean()
print(f"100% vs attendu: {e1:.2f} | 100% vs 0%: {diff0:.2f} | gauche brute: {left:.2f} | droite 50%: {right:.2f}")
ok = e1 < 3 and diff0 > 20 and left < 3 and right < 3
print("RESULTAT", "OK" if ok else "ECHEC")
sys.exit(0 if ok else 1)
