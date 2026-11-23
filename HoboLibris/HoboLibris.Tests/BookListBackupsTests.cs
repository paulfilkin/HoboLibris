using HoboLibris.Models;
using HoboLibris.Services;

namespace HoboLibris.Tests
{
    public class BookListBackupsTests
    {
        private static void Touch(string dir, string name, string text = "x") =>
            File.WriteAllText(Path.Combine(dir, name), text);

        [Fact]
        public void CountsBackupsButNotJournalsOrOtherFiles()
        {
            var dir = TestFolder.Fresh("backups-count");
            Touch(dir, "KoboReader-20250101T000000Z.sqlite");
            Touch(dir, "KoboReader-20250101T000000Z.sqlite-wal");
            Touch(dir, "KoboReader-20250102T000000Z.sqlite");
            Touch(dir, "notes.txt");

            Assert.Equal(2, BookListBackups.Count(dir));
            Assert.Equal(3, BookListBackups.Files(dir).Count);
        }

        [Fact]
        public void MissingFolderHasNoBackups()
        {
            var dir = Path.Combine(TestFolder.Fresh("backups-missing"), "not-there");
            Assert.Equal(0, BookListBackups.Count(dir));
        }

        [Fact]
        public void MovesBackupsAndJournalsOnly()
        {
            var root = TestFolder.Fresh("backups-move");
            var from = Path.Combine(root, "old");
            var to = Path.Combine(root, "new");
            Directory.CreateDirectory(from);
            Touch(from, "KoboReader-20250101T000000Z.sqlite");
            Touch(from, "KoboReader-20250101T000000Z.sqlite-shm");
            Touch(from, "notes.txt");

            var moved = BookListBackups.Move(from, to);

            Assert.Equal(2, moved);
            Assert.True(File.Exists(Path.Combine(to, "KoboReader-20250101T000000Z.sqlite")));
            Assert.True(File.Exists(Path.Combine(to, "KoboReader-20250101T000000Z.sqlite-shm")));
            Assert.True(File.Exists(Path.Combine(from, "notes.txt")));
            Assert.Equal(0, BookListBackups.Count(from));
        }

        [Fact]
        public void LeavesAFileWhoseNameIsTakenInTheNewFolder()
        {
            var root = TestFolder.Fresh("backups-clash");
            var from = Path.Combine(root, "old");
            var to = Path.Combine(root, "new");
            Directory.CreateDirectory(from);
            Directory.CreateDirectory(to);
            Touch(from, "KoboReader-20250101T000000Z.sqlite", "old");
            Touch(to, "KoboReader-20250101T000000Z.sqlite", "new");

            Assert.Equal(0, BookListBackups.Move(from, to));
            Assert.Equal("old", File.ReadAllText(Path.Combine(from, "KoboReader-20250101T000000Z.sqlite")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(to, "KoboReader-20250101T000000Z.sqlite")));
        }

        [Fact]
        public void MovingToTheSameFolderDoesNothing()
        {
            var dir = TestFolder.Fresh("backups-same");
            Touch(dir, "KoboReader-20250101T000000Z.sqlite");

            Assert.Equal(0, BookListBackups.Move(dir, dir + Path.DirectorySeparatorChar));
            Assert.True(BookListBackups.SameFolder(dir, dir.ToUpperInvariant()));
            Assert.Equal(1, BookListBackups.Count(dir));
        }

        [Fact]
        public void SigningOutKeepsTheBackupFolder()
        {
            var settings = new AppSettings { BookListBackupDir = @"D:\Backups", UserKey = "key", AccessToken = "token" };

            var after = settings.SignedOut();

            Assert.Equal(@"D:\Backups", after.BookListBackupDir);
            Assert.False(after.SignedIn);
        }
    }
}
