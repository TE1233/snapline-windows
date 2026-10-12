using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Snapline
{
    static class RemarkTests
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            yield return parent;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                foreach (var item in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return item;
        }
        static Button FindButton(Shelf shelf, Shot shot)
        { return Descendants((DependencyObject)shelf.Content).OfType<Button>().First(x => AutomationProperties.GetAutomationId(x) == "Remark-" + shot.File); }
        static RemarkText StripText(Shelf shelf, Shot shot)
        { return Descendants((DependencyObject)FindButton(shelf, shot).Content).OfType<RemarkText>().First(); }
        static void Click(Button button) { ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); }
        static void Key(RemarkEditor editor, System.Windows.Input.Key key)
        {
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, key)
            { RoutedEvent = UIElement.PreviewKeyDownEvent });
        }
        static void Snapshot(FrameworkElement element, string path)
        {
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
        internal static int Run(string root)
        {
            Require(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Remark tests need an empty isolated directory.");
            var report = new List<string>();
            var store = new Store(root);
            var shot = store.Add(Program.Demo("LAYOUT IDEAS", "把好想法，留给下次创作。", "#315B75"), "测试截图");
            DateTime created = shot.Created; string digest = shot.Digest, filename = shot.File;
            byte[] png = File.ReadAllBytes(store.FullPath(shot));
            store.Settings.CaptureShortcut = "None"; store.Settings.WelcomeShown = true; store.SaveSettings();
            store.AddNote("今天的计划\n把想到的事情，轻轻挂起来。", "Mint");
            string notes = File.ReadAllText(Path.Combine(root, "notes.json"));
            string legacy = File.ReadAllText(Path.Combine(root, "items.json")).Replace("\"Remark\":null,", "");
            File.WriteAllText(Path.Combine(root, "items.json"), legacy, new UTF8Encoding(false));
            var upgraded = new Store(root);
            Require(upgraded.Items.Count == 1 && String.IsNullOrEmpty(upgraded.Items[0].Remark), "Legacy screenshot did not load with empty remark.");
            string sample = "周五做方案时，参考这个布局。\n配色还需要确认 😊";
            store.UpdateRemark(shot, sample);
            Require(new Store(root).Items[0].Remark == sample, "Unicode/multiline remark lost on restart.");
            using (var locked = File.Open(Path.Combine(root, "items.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false; try { store.UpdateRemark(shot, "must not overwrite"); } catch { failed = true; }
                Require(failed && shot.Remark == sample && new Store(root).Items[0].Remark == sample, "Failed save changed remark.");
            }
            bool oversized = false; try { store.UpdateRemark(shot, new string('x', Store.MaxRemarkText + 1)); } catch { oversized = true; }
            Require(oversized && shot.Remark == sample, "Oversize input changed remark.");
            bool removed = false; try { store.UpdateRemark(new Shot(), "removed"); } catch { removed = true; }
            Require(removed, "Removed screenshot accepted a remark.");
            Require(shot.Created == created && shot.Digest == digest && shot.File == filename && File.ReadAllBytes(store.FullPath(shot)).SequenceEqual(png), "Remark modified screenshot pixels/identity/order.");
            Require(File.ReadAllText(Path.Combine(root, "notes.json")) == notes && store.Settings.CaptureShortcut == "None", "Remark changed unrelated notes/shortcut.");
            report.Add("PASS: legacy screenshot migration and Unicode/emoji/multiline remark persistence across restart");
            report.Add("PASS: save failure rolls back; overlong and removed-shot updates are rejected; PNG bytes, creation order, notes and shortcuts unchanged");
            store.UpdateRemark(shot, "");
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var shelf = new Shelf(store, true, true); app.MainWindow = shelf;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            int phase = 0, exit = 1;
            DateTime start = DateTime.Now; FileStream lockFile = null; ToolTip preview = null;
            IntPtr foreground = Native.GetForegroundWindow();
            ScrollViewer longScroll = null; string longText = String.Join("\n", Enumerable.Repeat("这个截图的背景、后续行动，以及想提醒自己的细节。", 60));
            timer.Tick += delegate
            {
                try
                {
                    Require((DateTime.Now - start).TotalSeconds < 16, "Remark UI test timeout at phase " + phase);
                    var editor = shelf.remarkEditor;
                    if (phase == 0)
                    {
                        Require(editor == null && Native.GetForegroundWindow() == foreground, "Remark entry opened/activated an editor automatically.");
                        Require(StripText(shelf, shot).Text.Contains("添加备注"), "Blank screenshot lacks add entry.");
                        report.Add("PASS: adding a screenshot shows a paper-strip entry and does not automatically open an editor or steal focus");
                        Click(FindButton(shelf, shot)); phase = 1;
                    }
                    else if (phase == 1 && editor != null && editor.IsLoaded)
                    {
                        Require(editor.Input.IsKeyboardFocused, "Explicitly opened editor did not focus its input.");
                        editor.Input.Text = sample;
                        editor.Snapshot(Path.Combine(root, "remark-editor-preview.png"));
                        Key(editor, System.Windows.Input.Key.Enter); phase = 2;
                    }
                    else if (phase == 2 && editor == null)
                    {
                        Require(shot.Remark == sample && new Store(root).Items[0].Remark == sample, "Enter failed to save/reload remark.");
                        Require(StripText(shelf, shot).Text == sample.Replace("\n", " "), "Saved single-line paper strip not updated.");
                        shelf.Snapshot(Path.Combine(root, "remark-shelf-preview.png"));
                        var card = Descendants((DependencyObject)shelf.Content).OfType<Grid>().First(x => AutomationProperties.GetAutomationId(x) == "ShotCard-" + shot.File);
                        preview = (ToolTip)card.ToolTip;
                        preview.IsOpen = true; phase = 3;
                    }
                    else if (phase == 3)
                    {
                        Require(preview.IsOpen, "Enlarged preview failed to open.");
                        Require(Descendants((Border)preview.Content).OfType<TextBlock>().Any(x => x.Text == sample), "Full remark missing from enlarged preview.");
                        Snapshot((FrameworkElement)preview.Content, Path.Combine(root, "remark-full-preview.png"));
                        preview.IsOpen = false; Click(FindButton(shelf, shot)); phase = 4;
                    }
                    else if (phase == 4 && editor != null && editor.IsLoaded)
                    {
                        Require(editor.Input.Text.Replace("\r\n", "\n") == sample, "Edit did not restore previous remark.");
                        editor.Input.Text = "discard this edit"; Key(editor, System.Windows.Input.Key.Escape); phase = 5;
                    }
                    else if (phase == 5 && editor == null)
                    {
                        Require(shot.Remark == sample && new Store(root).Items[0].Remark == sample, "Esc saved discarded edit.");
                        report.Add("PASS: paper entry opens a focused small editor; Enter saves, full preview includes remark, Esc cancels changes");
                        Click(FindButton(shelf, shot)); phase = 6;
                    }
                    else if (phase == 6 && editor != null && editor.IsLoaded)
                    {
                        lockFile = File.Open(Path.Combine(root, "items.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                        editor.Input.Text = "保存失败后，再试一次。"; editor.SaveAndClose();
                        Require(editor.IsVisible && editor.Input.Text.Contains("再试一次") && shot.Remark == sample, "Failed save closed editor or discarded text/old remark.");
                        lockFile.Dispose(); lockFile = null;
                        editor.SaveAndClose(); phase = 7;
                    }
                    else if (phase == 7 && editor == null)
                    {
                        Require(shot.Remark == "保存失败后，再试一次。", "Retry failed.");
                        report.Add("PASS: save failure keeps input/window/old remark; retry saves successfully");
                        var card = Descendants((DependencyObject)shelf.Content).OfType<Grid>().First(x => AutomationProperties.GetAutomationId(x) == "ShotCard-" + shot.File);
                        var menu = card.ContextMenu; menu.PlacementTarget = card; menu.IsOpen = true;
                        var item = menu.Items.OfType<MenuItem>().First(x => x.Header.ToString().Contains("截图备注"));
                        ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke(); phase = 8;
                    }
                    else if (phase == 8 && editor != null && editor.IsLoaded)
                    { editor.Input.Text = ""; editor.SaveAndClose(); phase = 9; }
                    else if (phase == 9 && editor == null && shelf.FlyingCount == 0 && shelf.HoverPreviewAllowed)
                    {
                        Require(String.IsNullOrEmpty(shot.Remark) && StripText(shelf, shot).Text.Contains("添加备注"), "Clear did not restore add entry.");
                        store.UpdateRemark(shot, sample);
                        Require(File.ReadAllBytes(store.FullPath(shot)).SequenceEqual(png), "UI editing changed exported source PNG.");
                        report.Add("PASS: image context menu opens remark editor; clearing restores add entry; copy/drag source PNG remains byte-identical");
                        store.UpdateRemark(shot, longText);
                        var card = Descendants((DependencyObject)shelf.Content).OfType<Grid>().First(x => AutomationProperties.GetAutomationId(x) == "ShotCard-" + shot.File);
                        preview = (ToolTip)card.ToolTip; preview.IsOpen = true; phase = 10;
                    }
                    else if (phase == 10)
                    {
                        Require(preview.IsOpen, "Long remark preview did not open.");
                        longScroll = Descendants((Border)preview.Content).OfType<ScrollViewer>().First();
                        Require(longScroll.ScrollableHeight > 0 && ((TextBlock)longScroll.Content).Text == longText, "Long remark was truncated instead of scrollable.");
                        var target = (UIElement)preview.PlacementTarget;
                        target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                        phase = 11;
                    }
                    else if (phase == 11)
                    {
                        Require(longScroll.VerticalOffset > 0, "Image hover wheel did not scroll long remark.");
                        preview.IsOpen = false; store.UpdateRemark(shot, sample);
                        report.Add("PASS: full long remark remains scrollable in a bounded preview; mouse wheel over image scrolls text");
                        Click(FindButton(shelf, shot)); phase = 12;
                    }
                    else if (phase == 12 && editor != null && editor.IsLoaded)
                    {
                        editor.Input.Text = "关闭应用时，保存这句用途备注。";
                        using (var locked = File.Open(Path.Combine(root, "items.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            shelf.Close();
                            Require(shelf.IsVisible && editor.IsVisible, "Failed app-exit save closed the editor/app.");
                        }
                        shelf.Close();
                        Require(new Store(root).Items[0].Remark == "关闭应用时，保存这句用途备注。", "App exit lost active remark draft.");
                        report.Add("PASS: app exit saves an active draft; save failure cancels app exit and retains editor text");
                        exit = 0; timer.Stop();
                    }
                }
                catch (Exception ex)
                {
                    report.Add("FAIL: " + ex); timer.Stop();
                    if (lockFile != null) { lockFile.Dispose(); lockFile = null; }
                    if (shelf.remarkEditor != null) shelf.remarkEditor.Close(); shelf.Close();
                }
            };
            shelf.Loaded += delegate { timer.Start(); }; shelf.Show();
            try { app.Run(); }
            finally { if (lockFile != null) lockFile.Dispose(); File.WriteAllLines(Path.Combine(root, "remark-results.txt"), report, Encoding.UTF8); }
            return exit;
        }
    }
}
