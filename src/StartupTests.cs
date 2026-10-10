using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace Snapline
{
    static class StartupTests
    {
        sealed class FailingEntry : IStartupEntry
        {
            public string Read() { return null; }
            public void Write(string command) { throw new UnauthorizedAccessException("Test denial"); }
            public void Remove() { throw new UnauthorizedAccessException("Test denial"); }
        }
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        internal static int Run(string root)
        {
            Require(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Test needs an empty isolated directory.");
            string testKey = @"Software\Snapline\StartupTests\" + Guid.NewGuid().ToString("N");
            var report = new List<string>();
            int exit = 1;
            try
            {
                var store = new Store(root);
                store.Settings.CaptureShortcut = "Custom";
                store.Settings.CustomCaptureModifiers = 6; store.Settings.CustomCaptureKey = 0x78;
                store.AddNote("Test note kept across startup preference changes", "Mint");
                store.Save(); store.SaveSettings();
                string notes = File.ReadAllText(Path.Combine(root, "notes.json"));
                string items = File.ReadAllText(Path.Combine(root, "items.json"));
                // Emulate a settings file from before startup preferences existed.
                string settingsFile = Path.Combine(root, "settings.json");
                string legacy = File.ReadAllText(settingsFile).Replace("\"StartupEnabled\":false,", "").Replace("\"StartupRevision\":0,", "");
                File.WriteAllText(settingsFile, legacy, new UTF8Encoding(false));
                store = new Store(root);
                using (var key = Registry.CurrentUser.CreateSubKey(testKey)) key.SetValue("OtherApplication", "sentinel");
                var entry = new RegistryStartupEntry(testKey);
                string spacedExe = Path.Combine(root, "中文 空格目录", "Snapline.exe");
                var startup = new StartupRegistration(store, entry, spacedExe);
                string error;
                Require(startup.Initialize(out error), "First launch setup: " + error);
                Require(entry.Read() == "\"" + Path.GetFullPath(spacedExe) + "\" --startup", "Startup command not quoted/quiet.");
                Require(new Store(root).Settings.StartupEnabled && new Store(root).Settings.StartupRevision == 1, "First launch preference not persisted.");
                Require(store.Settings.CaptureShortcut == "Custom" && store.Settings.CustomCaptureKey == 0x78 &&
                    File.ReadAllText(Path.Combine(root, "notes.json")) == notes && File.ReadAllText(Path.Combine(root, "items.json")) == items, "Startup setup changed existing data/shortcut.");
                report.Add("PASS: first normal launch/legacy migration, quoted Unicode and space path, own HKCU registration, existing notes/images/shortcut preserved");
                Require(startup.Apply(false, out error), "Disable startup: " + error);
                var reloaded = new Store(root);
                var restarted = new StartupRegistration(reloaded, entry, spacedExe);
                Require(restarted.Initialize(out error) && !reloaded.Settings.StartupEnabled && entry.Read() == null, "Disabled preference reverted after restart.");
                using (var key = Registry.CurrentUser.OpenSubKey(testKey)) Require((string)key.GetValue("OtherApplication") == "sentinel", "Another application's entry was changed.");
                report.Add("PASS: disabling survives restart and removes only Snapline's named entry");
                Require(restarted.Apply(true, out error), "Reenable startup: " + error);
                string movedExe = Path.Combine(root, "Moved", "Snapline.exe");
                var relocated = new StartupRegistration(reloaded, entry, movedExe);
                Require(relocated.Initialize(out error) && entry.Read() == StartupRegistration.Command(movedExe), "Moved portable path not refreshed.");
                report.Add("PASS: explicit reenable and normal launch after portable EXE relocation refresh registration");
                string before = entry.Read();
                using (var locked = File.Open(settingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Require(!relocated.Apply(false, out error) && reloaded.Settings.StartupEnabled && entry.Read() == before, "Save failure lost old startup registration/preference.");
                var denied = new StartupRegistration(reloaded, new FailingEntry(), movedExe);
                Require(!denied.Apply(true, out error) && reloaded.Settings.StartupEnabled, "Registry denial changed preference.");
                Require(new Store(root).Settings.StartupEnabled, "Failed save changed persisted settings.");
                report.Add("PASS: settings save failure rolls back registration; registry denial preserves previous preference");
                Require(StartupRegistration.ManagesSystemStartup(new string[0]) && StartupRegistration.ManagesSystemStartup(new[] { "--startup" }), "Normal/login runs rejected.");
                foreach (var argument in new[] { "--data-dir", "--demo", "--demo-notes", "--no-capture", "--startup-test", "--hover-test" })
                    Require(!StartupRegistration.ManagesSystemStartup(new[] { argument }), "QA run can configure real startup: " + argument);
                bool longPathRejected = false;
                try { StartupRegistration.Command(Path.Combine(root, new string('a', 260), "Snapline.exe")); }
                catch (Exception) { longPathRejected = true; }
                Require(longPathRejected, "Too-long startup command accepted.");
                report.Add("PASS: test/demo/custom-data runs cannot configure system startup; overlong paths report failure");
                reloaded.Settings.CaptureShortcut = "None"; reloaded.SaveSettings();
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var shelf = new Shelf(reloaded, true, false) { StartCollapsed = true, Startup = relocated };
                app.MainWindow = shelf;
                shelf.Loaded += delegate
                {
                    try
                    {
                        Require(!shelf.Expanded && shelf.ActualHeight <= 8, "Login startup expanded the shelf.");
                        shelf.startupMenu.PerformClick();
                        Require(!reloaded.Settings.StartupEnabled && entry.Read() == null && !shelf.startupMenu.Checked, "Tray toggle failed to disable startup.");
                        shelf.startupMenu.PerformClick();
                        Require(reloaded.Settings.StartupEnabled && entry.Read() == before && shelf.startupMenu.Checked, "Tray toggle failed to reenable startup.");
                        Require(!shelf.Expanded, "Startup toggle unexpectedly expanded the shelf.");
                        report.Add("PASS: real WPF login launch stays collapsed; tray startup toggle disables and reenables persisted registration");
                        exit = 0;
                    }
                    catch (Exception ex) { report.Add("FAIL: " + ex); }
                    shelf.Dispatcher.BeginInvoke(new Action(shelf.Close));
                };
                shelf.Show(); app.Run();
            }
            catch (Exception ex) { report.Add("FAIL: " + ex); }
            finally
            {
                // Only this test's generated key is removed, never Windows' real Run key.
                if (testKey.StartsWith(@"Software\Snapline\StartupTests\", StringComparison.Ordinal))
                    Registry.CurrentUser.DeleteSubKeyTree(testKey, false);
                Directory.CreateDirectory(root);
                File.WriteAllLines(Path.Combine(root, "startup-results.txt"), report, Encoding.UTF8);
            }
            return exit;
        }
    }
}
