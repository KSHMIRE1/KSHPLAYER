"""Les boutons sont-ils dessinés en bas à droite ? (intro : pastille sombre KSHFLIX ; suivant : rouge KSHFLIX)"""
import sys
import numpy as np
from PIL import Image
d = sys.argv[1]
def zone(n):
    im = np.asarray(Image.open(f"{d}/{n}.png").convert("RGB")).astype(int)
    h, w, _ = im.shape
    return im[h // 2:, w // 2:], (w, h)
z, size = zone("intro")
dark = (np.abs(z - [0x12, 0x12, 0x17]).sum(axis=2) < 20).sum()
z2, _ = zone("suivant")
red = (np.abs(z2 - [0xF0, 0x28, 0x3A]).sum(axis=2) < 40).sum()
print(f"capture {size} · pastille intro : {dark} px sombres · bouton suivant : {red} px rouges")
ok = dark > 300 and red > 300
print("RESULTAT", "OK" if ok else "ECHEC"); sys.exit(0 if ok else 1)
