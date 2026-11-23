using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HoboLibris.Helpers;
using HoboLibris.Models;
using HoboLibris.Services;

namespace HoboLibris.Tests
{
    public class LayoutTests
    {
        /// <summary>A side panel as the window has it: a column, its divider, the open panel and the strip.</summary>
        private static (FoldColumn Fold, ColumnDefinition Column, GridSplitter Splitter, Border Open, Button Strip) Panel(double width, bool folded)
        {
            var column = new ColumnDefinition();
            var splitter = new GridSplitter { Width = 10 };
            var open = new Border();
            var strip = new Button();
            return (new FoldColumn(column, splitter, open, strip, 300, 900, width, folded), column, splitter, open, strip);
        }

        [Fact]
        public void OpensAtItsWidthAndFoldsToAStrip() => StaThread.Run(() =>
        {
            var (fold, column, splitter, open, strip) = Panel(420, folded: false);
            Assert.Equal(420, column.Width.Value);
            Assert.Equal(Visibility.Visible, open.Visibility);
            Assert.Equal(Visibility.Collapsed, strip.Visibility);

            bool? saved = null;
            fold.FoldedChanged += f => saved = f;
            fold.SetFolded(true);
            Assert.Equal(FoldColumn.StripWidth, column.Width.Value);
            Assert.Equal(Visibility.Collapsed, open.Visibility);
            Assert.Equal(Visibility.Visible, strip.Visibility);
            Assert.Equal(Visibility.Collapsed, splitter.Visibility);
            Assert.True(saved);

            fold.SetHidden(true);
            Assert.Equal(0, column.Width.Value);
            Assert.Equal(Visibility.Collapsed, strip.Visibility);
        });

        [Fact]
        public void FoldsToFitANarrowWindowWithoutRememberingIt() => StaThread.Run(() =>
        {
            var (fold, column, _, open, _) = Panel(420, folded: false);
            var remembered = false;
            fold.FoldedChanged += _ => remembered = true;

            // 520 for the books, 300 for the panel, 10 for the divider: 829 is too narrow.
            fold.Fit(829, 520);
            Assert.True(fold.Folded);
            Assert.True(fold.FoldedToFit);
            Assert.False(remembered);

            // Not quite room enough to open again (the margin stops it flickering).
            fold.Fit(840, 520);
            Assert.True(fold.Folded);
            fold.Fit(900, 520);
            Assert.False(fold.Folded);
            Assert.Equal(Visibility.Visible, open.Visibility);
            Assert.Equal(420, column.Width.Value);
        });

        [Fact]
        public void APanelThePersonFoldedStaysFolded() => StaThread.Run(() =>
        {
            var (fold, _, _, _, _) = Panel(420, folded: true);
            fold.Fit(2000, 520);
            Assert.True(fold.Folded);
            Assert.False(fold.FoldedToFit);
        });

        [Fact]
        public void WidthsAreKeptWithinTheirLimits() => StaThread.Run(() =>
        {
            Assert.Equal(300, Panel(120, false).Column.Width.Value);
            Assert.Equal(900, Panel(4000, false).Column.Width.Value);
            Assert.Equal(300, Panel(double.NaN, false).Column.Width.Value);
        });

        [Fact]
        public void LayoutSettingsAreKeptAndSurviveSigningOut()
        {
            var settings = new AppSettings { CollectionsListWidth = 360, CollectionsListFolded = true, UserKey = "k", AccessToken = "t" };
            settings.Assistant.PaneFolded = true;
            var json = JsonSerializer.Serialize(settings);
            Assert.Contains("\"collections_list_width\":360", json);
            Assert.Contains("\"pane_folded\":true", json);
            var after = settings.SignedOut();
            Assert.Equal(360, after.CollectionsListWidth);
            Assert.True(after.CollectionsListFolded);
            Assert.True(after.Assistant.PaneFolded);
        }
    }
}
