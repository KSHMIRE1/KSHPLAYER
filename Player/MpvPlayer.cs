using System;
using System.Runtime.InteropServices;
using System.Threading;
using static KSHPLAYER.Player.MpvNative;

namespace KSHPLAYER.Player;

/// <summary>
/// Le moteur de KSHPLAYER : une instance libmpv qui dessine dans une fenêtre Win32 donnée.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private IntPtr _ctx;
    private Thread? _eventThread;
    private volatile bool _disposed;

    /// <summary>mpv demande (ou quitte) le plein écran, ex. double-clic ou touche F.</summary>
    public event Action<bool>? FullscreenChanged;
    /// <summary>Titre du média en cours.</summary>
    public event Action<string>? TitleChanged;
    /// <summary>Un fichier vient d'être chargé.</summary>
    public event Action? FileLoaded;
    /// <summary>mpv s'arrête (ex. touche Q).</summary>
    public event Action? Shutdown;

    private const ulong ObsFullscreen = 1;
    private const ulong ObsTitle = 2;

    public MpvPlayer(string configDir, HardwareProfile hw)
    {
        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("Impossible de créer l'instance mpv.");

        // --- Dossier mpv livré avec l'appli : barre de contrôle, icônes, raccourcis ---
        Opt("config", "yes");
        Opt("config-dir", configDir);
        Opt("log-file", AppLog.MpvLogPath);   // journal mpv : %LOCALAPPDATA%\KSHPLAYER\mpv.log
        Opt("load-scripts", "no");            // on ne charge que notre barre de contrôle
        Opt("scripts", System.IO.Path.Combine(configDir, "scripts", "kshosc.lua"));

        // --- Fenêtre : uniquement la vidéo, sans bord ni barre de titre ---
        Opt("title", "KSHPLAYER · ${media-title}");
        Opt("border", "no");                  // pas de cadre Windows
        Opt("window-dragging", "yes");        // on déplace la fenêtre en glissant la vidéo
        Opt("keepaspect-window", "yes");      // la fenêtre épouse le format du film : pas de bandes noires
        Opt("autofit-larger", "60%x60%");     // taille raisonnable quel que soit l'écran
        Opt("geometry", "50%:50%");           // centrée
        Opt("force-window", "immediate");
        Opt("keep-open", "no");
        Opt("idle", "once");                  // attend le fichier, puis fin du film -> retour à l'accueil

        // --- Interface : barre de contrôle maison KSHPLAYER (kshosc.lua) ---
        Opt("osc", "no");
        Opt("osd-bar", "no");
        Opt("input-default-bindings", "yes");
        Opt("input-vo-keyboard", "yes");
        Opt("osd-font", "Segoe UI");
        Opt("osd-bar", "yes");

        // --- Rendu vidéo (RTX 3080 + écran HDR) ---
        Opt("vo", "gpu-next");                // moteur libplacebo, le meilleur de mpv
        Opt("gpu-api", "d3d11");
        Opt("hwdec", "auto-safe");            // décodage par la carte graphique (NVIDIA, AMD, Intel)
        Opt("target-colorspace-hint", "yes"); // envoie le signal HDR à l'écran pour les contenus HDR
        Opt("tone-mapping", "auto");

        // --- Réglages adaptés à la carte graphique détectée ---
        switch (hw.Tier)
        {
            case QualityTier.Max:      // grosses cartes (ex. RTX 3070+, RX 6800+)
                Opt("profile", "high-quality");          // meilleurs algorithmes d'agrandissement
                Opt("video-sync", "display-resample");   // fluidité parfaite calée sur l'écran
                Opt("deband", "yes");                    // supprime les dégradés en escalier
                break;
            case QualityTier.Balanced: // cartes dédiées classiques
                Opt("video-sync", "display-resample");
                break;
            case QualityTier.Light:    // puces intégrées / petits portables
                Opt("profile", "fast");                  // rendu léger, priorité à la fluidité
                Opt("video-sync", "audio");
                Opt("hdr-compute-peak", "no");
                break;
        }

        // --- Audio (haut-parleurs de l'écran) ---
        Opt("audio-channels", "stereo");      // downmix propre du 5.1/7.1/Atmos vers la stéréo
        Opt("volume-max", "150");

        // --- Sous-titres ---
        Opt("sub-auto", "fuzzy");             // charge les .srt/.ass à côté de la vidéo
        Opt("slang", "fr,fre,fra,en,eng");
        Opt("alang", "fr,fre,fra,en,eng");
        Opt("sub-font", "Segoe UI Semibold");

        // --- Édition PRO : outils monteurs dans la barre (kshosc.lua) ---
        if (Edition.IsPro)
        {
            Opt("script-opts", "kshosc-pro=yes," +
                $"kshosc-lutdir={Quote(Edition.LutFolder)}," +
                $"kshosc-shotdir={Quote(Edition.CaptureFolder)}");
            Opt("hr-seek", "yes");          // sauts toujours à l'image près
            Opt("keep-open", "yes");        // reste sur la dernière image en fin de fichier
            Opt("mf-fps", "24");            // cadence par défaut des séquences d'images
        }

        Check(mpv_initialize(_ctx), "initialisation");

        mpv_observe_property(_ctx, ObsFullscreen, "fullscreen", MPV_FORMAT_FLAG);
        mpv_observe_property(_ctx, ObsTitle, "media-title", MPV_FORMAT_STRING);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    public void Load(string path)
    {
        // Le titre affiché = le nom du fichier tel que tu l'as nommé, pas le titre caché dans le MKV
        SetProperty("force-media-title", System.IO.Path.GetFileNameWithoutExtension(path));
        Command("loadfile", path, "replace");
    }

    /// <summary>Séquence d'images (EXR, DPX, TIFF, PNG…) : tout un dossier lu comme une vidéo.</summary>
    public void LoadSequence(string folder, string extension)
    {
        SetProperty("force-media-title", System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')) + "  ·  séquence " + extension.ToUpperInvariant());
        Command("loadfile", "mf://" + System.IO.Path.Combine(folder, "*." + extension), "replace");
    }

    // Valeur protégée pour les listes d'options mpv (%longueur%texte)
    private static string Quote(string v) => $"%{System.Text.Encoding.UTF8.GetByteCount(v)}%{v}";

    public void SetProperty(string name, string value) => mpv_set_property_string(_ctx, name, value);

    public string? GetProperty(string name)
    {
        var ptr = mpv_get_property_string(_ctx, name);
        if (ptr == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUTF8(ptr);
        mpv_free(ptr);
        return s;
    }

    public void Command(params string[] args)
    {
        if (_ctx == IntPtr.Zero) return;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;

            var arr = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
            try
            {
                Marshal.Copy(ptrs, 0, arr, ptrs.Length);
                mpv_command(_ctx, arr);
            }
            finally { Marshal.FreeHGlobal(arr); }
        }
        finally
        {
            foreach (var p in ptrs)
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    private void EventLoop()
    {
        while (!_disposed)
        {
            var evPtr = mpv_wait_event(_ctx, -1);
            if (evPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);

            switch (ev.EventId)
            {
                case MPV_EVENT_SHUTDOWN:
                    if (!_disposed) Shutdown?.Invoke();
                    return;

                case MPV_EVENT_FILE_LOADED:
                    FileLoaded?.Invoke();
                    break;

                case MPV_EVENT_PROPERTY_CHANGE:
                    var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
                    if (prop.Data == IntPtr.Zero) break;
                    if (ev.ReplyUserdata == ObsFullscreen && prop.Format == MPV_FORMAT_FLAG)
                        FullscreenChanged?.Invoke(Marshal.ReadInt32(prop.Data) != 0);
                    else if (ev.ReplyUserdata == ObsTitle && prop.Format == MPV_FORMAT_STRING)
                        TitleChanged?.Invoke(Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(prop.Data)) ?? "");
                    break;
            }
        }
    }

    private void Opt(string name, string value) => mpv_set_option_string(_ctx, name, value);

    private static void Check(int code, string what)
    {
        if (code < 0)
            throw new InvalidOperationException(
                $"mpv ({what}) : {Marshal.PtrToStringUTF8(mpv_error_string(code))}");
    }

    public void Dispose()
    {
        if (_disposed || _ctx == IntPtr.Zero) return;
        _disposed = true;
        mpv_wakeup(_ctx);
        _eventThread?.Join(1000);
        mpv_terminate_destroy(_ctx);
        _ctx = IntPtr.Zero;
    }
}
