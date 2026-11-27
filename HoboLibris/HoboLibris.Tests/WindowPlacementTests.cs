using HoboLibris.Helpers;

namespace HoboLibris.Tests
{
    public class WindowPlacementTests
    {
        // Screens laid out as on the machine in #84, in physical pixels: a 3456 x 2160 primary at 225%,
        // and two 1920 x 1080 screens at 100% above it that meet over it.  Working areas leave out a taskbar.
        private static readonly ScreenArea Primary = new(new PixelRect(0, 0, 3456, 2052), 2.25, true);
        private static readonly ScreenArea UpperLeft = new(new PixelRect(-246, -1080, 1920, 1032), 1.0, false);
        private static readonly ScreenArea UpperRight = new(new PixelRect(1674, -1080, 1920, 1032), 1.0, false);
        private static readonly ScreenArea[] Screens = { Primary, UpperLeft, UpperRight };

        [Fact]
        public void APlaceOnOneScreenIsKept()
        {
            var place = new PixelRect(400, 300, 2400, 1560);
            Assert.Equal(place, WindowPlacement.Fit(place, Screens));
        }

        [Fact]
        public void APlaceOnAScreenAboveThePrimaryIsKept()
        {
            var place = new PixelRect(0, -1000, 1200, 800);
            Assert.Equal(place, WindowPlacement.Fit(place, Screens));
        }

        [Fact]
        public void APlaceAcrossScreensMovesIntoTheOneItOverlapsMost()
        {
            // Mostly on the primary screen, partly on both screens above it.
            var fitted = WindowPlacement.Fit(new PixelRect(1000, -300, 2400, 1560), Screens);
            Assert.Equal(new PixelRect(1000, 0, 2400, 1560), fitted);
            Assert.Equal(Primary, Screens.Single(s => s.Work.Overlap(fitted) > 0));
        }

        [Fact]
        public void APlaceTooBigForItsScreenIsMadeSmaller()
        {
            var fitted = WindowPlacement.Fit(new PixelRect(-10, 100, 4000, 2500), Screens);
            Assert.Equal(new PixelRect(0, 0, 3456, 2052), fitted);
        }

        [Fact]
        public void APlaceOnNoScreenOpensCentredOnThePrimary()
        {
            var fitted = WindowPlacement.Fit(new PixelRect(9000, 9000, 2400, 1560), Screens);
            Assert.Equal(new PixelRect(378, 148, 2700, 1755), fitted);
        }

        [Fact]
        public void TheFirstStartIsCentredOnThePrimaryAtItsScaling()
        {
            // 1200 x 780 at 225%, as the live check on that machine found.
            Assert.Equal(new PixelRect(378, 148, 2700, 1755), WindowPlacement.Fit(null, Screens));

            // A small primary screen: the window is made smaller to fit.
            var small = new ScreenArea(new PixelRect(0, 0, 1366, 728), 1.0, true);
            Assert.Equal(new PixelRect(83, 0, 1200, 728), WindowPlacement.Fit(null, new[] { UpperLeft, small }));
        }
    }
}
