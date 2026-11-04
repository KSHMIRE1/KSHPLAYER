using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using KSHPLAYER.Setup;

namespace KSHPLAYER.Apercu
{
    /// <summary>
    /// KSHPLAYER-Apercu.exe (KSHPLAYERPRO-Apercu.exe en édition PRO), installé à côté du lecteur :
    ///   (sans argument) / --demarrage / --bienvenue : aperçu résident à la barre Espace
    ///   /uninstall [/S]                               : désinstallation de KSHPLAYER (fenêtre KSHFLIX, ou silencieuse)
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool Has(string a) => args.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log.Write("Erreur", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Erreur fatale", e.ExceptionObject as Exception);

            if (Has("/uninstall") || Has("--uninstall"))
                return UninstallForm.Run(silent: Has("/S") || Has("/quiet") || Has("--quiet"));

            using (var single = SingleInstance.Acquire())
            {
                if (single == null) return 0;   // un aperçu tourne déjà
                Application.Run(new ApercuApp(welcome: Has("--bienvenue")));
            }
            return 0;
        }
    }

    /// <summary>
    /// Un seul aperçu pour tout le PC, même si les deux éditions sont installées : la PRO prend la place de la normale.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        private readonly Mutex _mutex;
        private SingleInstance(Mutex m) { _mutex = m; }

        public static SingleInstance Acquire()
        {
            var m = new Mutex(false, @"Local\KSHPLAYER-Apercu");
            bool owned = Take(m, 0);
            if (!owned && Product.IsPro)
            {
                foreach (var p in Process.GetProcessesByName("KSHPLAYER-Apercu"))
                {
                    try { p.Kill(); } catch { }
                    p.Dispose();
                }
                owned = Take(m, 3000);
            }
            if (owned) return new SingleInstance(m);
            m.Dispose();
            return null;
        }

        private static bool Take(Mutex m, int ms)
        {
            try { return m.WaitOne(ms); }
            catch (AbandonedMutexException) { return true; }   // l'ancien aperçu a été arrêté brutalement
        }

        public void Dispose()
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }

    /// <summary>Journal de l'aperçu : %LOCALAPPDATA%\KSHPLAYER\apercu.log, plafonné à 1 Mo.</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string FilePath => Path.Combine(Installer.DataDir, "apercu.log");

        public static void Write(string message, Exception ex = null)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Installer.DataDir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        var old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message +
                        (ex != null ? Environment.NewLine + ex : "") + Environment.NewLine);
                }
            }
            catch { /* le journal ne doit jamais rien casser */ }
        }
    }
}
