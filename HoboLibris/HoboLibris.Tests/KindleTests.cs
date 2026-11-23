using HoboLibris.Models;
using HoboLibris.Services;

namespace HoboLibris.Tests
{
    public class KindleTests
    {
        [Fact]
        public void CopiesBookWithSidecarAndNamesIt()
        {
            var root = TestFolder.Fresh("kindle-test");
            var docs = Path.Combine(root, "documents");
            Directory.CreateDirectory(Path.Combine(docs, "Small Gardens_B00ABCDEFG.sdr"));
            File.WriteAllText(Path.Combine(docs, "Small Gardens_B00ABCDEFG.kfx"), "book");
            File.WriteAllText(Path.Combine(docs, "Small Gardens_B00ABCDEFG.sdr", "voucher"), "v");
            var books = new List<DeviceBook>();
            KindleService.Walk(docs, new KindleService.Source("Kindle (T:)", docs, "T:"), 0, books);
            Assert.Single(books);
            Assert.Equal("Small Gardens", books[0].Title);
            Assert.Equal("KFX", books[0].Format);
            Assert.Equal("T:", books[0].Drive);
            var outDir = Path.Combine(root, "backup");
            var saved = KindleService.BackUp(books[0], outDir, null);
            Assert.True(File.Exists(saved));
            Assert.True(File.Exists(Path.Combine(outDir, "Small Gardens_B00ABCDEFG.sdr", "voucher")));
            Assert.NotNull(books[0].SavedIn(outDir));
        }

        [Fact]
        public void ReadsTheConnectionCounts()
        {
            Assert.Equal(KindleConnection.NotSeen, KindleShellService.ConnectionFrom(0, 0, 0, 0));
            Assert.Equal(KindleConnection.StorageClosed, KindleShellService.ConnectionFrom(1, 1, 1, 1));
            Assert.Equal(KindleConnection.Seen, KindleShellService.ConnectionFrom(1, 1, 0, 1));
        }

        [Fact]
        public void PairsBookWithItsSdrFolder()
        {
            var items = new List<KindleShellService.ShellEntry>
            {
                new("Kindle", new List<string> { "Internal Storage", "documents", "Small Gardens_B00ABCDEFG.kfx" }, "Small Gardens_B00ABCDEFG.kfx", 4, false),
                new("Kindle", new List<string> { "Internal Storage", "documents", "Small Gardens_B00ABCDEFG.sdr" }, "Small Gardens_B00ABCDEFG.sdr", 0, true),
            };
            var books = KindleShellService.Parse(items);
            Assert.Single(books);
            Assert.Equal("Small Gardens", books[0].Title);
            Assert.Equal("KFX", books[0].Format);
            var d = books[0].Device!;
            Assert.Equal("Small Gardens_B00ABCDEFG.sdr", d.Sidecar![^1]);
            Assert.Equal(@"C:\Backup\Small Gardens_B00ABCDEFG.kfx", books[0].TargetIn(@"C:\Backup"));
        }

        [Fact]
        public void ReadsTitleAndAuthorFromAMobiHeader()
        {
            var root = TestFolder.Fresh("mobi-test");
            var file = Path.Combine(root, "book.mobi");
            File.WriteAllBytes(file, MobiFixture("Header Title", "Exth Title", "An Author"));
            var read = KindleService.ReadMobiTitle(file);
            Assert.NotNull(read);
            Assert.Equal("Exth Title", read.Value.Title);
            Assert.Equal("An Author", read.Value.Authors);
        }

        /// <summary>A minimal PalmDOC/MOBI file: 78-byte header, record 0 with a MOBI header, an EXTH block and the full name.</summary>
        private static byte[] MobiFixture(string fullName, string exthTitle, string author)
        {
            var mobiLen = 232;
            var exth = BuildExth((100, author), (503, exthTitle));
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(fullName);
            var rec0 = new List<byte>();
            rec0.AddRange(new byte[16]); // PalmDOC header
            rec0.AddRange("MOBI"u8.ToArray());
            rec0.AddRange(BigEndian((uint)mobiLen));
            while (rec0.Count < 16 + mobiLen)
            {
                rec0.Add(0);
            }
            var nameOffset = (uint)(16 + mobiLen + exth.Length);
            Array.Copy(BigEndian(nameOffset), 0, rec0.ToArray(), 0, 0); // placeholder, set below
            var rec0Bytes = rec0.ToArray();
            Array.Copy(BigEndian(nameOffset), 0, rec0Bytes, 84, 4);
            Array.Copy(BigEndian((uint)nameBytes.Length), 0, rec0Bytes, 88, 4);
            Array.Copy(BigEndian(0x40), 0, rec0Bytes, 128, 4);
            var file = new List<byte>();
            file.AddRange(new byte[60]);
            file.AddRange("BOOKMOBI"u8.ToArray());
            file.AddRange(new byte[10]);
            const int rec0Start = 78 + 8;
            file.AddRange(BigEndian((uint)rec0Start));
            file.AddRange(new byte[4]);
            while (file.Count < rec0Start)
            {
                file.Add(0);
            }
            file.AddRange(rec0Bytes);
            file.AddRange(exth);
            file.AddRange(nameBytes);
            return file.ToArray();
        }

        private static byte[] BuildExth(params (uint Kind, string Text)[] records)
        {
            var body = new List<byte>();
            foreach (var (kind, text) in records)
            {
                var data = System.Text.Encoding.UTF8.GetBytes(text);
                body.AddRange(BigEndian(kind));
                body.AddRange(BigEndian((uint)(8 + data.Length)));
                body.AddRange(data);
            }
            var exth = new List<byte>();
            exth.AddRange("EXTH"u8.ToArray());
            exth.AddRange(BigEndian((uint)(12 + body.Count)));
            exth.AddRange(BigEndian((uint)records.Length));
            exth.AddRange(body);
            return exth.ToArray();
        }

        private static byte[] BigEndian(uint v)
        {
            var b = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, v);
            return b;
        }
    }
}
