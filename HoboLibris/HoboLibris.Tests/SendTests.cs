using System.IO.Compression;
using HoboLibris.Models;
using HoboLibris.Services;

namespace HoboLibris.Tests
{
    public class SendTests
    {
        private static void Epub(string path, bool isProtected)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var w = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open()))
            {
                w.Write("application/epub+zip");
            }
            using (var w = new StreamWriter(zip.CreateEntry("META-INF/container.xml").Open()))
            {
                w.Write("<container/>");
            }
            if (isProtected)
            {
                using var w = new StreamWriter(zip.CreateEntry("META-INF/rights.xml").Open());
                w.Write("<rights/>");
            }
        }

        [Fact]
        public void SortsOutWhatToSend()
        {
            var dir = TestFolder.Fresh("send-test");
            Epub(Path.Combine(dir, "New Book - Someone.epub"), false);
            Epub(Path.Combine(dir, "Locked Book - Someone.epub"), true);
            Epub(Path.Combine(dir, "On Reader - Writer.kepub.epub"), false);
            Epub(Path.Combine(dir, "Store Title - Author.epub"), false);
            File.WriteAllText(Path.Combine(dir, "Broken - X.epub"), "not a zip");
            File.WriteAllText(Path.Combine(dir, "Paper - Y.pdf"), "%PDF-1.7 ...");
            File.WriteAllText(Path.Combine(dir, "Ticket.acsm"), "<fulfillmentToken/>");
            File.WriteAllText(Path.Combine(dir, "Notes.docx"), "ignored");

            var reader = new List<DeviceBook>
            {
                new()
                {
                    Source = @"E:\Books\On Reader - Writer.kepub.epub",
                    Title = "On Reader",
                    Authors = "Writer",
                    Format = "KEPUB",
                    Origin = "Kobo eReader (E:)",
                    Drive = "E:",
                    Size = 1,
                    TargetRel = "On Reader - Writer.kepub.epub",
                },
            };
            var store = new List<(string, string?)> { ("Store Title", null) };

            // Without an eReader nothing is "Ready".
            var unplugged = SendService.ScanFolder(dir, reader, store, false);
            Assert.All(unplugged, i => Assert.NotEqual(VerdictKind.Ready, i.Verdict.Kind));
            Assert.Contains(unplugged, i => i.Verdict.Kind == VerdictKind.Unchecked);

            var items = SendService.ScanFolder(dir, reader, store, true);
            VerdictKind? Verdict(string name) => items.FirstOrDefault(i => i.Path.EndsWith(name))?.Verdict.Kind;
            Assert.Equal(VerdictKind.Ready, Verdict("New Book - Someone.epub"));
            Assert.Equal(VerdictKind.Ready, Verdict("Paper - Y.pdf"));
            Assert.Equal(VerdictKind.Protected, Verdict("Locked Book - Someone.epub"));
            Assert.Equal(VerdictKind.OnReader, Verdict("On Reader - Writer.kepub.epub"));
            Assert.Equal(VerdictKind.StoreBook, Verdict("Store Title - Author.epub"));
            Assert.Equal(VerdictKind.NotKobo, Verdict("Broken - X.epub"));
            Assert.Equal(VerdictKind.NotKobo, Verdict("Ticket.acsm"));
            Assert.Null(Verdict("Notes.docx"));

            // Sending copies into the eReader's Books folder and never overwrites.
            var readerRoot = Path.Combine(dir, "ereader");
            var item = items.First(i => i.Path.EndsWith("New Book - Someone.epub"));
            var sent = SendService.Send(item, readerRoot, null);
            Assert.Equal(Path.Combine(readerRoot, "Books", "New Book - Someone.epub"), sent);
            Assert.Throws<HoboLibrisException>(() => { SendService.Send(item, readerRoot, null); });
        }
    }
}

