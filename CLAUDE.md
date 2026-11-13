# KSHPLAYER — guide de reprise (pour Claude et pour les développeurs)

Lecteur vidéo « VLC bis » de **Calvin (KSHMIRE)** : lit tout (4K HDR, Dolby Vision, tous codecs FFmpeg),
design de son appli **KSHFLIX** (logo, rouge `#F0283A`, fond `#121217`, police **Unbounded**).
Deux éditions : **KSHPLAYER** (tout le monde) et **KSHPLAYER PRO** (monteurs vidéo).
Plateformes : **Windows** (principal) + **Mac** (Apple Silicon + Intel, macOS 13+). Apple TV / iPhone : plus tard.

L'utilisateur parle **français**, n'est pas développeur : réponses simples, concrètes, sans jargon.
Priorité absolue : **performances** (« je veux surtout pas qu'il ne soit pas performant ») et rendu adapté à chaque PC.

Version publiée : voir `VERSION` — **1.3.1 publiée le 2026-11-13** (séries : passer l'intro, épisode suivant ; 1.2.0 = travail de Romain, voir § 3 bis).

**Priorité actuelle (oct. 2026) : la version Windows** (normal + PRO). Le Mac suit via `kshosc.lua` partagé —
toute modif de `mpv/scripts/kshosc.lua` touche aussi le Mac : garder `MAC`/`opts.platform` en tête.

---

## 1. Architecture

| Chemin | Rôle |
|---|---|
| `KSHPLAYER.sln`, `KSHPLAYER.csproj`, `Program.cs`, `Dialogs.cs`, `Player/`, `Updater.cs`, `Edition.cs`, `AppLog.cs` | **Lecteur Windows** — C# .NET Framework 4.8 (déjà dans Windows 10/11), **sans écran d'accueil** depuis 1.2 (avant : WinUI 3 + .NET 10 embarqués) |
| `Setup/` | **Installateur Windows** (.NET Framework 4.8 WinForms, un seul .exe) |
| `Apercu/` | **Aperçu à la barre Espace + désinstallateur** : `KSHPLAYER-Apercu.exe` (.NET Framework 4.8, x64), installé à côté du lecteur, compilé automatiquement par le projet `Setup` |
| `mpv/` | **Partagé Windows + Mac** : `scripts/kshosc.lua` (toute l'interface du lecteur), `fonts/uosc_icons.otf` (Material Icons Round), `input.conf` |
| `macOS/` | **Appli Mac** — Swift/AppKit + MPVKit, projet généré par XcodeGen (`project.yml`) |
| `third_party/mpv/` | Archive libmpv Windows (shinchiro, `mpv-dev-x86_64-20261012-git-88eb9f2e73.7z`) |
| `.github/workflows/release.yml` | Fabrique **et publie** les 4 installateurs à chaque push sur `main` |
| `.github/workflows/lut-test.yml` + `.github/lut-test/` | Test GPU du moteur LUT (branche `lut-test` uniquement) |
| `VERSION`, `NOTES.txt` | Numéro de version et texte du bandeau de mise à jour |

### Lecteur Windows
- **Pas d'écran d'accueil** (1.2, demandé par Romain) : `KSHPLAYER.exe "<fichier>" [--start=<secondes>]` ouvre la vidéo tout de suite (double-clic, « Ouvrir avec », Entrée dans l'aperçu Espace) et **fermer la vidéo ferme KSHPLAYER**. Lancé sans fichier (menu Démarrer, Bureau, menu de l'aperçu) : fenêtre « Ouvrir » de Windows (`Dialogs.PickVideo`, avec une fenêtre invisible dans la barre des tâches pour qu'on la retrouve), annuler = quitter.
  - `Program.cs` : lit les arguments, crée le moteur, attend sa fermeture (`MpvPlayer.Shutdown`), installe la mise à jour prête puis quitte. Messages d'erreur (moteur introuvable…) : `Dialogs.Error`.
  - PRO : un dossier, ou une image numérotée (`plan_010_0001.exr`), s'ouvre en séquence d'images (`mf://dossier/plan_010_*.exr`, `LoadSequence`). Une image seule s'ouvre comme une image.
  - Sans écran à dessiner, WinUI ne servait plus à rien : .NET Framework 4.8, exe d'environ 140 Ko, installateur passé de ≈83 Mo à ≈35 Mo (dont 34 pour libmpv), démarrage plus rapide. Il compile aussi hors Windows (Roslyn + assemblys de référence 4.8), ce qui permet de le tester.
- Le moteur est **libmpv** (`libmpv-2.dll`) piloté en P/Invoke (`Player/MpvNative.cs`, `Player/MpvPlayer.cs`).
- mpv affiche **sa propre fenêtre sans bordure** (`border=no`, `autofit-larger=60%x60%`), avec l'icône KSHPLAYER (`WindowIcon`, sinon Windows montre celle de mpv).
  - `kshosc.lua` passe mpv en `idle=yes` et le ferme lui-même en fin de lecture (`idle-active`), **sauf** si un fichier n'a pas pu être lu : la fiche « lecture impossible » reste affichée jusqu'à ce qu'on la ferme.
  - `--start` sert à « ouvrir en grand » depuis l'aperçu Espace ; `MpvPlayer.Load` utilise `loadfile <url> replace -1 start=…` (argument `index` : mpv ≥ 0.38, le libmpv livré est une 0.41).
  - **Le moteur s'arrête sur un fil à part** (`MpvPlayer.Dispose` → fil `mpv-arret`). En se détruisant, libmpv appelle `CoUninitialize()` sur le fil qui fait `mpv_terminate_destroy` (sortie audio WASAPI de mpv). Fait sur le fil de l'interface (jusqu'en 1.1.1), chaque vidéo refermée retirait une inscription COM/WinRT à l'accueil WinUI : cause probable de la croix qui ne fermait plus l'appli. **Ne jamais détruire mpv sur un fil UI ou COM.** Les commandes (fil principal, message de mise à jour venu d'un autre fil) et l'arrêt sont protégés par un verrou (`_gate`).
- **Mises à jour** (`Updater.cs`, `UpdateOnClose`) : pendant la lecture, lecture de `version.json` ; si une version plus récente existe, l'installateur est téléchargé en arrière-plan dans `%TEMP%\KSHPLAYER-maj` (reprise d'un téléchargement interrompu, clé de cache `?v=<version>`, version de l'exe vérifiée, réutilisé d'une session à l'autre), un message passager s'affiche dans la vidéo (« Mise à jour x.y.z prête · elle s'installera à la fermeture »), et à la fermeture le lecteur lance `Setup /update /pid=<lecteur>`.
- Rendu : `vo=gpu-next`, `gpu-api=d3d11` et `hwdec=auto-safe`.
  - Il y a 3 niveaux de qualité selon la VRAM (DXGI, `Player/HardwareProfile.cs`) : ≥6000 Mo Max, ≥1800 Mo Équilibré, sinon Léger.
- Options mpv clés :
  - `config-dir=<exe>\mpv` et `load-scripts=no` ; seul `kshosc.lua` est chargé (plus les scripts intégrés de mpv).
  - `script-opts` passe `kshosc-pro`, `lutdir` et `shotdir` (PRO).
  - Diagnostic : variable d'environnement `KSHPLAYER_MPV="hwdec=no,…"` (options mpv en plus).
- Journaux : `%LOCALAPPDATA%\KSHPLAYER\mpv.log` et `kshplayer.log` (`AppLog.cs`).
- Configurations de build :
  - `Debug` et `Release` → **KSHPLAYER** ;
  - `DebugPro` et `ReleasePro` → **KSHPLAYER PRO** (constante `KSHPRO`, AssemblyName `KSHPLAYERPRO`).
- Les anciens fichiers de l'accueil (`App.xaml(.cs)`, `MainWindow.xaml(.cs)`) sont supprimés du dépôt ; s'il en reste dans une copie du projet, `KSHPLAYER.csproj` les ignore.

### Barre de contrôle `mpv/scripts/kshosc.lua` (≈1000 lignes, cœur de l'UI)
- Pilule flottante en bas, dessinée en ASS (overlay OSD). Elle apparaît au survol et disparaît après 1,6 s.
- La souris passe par `mp.set_key_bindings(..., 'force')`, ce qui empêche le glissement de la fenêtre sur la barre.
- `layout()` : un mode compact (pour les vidéos portrait et les fenêtres étroites) masque le volume et les ±10 s.
- **Vidéo précédente / suivante** (1.2, idée d'Orbit Look) : Pg préc. / Pg suiv., touches multimédia, boutons latéraux de souris et boutons ⏮ ⏭ (nom de la voisine au survol). Même dossier, même type (vidéo ou audio), tri « comme l'Explorateur » (`nat_key`), fichiers `._*` du Mac ignorés. Les chapitres passent sur Ctrl + Pg préc. / Pg suiv. Le titre suit le fichier (`start-file` règle `force-media-title`, sauf séquences `mf://`).
- **Séries** (1.3, demandé par Calvin) : boutons « Passer l'intro » et « Épisode suivant » en bas à droite (Entrée = le bouton affiché).
  - Où sont l'intro / le générique : 1) chapitres nommés Intro, Opening, Générique, Credits… (`chapter_marks`, marche aussi sur Mac) ; 2) sinon, Windows : `KSHPLAYER.exe --intros "<épisode>" "<précédent>" "<suivant>"` (`Intros.cs`) lancé en arrière-plan par la barre (`kshosc-introexe`), résultats dans `%LOCALAPPDATA%\KSHPLAYER\intros-v2.json` (v2 depuis 1.3.1) (`kshosc-introdb`, clé = chemin en minuscules A-Z avec `\`).
  - `Intros.cs` : le son (mono 8 kHz, via libmpv `ao=pcm`, `vid=no`, `untimed`) des 7 premières et 4 dernières minutes de deux épisodes voisins → empreinte 32 bits / 0,1 s (33 bandes 300 Hz–3 kHz, façon Chromaprint), puis le plus long passage commun quel que soit le décalage (tolérance ±0,1 s ; morceaux ≥ 3 s recollés si le décalage bouge de ≤ 1,5 s — une coupe au montage le fait bouger, cf. How to Sell Drugs Online Fast ; total ≥ 15 s, intro ≤ 3 min, bords « nets » : 5 images sur 10). Test sur de vraies empreintes : `.github/series-test/reel/`, `introstest --compare a.fp b.fp`. Empreintes en cache (`empreintes\`, chaque épisode lu une fois), analyse de l'épisode en cours + préparation du suivant, priorité basse, une analyse à la fois (mutex).
  - Seulement pour les noms « épisode » (S01E02, 1x02, E02, Épisode 2, « - 02 ») avec un épisode suivant dans le dossier (`episode_like`, `series_on`).
  - « Épisode suivant » : dès le générique détecté (compte à rebours 10 s puis enchaînement, bouton « Générique » pour rester), sinon 25 s avant la fin sans compte à rebours ; en fin de fichier on enchaîne au lieu de fermer (PRO / keep-open : `eof-reached`).
  - Diagnostic : propriété `user-data/kshosc/episode`. Test CI : `.github/workflows/series-test.yml` (branche `series-test` ; faux épisodes, détection Linux + vrai KSHPLAYER.exe Windows, lecteur sous xvfb ; captures poussées sur la branche `test-captures`).
  - ⏮ ⏭ toujours visibles (grisés sans voisine), barre ≈ 25 % plus petite (`layout()` : `H / 1150`, max 1,3).
- **Fiche « lecture impossible »** (1.2) : `end-file` avec `reason=error` → carte KSHFLIX (`render_error`) avec la raison en clair (`explain_error` : introuvable, vide, téléchargement pas fini, RAW non pris en charge comme .braw, pas une vidéo…), boutons Afficher dans le dossier / ⏮ ⏭ / Fermer, Échap ou Entrée pour fermer.
- **Mode aperçu** (`kshosc-apercu=yes`, utilisé par `KSHPLAYER-Apercu.exe`) : Espace / Échap referme, ← → ↑ ↓ vidéo voisine, Entrée (ou le bouton en haut à droite) ouvre en grand dans le lecteur (`kshosc-appexe`, `--start=`), badge « APERÇU · ESPACE POUR FERMER ».
- Mesure du texte : `text_w()` mesure avec libass (`compute_bounds`) ; libass ajoute une marge fixe, d'où la mesure « texte » vs « textetexte ». Coupe au milieu UTF-8 : `ellipsize_middle()`.
- La croix en haut à droite quitte (et pose `user-data/kshosc/exit-app=yes`, utile seulement à l'ancienne appli 1.1.x qui revenait à son accueil).
- **Message passager** (1.2) : `script-message-to kshosc ksh-toast "<texte>" [secondes]` → pilule KSHFLIX centrée sous la rangée du haut (ex. mise à jour prête).
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
- `KshAppConfig=Release|ReleasePro`. La cible `BuildPayload` compile le lecteur et l'aperçu (.NET Framework 4.8) dans un même dossier (avec `libmpv-2.dll` et `mpv\`), puis :
  - `KshPack` (tâche MSBuild inline) compresse en **LZMS** via `cabinet.dll` ;
  - le résultat est embarqué en ressource `payload.kpk` dans le .exe (≈35 Mo).
- Installation par utilisateur dans `%LOCALAPPDATA%\Programs\<Produit>` : raccourcis, « Ouvrir avec », aperçu Espace (clé `HKCU\…\Run`, valeur « KSHPLAYER Aperçu ») et clé de désinstallation HKCU (éditeur, date, taille, `UninstallString` = `KSHPLAYER-Apercu.exe /uninstall`, `QuietUninstallString` = `… /uninstall /S`).
- **Écran adapté à ce qui est installé** (1.2, comme Orbit Look) : installation, « MISE À JOUR · 1.1.1 → 1.2.0 » (+ carte « Nouveautés » = `NOTES.txt` embarqué), réinstallation, remplacement d'une version plus récente. Les choix précédents sont repris ; KSHPLAYER et son aperçu sont **fermés automatiquement** (`Installer.StopRunning`).
- Le mode `/update` reprend les choix précédents sans rien demander :
  - lancé par le lecteur 1.2 (`/pid=<lecteur>`) : attend la fin de ce lecteur, puis **qu'aucune vidéo ne soit ouverte** (sans limite, « Annuler » possible ; il ne coupe jamais une vidéo), installe (aperçu arrêté puis relancé) et se referme. Il ne relance pas le lecteur (plus d'accueil à rouvrir) ;
  - lancé par une ancienne version (1.1.x, sans `/pid`) : comme avant, 10 s pour que l'appli se ferme, puis elle est fermée de force.
- Une seule installation / mise à jour / désinstallation à la fois par édition (verrou `Local\<Produit>-Installation`).
- **Désinstallation** (1.2) : fenêtre KSHFLIX (`Apercu/UninstallForm.cs`) au lieu de l'ancien `Desinstaller.cmd`, option « effacer aussi les réglages et journaux » (`%LOCALAPPDATA%\KSHPLAYER`). Les LUT et captures PRO restent. Le dossier du programme est supprimé 2 s après la fermeture du désinstallateur (cmd caché).
- `KshDeployNas` (après build Release) copie l'exe sur le NAS de Calvin `K:\Backup\Logiciel KSHPC\KSHPLAYER\…`.
  - Cette étape est **ignorée automatiquement** si `K:\Backup` n'existe pas, donc rien ne casse chez quelqu'un d'autre.
- Piège connu : ne jamais nommer une propriété MSBuild `AppConfig` (réservée).

### Aperçu à la barre Espace (`Apercu/`, Windows, 1.2)
- Idée reprise d'Orbit Look (l'appli Quick Look de Romain), **sans rien changer à Orbit Look** : si un processus `OrbitLook` tourne, KSHPLAYER se met en retrait et lui laisse la barre Espace.
- `KSHPLAYER-Apercu.exe` (ou `KSHPLAYERPRO-Apercu.exe`) est lancé avec Windows (`--demarrage`), une icône discrète près de l'horloge (menu sombre : pause, lancer avec Windows, ouvrir une vidéo…, quitter). Un seul aperçu pour tout le PC (mutex `Local\KSHPLAYER-Apercu`, la PRO remplace la normale).
- `KeyboardHook.cs` : crochet `WH_KEYBOARD_LL` sur son propre fil (il ne fait que ça, sinon Windows le retire). Premier appui sur Espace → `ApercuApp.OnSpace()` décide en quelques µs : pas de touche de modification, pas de nom en cours de frappe (< 1 s), Explorateur (`CabinetWClass`) ou Bureau (`Progman`/`WorkerW`) au premier plan, focus dans la liste des fichiers (`DirectUIHWND` / `SysListView32` dont le parent est `SHELLDLL_DefView` ; renommage, barre d'adresse, recherche → non). Espace maintenue : même décision jusqu'au relâchement.
- `ShellSelection.cs` : sélection lue en COM (`IShellWindows` → `IShellBrowser` → `IShellView`/`IFolderView` → `IShellItemArray`), onglet actif de Windows 11 = premier `ShellTabWindowClass`, Bureau via `FindWindowSW(SWC_DESKTOP)`. Si ce n'est pas une vidéo lisible (`Product.PreviewExtensions`), la touche Espace est renvoyée telle quelle (`SendInput` marqué `KSHP`).
- `PreviewPlayer.cs` : libmpv (chaînes UTF-8 faites à la main pour .NET Framework), réglages « Équilibré », fenêtre centrée sur l'écran de l'Explorateur, `kshosc.lua` en mode aperçu ; mise au premier plan par `AttachThreadInput`. Quand on passe d'une vidéo à l'autre, l'Explorateur sélectionne la même (`IShellView.SelectItem`). Espace sur la vidéo déjà ouverte la referme (comme Quick Look).
- Même règle que le lecteur : le moteur de l'aperçu est détruit sur un fil à part (`apercu-arret`). Détruit sur le fil COM de l'aperçu, il cassait la lecture de la sélection de l'Explorateur dès le premier aperçu refermé (Espace ne marchait qu'une fois, `RPC_E_WRONG_THREAD` dans `apercu.log`, constaté chez Romain le 2026-10-15). Le fil de l'aperçu prend aussi une inscription COM de réserve (`CoInitializeEx`) par sécurité.
- Journal : `%LOCALAPPDATA%\KSHPLAYER\apercu.log` (plafonné à 1 Mo) et `apercu-mpv.log`. Diagnostic : variable d'environnement `KSHPLAYER_APERCU_MPV="hwdec=no,…"` (options mpv en plus).
- Pour l'activer après coup : relancer l'installateur et cocher « Aperçu des vidéos avec la barre Espace ».

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
- Les applis lisent `version.json`.
  - **Windows** (1.2) : pendant la lecture → téléchargement en arrière-plan → message dans la vidéo → `Setup /update` à la fermeture du lecteur. (1.1.x : bandeau « Mettre à jour » de l'accueil.)
  - **Mac** : bandeau « Télécharger » qui ouvre le .dmg.
- Côté Windows, l'appli recopie aussi les 4 fichiers sur le NAS de Calvin s'il est présent (`Updater.MirrorToNasAsync`).
- **Pour publier une version** : augmenter `VERSION`, écrire `NOTES.txt` (une ligne), puis pousser sur `main`. Ça prend environ 6 min.
  - Si GitHub n'a pas de Mac libre (arrivé le 2026-11-04 : job « mac » annulé), Windows est publié quand même : les `.dmg` précédents restent en ligne et `version.json` omet les clés `mac`/`mac_pro`, donc les Mac ne voient pas de mise à jour. Republier plus tard (`gh run rerun` ou nouveau push) pour sortir les `.dmg`.
  - Pour un push qui ne doit rien publier, mettre `[skip ci]` dans le message de commit.
- ⚠️ L'URL de mise à jour est **codée en dur** sur `KSHMIRE1/KSHPLAYER` (`Updater.cs`, `macOS/Sources/Updater.swift`, `.github/version_json.py`).
  - Un fork publie ses propres fichiers, mais les applis installées continuent de suivre le dépôt de Calvin.

---

## 2. Compiler

### Windows (.NET SDK récent, ex. 10 ; Visual Studio facultatif)
```powershell
git clone https://github.com/KSHMIRE1/KSHPLAYER.git
cd KSHPLAYER
# lecteur seul : dotnet build KSHPLAYER.csproj -p:Configuration=Debug -p:Platform=x64 (ou F5 dans VS, Debug / DebugPro, x64)
# puis : KSHPLAYER.exe "C:\chemin\video.mkv" 
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
- ~~`osd-bar` défini deux fois~~ : corrigé en 1.2 (une seule fois, `yes`).
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

## 3 bis. Version 1.2.0 (préparée le 2026-10-15 par Romain avec Claude, pas encore publiée)

**Nouveautés** : **plus d'écran d'accueil** (la vidéo s'ouvre tout de suite, la fermer ferme KSHPLAYER ; lecteur en .NET Framework 4.8, installateur ≈35 Mo au lieu de ≈83), aperçu à la barre Espace (`Apercu/`), vidéo précédente / suivante, fiche « lecture impossible », mises à jour installées à la fermeture, écran « Mise à jour » de l'installateur, vraie désinstallation, moteur arrêté hors du fil principal (cause probable de la croix qui ne fermait pas l'appli en 1.1.1).

**Vérifié dans un conteneur Linux** (pas de Windows) :
- `kshosc.lua` dans un vrai mpv (0.37) avec captures : navigation ⏮ ⏭ et tri naturel, fiches d'erreur (abîmé, vide, .braw, .txt, introuvable), fin de vidéo → fermeture, mode aperçu (Espace, flèches, Entrée → `--start=`), barre compacte normale et PRO, message passager.
- Le lecteur 1.2 compilé (Roslyn + assemblys de référence .NET 4.8 de Mono) et lancé sous Mono contre un vrai libmpv : lecture jusqu'à la fin puis fermeture (code 0), fiche d'erreur qui reste ouverte, séquences PRO (dossier, image numérotée, image seule), message de mise à jour envoyé par le lecteur. `Updater` : lecture du vrai `version.json`, reprise d'un téléchargement, refus d'un installateur de la mauvaise version, réutilisation.
- Installateur et `Apercu` compilés, fenêtres affichées sous Mono (installation, mise à jour 1.1.1 → 1.2.0, désinstallation), `PreviewPlayer` ouvert / refermé plusieurs fois de suite contre un vrai libmpv.
- Relectures indépendantes (COM, structures Win32 ; puis lecteur sans accueil, mise à jour, MSBuild) : leurs remarques sont corrigées.

**Testé chez Romain** (installateur de test) : l'aperçu Espace ne marchait qu'une fois (corrigé : moteur arrêté hors du fil COM).

**À tester sur un vrai Windows** :
- double-clic / « Ouvrir avec » sur une vidéo, fenêtre « Ouvrir » (menu Démarrer), fermeture, `--start` depuis l'aperçu ;
- l'aperçu Espace : Explorateur (Windows 10 et 11, avec onglets), Bureau, renommage en cours, vidéo sur un NAS, mise au premier plan, Orbit Look présent / absent, plusieurs aperçus de suite ;
- « Afficher dans le dossier » sur un chemin avec espaces ;
- la mise à jour à la fermeture (publier une 1.2.1 de test) et la mise à jour depuis une 1.1.1 ;
- la désinstallation depuis Paramètres › Applications ;
- la CI (`release.yml`) avec le nouveau `KSHPLAYER.csproj` (NuGet était bloqué dans le conteneur, `dotnet msbuild` n'a pas pu tourner).

**Pour publier** : `VERSION` → `1.2.0` (`NOTES.txt` est prêt), puis pousser sur `main`.

---

## 4. Règles de travail avec Calvin
- Il ne faut jamais saisir ses mots de passe. Tout téléchargement sur son PC se fait avec son accord explicite, en précisant le nom, la source et la taille.
- Dossier du projet sur son PC : `C:\Users\KSHMIRE\source\repos\KSHPLAYER` (**pas un clone git** : on y copie les fichiers modifiés).
  - Le dossier `.github` y est protégé : il n'existe que sur GitHub.
- NAS : `K:\Backup\Logiciel KSHPC` (lecteur réseau `\\KSHNAS`).
- Ses autres projets (KSHFLIX, serveur Emby) partagent le même design ; garder la cohérence visuelle.
