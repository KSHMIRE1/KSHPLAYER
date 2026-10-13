"""Écrit version.json (lu par les applis pour leurs mises à jour)."""
import datetime
import json
import os
import sys

version, out = sys.argv[1], sys.argv[2]
notes = open("NOTES.txt", encoding="utf-8").read().strip() if os.path.exists("NOTES.txt") else ""
with open(out, "w", encoding="utf-8") as f:
    json.dump({
        "version": version,
        "date": datetime.date.today().isoformat(),
        "notes": notes,
        "files": {
            "windows": "KSHPLAYER-Setup.exe",
            "windows_pro": "KSHPLAYER-PRO-Setup.exe",
            "mac": "KSHPLAYER.dmg",
            "mac_pro": "KSHPLAYER-PRO.dmg",
        },
    }, f, ensure_ascii=False, indent=2)
