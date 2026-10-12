using System;
using System.Drawing;
using System.Text;

namespace Snapline
{
    // The hit rectangle is the visible collapsed blue indicator, in physical desktop pixels.
    static class TopHoverGate
    {
        internal const double IndicatorWidth = 68, IndicatorHeight = 2;
        internal static Rectangle IndicatorBounds(Rectangle area, double dpi)
        {
            dpi = dpi > 0 ? dpi : 1;
            int width = Math.Min(area.Width, Math.Max(1, (int)Math.Round(IndicatorWidth * dpi)));
            int height = Math.Max(1, (int)Math.Round(IndicatorHeight * dpi));
            return new Rectangle(area.Left + (area.Width - width) / 2, area.Top, width, height);
        }
        internal static bool Contains(Rectangle area, int x, int y, double dpi)
        {
            return IndicatorBounds(area, dpi).Contains(x, y);
        }
    }

    static class HoverPolicy
    {
        internal static bool IsFullscreen(string windowClass, bool shell, bool maximized, int style, Native.Rect rect, Rectangle bounds)
        {
            if (shell || windowClass == "Progman" || windowClass == "WorkerW" ||
                windowClass == "Shell_TrayWnd" || windowClass == "Shell_SecondaryTrayWnd") return false;
            // A normal framed window remains usable even when maximized with an auto-hidden taskbar.
            if ((style & 0x00C00000) == 0x00C00000 || (maximized && (style & 0x00040000) != 0)) return false;
            return Math.Abs(rect.Left - bounds.Left) <= 2 && Math.Abs(rect.Top - bounds.Top) <= 2 &&
                Math.Abs(rect.Right - bounds.Right) <= 2 && Math.Abs(rect.Bottom - bounds.Bottom) <= 2;
        }
        internal static bool Suppresses(IntPtr window, Rectangle bounds)
        {
            if (window == IntPtr.Zero) return false;
            bool shell = window == Native.GetShellWindow();
            var name = new StringBuilder(256);
            Native.GetClassName(window, name, name.Capacity);
            Native.Rect rect;
            if (Native.DwmGetWindowAttribute(window, 9, out rect, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.Rect))) != 0 &&
                !Native.GetWindowRect(window, out rect)) return false;
            return IsFullscreen(name.ToString(), shell, Native.IsZoomed(window), Native.GetWindowLong(window, -16), rect, bounds);
        }
    }
}
