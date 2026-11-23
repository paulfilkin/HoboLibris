using Microsoft.Data.Sqlite;
using HoboLibris.Services;

namespace HoboLibris.Tests
{
    public class KoboDeviceTests
    {
        [Fact]
        public void ReadsTitlesFromTheEreaderBookList()
        {
            var root = TestFolder.Fresh("kobo-db-test");
            Directory.CreateDirectory(Path.Combine(root, ".kobo", "kepub"));
            var db = Path.Combine(root, ".kobo", "KoboReader.sqlite");
            using (var c = new SqliteConnection($"Data Source={db}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"CREATE TABLE content (ContentID TEXT, ContentType TEXT, Title TEXT, Attribution TEXT);
                    INSERT INTO content VALUES ('file:///mnt/onboard/Books/My_Book.epub', 6, 'My Own Book', 'Me Myself');
                    INSERT INTO content VALUES ('file:///mnt/onboard/Books/My_Book.epub#(0)ch1.html', 9, 'Chapter 1', NULL);
                    INSERT INTO content VALUES ('0a1b2c3d-store-book', 6, 'The Quiet Harbour', 'A. Writer');
                    INSERT INTO content VALUES ('not-downloaded-store-book', 6, 'Elsewhere', 'B. Author');";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            File.WriteAllText(Path.Combine(root, ".kobo", "kepub", "0a1b2c3d-store-book"), "z");

            var (added, store) = KoboDeviceService.ReadLibrary(root);
            Assert.Equal(1, store);
            Assert.True(added.TryGetValue("books/my_book.epub", out var e));
            Assert.Equal("My Own Book", e.Title);
            Assert.Equal("Me Myself", e.Authors);
            Assert.Single(added);
        }

        [Fact]
        public void FindsAddedBooksAndSkipsHiddenFolders()
        {
            var root = TestFolder.Fresh("kobo-reader-test");
            Directory.CreateDirectory(Path.Combine(root, ".kobo", "kepub"));
            Directory.CreateDirectory(Path.Combine(root, "Books"));
            File.WriteAllText(Path.Combine(root, "Books", "My_Own_Book.kepub.epub"), "x");
            File.WriteAllText(Path.Combine(root, "Notes.pdf"), "y");
            File.WriteAllText(Path.Combine(root, ".kobo", "kepub", "abc"), "z");
            var files = new List<string>();
            KoboDeviceService.Walk(root, 0, files);
            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.DoesNotContain(".kobo", f));
        }
    }
}
