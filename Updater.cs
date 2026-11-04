using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KSHPLAYER.Player;

namespace KSHPLAYER;

/// <summary>Nouvelle version publiée (lue dans version.json sur GitHub).</summary>
public sealed class UpdateInfo
{
    public UpdateInfo(Version version, string notes, string setupFile, Dictionary<string, string> files)
    {
        Version = version; Notes = notes; SetupFile = setupFile; Files = files;
    }
    public Version Version { get; }
    public string Notes { get; }
    public string SetupFile { get; }
    public Dictionary<string, string> Files { get; }
}

/// <summary>
/// Mises à jour : GitHub publie installateurs + .dmg + version.json sur la branche « telechargements ».
/// Depuis 1.2 (plus d'écran d'accueil) : pendant la lecture, le lecteur vérifie s'il existe une version plus
/// récente, télécharge l'installateur en arrière-plan, le signale par un petit message dans la vidéo,
/// et l'installe quand on ferme KSHPLAYER (jamais en pleine vidéo).
/// Sur le PC de KSHMIRE (si K:\Backup\Logiciel KSHPC existe), il recopie aussi les 4 fichiers sur le NAS.
/// </summary>
public static class Updater
{
    public const string BaseUrl = "https://raw.githubusercontent.com/KSHMIRE1/KSHPLAYER/telechargements";
    public const string NasFolder = @"K:\Backup\Logiciel KSHPC";

    private static readonly HttpClient Quick;
    private static readonly HttpClient Big;

    static Updater()
    {
        // .NET Framework 4.8 laisse Windows choisir (TLS 1.2 / 1.3) ; si un réglage ancien l'en empêche, TLS 1.2 au minimum
        if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        Quick = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        Big = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public static Version Current => Norm(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));
    private static Version Norm(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var json = await Quick.GetStringAsync($"{BaseUrl}/version.json?t={DateTime.UtcNow.Ticks}").ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception ex)
        {
            AppLog.Write("Vérification des mises à jour impossible", ex);
            return null;
        }
    }

    /// <summary>Lit version.json (écrit par .github/version_json.py) sans dépendance JSON.</summary>
    public static UpdateInfo? Parse(string json)
    {
        var version = JsonString(json, "version");
        if (version == null || !Version.TryParse(version, out var v)) return null;
        var files = new Dictionary<string, string>();
        foreach (var key in new[] { "windows", "windows_pro", "mac", "mac_pro" })
        {
            var f = JsonString(json, key);
            if (f != null) files[key] = f;
        }
        if (!files.TryGetValue(Edition.IsPro ? "windows_pro" : "windows", out var setup)) return null;
        return new UpdateInfo(Norm(v), JsonString(json, "notes") ?? "", setup, files);
    }

    private static string? JsonString(string json, string key)
    {
        var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success) return null;
        return Regex.Replace(m.Groups[1].Value, @"\\(u[0-9a-fA-F]{4}|.)", e =>
        {
            var c = e.Groups[1].Value;
            if (c.Length == 5) return ((char)Convert.ToInt32(c.Substring(1), 16)).ToString();
            switch (c[0]) { case 'n': return "\n"; case 't': return "\t"; case 'r': return "\r"; default: return c; }
        });
    }

    public static bool IsNewer(UpdateInfo u) => u.Version > Current;

    /// <summary>
    /// Télécharge un fichier publié dans %TEMP%\&lt;dossier&gt; (« .part » tant que ce n'est pas fini). Un téléchargement
    /// interrompu (lecteur refermé avant la fin) reprend là où il s'était arrêté à la session suivante.
    /// <paramref name="version"/> sert de clé de cache : juste après une publication, GitHub ne peut pas resservir
    /// l'ancien fichier, et on ne recolle jamais deux versions différentes.
    /// </summary>
    public static async Task<string> DownloadAsync(string fileName, string version, IProgress<double>? progress, string folder = "KSHPLAYER-maj")
    {
        var dir = Path.Combine(Path.GetTempPath(), folder);
        var path = Path.Combine(dir, fileName);
        var part = path + ".part";
        var tag = Path.Combine(dir, fileName + ".version");
        Directory.CreateDirectory(dir);
        long have = 0;
        if (File.Exists(part))
        {
            if (File.Exists(tag) && File.ReadAllText(tag).Trim() == version) have = new FileInfo(part).Length;
            else File.Delete(part);             // morceau d'une autre version : on repart de zéro
        }
        File.WriteAllText(tag, version);

        using (var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{Uri.EscapeDataString(fileName)}?v={Uri.EscapeDataString(version)}"))
        {
            if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
            using (var resp = await Big.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (have > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // le morceau était déjà complet
                }
                else
                {
                    resp.EnsureSuccessStatusCode();
                    bool resume = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                    if (!resume) have = 0;
                    long total = resp.Content.Headers.ContentLength is long len ? have + len : -1, done = have;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        var buf = new byte[1 << 16];
                        int read;
                        while ((read = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, read).ConfigureAwait(false);
                            done += read;
                            if (total > 0) progress?.Report((double)done / total);
                        }
                    }
                    if (total > 0 && new FileInfo(part).Length != total) throw new IOException("Téléchargement incomplet : " + fileName);
                }
            }
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(part, path);
        try { File.Delete(tag); } catch { }
        return path;
    }

    /// <summary>Version écrite dans un installateur téléchargé (FileVersion), null si illisible.</summary>
    public static Version? InstallerVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return v != null && Version.TryParse(v, out var parsed) ? Norm(parsed) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// L'installateur de cette version, prêt dans %TEMP%\KSHPLAYER-maj : déjà là (session précédente) ou téléchargé.
    /// On vérifie sa version : jamais un ancien installateur servi par un cache.
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(UpdateInfo u)
    {
        var ready = Path.Combine(Path.GetTempPath(), "KSHPLAYER-maj", u.SetupFile);
        if (File.Exists(ready) && InstallerVersion(ready) == u.Version) return ready;
        var path = await DownloadAsync(u.SetupFile, u.Version.ToString(3), null).ConfigureAwait(false);
        if (InstallerVersion(path) != u.Version)
        {
            File.Delete(path);
            throw new InvalidDataException($"L'installateur téléchargé n'est pas la version {u.Version.ToString(3)}.");
        }
        return path;
    }

    /// <summary>
    /// Lance l'installateur en mode mise à jour. Il attend la fin de ce lecteur (/pid), puis que plus aucune
    /// vidéo ne soit ouverte (il ne coupe jamais une vidéo), installe en reprenant les choix précédents et se referme.
    /// </summary>
    public static void LaunchInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/update /pid=" + Process.GetCurrentProcess().Id) { UseShellExecute = true });

    /// <summary>Recopie les 4 installateurs sur le NAS (uniquement si le lecteur K: du NAS est présent).</summary>
    public static async Task MirrorToNasAsync(UpdateInfo u)
    {
        try
        {
            if (!Directory.Exists(NasFolder)) return;
            var dir = Path.Combine(NasFolder, "KSHPLAYER");
            var stamp = Path.Combine(dir, "version.txt");
            if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == u.Version.ToString(3)) return;

            foreach (var pair in new[] { ("windows", "Windows"), ("windows_pro", "Windows"), ("mac", "Mac"), ("mac_pro", "Mac") })
            {
                if (!u.Files.TryGetValue(pair.Item1, out var name)) continue;
                // dossier à part : jamais le même fichier que la mise à jour du lecteur
                var tmp = await DownloadAsync(name, u.Version.ToString(3), null, "KSHPLAYER-nas").ConfigureAwait(false);
                var target = Path.Combine(dir, pair.Item2);
                Directory.CreateDirectory(target);
                // copie en « .part » puis renommage : jamais d'installateur tronqué sur le NAS
                var final = Path.Combine(target, name);
                File.Copy(tmp, final + ".part", true);
                if (File.Exists(final)) File.Delete(final);
                File.Move(final + ".part", final);
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

/// <summary>
/// Mise à jour pendant la lecture : vérification + téléchargement en arrière-plan, message dans la vidéo,
/// installation à la fermeture de KSHPLAYER.
/// </summary>
internal sealed class UpdateOnClose
{
    private readonly MpvPlayer _player;
    private volatile string? _ready;

    public UpdateOnClose(MpvPlayer player) { _player = player; }

    public void Start()
    {
        Task.Run(async () =>
        {
            try
            {
                var u = await Updater.CheckAsync().ConfigureAwait(false);
                if (u == null) return;
                _ = Task.Run(() => Updater.MirrorToNasAsync(u));
                if (!Updater.IsNewer(u)) return;
                AppLog.Write($"Mise à jour {u.Version.ToString(3)} disponible : téléchargement en arrière-plan");
                var path = await Updater.DownloadInstallerAsync(u).ConfigureAwait(false);
                _ready = path;
                _player.Toast($"Mise à jour {u.Version.ToString(3)} prête  ·  elle s'installera à la fermeture", 8);
            }
            catch (Exception ex) { AppLog.Write("Mise à jour impossible", ex); }
        });
    }

    /// <summary>À la fermeture du lecteur : lance l'installateur si la mise à jour est prête.</summary>
    public void InstallIfReady()
    {
        var path = _ready;
        if (path == null || !File.Exists(path)) return;
        try
        {
            AppLog.Write("Installation de la mise à jour : " + path);
            Updater.LaunchInstaller(path);
        }
        catch (Exception ex) { AppLog.Write("Lancement de la mise à jour impossible", ex); }
    }
}
