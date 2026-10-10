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
            Require(!HoverPolicy.IsFullscreen("Progman", false, false, 0, full, bounds), "Desktop was classified as fullscreen.");
            Require(!HoverPolicy.IsFullscreen("WorkerW", false, false, 0, full, bounds), "Desktop worker was classified as fullscreen.");
            Require(!HoverPolicy.IsFullscreen("NormalApp", false, true, 0x00C40000, full, bounds), "Maximized framed window with hidden taskbar blocked hover.");
            Require(HoverPolicy.IsFullscreen("FullscreenApp", false, false, unchecked((int)0x80000000), full, bounds), "Borderless fullscreen no longer suppresses passive hover.");
            report.Add("PASS: desktop and framed/maximized apps are allowed; genuine borderless fullscreen still suppresses hover");
            DateTime time = new DateTime(2026, 10, 10, 12, 0, 0);
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 })
            {
                var gate = new TopHoverGate();
                int y = bounds.Top + (int)Math.Ceiling(4 * scale) - 1;
                Require(!gate.Observe("A", bounds, -960, y, scale, time), "Opened without dwell.");
                Require(!gate.Observe("A", bounds, -960, y, scale, time.AddMilliseconds(160)), "Opened too soon.");
                Require(gate.Observe("A", bounds, -960, y, scale, time.AddMilliseconds(240)), "Visible indicator failed at DPI " + scale);
            }
            var jitter = new TopHoverGate();
            jitter.Observe("A", bounds, -960, bounds.Top + 8, 1, time);
            jitter.Observe("A", bounds, -960, bounds.Top + 14, 1, time.AddMilliseconds(80));
            jitter.Observe("A", bounds, -960, bounds.Top + 8, 1, time.AddMilliseconds(160));
            Require(jitter.Observe("A", bounds, -960, bounds.Top + 8, 1, time.AddMilliseconds(240)), "Small pointer jitter lost dwell.");
            Require(!jitter.Observe("B", bounds, -960, bounds.Top + 8, 1, time.AddMilliseconds(320)), "Monitor change reused stale dwell.");
            Require(jitter.Observe("B", bounds, -960, bounds.Top + 8, 1, time.AddMilliseconds(560)), "Second monitor could not open.");
            jitter.Observe("B", bounds, -960, bounds.Top + 50, 1, time.AddMilliseconds(640));
            Require(!jitter.Observe("B", bounds, -960, bounds.Top + 8, 1, time.AddMilliseconds(720)), "Leaving edge did not reset dwell.");
            var work = new Rectangle(0, 48, 1920, 1032);
            var taskbar = new TopHoverGate();
            Require(!taskbar.Observe("A", work, 960, 0, 1, time), "Trigger ignored working-area origin.");
            taskbar.Observe("A", work, 960, 49, 1, time);
            Require(taskbar.Observe("A", work, 960, 49, 1, time.AddMilliseconds(240)), "Top taskbar prevents line hover.");
            report.Add("PASS: 100/125/150/175/200/250/300 percent scaling geometry, negative origins, taskbar offsets, jitter and monitor dwell reset");
            IntPtr desktop = Native.GetShellWindow();
            if (desktop != IntPtr.Zero)
                Require(!HoverPolicy.Suppresses(desktop, Forms.Screen.FromHandle(desktop).Bounds), "Actual Windows desktop still suppresses hover.");
            report.Add("PASS: actual Windows desktop window is excluded from fullscreen suppression");

            var store = new Store(root); store.Settings.CaptureShortcut = "None"; store.Settings.WelcomeShown = true;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var shelf = new Shelf(store, true, false); app.MainWindow = shelf;
            shelf.ForegroundWindow = delegate { return desktop; };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            Native.Point original, owned = new Native.Point(); Native.GetCursorPos(out original);
            Action<int, int> move = delegate(int x, int y) { Require(SetCursorPos(x, y), "Pointer could not move."); owned.X = x; owned.Y = y; };
            DateTime phaseStart = DateTime.Now;
            int phase = 0, exit = 1;
            IntPtr focus = IntPtr.Zero;
            HwndSource fixture = null;
            Rectangle area = new Rectangle();
            timer.Tick += delegate
            {
                try
                {
                    Require((DateTime.Now - phaseStart).TotalSeconds < 9, "Hover phase timeout " + phase);
                    if (phase == 0)
                    {
                        area = Forms.Screen.FromHandle(new WindowInteropHelper(shelf).Handle).WorkingArea;
                        move(area.Left + area.Width / 2, area.Bottom - 80);
                        shelf.ToggleFromTray(); phase = 1; phaseStart = DateTime.Now;
                    }
                    else if (phase == 1 && (DateTime.Now - phaseStart).TotalMilliseconds > 1300)
                    {
                        Require(shelf.ActualHeight <= 8, "Initial shelf did not collapse.");
                        focus = Native.GetForegroundWindow();
                        move(area.Left + area.Width / 2, area.Top + 8);
                        phase = 2; phaseStart = DateTime.Now;
                    }
                    else if (phase == 2 && (DateTime.Now - phaseStart).TotalMilliseconds > 600)
                    {
                        Require(shelf.ActualHeight > 200, "Actual hover near indicator failed on desktop.");
                        Require(Native.GetForegroundWindow() == focus, "Hover stole keyboard focus.");
                        report.Add("PASS: actual cursor hover near blue indicator opens over desktop without stealing keyboard focus");
                        shelf.ToggleFromTray();
                        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(shelf).Handle).Bounds;
                        var parameters = new HwndSourceParameters("Snapline hover fullscreen fixture")
                        { WindowStyle = unchecked((int)0x80000000), PositionX = screen.Left, PositionY = screen.Top, Width = screen.Width, Height = screen.Height };
                        fixture = new HwndSource(parameters);
                        shelf.ForegroundWindow = delegate { return fixture.Handle; };
                        phase = 3; phaseStart = DateTime.Now;
                    }
                    else if (phase == 3 && (DateTime.Now - phaseStart).TotalMilliseconds > 1500)
                    {
                        Require(shelf.ActualHeight <= 8, "Borderless fullscreen allowed accidental edge opening.");
                        shelf.ForegroundWindow = delegate { return desktop; };
                        phase = 4; phaseStart = DateTime.Now;
                    }
                    else if (phase == 4 && (DateTime.Now - phaseStart).TotalMilliseconds > 600)
                    {
                        Require(shelf.ActualHeight > 200, "Hover did not resume after fullscreen ended.");
                        report.Add("PASS: fullscreen suppresses accidental opening; returning to desktop resumes hover without moving pointer away");
                        move(area.Left + area.Width / 2, area.Bottom - 80);
                        phase = 5; phaseStart = DateTime.Now;
                    }
                    else if (phase == 5 && (DateTime.Now - phaseStart).TotalMilliseconds > 4100)
                    {
                        Require(shelf.ActualHeight <= 8, "Leaving hover shelf did not auto-collapse.");
                        report.Add("PASS: automatic collapse after leaving remains intact");
                        exit = 0; timer.Stop(); shelf.Close();
                    }
                }
                catch (Exception ex) { report.Add("FAIL: " + ex); timer.Stop(); shelf.Close(); }
            };
            shelf.Loaded += delegate { phaseStart = DateTime.Now; timer.Start(); };
            shelf.Show();
            try { app.Run(); }
            finally
            {
                if (fixture != null) fixture.Dispose();
                Native.Point current;
                if (Native.GetCursorPos(out current) && current.X == owned.X && current.Y == owned.Y)
                    SetCursorPos(original.X, original.Y);
                File.WriteAllLines(Path.Combine(root, "hover-results.txt"), report, Encoding.UTF8);
            }
            return exit;
        }
    }
}
