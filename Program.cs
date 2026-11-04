using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using KSHPLAYER.Player;

namespace KSHPLAYER;

/// <summary>
/// KSHPLAYER.exe / KSHPLAYERPRO.exe. Depuis 1.2, plus d'écran d'accueil :
///   KSHPLAYER.exe "&lt;fichier&gt;" [--start=&lt;secondes&gt;]  → la vidéo s'ouvre tout de suite ; la fermer ferme KSHPLAYER.
///   KSHPLAYER.exe (menu Démarrer, Bureau)          → fenêtre « Ouvrir » de Windows, puis la vidéo choisie.
/// La vidéo s'affiche dans la fenêtre sans bord de mpv, avec la barre kshosc.lua. Ce fil ne fait qu'attendre
/// la fermeture ; les mises à jour se préparent pendant la lecture et s'installent à la fermeture.
/// </summary>
internal static class Program
{
    private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Erreur fatale", e.ExceptionObject as Exception);

        string? file = null;
        double? start = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--start=", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(a.Substring(8), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) start = t;
            }
            else if (file == null && !a.StartsWith("--")) file = a;
        }

        if (file == null)
        {
            file = Dialogs.PickVideo();      // lancé sans fichier : fenêtre « Ouvrir » de Windows
            if (file == null) return 0;      // annulé : rien d'autre à faire
        }
        try { file = Path.GetFullPath(file); } catch { /* lien (http, smb…) : tel quel */ }

        return Play(file, start);
    }

    private static int Play(string path, double? start)
    {
        MpvPlayer player;
        try
        {
            player = new MpvPlayer(Path.Combine(AppDir, "mpv"), HardwareProfile.Detect());
        }
        catch (DllNotFoundException ex)
        {
            AppLog.Write("Moteur introuvable", ex);
            Dialogs.Error("Le moteur vidéo de " + Edition.Name + " (libmpv-2.dll) est introuvable.\n\nRéinstalle " + Edition.Name + ".");
            return 1;
        }
        catch (Exception ex)
        {
            AppLog.Write("Démarrage du moteur impossible", ex);
            Dialogs.Error(Edition.Name + " n'a pas pu démarrer :\n" + ex.Message);
            return 1;
        }

        using (var closed = new ManualResetEventSlim())
        {
            player.Shutdown += closed.Set;
            if (!Open(player, path, start))
            {
                player.Dispose();
                player.WaitClosed(3000);
                return 1;
            }

            var update = new UpdateOnClose(player);
            update.Start();

            closed.Wait();                   // la vidéo est refermée (croix, Échap, fin du film…)
            AppLog.Write("Fermeture de " + Edition.Name);
            player.Dispose();
            update.InstallIfReady();
            player.WaitClosed(3000);
        }
        return 0;
    }

    // ---------- Ouverture : vidéo, ou séquence d'images pour la PRO ----------

    private static readonly string[] SequenceExts = { "exr", "dpx", "tif", "tiff", "png", "jpg", "jpeg", "tga" };

    private static bool Open(MpvPlayer player, string path, double? start)
    {
        try
        {
            if (Edition.IsPro && Directory.Exists(path))
            {
                // un dossier = une séquence d'images (l'extension la plus nombreuse)
                var best = SequenceExts
                    .Select(e => (e, n: Directory.EnumerateFiles(path, "*." + e).Count()))
                    .OrderByDescending(x => x.n).First();
                if (best.n < 2)
                {
                    Dialogs.Error("Aucune séquence d'images dans ce dossier :\n" + path);
                    return false;
                }
                AppLog.Write($"Séquence : {path} ({best.n} × {best.e})");
                player.LoadSequence(path, best.e);
                return true;
            }

            if (Edition.IsPro && File.Exists(path) && SequenceOf(path) is { } seq)
            {
                // une image numérotée (plan_010_0001.exr) : toute la séquence dont elle fait partie
                AppLog.Write($"Séquence : {seq.folder} ({seq.prefix}*.{seq.ext}, {seq.count} images)");
                player.LoadSequence(seq.folder, seq.ext, seq.prefix);
                return true;
            }

            AppLog.Write("Ouverture : " + path + (start is > 0 ? $" (à {start:0.#} s)" : ""));
            player.Load(path, start);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Lecture impossible : " + path, ex);
            Dialogs.Error("Lecture impossible :\n" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// « plan_010_0001.exr » → toutes les images « plan_010_*.exr » numérotées du même dossier (au moins 2),
    /// sinon null (une image seule s'ouvre comme une image).
    /// </summary>
    private static (string folder, string prefix, string ext, int count)? SequenceOf(string file)
    {
        var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
        if (!SequenceExts.Contains(ext)) return null;
        var m = Regex.Match(Path.GetFileNameWithoutExtension(file), @"^(.*?)(\d+)$");
        if (!m.Success) return null;
        var folder = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(folder)) return null;
        string prefix = m.Groups[1].Value;
        var numbered = new Regex("^" + Regex.Escape(prefix) + @"\d+$", RegexOptions.IgnoreCase);
        int count = Directory.EnumerateFiles(folder!, prefix + "*." + ext)
            .Count(f => numbered.IsMatch(Path.GetFileNameWithoutExtension(f)));
        return count >= 2 ? (folder!, prefix, ext, count) : null;
    }
}
