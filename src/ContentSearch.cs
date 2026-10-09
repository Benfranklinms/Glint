using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
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
        public static readonly HashSet<string> TextExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "txt","md","markdown","rst","log","csv","tsv","json","jsonc","yaml","yml","toml","ini","cfg","conf","xml","html","htm","css","scss","less",
            "js","mjs","cjs","ts","tsx","jsx","vue","svelte","rs","cs","fs","vb","c","h","cc","cpp","hpp","cxx","m","mm","java","kt","kts","scala","go",
            "py","pyi","rb","php","pl","lua","r","jl","dart","swift","sh","bash","zsh","ps1","psm1","bat","cmd","sql","graphql","proto","gradle",
            "csproj","sln","props","targets","cmake","make","mk","dockerfile","tex","bib","ipynb","env","gitignore","editorconfig","lock",
        };

        /// Documents whose text has to be extracted rather than read.
        public static readonly HashSet<string> DocExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "pdf", "docx", "pptx", "xlsx", "odt", "odp" };

        public static readonly string[] SkipDirs =
        {
            "node_modules", ".git", "build", "vendor", "target", "bin", "obj", "dist", ".next", ".venv", "venv", "__pycache__",
            "AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", ".cache",
        };

        public static IEnumerable<string> Candidates(FileIndex idx, Query q, List<Hit> nameHits, CancellationToken ct)
        {
            bool haveWords = q.Words.Count > 0;
            if (haveWords && q.In == null)
            {
                foreach (var h in nameHits) if (!h.IsDir && h.Kind == HitKind.File) yield return h.Path;
                yield break;
            }
            // the trigram index narrows the whole indexed area to the few files that can match
            var indexed = ContentIndex.Candidates(q);
            if (indexed != null)
            {
                foreach (var p in indexed)
                {
                    if (ct.IsCancellationRequested) yield break;
                    yield return p;
                }
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
                        if (q.Exts != null ? q.Exts.Contains(ext.ToLowerInvariant()) : (TextExts.Contains(ext) || DocExts.Contains(ext))) yield return fsi.FullName;
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
                        string ext = Path.GetExtension(path).TrimStart('.');
                        bool doc = DocExts.Contains(ext);
                        if (fi.Length == 0 || fi.Length > (doc ? 60L << 20 : 4L << 20)) return;
                        int perFile = 0;
                        foreach (var (ln, label, line) in Lines(path, ext, doc))
                        {
                            if (ct.IsCancellationRequested) return;
                            if (line == null) return; // binary
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
                            found(new Hit { Name = Path.GetFileName(path), Path = path, Line = ln, LineLabel = label, Snippet = snippet.Replace('\t', ' ').Replace('\n', ' '), MatchStart = start, MatchLength = Math.Min(len, snippet.Length - start) });
                            if (++perFile >= 5) break;
                        }
                    }
                    catch { }
                });
            }
            catch (OperationCanceledException) { }
        }

        /// Text of a file as (number, label, text) chunks: lines for text files,
        /// pages for PDFs, paragraphs/slides/cells for Office and OpenDocument.
        /// A null text means "binary, give up".
        internal static IEnumerable<(int, string, string)> Lines(string path, string ext, bool doc)
        {
            if (!doc)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                using var sr = new StreamReader(fs, Encoding.UTF8, true);
                string line; int ln = 0;
                while ((line = sr.ReadLine()) != null)
                {
                    ln++;
                    if (ln == 1 && line.IndexOf('\0') >= 0) { yield return (0, null, null); yield break; }
                    yield return (ln, null, line);
                }
                yield break;
            }
            if (ext.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var x in PdfPages(path)) yield return x;
                yield break;
            }
            foreach (var x in ZipDoc(path, ext.ToLowerInvariant())) yield return x;
        }

        private static IEnumerable<(int, string, string)> PdfPages(string path)
        {
            UglyToad.PdfPig.PdfDocument pdf = null;
            try { pdf = UglyToad.PdfPig.PdfDocument.Open(path, new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true }); }
            catch { }
            if (pdf == null) yield break;
            using (pdf)
            {
                int pages = Math.Min(pdf.NumberOfPages, 500);
                for (int p = 1; p <= pages; p++)
                {
                    string text = null;
                    try { text = string.Join(" ", pdf.GetPage(p).GetWords().Select(w => w.Text)); } catch { }
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    // long pages are split so a snippet stays near its match
                    for (int i = 0; i < text.Length; i += 2000)
                        yield return (p, "page " + p, text.Substring(i, Math.Min(2400, text.Length - i)));
                }
            }
        }

        private static readonly Regex Tag = new Regex("<[^>]+>", RegexOptions.Compiled);

        private static IEnumerable<(int, string, string)> ZipDoc(string path, string ext)
        {
            var parts = new List<(int n, string label, string xml, string split)>();
            try
            {
                using var zip = ZipFile.OpenRead(path);
                string Read(ZipArchiveEntry e) { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }
                if (ext == "docx") { var e = zip.GetEntry("word/document.xml"); if (e != null) parts.Add((0, "para", Read(e), "</w:p>")); }
                else if (ext == "xlsx") { var e = zip.GetEntry("xl/sharedStrings.xml"); if (e != null) parts.Add((0, "cell", Read(e), "</si>")); }
                else if (ext == "pptx")
                {
                    foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml")))
                    {
                        int.TryParse(new string(e.Name.Where(char.IsDigit).ToArray()), out int n);
                        parts.Add((n, "slide " + n, Read(e), "</a:p>"));
                    }
                    parts.Sort((a, b) => a.n.CompareTo(b.n));
                }
                else { var e = zip.GetEntry("content.xml"); if (e != null) parts.Add((0, "para", Read(e), "</text:p>")); }
            }
            catch { }
            foreach (var (n, label, xml, split) in parts)
            {
                int k = 0;
                foreach (var chunk in xml.Split(split))
                {
                    k++;
                    string text = WebUtility.HtmlDecode(Tag.Replace(chunk, ""));
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    yield return (n > 0 ? n : k, label.StartsWith("slide") ? label : label + " " + k, text);
                }
            }
        }
    }
}
