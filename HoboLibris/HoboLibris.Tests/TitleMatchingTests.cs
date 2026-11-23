using HoboLibris.Services;

namespace HoboLibris.Tests
{
    public class TitleMatchingTests
    {
        [Fact]
        public void MatchesTitlesLoosely()
        {
            Assert.True(TitleMatching.SameTitle("The Quiet Harbour", "the quiet harbour"));
            Assert.True(TitleMatching.SameTitle("The Quiet Harbour: A Novel", "The Quiet Harbour"));
            Assert.True(TitleMatching.SameTitle("Letters from the Coast", "Letters From The Coast (Penguin Classics)"));
            Assert.False(TitleMatching.SameTitle("Maps", "Maps of the World"));
            Assert.False(TitleMatching.SameTitle("", "Anything"));
        }
    }
}
