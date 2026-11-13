using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using static KSHPLAYER.Player.MpvNative;

namespace KSHPLAYER;

/// <summary>
/// Détection des génériques de séries (« Passer l'intro », « Épisode suivant »), façon Netflix / Jellyfin.
///
/// Principe : l'intro d'une série a la même musique à chaque épisode. On compare le son du début (7 min) de deux
/// épisodes voisins : le plus long passage identique (15 s à 3 min) est l'intro. Pareil sur les 4 dernières minutes
/// pour le générique de fin. Aucun service en ligne, aucune base de données : tout se fait sur le PC.
///
/// Lancé en arrière-plan par la barre (kshosc.lua), priorité basse :
///   KSHPLAYER.exe --intros "&lt;épisode&gt;" "&lt;précédent ou vide&gt;" "&lt;suivant ou vide&gt;"
/// Analyse l'épisode en cours et prépare le suivant (prêt avant qu'on y arrive). Résultats dans
/// %LOCALAPPDATA%\KSHPLAYER\intros.json (lu par la barre), empreintes sonores dans %LOCALAPPDATA%\KSHPLAYER\empreintes
/// (chaque épisode n'est lu qu'une fois).
/// </summary>
internal static class Intros
{
    // ---------- Réglages ----------
    private const int Rate = 8000;              // le son est ramené en mono 8 kHz : bien assez pour reconnaître une musique
    private const int Win = 2048;               // fenêtre d'analyse : 0,256 s
    private const int Hop = 800;                // une empreinte toutes les 0,1 s
    private const double FrameSec = (double)Hop / Rate;
    private const double HeadSec = 420;         // début analysé : 7 min (l'intro arrive presque toujours avant)
    private const double TailSec = 240;         // fin analysée : 4 min
    private const int MinFrames = 150;          // passage commun d'au moins 15 s
    private const int MaxIntroFrames = 1800;    // une intro dure au plus 3 min
    private const int MaxGap = 20;              // trous tolérés dans un passage commun (2 s : bruitages, dialogues par-dessus)
    private const int MaxBits = 10;             // empreintes « identiques » : au plus 10 bits différents sur 32
    private const int FpVersion = 1;

    public static readonly string DbPath = Path.Combine(AppLog.Folder, "intros.json");
    private static readonly string FpFolder = Path.Combine(AppLog.Folder, "empreintes");

    public static int Run(string[] args)
    {
        string cur = args.Length > 0 ? args[0] : "", prev = args.Length > 1 ? args[1] : "", next = args.Length > 2 ? args[2] : "";
        if (cur == "") return 2;
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        // une seule analyse à la fois (on passe vite d'un épisode à l'autre) : la suivante attend son tour
        using var gate = new Mutex(false, @"Local\KSHPLAYER-Intros");
        bool owned = false;
        try
        {
            try { owned = gate.WaitOne(TimeSpan.FromMinutes(5)); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) return 3;

            var db = LoadDb();
            bool changed = false;
            if (!Known(db, cur))
                changed |= Analyze(db, cur, prev != "" ? prev : next);
            if (next != "" && !Known(db, next))
                changed |= Analyze(db, next, cur);
            if (changed) SaveDb(db);
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("Détection des génériques : " + cur, ex);
            return 1;
        }
        finally
        {
            if (owned) gate.ReleaseMutex();
        }
    }

    // ---------- Analyse d'un épisode avec un épisode voisin ----------

    internal sealed class Entry
    {
        public long Size;
        public double[]? Intro;     // [début, fin] en secondes
        public double[]? Credits;
    }

    private static bool Known(Dictionary<string, Entry> db, string file) =>
        db.TryGetValue(Key(file), out var e) && e.Size == SizeOf(file);

    private static bool Analyze(Dictionary<string, Entry> db, string file, string partner)
    {
        if (partner == "" || !File.Exists(file) || !File.Exists(partner)) return false;
        var a = GetPrint(file);
        var b = GetPrint(partner);
        if (a == null || b == null) return false;

        var ea = new Entry { Size = SizeOf(file) };
        var eb = db.TryGetValue(Key(partner), out var old) && old.Size == SizeOf(partner) ? old : new Entry { Size = SizeOf(partner) };

        var head = Match(a.Head, b.Head, MaxIntroFrames);
        if (head is { } h)
        {
            ea.Intro = Span(a.HeadStart, h.a0, h.a1);
            eb.Intro ??= Span(b.HeadStart, h.b0, h.b1);
        }
        var tail = Match(a.Tail, b.Tail, int.MaxValue);
        if (tail is { } t)
        {
            ea.Credits = Span(a.TailStart, t.a0, t.a1);
            eb.Credits ??= Span(b.TailStart, t.b0, t.b1);
        }
        db[Key(file)] = ea;
        db[Key(partner)] = eb;
        AppLog.Write($"Génériques de {Path.GetFileName(file)} : intro {Show(ea.Intro)}, fin {Show(ea.Credits)} (comparé à {Path.GetFileName(partner)})");
        return true;
    }

    // centre des fenêtres d'analyse
    private static double[] Span(double start, int f0, int f1) =>
        new[] { Math.Round(start + f0 * FrameSec + 0.128, 2), Math.Round(start + f1 * FrameSec + 0.128, 2) };

    private static string Show(double[]? s) =>
        s == null ? "aucune" : string.Format(CultureInfo.InvariantCulture, "{0:0.0}–{1:0.0} s", s[0], s[1]);

    // ---------- Comparaison : le plus long passage commun, quel que soit le décalage ----------

    internal static (int a0, int a1, int b0, int b1)? Match(Fp[] A, Fp[] B, int maxFrames)
    {
        int bestLen = 0;
        (int, int, int, int)? best = null;
        var sum = new int[A.Length + 1];                          // sum[k] = nombre d'images identiques avant k
        for (int d = -(B.Length - 1); d < A.Length; d++)       // A[i] face à B[i - d]
        {
            int i0 = Math.Max(0, d), i1 = Math.Min(A.Length, B.Length + d), len = i1 - i0;
            if (len < MinFrames) continue;
            for (int k = 0; k < len; k++)
            {
                var x = A[i0 + k]; var y = B[i0 + k - d];
                sum[k + 1] = sum[k] + (x.Ok && y.Ok && PopCount(x.Hash ^ y.Hash) <= MaxBits ? 1 : 0);
            }
            if (sum[len] * 2 < MinFrames) continue;
            int Hits(int from, int to) => sum[Math.Min(len, Math.Max(0, to))] - sum[Math.Min(len, Math.Max(0, from))];
            bool Ok(int k) => sum[k + 1] != sum[k];

            int pos = 0;
            while (pos < len)
            {
                if (!Ok(pos)) { pos++; continue; }
                int s0 = pos, last = pos;
                for (pos++; pos < len && pos - last <= MaxGap; pos++)
                    if (Ok(pos)) last = pos;
                // bords nets : un début (une fin) doit être suivi (précédé) d'un passage dense, 6 images sur 10,
                // sinon une ressemblance due au hasard juste avant l'intro l'allongerait de 1 à 2 s
                while (s0 < last && Hits(s0, s0 + 10) < 6) { s0++; while (s0 < last && !Ok(s0)) s0++; }
                while (last > s0 && Hits(last - 9, last + 1) < 6) { last--; while (last > s0 && !Ok(last)) last--; }
                int n = last - s0 + 1;
                if (n >= MinFrames && n <= maxFrames && Hits(s0, last + 1) * 2 >= n && n > bestLen)
                {
                    bestLen = n;
                    best = (i0 + s0, i0 + last, i0 + s0 - d, i0 + last - d);
                }
            }
        }
        return best;
    }

    private static int PopCount(uint v)
    {
        v -= (v >> 1) & 0x55555555u;
        v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
        return (int)(((v + (v >> 4)) & 0x0F0F0F0Fu) * 0x01010101u >> 24);
    }

    // ---------- Empreinte sonore (32 bits toutes les 0,1 s, façon Shazam / Chromaprint) ----------

    internal struct Fp { public uint Hash; public bool Ok; }

    internal sealed class Print
    {
        public double HeadStart, TailStart;
        public Fp[] Head = Array.Empty<Fp>(), Tail = Array.Empty<Fp>();
    }

    private static Print? GetPrint(string file)
    {
        var cache = Path.Combine(FpFolder, Hash(Key(file)) + "-" + SizeOf(file) + ".fp");
        var p = ReadPrint(cache);
        if (p != null) return p;

        var sw = Stopwatch.StartNew();
        var head = Decode(file, 0, HeadSec, out double dur);
        if (head == null || dur <= 0) return null;
        p = new Print { HeadStart = 0, Head = Fingerprint(head) };
        // fin : seulement si l'épisode est assez long pour ne pas relire le début
        double tailLen = Math.Min(TailSec, dur * 0.3);
        if (dur > HeadSec + 60 && tailLen >= 30)
        {
            p.TailStart = Math.Max(0, dur - tailLen);
            var tail = Decode(file, p.TailStart, tailLen, out _);
            if (tail != null) p.Tail = Fingerprint(tail);
        }
        AppLog.Write($"Empreinte de {Path.GetFileName(file)} : {sw.Elapsed.TotalSeconds:0.0} s");
        WritePrint(cache, p);
        return p;
    }

    internal static Fp[] Fingerprint(short[] pcm)
    {
        int frames = pcm.Length < Win ? 0 : (pcm.Length - Win) / Hop + 1;
        var fps = new Fp[frames];
        if (frames == 0) return fps;

        // 33 bandes de fréquence (300 Hz → 3 kHz, échelle logarithmique) → 32 bits par image
        const int Bands = 33;
        var edges = new int[Bands + 1];
        for (int i = 0; i <= Bands; i++)
            edges[i] = (int)Math.Round(300 * Math.Pow(3000.0 / 300, (double)i / Bands) * Win / Rate);
        var hann = new double[Win];
        for (int i = 0; i < Win; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Win - 1));

        var re = new double[Win]; var im = new double[Win];
        var prev = new double[Bands]; var cur = new double[Bands];
        bool prevLoud = false;
        for (int f = 0; f < frames; f++)
        {
            int off = f * Hop;
            double energy = 0;
            for (int i = 0; i < Win; i++)
            {
                double v = pcm[off + i];
                energy += v * v;
                re[i] = v * hann[i]; im[i] = 0;
            }
            bool loud = Math.Sqrt(energy / Win) > 120;   // le silence (écran noir) ne compte pas : il ressemble à tous les silences
            Fft(re, im);
            for (int b = 0; b < Bands; b++)
            {
                double e = 0;
                for (int k = edges[b]; k < Math.Max(edges[b] + 1, edges[b + 1]); k++) e += re[k] * re[k] + im[k] * im[k];
                cur[b] = Math.Log10(e + 1);
            }
            uint h = 0;
            for (int m = 0; m < 32; m++)
                if ((cur[m] - cur[m + 1]) - (prev[m] - prev[m + 1]) > 0) h |= 1u << m;
            fps[f] = new Fp { Hash = h, Ok = f > 0 && loud && prevLoud };
            prevLoud = loud;
            var t = prev; prev = cur; cur = t;
        }
        return fps;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    // ---------- Décodage du son par le moteur (libmpv), sans image, aussi vite que possible ----------

    private static short[]? Decode(string file, double start, double length, out double duration)
    {
        duration = 0;
        var pcmFile = Path.Combine(Path.GetTempPath(), $"kshplayer-intro-{Process.GetCurrentProcess().Id}.pcm");
        var ctx = mpv_create();
        if (ctx == IntPtr.Zero) return null;
        try
        {
            void O(string k, string v) => mpv_set_option_string(ctx, k, v);
            O("config", "no");
            O("terminal", "no");
            O("load-scripts", "no");
            O("ytdl", "no");
            O("osc", "no");
            O("input-default-bindings", "no");
            O("idle", "no");
            O("vid", "no");                       // pas d'image : seulement le son
            O("sid", "no");
            O("alang", "fr,fre,fra,en,eng");      // même piste que le lecteur
            O("ao", "pcm");
            O("ao-pcm-file", pcmFile);
            O("ao-pcm-waveheader", "no");
            O("audio-format", "s16");
            O("audio-channels", "mono");
            O("audio-samplerate", Rate.ToString(CultureInfo.InvariantCulture));
            O("untimed", "yes");                  // aussi vite que le disque le permet
            O("hr-seek", "yes");
            O("start", start.ToString("0.###", CultureInfo.InvariantCulture));
            O("length", length.ToString("0.###", CultureInfo.InvariantCulture));
            if (mpv_initialize(ctx) < 0) return null;
            if (!LoadFile(ctx, file)) return null;

            var sw = Stopwatch.StartNew();
            bool ended = false;
            while (!ended && sw.Elapsed < TimeSpan.FromMinutes(4))
            {
                var p = mpv_wait_event(ctx, 1);
                if (p == IntPtr.Zero) continue;
                var ev = (MpvEvent)Marshal.PtrToStructure(p, typeof(MpvEvent))!;
                if (ev.EventId == MPV_EVENT_FILE_LOADED)
                {
                    var d = mpv_get_property_string(ctx, "duration");
                    if (d != IntPtr.Zero)
                    {
                        double.TryParse(Utf8(d), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                        mpv_free(d);
                    }
                }
                else if (ev.EventId == MPV_EVENT_END_FILE || ev.EventId == MPV_EVENT_SHUTDOWN) ended = true;
            }
        }
        finally
        {
            mpv_terminate_destroy(ctx);          // le fichier son est refermé ici
        }
        try
        {
            if (!File.Exists(pcmFile)) return null;
            var bytes = File.ReadAllBytes(pcmFile);
            var pcm = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
            return pcm.Length >= Rate * 20 ? pcm : null;   // moins de 20 s de son : rien à comparer
        }
        finally
        {
            try { File.Delete(pcmFile); } catch { }
        }
    }

    private static bool LoadFile(IntPtr ctx, string file)
    {
        var args = new[] { "loadfile", file };
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var b = Encoding.UTF8.GetBytes(args[i] + "\0");
                ptrs[i] = Marshal.AllocHGlobal(b.Length);
                Marshal.Copy(b, 0, ptrs[i], b.Length);
            }
            var arr = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
            try
            {
                Marshal.Copy(ptrs, 0, arr, ptrs.Length);
                return mpv_command(ctx, arr) >= 0;
            }
            finally { Marshal.FreeHGlobal(arr); }
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    private static string Utf8(IntPtr p)
    {
        int n = 0;
        while (Marshal.ReadByte(p, n) != 0) n++;
        var b = new byte[n];
        Marshal.Copy(p, b, 0, n);
        return Encoding.UTF8.GetString(b);
    }

    // ---------- Cache des empreintes ----------

    private static Print? ReadPrint(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != FpVersion) return null;
            var p = new Print { HeadStart = r.ReadDouble(), TailStart = r.ReadDouble() };
            p.Head = ReadFps(r);
            p.Tail = ReadFps(r);
            return p;
        }
        catch { return null; }
    }

    private static Fp[] ReadFps(BinaryReader r)
    {
        var a = new Fp[r.ReadInt32()];
        for (int i = 0; i < a.Length; i++) a[i] = new Fp { Hash = r.ReadUInt32(), Ok = r.ReadBoolean() };
        return a;
    }

    private static void WritePrint(string path, Print p)
    {
        try
        {
            Directory.CreateDirectory(FpFolder);
            using var w = new BinaryWriter(File.Create(path));
            w.Write(FpVersion); w.Write(p.HeadStart); w.Write(p.TailStart);
            foreach (var arr in new[] { p.Head, p.Tail })
            {
                w.Write(arr.Length);
                foreach (var f in arr) { w.Write(f.Hash); w.Write(f.Ok); }
            }
        }
        catch (Exception ex) { AppLog.Write("Empreinte non enregistrée", ex); }
    }

    // ---------- intros.json (lu aussi par kshosc.lua) ----------
    // {"v":1,"files":{
    // "c:\\series\\show\\s01e01.mkv":{"size":123,"intro":[61.2,151.3],"credits":null},
    // ...}}
    // Clé : chemin avec « \ » et lettres A-Z en minuscules (même règle des deux côtés).

    internal static string Key(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (char c in path)
            sb.Append(c == '/' ? '\\' : (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c);
        return sb.ToString();
    }

    private static long SizeOf(string file)
    {
        try { return new FileInfo(file).Length; } catch { return -1; }
    }

    private static string Hash(string s)
    {
        using var sha = SHA1.Create();
        var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }

    private static readonly Regex EntryRx = new Regex(
        "\"((?:[^\"\\\\]|\\\\.)*)\":\\{\"size\":(-?\\d+),\"intro\":(null|\\[[^\\]]*\\]),\"credits\":(null|\\[[^\\]]*\\])\\}",
        RegexOptions.Compiled);

    internal static Dictionary<string, Entry> LoadDb()
    {
        var db = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(DbPath)) return db;
            foreach (Match m in EntryRx.Matches(File.ReadAllText(DbPath, Encoding.UTF8)))
                db[Unescape(m.Groups[1].Value)] = new Entry
                {
                    Size = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    Intro = ParseSpan(m.Groups[3].Value),
                    Credits = ParseSpan(m.Groups[4].Value),
                };
        }
        catch (Exception ex) { AppLog.Write("intros.json illisible", ex); }
        return db;
    }

    private static double[]? ParseSpan(string s)
    {
        if (s == "null") return null;
        var parts = s.Trim('[', ']').Split(',');
        if (parts.Length != 2) return null;
        return new[] { double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture) };
    }

    internal static void SaveDb(Dictionary<string, Entry> db)
    {
        var sb = new StringBuilder("{\"v\":1,\"files\":{\n");
        bool first = true;
        foreach (var kv in db)
        {
            if (!first) sb.Append(",\n");
            first = false;
            sb.Append('"').Append(Escape(kv.Key)).Append("\":{\"size\":").Append(kv.Value.Size.ToString(CultureInfo.InvariantCulture))
              .Append(",\"intro\":").Append(SpanJson(kv.Value.Intro))
              .Append(",\"credits\":").Append(SpanJson(kv.Value.Credits)).Append('}');
        }
        sb.Append("\n}}\n");
        Directory.CreateDirectory(AppLog.Folder);
        var tmp = DbPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        if (File.Exists(DbPath)) File.Delete(DbPath);
        File.Move(tmp, DbPath);
    }

    private static string SpanJson(double[]? s) =>
        s == null ? "null" : string.Format(CultureInfo.InvariantCulture, "[{0},{1}]", s[0], s[1]);

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                if (n == 'u' && i + 4 < s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                else sb.Append(n);
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
