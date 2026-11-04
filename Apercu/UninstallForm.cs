using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using KSHPLAYER.Setup;

namespace KSHPLAYER.Apercu
{
    /// <summary>
    /// Désinstallation de KSHPLAYER (Paramètres › Applications › Désinstaller) : petite fenêtre KSHFLIX
    /// au lieu de l'ancienne fenêtre noire (Desinstaller.cmd). « /uninstall /S » : sans fenêtre.
    /// </summary>
    internal sealed class UninstallForm : Form
    {
        public static int Run(bool silent)
        {
            // jamais pendant une installation ou une mise à jour (elle réinstallerait ce qu'on retire)
            using (var lockInstall = Installer.TryLockInstallation())
            {
                if (lockInstall == null)
                {
                    if (!silent)
                        MessageBox.Show("Une installation de " + Product.Name + " est en cours : réessaie quand elle sera finie.",
                            Product.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }
                int result = RunLocked(silent);
                try { lockInstall.ReleaseMutex(); } catch { }
                return result;
            }
        }

        private static int RunLocked(bool silent)
        {
            int code = 0;
            bool removed = false;
            if (silent)
            {
                try
                {
                    Installer.Uninstall(false, _ => { });
                    removed = true;
                    Log.Write("Désinstallation silencieuse terminée");
                }
                catch (Exception ex)
                {
                    Log.Write("Désinstallation silencieuse", ex);
                    code = 1;
                }
            }
            else
            {
                using (var f = new UninstallForm())
                {
                    Application.Run(f);
                    removed = f._done;
                }
            }
            if (removed) Installer.RemoveInstallFolderAfterExit();
            return code;
        }

        private readonly float S;
        private readonly Image _logo;
        private readonly CheckBox _data;
        private readonly Button _go, _cancel;
        private readonly Label _status;
        private double _progress = -1;
        private bool _done;
        private string _title, _text;

        private int P(float v) => (int)Math.Round(v * S);

        private UninstallForm()
        {
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SetupForm.Bg;
            ForeColor = SetupForm.Text1;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(P(660), P(Product.IsPro ? 400 : 372));
            Text = "Désinstaller " + Product.Name;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (var s = typeof(UninstallForm).Assembly.GetManifestResourceStream("logo.png"))
                _logo = s != null ? Image.FromStream(s) : null;

            _title = "Désinstaller " + Product.Name + " ?";
            _text = "Le programme, ses raccourcis, « Ouvrir avec » et l'aperçu à la barre Espace seront retirés de ce PC.";
            if (Product.IsPro)
                _text += "\nTes LUT (Documents\\KSHPLAYER PRO) et tes captures (Images\\KSHPLAYER PRO) restent là où elles sont.";

            var close = new Label
            {
                Text = "✕", ForeColor = SetupForm.Text2, BackColor = SetupForm.Bg, Font = new Font("Segoe UI", 12f),
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
                Bounds = new Rectangle(ClientSize.Width - P(52), P(14), P(36), P(36)),
            };
            close.Click += (s, e) => { if (_cancel.Enabled || _done) Close(); };
            close.MouseEnter += (s, e) => close.ForeColor = SetupForm.Red;
            close.MouseLeave += (s, e) => close.ForeColor = SetupForm.Text2;
            Controls.Add(close);

            _data = new CheckBox
            {
                Text = "  Effacer aussi les réglages et les journaux", Checked = false, ForeColor = SetupForm.Text2,
                BackColor = SetupForm.Bg, FlatStyle = FlatStyle.Flat, AutoSize = true, Cursor = Cursors.Hand,
                Location = new Point(P(40), ClientSize.Height - P(118)), Font = new Font("Segoe UI", 9.5f),
            };
            _data.FlatAppearance.BorderColor = SetupForm.Line;
            _data.FlatAppearance.CheckedBackColor = SetupForm.Red;
            Controls.Add(_data);

            _status = new Label
            {
                Text = Installer.IsRunning() ? Product.Name + " est ouvert : il sera fermé." : Installer.InstallDir,
                ForeColor = SetupForm.Text3, BackColor = SetupForm.Bg, Font = new Font("Segoe UI", 8.5f),
                AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(P(40), ClientSize.Height - P(66), ClientSize.Width - P(400), P(40)),
            };
            Controls.Add(_status);

            _cancel = MakeButton("Annuler", false);
            _cancel.Bounds = new Rectangle(ClientSize.Width - P(340), ClientSize.Height - P(70), P(120), P(46));
            _cancel.Click += (s, e) => Close();
            _go = MakeButton("Désinstaller", true);
            _go.Bounds = new Rectangle(ClientSize.Width - P(208), ClientSize.Height - P(70), P(168), P(46));
            _go.Click += OnUninstall;
            AcceptButton = _go;
            CancelButton = _cancel;

            MouseDown += DragWindow;
        }

        private Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = primary ? SetupForm.Red : SetupForm.Surface, ForeColor = primary ? Color.White : SetupForm.Text2,
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = SetupForm.Line;
            b.FlatAppearance.MouseOverBackColor = primary ? SetupForm.RedHi : Color.FromArgb(0x1B, 0x1B, 0x22);
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(0xC8, 0x1E, 0x30) : SetupForm.Line;
            Controls.Add(b);
            return b;
        }

        private async void OnUninstall(object sender, EventArgs e)
        {
            if (_done) { Close(); return; }
            _go.Enabled = _cancel.Enabled = _data.Enabled = false;
            _go.Text = "Désinstallation…";
            _status.ForeColor = SetupForm.Text2;
            _status.Text = "Fermeture de " + Product.Name + " et retrait des fichiers…";
            _progress = 0;
            Invalidate();
            bool data = _data.Checked;
            try
            {
                await Task.Run(() => Installer.Uninstall(data, p => BeginInvoke(new Action(() =>
                {
                    _progress = p;
                    Invalidate(new Rectangle(0, ClientSize.Height - P(30), ClientSize.Width, P(30)));
                }))));
                _done = true;
                if (!data) Log.Write(Product.Name + " désinstallé");   // sinon le journal effacé serait recréé
                _title = Product.Name + " a été désinstallé";
                _text = "Merci de l'avoir utilisé ! Pour le retrouver, relance simplement son installateur.";
                _status.ForeColor = SetupForm.Text1;
                _status.Text = "✓  Terminé.";
                _data.Visible = false;
                _cancel.Visible = false;
                _go.Text = "Fermer";
                _go.Enabled = true;
                Invalidate();
            }
            catch (Exception ex)
            {
                Log.Write("Désinstallation", ex);
                _status.ForeColor = SetupForm.Pink;
                _status.Text = "Échec de la désinstallation : " + ex.Message;
                _go.Text = "Réessayer";
                _go.Enabled = _cancel.Enabled = _data.Enabled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // logo + nom (comme l'installateur, en plus petit)
            if (_logo != null) g.DrawImage(_logo, P(40), P(34), P(48), P(48));
            using (var fName = new Font("Segoe UI Black", 20f, FontStyle.Bold))
            using (var white = new SolidBrush(SetupForm.Text1))
            using (var red = new SolidBrush(SetupForm.Red))
            {
                float x = P(100), y = P(38);
                var ksh = g.MeasureString("KSH", fName, PointF.Empty, StringFormat.GenericTypographic);
                g.DrawString("KSH", fName, white, x, y, StringFormat.GenericTypographic);
                g.DrawString("PLAYER", fName, red, x + ksh.Width, y, StringFormat.GenericTypographic);
                if (Product.IsPro)
                {
                    var player = g.MeasureString("PLAYER", fName, PointF.Empty, StringFormat.GenericTypographic);
                    var badge = new RectangleF(x + ksh.Width + player.Width + P(10), P(45), P(48), P(26));
                    using (var path = SetupForm.Rounded(badge, P(7))) g.FillPath(red, path);
                    using (var fPro = new Font("Segoe UI Black", 10f, FontStyle.Bold))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("PRO", fPro, Brushes.White, badge, sf);
                }
            }
            using (var pen = new Pen(SetupForm.Line, Math.Max(1, S))) g.DrawLine(pen, P(40), P(106), ClientSize.Width - P(40), P(106));

            using (var fTitle = new Font("Segoe UI Semibold", 15f, FontStyle.Bold))
            using (var b = new SolidBrush(SetupForm.Text1))
                g.DrawString(_title, fTitle, b, P(38), P(124));
            using (var fText = new Font("Segoe UI", 10f))
            using (var b = new SolidBrush(SetupForm.Text2))
                g.DrawString(_text, fText, b, new RectangleF(P(40), P(166), ClientSize.Width - P(80), P(90)));

            if (_progress >= 0)
            {
                var r = new RectangleF(P(40), ClientSize.Height - P(20), ClientSize.Width - P(80), P(4));
                using (var bg = new SolidBrush(SetupForm.Line)) using (var p = SetupForm.Rounded(r, P(2))) g.FillPath(bg, p);
                var f = new RectangleF(r.X, r.Y, Math.Max(P(4), r.Width * (float)_progress), r.Height);
                using (var red = new SolidBrush(SetupForm.Red)) using (var p = SetupForm.Rounded(f, P(2))) g.FillPath(red, p);
            }
        }

        // ----- Fenêtre sans bordure : on la déplace en glissant le fond, ombre portée -----
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
                cp.ClassStyle |= 0x20000;   // ombre portée
                return cp;
            }
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
