namespace KSHPLAYER.Setup
{
    /// <summary>Ce qui change entre l'installateur normal et l'installateur PRO.</summary>
    internal static class Product
    {
#if KSHPRO
        public const bool IsPro = true;
        public const string Name = "KSHPLAYER PRO";
        public const string Exe = "KSHPLAYERPRO.exe";
        public const string Key = "KSHPLAYERPRO";
        public const string Tagline = "Le lecteur des monteurs : timecode, image par image, LUT, repères de cadrage.";
#else
        public const bool IsPro = false;
        public const string Name = "KSHPLAYER";
        public const string Exe = "KSHPLAYER.exe";
        public const string Key = "KSHPLAYER";
        public const string Tagline = "Le lecteur qui lit tout, en 4K HDR, sans prise de tête.";
#endif
        public static readonly string Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        public const string Publisher = "KSHMIRE";

        /// <summary>Descriptif affiché à l'ouverture de l'installateur : (titre, détail).</summary>
        public static readonly string[][] Features =
        {
            new[] { "Lit absolument tout",
                    "MKV, MP4, AVI, MOV, TS/M2TS, WebM… en H.264, HEVC, AV1, VVC/H.266, VP9, MPEG-2, VC-1 et des centaines d'autres." },
            new[] { "4K HDR et Dolby Vision",
                    "HDR10, HDR10+, HLG et Dolby Vision (profils 5, 7 et 8) avec des couleurs justes, même sur un écran SDR." },
            new[] { "Réglé selon ton PC",
                    "Détecte ta carte graphique et choisit le meilleur mode : Qualité max, Équilibré ou Léger pour les portables." },
            new[] { "Son de cinéma",
                    "Dolby TrueHD / Atmos, DTS-HD MA / DTS:X, E-AC-3… parfaitement ramenés en stéréo pour tes haut-parleurs." },
            new[] { "Sous-titres et pistes",
                    "SRT, ASS, PGS (Blu-ray), VobSub… choix des pistes audio et sous-titres en un clic, français en priorité." },
            new[] { "Que la vidéo à l'écran",
                    "Fenêtre sans bord ni bandes noires, barre de contrôle KSHFLIX qui n'apparaît qu'en approchant du bas." },
            new[] { "DVD, Blu-ray et flux",
                    "Dossiers / ISO de DVD et Blu-ray non protégés, liens HLS, DASH, RTSP, SMB, SFTP." },
#if KSHPRO
            new[] { "PRO · Timecode",
                    "Affichage HH:MM:SS:II avec le timecode de départ des rushs." },
            new[] { "PRO · Image par image et J-K-L",
                    "← → image par image, jog à la molette, marche avant / arrière x2 x4 x8 comme dans Premiere et Resolve." },
            new[] { "PRO · Entrée / Sortie",
                    "Points I / O affichés sur la timeline et lecture en boucle du passage." },
            new[] { "PRO · LUT .cube",
                    "Applique tes LUT (Log → Rec.709) en direct, rangées dans Documents\\KSHPLAYER PRO\\LUTs." },
            new[] { "PRO · Infos techniques",
                    "Codec, débit, 4:2:2 / 4:2:0, 8-10-12 bits, espace colorimétrique, HDR, audio, images perdues." },
            new[] { "PRO · Repères de cadrage",
                    "Zones de sécurité, 2.39, 1.85, 9:16, 4:5 et règle des tiers." },
            new[] { "PRO · Captures et séquences",
                    "Capture PNG pleine qualité nommée au timecode, lecture des séquences EXR, DPX, TIFF, PNG." },
            new[] { "PRO · Formats pro",
                    "ProRes (et ProRes RAW), DNxHD / DNxHR, CineForm, MXF des caméras Sony, Canon, Panasonic." },
#endif
        };

        /// <summary>Extensions ajoutées au menu « Ouvrir avec ».</summary>
        public static readonly string[] VideoExtensions =
        {
            ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg",
            ".vob", ".flv", ".ogv", ".3gp", ".rmvb", ".mxf", ".asf", ".divx", ".y4m",
        };
    }
}
