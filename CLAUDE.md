# KSHPLAYER — guide de reprise (pour Claude et pour les développeurs)

Lecteur vidéo « VLC bis » de **Calvin (KSHMIRE)** : lit tout (4K HDR, Dolby Vision, tous codecs FFmpeg),
design de son appli **KSHFLIX** (logo, rouge `#F0283A`, fond `#121217`, police **Unbounded**).
Deux éditions : **KSHPLAYER** (tout le monde) et **KSHPLAYER PRO** (monteurs vidéo).
Plateformes : **Windows** (principal) + **Mac** (Apple Silicon + Intel, macOS 13+). Apple TV / iPhone : plus tard.

L'utilisateur parle **français**, n'est pas développeur : réponses simples, concrètes, sans jargon.
Priorité absolue : **performances** (« je veux surtout pas qu'il ne soit pas performant ») et rendu adapté à chaque PC.

Version publiée : voir `VERSION` (1.1.1 au 2026-10-14).

**Priorité actuelle (oct. 2026) : la version Windows** (normal + PRO). Le Mac suit via `kshosc.lua` partagé —
toute modif de `mpv/scripts/kshosc.lua` touche aussi le Mac : garder `MAC`/`opts.platform` en tête.

---

## 1. Architecture

| Chemin | Rôle |
|---|---|
| `KSHPLAYER.sln`, `KSHPLAYER.csproj`, `*.xaml(.cs)`, `Player/`, `Updater.cs`, `Edition.cs`, `AppLog.cs` | **Appli Windows** — C# .NET 10, WinUI 3 non packagée (`net10.0-windows10.0.19041.0`, WindowsAppSDK self-contained) |
| `Setup/` | **Installateur Windows** (.NET Framework 4.8 WinForms, un seul .exe) |
| `mpv/` | **Partagé Windows + Mac** : `scripts/kshosc.lua` (toute l'interface du lecteur), `fonts/uosc_icons.otf` (Material Icons Round), `input.conf` |
| `macOS/` | **Appli Mac** — Swift/AppKit + MPVKit, projet généré par XcodeGen (`project.yml`) |
| `third_party/mpv/` | Archive libmpv Windows (shinchiro, `mpv-dev-x86_64-20261012-git-88eb9f2e73.7z`) |
| `.github/workflows/release.yml` | Fabrique **et publie** les 4 installateurs à chaque push sur `main` |
| `.github/workflows/lut-test.yml` + `.github/lut-test/` | Test GPU du moteur LUT (branche `lut-test` uniquement) |
| `VERSION`, `NOTES.txt` | Numéro de version et texte du bandeau de mise à jour |

### Lecteur Windows
- Le moteur est **libmpv** (`libmpv-2.dll`) piloté en P/Invoke (`Player/MpvNative.cs`, `Player/MpvPlayer.cs`).
- mpv affiche **sa propre fenêtre sans bordure** (`border=no`, `idle=once`, `autofit-larger=60%x60%`). L'accueil WinUI est caché pendant la lecture et réapparaît à la fin.
  - Ne pas réembarquer mpv dans une HWND WinUI : la barre de contrôle ne recevait plus la souris.
- Rendu : `vo=gpu-next`, `gpu-api=d3d11` et `hwdec=auto-safe`.
  - Il y a 3 niveaux de qualité selon la VRAM (DXGI, `Player/HardwareProfile.cs`) : ≥6000 Mo Max, ≥1800 Mo Équilibré, sinon Léger.
- Options mpv clés :
  - `config-dir=<exe>\mpv` et `load-scripts=no` ; seul `kshosc.lua` est chargé.
  - `script-opts` passe `kshosc-pro`, `lutdir`, `shotdir`, `platform` et `font`.
- Journaux : `%LOCALAPPDATA%\KSHPLAYER\mpv.log` et le journal de l'appli (`AppLog.cs`).
- Configurations de build :
  - `Debug` et `Release` → **KSHPLAYER** ;
  - `DebugPro` et `ReleasePro` → **KSHPLAYER PRO** (constante `KSHPRO`, AssemblyName `KSHPLAYERPRO`).

### Barre de contrôle `mpv/scripts/kshosc.lua` (≈1000 lignes, cœur de l'UI)
- Pilule flottante en bas, dessinée en ASS (overlay OSD). Elle apparaît au survol et disparaît après 1,6 s.
- La souris passe par `mp.set_key_bindings(..., 'force')`, ce qui empêche le glissement de la fenêtre sur la barre.
- `layout()` : un mode compact (pour les vidéos portrait et les fenêtres étroites) masque le volume et les ±10 s.
- La croix en haut à droite met `user-data/kshosc/exit-app=yes`, puis quitte.
  - L'appli Windows observe cette propriété et se ferme entièrement.
- Outils **PRO** :
  - timecode HH:MM:SS:II, image par image (←/→), J-K-L, entrée/sortie I/O (boucle) ;
  - infos techniques (Ctrl+I), repères de cadrage (G), capture PNG (S), séquences d'images, **LUT .cube** (T, Maj+T, Alt+T).
- **Moteur LUT** (depuis 1.1.1, 100 % carte graphique) : `cube_to_shader()` convertit le .cube en shader libplacebo.
  - Le shader contient une texture 3D `rgba16` et un hook sur `MAIN`.
  - Ses paramètres `ksh_k` (intensité) et `ksh_split` (ligne avant/après, -1 = pas de comparaison) changent en direct via `glsl-shader-opts`, sans recompilation.
  - Le shader est écrit dans `%TEMP%` (ou `$TMPDIR` sur Mac) et mis en cache selon le chemin, la taille et la date du .cube.
  - Une LUT 1D se rabat sur l'option mpv `lut`.
  - La capture **avec** LUT utilise un filtre lavfi `lut3d` le temps d'une image.
  - LUT mémorisée par dossier : `Documents\KSHPLAYER PRO\luts-par-dossier.json`.
  - **Ne pas revenir** au filtre CPU `lut3d`+`blend` pour l'affichage : il ramait en 4K (bug signalé par Calvin).

### Installateur Windows (`Setup/`)
- `KshAppConfig=Release|ReleasePro`. La cible `BuildPayload` publie l'appli, puis :
  - `KshTrim` supprime l'inutile (onnxruntime, DirectML, Windows.AI, pdb, langues, etc.) ;
  - `KshPack` (tâche MSBuild inline) compresse en **LZMS** via `cabinet.dll` ;
  - le résultat est embarqué en ressource `payload.kpk` dans le .exe (≈83 Mo).
- Installation par utilisateur dans `%LOCALAPPDATA%\Programs\<Produit>` : raccourcis, « Ouvrir avec », `Desinstaller.cmd` et clé de désinstallation HKCU.
- Le mode `/update` est silencieux : il reprend les choix précédents, attend la fermeture de l'appli, installe puis la relance.
- `KshDeployNas` (après build Release) copie l'exe sur le NAS de Calvin `K:\Backup\Logiciel KSHPC\KSHPLAYER\…`.
  - Cette étape est **ignorée automatiquement** si `K:\Backup` n'existe pas, donc rien ne casse chez quelqu'un d'autre.
- Piège connu : ne jamais nommer une propriété MSBuild `AppConfig` (réservée).

### Appli Mac (`macOS/`)
- Swift/AppKit avec MPVKit (`MPVKit-GPL` 1.1.0-n9.0.2). Rendu dans un `CAMetalLayer` : `vo=gpu-next`, `gpu-api=vulkan`, `gpu-context=moltenvk`, `hwdec=videotoolbox`.
- La souris et le clavier sont transmis à mpv (commandes `mouse` / `keydown` / `keypress`), puis c'est le même `kshosc.lua` qui dessine l'UI (`platform=mac`).
- `prepare.sh` copie `mpv/` dans les ressources, XcodeGen génère le projet, `make-dmg.sh` fabrique les .dmg.
- Signature **ad hoc** seulement (pas de compte Apple Developer). Au 1er lancement : clic droit → Ouvrir, ou Réglages → Confidentialité → « Ouvrir quand même ».

### Publication et mises à jour
- Un push sur `main` du dépôt **KSHMIRE1/KSHPLAYER** lance `release.yml` :
  - `windows-latest` : `dotnet msbuild Setup/... -p:Configuration=Release|ReleasePro` ;
  - `macos-15` : xcodebuild universel ;
  - ensuite, un push forcé de la branche orpheline **`telechargements`** avec les 4 fichiers et `version.json`.
- Liens publics : `https://raw.githubusercontent.com/KSHMIRE1/KSHPLAYER/telechargements/<fichier>`
  - Fichiers : `KSHPLAYER-Setup.exe`, `KSHPLAYER-PRO-Setup.exe`, `KSHPLAYER.dmg`, `KSHPLAYER-PRO.dmg`, `version.json`.
- Les applis lisent `version.json` au démarrage.
  - **Windows** : bandeau « Mettre à jour » → téléchargement → `Setup /update` → relance.
  - **Mac** : bandeau « Télécharger » qui ouvre le .dmg.
- Côté Windows, l'appli recopie aussi les 4 fichiers sur le NAS de Calvin s'il est présent (`Updater.MirrorToNasAsync`).
- **Pour publier une version** : augmenter `VERSION`, écrire `NOTES.txt` (une ligne), puis pousser sur `main`. Ça prend environ 6 min.
  - Pour un push qui ne doit rien publier, mettre `[skip ci]` dans le message de commit.
- ⚠️ L'URL de mise à jour est **codée en dur** sur `KSHMIRE1/KSHPLAYER` (`Updater.cs`, `macOS/Sources/Updater.swift`, `.github/version_json.py`).
  - Un fork publie ses propres fichiers, mais les applis installées continuent de suivre le dépôt de Calvin.

---

## 2. Compiler

### Windows (Visual Studio 2022/2026 + .NET 10 SDK + charge « Développement .NET Desktop »)
```powershell
git clone https://github.com/KSHMIRE1/KSHPLAYER.git
cd KSHPLAYER
# appli seule (F5 dans VS, configuration Debug ou DebugPro, plateforme x64)
# installateurs :
dotnet msbuild Setup/KSHPLAYER.Setup.csproj -restore -p:Configuration=Release    -p:Platform=x64
dotnet msbuild Setup/KSHPLAYER.Setup.csproj -restore -p:Configuration=ReleasePro -p:Platform=x64
# → Setup/bin/x64/<Config>/net48/KSHPLAYER-Setup.exe / KSHPLAYER-PRO-Setup.exe
```
`libmpv-2.dll` est extraite automatiquement de `third_party/mpv/*.7z` dans `engine/` au premier build (avec le `tar.exe` de Windows).

### Mac (Xcode 16+, Homebrew)
```bash
brew install xcodegen
bash macOS/prepare.sh && (cd macOS && xcodegen generate)
cd macOS && xcodebuild -project KSHPLAYER.xcodeproj -scheme "KSHPLAYER" -configuration Release \
  -destination 'generic/platform=macOS' -derivedDataPath build ONLY_ACTIVE_ARCH=NO CODE_SIGNING_ALLOWED=NO build
# schéma "KSHPLAYER PRO" pour la PRO ; puis : bash make-dmg.sh  → macOS/dist/*.dmg
```
Le plus simple reste de **laisser GitHub Actions compiler** : il suffit de pousser sur `main`.

---

## 3. Audit — état au 2026-10-14

**Vérifié et fonctionnel**
- La CI complète passe (run vert, 1.1.1 publiée). Les .dmg sont universels (arm64 + x86_64, ≈38 Mo) et les .exe font ≈83 Mo.
- Moteur LUT GPU testé en CI : couleurs exactes, intensité et ligne avant/après réglables en direct (`lut-test.yml`).
- Calvin a testé sur son PC : la lecture, l'image, le son et les LUT 100 % marchent.

**Jamais testé en conditions réelles** (à vérifier en priorité)
- l'appli **Mac** sur un vrai Mac (aucun retour encore) ;
- mise à jour automatique Windows (`/update`) et copie sur le NAS ;
- LUT 1.1.1 sur une vraie carte graphique Windows/Mac ;
- barre en mode portrait ;
- installateur allégé (`KshTrim`) sur un PC « vierge ».

**Points faibles connus / à décider**
- `audio-channels=stereo` est forcé dans `MpvPlayer.cs` : c'était voulu pour Calvin (son des haut-parleurs de l'écran), mais ça **downmixe aussi chez ceux qui ont un ampli 5.1/7.1**.
  - Mettre `auto` ou en faire un réglage.
- `osd-bar` est défini deux fois (`no` puis `yes`) dans `MpvPlayer.cs`. Sans gravité, à nettoyer.
- Binaires **non signés** : SmartScreen sur Windows (« Informations complémentaires → Exécuter quand même »), Gatekeeper sur Mac.
- **Licence** : libmpv/FFmpeg et MPVKit-GPL sont sous **GPL**. Le dépôt est public, donc c'est cohérent ; il n'y a pas encore de fichier `LICENSE`.
- La branche `lut-test` est restée sur GitHub (elle se supprime sans risque).
- Aucun test automatique de l'UI ; seul le moteur LUT est testé en CI.

**Demandes en attente / idées**
- **.braw (Blackmagic RAW)** : FFmpeg ne sait pas le lire.
  - Il faudrait le Blackmagic RAW SDK (gratuit après un formulaire, à télécharger par Calvin).
  - L'idée : un décodeur branché à mpv via `mpv_stream_cb_add_ro` (flux rawvideo à taille d'image fixe, donc recherche facile).
  - **Mis de côté** par Calvin pour l'instant.
- Aperçu Quick Look (barre espace) sur Mac.
- Apple TV / iPhone.
- Signature de code (payante).

---

## 4. Règles de travail avec Calvin
- Il ne faut jamais saisir ses mots de passe. Tout téléchargement sur son PC se fait avec son accord explicite, en précisant le nom, la source et la taille.
- Dossier du projet sur son PC : `C:\Users\KSHMIRE\source\repos\KSHPLAYER` (**pas un clone git** : on y copie les fichiers modifiés).
  - Le dossier `.github` y est protégé : il n'existe que sur GitHub.
- NAS : `K:\Backup\Logiciel KSHPC` (lecteur réseau `\\KSHNAS`).
- Ses autres projets (KSHFLIX, serveur Emby) partagent le même design ; garder la cohérence visuelle.
