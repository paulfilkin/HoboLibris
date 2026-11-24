using System.Text.Json;
using HoboLibris.Models;
using HoboLibris.Windows;

namespace HoboLibris.Tests
{
    public class SettingsPagesTests
    {
        [Theory]
        [InlineData("general", 0)]
        [InlineData("kobo", 1)]
        [InlineData("kindle", 2)]
        [InlineData("ai", 3)]
        [InlineData(" AI ", 3)]
        [InlineData("assistant", 4)]
        [InlineData("sources", 5)]
        [InlineData("tools", 6)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("no such page", 0)]
        public void FindsThePageInMenuOrder(string? name, int index)
        {
            Assert.Equal(index, SettingsPages.IndexOf(name));
        }

        [Fact]
        public void TheBannerOpensTheAiModelPage()
        {
            Assert.Equal("ai", SettingsPages.All[SettingsPages.IndexOf(SettingsPages.Model)]);
            Assert.Equal(7, SettingsPages.All.Count);
        }

        [Fact]
        public void ThePageUsedLastIsKeptAndSurvivesSigningOut()
        {
            var settings = new AppSettings { SettingsPage = "sources", UserKey = "k", AccessToken = "t" };
            Assert.Contains("\"settings_page\":\"sources\"", JsonSerializer.Serialize(settings));
            Assert.Equal("sources", settings.SignedOut().SettingsPage);
            Assert.Equal("", new AppSettings().SettingsPage);
        }
    }
}
