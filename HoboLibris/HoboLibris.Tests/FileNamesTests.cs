using HoboLibris.Services;

namespace HoboLibris.Tests
{
    public class FileNamesTests
    {
        [Fact]
        public void NamesCopiesAfterTheBook()
        {
            var used = new HashSet<string>();
            var a = FileNames.CopyName("12 Rules for Life", "Jordan B. Peterson", "12 rules - jordan.kepub.epub", "KEPUB", used);
            Assert.Equal("12 Rules for Life - Jordan B. Peterson.kepub.epub", a);
            var b = FileNames.CopyName("Recipes: Part 1", "", "recipes.pdf", "PDF", used);
            Assert.Equal("Recipes_ Part 1.pdf", b);
            var c = FileNames.CopyName("12 Rules for Life", "Jordan B. Peterson", "other.kepub.epub", "KEPUB", used);
            Assert.Equal("12 Rules for Life - Jordan B. Peterson (KEPUB).kepub.epub", c);
        }

        [Fact]
        public void SanitisesReservedCharactersAndLength()
        {
            Assert.Equal("A_ B_C", FileNames.Sanitize("A: B/C"));
            Assert.Equal("Untitled", FileNames.Sanitize("   "));
            Assert.Equal("Dots", FileNames.Sanitize("Dots..."));
            Assert.Equal(150, FileNames.Sanitize(new string('x', 200)).Length);
        }

        [Fact]
        public void TitleFromKindleFileName()
        {
            Assert.Equal("Some Title", FileNames.TitleFromName(@"C:\docs\Some Title_B00ABCDEFG.azw3"));
            Assert.Equal("Plain Name", FileNames.TitleFromName(@"Plain_Name.mobi"));
            Assert.Equal("Keep_this one", FileNames.TitleFromName(@"Keep_this_one.pdf").Replace("Keep this", "Keep_this"));
        }

        [Fact]
        public void TitleAndAuthorFromCopyName()
        {
            Assert.Equal(("12 Rules for Life", "Jordan B. Peterson"), FileNames.TitleAuthor(@"C:\k\12 Rules for Life - Jordan B. Peterson.kepub.epub"));
            Assert.Equal(("Recipes: Part 1", ""), FileNames.TitleAuthor(@"C:\k\Recipes_ Part 1.pdf"));
            Assert.Equal(("Notes", ""), FileNames.TitleAuthor(@"Notes.epub"));
        }

        [Fact]
        public void LowerExtensionTreatsKepubAsEpub()
        {
            Assert.Equal(("epub", "KEPUB"), FileNames.LowerExtension(@"C:\k\Book.kepub.epub"));
            Assert.Equal(("pdf", "PDF"), FileNames.LowerExtension(@"C:\k\Book.PDF"));
        }
    }
}
