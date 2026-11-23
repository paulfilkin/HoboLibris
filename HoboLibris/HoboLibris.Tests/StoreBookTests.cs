using System.Text.Json.Nodes;
using HoboLibris.Models;
using HoboLibris.Services;

namespace HoboLibris.Tests
{
    public class StoreBookTests
    {
        private static DownloadLink Link(string drm, string format, string platform = "Generic") =>
            new() { Drm = drm, Format = format, Url = "https://example.invalid/" + format, Platform = platform };

        [Fact]
        public void PrefersUnprotectedThenEpub3()
        {
            var book = new StoreBook
            {
                Title = "T",
                Links = { Link("AdobeDrm", "EPUB3"), Link("None", "KEPUB"), Link("None", "EPUB"), Link("None", "EPUB3SAMPLE") },
            };
            Assert.Equal("EPUB", book.BestLink()!.Format);
            Assert.False(book.IsProtected);

            var protectedOnly = new StoreBook { Title = "T", Links = { Link("AdobeDrm", "EPUB"), Link("AdobeDrm", "EPUB3") } };
            Assert.Equal("EPUB3", protectedOnly.BestLink()!.Format);
            Assert.True(protectedOnly.IsProtected);

            var none = new StoreBook { Title = "T", Links = { Link("None", "MANIFEST") } };
            Assert.Null(none.BestLink());
        }

        [Fact]
        public void NamesFilesAfterTitleAndFirstAuthorAndFindsOldNames()
        {
            var dir = TestFolder.Fresh("store-names");
            var book = new StoreBook { Title = "The Quiet Harbour: A Novel", Authors = "A. Writer, B. Editor", Links = { Link("None", "KEPUB") } };
            Assert.Equal("The Quiet Harbour_ A Novel - A. Writer", book.FileStem());
            Assert.Equal("kepub.epub", book.Extension);
            Assert.Null(book.SavedIn(dir));
            File.WriteAllText(Path.Combine(dir, "A. Writer - The Quiet Harbour_ A Novel.epub"), "x");
            Assert.NotNull(book.SavedIn(dir));
        }

        [Fact]
        public void ParsesTheReturnLink()
        {
            var (id, key, email) = KoboStoreService.ParseReturnUrl("kobo://UserAuthenticated?userId=u1&userKey=k1&email=a%40b.com");
            Assert.Equal("u1", id);
            Assert.Equal("k1", key);
            Assert.Equal("a@b.com", email);
            Assert.Throws<HoboLibrisException>(() => KoboStoreService.ParseReturnUrl("not a link"));
            Assert.Throws<HoboLibrisException>(() => KoboStoreService.ParseReturnUrl("kobo://UserAuthenticated?userId=u1"));
        }

        [Fact]
        public void BuildsTheSignInUrlWithOurReturnAddress()
        {
            var settings = new AppSettings { DeviceId = "dev123" };
            var resources = new JsonObject { ["sign_in_page"] = "https://authorize.kobo.com/signin?returnUrl=https://kobo.com/&lang=en" };
            var url = KoboStoreService.SignInUrl(settings, resources);
            Assert.StartsWith("https://authorize.kobo.com/signin?", url);
            Assert.Contains("lang=en", url);
            Assert.Contains("pwsdid=dev123", url);
            Assert.Contains("returnUrl=kobo%3A%2F%2FUserAuthenticated", url);
            Assert.DoesNotContain("kobo.com%2F&", url);
            Assert.Throws<HoboLibrisException>(() => { KoboStoreService.SignInUrl(settings, new JsonObject()); });
        }
    }
}

