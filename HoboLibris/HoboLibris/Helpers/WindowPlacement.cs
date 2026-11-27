using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;

namespace HoboLibris.Helpers
{
    /// <summary>A rectangle on the desktop, in physical pixels.</summary>
    public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width;
        public int Bottom => Top + Height;

        /// <summary>How many pixels the two rectangles share.</summary>
        public long Overlap(PixelRect other)
        {
            long width = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
            long height = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
            return width > 0 && height > 0 ? width * height : 0;
        }
    }

    /// <summary>A screen's working area (the screen less the taskbar), its scaling, and whether it is the primary screen.</summary>
    public readonly record struct ScreenArea(PixelRect Work, double Scale, bool Primary);

    /// <summary>Where the main window was when it was closed, kept in settings.json (#84).  Physical pixels.</summary>
    public sealed class WindowPlace
    {
        [JsonPropertyName("left")]
        public int Left { get; set; }

        [JsonPropertyName("top")]
        public int Top { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("maximised")]
        public bool Maximised { get; set; }

        [JsonIgnore]
        public PixelRect Rect => new(Left, Top, Width, Height);
    }

    /// <summary>
    /// Where the main window opens (#84): where it was last closed, moved wholly inside the working
    /// area of the screen it overlaps most; or, on first start or when that place is on no screen now,
    /// centred on the primary screen.  Never across screens, as CenterScreen could do with the mouse
    /// where screens meet.
    /// </summary>
    public static class WindowPlacement
    {
        /// <summary>The size on first start, in device-independent pixels (as MainWindow.xaml had it).</summary>
        public const double DefaultWidth = 1200;
        public const double DefaultHeight = 780;

        public static PixelRect Fit(PixelRect? saved, IReadOnlyList<ScreenArea> screens)
        {
            var defaultSize = new PixelRect(0, 0, (int)DefaultWidth, (int)DefaultHeight);
            if (screens.Count == 0)
            {
                return saved ?? defaultSize;
            }
            if (saved is { Width: > 0, Height: > 0 } place)
            {
                var best = screens.OrderByDescending(s => s.Work.Overlap(place)).First();
                if (best.Work.Overlap(place) > 0)
                {
                    return Inside(place, best.Work);
                }
            }
            var primary = screens.FirstOrDefault(s => s.Primary, screens[0]);
            var work = primary.Work;
            int width = Math.Min((int)Math.Round(DefaultWidth * primary.Scale), work.Width);
            int height = Math.Min((int)Math.Round(DefaultHeight * primary.Scale), work.Height);
            return new PixelRect(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
        }

        /// <summary>The rectangle made smaller if it doesn't fit the working area, then moved inside it.</summary>
        private static PixelRect Inside(PixelRect rect, PixelRect work)
        {
            int width = Math.Min(rect.Width, work.Width);
            int height = Math.Min(rect.Height, work.Height);
            return new PixelRect(
                Math.Clamp(rect.Left, work.Left, work.Right - width),
                Math.Clamp(rect.Top, work.Top, work.Bottom - height),
                width, height);
        }

        /// <summary>The working area and scaling of each screen now attached.</summary>
        public static List<ScreenArea> Screens()
        {
            var screens = new List<ScreenArea>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
                    screens.Add(new ScreenArea(info.Work.ToPixelRect(), scale, (info.Flags & MonitorInfoPrimary) != 0));
                }
                return true;
            }, IntPtr.Zero);
            return screens;
        }

        // ---- Win32 ----------------------------------------------------------------------------

        private const int MonitorInfoPrimary = 1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left, Top, Right, Bottom;

            public readonly PixelRect ToPixelRect() => new(Left, Top, Right - Left, Bottom - Top);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public int Flags;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }

    /// <summary>
    /// Puts a window where WindowPlacement says before it is shown, and notes where it is while it is
    /// neither maximised nor minimised, so a maximised window comes back over its normal place.
    /// </summary>
    public sealed class WindowPlaceKeeper
    {
        private readonly Window _window;
        private PixelRect? _normal;

        public WindowPlaceKeeper(Window window, WindowPlace? saved)
        {
            _window = window;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SourceInitialized += (_, _) => Restore(saved);
            window.LocationChanged += (_, _) => Note();
            window.SizeChanged += (_, _) => Note();
        }

        private IntPtr Handle => new WindowInteropHelper(_window).Handle;

        private void Restore(WindowPlace? saved)
        {
            var rect = WindowPlacement.Fit(saved?.Rect, WindowPlacement.Screens());
            // The first move takes the window to its screen, where WPF scales it for that screen; the
            // second gives it the size in that screen's pixels.
            for (int i = 0; i < 2; i++)
            {
                SetWindowPos(Handle, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, NoZOrder | NoActivate);
            }
            _normal = rect;
            if (saved?.Maximised == true)
            {
                _window.WindowState = WindowState.Maximized;
            }
        }

        private void Note()
        {
            var handle = Handle;
            if (handle != IntPtr.Zero && !IsZoomed(handle) && !IsIconic(handle) && GetWindowRect(handle, out var rect))
            {
                _normal = rect.ToPixelRect();
            }
        }

        /// <summary>Where the window is now, to save; null before it has been placed.</summary>
        public WindowPlace? Current()
        {
            if (_normal is not { } normal)
            {
                return null;
            }
            return new WindowPlace
            {
                Left = normal.Left, Top = normal.Top, Width = normal.Width, Height = normal.Height,
                Maximised = Handle != IntPtr.Zero && IsZoomed(Handle),
            };
        }

        private const uint NoZOrder = 0x0004;
        private const uint NoActivate = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out WindowPlacement.NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);
    }
}
