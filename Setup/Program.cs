using System;
using System.Linq;
using System.Windows.Forms;

namespace KSHPLAYER.Setup
{
    internal static class Program
    {
        /// <summary>
        /// KSHPLAYER-Setup.exe            : installation (écran adapté à ce qui est déjà installé)
        /// KSHPLAYER-Setup.exe /update    : mise à jour lancée par le lecteur (1.2 : /pid=&lt;lecteur&gt;), sans question
        /// </summary>
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool update = args.Any(a => a.Equals("/update", StringComparison.OrdinalIgnoreCase));
            int pid = 0;
            foreach (var a in args)
                if (a.StartsWith("/pid=", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(5), out pid);

            // une seule installation à la fois (deux lecteurs refermés, installateur lancé deux fois, désinstallation…)
            using (var lockInstall = Installer.TryLockInstallation())
            {
                if (lockInstall == null)
                {
                    if (!update)
                        MessageBox.Show("L'installation de " + Product.Name + " est déjà en cours.", Product.Name,
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new SetupForm(update, pid));
                try { lockInstall.ReleaseMutex(); } catch { }
            }
        }
    }
}
