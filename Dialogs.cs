using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace KSHPLAYER;

/// <summary>
/// Les deux seules fenêtres de KSHPLAYER en dehors de la vidéo : « Ouvrir » (lancé sans fichier) et un message
/// en cas de problème. Classe à part : WinForms n'est chargé que si l'une d'elles sert.
/// </summary>
internal static class Dialogs
{
    private static readonly string[] Videos =
    {
        "mkv", "mk3d", "mp4", "m4v", "mov", "qt", "avi", "divx", "wmv", "asf", "webm", "ts", "m2ts", "mts", "m2t",
        "mpg", "mpeg", "m2v", "vob", "flv", "f4v", "ogv", "3gp", "3g2", "rm", "rmvb", "mxf", "y4m", "dv",
        "hevc", "h264", "h265", "264", "265", "ivf", "wtv", "iso",
    };
    private static readonly string[] Images = { "exr", "dpx", "tif", "tiff", "png", "jpg", "jpeg", "tga" };

    private static bool _styles;

    private static void Styles()
    {
        if (_styles) return;
        _styles = true;
        Application.EnableVisualStyles();   // fenêtre « Ouvrir » moderne de Windows (et non l'ancienne)
    }

    /// <summary>
    /// Fenêtre invisible mais présente dans la barre des tâches (icône et nom KSHPLAYER) : sans elle, la fenêtre
    /// « Ouvrir » n'y aurait pas de bouton, et passée derrière une autre fenêtre on ne la retrouverait plus.
    /// </summary>
    private static Form Owner()
    {
        var f = new Form
        {
            Text = Edition.Name,
            ShowInTaskbar = true,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(1, 1),
            Opacity = 0,
        };
        try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        f.Show();
        f.Activate();
        return f;
    }

    /// <summary>Fenêtre « Ouvrir » de Windows ; null si on annule.</summary>
    public static string? PickVideo()
    {
        try
        {
            Styles();
            string Mask(string[] exts) => string.Join(";", exts.Select(e => "*." + e));
            using (var owner = Owner())
            using (var dlg = new OpenFileDialog
            {
                Title = "Ouvrir une vidéo  ·  " + Edition.Name,
                Filter = Edition.IsPro
                    ? $"Vidéos et séquences d'images|{Mask(Videos)};{Mask(Images)}|Vidéos|{Mask(Videos)}|Séquences d'images (une image du plan)|{Mask(Images)}|Tous les fichiers|*.*"
                    : $"Vidéos|{Mask(Videos)}|Tous les fichiers|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                AutoUpgradeEnabled = true,
            })
            {
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Fenêtre « Ouvrir » impossible", ex);
            return null;
        }
    }

    public static void Error(string message)
    {
        try
        {
            Styles();
            using (var owner = Owner())
                MessageBox.Show(owner, message, Edition.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { AppLog.Write("Message impossible : " + message, ex); }
    }
}
