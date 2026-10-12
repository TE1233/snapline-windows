using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Snapline
{
    static class HoverTests
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        internal static int Run(string root)
        {
            Require(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Hover tests need an isolated empty directory.");
            var report = new List<string>();
            var bounds = new Rectangle(-1920, -1080, 1920, 1080);
            var full = new Native.Rect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
            Require(!HoverPolicy.IsFullscreen("Progman", false, false, 0, full, bounds), "Desktop classified as fullscreen.");
            Require(!HoverPolicy.IsFullscreen("WorkerW", false, false, 0, full, bounds), "Desktop worker classified as fullscreen.");
            Require(!HoverPolicy.IsFullscreen("NormalApp", false, true, 0x00C40000, full, bounds), "Maximized framed window blocked hover.");
            Require(HoverPolicy.IsFullscreen("FullscreenApp", false, false, unchecked((int)0x80000000), full, bounds), "Borderless fullscreen no longer suppresses hover.");
            report.Add("PASS: desktop and framed/maximized apps are allowed; genuine borderless fullscreen still suppresses hover");
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 })
            {
                Rectangle line = TopHoverGate.IndicatorBounds(bounds, scale);
                int x = line.Left + line.Width / 2, y = line.Top;
                Require(!TopHoverGate.Contains(bounds, bounds.Left + 20, bounds.Top, scale), "Whole edge still activates.");
                Require(!TopHoverGate.Contains(bounds, x, line.Bottom, scale), "Pixel below line activates.");
                Require(!TopHoverGate.Contains(bounds, line.Left - 1, y, scale), "Pixel beside line activates.");
                Require(!TopHoverGate.Contains(bounds, line.Right, y, scale), "Right exclusive boundary activates.");
                Require(TopHoverGate.Contains(bounds, x, y, scale), "Blue line still needs dwell at DPI " + scale);
                Require(!TopHoverGate.Contains(bounds, x, line.Bottom, scale), "Opened after leaving line.");
                Require(TopHoverGate.Contains(bounds, x, y, scale), "Returning to blue line did not activate immediately.");
            }
            var work = new Rectangle(0, 48, 1920, 1032);
            Require(!TopHoverGate.Contains(work, 960, 0, 1), "Trigger ignored working-area origin.");
            Require(TopHoverGate.Contains(work, 960, 49, 1), "Top taskbar prevents immediate line hover.");
            report.Add("PASS: only the centered 68x2 logical-pixel line activates at 100/125/150/175/200/250/300 percent DPI; negative origins and taskbar offsets work");
            report.Add("PASS: first contact and re-entry activate without dwell; left/right/below-line pixels do not activate");
            IntPtr desktop = Native.GetShellWindow();
            if (desktop != IntPtr.Zero) Require(!HoverPolicy.Suppresses(desktop, Forms.Screen.FromHandle(desktop).Bounds), "Desktop still suppresses hover.");
            var store = new Store(root); store.Settings.CaptureShortcut = "None"; store.Settings.WelcomeShown = true;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var shelf = new Shelf(store, true, false); app.MainWindow = shelf;
            shelf.ForegroundWindow = delegate { return desktop; };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            Native.Point owned; Native.GetCursorPos(out owned);
            shelf.PointerPosition = delegate { return owned; };
            Action<int, int> move = delegate(int x, int y) { owned = new Native.Point { X = x, Y = y }; };
            DateTime phaseStart = DateTime.Now;
            int phase = 0, exit = 1; IntPtr focus = IntPtr.Zero; HwndSource fixture = null;
            bool sawSlide = false;
            Rectangle area = new Rectangle(), visible = new Rectangle();
            timer.Tick += delegate
            {
                try
                {
                    Require((DateTime.Now - phaseStart).TotalSeconds < 9, "Hover phase timeout " + phase);
                    if (phase == 0)
                    {
                        area = Forms.Screen.FromHandle(new WindowInteropHelper(shelf).Handle).WorkingArea;
                        move(area.Left + area.Width / 2, area.Bottom - 80); shelf.ToggleFromTray(); phase = 1; phaseStart = DateTime.Now;
                    }
                    else if (phase == 1 && (DateTime.Now - phaseStart).TotalMilliseconds > 1300)
                    {
                        Require(shelf.ActualHeight <= 8, "Initial shelf did not collapse.");
                        visible = shelf.IndicatorHitBounds(); Require(!visible.IsEmpty && visible.Width < area.Width / 2 && visible.Height <= 8, "Visible indicator bounds unavailable.");
                        focus = Native.GetForegroundWindow(); move(area.Left + 20, area.Top); phase = 2; phaseStart = DateTime.Now;
                    }
                    else if (phase == 2 && (DateTime.Now - phaseStart).TotalMilliseconds > 600)
                    {
                        Require(shelf.ActualHeight <= 8, "Off-line screen top opened shelf.");
                        move(visible.Left + visible.Width / 2, visible.Bottom + 6); phase = 3; phaseStart = DateTime.Now;
                    }
                    else if (phase == 3 && (DateTime.Now - phaseStart).TotalMilliseconds > 600)
                    {
                        Require(shelf.ActualHeight <= 8, "Below-line hover opened shelf.");
                        report.Add("PASS: independent pointer touching screen top away from rendered line, or just below it, stays collapsed");
                        move(visible.Left + visible.Width / 2, visible.Top); phase = 4; phaseStart = DateTime.Now;
                        var marker = ((System.Windows.Controls.Grid)shelf.Content).Children.OfType<System.Windows.Controls.Canvas>().First().Children.OfType<System.Windows.Controls.Border>().Single();
                        marker.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                        Require(shelf.Expanded && shelf.Opening && shelf.ShelfOffset <= Shelf.HiddenShelfOffset + 1,
                            "Blue-line MouseEnter waited for polling or dwell instead of starting opening synchronously.");
                        report.Add("PASS: blue-line MouseEnter starts downward opening immediately in the same input event");
                    }
                    else if (phase == 4)
                    {
                        if (shelf.Opening && shelf.ShelfOffset > Shelf.HiddenShelfOffset && shelf.ShelfOffset < -5) sawSlide = true;
                        if ((DateTime.Now - phaseStart).TotalMilliseconds <= 700) return;
                        Require(sawSlide && !shelf.Opening && Math.Abs(shelf.ShelfOffset) < .1, "Blue-line opening skipped the downward slide.");
                        Require(shelf.ActualHeight > 200, "Touching visible blue line failed."); Require(Native.GetForegroundWindow() == focus, "Hover stole focus.");
                        report.Add("PASS: rendered blue-line trigger visibly slides shelf downward and settles without stealing keyboard focus");
                        shelf.ToggleFromTray(); var screen = Forms.Screen.FromHandle(new WindowInteropHelper(shelf).Handle).Bounds;
                        fixture = new HwndSource(new HwndSourceParameters("Snapline hover fullscreen fixture")
                        { WindowStyle = unchecked((int)0x80000000), PositionX = screen.Left, PositionY = screen.Top, Width = screen.Width, Height = screen.Height });
                        shelf.ForegroundWindow = delegate { return fixture.Handle; }; phase = 5; phaseStart = DateTime.Now;
                    }
                    else if (phase == 5 && (DateTime.Now - phaseStart).TotalMilliseconds > 1500)
                    {
                        Require(shelf.ActualHeight <= 8, "Fullscreen allowed hover."); shelf.ForegroundWindow = delegate { return desktop; }; phase = 6; phaseStart = DateTime.Now;
                    }
                    else if (phase == 6 && (DateTime.Now - phaseStart).TotalMilliseconds > 600)
                    {
                        Native.Point at = owned;
                        Require(shelf.ActualHeight > 200, "Hover did not resume after fullscreen. Cursor=" + at.X + "," + at.Y + " Line=" + shelf.IndicatorHitBounds() + " Height=" + shelf.ActualHeight);
                        report.Add("PASS: fullscreen suppresses accidental opening; returning to desktop resumes line hover");
                        move(area.Left + area.Width / 2, area.Bottom - 80); phase = 7; phaseStart = DateTime.Now;
                    }
                    else if (phase == 7 && (DateTime.Now - phaseStart).TotalMilliseconds > 4100)
                    {
                        Require(shelf.ActualHeight <= 8, "Leaving did not auto-collapse."); report.Add("PASS: automatic collapse after leaving remains intact");
                        exit = 0; timer.Stop(); shelf.Close();
                    }
                }
                catch (Exception ex) { report.Add("FAIL: " + ex); timer.Stop(); shelf.Close(); }
            };
            shelf.Loaded += delegate { phaseStart = DateTime.Now; timer.Start(); }; shelf.Show();
            try { app.Run(); }
            finally
            {
                if (fixture != null) fixture.Dispose();
                File.WriteAllLines(Path.Combine(root, "hover-results.txt"), report, Encoding.UTF8);
            }
            return exit;
        }
    }
}
