using System.IO;
using System.Management;
using HoboLibris.Models;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    public enum KindleConnection
    {
        /// <summary>Windows sees no Amazon device at all.</summary>
        NotSeen,
        /// <summary>The Kindle is connected but its storage reports no media.</summary>
        StorageClosed,
        /// <summary>The Kindle is connected; nothing looks wrong.</summary>
        Seen,
        /// <summary>The check itself didn't work.</summary>
        Unknown,
    }

    /// <summary>
    /// Kindles that connect as media devices (shown in File Explorer under This PC with no drive
    /// letter). Windows only lets programs browse these through its shell, so the listing and the
    /// copying use the same Shell.Application objects File Explorer uses. Files are copied unchanged.
    /// </summary>
    public static class KindleShellService
    {
        private const int ThisPc = 17; // ssfDRIVES

        public record ShellEntry(string Device, List<string> Path, string Name, long Size, bool Folder);

        private static dynamic Shell() =>
            Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")
                                     ?? throw new InvalidOperationException("Shell.Application is not available"))!;

        /// <summary>Lists the book files (and ".sdr" folders) under "documents" on every media device.</summary>
        private static (List<ShellEntry> Items, int Devices) ListDevices()
        {
            var items = new List<ShellEntry>();
            var devices = 0;
            var exts = KindleService.BookExtensions.Select(e => "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
            dynamic shell = Shell();
            dynamic pc = shell.NameSpace(ThisPc);
            foreach (dynamic dev in pc.Items())
            {
                if ((bool)dev.IsFileSystem)
                {
                    continue;
                }
                dynamic? df = dev.GetFolder;
                if (df == null)
                {
                    continue;
                }
                var found = false;
                string deviceName = dev.Name;
                foreach (dynamic storage in df.Items())
                {
                    if (!(bool)storage.IsFolder)
                    {
                        continue;
                    }
                    string storageName = storage.Name;
                    dynamic? storageFolder = storage.GetFolder;
                    if (storageFolder == null)
                    {
                        continue;
                    }
                    foreach (dynamic top in storageFolder.Items())
                    {
                        if ((bool)top.IsFolder && string.Equals((string)top.Name, "documents", StringComparison.Ordinal))
                        {
                            found = true;
                            WalkDevice(top.GetFolder, deviceName, new List<string> { storageName, "documents" }, 0, exts, items);
                        }
                    }
                }
                if (found)
                {
                    devices++;
                }
            }
            return (items, devices);
        }

        private static void WalkDevice(dynamic folder, string device, List<string> parts, int depth, HashSet<string> exts, List<ShellEntry> items)
        {
            if (depth > 5 || folder == null)
            {
                return;
            }
            foreach (dynamic it in folder!.Items())
            {
                string name = it.Name;
                var p = new List<string>(parts) { name };
                if ((bool)it.IsFolder)
                {
                    if (name.EndsWith(".sdr", StringComparison.OrdinalIgnoreCase))
                    {
                        items.Add(new ShellEntry(device, p, name, 0, true));
                    }
                    else
                    {
                        WalkDevice(it.GetFolder, device, p, depth + 1, exts, items);
                    }
                }
                else if (exts.Contains(Path.GetExtension(name)))
                {
                    long size = 0;
                    try
                    {
                        var value = it.ExtendedProperty("System.Size");
                        if (value != null)
                        {
                            size = Convert.ToInt64(value);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager.Debug($"No size for {name} on {device}: {ex.Message}");
                    }
                    items.Add(new ShellEntry(device, p, name, size, false));
                }
            }
        }

        /// <summary>Books on every media-device Kindle, and how many such devices have a "documents" folder.</summary>
        public static (List<DeviceBook> Books, int Devices) Scan()
        {
            try
            {
                var (items, devices) = StaThread.Run(ListDevices);
                return (Parse(items), devices);
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not look for media-device Kindles: {ex.Message}");
                return (new List<DeviceBook>(), 0);
            }
        }

        public static List<DeviceBook> Parse(List<ShellEntry> items)
        {
            var folders = items.Where(e => e.Folder).ToList();
            var books = new List<DeviceBook>();
            foreach (var e in items.Where(e => !e.Folder && e.Path.Count > 0))
            {
                var parent = e.Path.Take(e.Path.Count - 1).ToList();
                var sdrName = Path.GetFileNameWithoutExtension(e.Name) + ".sdr";
                var sidecar = folders.FirstOrDefault(f =>
                    f.Device == e.Device
                    && f.Path.Count == e.Path.Count
                    && f.Path.Take(f.Path.Count - 1).SequenceEqual(parent)
                    && f.Name.Equals(sdrName, StringComparison.OrdinalIgnoreCase))?.Path;
                books.Add(new DeviceBook
                {
                    Source = $"device:{e.Device}/{string.Join("/", e.Path)}",
                    Title = FileNames.TitleFromName(e.Name),
                    Authors = "",
                    Format = Path.GetExtension(e.Name).TrimStart('.').ToUpperInvariant(),
                    Origin = e.Device,
                    Size = Math.Max(0, e.Size),
                    Device = new DeviceRef { Device = e.Device, Path = e.Path, Sidecar = sidecar },
                });
            }
            return books;
        }

        /// <summary>
        /// What Windows sees when no Kindle storage could be read. Only fast queries are used: the
        /// disk list for Kindles with a drive letter, and This PC for those without.
        /// </summary>
        public static KindleConnection Connection()
        {
            try
            {
                var disks = 0;
                var noMedia = 0;
                using (var searcher = new ManagementObjectSearcher("SELECT Model, Size FROM Win32_DiskDrive WHERE Model LIKE '%Kindle%'"))
                {
                    foreach (var disk in searcher.Get())
                    {
                        disks++;
                        var size = disk["Size"];
                        if (size == null || Convert.ToUInt64(size) == 0)
                        {
                            noMedia++;
                        }
                    }
                }
                var wpd = StaThread.Run(() =>
                {
                    var count = 0;
                    dynamic shell = Shell();
                    foreach (dynamic it in shell.NameSpace(ThisPc).Items())
                    {
                        if (!(bool)it.IsFileSystem && ((string)it.Name).Contains("Kindle", StringComparison.OrdinalIgnoreCase))
                        {
                            count++;
                        }
                    }
                    return count;
                });
                return ConnectionFrom(disks, disks, noMedia, wpd);
            }
            catch (Exception ex)
            {
                LogManager.Warn($"The Kindle connection check failed: {ex.Message}");
                return KindleConnection.Unknown;
            }
        }

        public static KindleConnection ConnectionFrom(int usb, int disks, int noMedia, int wpd)
        {
            if (usb == 0 && wpd == 0 && disks == 0)
            {
                return KindleConnection.NotSeen;
            }
            if (disks > 0 && noMedia == disks)
            {
                return KindleConnection.StorageClosed;
            }
            return KindleConnection.Seen;
        }

        /// <summary>Safely ejects a drive (e.g. "D:") the same way File Explorer's Eject does.</summary>
        public static void Eject(string drive)
        {
            StaThread.Run(() =>
            {
                dynamic shell = Shell();
                dynamic? item = shell.NameSpace(ThisPc).ParseName(drive);
                if (item == null)
                {
                    throw new HoboLibrisException(Strings.Kindle_NoLongerConnected);
                }
                item.InvokeVerb("Eject");
                Thread.Sleep(2000);
            });
        }

        private static dynamic FindItem(dynamic folder, string name)
        {
            foreach (dynamic it in folder!.Items())
            {
                if (string.Equals((string)it.Name, name, StringComparison.Ordinal))
                {
                    return it;
                }
            }
            return null!;
        }

        /// <summary>
        /// Copies one item (file or folder) from the device into a folder, and waits until it has
        /// arrived: the shell copies in the background.
        /// </summary>
        private static void CopyItem(string device, IReadOnlyList<string> path, string dest, long size, bool folder)
        {
            StaThread.Run(() =>
            {
                dynamic shell = Shell();
                dynamic pc = shell.NameSpace(ThisPc);
                dynamic? dev = null;
                foreach (dynamic d in pc.Items())
                {
                    if (string.Equals((string)d.Name, device, StringComparison.Ordinal) && !(bool)d.IsFileSystem)
                    {
                        dev = d;
                    }
                }
                if (dev == null)
                {
                    throw new HoboLibrisException(Strings.Kindle_NoLongerConnected);
                }
                dynamic f = dev.GetFolder;
                for (var i = 0; i < path.Count - 1; i++)
                {
                    dynamic? next = FindItem(f, path[i]);
                    if (next == null)
                    {
                        throw new HoboLibrisException(string.Format(Strings.Kindle_FolderNotFound, path[i]));
                    }
                    f = next.GetFolder;
                }
                dynamic? item = FindItem(f, path[^1]);
                if (item == null)
                {
                    throw new HoboLibrisException(Strings.Kindle_BookNoLongerOnKindle);
                }
                Directory.CreateDirectory(dest);
                dynamic destFolder = shell.NameSpace(dest);
                // FOF_SILENT | FOF_RENAMEONCOLLISION off | FOF_NOCONFIRMATION | FOF_NOCONFIRMMKDIR | FOF_NOERRORUI
                destFolder.CopyHere(item, 4 + 16 + 512 + 1024);
                var target = Path.Combine(dest, path[^1]);
                var deadline = DateTime.UtcNow.AddSeconds(900);
                long last = -1;
                var stable = 0;
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(500);
                    if (!File.Exists(target) && !Directory.Exists(target))
                    {
                        continue;
                    }
                    var current = SizeOnDisk(target);
                    if (!folder && size > 0 && current >= size)
                    {
                        Thread.Sleep(500);
                        return;
                    }
                    stable = current == last ? stable + 1 : 0;
                    last = current;
                    if (stable >= 4 && (current > 0 || folder))
                    {
                        return;
                    }
                }
                throw new HoboLibrisException(Strings.Kindle_CopyTookTooLong);
            });
        }

        private static long SizeOnDisk(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                }
                return new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                LogManager.Debug($"No size for {path}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Copy a book, and its ".sdr" folder if it has one, from the device to target.</summary>
        public static string BackUp(DeviceRef d, long size, string target, IProgress<double>? progress)
        {
            var dest = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(dest))
            {
                throw new HoboLibrisException(Strings.Kindle_NoBackupFolder);
            }
            try
            {
                File.Delete(target);
            }
            catch (Exception ex)
            {
                LogManager.Debug($"Could not remove the old copy {target}: {ex.Message}");
            }
            progress?.Report(0.05);
            CopyItem(d.Device, d.Path, dest, size, false);
            if (d.Sidecar != null && d.Sidecar.Count > 0)
            {
                progress?.Report(0.6);
                var old = Path.Combine(dest, d.Sidecar[^1]);
                try
                {
                    if (Directory.Exists(old))
                    {
                        Directory.Delete(old, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Debug($"Could not remove the old sidecar {old}: {ex.Message}");
                }
                CopyItem(d.Device, d.Sidecar, dest, 0, true);
            }
            progress?.Report(1.0);
            return target;
        }
    }
}
