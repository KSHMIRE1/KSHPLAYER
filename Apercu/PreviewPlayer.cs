using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using KSHPLAYER.Setup;

namespace KSHPLAYER.Apercu
{
    /// <summary>
    /// Fenêtre d'aperçu : le moteur de KSHPLAYER (libmpv-2.dll, installé à côté de cet exe) avec sa barre
    /// kshosc.lua en mode « aperçu » (Espace ou Échap referme, ← → vidéo voisine, Entrée ouvre en grand).
    /// </summary>
    internal sealed class PreviewPlayer : IDisposable
    {
        private IntPtr _ctx;
        private readonly Thread _events;
        private Thread _closer;
        private volatile bool _shutdown, _disposing;

        /// <summary>La fenêtre de l'aperçu existe (HWND) : on peut lui donner le premier plan.</summary>
        public event Action<IntPtr> WindowReady;
        /// <summary>L'aperçu montre un autre fichier (flèches ← →).</summary>
        public event Action<string> PathChanged;
        /// <summary>L'aperçu s'est refermé (Espace, Échap, la croix…).</summary>
        public event Action Closed;

        public string CurrentPath { get; private set; }
        public bool Alive => _ctx != IntPtr.Zero && !_shutdown;

        public PreviewPlayer(string appDir, int screen)
        {
            _ctx = mpv_create();
            if (_ctx == IntPtr.Zero) throw new InvalidOperationException("Moteur mpv indisponible.");

            string mpvDir = Path.Combine(appDir, "mpv");
            Opt("config", "yes");
            Opt("config-dir", mpvDir);
            try { Directory.CreateDirectory(Installer.DataDir); } catch { }
            Opt("log-file", Path.Combine(Installer.DataDir, "apercu-mpv.log"));
            Opt("load-scripts", "no");
            Opt("scripts", Path.Combine(mpvDir, "scripts", "kshosc.lua"));
            var so = new StringBuilder("kshosc-apercu=yes,kshosc-appexe=" + Quote(Path.Combine(appDir, Product.Exe)));
            if (Product.IsPro)
                so.Append(",kshosc-pro=yes,kshosc-lutdir=").Append(Quote(ProFolder(Environment.SpecialFolder.MyDocuments, "LUTs")))
                  .Append(",kshosc-shotdir=").Append(Quote(ProFolder(Environment.SpecialFolder.MyPictures, null)));
            Opt("script-opts", so.ToString());

            // Fenêtre : comme le lecteur (sans bord, au format du film), centrée sur l'écran de l'Explorateur
            Opt("title", "Aperçu · ${media-title}");
            Opt("border", "no");
            Opt("window-dragging", "yes");
            Opt("keepaspect-window", "yes");
            Opt("autofit-larger", "60%x60%");
            Opt("autofit-smaller", "35%x35%");
            Opt("geometry", "50%:50%");
            if (screen >= 0) Opt("screen", screen.ToString(CultureInfo.InvariantCulture));
            Opt("force-window", "immediate");
            Opt("keep-open", "yes");
            Opt("idle", "yes");

            // Interface : la barre KSHPLAYER (kshosc.lua), rien d'autre
            Opt("osc", "no");
            Opt("osd-bar", "no");
            Opt("input-default-bindings", "yes");
            Opt("input-vo-keyboard", "yes");
            Opt("osd-font", "Segoe UI");

            // Rendu : réglages « Équilibré » du lecteur (un aperçu doit s'ouvrir vite, sur tous les PC)
            Opt("vo", "gpu-next");
            Opt("gpu-api", "d3d11");
            Opt("hwdec", "auto-safe");
            Opt("target-colorspace-hint", "yes");
            Opt("tone-mapping", "auto");
            Opt("audio-channels", "stereo");
            Opt("volume-max", "150");
            Opt("sub-auto", "fuzzy");
            Opt("slang", "fr,fre,fra,en,eng");
            Opt("alang", "fr,fre,fra,en,eng");
            Opt("sub-font", "Segoe UI Semibold");
            if (Product.IsPro) Opt("hr-seek", "yes");

            // Diagnostic : options mpv en plus, ex. KSHPLAYER_APERCU_MPV="hwdec=no,gpu-api=vulkan"
            var extra = Environment.GetEnvironmentVariable("KSHPLAYER_APERCU_MPV");
            if (!string.IsNullOrWhiteSpace(extra))
                foreach (var kv in extra.Split(','))
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0) Opt(kv.Substring(0, eq).Trim(), kv.Substring(eq + 1).Trim());
                }

            int r = mpv_initialize(_ctx);
            if (r < 0)
            {
                IntPtr failed = _ctx;
                _ctx = IntPtr.Zero;
                _closer = DestroyOnOwnThread(failed, null);
                throw new InvalidOperationException("Le moteur mpv n'a pas démarré (" + r + ").");
            }
            mpv_observe_property(_ctx, ObsWindow, U("window-id"), MPV_FORMAT_INT64);
            mpv_observe_property(_ctx, ObsPath, U("path"), MPV_FORMAT_STRING);

            IntPtr handle = _ctx;   // copie : Dispose remet _ctx à zéro sans attendre le fil d'événements
            _events = new Thread(() => EventLoop(handle)) { IsBackground = true, Name = "apercu-mpv" };
            _events.Start();
        }

        /// <summary>Charge le moteur (gros fichier) en avance : le premier aperçu s'ouvre ensuite plus vite.</summary>
        public static void Preload()
        {
            try { mpv_client_api_version(); }
            catch (Exception ex) { Log.Write("Moteur mpv introuvable à côté de l'aperçu", ex); }
        }

        public void Load(string path)
        {
            CurrentPath = path;
            Command("loadfile", path, "replace");
        }

        public void Close() => Command("quit");

        private static string ProFolder(Environment.SpecialFolder root, string sub)
        {
            var dir = Path.Combine(Environment.GetFolderPath(root), "KSHPLAYER PRO");
            if (sub != null) dir = Path.Combine(dir, sub);
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        // Valeur protégée pour les listes d'options mpv (%longueur%texte)
        private static string Quote(string v) => "%" + Encoding.UTF8.GetByteCount(v) + "%" + v;

        private void EventLoop(IntPtr ctx)
        {
            while (!_disposing)
            {
                IntPtr evp = mpv_wait_event(ctx, -1);
                if (evp == IntPtr.Zero || _disposing) continue;
                var ev = (MpvEvent)Marshal.PtrToStructure(evp, typeof(MpvEvent));
                if (ev.EventId == MPV_EVENT_SHUTDOWN)
                {
                    _shutdown = true;
                    try { Closed?.Invoke(); } catch (Exception ex) { Log.Write("Fermeture de l'aperçu", ex); }
                    return;
                }
                if (ev.EventId != MPV_EVENT_PROPERTY_CHANGE || ev.Data == IntPtr.Zero) continue;
                var prop = (MpvEventProperty)Marshal.PtrToStructure(ev.Data, typeof(MpvEventProperty));
                if (prop.Data == IntPtr.Zero) continue;
                try
                {
                    if (ev.ReplyUserdata == ObsWindow && prop.Format == MPV_FORMAT_INT64)
                    {
                        long id = Marshal.ReadInt64(prop.Data);
                        if (id != 0) WindowReady?.Invoke(new IntPtr(id));
                    }
                    else if (ev.ReplyUserdata == ObsPath && prop.Format == MPV_FORMAT_STRING)
                    {
                        var p = Utf8(Marshal.ReadIntPtr(prop.Data));
                        if (!string.IsNullOrEmpty(p))
                        {
                            CurrentPath = p;
                            PathChanged?.Invoke(p);
                        }
                    }
                }
                catch (Exception ex) { Log.Write("Événement de l'aperçu", ex); }
            }
        }

        /// <summary>
        /// Referme l'aperçu et arrête le moteur sur un fil à part, sans attendre. Jamais sur le fil de l'aperçu :
        /// en se détruisant, libmpv appelle CoUninitialize() sur le fil qui le détruit (sortie audio WASAPI).
        /// Sur le fil COM de l'aperçu, ça cassait sa liaison avec l'Explorateur : Espace ne marchait qu'une fois
        /// (« RPC_E_WRONG_THREAD » au deuxième aperçu).
        /// </summary>
        public void Dispose()
        {
            IntPtr ctx = _ctx;
            if (ctx == IntPtr.Zero) return;
            if (!_shutdown) Command("quit");
            _ctx = IntPtr.Zero;         // plus aucune commande vers ce moteur
            _closer = DestroyOnOwnThread(ctx, _events);
        }

        /// <summary>Attend que le moteur soit complètement arrêté (fermeture de l'aperçu résident).</summary>
        public void WaitClosed(int milliseconds) => _closer?.Join(milliseconds);

        private Thread DestroyOnOwnThread(IntPtr ctx, Thread events)
        {
            var t = new Thread(() =>
            {
                try
                {
                    if (events != null && !events.Join(3000))
                    {
                        _disposing = true;      // le moteur ne répond plus : on réveille le fil d'événements pour qu'il s'arrête
                        mpv_wakeup(ctx);
                        events.Join(1000);
                    }
                    mpv_terminate_destroy(ctx);
                }
                catch (Exception ex) { Log.Write("Arrêt du moteur de l'aperçu", ex); }
            }) { IsBackground = true, Name = "apercu-arret" };
            t.Start();
            return t;
        }

        // ---------- libmpv (API C) ; chaînes en UTF-8 faites à la main (.NET Framework) ----------
        private const string Lib = "libmpv-2.dll";
        private const int MPV_FORMAT_STRING = 1, MPV_FORMAT_INT64 = 4;
        private const int MPV_EVENT_SHUTDOWN = 1, MPV_EVENT_PROPERTY_CHANGE = 22;
        private const ulong ObsWindow = 1, ObsPath = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEvent { public int EventId; public int Error; public ulong ReplyUserdata; public IntPtr Data; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEventProperty { public IntPtr Name; public int Format; public IntPtr Data; }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern uint mpv_client_api_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_wakeup(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, IntPtr[] args);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_observe_property(IntPtr ctx, ulong reply, byte[] name, int format);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        private static byte[] U(string s) => Encoding.UTF8.GetBytes(s + "\0");

        private static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            var buf = new byte[len];
            Marshal.Copy(p, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }

        private void Opt(string name, string value) => mpv_set_option_string(_ctx, U(name), U(value));

        private void Command(params string[] args)
        {
            if (_ctx == IntPtr.Zero || _shutdown) return;
            var ptrs = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    var b = U(args[i]);
                    ptrs[i] = Marshal.AllocHGlobal(b.Length);
                    Marshal.Copy(b, 0, ptrs[i], b.Length);
                }
                mpv_command(_ctx, ptrs);
            }
            finally
            {
                foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }
    }
}
