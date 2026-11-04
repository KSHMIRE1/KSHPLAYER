using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static KSHPLAYER.Player.MpvNative;

namespace KSHPLAYER.Player;

/// <summary>
/// Le moteur de KSHPLAYER : une instance libmpv qui affiche la vidéo dans sa propre fenêtre sans bord,
/// avec la barre de contrôle kshosc.lua. Depuis 1.2 il n'y a plus d'écran d'accueil : quand mpv s'arrête
/// (croix, Échap, fin du film), KSHPLAYER se ferme.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private IntPtr _ctx;
    private readonly object _gate = new object();   // commandes (fil principal, mise à jour) contre arrêt du moteur
    private readonly Thread _eventThread;
    private Thread? _closer;
    private volatile bool _disposed;
    private bool _iconSet;

    /// <summary>mpv s'est arrêté : vidéo refermée ou fin du film.</summary>
    public event Action? Shutdown;

    private const ulong ObsWindow = 1;

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
        Opt("scripts", Path.Combine(configDir, "scripts", "kshosc.lua"));

        // --- Fenêtre : uniquement la vidéo, sans bord ni barre de titre ---
        Opt("title", Edition.Name + " · ${media-title}");
        Opt("border", "no");                  // pas de cadre Windows
        Opt("window-dragging", "yes");        // on déplace la fenêtre en glissant la vidéo
        Opt("keepaspect-window", "yes");      // la fenêtre épouse le format du film : pas de bandes noires
        Opt("autofit-larger", "60%x60%");     // taille raisonnable quel que soit l'écran
        Opt("geometry", "50%:50%");           // centrée
        Opt("force-window", "immediate");
        Opt("keep-open", "no");
        Opt("idle", "once");                  // kshosc.lua passe à idle=yes et ferme lui-même en fin de film

        // --- Interface : barre de contrôle maison KSHPLAYER (kshosc.lua) ---
        Opt("osc", "no");
        Opt("osd-bar", "yes");
        Opt("input-default-bindings", "yes");
        Opt("input-vo-keyboard", "yes");
        Opt("osd-font", "Segoe UI");

        // --- Rendu vidéo (cartes graphiques NVIDIA / AMD / Intel, écrans HDR) ---
        Opt("vo", "gpu-next");                // moteur libplacebo, le meilleur de mpv
        Opt("gpu-api", "d3d11");
        Opt("hwdec", "auto-safe");            // décodage par la carte graphique
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

        // Diagnostic : options mpv en plus, ex. KSHPLAYER_MPV="hwdec=no,gpu-api=vulkan"
        var extra = Environment.GetEnvironmentVariable("KSHPLAYER_MPV");
        if (!string.IsNullOrWhiteSpace(extra))
            foreach (var kv in extra!.Split(','))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0) Opt(kv.Substring(0, eq).Trim(), kv.Substring(eq + 1).Trim());
            }

        Check(mpv_initialize(_ctx), "initialisation");
        mpv_observe_property(_ctx, ObsWindow, "window-id", MPV_FORMAT_INT64);

        IntPtr handle = _ctx;   // copie : Dispose remet _ctx à zéro sans attendre le fil d'événements
        _eventThread = new Thread(() => EventLoop(handle)) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    /// <param name="start">Position de départ en secondes (« ouvrir en grand » depuis l'aperçu Espace).</param>
    public void Load(string path, double? start = null)
    {
        // Le titre affiché = le nom du fichier tel que tu l'as nommé, pas le titre caché dans le MKV
        SetProperty("force-media-title", Path.GetFileNameWithoutExtension(path));
        if (start is > 0.5)
            Command("loadfile", path, "replace", "-1",
                "start=" + start.Value.ToString("0.###", CultureInfo.InvariantCulture));
        else
            Command("loadfile", path, "replace");
    }

    /// <summary>
    /// Séquence d'images (EXR, DPX, TIFF, PNG…) lue comme une vidéo : tout le dossier (prefix vide)
    /// ou seulement les images qui commencent par <paramref name="prefix"/> (ex. « plan_010_ » pour plan_010_0001.exr).
    /// </summary>
    public void LoadSequence(string folder, string extension, string prefix = "")
    {
        SetProperty("force-media-title", Path.GetFileName(folder.TrimEnd('\\', '/')) +
            (prefix.Length > 0 ? " · " + prefix.TrimEnd('_', '-', '.', ' ') : "") + "  ·  séquence " + extension.ToUpperInvariant());
        Command("loadfile", "mf://" + Path.Combine(folder, prefix + "*." + extension), "replace");
    }

    /// <summary>Petit message KSHFLIX dans la vidéo, sous la rangée du haut (kshosc.lua).</summary>
    public void Toast(string text, int seconds) =>
        Command("script-message-to", "kshosc", "ksh-toast", text, seconds.ToString(CultureInfo.InvariantCulture));

    // Valeur protégée pour les listes d'options mpv (%longueur%texte)
    private static string Quote(string v) => $"%{Encoding.UTF8.GetByteCount(v)}%{v}";

    public void SetProperty(string name, string value)
    {
        lock (_gate)
        {
            if (_ctx != IntPtr.Zero) mpv_set_property_string(_ctx, name, value);
        }
    }

    public void Command(params string[] args)
    {
        lock (_gate) CommandLocked(args);
    }

    private void CommandLocked(string[] args)
    {
        if (_ctx == IntPtr.Zero) return;
        var ptrs = new IntPtr[args.Length + 1];   // la dernière case reste à zéro : fin de liste pour mpv
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(args[i] + "\0");
                ptrs[i] = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, ptrs[i], bytes.Length);
            }
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
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    private void EventLoop(IntPtr ctx)
    {
        while (!_disposed)
        {
            var evPtr = mpv_wait_event(ctx, -1);
            if (evPtr == IntPtr.Zero) continue;
            var ev = (MpvEvent)Marshal.PtrToStructure(evPtr, typeof(MpvEvent));

            if (ev.EventId == MPV_EVENT_SHUTDOWN)
            {
                if (!_disposed)
                {
                    try { Shutdown?.Invoke(); } catch (Exception ex) { AppLog.Write("Fermeture du lecteur", ex); }
                }
                return;
            }
            if (ev.EventId != MPV_EVENT_PROPERTY_CHANGE || ev.Data == IntPtr.Zero) continue;
            var prop = (MpvEventProperty)Marshal.PtrToStructure(ev.Data, typeof(MpvEventProperty));
            if (ev.ReplyUserdata == ObsWindow && prop.Format == MPV_FORMAT_INT64 && prop.Data != IntPtr.Zero)
            {
                long id = Marshal.ReadInt64(prop.Data);
                if (id != 0 && !_iconSet)
                {
                    _iconSet = true;
                    WindowIcon.Apply(new IntPtr(id));   // l'icône KSHPLAYER dans la barre des tâches (pas celle de mpv)
                }
            }
        }
    }

    private void Opt(string name, string value) => mpv_set_option_string(_ctx, name, value);

    private static void Check(int code, string what)
    {
        if (code < 0)
            throw new InvalidOperationException($"mpv ({what}) : {Utf8(mpv_error_string(code))}");
    }

    private static string Utf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int len = 0;
        while (Marshal.ReadByte(p, len) != 0) len++;
        var buf = new byte[len];
        Marshal.Copy(p, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }

    /// <summary>
    /// Arrête le moteur sur un fil à part, sans attendre. Jamais sur le fil principal : en se détruisant,
    /// libmpv appelle CoUninitialize() sur le fil qui le détruit (sortie audio WASAPI). Avec l'ancien accueil
    /// WinUI, chaque vidéo refermée retirait ainsi une inscription COM/WinRT à l'interface (croix qui ne
    /// répondait plus) ; l'aperçu Espace, lui, perdait l'Explorateur.
    /// </summary>
    public void Dispose()
    {
        IntPtr ctx;
        lock (_gate)
        {
            if (_disposed || _ctx == IntPtr.Zero) return;
            _disposed = true;
            ctx = _ctx;
            _ctx = IntPtr.Zero;     // plus aucune commande vers ce moteur (le message de mise à jour arrive d'un autre fil)
        }
        var events = _eventThread;
        _closer = new Thread(() =>
        {
            try
            {
                mpv_wakeup(ctx);
                events.Join(1000);
                mpv_terminate_destroy(ctx);
            }
            catch (Exception ex) { AppLog.Write("Arrêt du moteur", ex); }
        }) { IsBackground = true, Name = "mpv-arret" };
        _closer.Start();
    }

    /// <summary>Attend que le moteur soit complètement arrêté.</summary>
    public void WaitClosed(int milliseconds) => _closer?.Join(milliseconds);
}

/// <summary>Met l'icône de KSHPLAYER sur la fenêtre de mpv (sinon Windows affiche celle de mpv).</summary>
internal static class WindowIcon
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WM_SETICON = 0x0080;

    public static void Apply(IntPtr hwnd)
    {
        try
        {
            var exe = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(exe) || ExtractIconEx(exe!, 0, out var big, out var small, 1) == 0) return;
            if (big != IntPtr.Zero) PostMessage(hwnd, WM_SETICON, (IntPtr)1, big);     // ICON_BIG
            if (small != IntPtr.Zero) PostMessage(hwnd, WM_SETICON, IntPtr.Zero, small); // ICON_SMALL
        }
        catch { /* une icône manquante ne doit jamais gêner la lecture */ }
    }
}
