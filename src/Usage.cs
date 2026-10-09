using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Glint
{
    /// Remembers what you open from Glint so it rises next time ("frecency":
    /// how often, weighted by how recently). Kept in %LocalAppData%\Glint\usage.json.
    public static class Usage
    {
        public sealed class Entry { public int Count { get; set; } public DateTime Last { get; set; } }

        private static ConcurrentDictionary<string, Entry> map = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static string FilePath => Path.Combine(Settings.Dir, "usage.json");

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
                if (d != null) map = new ConcurrentDictionary<string, Entry>(d, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        public static void Record(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var e = map.GetOrAdd(key, _ => new Entry());
            e.Count++; e.Last = DateTime.Now;
            Save();
        }

        /// 0 for things you never opened; up to about 400 for daily favourites.
        public static int Bonus(string key)
        {
            if (map.IsEmpty || key == null || !map.TryGetValue(key, out var e)) return 0;
            double days = Math.Max(0, (DateTime.Now - e.Last).TotalDays);
            double recency = days < 1 ? 1.0 : days < 7 ? 0.8 : days < 30 ? 0.5 : days < 90 ? 0.25 : 0.1;
            return (int)Math.Min(400, 110 * Math.Log2(1 + e.Count) * recency);
        }

        /// Your most-used things, strongest first.
        public static List<string> Top(int n) =>
            map.OrderByDescending(kv => Bonus(kv.Key)).ThenByDescending(kv => kv.Value.Last).Take(n).Select(kv => kv.Key).ToList();

        public static void Clear() { map.Clear(); Save(); }

        private static void Save()
        {
            try
            {
                // keep the 2,000 strongest so the file stays small
                var keep = map.OrderByDescending(kv => Bonus(kv.Key)).ThenByDescending(kv => kv.Value.Last).Take(2000)
                              .ToDictionary(kv => kv.Key, kv => kv.Value);
                Directory.CreateDirectory(Settings.Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(keep));
            }
            catch { }
        }
    }
}
