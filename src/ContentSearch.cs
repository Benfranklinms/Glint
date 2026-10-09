using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Glint
{
    /// grep: and regex: — reads text files fresh from disk, in parallel.
    /// Unlike fsearch there is no trigram index yet, so the scope matters:
    /// it searches in: when given, the name matches when there are other words,
    /// and your user folder otherwise.
    public static class ContentSearch
    {
        private static readonly HashSet<string> TextExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "txt","md","markdown","rst","log","csv","tsv","json","jsonc","yaml","yml","toml","ini","cfg","conf","xml","html","htm","css","scss","less",
            "js","mjs","cjs","ts","tsx","jsx","vue","svelte","rs","cs","fs","vb","c","h","cc","cpp","hpp","cxx","m","mm","java","kt","kts","scala","go",
            "py","pyi","rb","php","pl","lua","r","jl","dart","swift","sh","bash","zsh","ps1","psm1","bat","cmd","sql","graphql","proto","gradle",
            "csproj","sln","props","targets","cmake","make","mk","dockerfile","tex","bib","ipynb","env","gitignore","editorconfig","lock",
        };

        private static readonly string[] SkipDirs =
        {
            "node_modules", ".git", "build", "vendor", "target", "bin", "obj", "dist", ".next", ".venv", "venv", "__pycache__",
            "AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", ".cache",
        };

        public static IEnumerable<string> Candidates(FileIndex idx, Query q, List<Hit> nameHits, CancellationToken ct)
        {
            bool haveWords = q.Words.Count > 0 || q.Exts != null;
            if (haveWords && q.In == null)
            {
                foreach (var h in nameHits) if (!h.IsDir) yield return h.Path;
                yield break;
            }
            string root = q.In ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, RecurseSubdirectories = false };
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) yield break;
                string dir = stack.Pop();
                IEnumerable<FileSystemInfo> items;
                try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList(); }
                catch { continue; }
                foreach (var fsi in items)
                {
                    if ((fsi.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (!SkipDirs.Contains(fsi.Name, StringComparer.OrdinalIgnoreCase)) stack.Push(fsi.FullName);
                    }
                    else
                    {
                        string ext = Path.GetExtension(fsi.Name).TrimStart('.');
                        if (q.Exts != null ? q.Exts.Contains(ext.ToLowerInvariant()) : TextExts.Contains(ext)) yield return fsi.FullName;
                    }
                }
            }
        }

        public static void Run(FileIndex idx, Query q, List<Hit> nameHits, Action<Hit> found, CancellationToken ct)
        {
            Regex re = null;
            string needle = q.Grep;
            StringComparison cmp = StringComparison.Ordinal;
            if (q.Regex != null)
            {
                try { re = new Regex(q.Regex, (q.Regex.Any(char.IsUpper) ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
                catch { return; }
            }
            else if (!needle.Any(char.IsUpper)) cmp = StringComparison.OrdinalIgnoreCase; // smart case

            int total = 0, limit = q.Limit;
            try
            {
                Parallel.ForEach(Candidates(idx, q, nameHits, ct), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) }, (path, state) =>
                {
                    if (Volatile.Read(ref total) >= limit) { state.Stop(); return; }
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Length == 0 || fi.Length > 4 << 20) return;
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                        using var sr = new StreamReader(fs, Encoding.UTF8, true);
                        string line; int ln = 0, perFile = 0;
                        while ((line = sr.ReadLine()) != null)
                        {
                            ln++;
                            if (ln == 1 && line.IndexOf('\0') >= 0) return; // binary
                            int at, len;
                            if (re != null) { var m = re.Match(line); if (!m.Success) continue; at = m.Index; len = m.Length; }
                            else { at = line.IndexOf(needle, cmp); if (at < 0) continue; len = needle.Length; }
                            if (Interlocked.Increment(ref total) > limit) { state.Stop(); return; }
                            string snippet = line; int start = at;
                            if (snippet.Length > 160)
                            {
                                int from = Math.Max(0, at - 50);
                                snippet = snippet.Substring(from, Math.Min(160, snippet.Length - from));
                                start = at - from;
                            }
                            found(new Hit { Name = Path.GetFileName(path), Path = path, Line = ln, Snippet = snippet.Replace('\t', ' '), MatchStart = start, MatchLength = Math.Min(len, snippet.Length - start) });
                            if (++perFile >= 5) break;
                        }
                    }
                    catch { }
                });
            }
            catch (OperationCanceledException) { }
        }
    }
}
