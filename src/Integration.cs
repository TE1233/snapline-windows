using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace Snapline
{
    // Runs against an isolated store and restores a fully materialized clipboard snapshot.
    // If a clipboard format cannot be preserved, the test stops before writing to it.
    static class Integration
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        internal static int Run(string root)
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException("Integration test requires an empty, isolated directory.");
            var store = new Store(root);
            store.Settings.ScreenshotFolder = true; store.Settings.ClipboardImages = true; // Legacy flags must not enable broad collection.
            store.Settings.WelcomeShown = true;
            string watched = Path.Combine(root, "TestScreenshots");
            // The watcher deliberately excludes the application's own store.
            string external = root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + "-source";
            Directory.CreateDirectory(external);
            store.Settings.Folders.Add(external);
            store.SaveSettings();
            var backup = new DataObject();
            var previous = Clipboard.GetDataObject();
            bool backupEmpty = previous == null || previous.GetFormats(false).Length == 0;
            uint savedSequence = Native.GetClipboardSequenceNumber();
            if (previous != null)
            {
                foreach (string format in previous.GetFormats(false))
                {
                    object value = previous.GetData(format, false);
                    if (value == null) throw new InvalidOperationException("Cannot preserve clipboard format: " + format);
                    var memory = value as MemoryStream;
                    if (memory != null) value = new MemoryStream(memory.ToArray());
                    var bitmap = value as BitmapSource;
                    if (bitmap != null) { value = bitmap.Clone(); ((BitmapSource)value).Freeze(); }
                    backup.SetData(format, value, false);
                }
            }
            if (Native.GetClipboardSequenceNumber() != savedSequence)
                throw new InvalidOperationException("Clipboard changed before test; no writes performed.");
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var shelf = new Shelf(store, false, false);
            app.MainWindow = shelf;
            var report = new List<string>();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            DateTime phaseStart = DateTime.Now;
            int phase = 0, exitCode = 1, captureLaunches = 0;
            Action<BitmapSource> captureResult = null;
            string expectedCaptureHash = Store.Hash(Program.Demo("APP CAPTURE", "晾台快捷键截图", "#426C60"));
            uint cancellationSequence = 0;
            string editedHash = "";
            uint lastOwnedSequence = savedSequence;
            string importedFile = Path.Combine(external, "partial.png");
            bool restored = false, restoringClipboard = false, testFailed = false;
            bool pointerMoved = false;
            Native.Point originalPointer = new Native.Point(), testPointer = new Native.Point();
            Native.Rect shelfRect = new Native.Rect();
            System.Drawing.Point outsidePointer = new System.Drawing.Point();
            IntPtr foregroundBeforeTray = IntPtr.Zero;
            Action<int, int> movePointer = delegate(int x, int y)
            {
                if (!pointerMoved && !Native.GetCursorPos(out originalPointer)) throw new Exception("Cannot save mouse position.");
                if (!SetCursorPos(x, y)) throw new Exception("Cannot set test mouse position.");
                pointerMoved = true;
                testPointer = new Native.Point { X = x, Y = y };
            };
            Action restore = delegate
            {
                if (restored) return;
                if (Native.GetClipboardSequenceNumber() != lastOwnedSequence && !Clipboard.IsCurrent(backup))
                {
                    restored = true;
                    report.Add("NOTE: clipboard changed externally; newer contents preserved");
                    return;
                }
                if (backupEmpty)
                {
                    Clipboard.Clear(); restored = true;
                    report.Add("PASS: originally empty clipboard restored"); return;
                }
                if (!restoringClipboard)
                {
                    // Separate OLE publication from flushing so clipboard viewers can service messages.
                    Clipboard.SetDataObject(backup, false);
                    lastOwnedSequence = Native.GetClipboardSequenceNumber();
                    restoringClipboard = true;
                    return;
                }
                Clipboard.Flush();
                restored = true;
                report.Add("PASS: previous clipboard formats restored");
            };
            Action<string> require = delegate(string message) { report.Add("PASS: " + message); };
            timer.Tick += delegate
            {
                try
                {
                    if (phase > 0 && (DateTime.Now - phaseStart).TotalSeconds > 12)
                        throw new Exception("Timeout in phase " + phase);
                    if (phase == 0)
                    {
                        if (shelf.ClipboardRegistered || !shelf.HotkeyRegistered || !shelf.CaptureHotkeyRegistered) throw new Exception("Unexpected listener/hotkey registration state.");
                        require("Ctrl+Alt+T and Ctrl+B registered; broad clipboard listener is absent");
                        Clipboard.SetImage(Program.Demo("EXTERNAL IMAGE", "模拟微信截图", "#315B75"));
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 1; phaseStart = DateTime.Now;
                    }
                    else if (phase == 1 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        if (store.Items.Count != 0) throw new Exception("External clipboard image was collected.");
                        require("external clipboard images are ignored, even with old collection preferences enabled");
                        shelf.CaptureLauncher = delegate(Action<BitmapSource> complete) { captureLaunches++; captureResult = complete; };
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.Hotkey, new IntPtr(2), IntPtr.Zero);
                        phase = 101; phaseStart = DateTime.Now;
                    }
                    else if (phase == 101 && (DateTime.Now - phaseStart).TotalMilliseconds > 500)
                    {
                        if (captureLaunches != 1 || captureResult == null || shelf.ActualHeight > 8) throw new Exception("Capture session was not dispatched correctly.");
                        Clipboard.SetImage(Program.Demo("DURING CAPTURE", "框选期间的外部图片", "#8C6B4D"));
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 102; phaseStart = DateTime.Now;
                    }
                    else if (phase == 102 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        if (store.Items.Count != 0) throw new Exception("External image was collected while selecting.");
                        require("external clipboard image is ignored during an active Ctrl+B session");
                        captureResult(Program.Demo("APP CAPTURE", "晾台快捷键截图", "#426C60"));
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 103; phaseStart = DateTime.Now;
                    }
                    else if (phase == 103 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        if (store.Items.Count != 1 || store.Items[0].Digest != expectedCaptureHash || !Clipboard.ContainsImage()) throw new Exception("Direct capture result was not collected and copied.");
                        require("Ctrl+B collects its direct result and copies it, without collecting the competing clipboard image");
                        captureResult(Program.Demo("STALE RESULT", "重复回调", "#315B75"));
                        if (store.Items.Count != 1 || Native.GetClipboardSequenceNumber() != lastOwnedSequence) throw new Exception("Duplicate completion changed store or clipboard.");
                        require("duplicate capture completion is ignored");
                        shelf.Copy(store.Items[0]);
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 2; phaseStart = DateTime.Now;
                    }
                    else if (phase == 2 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        if (store.Items.Count != 1 || !Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()) throw new Exception("Copy formats or self-copy deduplication failed.");
                        require("copy exports bitmap for image pasting without adding another card");
                        shelf.CopyFile(store.Items[0]);
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 21; phaseStart = DateTime.Now;
                    }
                    else if (phase == 21 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        if (!Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || store.Items.Count != 1) throw new Exception("Copy file formats failed.");
                        require("separate copy-file action exports PNG file drop format");
                        System.IO.File.WriteAllBytes(importedFile, new byte[] { 137, 80, 78, 71 });
                        phase = 3; phaseStart = DateTime.Now;
                    }
                    else if (phase == 3 && (DateTime.Now - phaseStart).TotalMilliseconds > 850)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(Program.Demo("EXTERNAL FILE", "其他软件保存的截图", "#315B75")));
                        using (var stream = System.IO.File.Create(importedFile)) encoder.Save(stream);
                        phase = 4; phaseStart = DateTime.Now;
                    }
                    else if (phase == 4 && (DateTime.Now - phaseStart).TotalMilliseconds > 1100)
                    {
                        if (store.Items.Count != 1 || !System.IO.File.Exists(importedFile)) throw new Exception("External screenshot file was collected or modified.");
                        require("external screenshot folder files are ignored and preserved, including legacy configured folders");
                        var edited = Program.Demo("EDITED CACHE", "缓存编辑更新", "#315B75");
                        editedHash = Store.Hash(edited);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(edited));
                        using (var stream = System.IO.File.Create(store.FullPath(store.Items[0]))) encoder.Save(stream);
                        phase = 5; phaseStart = DateTime.Now;
                    }
                    else if (phase == 5 && (DateTime.Now - phaseStart).TotalMilliseconds > 1100)
                    {
                        if (store.Items.Count != 1 || store.Items[0].Digest != editedHash) throw new Exception("Existing cached image edit was not refreshed.");
                        require("editing an existing cached image still refreshes its card");
                        cancellationSequence = Native.GetClipboardSequenceNumber();
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.Hotkey, new IntPtr(2), IntPtr.Zero);
                        phase = 6; phaseStart = DateTime.Now;
                    }
                    else if (phase == 6 && (DateTime.Now - phaseStart).TotalMilliseconds > 500)
                    {
                        captureResult(null);
                        if (store.Items.Count != 1 || Native.GetClipboardSequenceNumber() != cancellationSequence) throw new Exception("Cancellation changed clipboard or store.");
                        require("cancelled Ctrl+B session preserves clipboard and adds no image");
                        Clipboard.SetImage(Program.Demo("AFTER CANCEL", "取消后微信截图", "#8C6B4D"));
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 61; phaseStart = DateTime.Now;
                    }
                    else if (phase == 61 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        captureResult(Program.Demo("LATE RESULT", "过期截图", "#315B75"));
                        if (store.Items.Count != 1 || Native.GetClipboardSequenceNumber() != lastOwnedSequence) throw new Exception("Cancelled session or subsequent external image was collected.");
                        require("images copied after cancellation and late cancelled-session results are ignored");
                        shelf.StartCapture();
                        phase = 62; phaseStart = DateTime.Now;
                    }
                    else if (phase == 62 && (DateTime.Now - phaseStart).TotalMilliseconds > 500)
                    {
                        captureResult(Program.Demo("BUTTON CAPTURE", "按钮仅复制", "#426C60"));
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 63; phaseStart = DateTime.Now;
                    }
                    else if (phase == 63 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        if (store.Items.Count != 1 || !Clipboard.ContainsImage()) throw new Exception("Button capture did not remain copy-only.");
                        require("mouse capture button copies without automatic collection");
                        store.Settings.CaptureShortcut = "CtrlPrintScreen"; shelf.RegisterCaptureShortcut();
                        if (!shelf.CaptureHotkeyRegistered) throw new Exception("Alternate shortcut failed to register.");
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.Hotkey, new IntPtr(2), IntPtr.Zero);
                        phase = 64; phaseStart = DateTime.Now;
                    }
                    else if (phase == 64 && (DateTime.Now - phaseStart).TotalMilliseconds > 500)
                    {
                        captureResult(Program.Demo("ALTERNATE CAPTURE", "其他快捷键仅复制", "#315B75"));
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 65; phaseStart = DateTime.Now;
                    }
                    else if (phase == 65 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        if (store.Items.Count != 1 || !Clipboard.ContainsImage()) throw new Exception("Alternate screenshot shortcut was automatically collected.");
                        require("alternate screenshot shortcut copies without automatic collection; default Ctrl+B and custom combinations collect");
                        string shortcutError;
                        if (!shelf.TrySelectCaptureShortcut("Custom", 6, 0x78, out shortcutError)) throw new Exception(shortcutError);
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.Hotkey, new IntPtr(shelf.CaptureHotkeyId), IntPtr.Zero);
                        phase = 66; phaseStart = DateTime.Now;
                    }
                    else if (phase == 66 && (DateTime.Now - phaseStart).TotalMilliseconds > 500)
                    {
                        captureResult(Program.Demo("CUSTOM CAPTURE", "自定义快捷键截图", "#426C60"));
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 67; phaseStart = DateTime.Now;
                    }
                    else if (phase == 67 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        if (store.Items.Count != 2 || !Clipboard.ContainsImage()) throw new Exception("Custom shortcut did not collect its direct result.");
                        require("custom screenshot shortcut collects its own result and copies it");
                        Clipboard.SetImage(Program.Demo("EXTERNAL AFTER CUSTOM", "换键后外部截图", "#315B75"));
                        lastOwnedSequence = Native.GetClipboardSequenceNumber();
                        phase = 68; phaseStart = DateTime.Now;
                    }
                    else if (phase == 68 && (DateTime.Now - phaseStart).TotalMilliseconds > 450)
                    {
                        if (store.Items.Count != 2) throw new Exception("Custom shortcut enabled external collection.");
                        require("external screenshots remain ignored after choosing a custom shortcut");
                        store.Settings.CaptureShortcut = "CtrlB"; shelf.RegisterCaptureShortcut();
                        phase = 7; phaseStart = DateTime.Now;
                    }
                    else if (phase == 7)
                    {
                        try { restore(); } catch (System.Runtime.InteropServices.COMException) { return; }
                        if (!restored) return;
                        if (testFailed) { timer.Stop(); shelf.Close(); return; }
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.Hotkey, new IntPtr(1), IntPtr.Zero);
                        phase = 8; phaseStart = DateTime.Now;
                    }
                    else if (phase == 8 && (DateTime.Now - phaseStart).TotalMilliseconds > 350)
                    {
                        if (shelf.ActualHeight > 8) throw new Exception("Hotkey did not collapse shelf.");
                        require("WM_HOTKEY collapses shelf to thin top indicator");
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.Hotkey, new IntPtr(1), IntPtr.Zero);
                        phase = 9; phaseStart = DateTime.Now;
                    }
                    else if (phase == 9 && (DateTime.Now - phaseStart).TotalMilliseconds > 350)
                    {
                        if (shelf.ActualHeight < 200) throw new Exception("Hotkey did not expand shelf.");
                        if (Native.GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(shelf).Handle) throw new Exception("Shelf stole keyboard focus.");
                        require("WM_HOTKEY expands shelf without taking keyboard focus");
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.Hotkey, new IntPtr(99), IntPtr.Zero);
                        phase = 91; phaseStart = DateTime.Now;
                    }
                    else if (phase == 91 && (DateTime.Now - phaseStart).TotalMilliseconds > 240)
                    {
                        if (shelf.ActualHeight < 200) throw new Exception("Unknown hotkey toggled shelf.");
                        require("unknown hotkey IDs leave shelf unchanged");
                        captureLaunches = 0;
                        shelf.CaptureLauncher = delegate(Action<BitmapSource> complete) { captureLaunches++; complete(null); };
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.Hotkey, new IntPtr(2), IntPtr.Zero);
                        phase = 10; phaseStart = DateTime.Now;
                    }
                    else if (phase == 10 && (DateTime.Now - phaseStart).TotalMilliseconds > 500)
                    {
                        if (captureLaunches != 1 || shelf.ActualHeight > 8) throw new Exception("Capture hotkey did not hide shelf and dispatch one capture.");
                        require("capture hotkey uses separate route, hides shelf before dispatch, launches exactly once");
                        phase = 11; phaseStart = DateTime.Now;
                    }
                    else if (phase == 11 && (DateTime.Now - phaseStart).TotalMilliseconds > 1100)
                    {
                        var area = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(shelf).Handle).WorkingArea;
                        outsidePointer = new System.Drawing.Point(area.Left + area.Width / 2, area.Bottom - 100);
                        movePointer(outsidePointer.X, outsidePointer.Y);
                        foregroundBeforeTray = Native.GetForegroundWindow();
                        shelf.ToggleFromTray();
                        phase = 12; phaseStart = DateTime.Now;
                    }
                    else if (phase == 12 && (DateTime.Now - phaseStart).TotalMilliseconds > 1100)
                    {
                        if (shelf.ActualHeight < 200) throw new Exception("Tray shelf collapsed before its travel grace period.");
                        if (Native.GetForegroundWindow() != foregroundBeforeTray) throw new Exception("Tray opening changed keyboard focus.");
                        require("tray opening gives mouse travel grace and preserves keyboard focus");
                        phase = 13; phaseStart = DateTime.Now;
                    }
                    else if (phase == 13 && (DateTime.Now - phaseStart).TotalMilliseconds > 2400)
                    {
                        if (shelf.ActualHeight > 8) throw new Exception("Tray opening stayed pinned after its grace period.");
                        require("tray opening auto-collapses when mouse remains outside");
                        shelf.ToggleFromTray();
                        Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, out shelfRect);
                        movePointer(shelfRect.Left + 20, shelfRect.Top + (shelfRect.Bottom - shelfRect.Top) / 2);
                        phase = 14; phaseStart = DateTime.Now;
                    }
                    else if (phase == 14 && (DateTime.Now - phaseStart).TotalMilliseconds > 3500)
                    {
                        if (shelf.ActualHeight < 200) throw new Exception("Tray shelf collapsed while mouse was inside.");
                        require("tray shelf stays open while mouse is inside, beyond the grace period");
                        movePointer(outsidePointer.X, outsidePointer.Y);
                        phase = 15; phaseStart = DateTime.Now;
                    }
                    else if (phase == 15 && (DateTime.Now - phaseStart).TotalMilliseconds > 1100)
                    {
                        if (shelf.ActualHeight > 8) throw new Exception("Tray shelf did not collapse after mouse left.");
                        require("tray shelf auto-collapses after mouse leaves");
                        shelf.TogglePin();
                        phase = 16; phaseStart = DateTime.Now;
                    }
                    else if (phase == 16 && (DateTime.Now - phaseStart).TotalMilliseconds > 3500)
                    {
                        if (shelf.ActualHeight < 200) throw new Exception("Explicit fixed-display choice did not keep shelf open.");
                        require("explicit fixed-display choice keeps shelf open with mouse outside");
                        shelf.TogglePin();
                        phase = 17; phaseStart = DateTime.Now;
                    }
                    else if (phase == 17 && (DateTime.Now - phaseStart).TotalMilliseconds > 3500)
                    {
                        if (shelf.ActualHeight > 8) throw new Exception("Unchecking fixed-display did not resume auto-collapse.");
                        require("unchecking fixed-display restores auto-collapse");
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.Hotkey, new IntPtr(1), IntPtr.Zero);
                        phase = 18; phaseStart = DateTime.Now;
                    }
                    else if (phase == 18 && (DateTime.Now - phaseStart).TotalMilliseconds > 1100)
                    {
                        if (shelf.ActualHeight < 200) throw new Exception("Ctrl+Alt+T no longer pins shelf.");
                        require("Ctrl+Alt+T still opens with fixed display");
                        shelf.ToggleFromTray();
                        phase = 181; phaseStart = DateTime.Now;
                    }
                    else if (phase == 181 && (DateTime.Now - phaseStart).TotalMilliseconds > 350)
                    {
                        if (shelf.ActualHeight > 8) throw new Exception("Tray toggle did not close a pinned shelf.");
                        require("tray toggle closes fixed-display shelf");
                        Native.PostMessage(new System.Windows.Interop.WindowInteropHelper(shelf).Handle, Native.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                        phase = 19; phaseStart = DateTime.Now;
                    }
                    else if (phase == 19 && (DateTime.Now - phaseStart).TotalMilliseconds > 3500)
                    {
                        if (shelf.ActualHeight > 8) throw new Exception("Second-launch message pinned the shelf.");
                        require("second-launch opening also restores automatic collapse");
                        exitCode = 0;
                        timer.Stop(); shelf.Close();
                    }
                }
                catch (Exception ex)
                {
                    report.Add("FAIL: " + ex);
                    testFailed = true;
                    if (phase != 7 && !restored)
                    {
                        store.Settings.ClipboardImages = false;
                        phase = 7; phaseStart = DateTime.Now; return;
                    }
                    try { restore(); } catch (Exception restoreError) { report.Add("RESTORE ERROR: " + restoreError); }
                    timer.Stop(); shelf.Close();
                }
            };
            shelf.Loaded += delegate { phaseStart = DateTime.Now; timer.Start(); };
            shelf.Show();
            try { app.Run(); }
            finally
            {
                try { restore(); } catch (Exception ex) { report.Add("RESTORE ERROR: " + ex); exitCode = 1; }
                Native.Point currentPointer;
                if (pointerMoved && Native.GetCursorPos(out currentPointer) && currentPointer.X == testPointer.X && currentPointer.Y == testPointer.Y)
                {
                    if (SetCursorPos(originalPointer.X, originalPointer.Y)) report.Add("PASS: test mouse position restored");
                    else { report.Add("FAIL: cannot restore test mouse position"); exitCode = 1; }
                }
                System.IO.File.WriteAllLines(Path.Combine(root, "integration-results.txt"), report, Encoding.UTF8);
            }
            return exitCode;
        }
    }
}
