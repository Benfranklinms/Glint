using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Glint
{
    /// What's inside your files, as one small Bloom filter of trigrams per file
    /// (about one byte per distinct three-letter run). A grep: query only opens
    /// the files whose filter holds every trigram of the search text, so
    /// searching the whole indexed area takes milliseconds instead of a scan.
    /// Built in the background, saved to %LocalAppData%\Glint\content.bin and
    /// kept current by a folder watcher.
    public static class ContentIndex
    {
        private sealed class Entry { public string Path; public long Ticks; public long Size; public byte[] Bloom; }

        private static ConcurrentDictionary<string, Entry> entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> dirty = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private static List<string> roots = new List<string>();
        private static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private static Timer flush;
        public static bool Complete { get; private set; }
        public static string Status { get; private set; } = "";
        public static int Files => entries.Count;
        private static Settings settings;
        private static CancellationTokenSource stop;

        private static string FilePath => Path.Combine(Settings.Dir, "content.bin");

        public static List<string> DefaultRoots()
        {
            var r = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            string sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && d.DriveType == DriveType.Fixed && !string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase)) r.Add(d.Name);
                }
                catch { }
            }
            return r;
        }

        public static void Start(Settings s)
        {
            settings = s;
            stop?.Cancel();
            Complete = false;
            if (!s.ContentIndex) { entries.Clear(); Status = "Content index off"; return; }
            var ct = (stop = new CancellationTokenSource()).Token;
            roots = (s.ContentFolders != null && s.ContentFolders.Count > 0 ? s.ContentFolders.Select(Query.ExpandHome).ToList() : DefaultRoots())
                .Where(Directory.Exists).Select(p => p.TrimEnd('\\') + "\\").ToList();
            var t = new Thread(() =>
            {
                try
                {
                    Load();
                    Status = $"Content index: {entries.Count:N0} files (saved), checking…";
                    Rebuild(ct);
                    if (ct.IsCancellationRequested) return;
                    Complete = true;
                    Status = $"Content index: {entries.Count:N0} files";
                    Save();
                    Watch();
                }
                catch { }
            }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "content-index" };
            t.Start();
        }

        private static IEnumerable<string> Walk(string root, CancellationToken ct)
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden, RecurseSubdirectories = false };
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) yield break;
                string dir = stack.Pop();
                List<FileSystemInfo> items;
                try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList(); }
                catch { continue; }
                foreach (var fsi in items)
                {
                    if ((fsi.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (fsi.Name[0] != '.' && !ContentSearch.SkipDirs.Contains(fsi.Name, StringComparer.OrdinalIgnoreCase) && !IsExcluded(fsi.FullName)) stack.Push(fsi.FullName);
                    }
                    else if (Wanted(fsi.Name)) yield return fsi.FullName;
                }
            }
        }

        private static bool IsExcluded(string path)
        {
            var ex = settings?.Excluded;
            if (ex == null || ex.Count == 0) return false;
            foreach (var e in ex)
            {
                string p = Query.ExpandHome(e).TrimEnd('\\');
                if (path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool Wanted(string name)
        {
            string ext = Path.GetExtension(name).TrimStart('.');
            return ContentSearch.TextExts.Contains(ext) || ContentSearch.DocExts.Contains(ext);
        }

        private static void Rebuild(CancellationToken ct)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int done = 0;
            var files = new List<string>();
            foreach (var root in roots) foreach (var f in Walk(root, ct)) { files.Add(f); seen.Add(f); }
            Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, f =>
            {
                Update(f);
                int d = Interlocked.Increment(ref done);
                if (d % 500 == 0) Status = $"Content index: {d:N0} of {files.Count:N0} files";
                if (d % 20000 == 0) Save();
            });
            foreach (var k in entries.Keys.ToList()) if (!seen.Contains(k)) entries.TryRemove(k, out _);
        }

        /// (Re)indexes one file when it changed since we last read it.
        private static void Update(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { entries.TryRemove(path, out _); return; }
                string ext = fi.Extension.TrimStart('.');
                bool doc = ContentSearch.DocExts.Contains(ext);
                if (fi.Length == 0 || fi.Length > (doc ? 60L << 20 : 4L << 20)) { entries.TryRemove(path, out _); return; }
                long ticks = fi.LastWriteTimeUtc.Ticks;
                if (entries.TryGetValue(path, out var e) && e.Ticks == ticks && e.Size == fi.Length) return;
                var grams = new HashSet<uint>();
                foreach (var (_, _, line) in ContentSearch.Lines(path, ext, doc))
                {
                    if (line == null) { entries.TryRemove(path, out _); return; } // binary
                    AddGrams(line, grams);
                    if (grams.Count > 400_000) break;
                }
                entries[path] = new Entry { Path = path, Ticks = ticks, Size = fi.Length, Bloom = MakeBloom(grams) };
            }
            catch { }
        }

        // ---------- trigrams and the filter ----------

        private static uint Gram(char a, char b, char c)
        {
            unchecked
            {
                uint h = (uint)char.ToLowerInvariant(a) * 0x9E3779B1u;
                h ^= (uint)char.ToLowerInvariant(b) * 0x85EBCA77u + (h << 6) + (h >> 2);
                h ^= (uint)char.ToLowerInvariant(c) * 0xC2B2AE3Du + (h << 6) + (h >> 2);
                return h;
            }
        }

        private static void AddGrams(string s, HashSet<uint> into)
        {
            for (int i = 0; i + 2 < s.Length; i++) into.Add(Gram(s[i], s[i + 1], s[i + 2]));
        }

        private static byte[] MakeBloom(HashSet<uint> grams)
        {
            int bits = 256;
            while (bits < grams.Count * 10 && bits < (1 << 23)) bits <<= 1;
            var b = new byte[bits / 8];
            uint mask = (uint)bits - 1;
            foreach (uint g in grams)
            {
                uint h1 = g & mask, h2 = Mix(g) & mask;
                b[h1 >> 3] |= (byte)(1 << (int)(h1 & 7));
                b[h2 >> 3] |= (byte)(1 << (int)(h2 & 7));
            }
            return b;
        }

        private static uint Mix(uint g) { unchecked { g ^= g >> 16; g *= 0x7feb352du; g ^= g >> 15; g *= 0x846ca68bu; g ^= g >> 16; return g; } }

        private static bool MayContain(byte[] b, uint[] grams)
        {
            uint mask = (uint)(b.Length * 8) - 1;
            foreach (uint g in grams)
            {
                uint h1 = g & mask, h2 = Mix(g) & mask;
                if ((b[h1 >> 3] & (1 << (int)(h1 & 7))) == 0 || (b[h2 >> 3] & (1 << (int)(h2 & 7))) == 0) return false;
            }
            return true;
        }

        /// Literal runs a regex must contain, so the index can help with regex: too.
        public static List<string> RequiredLiterals(string regex)
        {
            var runs = new List<string>();
            if (regex.Contains('|')) return runs;
            var cur = new StringBuilder();
            void Cut(bool dropLast) { if (dropLast && cur.Length > 0) cur.Length--; if (cur.Length >= 3) runs.Add(cur.ToString()); cur.Clear(); }
            for (int i = 0; i < regex.Length; i++)
            {
                char c = regex[i];
                if (c == '\\' && i + 1 < regex.Length)
                {
                    char n = regex[++i];
                    if (char.IsLetterOrDigit(n)) Cut(false); // \s \w \d \b …
                    else cur.Append(n);                      // \. \( …
                    continue;
                }
                if (c == '[') { Cut(false); while (i < regex.Length && regex[i] != ']') i++; continue; }
                if (c == '?' || c == '*' || c == '{') { Cut(true); if (c == '{') while (i < regex.Length && regex[i] != '}') i++; continue; }
                if (c == '+' ) { Cut(false); continue; }
                if (".()^$".IndexOf(c) >= 0) { Cut(false); continue; }
                cur.Append(c);
            }
            Cut(false);
            return runs;
        }

        /// Paths that may match, or null when the index can't answer this query
        /// (not built yet, searching outside the indexed folders, or no 3-letter text).
        public static IEnumerable<string> Candidates(Query q)
        {
            if (!Complete || settings == null || !settings.ContentIndex) return null;
            var lits = q.Regex != null ? RequiredLiterals(q.Regex) : (q.Grep != null && q.Grep.Length >= 3 ? new List<string> { q.Grep } : new List<string>());
            if (lits.Count == 0) return null;
            string scope = q.In?.TrimEnd('\\');
            if (scope != null && !roots.Any(r => (scope + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase))) return null;
            var set = new HashSet<uint>();
            foreach (var l in lits) AddGrams(l, set);
            var grams = set.ToArray();
            return Iterate(grams, scope, q.Exts);
        }

        private static IEnumerable<string> Iterate(uint[] grams, string scope, HashSet<string> exts)
        {
            foreach (var p in dirty.Keys) if (InScope(p, scope, exts) && File.Exists(p)) yield return p;
            foreach (var e in entries.Values)
            {
                if (dirty.ContainsKey(e.Path) || !InScope(e.Path, scope, exts)) continue;
                if (MayContain(e.Bloom, grams)) yield return e.Path;
            }
        }

        private static bool InScope(string p, string scope, HashSet<string> exts)
        {
            if (scope != null && !p.StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (exts != null && !exts.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant())) return false;
            return true;
        }

        // ---------- staying current ----------

        private static void Watch()
        {
            lock (watchers)
            {
                foreach (var w in watchers) w.Dispose();
                watchers.Clear();
                foreach (var r in roots)
                {
                    try
                    {
                        var w = new FileSystemWatcher(r) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                        FileSystemEventHandler on = (s, e) => Touch(e.FullPath);
                        w.Changed += on; w.Created += on; w.Deleted += on;
                        w.Renamed += (s, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
                        w.EnableRaisingEvents = true;
                        watchers.Add(w);
                    }
                    catch { }
                }
            }
            flush = new Timer(_ => Flush(), null, 3000, 3000);
        }

        private static void Touch(string path)
        {
            if (!Wanted(path)) return;
            foreach (var skip in ContentSearch.SkipDirs) if (path.IndexOf("\\" + skip + "\\", StringComparison.OrdinalIgnoreCase) >= 0) return;
            dirty[path] = 0;
        }

        private static int saving;
        private static void Flush()
        {
            if (dirty.IsEmpty || Interlocked.Exchange(ref saving, 1) == 1) return;
            try
            {
                foreach (var p in dirty.Keys.ToList())
                {
                    if (File.Exists(p)) Update(p); else entries.TryRemove(p, out _);
                    dirty.TryRemove(p, out _);
                }
                Save();
            }
            catch { }
            finally { saving = 0; }
        }

        // ---------- on disk ----------

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                string tmp = FilePath + ".tmp";
                var list = entries.Values.ToList();
                using (var fs = new FileStream(tmp, FileMode.Create))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                using (var w = new BinaryWriter(new BufferedStream(gz, 1 << 20), Encoding.UTF8))
                {
                    w.Write("GLINTCNT"); w.Write(1); w.Write(list.Count);
                    foreach (var e in list) { w.Write(e.Path); w.Write(e.Ticks); w.Write(e.Size); w.Write(e.Bloom.Length); w.Write(e.Bloom); }
                    w.Write("GLINTCNT");
                }
                File.Move(tmp, FilePath, true);
            }
            catch { }
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var r = new BinaryReader(new BufferedStream(gz, 1 << 20), Encoding.UTF8);
                if (r.ReadString() != "GLINTCNT" || r.ReadInt32() != 1) return;
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var e = new Entry { Path = r.ReadString(), Ticks = r.ReadInt64(), Size = r.ReadInt64() };
                    e.Bloom = r.ReadBytes(r.ReadInt32());
                    if (roots.Any(x => e.Path.StartsWith(x, StringComparison.OrdinalIgnoreCase))) d[e.Path] = e;
                }
                if (r.ReadString() != "GLINTCNT") return;
                entries = d;
            }
            catch { }
        }

        public static void Clear() { stop?.Cancel(); entries.Clear(); try { File.Delete(FilePath); } catch { } }
    }
}
