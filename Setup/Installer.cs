using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KSHPLAYER.Setup
{
    /// <summary>Installation par utilisateur (pas besoin des droits administrateur).</summary>
    internal static class Installer
    {
        public static string InstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product.Name);

        public static string StartMenuLink => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product.Name + ".lnk");

        public static string DesktopLink => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Product.Name + ".lnk");

        public static string ExePath => Path.Combine(InstallDir, Product.Exe);

        public static bool IsRunning() =>
            Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Product.Exe)).Any();

        public static void Install(bool desktop, bool openWith, Action<double> progress)
        {
            var dir = InstallDir;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);   // mise à jour propre
            Directory.CreateDirectory(dir);

            Unpack(dir, progress);

            CreateShortcut(StartMenuLink, ExePath, dir);
            if (desktop) CreateShortcut(DesktopLink, ExePath, dir);
            if (openWith) RegisterOpenWith();
            WriteUninstaller(desktop);
            RegisterUninstall();
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // rafraîchit l'Explorateur
            progress(1.0);
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

        // ----- Désinstallation (Paramètres > Applications) -----
        private static string UninstallKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Product.Key;
        private static string UninstallCmd => Path.Combine(InstallDir, "Desinstaller.cmd");

        private static void RegisterUninstall()
        {
            long sizeKb = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Product.Name);
                k.SetValue("DisplayVersion", Product.Version);
                k.SetValue("Publisher", Product.Publisher);
                k.SetValue("DisplayIcon", ExePath + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + UninstallCmd + "\"");
                k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void WriteUninstaller(bool desktop)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("echo Desinstallation de " + Product.Name + "...");
            sb.AppendLine("taskkill /im \"" + Product.Exe + "\" /f >nul 2>&1");
            sb.AppendLine("del \"" + StartMenuLink + "\" >nul 2>&1");
            if (desktop) sb.AppendLine("del \"" + DesktopLink + "\" >nul 2>&1");
            sb.AppendLine("reg delete \"HKCU\\" + UninstallKey + "\" /f >nul 2>&1");
            sb.AppendLine("reg delete \"HKCU\\Software\\Classes\\" + ProgId + "\" /f >nul 2>&1");
            sb.AppendLine("reg delete \"HKCU\\Software\\Classes\\Applications\\" + Product.Exe + "\" /f >nul 2>&1");
            foreach (var ext in Product.VideoExtensions)
                sb.AppendLine("reg delete \"HKCU\\Software\\Classes\\" + ext + "\\OpenWithProgids\" /v \"" + ProgId + "\" /f >nul 2>&1");
            sb.AppendLine("cd /d \"%TEMP%\"");
            sb.AppendLine("start \"\" /b cmd /c \"timeout /t 2 /nobreak >nul & rd /s /q \"" + InstallDir + "\"\"");
            sb.AppendLine("echo " + Product.Name + " a ete desinstalle.");
            File.WriteAllText(UninstallCmd, sb.ToString(), new UTF8Encoding(false));
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    }
}
