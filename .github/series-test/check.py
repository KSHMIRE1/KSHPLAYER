"""Compare intros.json aux positions réelles (tolérance 1,5 s)."""
import json, sys
db = json.load(open(sys.argv[1], encoding="utf-8"))["files"]
want = {"s01e01": ((35, 85), (520, 560)), "s01e02": ((72, 122), (515, 555)), "s01e03": ((20, 70), (520, 560))}
ok, lines = True, []
for key, e in db.items():
    ep = key.split("ma serie ")[-1][:6]
    if ep not in want: continue
    for what, got, exp in (("intro", e["intro"], want[ep][0]), ("fin", e["credits"], want[ep][1])):
        good = got is not None and abs(got[0] - exp[0]) <= 1.5 and abs(got[1] - exp[1]) <= 1.5
        ok &= good
        lines.append(f"{ep} {what}: trouvé {got} attendu {list(exp)} {'OK' if good else 'ECHEC'}")
seen = {k.split('ma serie ')[-1][:6] for k in db}
if seen != set(want): ok = False; lines.append(f"épisodes analysés : {sorted(seen)}")
print("\n".join(lines)); print("RESULTAT", "OK" if ok else "ECHEC")
sys.exit(0 if ok else 1)
