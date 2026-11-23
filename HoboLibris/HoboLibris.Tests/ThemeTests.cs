using HoboLibris.Helpers;
using HoboLibris.Models;

namespace HoboLibris.Tests
{
    public class ThemeTests
    {
        [Theory]
        [InlineData("light", AppTheme.Light)]
        [InlineData("DARK", AppTheme.Dark)]
        [InlineData(" dark ", AppTheme.Dark)]
        [InlineData("system", AppTheme.System)]
        [InlineData("", AppTheme.System)]
        [InlineData(null, AppTheme.System)]
        [InlineData("purple", AppTheme.System)]
        public void ReadsTheSavedChoice(string? saved, AppTheme expected)
        {
            Assert.Equal(expected, ThemeManager.Parse(saved));
        }

        [Fact]
        public void SavesTheChoiceAsAWord()
        {
            Assert.Equal("dark", ThemeManager.ToSetting(AppTheme.Dark));
            Assert.Equal(AppTheme.Light, ThemeManager.Parse(ThemeManager.ToSetting(AppTheme.Light)));
        }

        [Fact]
        public void FollowsWindowsUntilChosenAndKeepsTheChoiceAfterSigningOut()
        {
            Assert.Equal("system", new AppSettings().Theme);
            var settings = new AppSettings { Theme = "dark", UserKey = "key", AccessToken = "token" };
            Assert.Equal("dark", settings.SignedOut().Theme);
        }
    }
}
