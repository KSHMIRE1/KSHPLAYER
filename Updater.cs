using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace KSHPLAYER;

/// <summary>Nouvelle version publiée (lue dans version.json sur GitHub).</summary>
public sealed record UpdateInfo(Version Version, string Notes, string SetupFile, JsonElement Files);

/// <summary>
/// Mises à jour : GitHub publie installateurs + .dmg + version.json sur la branche « telechargements ».
/// Au démarrage, l'appli compare sa version, propose la mise à jour, télécharge et lance l'installateur.
/// Sur le PC de KSHMIRE (si K:\Backup\Logiciel KSHPC existe), elle recopie aussi les 4 fichiers sur le NAS.
/// </summary>
public static class Updater
{
    public const string BaseUrl = "https://raw.githubusercontent.com/KSHMIRE1/KSHPLAYER/telechargements";
    public const string NasFolder = @"K:\Backup\Logiciel KSHPC";

    private static readonly HttpClient Quick = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly HttpClient Big = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static Version Current => Norm(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));
    private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var json = await Quick.GetStringAsync($"{BaseUrl}/version.json?t={DateTime.UtcNow.Ticks}");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var v = Norm(Version.Parse(root.GetProperty("version").GetString()!));
            var files = root.GetProperty("files").Clone();
            var setup = files.GetProperty(Edition.IsPro ? "windows_pro" : "windows").GetString()!;
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
            return new UpdateInfo(v, notes, setup, files);
        }
        catch (Exception ex)
        {
            AppLog.Write("Vérification des mises à jour impossible", ex);
            return null;
        }
    }

    public static bool IsNewer(UpdateInfo u) => u.Version > Current;

    public static async Task<string> DownloadAsync(string fileName, IProgress<double>? progress)
    {
        var path = Path.Combine(Path.GetTempPath(), "KSHPLAYER-maj", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var resp = await Big.GetAsync($"{BaseUrl}/{Uri.EscapeDataString(fileName)}", HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(path))
        {
            var buf = new byte[1 << 16];
            int read;
            while ((read = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        return path;
    }

    /// <summary>Lance l'installateur en mode mise à jour (installe tout seul puis relance KSHPLAYER).</summary>
    public static void LaunchInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/update") { UseShellExecute = true });

    /// <summary>Recopie les 4 installateurs sur le NAS (uniquement si le lecteur K: du NAS est présent).</summary>
    public static async Task MirrorToNasAsync(UpdateInfo u)
    {
        try
        {
            if (!Directory.Exists(NasFolder)) return;
            var dir = Path.Combine(NasFolder, "KSHPLAYER");
            var stamp = Path.Combine(dir, "version.txt");
            if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == u.Version.ToString(3)) return;

            foreach (var (key, sub) in new[] { ("windows", "Windows"), ("windows_pro", "Windows"), ("mac", "Mac"), ("mac_pro", "Mac") })
            {
                if (!u.Files.TryGetProperty(key, out var f) || f.GetString() is not { } name) continue;
                var tmp = await DownloadAsync(name, null);
                var target = Path.Combine(dir, sub);
                Directory.CreateDirectory(target);
                File.Copy(tmp, Path.Combine(target, name), true);
                File.Delete(tmp);
            }
            File.WriteAllText(stamp, u.Version.ToString(3));
            AppLog.Write($"NAS : installateurs {u.Version.ToString(3)} copiés dans {dir}");
        }
        catch (Exception ex)
        {
            AppLog.Write("Copie sur le NAS impossible", ex);
        }
    }
}
