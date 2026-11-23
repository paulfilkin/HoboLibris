using System.Buffers.Binary;
using System.IO;
using System.Text;
using HoboLibris.Models;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>
    /// Kindle backup: finds the books already on a plugged-in Kindle (a drive with "documents" and
    /// "system" folders, or a media device with no drive letter, see KindleShellService) or in the
    /// Kindle for PC content folder, and copies them, unchanged, into a backup folder. Nothing is
    /// decrypted or converted.
    /// </summary>
    public static class KindleService
    {
        /// <summary>File types Kindle books come in.</summary>
        public static readonly string[] BookExtensions = { "azw", "azw3", "azw4", "kfx", "mobi", "prc", "pdf" };

        public record Source(string Origin, string Root, string? Drive);

        /// <summary>Places books may be: connected Kindles, then Kindle for PC, then folders the person added.</summary>
        public static List<Source> Sources(IReadOnlyList<string> extra)
        {
            var found = new List<Source>();
            for (var letter = 'D'; letter <= 'Z'; letter++)
            {
                var drive = $"{letter}:";
                var root = drive + "\\";
                try
                {
                    var docs = Path.Combine(root, "documents");
                    if (Directory.Exists(docs) && Directory.Exists(Path.Combine(root, "system")))
                    {
                        found.Add(new Source(string.Format(Strings.Kindle_Origin_Drive, drive), docs, drive));
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Debug($"Could not look at drive {drive}: {ex.Message}");
                }
            }
            var pc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "My Kindle Content");
            if (Directory.Exists(pc))
            {
                found.Add(new Source(Strings.Kindle_Origin_ForPc, pc, null));
            }
            foreach (var e in extra)
            {
                if (Directory.Exists(e))
                {
                    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(e));
                    if (string.IsNullOrEmpty(name))
                    {
                        name = e;
                    }
                    found.Add(new Source(string.Format(Strings.Kindle_Origin_Folder, name), e, null));
                }
            }
            return found;
        }

        private static bool IsBook(string path)
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return BookExtensions.Contains(ext);
        }

        private static long SizeOf(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    return Directory.EnumerateFileSystemEntries(path).Sum(SizeOf);
                }
                return new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                LogManager.Debug($"No size for {path}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Eject a drive ("D:") so the device goes back to reading and charging.</summary>
        public static void Eject(string drive) => KindleShellService.Eject(drive);

        /// <summary>When no Kindle could be read: what Windows sees, and what to do, in plain words.</summary>
        public static string? ConnectionNote()
        {
            return KindleShellService.Connection() switch
            {
                KindleConnection.NotSeen => Strings.Kindle_Note_NotSeen,
                KindleConnection.StorageClosed => Strings.Kindle_Note_StorageClosed,
                KindleConnection.Seen => Strings.Kindle_Note_Seen,
                _ => null,
            };
        }

        /// <summary>Look through every source for books. Also returns how many places were found.</summary>
        public static (List<DeviceBook> Books, int Sources) Scan(IReadOnlyList<string> extra)
        {
            var books = new List<DeviceBook>();
            var places = Sources(extra);
            var count = places.Count;
            foreach (var place in places)
            {
                Walk(place.Root, place, 0, books);
            }
            var (deviceBooks, devices) = KindleShellService.Scan();
            count += devices;
            books.AddRange(deviceBooks);
            books.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            return (books, count);
        }

        public static void Walk(string dir, Source source, int depth, List<DeviceBook> books)
        {
            if (depth > 4)
            {
                return;
            }
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch (Exception ex)
            {
                LogManager.Debug($"Could not list {dir}: {ex.Message}");
                return;
            }
            Array.Sort(entries, StringComparer.Ordinal);
            foreach (var p in entries)
            {
                var name = Path.GetFileName(p);
                if (Directory.Exists(p))
                {
                    var upper = name.ToUpperInvariant();
                    // A book's sidecar folder is copied with its book, not scanned on its own.
                    if (upper.EndsWith(".SDR", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    // Kindle for PC keeps each book in its own <ASIN>_EBOK / _PDOC folder: copy it whole.
                    if (upper.EndsWith("_EBOK", StringComparison.Ordinal) || upper.EndsWith("_PDOC", StringComparison.Ordinal))
                    {
                        var file = FirstBookFile(p);
                        if (file != null)
                        {
                            books.Add(Describe(p, file, source));
                        }
                        continue;
                    }
                    Walk(p, source, depth + 1, books);
                }
                else if (IsBook(p))
                {
                    books.Add(Describe(p, p, source));
                }
            }
        }

        private static string? FirstBookFile(string dir)
        {
            try
            {
                var files = Directory.GetFiles(dir);
                Array.Sort(files, StringComparer.Ordinal);
                return files.FirstOrDefault(IsBook);
            }
            catch (Exception ex)
            {
                LogManager.Debug($"Could not list {dir}: {ex.Message}");
                return null;
            }
        }

        private static DeviceBook Describe(string source, string file, Source place)
        {
            var format = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
            var (title, authors) = ReadMobiTitle(file) ?? ("", "");
            if (title.Length == 0)
            {
                title = FileNames.TitleFromName(file);
            }
            // A book on the device has a sidecar folder <name>.sdr beside it (KFX books need it).
            var size = SizeOf(source);
            var sdr = Sidecar(source);
            if (sdr != null)
            {
                size += SizeOf(sdr);
            }
            return new DeviceBook
            {
                Source = source,
                Title = title,
                Authors = authors,
                Format = format,
                Origin = place.Origin,
                Drive = place.Drive,
                Size = size,
            };
        }

        public static string? Sidecar(string source)
        {
            if (Directory.Exists(source))
            {
                return null;
            }
            var stem = Path.GetFileNameWithoutExtension(source);
            var p = Path.Combine(Path.GetDirectoryName(source) ?? "", stem + ".sdr");
            return Directory.Exists(p) ? p : null;
        }

        private static uint? BeU32(byte[] b, int at) =>
            at >= 0 && at + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at, 4)) : null;

        /// <summary>
        /// Title and author from a MOBI / AZW / AZW3 header (EXTH 503 title, 100 author, else the
        /// full name in the MOBI header). Other formats give null.
        /// </summary>
        public static (string Title, string Authors)? ReadMobiTitle(string file)
        {
            byte[] head;
            try
            {
                using var f = File.OpenRead(file);
                head = new byte[256 * 1024];
                var total = 0;
                int n;
                while (total < head.Length && (n = f.Read(head, total, head.Length - total)) > 0)
                {
                    total += n;
                }
                if (total < head.Length)
                {
                    head = head[..total];
                }
            }
            catch (Exception ex)
            {
                LogManager.Debug($"Could not read {file}: {ex.Message}");
                return null;
            }
            if (head.Length < 68 || !head.AsSpan(60, 8).SequenceEqual("BOOKMOBI"u8))
            {
                return null;
            }
            if (BeU32(head, 78) is not { } rec0u)
            {
                return null;
            }
            var rec0 = (int)rec0u;
            if (rec0 < 0 || rec0 + 20 > head.Length || !head.AsSpan(rec0 + 16, 4).SequenceEqual("MOBI"u8))
            {
                return null;
            }
            if (BeU32(head, rec0 + 20) is not { } mobiLen)
            {
                return null;
            }
            var title = "";
            var authors = new List<string>();
            if (BeU32(head, rec0 + 84) is { } nameOff && BeU32(head, rec0 + 88) is { } nameLen)
            {
                var start = rec0 + (long)nameOff;
                var end = start + nameLen;
                if (start >= 0 && end <= head.Length)
                {
                    title = Encoding.UTF8.GetString(head, (int)start, (int)nameLen).Trim();
                }
            }
            var exthFlags = BeU32(head, rec0 + 128) ?? 0;
            var exth = rec0 + 16 + (long)mobiLen;
            if ((exthFlags & 0x40) != 0 && exth + 12 <= head.Length && head.AsSpan((int)exth, 4).SequenceEqual("EXTH"u8))
            {
                if (BeU32(head, (int)exth + 8) is { } count)
                {
                    var at = (int)exth + 12;
                    for (var i = 0; i < Math.Min(count, 500); i++)
                    {
                        if (BeU32(head, at) is not { } kind || BeU32(head, at + 4) is not { } len)
                        {
                            break;
                        }
                        if (len < 8 || at + len > head.Length)
                        {
                            break;
                        }
                        var text = Encoding.UTF8.GetString(head, at + 8, (int)len - 8).Trim();
                        if (kind == 100 && text.Length > 0)
                        {
                            authors.Add(text);
                        }
                        else if (kind == 503 && text.Length > 0)
                        {
                            title = text;
                        }
                        at += (int)len;
                    }
                }
            }
            return (title, string.Join(", ", authors));
        }

        private static void CopyTree(string from, string to, ref long done, long total, IProgress<double>? progress)
        {
            if (Directory.Exists(from))
            {
                Directory.CreateDirectory(to);
                foreach (var e in Directory.EnumerateFileSystemEntries(from))
                {
                    CopyTree(e, Path.Combine(to, Path.GetFileName(e)), ref done, total, progress);
                }
                return;
            }
            var part = Path.ChangeExtension(to, ".part");
            using (var r = File.OpenRead(from))
            using (var w = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[256 * 1024];
                int n;
                while ((n = r.Read(buffer, 0, buffer.Length)) > 0)
                {
                    w.Write(buffer, 0, n);
                    done += n;
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(1.0, (double)done / total));
                    }
                }
            }
            File.Move(part, to, overwrite: true);
        }

        /// <summary>Copy a book (and its sidecar folder, if any) into the backup folder, unchanged.</summary>
        public static string BackUp(DeviceBook book, string dir, IProgress<double>? progress)
        {
            var target = book.TargetIn(dir);
            try
            {
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Kindle_CouldNotCreateFolder, ex.Message), ex);
            }
            if (book.Device != null)
            {
                return KindleShellService.BackUp(book.Device, book.Size, target, progress);
            }
            long done = 0;
            try
            {
                CopyTree(book.Source, target, ref done, book.Size, progress);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new HoboLibrisException(string.Format(Strings.Kindle_CouldNotCopyBook, ex.Message), ex);
            }
            var sdr = Sidecar(book.Source);
            if (sdr != null)
            {
                var to = Path.Combine(Path.GetDirectoryName(target) ?? dir, Path.GetFileName(sdr));
                try
                {
                    CopyTree(sdr, to, ref done, book.Size, progress);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new HoboLibrisException(string.Format(Strings.Kindle_CouldNotCopyExtraFiles, ex.Message), ex);
                }
            }
            return target;
        }
    }
}
