using System.IO;
using System.IO.Compression;
using HoboLibris.Models;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>
    /// Send to eReader: books put in the Kobo folder by hand (not bought in the Kobo store, not
    /// copied from the eReader) are checked and copied to the Kobo eReader's "Books" folder. The
    /// eReader adds them to its library when it is ejected. Nothing is ever deleted on either side.
    /// </summary>
    public static class SendService
    {
        /// <summary>Folder on the eReader the books are copied into.</summary>
        public const string ReaderFolder = "Books";

        /// <summary>File types a Kobo eReader opens.</summary>
        private static readonly string[] KoboTypes = { "epub", "pdf", "mobi", "txt", "rtf", "cbz", "cbr" };
        /// <summary>Other book files people keep, listed so they don't silently go missing.</summary>
        private static readonly string[] OtherBookTypes = { "acsm", "azw", "azw3", "azw4", "kfx", "prc", "djvu", "fb2", "lit" };

        private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
        private static readonly byte[] MobiMagic = "BOOKMOBI"u8.ToArray();

        /// <summary>Whether a Kobo can open the file: the type, damage, and copy protection.</summary>
        public static Verdict CheckFile(string path, string ext)
        {
            switch (ext)
            {
                case "epub":
                {
                    ZipArchive zip;
                    try
                    {
                        zip = ZipFile.OpenRead(path);
                    }
                    catch (IOException)
                    {
                        return Verdict.NotKobo(Strings.Send_FileCouldNotBeOpened);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return Verdict.NotKobo(Strings.Send_FileCouldNotBeOpened);
                    }
                    catch (InvalidDataException)
                    {
                        return Verdict.NotKobo(Strings.Send_DamagedEpubNotZip);
                    }
                    using (zip)
                    {
                        HashSet<string> names;
                        try
                        {
                            names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
                        }
                        catch (InvalidDataException)
                        {
                            return Verdict.NotKobo(Strings.Send_DamagedEpubNotZip);
                        }
                        if (!names.Contains("META-INF/container.xml"))
                        {
                            return Verdict.NotKobo(Strings.Send_DamagedEpubNoContainer);
                        }
                        if (names.Contains("META-INF/rights.xml"))
                        {
                            return Verdict.Protected(Strings.Send_AdobeProtected);
                        }
                        return Verdict.Ready;
                    }
                }
                case "pdf":
                {
                    var head = ReadHead(path, 5);
                    return head.AsSpan().SequenceEqual(PdfMagic) ? Verdict.Ready : Verdict.NotKobo(Strings.Send_DamagedPdf);
                }
                case "mobi":
                {
                    var head = ReadHead(path, 4096);
                    if (head.Length < 68 || !head.AsSpan(60, 8).SequenceEqual(MobiMagic))
                    {
                        return Verdict.NotKobo(Strings.Send_DamagedMobi);
                    }
                    var encryption = 0;
                    if (head.Length >= 82)
                    {
                        var rec0 = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(78, 4));
                        if (rec0 + 14 <= head.Length)
                        {
                            encryption = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(rec0 + 12, 2));
                        }
                    }
                    return encryption != 0 ? Verdict.Protected(Strings.Send_KindleProtected) : Verdict.Ready;
                }
                default:
                    return Verdict.Ready;
            }
        }

        private static byte[] ReadHead(string path, int count)
        {
            try
            {
                using var f = File.OpenRead(path);
                var buffer = new byte[count];
                var total = 0;
                int n;
                while (total < count && (n = f.Read(buffer, total, count - total)) > 0)
                {
                    total += n;
                }
                return total == count ? buffer : buffer[..total];
            }
            catch (Exception ex)
            {
                LogManager.Debug($"Could not read {path}: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        private static void Walk(string dir, int depth, List<string> outFiles)
        {
            if (depth > 3)
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
                if (name.StartsWith('.') || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (Directory.Exists(p))
                {
                    Walk(p, depth + 1, outFiles);
                }
                else
                {
                    outFiles.Add(p);
                }
            }
        }

        /// <summary>
        /// Book files in the Kobo folder, each with what sending it would do. store holds each
        /// store book's title and its saved copy, if any. Without a connected eReader no book can
        /// be called "Ready": they are "Unchecked".
        /// </summary>
        public static List<SendItem> ScanFolder(string dir, IReadOnlyList<DeviceBook> reader, IReadOnlyList<(string Title, string? SavedPath)> store, bool readerConnected)
        {
            var readerNames = reader.Select(b => b.FileName.ToLowerInvariant()).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
            var readerCopies = reader.Select(b => b.SavedIn(dir)).Where(p => p != null).Select(p => p!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var storeCopies = store.Where(s => s.SavedPath != null).Select(s => s.SavedPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var files = new List<string>();
            if (Directory.Exists(dir))
            {
                Walk(dir, 0, files);
            }
            var items = new List<SendItem>();
            foreach (var path in files)
            {
                var (ext, format) = FileNames.LowerExtension(path);
                var known = KoboTypes.Contains(ext);
                if (!known && !OtherBookTypes.Contains(ext))
                {
                    continue;
                }
                var (title, author) = FileNames.TitleAuthor(path);
                var name = Path.GetFileName(path).ToLowerInvariant();
                Verdict verdict;
                if (storeCopies.Contains(path) || store.Any(s => TitleMatching.SameTitle(s.Title, title)))
                {
                    verdict = Verdict.StoreBook;
                }
                else if (readerCopies.Contains(path) || readerNames.Contains(name) || reader.Any(b => TitleMatching.SameTitle(b.Title, title)))
                {
                    verdict = Verdict.OnReader;
                }
                else if (!known)
                {
                    verdict = Verdict.NotKobo(ext == "acsm"
                        ? Strings.Send_AcsmTicket
                        : string.Format(Strings.Send_KoboDoesNotOpen, format));
                }
                else
                {
                    var v = CheckFile(path, ext);
                    verdict = v.Kind == VerdictKind.Ready && !readerConnected ? Verdict.Unchecked : v;
                }
                long size = 0;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (Exception ex)
                {
                    LogManager.Debug($"No size for {path}: {ex.Message}");
                }
                items.Add(new SendItem { Path = path, Title = title, Author = author, Format = format, Size = size, Verdict = verdict });
            }
            // Ready first, then the rest, each by title.
            items.Sort((a, b) =>
            {
                var ra = a.Verdict.Kind == VerdictKind.Ready ? 0 : 1;
                var rb = b.Verdict.Kind == VerdictKind.Ready ? 0 : 1;
                if (ra != rb)
                {
                    return ra.CompareTo(rb);
                }
                return string.CompareOrdinal(TitleMatching.Normalise(a.Title), TitleMatching.Normalise(b.Title));
            });
            return items;
        }

        /// <summary>Copy one book to "eReader\Books\same file name". Never overwrites.</summary>
        public static string Send(SendItem item, string readerRoot, IProgress<double>? progress)
        {
            var folder = Path.Combine(readerRoot, ReaderFolder);
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Send_CouldNotCreateBooksFolder, ex.Message), ex);
            }
            var fileName = Path.GetFileName(item.Path);
            var target = Path.Combine(folder, fileName);
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new HoboLibrisException(Strings.Send_AlreadyOnReader);
            }
            var part = Path.Combine(folder, fileName + ".part");
            try
            {
                using var r = File.OpenRead(item.Path);
                using (var w = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[256 * 1024];
                    long done = 0;
                    int n;
                    while ((n = r.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        try
                        {
                            w.Write(buffer, 0, n);
                        }
                        catch (IOException ex)
                        {
                            throw new HoboLibrisException(string.Format(Strings.Send_CouldNotWriteToReader, ex.Message), ex);
                        }
                        done += n;
                        if (item.Size > 0)
                        {
                            progress?.Report(Math.Min(1.0, (double)done / item.Size));
                        }
                    }
                    w.Flush();
                }
            }
            catch (HoboLibrisException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new HoboLibrisException(string.Format(Strings.Send_CouldNotReadBook, ex.Message), ex);
            }
            try
            {
                File.Move(part, target);
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Send_CouldNotFinishCopy, ex.Message), ex);
            }
            return target;
        }
    }
}
