using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Glint
{
    public sealed class Hit
    {
        public int Index;
        public string Name;
        public string Path;
        public bool IsDir;
        public int Score;
        public bool ViaTypo;
        public string Corrected; // the word the typo was taken to mean
        // content hits
        public int Line;
        public string Snippet;
        public int MatchStart, MatchLength;
        public string LineLabel; // "line 12", "page 3", "slide 2"
    }

    /// One parsed query: fuzzy words plus filters, in the same syntax as fsearch.
    public sealed class Query
    {
        public sealed class Word { public string Text; public bool Exact, Prefix, Suffix, Exclude; public ulong Mask; }
        public List<Word> Words = new List<Word>();
        public HashSet<string> Exts;          // ext:rs,toml
        public string In;                     // in:~/Developer
        public bool? DirsOnly;                // type:dir / type:file
        public long? MinSize, MaxSize;        // size:>5mb
        public TimeSpan? NewerThan, OlderThan;// mtime:<7d
        public string PathContains;           // path:src
        public string Grep;                   // grep:text
        public string Regex;                  // regex:fn\s+
        public int Limit = 50;
        public bool IsEmpty => Words.Count == 0 && Exts == null && In == null && Grep == null && Regex == null && PathContains == null && DirsOnly == null;

        private static readonly Dictionary<string, string[]> Kinds = new Dictionary<string, string[]>
        {
            ["image"] = new[] { "png", "jpg", "jpeg", "gif", "webp", "bmp", "heic", "svg", "tif", "tiff", "ico" },
            ["video"] = new[] { "mp4", "mkv", "mov", "avi", "webm", "wmv", "m4v" },
            ["audio"] = new[] { "mp3", "wav", "flac", "m4a", "aac", "ogg", "wma" },
            ["doc"] = new[] { "pdf", "doc", "docx", "txt", "md", "rtf", "odt", "ppt", "pptx", "xls", "xlsx", "csv" },
            ["code"] = new[] { "rs", "cs", "c", "h", "cpp", "hpp", "py", "js", "ts", "tsx", "jsx", "java", "kt", "go", "rb", "php", "swift", "html", "css", "json", "toml", "yaml", "yml", "xml", "sh", "ps1", "sql" },
            ["archive"] = new[] { "zip", "rar", "7z", "tar", "gz", "xz", "bz2" },
        };

        public static Query Parse(string text)
        {
            var q = new Query();
            foreach (string raw in Tokenize(text))
            {
                int c = raw.IndexOf(':');
                if (c > 1 && c < raw.Length - 1)
                {
                    string key = raw.Substring(0, c).ToLowerInvariant(), val = raw.Substring(c + 1);
                    switch (key)
                    {
                        case "ext": q.Exts = new HashSet<string>(val.ToLowerInvariant().Split(',').Select(e => e.TrimStart('.')).Where(e => e.Length > 0)); continue;
                        case "in": q.In = ExpandHome(val); continue;
                        case "path": q.PathContains = val; continue;
                        case "grep": q.Grep = val; continue;
                        case "regex": case "re": q.Regex = val; continue;
                        case "limit": if (int.TryParse(val, out int l)) q.Limit = Math.Clamp(l, 1, 500); continue;
                        case "size": ParseSize(q, val); continue;
                        case "mtime": ParseAge(q, val); continue;
                        case "type": case "kind":
                            string v = val.ToLowerInvariant();
                            if (v == "dir" || v == "folder" || v == "directory") q.DirsOnly = true;
                            else if (v == "file") q.DirsOnly = false;
                            else if (Kinds.TryGetValue(v.TrimEnd('s'), out var exts)) { q.Exts = new HashSet<string>(exts); q.DirsOnly = false; }
                            continue;
                    }
                }
                var w = new Word();
                string t = raw;
                if (t.StartsWith("!") && t.Length > 1) { w.Exclude = true; t = t.Substring(1); }
                if (t.StartsWith("'") && t.Length > 1) { w.Exact = true; t = t.Substring(1); }
                if (t.StartsWith("^") && t.Length > 1) { w.Prefix = true; t = t.Substring(1); }
                if (t.EndsWith("$") && t.Length > 1) { w.Suffix = true; t = t.Substring(0, t.Length - 1); }
                w.Text = t.ToLowerInvariant();
                w.Mask = Matcher.MaskOf(w.Text);
                q.Words.Add(w);
            }
            return q;
        }

        private static IEnumerable<string> Tokenize(string s)
        {
            var cur = new System.Text.StringBuilder();
            bool quote = false;
            foreach (char ch in s)
            {
                if (ch == '"') { quote = !quote; continue; }
                if (char.IsWhiteSpace(ch) && !quote) { if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); } continue; }
                cur.Append(ch);
            }
            if (cur.Length > 0) yield return cur.ToString();
        }

        public static string ExpandHome(string p)
        {
            p = p.Replace('/', '\\');
            if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1);
            return p;
        }

        private static void ParseSize(Query q, string v)
        {
            char op = v.Length > 0 && (v[0] == '>' || v[0] == '<') ? v[0] : '>';
            string num = v.TrimStart('>', '<', '=').ToLowerInvariant();
            long mul = 1;
            foreach (var (suf, m) in new[] { ("gb", 1L << 30), ("mb", 1L << 20), ("kb", 1L << 10), ("g", 1L << 30), ("m", 1L << 20), ("k", 1L << 10), ("b", 1L) })
                if (num.EndsWith(suf)) { mul = m; num = num.Substring(0, num.Length - suf.Length); break; }
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return;
            long bytes = (long)(d * mul);
            if (op == '>') q.MinSize = bytes; else q.MaxSize = bytes;
        }

        private static void ParseAge(Query q, string v)
        {
            char op = v.Length > 0 && (v[0] == '>' || v[0] == '<') ? v[0] : '<';
            string num = v.TrimStart('>', '<', '=').ToLowerInvariant();
            double mul = 1;
            char unit = num.Length > 0 ? num[^1] : 'd';
            if (char.IsLetter(unit)) num = num.Substring(0, num.Length - 1); else unit = 'd';
            mul = unit switch { 'm' => 1.0 / 1440, 'h' => 1.0 / 24, 'w' => 7, 'y' => 365, _ => 1 };
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return;
            var span = TimeSpan.FromDays(d * mul);
            if (op == '<') q.NewerThan = span; else q.OlderThan = span;
        }
    }

    public static class Matcher
    {
        /// One bit per letter/digit/punctuation class present, for a cheap "could it match" test.
        public static ulong MaskOf(string s)
        {
            ulong m = 0;
            foreach (char ch0 in s)
            {
                char ch = char.ToLowerInvariant(ch0);
                if (ch >= 'a' && ch <= 'z') m |= 1UL << (ch - 'a');
                else if (ch >= '0' && ch <= '9') m |= 1UL << (26 + ch - '0');
                else if (ch == '.') m |= 1UL << 36;
                else if (ch == '_') m |= 1UL << 37;
                else if (ch == '-') m |= 1UL << 38;
                else if (ch == ' ') m |= 1UL << 39;
                else m |= 1UL << 40;
            }
            return m;
        }

        private static readonly HashSet<string> NoisyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "AppData", "Program Files", "Program Files (x86)", "ProgramData", "node_modules", "$Recycle.Bin",
            "target", "obj", "bin", "site-packages", "dist-packages", "WinSxS", "vendor", "packages", "vcpkg", "Strawberry",
            "Modules", "hostedtoolcache", "Library", "__pycache__", "venv", "build",
        };

        /// Where a file lives matters as much as its name: your own folders
        /// beat toolchains, caches and system trees. Walks the parent chain once.
        private static int LocationBias(string[] names, int[] parents, int i, int userDir)
        {
            bool underUser = false, noisy = false; int depth = 0;
            for (int p = parents[i]; p >= 0; p = parents[p])
            {
                if (p == userDir) { underUser = true; break; }
                string n = names[p];
                if (n.Length > 0 && (n[0] == '.' || n[0] == '$') || NoisyDirs.Contains(n)) noisy = true;
                depth++;
                if (depth > 64) break;
            }
            if (underUser)
                for (int p = parents[i]; p >= 0 && p != userDir; p = parents[p])
                { string n = names[p]; if (n.Length > 0 && (n[0] == '.' || n[0] == '$') || NoisyDirs.Contains(n)) { noisy = true; break; } }
            int b = -Math.Min(depth * 6, 60);
            if (noisy) b -= 220;
            else if (underUser) b += 160;
            return b;
        }

        private static readonly string[] Noisy =
        {
            @"\windows\", @"\appdata\", @"\program files", @"\programdata\", @"\node_modules\", @"\.git\",
            @"\$recycle.bin\", @"\target\", @"\obj\", @"\bin\", @"\.cache\", @"\site-packages\", @"\winsxs\",
        };

        /// Scores one word against one name. 0 = no match.
        public static int ScoreWord(Query.Word w, string name, out bool typo)
        {
            typo = false;
            string t = w.Text;
            if (t.Length == 0) return 1;
            int at = name.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (w.Prefix) return at == 0 ? 900 - name.Length : 0;
            if (w.Suffix) return name.EndsWith(t, StringComparison.OrdinalIgnoreCase) ? 850 - name.Length : 0;
            if (at >= 0)
            {
                int s = 600;
                if (name.Length == t.Length) s += 400;                                    // whole name
                else if (StemEquals(name, t)) s += 350;                                   // main == main.rs
                if (at == 0) s += 150;
                else if (IsBoundary(name, at)) s += 90;
                return s - Math.Min(name.Length, 120);
            }
            if (w.Exact) return 0;
            int f = Subsequence(t, name);
            if (f > 0) return f;
            // typo: 4 letters forgive a swap, 5+ forgive any one edit
            if (t.Length >= 4 && name.Length >= t.Length - 1)
            {
                int max = t.Length >= 5 ? 1 : 0;
                var ns = name.AsSpan();
                if (Typo(t, ns, max)) { typo = true; return 380 - Math.Min(name.Length, 120); }
                int dot = name.LastIndexOf('.');
                if (dot > 0 && Math.Abs(dot - t.Length) <= 1 && Typo(t, ns.Slice(0, dot), max)) { typo = true; return 370 - Math.Min(name.Length, 120); }
                int start = 0;
                for (int i = 1; i <= name.Length; i++)
                {
                    bool cut = i == name.Length || !char.IsLetterOrDigit(name[i]) || (char.IsLower(name[i - 1]) && char.IsUpper(name[i]));
                    if (!cut) continue;
                    int len = i - start;
                    if (len > 0 && Math.Abs(len - t.Length) <= 1 && !(start == 0 && i == name.Length) && Typo(t, ns.Slice(start, len), max))
                    { typo = true; return 300 - Math.Min(name.Length, 120); }
                    start = (i < name.Length && !char.IsLetterOrDigit(name[i])) ? i + 1 : i;
                }
            }
            return 0;
        }

        private static bool StemEquals(string name, string t)
        {
            int dot = name.LastIndexOf('.');
            return dot > 0 && dot == t.Length && name.StartsWith(t, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBoundary(string s, int i)
        {
            char p = s[i - 1], c = s[i];
            return !char.IsLetterOrDigit(p) || (char.IsLower(p) && char.IsUpper(c));
        }

        /// Characters of t appear in order in s; tighter runs and word starts score higher.
        private static int Subsequence(string t, string s)
        {
            int si = 0, score = 200, prev = -2, gaps = 0, bad = 0;
            for (int ti = 0; ti < t.Length; ti++)
            {
                char c = t[ti];
                while (si < s.Length && char.ToLowerInvariant(s[si]) != c) si++;
                if (si == s.Length) return 0;
                if (si == prev + 1) score += 12;
                else if (prev >= 0) { gaps++; if (IsBoundary(s, si)) score += 8; else bad++; }
                else if (si == 0 || IsBoundary(s, si)) score += 20;
                else bad++;
                prev = si; si++;
            }
            score -= gaps * 25;
            if (bad > 1 || gaps > t.Length / 2 + 1) return 0;
            return Math.Max(1, score - Math.Min(s.Length, 120));
        }

        /// Optimal string alignment distance <= 1 (max=1), or a single adjacent swap only (max=0).
        private static bool Typo(string a, ReadOnlySpan<char> b, int max)
        {
            int la = a.Length, lb = b.Length;
            if (Math.Abs(la - lb) > 1) return false;
            if (la == lb)
            {
                int diff = -1, diffs = 0;
                for (int i = 0; i < la; i++)
                    if (a[i] != char.ToLowerInvariant(b[i])) { if (diffs++ == 0) diff = i; if (diffs > 2) return false; }
                if (diffs == 0) return false;
                if (diffs == 2 && diff + 1 < la && a[diff] == char.ToLowerInvariant(b[diff + 1]) && a[diff + 1] == char.ToLowerInvariant(b[diff])) return true;
                return max >= 1 && diffs == 1;
            }
            if (max < 1) return false;
            ReadOnlySpan<char> lo = la < lb ? a.AsSpan() : b, hi = la < lb ? b : a.AsSpan();
            int x = 0, y = 0; bool skipped = false;
            while (x < lo.Length && y < hi.Length)
            {
                if (char.ToLowerInvariant(lo[x]) == char.ToLowerInvariant(hi[y])) { x++; y++; }
                else { if (skipped) return false; skipped = true; y++; }
            }
            return true;
        }

        // ---------- name search ----------

        public static List<Hit> Search(FileIndex idx, Query q, CancellationToken ct)
        {
            int n = idx.Count;
            var names = idx.Names; var parents = idx.Parents; var flags = idx.Flags; var masks = idx.Masks;
            int inDir = -1;
            if (q.In != null) { inDir = idx.FindDir(q.In); if (inDir < 0) return new List<Hit>(); }
            var include = q.Words.Where(w => !w.Exclude).ToList();
            var exclude = q.Words.Where(w => w.Exclude).ToList();

            const int Keep = 400;
            int userDir = idx.FindDir(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var buckets = new List<(int score, int i, bool typo, string word)>[Environment.ProcessorCount];
            int chunk = Math.Max(1 << 15, n / (buckets.Length * 4) + 1);
            int parts = (n + chunk - 1) / chunk;
            var results = new System.Collections.Concurrent.ConcurrentBag<List<(int score, int i, bool typo, string word)>>();

            Parallel.For(0, parts, new ParallelOptions { CancellationToken = ct }, part =>
            {
                var local = new List<(int, int, bool, string)>(64);
                int end = Math.Min(n, (part + 1) * chunk);
                for (int i = part * chunk; i < end; i++)
                {
                    if ((i & 0x3FFF) == 0 && ct.IsCancellationRequested) return;
                    byte f = flags[i];
                    if ((f & (FileIndex.Gone | FileIndex.Hidden)) != 0 || parents[i] < 0) continue;
                    bool dir = (f & FileIndex.Dir) != 0;
                    if (q.DirsOnly == true && !dir) continue;
                    if (q.DirsOnly == false && dir) continue;
                    string name = names[i];
                    if (q.Exts != null)
                    {
                        if (dir) continue;
                        int dot = name.LastIndexOf('.');
                        if (dot < 0 || !q.Exts.Contains(name.Substring(dot + 1).ToLowerInvariant())) continue;
                    }
                    ulong m = masks[i];
                    int total = 0; bool anyTypo = false; string corrected = null; bool ok = true;
                    foreach (var w in include)
                    {
                        ulong missing = w.Mask & ~m;
                        if (missing != 0 && (w.Text.Length < 5 || BitOperations.PopCount(missing) > 1)) { ok = false; break; }
                        int s = ScoreWord(w, name, out bool typo);
                        if (s <= 0) { ok = false; break; }
                        total += s;
                        if (typo) { anyTypo = true; corrected = name; }
                    }
                    if (!ok) continue;
                    foreach (var w in exclude)
                        if (name.IndexOf(w.Text, StringComparison.OrdinalIgnoreCase) >= 0) { ok = false; break; }
                    if (!ok) continue;
                    if (inDir >= 0 && !idx.IsUnder(i, inDir)) continue;
                    if (include.Count == 0) total = 500 - Math.Min(name.Length, 120);
                    total += LocationBias(names, parents, i, userDir);
                    local.Add((total, i, anyTypo, corrected));
                }
                if (local.Count > Keep) local = local.OrderByDescending(x => x.Item1).Take(Keep).ToList();
                results.Add(local);
            });
            ct.ThrowIfCancellationRequested();

            var top = results.SelectMany(x => x).OrderByDescending(x => x.score).Take(Keep * 2);
            var hits = new List<Hit>();
            foreach (var (score, i, typo, word) in top)
            {
                string path = idx.PathOf(i);
                string lower = path.ToLowerInvariant();
                if (q.PathContains != null && lower.IndexOf(q.PathContains.ToLowerInvariant(), StringComparison.Ordinal) < 0) continue;
                int s = score;
                hits.Add(new Hit { Index = i, Name = names[i], Path = path, IsDir = (flags[i] & FileIndex.Dir) != 0, Score = s, ViaTypo = typo, Corrected = word });
            }
            IEnumerable<Hit> ordered = hits.OrderByDescending(h => h.Score);
            if (q.MinSize != null || q.MaxSize != null || q.NewerThan != null || q.OlderThan != null)
                ordered = ordered.Where(h => PassesStat(h, q));
            return ordered.Take(q.Limit).ToList();
        }

        private static bool PassesStat(Hit h, Query q)
        {
            try
            {
                var fi = new FileInfo(h.Path);
                if (!fi.Exists) return q.MinSize == null && q.MaxSize == null && q.NewerThan == null && q.OlderThan == null;
                if (q.MinSize != null && fi.Length < q.MinSize) return false;
                if (q.MaxSize != null && fi.Length > q.MaxSize) return false;
                var age = DateTime.Now - fi.LastWriteTime;
                if (q.NewerThan != null && age > q.NewerThan) return false;
                if (q.OlderThan != null && age < q.OlderThan) return false;
                return true;
            }
            catch { return false; }
        }
    }
}
