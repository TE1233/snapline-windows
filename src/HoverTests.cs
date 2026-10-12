using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Snapline
{
    static class HoverTests
    {
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
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
            DateTime time = new DateTime(2026, 10, 12, 12, 0, 0);
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 })
            {
                var gate = new TopHoverGate();
                Rectangle line = TopHoverGate.IndicatorBounds(bounds, scale);
                int x = line.Left + line.Width / 2, y = line.Top;
                Require(!gate.Observe("A", bounds, bounds.Left + 20, bounds.Top, scale, time), "Whole edge still activates.");
                Require(!gate.Observe("A", bounds, x, line.Bottom, scale, time), "Pixel below line activates.");
                Require(!gate.Observe("A", bounds, line.Left - 1, y, scale, time), "Pixel beside line activates.");
                Require(!gate.Observe("A", bounds, line.Right, y, scale, time), "Right exclusive boundary activates.");
                Require(!gate.Observe("A", bounds, x, y, scale, time), "Opened without dwell.");
                Require(!gate.Observe("A", bounds, x, y, scale, time.AddMilliseconds(160)), "Opened too soon.");
                Require(gate.Observe("A", bounds, x, y, scale, time.AddMilliseconds(240)), "Blue line failed at DPI " + scale);
                Require(!gate.Observe("A", bounds, x, line.Bottom, scale, time.AddMilliseconds(320)), "Opened after leaving line.");
                Require(!gate.Observe("A", bounds, x, y, scale, time.AddMilliseconds(400)), "Off-line movement retained dwell.");
                Require(!gate.Observe("B", bounds, x, y, scale, time.AddMilliseconds(480)), "Monitor change reused dwell.");
                Require(gate.Observe("B", bounds, x, y, scale, time.AddMilliseconds(720)), "Second monitor failed.");
            }
            var work = new Rectangle(0, 48, 1920, 1032);
            var taskbar = new TopHoverGate();
            Require(!taskbar.Observe("A", work, 960, 0, 1, time), "Trigger ignored working-area origin.");
            taskbar.Observe("A", work, 960, 49, 1, time);
            Require(taskbar.Observe("A", work, 960, 49, 1, time.AddMilliseconds(240)), "Top taskbar prevents line hover.");
            report.Add("PASS: only the centered 68x2 logical-pixel line activates at 100/125/150/175/200/250/300 percent DPI; negative origins and taskbar offsets work");
            report.Add("PASS: left/right/below-line pixels do not activate; exiting the line and monitor changes reset dwell immediately");
            IntPtr desktop = Native.GetShellWindow();
            if (desktop != IntPtr.Zero) Require(!HoverPolicy.Suppresses(desktop, Forms.Screen.FromHandle(desktop).Bounds), "Desktop still suppresses hover.");
            var store = new Store(root); store.Settings.CaptureShortcut = "None"; store.Settings.WelcomeShown = true;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var shelf = new Shelf(store, true, false); app.MainWindow = shelf;
            shelf.ForegroundWindow = delegate { return desktop; };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            Native.Point original, owned = new Native.Point(); Native.GetCursorPos(out original);
            Action<int, int> move = delegate(int x, int y) { Require(SetCursorPos(x, y), "Pointer could not move."); owned.X = x; owned.Y = y; };
            DateTime phaseStart = DateTime.Now;
            int phase = 0, exit = 1; IntPtr focus = IntPtr.Zero; HwndSource fixture = null;
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
                        report.Add("PASS: actual mouse touching screen top away from line, or just below the centered line, stays collapsed");
                        move(visible.Left + visible.Width / 2, visible.Top); phase = 4; phaseStart = DateTime.Now;
                    }
                    else if (phase == 4 && (DateTime.Now - phaseStart).TotalMilliseconds > 600)
                    {
                        Require(shelf.ActualHeight > 200, "Touching visible blue line failed."); Require(Native.GetForegroundWindow() == focus, "Hover stole focus.");
                        report.Add("PASS: actual cursor touching the rendered blue line opens over desktop without stealing keyboard focus");
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
                        Native.Point at; Native.GetCursorPos(out at);
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
                if (fixture != null) fixture.Dispose(); Native.Point current;
                if (Native.GetCursorPos(out current) && current.X == owned.X && current.Y == owned.Y) SetCursorPos(original.X, original.Y);
                File.WriteAllLines(Path.Combine(root, "hover-results.txt"), report, Encoding.UTF8);
            }
            return exit;
        }
    }
}
