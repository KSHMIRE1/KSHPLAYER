# KSHPLAYER

Lecteur vidéo qui lit tout (4K HDR, Dolby Vision, tous codecs), moteur **mpv / FFmpeg**, design **KSHFLIX**.
Deux éditions : **KSHPLAYER** (tout le monde) et **KSHPLAYER PRO** (monteurs : timecode, J-K-L, LUT, repères…).

| Dossier | Contenu |
|---|---|
| `/` | Appli Windows (C# / WinUI 3) — `KSHPLAYER.sln` |
| `Setup/` | Installateur Windows (KSHPLAYER-Setup.exe / KSHPLAYER-PRO-Setup.exe) |
| `mpv/` | Partagé Windows + Mac : barre de contrôle `kshosc.lua`, icônes, raccourcis |
| `macOS/` | Appli Mac (Swift / AppKit + MPVKit), compilée par GitHub Actions |

**Mac :** onglet *Actions* > *macOS* > les `.dmg` sont dans *Artifacts*.
