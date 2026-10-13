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
        private readonly CheckBox _desktop, _openWith;
        private readonly Button _install, _cancel;
        private readonly Label _status;
        private double _progress = -1;
        private bool _done;
        private readonly bool _updateMode;

        private int P(float v) => (int)Math.Round(v * S);

        public SetupForm(bool updateMode = false)
        {
            _updateMode = updateMode;
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Text1;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(P(860), P(Product.IsPro ? 720 : 640));
            Text = "Installation de " + Product.Name;
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
                Bounds = new Rectangle(P(40), P(186), ClientSize.Width - P(80), ClientSize.Height - P(186) - P(150)),
            };
            Controls.Add(_features);

            // Options
            _desktop = MakeCheck("Créer un raccourci sur le Bureau", P(40), ClientSize.Height - P(132));
            _openWith = MakeCheck("Ajouter " + Product.Name + " à « Ouvrir avec » pour les vidéos", P(40), ClientSize.Height - P(104));

            _status = new Label
            {
                Text = "Installé dans " + Installer.InstallDir, ForeColor = Text3, BackColor = Bg,
                Font = new Font("Segoe UI", 8.5f), AutoSize = false, AutoEllipsis = true,
                Bounds = new Rectangle(P(40), ClientSize.Height - P(66), ClientSize.Width - P(400), P(40)),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            Controls.Add(_status);

            _cancel = MakeButton("Annuler", false);
            _cancel.Bounds = new Rectangle(ClientSize.Width - P(340), ClientSize.Height - P(70), P(120), P(46));
            _cancel.Click += (s, e) => Close();
            _install = MakeButton("Installer  →", true);
            _install.Bounds = new Rectangle(ClientSize.Width - P(208), ClientSize.Height - P(70), P(168), P(46));
            _install.Click += OnInstall;

            MouseDown += DragWindow;

            if (_updateMode)
            {
                // mise à jour depuis l'appli : on garde les choix précédents et on installe tout de suite
                _desktop.Checked = System.IO.File.Exists(Installer.DesktopLink);
                _openWith.Checked = Installer.OpenWithRegistered();
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
            using (var fEye = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var b = new SolidBrush(Pink))
                g.DrawString(("CE QUE FAIT " + Product.Name).ToUpperInvariant(), fEye, b, P(40), P(156));
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
            if (_updateMode)
            {
                // laisse à KSHPLAYER le temps de se fermer
                for (int i = 0; i < 40 && Installer.IsRunning(); i++) await Task.Delay(250);
            }
            if (Installer.IsRunning())
            {
                _status.ForeColor = Pink;
                _status.Text = Product.Name + " est ouvert : ferme-le puis clique à nouveau sur Installer.";
                return;
            }

            _install.Enabled = _cancel.Enabled = _desktop.Enabled = _openWith.Enabled = false;
            _install.Text = "Installation…";
            _status.ForeColor = Text2;
            _status.Text = "Copie de " + Product.Name + "…";
            _progress = 0; Invalidate();
            bool desktop = _desktop.Checked, openWith = _openWith.Checked;
            try
            {
                await Task.Run(() => Installer.Install(desktop, openWith, p => BeginInvoke(new Action(() =>
                {
                    _progress = p;
                    Invalidate(new Rectangle(0, ClientSize.Height - P(30), ClientSize.Width, P(30)));
                }))));
                _done = true;
                _status.ForeColor = Text1;
                _status.Text = "✓  " + Product.Name + " est installé. Tu le trouves dans le menu Démarrer.";
                _install.Text = "Lancer  →";
                _install.Enabled = true;
                _cancel.Text = "Fermer";
                _cancel.Enabled = true;
                if (_updateMode)
                {
                    _status.Text = "✓  Mise à jour " + Product.Version + " installée, relance de " + Product.Name + "…";
                    await Task.Delay(900);
                    OnInstall(this, EventArgs.Empty);   // _done = true -> lance l'appli et ferme
                }
            }
            catch (Exception ex)
            {
                _status.ForeColor = Pink;
                _status.Text = "Échec de l'installation : " + ex.Message;
                _install.Text = "Réessayer";
                _install.Enabled = _cancel.Enabled = true;
            }
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
                // hauteur de chaque ligne = carte la plus haute des deux colonnes
                var feats = Product.Features;
                float y = -_offset;
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
