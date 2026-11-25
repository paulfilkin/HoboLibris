using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HoboLibris.Models;

namespace HoboLibris.Services
{
    /// <summary>
    /// The eReader's collections kept on this computer (#32): as they were at the last read or save,
    /// with the changes made since, so they can be changed with no eReader connected and the changes
    /// survive ejecting, unplugging and closing HoboLibris.  One file for each eReader, named by its
    /// serial number.  With no eReader connected, the newest file is opened offline.
    /// </summary>
    public class CollectionsStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public class Shelf
        {
            public string Name { get; set; } = "";
            /// <summary>The eReader's own id for the collection (Shelf.Id), when known.</summary>
            public string? Id { get; set; }
            public List<string> Books { get; set; } = new();
        }

        public class Snapshot
        {
            public int Version { get; set; } = 1;
            public string? Serial { get; set; }
            /// <summary>When the collections were read from the eReader (UTC).</summary>
            public DateTime Taken { get; set; }
            public List<CollectionBook> Books { get; set; } = new();
            /// <summary>The collections as read from the eReader.</summary>
            public List<Shelf> Base { get; set; } = new();
            /// <summary>The collections with the changes made since.</summary>
            public List<Shelf> Current { get; set; } = new();
            /// <summary>Collections renamed since: current name -> name as read.</summary>
            public Dictionary<string, string> Renamed { get; set; } = new();
        }

        public string Folder { get; }

        public CollectionsStore(string folder)
        {
            Folder = folder;
        }

        /// <summary>The file for an eReader: its serial number, or "kobo" when that couldn't be read.</summary>
        public string FileFor(string? serial)
        {
            var name = string.IsNullOrWhiteSpace(serial) ? "kobo" : serial.Trim();
            foreach (var bad in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(bad, '_');
            }
            return Path.Combine(Folder, name + ".json");
        }

        public static Snapshot ToSnapshot(Collections c) => new()
        {
            Serial = c.Serial,
            Taken = c.Taken,
            Books = c.Books,
            Base = c.Original.Select(kv => new Shelf { Name = kv.Key, Id = c.ShelfIds.GetValueOrDefault(kv.Key), Books = kv.Value.ToList() }).ToList(),
            Current = c.Current.Select(kv => new Shelf { Name = kv.Key, Books = kv.Value.ToList() }).ToList(),
            Renamed = new Dictionary<string, string>(c.Renamed),
        };

        /// <summary>Collections from a snapshot, opened offline.</summary>
        public static Collections FromSnapshot(Snapshot s)
        {
            var c = Collections.FromParts("", s.Serial, s.Taken, s.Books,
                s.Base.Select(b => (b.Name, b.Id, (IEnumerable<string>)b.Books)), offline: true);
            c.Restore(s.Current.Select(b => (b.Name, (IEnumerable<string>)b.Books)), s.Renamed);
            return c;
        }

        /// <summary>Keep the collections and their changes on this computer.  False (and logged) when the file couldn't be written.</summary>
        public bool Save(Collections c)
        {
            var path = FileFor(c.Serial);
            try
            {
                Directory.CreateDirectory(Folder);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(ToSnapshot(c), Options));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not keep the collections in {path}: {ex.Message}");
                return false;
            }
        }

        /// <summary>The collections kept for an eReader, offline; null when there are none.</summary>
        public Collections? Load(string? serial) => Read(FileFor(serial));

        /// <summary>The collections kept most recently, for any eReader, offline; null when there are none.</summary>
        public Collections? Latest()
        {
            if (!Directory.Exists(Folder))
            {
                return null;
            }
            return Directory.GetFiles(Folder, "*.json")
                .Select(Read)
                .Where(c => c != null)
                .OrderByDescending(c => c!.Taken)
                .FirstOrDefault();
        }

        private static Collections? Read(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Options);
                return s == null ? null : FromSnapshot(s);
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not read the collections kept in {path}: {ex.Message}");
                return null;
            }
        }
    }
}
