using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Snapline
{
    static class ShortcutTests
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        static void Keys(bool down)
        {
            if (down) { keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x10, 0, 0, UIntPtr.Zero); keybd_event(0x78, 0, 0, UIntPtr.Zero); }
            else { keybd_event(0x78, 0, 2, UIntPtr.Zero); keybd_event(0x10, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero); }
        }
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        internal static int Run(string root)
        {
            Require(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Test needs an empty isolated directory.");
            var report = new List<string>();
            Require(ShortcutSpec.Validate(0, 0x43) != null && ShortcutSpec.Validate(8, 0x43) != null &&
                ShortcutSpec.Validate(2, 0x7B) != null && ShortcutSpec.Validate(3, 0x54) != null, "Invalid/reserved shortcuts accepted.");
            Require(ShortcutSpec.Validate(2, 0x43) == null && ShortcutSpec.Validate(6, 0x43) == null, "Two/three key combinations rejected.");
            var store = new Store(root);
            store.Settings.CaptureShortcut = "Custom"; store.Settings.CustomCaptureModifiers = 6; store.Settings.CustomCaptureKey = 0x78; store.SaveSettings();
            var loaded = new Store(root);
            Require(loaded.Settings.CaptureShortcut == "Custom" && ShortcutSpec.From(loaded.Settings).Label == "Ctrl + Shift + F9", "Custom preference lost.");
            store.Settings.CustomCaptureKey = 0x7B; store.SaveSettings();
            Require(new Store(root).Settings.CaptureShortcut == "CtrlB", "Invalid custom settings failed to recover.");
            store.Settings.CaptureShortcut = "None"; store.Settings.CustomCaptureKey = 0; store.Settings.CustomCaptureModifiers = 0; store.SaveSettings();
            report.Add("PASS: custom preference round-trip, legacy defaults, invalid/reserved key validation");
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var shelf = new Shelf(store, true, false); app.MainWindow = shelf;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            int phase = 0, exit = 1, launches = 0;
            bool injected = false, occupied = false;
            DateTime start = DateTime.Now;
            shelf.CaptureLauncher = delegate { launches++; };
            timer.Tick += delegate
            {
                try
                {
                    Require((DateTime.Now - start).TotalSeconds < 15, "Shortcut UI test timeout.");
                    var editor = app.Windows.OfType<ShortcutEditor>().FirstOrDefault();
                    string error;
                    if (phase == 0)
                    {
                        Require(shelf.TrySelectCaptureShortcut("Custom", 6, 0x78, out error), "Initial custom registration: " + error);
                        Require(shelf.CaptureHotkeyRegistered && shelf.ShortcutCollects, "Custom does not register/collect.");
                        occupied = Native.RegisterHotKey(IntPtr.Zero, 1510, 0x4007, 0x7A);
                        Require(occupied, "Cannot reserve competing Ctrl+Alt+Shift+F11.");
                        Require(!shelf.TrySelectCaptureShortcut("Custom", 7, 0x7A, out error) &&
                            store.Settings.CustomCaptureKey == 0x78 && shelf.CaptureHotkeyRegistered, "Conflict discarded current shortcut.");
                        using (var locked = File.Open(Path.Combine(root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                            Require(!shelf.TrySelectCaptureShortcut("Custom", 6, 0x77, out error) &&
                                store.Settings.CustomCaptureKey == 0x78 && shelf.CaptureHotkeyRegistered, "Save failure discarded current shortcut.");
                        Require(Native.RegisterHotKey(IntPtr.Zero, 1511, 0x4006, 0x77), "Failed save leaked candidate registration.");
                        Native.UnregisterHotKey(IntPtr.Zero, 1511);
                        report.Add("PASS: occupied combination and save failure preserve old registration and preference");
                        phase = 1; shelf.Dispatcher.BeginInvoke(new Action(shelf.EditCaptureShortcut));
                    }
                    else if (phase == 1 && editor != null)
                    {
                        Require(Native.GetForegroundWindow() == new WindowInteropHelper(editor).Handle, "Recorder has no foreground focus; no keys injected.");
                        Keys(true); injected = true; phase = 2;
                    }
                    else if (phase == 2 && editor != null)
                    {
                        Require(editor.Candidate != null && editor.Candidate.Label == "Ctrl + Shift + F9", "Actual key recording failed.");
                        Require(launches == 0, "Current shortcut triggered capture while recording.");
                        Keys(false); injected = false;
                        editor.Snapshot(Path.Combine(root, "shortcut-editor-preview.png"));
                        var button = ((Grid)editor.Content).Children.OfType<Grid>().First().Children.OfType<Grid>().Last().Children.OfType<Button>().Last();
                        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                        phase = 3;
                    }
                    else if (phase == 3 && editor == null)
                    {
                        Require(shelf.CaptureHotkeyRegistered && store.Settings.CustomCaptureKey == 0x78, "Saving recorded shortcut failed.");
                        report.Add("PASS: real Ctrl+Shift+F9 recording, own shortcut suspended, save button enables and persists");
                        phase = 4; shelf.Dispatcher.BeginInvoke(new Action(shelf.EditCaptureShortcut));
                    }
                    else if (phase == 4 && editor != null)
                    {
                        editor.Record(6, 0x77); editor.Close(); phase = 5;
                    }
                    else if (phase == 5 && editor == null)
                    {
                        Require(shelf.CaptureHotkeyRegistered && store.Settings.CustomCaptureKey == 0x78, "Cancel changed current combination.");
                        report.Add("PASS: cancel restores old shortcut without saving the new candidate");
                        phase = 6; shelf.Dispatcher.BeginInvoke(new Action(shelf.EditCaptureShortcut));
                    }
                    else if (phase == 6 && editor != null)
                    {
                        editor.Record(7, 0x7A); editor.SaveChoice();
                        Require(editor.IsVisible && store.Settings.CustomCaptureKey == 0x78, "Conflict dismissed dialog or changed settings.");
                        editor.Record(2, 0x7A); editor.SaveChoice(); phase = 7;
                    }
                    else if (phase == 7 && editor == null)
                    {
                        Require(store.Settings.CustomCaptureModifiers == 2 && store.Settings.CustomCaptureKey == 0x7A && shelf.CaptureHotkeyRegistered,
                            "Retry with two-key Ctrl+F11 failed.");
                        Require(new Store(root).Settings.CustomCaptureKey == 0x7A, "Restart lost final two-key combination.");
                        report.Add("PASS: inline conflict feedback permits retry; two-key Ctrl+F11 survives restart");
                        exit = 0; timer.Stop(); shelf.Close();
                    }
                }
                catch (Exception ex)
                {
                    report.Add("FAIL: " + ex);
                    if (injected) { Keys(false); injected = false; }
                    timer.Stop();
                    foreach (var window in app.Windows.OfType<ShortcutEditor>().ToArray()) window.Close();
                    shelf.Close();
                }
            };
            shelf.Loaded += delegate { start = DateTime.Now; timer.Start(); };
            shelf.Show();
            try { app.Run(); }
            finally
            {
                if (injected) Keys(false);
                if (occupied) Native.UnregisterHotKey(IntPtr.Zero, 1510);
                File.WriteAllLines(Path.Combine(root, "shortcut-results.txt"), report, Encoding.UTF8);
            }
            return exit;
        }
    }
}
