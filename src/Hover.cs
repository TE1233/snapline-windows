using System;
using System.Drawing;
using System.Text;

namespace Snapline
{
    // Uses physical desktop coordinates. A slightly wider band avoids missing the visible line.
    sealed class TopHoverGate
    {
        string monitor;
        int top, left;
        DateTime entered, lastSeen;
        internal const int DwellMilliseconds = 200;
        internal static int TriggerHeight(double dpi) { return Math.Max(12, (int)Math.Ceiling(8 * Math.Max(1, dpi))); }
        internal void Reset() { monitor = null; entered = lastSeen = DateTime.MinValue; }
        internal bool Observe(string device, Rectangle area, int x, int y, double dpi, DateTime now)
        {
            int band = TriggerHeight(dpi);
            bool same = monitor == device && top == area.Top && left == area.Left;
            bool horizontal = x >= area.Left && x < area.Right;
            bool inside = horizontal && y >= area.Top && y < area.Top + band;
            if (!inside)
            {
                // Keep dwell progress through one brief, small pointer wobble, without opening outside the band.
                if (same && horizontal && y >= area.Top && y < area.Top + band + 4 &&
                    (now - lastSeen).TotalMilliseconds <= 120) return false;
                Reset(); return false;
            }
            if (!same || entered == DateTime.MinValue || now < entered)
            { monitor = device; top = area.Top; left = area.Left; entered = now; }
            lastSeen = now;
            return (now - entered).TotalMilliseconds >= DwellMilliseconds;
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
