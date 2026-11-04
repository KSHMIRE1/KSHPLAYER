using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace KSHPLAYER.Setup
{
    /// <summary>
    /// Installation par utilisateur (pas besoin des droits administrateur).
    /// Ce fichier sert aussi au désinstallateur (KSHPLAYER-Apercu.exe /uninstall) : une seule source pour
    /// les chemins, les raccourcis et les clés de registre.
    /// </summary>
    internal static class Installer
    {
        public static string InstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product.Name);

        public static string StartMenuLink => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product.Name + ".lnk");

        public static string DesktopLink => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Product.Name + ".lnk");

        public static string ExePath => Path.Combine(InstallDir, Product.Exe);

        /// <summary>Aperçu Espace (résident) et désinstallateur : le même petit exe.</summary>
        public static string ApercuPath => Path.Combine(InstallDir, Product.ApercuExe);

        /// <summary>Réglages et journaux, partagés par les deux éditions : %LOCALAPPDATA%\KSHPLAYER</summary>
        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KSHPLAYER");

        private static string[] ProcessNames => new[]
        {
            Path.GetFileNameWithoutExtension(Product.Exe),
            Path.GetFileNameWithoutExtension(Product.ApercuExe),
        };

        public static bool IsRunning() => RunningProcesses().Any();

        /// <summary>Le lecteur lui-même est ouvert ? (l'aperçu résident, lui, ne se ferme jamais tout seul)</summary>
        public static bool IsAppRunning()
        {
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Product.Exe));
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }

        private static Process[] RunningProcesses()
        {
            int self = Process.GetCurrentProcess().Id;
            return ProcessNames.SelectMany(Process.GetProcessesByName).Where(p => p.Id != self).ToArray();
        }

        private static bool Exited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        /// <summary>
        /// Ferme KSHPLAYER et son aperçu s'ils tournent (proprement d'abord, puis de force).
        /// includePlayer = false (mise à jour préparée par le lecteur) : seul l'aperçu est arrêté, jamais une vidéo.
        /// Renvoie false si un processus refuse de s'arrêter.
        /// </summary>
        public static bool StopRunning(bool includePlayer = true)
        {
            var apercu = Path.GetFileNameWithoutExtension(Product.ApercuExe);
            var procs = RunningProcesses()
                .Where(p => includePlayer || string.Equals(p.ProcessName, apercu, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (procs.Length == 0) return true;
            foreach (var p in procs)
            {
                try
                {
                    // l'aperçu n'a pas de fenêtre à fermer : on l'arrête directement
                    if (string.Equals(p.ProcessName, apercu, StringComparison.OrdinalIgnoreCase)) p.Kill();
                    else p.CloseMainWindow();
                }
                catch { }
            }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500 && procs.Any(p => !Exited(p))) Thread.Sleep(100);
            foreach (var p in procs.Where(p => !Exited(p)))
            {
                try { p.Kill(); } catch { }
            }
            while (sw.ElapsedMilliseconds < 8000 && procs.Any(p => !Exited(p))) Thread.Sleep(100);
            return procs.All(Exited);
        }

        /// <summary>
        /// Une seule installation / désinstallation / mise à jour à la fois pour cette édition (verrou système).
        /// null si une autre est déjà en cours ; sinon à garder jusqu'à la fin, puis Dispose.
        /// </summary>
        public static Mutex TryLockInstallation()
        {
            var m = new Mutex(false, @"Local\" + Product.Key + "-Installation");
            try
            {
                if (m.WaitOne(0)) return m;
            }
            catch (AbandonedMutexException) { return m; }   // l'installateur précédent a été arrêté brutalement
            m.Dispose();
            return null;
        }

        // ----- Version déjà installée (écran « Mise à jour ») -----

        /// <summary>Version installée (null si KSHPLAYER n'est pas installé pour cet utilisateur).</summary>
        public static Version InstalledVersion()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                {
                    var v = k?.GetValue("DisplayVersion") as string;
                    if (v == null || !File.Exists(ExePath)) return null;
                    return Version.TryParse(v, out var parsed) ? parsed : null;
                }
            }
            catch { return null; }
        }

        public static Version CurrentVersion => Version.Parse(Product.Version);

        // ----- Installation -----

        public static void Install(bool desktop, bool openWith, bool apercu, Action<double> progress, bool keepPlayer = false)
        {
            var dir = InstallDir;
            if (!StopRunning(includePlayer: !keepPlayer))
                throw new InvalidOperationException(Product.Name + " refuse de se fermer : ferme-le puis réessaie.");
            if (keepPlayer && IsAppRunning())
                throw new PlayerRunningException();     // une vidéo vient d'être ouverte : on attendra qu'elle soit refermée
            DeleteDirectory(dir);   // mise à jour propre (les réglages vivent ailleurs, dans %LOCALAPPDATA%\KSHPLAYER)
            Directory.CreateDirectory(dir);

            Unpack(dir, progress);

            CreateShortcut(StartMenuLink, ExePath, dir);
            if (desktop) CreateShortcut(DesktopLink, ExePath, dir);
            else TryDelete(DesktopLink);
            if (openWith) RegisterOpenWith(); else UnregisterOpenWith();
            SetApercuAtStartup(apercu && File.Exists(ApercuPath));
            RegisterUninstall();
            RefreshExplorer();
            progress(1.0);
        }

        /// <summary>Supprime un dossier, en réessayant un peu si un fichier est encore verrouillé.</summary>
        private static void DeleteDirectory(string dir)
        {
            for (int attempt = 0; Directory.Exists(dir); attempt++)
            {
                try { Directory.Delete(dir, true); return; }
                catch (Exception) when (attempt < 10) { Thread.Sleep(300); }
            }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        // ----- Décompression de l'archive KSHPAK (LZMS, moteur intégré à Windows) -----
        [DllImport("cabinet.dll", SetLastError = true)]
        private static extern bool CreateDecompressor(uint algorithm, IntPtr alloc, out IntPtr handle);
        [DllImport("cabinet.dll", SetLastError = true)]
        private static extern bool Decompress(IntPtr h, byte[] src, UIntPtr srcSize, byte[] dst, UIntPtr dstSize, out UIntPtr outSize);
        [DllImport("cabinet.dll")]
        private static extern bool CloseDecompressor(IntPtr h);
        private const uint LZMS = 5;

        private static void Unpack(string dir, Action<double> progress)
        {
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.kpk"))
            using (var r = new BinaryReader(res))
            {
                if (r.ReadString() != "KSHPAK1") throw new InvalidDataException("Archive d'installation invalide.");
                int count = r.ReadInt32();
                long total = res.Length, done = 0;
                if (!CreateDecompressor(LZMS, IntPtr.Zero, out var h))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Décompresseur Windows indisponible.");
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        string rel = r.ReadString();
                        long size = r.ReadInt64();
                        bool packed = r.ReadByte() == 1;
                        long stored = r.ReadInt64();
                        byte[] data = r.ReadBytes((int)stored);

                        var target = Path.GetFullPath(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));

                        if (packed)
                        {
                            var outBuf = new byte[size];
                            if (!Decompress(h, data, (UIntPtr)data.Length, outBuf, (UIntPtr)outBuf.Length, out var got) || (long)got.ToUInt64() != size)
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "Fichier endommagé : " + rel);
                            File.WriteAllBytes(target, outBuf);
                        }
                        else File.WriteAllBytes(target, data);

                        done += stored;
                        progress(0.9 * done / total);
                    }
                }
                finally { CloseDecompressor(h); }
            }
        }

        // ----- Raccourcis (.lnk) via WScript.Shell -----
        private static void CreateShortcut(string lnk, string target, string workDir)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            object sc = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var t = sc.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { Product.Tagline });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            Marshal.FinalReleaseComObject(sc);
            Marshal.FinalReleaseComObject(shell);
        }

        // ----- « Ouvrir avec » pour les vidéos -----
        private static string ProgId => Product.Key + ".Video";

        public static bool OpenWithRegistered()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId)) return k != null;
        }

        private static void RegisterOpenWith()
        {
            var cmd = "\"" + ExePath + "\" \"%1\"";
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                k.SetValue("", "Vidéo · " + Product.Name);
                using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue("", ExePath + ",0");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", cmd);
            }
            using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\" + Product.Exe))
            {
                app.SetValue("FriendlyAppName", Product.Name);
                using (var c = app.CreateSubKey(@"shell\open\command")) c.SetValue("", cmd);
                using (var st = app.CreateSubKey("SupportedTypes"))
                    foreach (var ext in Product.VideoExtensions) st.SetValue(ext, "");
            }
            foreach (var ext in Product.VideoExtensions)
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids"))
                    k.SetValue(ProgId, new byte[0], RegistryValueKind.None);
        }

        private static void UnregisterOpenWith()
        {
            TryDeleteKey(@"Software\Classes\" + ProgId);
            TryDeleteKey(@"Software\Classes\Applications\" + Product.Exe);
            foreach (var ext in Product.VideoExtensions)
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids", true))
                        k?.DeleteValue(ProgId, false);
                }
                catch { }
            }
        }

        private static void TryDeleteKey(string path)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(path, false); } catch { }
        }

        // ----- Aperçu Espace : lancé avec Windows -----
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string RunValue => Product.Name + " Aperçu";

        public static bool ApercuAtStartup()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue(RunValue) != null; }
            catch { return false; }
        }

        public static void SetApercuAtStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunValue, "\"" + ApercuPath + "\" --demarrage");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch { }
        }

        /// <summary>Lance l'aperçu tout de suite après l'installation (Espace marche sans redémarrer).</summary>
        public static void StartApercu(bool welcome)
        {
            try
            {
                if (File.Exists(ApercuPath))
                    Process.Start(new ProcessStartInfo(ApercuPath, welcome ? "--bienvenue" : "--demarrage")
                    { UseShellExecute = false, WorkingDirectory = InstallDir });
            }
            catch { }
        }

        // ----- Paramètres > Applications -----
        private static string UninstallKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Product.Key;

        private static void RegisterUninstall()
        {
            long sizeKb = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Product.Name);
                k.SetValue("DisplayVersion", Product.Version);
                k.SetValue("Publisher", Product.Publisher);
                k.SetValue("Comments", Product.Tagline);
                k.SetValue("DisplayIcon", ExePath + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("URLInfoAbout", Product.Website);
                k.SetValue("UninstallString", "\"" + ApercuPath + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + ApercuPath + "\" /uninstall /S");
                k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        // ----- Désinstallation (lancée par KSHPLAYER-Apercu.exe /uninstall) -----

        /// <summary>
        /// Retire KSHPLAYER de ce PC : programme, raccourcis, « Ouvrir avec », démarrage de l'aperçu,
        /// entrée dans Paramètres. Les LUT et captures de l'édition PRO (Documents / Images) restent.
        /// </summary>
        public static void Uninstall(bool removeData, Action<double> progress)
        {
            progress(0.05);
            StopRunning();
            progress(0.25);
            SetApercuAtStartup(false);
            TryDelete(StartMenuLink);
            TryDelete(DesktopLink);
            UnregisterOpenWith();
            TryDeleteKey(UninstallKey);
            progress(0.45);

            // fichiers du programme : tout sauf l'exe qui tourne (il part juste après, voir plus bas)
            var dir = InstallDir;
            var self = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
                    if (!string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            }
            progress(0.8);

            if (removeData)
            {
                try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); } catch { }
                try
                {
                    var tmp = Path.GetTempPath();
                    foreach (var sub in new[] { "KSHPLAYER-maj", "KSHPLAYER-nas" })
                    {
                        var d = Path.Combine(tmp, sub);
                        if (Directory.Exists(d)) Directory.Delete(d, true);
                    }
                    foreach (var f in Directory.EnumerateFiles(tmp, "kshplayer-lut-*.glsl")) TryDelete(f);
                }
                catch { }
            }

            RefreshExplorer();
            progress(1.0);
        }

        /// <summary>
        /// À appeler juste avant que le désinstallateur se ferme : le dossier du programme (et l'exe qui tourne)
        /// disparaissent 2 s plus tard, une fois l'exe libéré. Aucune fenêtre noire.
        /// </summary>
        public static void RemoveInstallFolderAfterExit()
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 > nul & rd /s /q \"" + InstallDir + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),
                });
            }
            catch { }
        }

        /// <summary>Prévient l'Explorateur (icônes, « Ouvrir avec ») : utile mais jamais bloquant.</summary>
        private static void RefreshExplorer()
        {
            try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    }

    /// <summary>Mise à jour : une vidéo a été ouverte juste avant l'installation, on attend qu'elle soit refermée.</summary>
    internal sealed class PlayerRunningException : Exception
    {
        public PlayerRunningException() : base(Product.Name + " est ouvert.") { }
    }
}
