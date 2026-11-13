"""Générique vu à l'image : début attendu à ±3 s (images clés toutes les 2 s)."""
import json, sys
db = json.load(open(sys.argv[1], encoding="utf-8"))["files"]
want = {"autre serie s01e01": (350, 420), "autre serie s01e02": (336, 410)}
ok, lines = True, []
for name, exp in want.items():
    e = next((v for k, v in db.items() if k.endswith(name + ".mkv")), None)
    got = e and e["credits"]
    good = bool(got) and abs(got[0] - exp[0]) <= 3 and abs(got[1] - exp[1]) <= 2
    ok &= good
    lines.append(f"{name} fin: trouvé {got} attendu {list(exp)} {'OK' if good else 'ECHEC'}")
print("\n".join(lines)); print("RESULTAT", "OK" if ok else "ECHEC"); sys.exit(0 if ok else 1)
