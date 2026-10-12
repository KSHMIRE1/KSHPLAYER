using System;
using System.IO;

namespace KSHPLAYER;

/// <summary>Journal simple dans %LOCALAPPDATA%\KSHPLAYER pour diagnostiquer les soucis.</summary>
public static class AppLog
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KSHPLAYER");
    public static string MpvLogPath => Path.Combine(Folder, "mpv.log");
    private static string AppLogPath => Path.Combine(Folder, "kshplayer.log");

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(AppLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{(ex != null ? Environment.NewLine + ex : "")}{Environment.NewLine}");
        }
        catch { /* le journal ne doit jamais faire planter l'appli */ }
    }
}
