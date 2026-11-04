using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using KSHPLAYER.Setup;
using static KSHPLAYER.Apercu.NativeMethods;

namespace KSHPLAYER.Apercu
{
    /// <summary>
    /// Aperçu résident (lancé avec Windows) : une icône discrète près de l'horloge, et la barre Espace
    /// qui ouvre la vidéo sélectionnée dans l'Explorateur ou sur le Bureau. Idée reprise d'Orbit Look ;
    /// si Orbit Look tourne sur le même PC, KSHPLAYER se met en retrait et lui laisse la barre Espace.
    /// </summary>
    internal sealed class ApercuApp : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly PreviewWorker _worker = new PreviewWorker();
        private readonly KeyboardHook _hook;
        private readonly ToolStripMenuItem _miState, _miActive, _miStartup;
        private volatile bool _paused;

        public ApercuApp(bool welcome)
        {
            _hook = new KeyboardHook(OnSpace);
            _hook.Start();

            _miState = new ToolStripMenuItem("") { Enabled = false };
            _miActive = new ToolStripMenuItem("Aperçu avec la barre Espace", null, (s, e) => _paused = !_paused);
            _miStartup = new ToolStripMenuItem("Lancer avec Windows", null,
                (s, e) => Installer.SetApercuAtStartup(!Installer.ApercuAtStartup()));
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
            menu.Items.AddRange(new ToolStripItem[]
            {
                _miState, new ToolStripSeparator(), _miActive, _miStartup, new ToolStripSeparator(),
                new ToolStripMenuItem("Ouvrir une vidéo…", null, (s, e) => OpenApp()),
                new ToolStripMenuItem("Quitter l'aperçu", null, (s, e) => ExitThread()),
            });
            menu.Opening += (s, e) =>
            {
                _miActive.Checked = !_paused;
                _miStartup.Checked = Installer.ApercuAtStartup();
                _miState.Text = _hook.OtherPreviewRunning ? "En retrait : Orbit Look s'occupe de la barre Espace"
                              : _paused ? "En pause"
                              : "Sélectionne une vidéo, puis Espace";
            };

            _tray = new NotifyIcon
            {
                Icon = TrayIcon(),
                Text = Product.Name + " · aperçu (Espace)",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.DoubleClick += (s, e) => OpenApp();
            if (welcome)
                _tray.ShowBalloonTip(6000, "Aperçu " + Product.Name + " activé",
                    "Dans l'Explorateur ou sur le Bureau, sélectionne une vidéo et appuie sur Espace.", ToolTipIcon.None);
            Log.Write("Aperçu démarré : " + Product.Name + " " + Product.Version);
        }

        /// <summary>
        /// Appelé par le crochet clavier au premier appui sur Espace (il faut répondre en quelques microsecondes) :
        /// true = la touche est pour l'aperçu. Le travail réel (lire la sélection, ouvrir la vidéo) part
        /// sur le fil de l'aperçu ; si la sélection n'est finalement pas une vidéo, Espace est renvoyée telle quelle.
        /// </summary>
        private bool OnSpace()
        {
            if (_paused || _hook.OtherPreviewRunning) return false;
            if (KeyDown(VK_SHIFT) || KeyDown(VK_CONTROL) || KeyDown(VK_MENU) || KeyDown(VK_LWIN) || KeyDown(VK_RWIN)) return false;
            // on tape un nom pour sauter à un fichier (« Le Seigneur… ») : l'espace est pour l'Explorateur
            if (unchecked(Environment.TickCount - _hook.LastTypedTick) < 1000) return false;
            // fil de l'aperçu bloqué (Explorateur figé, disque réseau qui ne répond pas) : on ne promet rien
            if (_worker.Busy) return false;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _worker.PreviewWindow) return false;   // dans l'aperçu, Espace le referme (kshosc.lua)
            var kind = ShellSelection.KindOf(fg);
            if (kind == ShellKind.None || !ShellSelection.FocusInFileList(fg, kind)) return false;
            int asked = Environment.TickCount;
            _worker.Post(() => _worker.OpenFromShell(fg, kind, asked));
            return true;
        }

        private static void OpenApp()
        {
            try
            {
                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Product.Exe);
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Write("Ouverture de " + Product.Name, ex); }
        }

        private static Icon TrayIcon()
        {
            try
            {
                using (var s = typeof(ApercuApp).Assembly.GetManifestResourceStream("app.ico"))
                    if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
            }
            catch { }
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        protected override void ExitThreadCore()
        {
            Log.Write("Aperçu arrêté");
            _tray.Visible = false;
            _tray.Dispose();
            _hook.Dispose();
            _worker.Dispose();
            base.ExitThreadCore();
        }
    }

    /// <summary>
    /// Fil de l'aperçu (STA, pour parler à l'Explorateur en COM) : lit la sélection, ouvre / referme la fenêtre
    /// d'aperçu, et fait suivre la sélection de l'Explorateur quand on passe d'une vidéo à l'autre.
    /// </summary>
    internal sealed class PreviewWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private ShellSelection _shell;
        private PreviewPlayer _player;
        private IntPtr _source;          // fenêtre de l'Explorateur (ou Bureau) d'où vient l'aperçu
        private ShellKind _sourceKind;
        private string _opened;          // fichier ouvert depuis l'Explorateur (déjà sélectionné là-bas)
        private volatile IntPtr _previewHwnd;
        private volatile int _busySince; // début de la tâche en cours (0 = libre)

        private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;

        public PreviewWorker()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "apercu" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>Fenêtre de l'aperçu ouvert (IntPtr.Zero sinon) : lue par le crochet clavier.</summary>
        public IntPtr PreviewWindow => _previewHwnd;

        /// <summary>Le fil de l'aperçu est coincé depuis plus de 300 ms (Explorateur qui ne répond pas…).</summary>
        public bool Busy
        {
            get
            {
                int since = _busySince;
                return since != 0 && unchecked(Environment.TickCount - since) > 300;
            }
        }

        public void Post(Action a)
        {
            try { if (!_queue.IsAddingCompleted) _queue.Add(a); }
            catch (InvalidOperationException) { }
        }

        private void Run()
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // file de messages : nécessaire pour AttachThreadInput
            // Filet de sécurité : une inscription COM de plus sur ce fil. Si une bibliothèque appelle un jour
            // CoUninitialize() ici par erreur (libmpv le fait en se détruisant), le lien avec l'Explorateur tient.
            CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            _shell = new ShellSelection();
            // le moteur (gros fichier) est chargé en avance, sans retarder le démarrage de Windows
            if (_queue.TryTake(out var first, 4000)) Safe(first);
            Safe(PreviewPlayer.Preload);
            foreach (var a in _queue.GetConsumingEnumerable()) Safe(a);
            var last = _player;
            Safe(DisposePlayer);
            last?.WaitClosed(3000);
            _shell.Dispose();
        }

        private void Safe(Action a)
        {
            if (a == null) return;
            _busySince = Environment.TickCount | 1;
            try { a(); }
            catch (Exception ex) { Log.Write("Aperçu", ex); }
            finally { _busySince = 0; }
        }

        /// <summary>Espace dans l'Explorateur / sur le Bureau.</summary>
        public void OpenFromShell(IntPtr shellWindow, ShellKind kind, int askedTick)
        {
            // demande restée en attente (l'Explorateur ne répondait plus) : trop tard, on l'oublie
            if (unchecked(Environment.TickCount - askedTick) > 1000) { Log.Write("Espace trop ancienne : ignorée"); return; }
            string path = _shell.SelectedPath(shellWindow, kind);
            if (!IsPreviewable(path))
            {
                // pas une vidéo : la touche Espace repart vers l'Explorateur comme si de rien n'était
                ReinjectSpace();
                return;
            }
            _source = shellWindow;
            _sourceKind = kind;
            _opened = path;

            if (_player != null && _player.Alive)
            {
                // comme Quick Look : Espace sur la vidéo déjà ouverte referme l'aperçu, sur une autre la montre
                if (SamePath(_player.CurrentPath, path)) { _player.Close(); return; }
                _player.Load(path);
                if (_previewHwnd != IntPtr.Zero) ForceForeground(_previewHwnd);
                return;
            }

            DisposePlayer();
            try
            {
                var player = new PreviewPlayer(AppDir, ScreenIndexOf(shellWindow));
                player.WindowReady += h => Post(() =>
                {
                    if (_player != player) return;
                    _previewHwnd = h;
                    SetWindowIcon(h);        // l'icône KSHPLAYER dans la barre des tâches (pas celle de mpv)
                    ForceForeground(h);
                });
                player.PathChanged += p => Post(() => { if (_player == player) FollowInShell(p); });
                player.Closed += () => Post(() => { if (_player == player) DisposePlayer(); else player.Dispose(); });
                _player = player;
                player.Load(path);
                Log.Write("Aperçu : " + path);
            }
            catch (Exception ex)
            {
                Log.Write("Aperçu impossible : " + path, ex);
                DisposePlayer();
            }
        }

        /// <summary>← → dans l'aperçu : l'Explorateur sélectionne la même vidéo (on la retrouve en refermant).</summary>
        private void FollowInShell(string path)
        {
            if (_source == IntPtr.Zero || SamePath(path, _opened) || !IsWindow(_source)) return;
            _opened = path;
            _shell.Select(_source, _sourceKind, path);
        }

        private void DisposePlayer()
        {
            var p = _player;
            _player = null;
            _previewHwnd = IntPtr.Zero;
            p?.Dispose();
        }

        private static bool IsPreviewable(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var ext = Path.GetExtension(path);
            return Product.PreviewExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))
                   && File.Exists(path);
        }

        private static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Numéro de l'écran où se trouve l'Explorateur (même ordre que mpv), -1 si inconnu.</summary>
        private static int ScreenIndexOf(IntPtr hwnd)
        {
            IntPtr target = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            int index = -1, i = 0;
            MonitorEnumProc cb = (h, dc, rc, data) => { if (h == target) index = i; i++; return true; };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return index;
        }

        /// <summary>
        /// Donne le premier plan à l'aperçu. Windows le refuse à un programme en arrière-plan : on s'attache
        /// un instant au fil de la fenêtre active (l'Explorateur), qui, lui, a le droit.
        /// </summary>
        private static void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return;
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
        }

        private static void ReinjectSpace()
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = VK_SPACE;
            inputs[0].u.ki.dwExtraInfo = KeyboardHook.OwnInputTag;
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = VK_SPACE;
            inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[1].u.ki.dwExtraInfo = KeyboardHook.OwnInputTag;
            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(4000);
        }
    }

    /// <summary>Menu de l'icône : sombre, charte KSHFLIX.</summary>
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? SetupForm.Text1 : SetupForm.Text3;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            int d = Math.Min(r.Width, r.Height) - 4;
            var box = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            using (var b = new SolidBrush(SetupForm.Red)) g.FillEllipse(b, box);
            using (var p = new Pen(Color.White, Math.Max(1.5f, d / 8f)))
                g.DrawLines(p, new[]
                {
                    new PointF(box.X + d * 0.27f, box.Y + d * 0.52f),
                    new PointF(box.X + d * 0.44f, box.Y + d * 0.68f),
                    new PointF(box.X + d * 0.74f, box.Y + d * 0.34f),
                });
        }
    }

    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        private static readonly Color Hover = Color.FromArgb(0x24, 0x24, 0x2C);
        public override Color ToolStripDropDownBackground => SetupForm.Surface;
        public override Color ImageMarginGradientBegin => SetupForm.Surface;
        public override Color ImageMarginGradientMiddle => SetupForm.Surface;
        public override Color ImageMarginGradientEnd => SetupForm.Surface;
        public override Color MenuBorder => SetupForm.Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => SetupForm.Line;
        public override Color SeparatorLight => SetupForm.Line;
        public override Color CheckBackground => SetupForm.Surface;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
