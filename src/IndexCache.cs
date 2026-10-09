using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Glint
{
    /// The name index on disk (%LocalAppData%\Glint\index.bin), so Glint is
    /// ready the moment you sign in. Deleted entries are dropped on save.
    internal static class IndexCache
    {
        private const string Magic = "GLINTIDX";
        private const int Version = 2;

        public sealed class Vol
        {
            public string Letter; public int Root; public bool Mft; public ulong JournalId; public long NextUsn;
            public (ulong, int)[] Frn;
        }

        public sealed class Snapshot
        {
            public int Count; public string[] Names; public int[] Parents; public byte[] Flags;
            public List<Vol> Volumes = new List<Vol>();
        }

        public static string FilePath => Path.Combine(Settings.Dir, "index.bin");

        public static void Save(Snapshot s)
        {
            // compact: drop Gone entries and renumber
            var map = new int[s.Count];
            int n = 0;
            for (int i = 0; i < s.Count; i++) map[i] = (s.Flags[i] & FileIndex.Gone) != 0 ? -1 : n++;

            Directory.CreateDirectory(Settings.Dir);
            string tmp = FilePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(new BufferedStream(gz, 1 << 20), Encoding.UTF8))
            {
                w.Write(Magic); w.Write(Version);
                w.Write(s.Volumes.Count);
                foreach (var v in s.Volumes)
                {
                    w.Write(v.Letter); w.Write(map[v.Root]); w.Write(v.Mft); w.Write(v.JournalId); w.Write(v.NextUsn);
                    if (v.Mft)
                    {
                        var live = v.Frn.Where(f => f.Item2 < s.Count && map[f.Item2] >= 0).ToArray();
                        w.Write(live.Length);
                        foreach (var (k, i) in live) { w.Write(k); w.Write(map[i]); }
                    }
                }
                w.Write(n);
                for (int i = 0; i < s.Count; i++)
                {
                    if (map[i] < 0) continue;
                    int p = s.Parents[i];
                    w.Write(s.Names[i] ?? "");
                    w.Write(p >= 0 && p < s.Count ? map[p] : -1);
                    w.Write(s.Flags[i]);
                }
                w.Write(Magic); // trailer: a truncated file never loads
            }
            File.Move(tmp, FilePath, true);
        }

        /// Loads the saved index if it covers exactly these drives.
        public static bool TryLoad(FileIndex idx, List<string> letters)
        {
            if (!File.Exists(FilePath)) return false;
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(gz, 1 << 20), Encoding.UTF8);
            if (r.ReadString() != Magic || r.ReadInt32() != Version) return false;
            var s = new Snapshot();
            int vols = r.ReadInt32();
            for (int k = 0; k < vols; k++)
            {
                var v = new Vol { Letter = r.ReadString(), Root = r.ReadInt32(), Mft = r.ReadBoolean(), JournalId = r.ReadUInt64(), NextUsn = r.ReadInt64() };
                if (v.Mft)
                {
                    int c = r.ReadInt32();
                    v.Frn = new (ulong, int)[c];
                    for (int i = 0; i < c; i++) v.Frn[i] = (r.ReadUInt64(), r.ReadInt32());
                }
                s.Volumes.Add(v);
            }
            var saved = s.Volumes.Select(v => v.Letter).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            if (!saved.SequenceEqual(letters.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) return false;
            int n = r.ReadInt32();
            s.Count = n; s.Names = new string[n]; s.Parents = new int[n]; s.Flags = new byte[n];
            for (int i = 0; i < n; i++) { s.Names[i] = r.ReadString(); s.Parents[i] = r.ReadInt32(); s.Flags[i] = r.ReadByte(); }
            if (r.ReadString() != Magic) return false;
            idx.LoadFrom(s);
            return true;
        }

        public static void Delete() { try { File.Delete(FilePath); } catch { } }
    }
}
