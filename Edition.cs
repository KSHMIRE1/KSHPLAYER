using System;
using System.IO;

namespace KSHPLAYER;

/// <summary>
/// Les deux éditions sortent du même code :
///   - KSHPLAYER      : configurations Debug / Release
///   - KSHPLAYER PRO  : configurations DebugPro / ReleasePro (symbole KSHPRO) — outils pour monteurs
/// </summary>
public static class Edition
{
#if KSHPRO
    public const bool IsPro = true;
    public const string Name = "KSHPLAYER PRO";
#else
    public const bool IsPro = false;
    public const string Name = "KSHPLAYER";
#endif

    /// <summary>Dossier des LUT .cube (PRO) : Documents\KSHPLAYER PRO\LUTs</summary>
    public static string LutFolder => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KSHPLAYER PRO", "LUTs"));

    /// <summary>Dossier des captures (PRO) : Images\KSHPLAYER PRO</summary>
    public static string CaptureFolder => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KSHPLAYER PRO"));

    private static string Ensure(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
}
