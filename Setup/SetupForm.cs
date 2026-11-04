using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KSHPLAYER.Setup
{
    /// <summary>Fenêtre d'installation, charte KSHFLIX « Salle obscure ».</summary>
    internal sealed class SetupForm : Form
    {
        // Couleurs KSHFLIX
        internal static readonly Color Bg = Color.FromArgb(0x05, 0x05, 0x06);
        internal static readonly Color Surface = Color.FromArgb(0x12, 0x12, 0x17);
        internal static readonly Color Line = Color.FromArgb(0x23, 0x23, 0x2C);
        internal static readonly Color Red = Color.FromArgb(0xF0, 0x28, 0x3A);
        internal static readonly Color RedHi = Color.FromArgb(0xFF, 0x3A, 0x4B);
        internal static readonly Color Pink = Color.FromArgb(0xFF, 0x8A, 0x95);
        internal static readonly Color Text1 = Color.FromArgb(0xF4, 0xF4, 0xF6);
        internal static readonly Color Text2 = Color.FromArgb(0xB9, 0xB9, 0xC2);
        internal static readonly Color Text3 = Color.FromArgb(0x7A, 0x7A, 0x84);

        private readonly float S;
        private readonly Image _logo;
        private readonly FeatureView _features;
        private readonly CheckBox _desktop, _openWith, _apercu;
        private readonly Button _install, _cancel;
        private readonly Label _status;
        private double _progress = -1;
        private bool _done, _busy, _closing;
        private readonly bool _updateMode;
        private readonly int _pid;     // lecteur qui a lancé la mise à jour (1.2) ; 0 = ancienne version ou installation

        // Écran adapté à ce qui est déjà installé (comme Orbit Look) : installation, mise à jour, réinstallation…
        private enum Mode { Install, Update, Reinstall, Downgrade }
        private readonly Mode _mode;
        private readonly Version _installed;

        private int P(float v) => (int)Math.Round(v * S);

        public SetupForm(bool updateMode = false, int pid = 0)
        {
            _updateMode = updateMode;
            _pid = pid;
            FormClosing += (s, e) => _closing = true;
            _installed = Installer.InstalledVersion();
            var current = Installer.CurrentVersion;
            _mode = _installed == null ? Mode.Install
                  : _installed < current ? Mode.Update
                  : _installed == current ? Mode.Reinstall
                  : Mode.Downgrade;
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Text1;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(P(860), P(Product.IsPro ? 748 : 668));
            Text = (_mode == Mode.Install ? "Installation de " : "Mise à jour de ") + Product.Name;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
                _logo = s != null ? Image.FromStream(s) : null;

            // Fermer
            var close = new Label { Text = "✕", ForeColor = Text2, BackColor = Bg, Font = new Font("Segoe UI", 12f),
                                    AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                                    Bounds = new Rectangle(ClientSize.Width - P(52), P(14), P(36), P(36)), Cursor = Cursors.Hand };
            close.Click += (s, e) => Close();
            close.MouseEnter += (s, e) => close.ForeColor = Red;
            close.MouseLeave += (s, e) => close.ForeColor = Text2;
            Controls.Add(close);

            // Descriptif des fonctions
            _features = new FeatureView(S)
            {
                Bounds = new Rectangle(P(40), P(186), ClientSize.Width - P(80), ClientSize.Height - P(186) - P(178)),
            };
            if (_mode == Mode.Update)
            {
                var notes = ReadNotes();
                if (notes != "") _features.Highlight = new[] { "Nouveautés de la version " + Product.Version, notes };
            }
            Controls.Add(_features);

            // Options
            _desktop = MakeCheck("Créer un raccourci sur le Bureau", P(40), ClientSize.Height - P(160));
            _openWith = MakeCheck("Ajouter " + Product.Name + " à « Ouvrir avec » pour les vidéos", P(40), ClientSize.Height - P(132));
            _apercu = MakeCheck("Aperçu des vidéos avec la barre Espace dans l'Explorateur (lancé avec Windows)", P(40), ClientSize.Height - P(104));

            if (_mode != Mode.Install || _updateMode)
            {
                // déjà installé : on reprend les choix précédents
                _desktop.Checked = System.IO.File.Exists(Installer.DesktopLink);
                _openWith.Checked = Installer.OpenWithRegistered();
                // l'aperçu est nouveau en 1.2 : coché par défaut si la version installée ne l'avait pas encore
                _apercu.Checked = !System.IO.File.Exists(Installer.ApercuPath) || Installer.ApercuAtStartup();
            }

            _status = new Label
            {
                Text = StatusIntro(), ForeColor = Text3, BackColor = Bg,
                Font = new Font("Segoe UI", 8.5f), AutoSize = false, AutoEllipsis = true,
                Bounds = new Rectangle(P(40), ClientSize.Height - P(66), ClientSize.Width - P(400), P(40)),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            Controls.Add(_status);

            _cancel = MakeButton("Annuler", false);
            _cancel.Bounds = new Rectangle(ClientSize.Width - P(340), ClientSize.Height - P(70), P(120), P(46));
            _cancel.Click += (s, e) => Close();
            _install = MakeButton(_mode == Mode.Update ? "Mettre à jour  →"
                                : _mode == Mode.Reinstall ? "Réinstaller  →"
                                : _mode == Mode.Downgrade ? "Remplacer  →"
                                : "Installer  →", true);
            _install.Bounds = new Rectangle(ClientSize.Width - P(208), ClientSize.Height - P(70), P(168), P(46));
            _install.Click += OnInstall;

            MouseDown += DragWindow;

            if (_updateMode)
            {
                // mise à jour depuis l'appli : choix précédents (repris plus haut) et installation tout de suite
                Shown += async (s, e) =>
                {
                    await Task.Delay(400);
                    OnInstall(this, EventArgs.Empty);
                };
            }
        }

        private CheckBox MakeCheck(string text, int x, int y)
        {
            var c = new CheckBox
            {
                Text = "  " + text, Checked = true, ForeColor = Text2, BackColor = Bg, FlatStyle = FlatStyle.Flat,
                AutoSize = true, Location = new Point(x, y), Font = new Font("Segoe UI", 9.5f), Cursor = Cursors.Hand,
            };
            c.FlatAppearance.BorderColor = Line;
            c.FlatAppearance.CheckedBackColor = Red;
            Controls.Add(c);
            return c;
        }

        private Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = primary ? Red : Surface, ForeColor = primary ? Color.White : Text2,
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = primary ? RedHi : Color.FromArgb(0x1B, 0x1B, 0x22);
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(0xC8, 0x1E, 0x30) : Line;
            Controls.Add(b);
            return b;
        }

        private string StatusIntro()
        {
            switch (_mode)
            {
                case Mode.Update: return "Tes réglages sont gardés. " + Product.Name + " sera fermé s'il est ouvert.";
                case Mode.Reinstall: return "Réinstallation dans " + Installer.InstallDir;
                case Mode.Downgrade: return "Une version plus récente (" + _installed.ToString(3) + ") est installée : elle sera remplacée par la " + Product.Version + ".";
                default: return "Installé dans " + Installer.InstallDir;
            }
        }

        /// <summary>Texte de NOTES.txt (embarqué à la compilation) : les nouveautés de cette version.</summary>
        private static string ReadNotes()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("notes.txt"))
                    return s == null ? "" : new System.IO.StreamReader(s, System.Text.Encoding.UTF8).ReadToEnd().Trim();
            }
            catch { return ""; }
        }

        // ---------------- Dessin de l'en-tête et de la barre de progression ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // logo + nom
            if (_logo != null) g.DrawImage(_logo, P(40), P(40), P(64), P(64));
            using (var fName = new Font("Segoe UI Black", 28f, FontStyle.Bold))
            using (var white = new SolidBrush(Text1))
            using (var red = new SolidBrush(Red))
            {
                float x = P(120), y = P(42);
                var ksh = g.MeasureString("KSH", fName, PointF.Empty, StringFormat.GenericTypographic);
                g.DrawString("KSH", fName, white, x, y, StringFormat.GenericTypographic);
                g.DrawString("PLAYER", fName, red, x + ksh.Width, y, StringFormat.GenericTypographic);
                var player = g.MeasureString("PLAYER", fName, PointF.Empty, StringFormat.GenericTypographic);
                if (Product.IsPro)
                {
                    var bx = x + ksh.Width + player.Width + P(12);
                    using (var path = Rounded(new RectangleF(bx, P(54), P(60), P(32)), P(9)))
                        g.FillPath(red, path);
                    using (var fPro = new Font("Segoe UI Black", 12f, FontStyle.Bold))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("PRO", fPro, Brushes.White, new RectangleF(bx, P(54), P(60), P(32)), sf);
                }
            }
            using (var fTag = new Font("Segoe UI", 11f))
            using (var b = new SolidBrush(Text2))
                g.DrawString(Product.Tagline, fTag, b, P(121), P(96));

            // séparateur + titre de section
            using (var pen = new Pen(Line, Math.Max(1, S))) g.DrawLine(pen, P(40), P(140), ClientSize.Width - P(40), P(140));
            string eyebrow;
            switch (_mode)
            {
                case Mode.Update: eyebrow = "MISE À JOUR   ·   " + _installed.ToString(3) + "   →   " + Product.Version; break;
                case Mode.Reinstall: eyebrow = "DÉJÀ INSTALLÉ   ·   VERSION " + Product.Version; break;
                case Mode.Downgrade: eyebrow = "VERSION PLUS RÉCENTE DÉJÀ INSTALLÉE   ·   " + _installed.ToString(3); break;
                default: eyebrow = "CE QUE FAIT " + Product.Name; break;
            }
            using (var fEye = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var b = new SolidBrush(Pink))
                g.DrawString(eyebrow.ToUpperInvariant(), fEye, b, P(40), P(156));
            using (var fVer = new Font("Segoe UI", 8.5f))
            using (var b = new SolidBrush(Text3))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far })
                g.DrawString("Version " + Product.Version + "  ·  Windows 10 / 11 (64 bits)", fVer, b,
                    new RectangleF(0, P(156), ClientSize.Width - P(40), P(20)), sf);

            // progression
            if (_progress >= 0)
            {
                var r = new RectangleF(P(40), ClientSize.Height - P(20), ClientSize.Width - P(80), P(4));
                using (var bg = new SolidBrush(Line)) using (var p = Rounded(r, P(2))) g.FillPath(bg, p);
                var f = new RectangleF(r.X, r.Y, Math.Max(P(4), r.Width * (float)_progress), r.Height);
                using (var red = new SolidBrush(Red)) using (var p = Rounded(f, P(2))) g.FillPath(red, p);
            }
        }

        internal static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------- Installation ----------------
        private async void OnInstall(object sender, EventArgs e)
        {
            if (_done)
            {
                try { Process.Start(new ProcessStartInfo(Installer.ExePath) { UseShellExecute = true }); } catch { }
                Close();
                return;
            }
            if (_busy) return;   // un seul clic compte
            _busy = true;
            _install.Enabled = _cancel.Enabled = _desktop.Enabled = _openWith.Enabled = _apercu.Enabled = false;
            _install.Text = _mode == Mode.Install ? "Installation…" : "Mise à jour…";
            _status.ForeColor = Text2;
            // mise à jour préparée par le lecteur 1.2 (/pid) : on ne coupe jamais une vidéo
            bool keepPlayer = _updateMode && _pid > 0;
            if (_updateMode)
            {
                if (keepPlayer)
                {
                    await WaitForExitAsync(_pid, 30000);        // le lecteur qui nous a lancés se referme
                    if (!await WaitNoPlayerAsync()) return;      // une autre vidéo est ouverte : on attend (Annuler possible)
                }
                else
                {
                    // lancée par une ancienne version (1.1.x) : comme avant, 10 s pour se fermer, puis on la ferme
                    for (int i = 0; i < 40 && Installer.IsAppRunning(); i++) await Task.Delay(250);
                }
                if (Installer.InstalledVersion() == null) { Close(); return; }   // désinstallé entre-temps
            }
            if (!keepPlayer && Installer.IsRunning())
            {
                // comme Orbit Look : on ferme l'appli et son aperçu nous-mêmes, au lieu de demander
                _status.Text = "Fermeture de " + Product.Name + "…";
                if (!await Task.Run(() => Installer.StopRunning()))
                {
                    _status.ForeColor = Pink;
                    _status.Text = Product.Name + " refuse de se fermer : ferme-le puis clique à nouveau.";
                    _install.Text = "Réessayer";
                    _install.Enabled = _cancel.Enabled = _desktop.Enabled = _openWith.Enabled = _apercu.Enabled = true;
                    _busy = false;
                    return;
                }
            }
            _status.Text = "Copie de " + Product.Name + "…";
            _progress = 0; Invalidate();
            bool desktop = _desktop.Checked, openWith = _openWith.Checked, apercu = _apercu.Checked;
            bool hadApercu = System.IO.File.Exists(Installer.ApercuPath);
            try
            {
                while (true)
                {
                    try
                    {
                        await Task.Run(() => Installer.Install(desktop, openWith, apercu, p => BeginInvoke(new Action(() =>
                        {
                            _progress = p;
                            Invalidate(new Rectangle(0, ClientSize.Height - P(30), ClientSize.Width, P(30)));
                        })), keepPlayer));
                        break;
                    }
                    catch (PlayerRunningException)
                    {
                        // une vidéo a été ouverte juste avant la copie : on attend qu'elle soit refermée, puis on recommence
                        if (!await WaitNoPlayerAsync()) return;
                        _status.Text = "Copie de " + Product.Name + "…";
                    }
                }
                if (apercu) Installer.StartApercu(welcome: !hadApercu);
                _done = true;
                _status.ForeColor = Text1;
                _status.Text = _mode == Mode.Update ? "✓  " + Product.Name + " est à jour (version " + Product.Version + ")."
                             : _mode == Mode.Install ? "✓  " + Product.Name + " est installé. Pour lire une vidéo : clic droit › Ouvrir avec › " + Product.Name + "."
                             : "✓  " + Product.Name + " " + Product.Version + " est installé.";
                _install.Text = "Ouvrir une vidéo  →";   // lance le lecteur : fenêtre « Ouvrir » de Windows
                _install.Enabled = true;
                _busy = false;
                _cancel.Text = "Fermer";
                _cancel.Enabled = true;
                if (_updateMode)
                {
                    // plus d'écran d'accueil à rouvrir : la prochaine vidéo s'ouvrira dans la nouvelle version
                    _status.Text = "✓  " + Product.Name + " est à jour (version " + Product.Version + ").";
                    await Task.Delay(2200);
                    Close();
                }
            }
            catch (Exception ex)
            {
                _status.ForeColor = Pink;
                _status.Text = "Échec de l'installation : " + ex.Message;
                _install.Text = "Réessayer";
                _install.Enabled = _cancel.Enabled = _desktop.Enabled = _openWith.Enabled = _apercu.Enabled = true;
                _busy = false;
            }
        }

        private static async Task WaitForExitAsync(int pid, int ms)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    var sw = Stopwatch.StartNew();
                    while (!p.HasExited && sw.ElapsedMilliseconds < ms) await Task.Delay(200);
                }
            }
            catch { /* déjà terminé */ }
        }

        /// <summary>
        /// Attend que plus aucune vidéo ne soit ouverte, sans limite (la mise à jour ne coupe jamais une vidéo) ;
        /// « Annuler » reste possible (elle sera proposée de nouveau à la prochaine fermeture). false si on a annulé.
        /// </summary>
        private async Task<bool> WaitNoPlayerAsync()
        {
            if (!Installer.IsAppRunning()) return true;
            _status.Text = "Mise à jour prête : elle s'installe dès que " + Product.Name + " est fermé.";
            _cancel.Enabled = true;
            while (Installer.IsAppRunning())
            {
                await Task.Delay(500);
                if (_closing || IsDisposed) return false;
            }
            _cancel.Enabled = false;
            return true;
        }

        // ---------------- Déplacement de la fenêtre sans bordure ----------------
        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // ombre portée
                return cp;
            }
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }

    /// <summary>Grille de cartes « fonctionnalités », défilement à la molette, barre fine rouge.</summary>
    internal sealed class FeatureView : Control
    {
        private readonly float S;
        private float _offset;
        private float _contentHeight;

        /// <summary>Carte mise en avant en tête de liste (titre, texte) : les nouveautés lors d'une mise à jour.</summary>
        public string[] Highlight { get; set; }

        public FeatureView(float scale)
        {
            S = scale;
            DoubleBuffered = true;
            SetStyle(ControlStyles.Selectable, true);
            BackColor = SetupForm.Bg;
            MouseEnter += (s, e) => Focus();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            float max = Math.Max(0, _contentHeight - Height);
            _offset = Math.Max(0, Math.Min(max, _offset - e.Delta / 120f * 60 * S));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(SetupForm.Bg);

            float gap = 12 * S, cw = (Width - gap - 10 * S) / 2f, pad = 16 * S;
            using (var fT = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold))
            using (var fD = new Font("Segoe UI", 9f))
            using (var bT = new SolidBrush(SetupForm.Text1))
            using (var bD = new SolidBrush(SetupForm.Text2))
            using (var bS = new SolidBrush(SetupForm.Surface))
            using (var red = new SolidBrush(SetupForm.Red))
            using (var pen = new Pen(SetupForm.Line, Math.Max(1, S)))
            {
                float y = -_offset;
                // carte « Nouveautés » sur toute la largeur, liseré rouge
                if (Highlight != null)
                {
                    float fw = Width - 10 * S;
                    var dh = g.MeasureString(Highlight[1], fD, (int)(fw - pad * 2 - 14 * S)).Height;
                    var r = new RectangleF(0, y, fw, pad * 2 + 22 * S + dh);
                    using (var path = SetupForm.Rounded(r, 16 * S))
                    using (var redPen = new Pen(SetupForm.Red, Math.Max(1, S)))
                    {
                        g.FillPath(bS, path);
                        g.DrawPath(redPen, path);
                    }
                    g.FillEllipse(red, pad, y + pad + 6 * S, 7 * S, 7 * S);
                    g.DrawString(Highlight[0], fT, bT, pad + 14 * S, y + pad - 2 * S);
                    g.DrawString(Highlight[1], fD, bD, new RectangleF(pad + 14 * S, y + pad + 22 * S, fw - pad * 2 - 14 * S, dh + 4 * S));
                    y += r.Height + gap;
                }

                // hauteur de chaque ligne = carte la plus haute des deux colonnes
                var feats = Product.Features;
                for (int i = 0; i < feats.Length; i += 2)
                {
                    float rowH = 0;
                    for (int j = i; j < Math.Min(i + 2, feats.Length); j++)
                    {
                        var dh = g.MeasureString(feats[j][1], fD, (int)(cw - pad * 2 - 14 * S)).Height;
                        rowH = Math.Max(rowH, pad * 2 + 22 * S + dh);
                    }
                    for (int j = i; j < Math.Min(i + 2, feats.Length); j++)
                    {
                        float x = (j - i) * (cw + gap);
                        var r = new RectangleF(x, y, cw, rowH);
                        using (var path = SetupForm.Rounded(r, 16 * S))
                        {
                            g.FillPath(bS, path);
                            g.DrawPath(pen, path);
                        }
                        bool pro = feats[j][0].StartsWith("PRO");
                        g.FillEllipse(red, x + pad, y + pad + 6 * S, 7 * S, 7 * S);
                        g.DrawString(pro ? feats[j][0].Substring(6) : feats[j][0], fT, bT, x + pad + 14 * S, y + pad - 2 * S);
                        if (pro)
                        {
                            var tw = g.MeasureString(feats[j][0].Substring(6), fT).Width;
                            var badge = new RectangleF(x + pad + 14 * S + tw + 4 * S, y + pad, 34 * S, 18 * S);
                            using (var bp = SetupForm.Rounded(badge, 5 * S)) g.FillPath(red, bp);
                            using (var fB = new Font("Segoe UI Black", 7f, FontStyle.Bold))
                            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                g.DrawString("PRO", fB, Brushes.White, badge, sf);
                        }
                        g.DrawString(feats[j][1], fD, bD,
                            new RectangleF(x + pad + 14 * S, y + pad + 22 * S, cw - pad * 2 - 14 * S, rowH));
                    }
                    y += rowH + gap;
                }
                _contentHeight = y + _offset - gap;

                // barre de défilement fine
                if (_contentHeight > Height)
                {
                    float trackH = Height, thumbH = Math.Max(30 * S, trackH * Height / _contentHeight);
                    float thumbY = (trackH - thumbH) * (_offset / (_contentHeight - Height));
                    using (var p = SetupForm.Rounded(new RectangleF(Width - 4 * S, thumbY, 4 * S, thumbH), 2 * S))
                        g.FillPath(red, p);
                }
            }
        }
    }
}
