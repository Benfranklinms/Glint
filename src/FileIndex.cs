using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Glint
{
    /// Every file and folder name on every fixed drive, held as flat arrays
    /// (name, parent, flags). Paths are rebuilt by walking parents.
    ///
    /// NTFS drives are read straight from the master file table when Glint
    /// runs elevated (a few seconds for millions of files) and kept current
    /// from the USN change journal. Without elevation, or on FAT/exFAT/network
    /// drives, a normal folder crawl plus FileSystemWatcher does the same job,
    /// just slower to build.
    public sealed class FileIndex
    {
        public const byte Dir = 1, Gone = 2, Hidden = 4;

        // Readers take these array references without a lock: arrays only grow
        // by copy, and slots are written before Count moves past them.
        private string[] names = new string[1 << 16];
        private int[] parents = new int[1 << 16];
        private byte[] flags = new byte[1 << 16];
        private ulong[] masks = new ulong[1 << 16];
        private volatile int count;
        private readonly object write = new object();

        public int Count => count;
        public string[] Names => names;
        public int[] Parents => parents;
        public byte[] Flags => flags;
        public ulong[] Masks => masks;

        public event Action Changed;
        public string Status { get; private set; } = "Indexing…";
        public bool Ready { get; private set; }
        public bool UsedMft { get; private set; }
        public int Generation;

        private readonly List<Volume> volumes = new List<Volume>();

        private sealed class Volume
        {
            public string Letter;            // "C:"
            public int Root;                 // index of the "C:" entry
            public Dictionary<ulong, int> Frn; // NTFS file reference -> index (MFT mode)
            public ulong JournalId;
            public long NextUsn;
            public FileSystemWatcher Watcher;
            public Dictionary<string, int> DirsByPath; // crawl mode
            public volatile bool Stopped;              // replaced by a rebuild
        }

        // ---------- building ----------

        public Settings Settings = new Settings();
        public bool FromCache { get; private set; }
        /// Bumped whenever the arrays are replaced wholesale (a rebuild swapped in).
        public int Epoch;
        public HashSet<int> ExcludedDirs = new HashSet<int>();
        private int userDir = -2, userDirEpoch = -1;

        public static string Letter(DriveInfo d) => d.Name.TrimEnd('\\');

        private List<DriveInfo> Drives()
        {
            var s = Settings;
            return DriveInfo.GetDrives().Where(d =>
            {
                try
                {
                    if (!d.IsReady) return false;
                    return d.DriveType == DriveType.Fixed
                        || (s.IncludeRemovable && d.DriveType == DriveType.Removable)
                        || (s.IncludeNetwork && d.DriveType == DriveType.Network);
                }
                catch { return false; }
            }).ToList();
        }

        public void Start()
        {
            Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool admin = Native.IsAdmin();
                var drives = Drives();
                var letters = drives.Select(Letter).ToList();
                bool loaded = false;
                if (Settings.SaveIndex)
                {
                    try { loaded = IndexCache.TryLoad(this, letters); } catch { loaded = false; }
                }
                if (loaded)
                {
                    FromCache = true;
                    Ready = true;
                    Status = $"{count:N0} items ready in {sw.Elapsed.TotalSeconds:0.0} s (saved index)";
                    RefreshExcluded();
                    Interlocked.Increment(ref Generation);
                    Changed?.Invoke();

                    // NTFS volumes catch up from the change journal where they left off;
                    // anything else (or a journal that has moved on) gets a fresh scan.
                    bool rebuild = false;
                    foreach (var v in volumes)
                        if (v.Frn == null || !admin || !JournalStillCovers(v)) { rebuild = true; break; }
                    if (rebuild)
                    {
                        Status = $"{count:N0} items (saved index) · refreshing…";
                        var fresh = new FileIndex { Settings = Settings };
                        fresh.Build(drives, admin);
                        SwapIn(fresh);
                        Status = $"{count:N0} items indexed in {sw.Elapsed.TotalSeconds:0.0} s" + (UsedMft ? "" : " (folder crawl)");
                    }
                    else
                    {
                        UsedMft = true;
                        Status = $"{count:N0} items · ready in {sw.Elapsed.TotalSeconds:0.0} s from saved index";
                    }
                }
                else
                {
                    Build(drives, admin);
                    Status = $"{count:N0} items indexed in {sw.Elapsed.TotalSeconds:0.0} s" + (UsedMft ? "" : " (folder crawl)");
                    Ready = true;
                }
                RefreshExcluded();
                Interlocked.Increment(ref Generation);
                Changed?.Invoke();
                foreach (var v in volumes) StartLive(v);
                if (Settings.SaveIndex) { SaveNow(); StartAutoSave(); }
            });
        }

        /// A full fresh scan, swapped in when done (Settings > Rebuild index).
        public void Rebuild()
        {
            Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Status = $"{count:N0} items · rebuilding…";
                Changed?.Invoke();
                var fresh = new FileIndex { Settings = Settings };
                fresh.Build(Drives(), Native.IsAdmin());
                SwapIn(fresh);
                Status = $"{count:N0} items indexed in {sw.Elapsed.TotalSeconds:0.0} s" + (UsedMft ? "" : " (folder crawl)");
                RefreshExcluded();
                Interlocked.Increment(ref Generation);
                Changed?.Invoke();
                foreach (var v in volumes) StartLive(v);
                SaveNow();
            });
        }

        private void Build(List<DriveInfo> drives, bool admin)
        {
            Parallel.ForEach(drives, new ParallelOptions { MaxDegreeOfParallelism = 2 }, d =>
            {
                var v = new Volume { Letter = Letter(d) };
                lock (write) { v.Root = AddUnlocked(v.Letter, -1, Dir); volumes.Add(v); }
                bool ntfs = false;
                try { ntfs = string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) && d.DriveType != DriveType.Network; } catch { }
                bool ok = admin && ntfs && TryReadMft(v);
                if (ok) UsedMft = true; else Crawl(v);
            });
            MarkHidden();
        }

        /// Takes over a freshly built index in one step.
        private void SwapIn(FileIndex fresh)
        {
            lock (write)
            {
                foreach (var old in volumes) { old.Stopped = true; try { old.Watcher?.Dispose(); } catch { } }
                names = fresh.names; parents = fresh.parents; flags = fresh.flags; masks = fresh.masks;
                count = fresh.count;
                volumes.Clear(); volumes.AddRange(fresh.volumes);
                UsedMft = fresh.UsedMft;
                Interlocked.Increment(ref Epoch);
            }
        }

        private bool JournalStillCovers(Volume v)
        {
            try
            {
                using SafeFileHandle h = Native.CreateFile(@"\\.\" + v.Letter, Native.GENERIC_READ,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return false;
                if (!Native.DeviceIoControl(h, Native.FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, out var jd,
                        Marshal.SizeOf<Native.USN_JOURNAL_DATA_V0>(), out _, IntPtr.Zero)) return false;
                return jd.UsnJournalID == v.JournalId && v.NextUsn >= jd.FirstUsn && v.NextUsn <= jd.NextUsn;
            }
            catch { return false; }
        }

        // ---------- saved index ----------

        private System.Threading.Timer saveTimer;
        private int savedGeneration = -1;

        private void StartAutoSave()
        {
            saveTimer = new System.Threading.Timer(_ => { if (Generation != savedGeneration) SaveNow(); }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        }

        public void SaveNow()
        {
            if (!Ready || !Settings.SaveIndex) return;
            try
            {
                IndexCache.Snapshot snap;
                lock (write) snap = TakeSnapshot();
                IndexCache.Save(snap);
                savedGeneration = Generation;
            }
            catch { }
        }

        private IndexCache.Snapshot TakeSnapshot()
        {
            int n = count;
            var snap = new IndexCache.Snapshot { Count = n, Names = new string[n], Parents = new int[n], Flags = new byte[n] };
            Array.Copy(names, snap.Names, n); Array.Copy(parents, snap.Parents, n); Array.Copy(flags, snap.Flags, n);
            foreach (var v in volumes)
                snap.Volumes.Add(new IndexCache.Vol
                {
                    Letter = v.Letter, Root = v.Root, Mft = v.Frn != null, JournalId = v.JournalId, NextUsn = v.NextUsn,
                    Frn = v.Frn?.Select(kv => (kv.Key, kv.Value)).ToArray(),
                });
            return snap;
        }

        /// Called by IndexCache.TryLoad with entries already compacted.
        internal void LoadFrom(IndexCache.Snapshot s)
        {
            lock (write)
            {
                int cap = 1 << 16;
                while (cap < s.Count + 1024) cap <<= 1;
                names = new string[cap]; parents = new int[cap]; flags = new byte[cap]; masks = new ulong[cap];
                Array.Copy(s.Names, names, s.Count); Array.Copy(s.Parents, parents, s.Count); Array.Copy(s.Flags, flags, s.Count);
                for (int i = 0; i < s.Count; i++) masks[i] = Matcher.MaskOf(names[i]);
                count = s.Count;
                volumes.Clear();
                foreach (var sv in s.Volumes)
                {
                    var v = new Volume { Letter = sv.Letter, Root = sv.Root, JournalId = sv.JournalId, NextUsn = sv.NextUsn };
                    if (sv.Mft)
                    {
                        v.Frn = new Dictionary<ulong, int>(sv.Frn.Length + 16);
                        foreach (var (k, i) in sv.Frn) v.Frn[k] = i;
                    }
                    volumes.Add(v);
                }
            }
        }

        // ---------- excluded folders and lookups ----------

        public void RefreshExcluded()
        {
            var set = new HashSet<int>();
            foreach (var p in Settings.Excluded ?? new List<string>())
            {
                int d = FindDir(Query.ExpandHome(p));
                if (d >= 0) set.Add(d);
            }
            ExcludedDirs = set;
        }

        /// The index entry of the user's profile folder, cached until a rebuild.
        public int UserDir
        {
            get
            {
                if (userDirEpoch != Epoch || userDir == -2 || (userDir < 0 && Ready))
                {
                    userDir = FindDir(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    userDirEpoch = Epoch;
                }
                return userDir;
            }
        }

        /// For tests and tools: append one entry.
        public int Add(string name, int parent, byte f) { lock (write) return AddUnlocked(name, parent, f); }

        private int AddUnlocked(string name, int parent, byte f)
        {
            int i = count;
            if (i == names.Length)
            {
                int n = names.Length * 2;
                Array.Resize(ref names, n); Array.Resize(ref parents, n);
                Array.Resize(ref flags, n); Array.Resize(ref masks, n);
            }
            names[i] = name; parents[i] = parent; flags[i] = f; masks[i] = Matcher.MaskOf(name);
            count = i + 1;
            return i;
        }

        private static ulong Key(ulong frn) => frn & 0x0000FFFFFFFFFFFFUL;

        private unsafe bool TryReadMft(Volume v)
        {
            using SafeFileHandle h = Native.CreateFile(@"\\.\" + v.Letter, Native.GENERIC_READ,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return false;
            if (!Native.DeviceIoControl(h, Native.FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, out var jd,
                    Marshal.SizeOf<Native.USN_JOURNAL_DATA_V0>(), out _, IntPtr.Zero))
                return false;

            var frns = new List<ulong>(1 << 20);
            var pfrns = new List<ulong>(1 << 20);
            var nms = new List<string>(1 << 20);
            var dirs = new List<bool>(1 << 20);

            const int BufSize = 1 << 20;
            IntPtr buf = Marshal.AllocHGlobal(BufSize);
            try
            {
                var med = new Native.MFT_ENUM_DATA_V0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = jd.NextUsn };
                while (Native.DeviceIoControl(h, Native.FSCTL_ENUM_USN_DATA, ref med, Marshal.SizeOf(med), buf, BufSize, out int got, IntPtr.Zero) && got > 8)
                {
                    byte* p = (byte*)buf;
                    med.StartFileReferenceNumber = *(ulong*)p;
                    int off = 8;
                    while (off < got)
                    {
                        byte* r = p + off;
                        uint len = *(uint*)r;
                        if (len == 0) break;
                        if (*(ushort*)(r + 4) == 2)
                        {
                            ulong frn = *(ulong*)(r + 8), parent = *(ulong*)(r + 16);
                            uint attr = *(uint*)(r + 52);
                            int nlen = *(ushort*)(r + 56), noff = *(ushort*)(r + 58);
                            frns.Add(Key(frn)); pfrns.Add(Key(parent));
                            nms.Add(new string((char*)(r + noff), 0, nlen / 2));
                            dirs.Add((attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0);
                        }
                        off += (int)len;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            if (frns.Count == 0) return false;

            lock (write)
            {
                v.Frn = new Dictionary<ulong, int>(frns.Count + 16) { [5] = v.Root }; // 5 = NTFS root folder
                int first = count;
                for (int k = 0; k < frns.Count; k++)
                {
                    bool meta = pfrns[k] == 5 && nms[k].StartsWith("$");
                    int i = AddUnlocked(nms[k], -1, (byte)((dirs[k] ? Dir : 0) | (meta ? Hidden : 0)));
                    v.Frn[frns[k]] = i;
                }
                for (int k = 0; k < frns.Count; k++)
                {
                    int i = first + k;
                    parents[i] = v.Frn.TryGetValue(pfrns[k], out int pi) ? pi : v.Root;
                }
            }
            v.JournalId = jd.UsnJournalID;
            v.NextUsn = jd.NextUsn;
            return true;
        }

        private static readonly EnumerationOptions Shallow = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        private void Crawl(Volume v)
        {
            v.DirsByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [v.Letter + "\\"] = v.Root };
            var stack = new Stack<(string path, int idx)>();
            stack.Push((v.Letter + "\\", v.Root));
            var batch = new List<(string name, bool dir, string full)>(512);
            while (stack.Count > 0)
            {
                var (path, idx) = stack.Pop();
                batch.Clear();
                try
                {
                    foreach (var fsi in new DirectoryInfo(path).EnumerateFileSystemInfos("*", Shallow))
                        batch.Add((fsi.Name, (fsi.Attributes & FileAttributes.Directory) != 0, fsi.FullName));
                }
                catch { continue; }
                lock (write)
                {
                    foreach (var (name, dir, full) in batch)
                    {
                        int i = AddUnlocked(name, idx, dir ? Dir : (byte)0);
                        if (dir) { v.DirsByPath[full] = i; stack.Push((full, i)); }
                    }
                }
                if (count % 50000 < batch.Count) Status = $"Indexing… {count:N0} items";
            }
        }

        /// Entries under $-metadata folders and the recycle bin stay out of results.
        private void MarkHidden()
        {
            int n = count;
            var state = new byte[n]; // 0 unknown, 1 visible, 2 hidden
            for (int i = 0; i < n; i++) Resolve(i, state);
            for (int i = 0; i < n; i++) if (state[i] == 2) flags[i] |= Hidden;
        }

        private byte Resolve(int i, byte[] state)
        {
            var chain = new List<int>();
            byte s = 1;
            int cur = i, guard = 0;
            while (cur >= 0 && guard++ < 512)
            {
                if (state[cur] != 0) { s = state[cur]; break; }
                chain.Add(cur);
                if ((flags[cur] & Hidden) != 0 || names[cur].Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) { s = 2; break; }
                cur = parents[cur];
            }
            foreach (int c in chain) state[c] = s;
            return s;
        }

        // ---------- staying current ----------

        private void StartLive(Volume v)
        {
            if (v.Frn != null)
            {
                var t = new Thread(() => JournalLoop(v)) { IsBackground = true, Name = "usn " + v.Letter, Priority = ThreadPriority.BelowNormal };
                t.Start();
                return;
            }
            try
            {
                var w = new FileSystemWatcher(v.Letter + "\\")
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                w.Created += (s, e) => CrawlAdd(v, e.FullPath);
                w.Deleted += (s, e) => CrawlRemove(v, e.FullPath);
                w.Renamed += (s, e) => { CrawlRemove(v, e.OldFullPath); CrawlAdd(v, e.FullPath); };
                w.EnableRaisingEvents = true;
                v.Watcher = w;
            }
            catch { }
        }

        private unsafe void JournalLoop(Volume v)
        {
            using SafeFileHandle h = Native.CreateFile(@"\\.\" + v.Letter, Native.GENERIC_READ,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return;
            const int BufSize = 1 << 16;
            IntPtr buf = Marshal.AllocHGlobal(BufSize);
            try
            {
                while (!v.Stopped)
                {
                    var rd = new Native.READ_USN_JOURNAL_DATA_V0
                    {
                        StartUsn = v.NextUsn,
                        ReasonMask = Native.USN_REASON_FILE_CREATE | Native.USN_REASON_FILE_DELETE | Native.USN_REASON_RENAME_NEW_NAME | Native.USN_REASON_RENAME_OLD_NAME,
                        UsnJournalID = v.JournalId,
                    };
                    bool changed = false;
                    if (Native.DeviceIoControl(h, Native.FSCTL_READ_USN_JOURNAL, ref rd, Marshal.SizeOf(rd), buf, BufSize, out int got, IntPtr.Zero) && got > 8)
                    {
                        byte* p = (byte*)buf;
                        v.NextUsn = *(long*)p;
                        int off = 8;
                        lock (write)
                        {
                            if (v.Stopped) return;
                            while (off < got)
                            {
                                byte* r = p + off;
                                uint len = *(uint*)r;
                                if (len == 0) break;
                                if (*(ushort*)(r + 4) == 2)
                                {
                                    ulong frn = Key(*(ulong*)(r + 8)), parent = Key(*(ulong*)(r + 16));
                                    uint reason = *(uint*)(r + 40), attr = *(uint*)(r + 52);
                                    int nlen = *(ushort*)(r + 56), noff = *(ushort*)(r + 58);
                                    string name = new string((char*)(r + noff), 0, nlen / 2);
                                    bool dir = (attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                                    if ((reason & Native.USN_REASON_FILE_DELETE) != 0)
                                    {
                                        if (v.Frn.TryGetValue(frn, out int i)) { flags[i] |= Gone; v.Frn.Remove(frn); changed = true; }
                                    }
                                    else if ((reason & (Native.USN_REASON_FILE_CREATE | Native.USN_REASON_RENAME_NEW_NAME)) != 0)
                                    {
                                        int pi = v.Frn.TryGetValue(parent, out int pp) ? pp : v.Root;
                                        byte hid = (byte)(pi >= 0 && (flags[pi] & Hidden) != 0 ? Hidden : 0);
                                        if (v.Frn.TryGetValue(frn, out int i))
                                        {
                                            names[i] = name; parents[i] = pi; masks[i] = Matcher.MaskOf(name);
                                            flags[i] = (byte)((dir ? Dir : 0) | hid);
                                        }
                                        else v.Frn[frn] = AddUnlocked(name, pi, (byte)((dir ? Dir : 0) | hid));
                                        changed = true;
                                    }
                                }
                                off += (int)len;
                            }
                        }
                    }
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == 1181 /*JOURNAL_ENTRY_DELETED*/ || err == 1179 /*JOURNAL_NOT_ACTIVE*/)
                        {
                            if (Native.DeviceIoControl(h, Native.FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, out var jd,
                                    Marshal.SizeOf<Native.USN_JOURNAL_DATA_V0>(), out _, IntPtr.Zero))
                            { v.JournalId = jd.UsnJournalID; v.NextUsn = jd.NextUsn; }
                        }
                    }
                    if (changed) { Interlocked.Increment(ref Generation); Changed?.Invoke(); }
                    Thread.Sleep(changed ? 50 : 100);
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void CrawlAdd(Volume v, string full)
        {
            try
            {
                string parent = Path.GetDirectoryName(full);
                if (parent == null) return;
                if (!parent.EndsWith("\\") && parent.Length == 2) parent += "\\";
                lock (write)
                {
                    if (v.Stopped || !v.DirsByPath.TryGetValue(parent, out int pi)) return;
                    bool dir = Directory.Exists(full);
                    byte hid = (byte)((flags[pi] & Hidden) != 0 ? Hidden : 0);
                    int i = AddUnlocked(Path.GetFileName(full), pi, (byte)((dir ? Dir : 0) | hid));
                    if (dir) v.DirsByPath[full] = i;
                }
                Interlocked.Increment(ref Generation);
                Changed?.Invoke();
            }
            catch { }
        }

        private void CrawlRemove(Volume v, string full)
        {
            try
            {
                string parent = Path.GetDirectoryName(full);
                if (parent == null) return;
                if (!parent.EndsWith("\\") && parent.Length == 2) parent += "\\";
                string name = Path.GetFileName(full);
                lock (write)
                {
                    if (v.Stopped || !v.DirsByPath.TryGetValue(parent, out int pi)) return;
                    int n = count;
                    for (int i = n - 1; i >= 0; i--)
                        if (parents[i] == pi && (flags[i] & Gone) == 0 && string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                        { flags[i] |= Gone; break; }
                    v.DirsByPath.Remove(full);
                }
                Interlocked.Increment(ref Generation);
                Changed?.Invoke();
            }
            catch { }
        }

        // ---------- reading ----------

        public string PathOf(int i)
        {
            var parts = new List<string>(16);
            int cur = i, guard = 0;
            while (cur >= 0 && guard++ < 512) { parts.Add(names[cur]); cur = parents[cur]; }
            parts.Reverse();
            if (parts.Count == 1) return parts[0] + "\\";
            return string.Join("\\", parts);
        }

        public bool IsUnder(int i, int ancestor)
        {
            int cur = parents[i], guard = 0;
            while (cur >= 0 && guard++ < 512) { if (cur == ancestor) return true; cur = parents[cur]; }
            return false;
        }

        /// The index entry for a folder path, or -1.
        public int FindDir(string path)
        {
            path = path.TrimEnd('\\');
            if (path.Length == 0) return -1;
            string[] segs = path.Split('\\');
            int cur = -1;
            for (int s = 0; s < segs.Length; s++)
            {
                int n = count, found = -1;
                for (int i = 0; i < n; i++)
                {
                    if (parents[i] != cur || (flags[i] & (Dir | Gone)) != Dir) continue;
                    if (string.Equals(names[i], segs[s], StringComparison.OrdinalIgnoreCase)) { found = i; break; }
                }
                if (found < 0) return -1;
                cur = found;
            }
            return cur;
        }
    }
}
