using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HoboLibris.Helpers
{
    /// <summary>
    /// One side panel of a two-panel layout (the Assistant beside the book screens, the collections
    /// list beside its books), after Mycelia's FoldPanel: it can be dragged wider or narrower, folded
    /// to a narrow strip that opens it again, and folds by itself when the window gets too narrow for
    /// both panels.  Its width and whether the person folded it are kept by the caller.
    /// </summary>
    public sealed class FoldColumn
    {
        public const double StripWidth = 36;

        /// <summary>Extra room needed before a panel folded to fit opens again, so it doesn't flicker.</summary>
        private const double Hysteresis = 40;

        private readonly ColumnDefinition _column;
        private readonly GridSplitter _splitter;
        private readonly UIElement _open;
        private readonly UIElement _strip;
        private readonly double _minWidth;
        private readonly double _maxWidth;

        public FoldColumn(ColumnDefinition column, GridSplitter splitter, UIElement open, UIElement strip,
            double minWidth, double maxWidth, double width, bool folded)
        {
            _column = column;
            _splitter = splitter;
            _open = open;
            _strip = strip;
            _minWidth = minWidth;
            _maxWidth = maxWidth;
            OpenWidth = Clamp(width);
            Folded = folded;
            _splitter.DragCompleted += (_, _) => RememberWidth();
            // The arrow keys move a focused splitter; remember the width once a key is let go.
            _splitter.KeyUp += (_, _) => RememberWidth();
            Apply();
        }

        /// <summary>The width when open.</summary>
        public double OpenWidth { get; private set; }

        public bool Folded { get; private set; }

        /// <summary>Folded by the layout to make room, not by the person; opens again when there is room.</summary>
        public bool FoldedToFit { get; private set; }

        /// <summary>Not shown at all (the Assistant closed from its title-bar icon).</summary>
        public bool Hidden { get; private set; }

        /// <summary>The person dragged the width; the caller saves it.</summary>
        public event Action<double>? WidthChanged;

        /// <summary>The person folded or opened the panel; the caller saves it.</summary>
        public event Action<bool>? FoldedChanged;

        public void SetHidden(bool hidden)
        {
            Hidden = hidden;
            Apply();
        }

        /// <summary>Folds or opens the panel.  Done by the person, it is remembered; done to fit, it isn't.</summary>
        public void SetFolded(bool folded, bool byPerson = true)
        {
            if (Folded == folded && (!byPerson || !FoldedToFit))
            {
                return;
            }
            Folded = folded;
            FoldedToFit = !byPerson && folded;
            Apply();
            if (byPerson)
            {
                FoldedChanged?.Invoke(folded);
            }
        }

        /// <summary>
        /// The layout is <paramref name="available"/> wide and the other panel needs <paramref name="othersMin"/>:
        /// folds this panel when both don't fit, and opens it again once they do.
        /// </summary>
        public void Fit(double available, double othersMin)
        {
            if (Hidden || available <= 0)
            {
                return;
            }
            var needed = othersMin + _minWidth + _splitter.Width;
            if (!Folded && available < needed)
            {
                SetFolded(true, byPerson: false);
            }
            else if (FoldedToFit && available >= needed + Hysteresis)
            {
                SetFolded(false, byPerson: false);
            }
            else if (!Folded && _column.ActualWidth > 0 && available - _column.ActualWidth - _splitter.Width < othersMin)
            {
                // The window narrowed: give the other panel its minimum before this one folds.
                _column.Width = new GridLength(Math.Max(_minWidth, available - othersMin - _splitter.Width));
            }
        }

        private double Clamp(double width) => Math.Clamp(double.IsNaN(width) || width <= 0 ? _minWidth : width, _minWidth, _maxWidth);

        private void RememberWidth()
        {
            if (Folded || Hidden || _column.ActualWidth <= 0)
            {
                return;
            }
            OpenWidth = Clamp(_column.ActualWidth);
            WidthChanged?.Invoke(OpenWidth);
        }

        private void Apply()
        {
            var open = !Hidden && !Folded;
            _column.MinWidth = open ? _minWidth : 0;
            _column.MaxWidth = open ? _maxWidth : double.PositiveInfinity;
            _column.Width = new GridLength(Hidden ? 0 : Folded ? StripWidth : OpenWidth);
            _open.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            _strip.Visibility = !Hidden && Folded ? Visibility.Visible : Visibility.Collapsed;
            _splitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
