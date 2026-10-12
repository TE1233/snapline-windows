using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Snapline
{
    static class InteractionTests
    {
        static void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
        static FrameworkElement[] ShelfChildren(Shelf shelf)
        { return ((Grid)shelf.Content).Children.OfType<Canvas>().First().Children.OfType<FrameworkElement>().Where(x => !Object.Equals(x.Tag, "ShelfFeedback")).ToArray(); }
        static Dictionary<FrameworkElement, double> Angles(Shelf shelf)
        { return ShelfChildren(shelf).Where(x => x.RenderTransform is RotateTransform).ToDictionary(x => x, x => ((RotateTransform)x.RenderTransform).Angle); }
        static void CheckAngles(Dictionary<FrameworkElement, double> angles, double tolerance)
        { foreach (var pair in angles) Check(Math.Abs(((RotateTransform)pair.Key.RenderTransform).Angle - pair.Value) < tolerance, "An existing card's angle jumped when editing ended."); }
        static void CheckFallMotion(List<string> report)
        {
            double previous = 0;
            for (int i = 3; i <= 8; i++)
            {
                double a = PaperFallMotion.At(i / 10.0, 800).Y, b = PaperFallMotion.At((i + 1) / 10.0, 800).Y;
                Check(b - a > previous, "Equal-time falling distances do not increase."); previous = b - a;
            }
            for (int step = 0; step <= 60; step++)
            {
                double progress = step / 60.0; var pose = PaperFallMotion.At(progress, 800);
                Check(Math.Abs(pose.X) < 40 && Math.Abs(pose.Angle) < .16, "Fall has excessive sideways movement/rotation.");
                for (int j = 0; j < 12; j++) for (int i = 0; i < 16; i++)
                {
                    Point a = PaperFallMotion.Vertex(i / 16.0, j / 12.0, 206, 177, progress, pose, .03);
                    Point b = PaperFallMotion.Vertex((i + 1) / 16.0, j / 12.0, 206, 177, progress, pose, .03);
                    Point c = PaperFallMotion.Vertex(i / 16.0, (j + 1) / 12.0, 206, 177, progress, pose, .03);
                    Point d = PaperFallMotion.Vertex((i + 1) / 16.0, (j + 1) / 12.0, 206, 177, progress, pose, .03);
                    Check((b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X) > 0 &&
                        (d.X-b.X)*(c.Y-b.Y)-(d.Y-b.Y)*(c.X-b.X) > 0, "Paper flips or a mesh triangle folds over.");
                }
            }
            report.Add("PASS: 1200ms downward release; equal-time drop distances increase; mild sway; all mesh triangles remain front-facing");
        }
        static Grid Card(Shelf shelf, Shot shot)
        { return Descendants((DependencyObject)shelf.Content).OfType<Grid>().First(x => AutomationProperties.GetAutomationId(x) == "ShotCard-" + shot.File); }
        static void CheckRemovalPreviewBlocked(Shelf shelf)
        {
            Check(!shelf.HoverPreviewAllowed, "Falling paper still allows enlarged hover previews.");
            foreach (var target in Descendants((DependencyObject)shelf.Content).OfType<FrameworkElement>().Where(x => x.ToolTip is ToolTip))
                Check(!ToolTipService.GetIsEnabled(target), "A reflowed screenshot/memo/help popup was enabled before all falls ended.");
        }
        static Point OnCard(Shelf shelf, Shot shot, double heightFraction)
        {
            var card = Card(shelf, shot); card.UpdateLayout();
            return card.PointToScreen(new Point(card.ActualWidth / 2, card.ActualHeight * heightFraction));
        }
        static bool InEditor(RemarkEditor editor, Point point)
        {
            Native.Rect bounds; Check(Native.GetWindowRect(new WindowInteropHelper(editor).Handle, out bounds), "Cannot read native editor bounds.");
            return point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject value)
        {
            yield return value;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(value, i))) yield return child;
        }
        static void Snapshot(FrameworkElement element, string path)
        {
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) png.Save(stream);
        }
        internal static int Run(string root)
        {
            Check(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Use an empty isolated test directory.");
            var store = new Store(root);
            var shot = store.Add(Program.Demo("A SMALL REMINDER", "把截图的用途，轻轻记下来。", "#315B75"), "交互测试截图");
            byte[] pixels = File.ReadAllBytes(store.FullPath(shot));
            string external = Path.Combine(root, "external-original.png"); File.WriteAllBytes(external, pixels);
            var note = store.AddNote("明天出门时，记得带相机。", "Mint");
            store.UpdateRemark(shot, "原有用途备注");
            store.Settings.CaptureShortcut = "None"; store.Settings.WelcomeShown = true; store.SaveSettings();
            using (var locked = File.Open(Path.Combine(root, "items.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false; try { store.Remove(shot); } catch (IOException) { failed = true; }
                Check(failed && store.Items.Contains(shot) && new Store(root).Items.Count == 1 && File.ReadAllBytes(store.FullPath(shot)).SequenceEqual(pixels), "Failed deletion lost the screenshot or changed pixels/order.");
            }
            var report = new List<string> { "PASS: failed screenshot removal preserves its record, cache bytes and order" };
            CheckFallMotion(report);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var shelf = new Shelf(store, true, true); app.MainWindow = shelf;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            DateTime start = DateTime.UtcNow, fakeNow = start; bool inside = true, onShot = false;
            DateTime nativeLeftAt = start;
            Point testPointer = new Point(); Shot otherShot = null;
            Shot hoverSurvivor = null, dropFirst = null, dropSecond = null;
            Memo hoverMemo = null;
            ToolTip blockedPreview = null;
            bool sawOnlySecondFall = false;
            DateTime lastFallEndedAt = start;
            Point stationaryPointer = new Point();
            DateTime motionAt = start;
            Grid savedCard = null;
            FrameworkElement[] savedChildren = null;
            Dictionary<FrameworkElement, double> heldAngles = null;
            int phase = 0, exit = 1; FileStream lockFile = null;
            string first = "周五做方案时，参考这个布局。\n完整的备注和换行仍保留 😊";
            Action open = delegate
            {
                inside = true; onShot = false;
                var strip = Descendants((DependencyObject)shelf.Content).OfType<Button>().First(x => AutomationProperties.GetAutomationId(x) == "Remark-" + shot.File);
                shelf.EditRemark(shot, strip);
                Check(shelf.remarkEditor != null, "Shelf stayed blocked after closing its editor.");
                savedCard = Card(shelf, shot);
                savedChildren = ShelfChildren(shelf);
                Check(!shelf.HoverPreviewAllowed, "Remark editing still allows enlarged hover previews.");
                foreach (var target in Descendants((DependencyObject)shelf.Content).OfType<FrameworkElement>().Where(x => x.ToolTip is ToolTip))
                    Check(!ToolTipService.GetIsEnabled(target), "A screenshot/memo/help popup is enabled during remark editing.");
                shelf.remarkEditor.PointerInside = delegate { return inside; };
                shelf.remarkEditor.PointerOnShot = delegate { return onShot; };
                shelf.remarkEditor.Clock = delegate { return fakeNow; };
            };
            timer.Tick += delegate
            {
                try
                {
                    Check((DateTime.UtcNow - start).TotalSeconds < 35, "Interaction test timeout at phase " + phase);
                    var editor = shelf.remarkEditor;
                    if (phase == 0) { open(); phase = 1; }
                    else if (phase == 1 && editor.IsLoaded)
                    {
                        editor.Input.Text = first;
                        inside = false; onShot = true; editor.ObservePointer(editor.PointerInRemarkArea(), fakeNow);
                        fakeNow = fakeNow.AddSeconds(2); editor.ObservePointer(editor.PointerInRemarkArea(), fakeNow);
                        Check(editor.IsVisible && !editor.IsFading && !editor.IsDismissPending, "Opened editor closed while pointer stayed on its screenshot without entering editor.");
                        onShot = false;
                        inside = false; editor.ObservePointer(false, fakeNow);
                        fakeNow = fakeNow.AddMilliseconds(100); editor.ObservePointer(false, fakeNow);
                        Check(editor.IsVisible && !editor.IsFading && editor.IsDismissPending, "The 0.2-second delay is inaccurate.");
                        var children = Descendants((DependencyObject)editor.Content).OfType<TextBlock>().ToArray();
                        Check(!children.Any(x => AutomationProperties.GetAutomationId(x) == "RemarkCloseCountdown" || AutomationProperties.GetAutomationId(x) == "RemarkSaveReminder" || x.Text.Contains("秒后")) &&
                            !Descendants((DependencyObject)editor.Content).OfType<Border>().Any(x => x.Height == 3), "Automatic dismissal reminder/progress still shown.");
                        editor.Snapshot(Path.Combine(root, "remark-editor-preview.png")); phase = 2;
                        heldAngles = Angles(shelf);
                    }
                    else if (phase == 2)
                    {
                        CheckAngles(heldAngles, .001);
                        inside = true; editor.ObservePointer(true, fakeNow);
                        Check(!editor.IsDismissPending && editor.Opacity == 1 && shot.Remark == "原有用途备注", "Returning did not cancel dismissal without saving early.");
                        inside = false; editor.ObservePointer(false, fakeNow);
                        fakeNow = fakeNow.AddMilliseconds(199); editor.ObservePointer(false, fakeNow);
                        Check(!editor.IsFading && shot.Remark == "原有用途备注", "Editor saved before 0.2 seconds.");
                        fakeNow = fakeNow.AddMilliseconds(1); editor.ObservePointer(false, fakeNow);
                        Check(editor.IsFading && shot.Remark == first && new Store(root).Items[0].Remark == first, "Auto-dismiss did not save before fading.");
                        Check(Object.ReferenceEquals(savedCard, Card(shelf, shot)), "Saving rebuilt all cards while editor remains open.");
                        onShot = true; editor.ObservePointer(editor.PointerInRemarkArea(), fakeNow);
                        Check(!editor.IsFading && editor.Opacity == 1, "Returning to current screenshot during fade did not restore editor."); onShot = false;
                        report.Add("PASS: current screenshot holds newly opened editor; 199/200ms boundary; no hint/countdown/progress; returning to editor or current shot cancels dismissal/fade; saved before fading"); phase = 3;
                    }
                    else if (phase == 3)
                    {
                        editor.Input.Text = "自动收起时，保存这句提醒 😊";
                        inside = false; editor.ObservePointer(false, fakeNow);
                        fakeNow = fakeNow.AddMilliseconds(150); editor.ObservePointer(false, fakeNow);
                        editor.Input.Text += "。";
                        editor.ObservePointer(false, fakeNow); fakeNow = fakeNow.AddMilliseconds(100); editor.ObservePointer(false, fakeNow);
                        Check(!editor.IsFading, "Continuing to type did not restart the countdown.");
                        fakeNow = fakeNow.AddMilliseconds(100); editor.ObservePointer(false, fakeNow); phase = 4;
                    }
                    else if (phase == 4 && editor == null)
                    {
                        Check(savedChildren.SequenceEqual(ShelfChildren(shelf)), "Saving recreated unrelated cards or rope.");
                        Check(shelf.HoverPreviewAllowed && ToolTipService.GetIsEnabled(Card(shelf, shot)), "Hover preview did not restore after editing.");
                        var preview = (ToolTip)Card(shelf, shot).ToolTip;
                        var previewScroll = preview.Tag as ScrollViewer;
                        Check(previewScroll != null && ((TextBlock)previewScroll.Content).Text == shot.Remark, "Local update left stale full-preview text.");
                        report.Add("PASS: editing disables screenshot/memo/help hover popups before opening; saving retains visuals until close; hover restored afterward");
                        Check(new Store(root).Items[0].Remark == "自动收起时，保存这句提醒 😊。", "Auto-close lost the latest input.");
                        open(); phase = 45;
                    }
                    else if (phase == 45 && editor.IsLoaded)
                    {
                        heldAngles = Angles(shelf);
                        string prior = shot.Remark; editor.Input.Text = "取消这次改动"; editor.Close();
                        Check(shot.Remark == prior && savedChildren.SequenceEqual(ShelfChildren(shelf)), "Cancel rebuilt the shelf or saved discarded input.");
                        CheckAngles(heldAngles, .001);
                        open(); phase = 46;
                    }
                    else if (phase == 46 && editor.IsLoaded)
                    {
                        heldAngles = Angles(shelf); editor.SaveAndClose();
                        Check(savedChildren.SequenceEqual(ShelfChildren(shelf)), "Unchanged save rebuilt the shelf.");
                        CheckAngles(heldAngles, .001);
                        report.Add("PASS: save, cancel and unchanged save keep all existing card/rope objects; pause/resume retains angles; current strip and full preview update locally");
                        open(); phase = 5;
                    }
                    else if (phase == 5 && editor.IsLoaded)
                    {
                        lockFile = File.Open(Path.Combine(root, "items.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                        editor.Input.Text = "保存失败时，保留窗口里的文字。";
                        inside = false; editor.ObservePointer(false, fakeNow); fakeNow = fakeNow.AddMilliseconds(200); editor.ObservePointer(false, fakeNow);
                        Check(editor.IsVisible && !editor.IsFading && !editor.IsDismissPending && shot.Remark == "自动收起时，保存这句提醒 😊。", "Failed auto-save closed editor or overwrote prior text.");
                        fakeNow = fakeNow.AddSeconds(8); editor.ObservePointer(false, fakeNow);
                        Check(editor.IsVisible && editor.Input.Text.Contains("保留窗口"), "Failed save retried/closed without further interaction.");
                        lockFile.Dispose(); lockFile = null; inside = true; editor.ObservePointer(true, fakeNow);
                        inside = false; editor.ObservePointer(false, fakeNow); fakeNow = fakeNow.AddMilliseconds(200); editor.ObservePointer(false, fakeNow); phase = 6;
                    }
                    else if (phase == 6 && editor == null)
                    {
                        Check(shot.Remark == "保存失败时，保留窗口里的文字。" && File.ReadAllBytes(store.FullPath(shot)).SequenceEqual(pixels), "Retry did not preserve latest text/original PNG.");
                        report.Add("PASS: typing postpones dismissal; automatic close releases shelf; failed auto-save keeps input and old record, pointer return enables retry");
                        shelf.Snapshot(Path.Combine(root, "compact-remark-preview.png"));
                        otherShot = store.Add(Program.Demo("OTHER CARD", "另一张截图的区域不保护当前备注窗口。", "#64716F"), "另一张测试截图");
                        open(); phase = 70;
                    }
                    else if (phase == 70 && editor.IsLoaded)
                    {
                        Check(!ToolTipService.GetIsEnabled(Card(shelf, otherShot)), "Other screenshot still previews during editing.");
                        editor.Input.Text = "离开这张截图和窗口后，保存完整用途。";
                        testPointer = OnCard(shelf, shot, 0.25);
                        editor.PointerInside = delegate { return InEditor(editor, testPointer); };
                        editor.PointerOnShot = delegate { return shelf.IsPointerOnRemarkShot(shot, testPointer); };
                        editor.Clock = delegate { return DateTime.UtcNow; };
                        Check(editor.PointerOnShot() && !editor.PointerInside(), "Screenshot screen-space hit test failed.");
                        Check(shelf.IsPointerOnRemarkShot(shot, OnCard(shelf, shot, 0.95)), "Screenshot remark strip does not hold editor.");
                        Check(!shelf.IsPointerOnRemarkShot(shot, OnCard(shelf, otherShot, 0.25)), "Other screenshot incorrectly holds editor.");
                        Native.Rect bounds; Native.GetWindowRect(new WindowInteropHelper(shelf).Handle, out bounds);
                        Check(!shelf.IsPointerOnRemarkShot(shot, new Point(bounds.Left + 40, bounds.Top + 70)), "Blank shelf area incorrectly holds editor.");
                        var memo = Descendants((DependencyObject)shelf.Content).OfType<Grid>().First(x => Object.ReferenceEquals(x.Tag, note));
                        Check(!shelf.IsPointerOnRemarkShot(shot, memo.PointToScreen(new Point(memo.ActualWidth / 2, memo.ActualHeight / 2))), "Memo area incorrectly holds editor.");
                        nativeLeftAt = DateTime.UtcNow; phase = 71;
                    }
                    else if (phase == 71)
                    {
                        Check(editor != null && !editor.IsDismissPending && editor.IsVisible, "Screenshot hover without entering editor did not hold it open.");
                        if ((DateTime.UtcNow - nativeLeftAt).TotalSeconds < 0.7) return;
                        Native.Rect bounds; Native.GetWindowRect(new WindowInteropHelper(editor).Handle, out bounds);
                        testPointer = new Point((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
                        nativeLeftAt = DateTime.UtcNow; phase = 73;
                    }
                    else if (phase == 73)
                    {
                        Check(editor != null && !editor.IsDismissPending && editor.IsVisible, "Editor hover did not hold window open.");
                        if ((DateTime.UtcNow - nativeLeftAt).TotalSeconds < 0.35) return;
                        testPointer = OnCard(shelf, otherShot, 0.25);
                        Check(!editor.PointerInRemarkArea(), "Other screenshot overlaps the protected editor/card region.");
                        nativeLeftAt = DateTime.UtcNow; phase = 72;
                    }
                    else if (phase == 72)
                    {
                        double elapsed = (DateTime.UtcNow - nativeLeftAt).TotalSeconds;
                        if (editor != null && elapsed < 0.18) Check(!editor.IsFading, "Real timer started fade early.");
                        if (editor == null)
                        {
                            Check(savedChildren.SequenceEqual(ShelfChildren(shelf)), "Auto-saving rebuilt another screenshot or memo.");
                            Check(elapsed >= 0.33 && elapsed < 1.2 && new Store(root).Items.First(x => x.File == shot.File).Remark == "离开这张截图和窗口后，保存完整用途。", "Real dismissal timing/persistence failed: " + elapsed);
                            store.Remove(otherShot);
                            report.Add("PASS: real window/card screen coordinates include photo and strip, exclude other shot/memo/blank shelf; real timer holds current card and editor, saves and fades after leaving in " + elapsed.ToString("0.00") + " seconds; user's pointer untouched");
                            open(); phase = 7;
                        }
                    }
                    else if (phase == 7 && editor.IsLoaded)
                    {
                        var strip = Descendants((DependencyObject)shelf.Content).OfType<Button>().First(x => AutomationProperties.GetAutomationId(x) == "Remark-" + shot.File);
                        var card = Descendants((DependencyObject)shelf.Content).OfType<Grid>().First(x => AutomationProperties.GetAutomationId(x) == "ShotCard-" + shot.File);
                        Check(Math.Abs(strip.ActualWidth - card.ActualWidth) < 0.1 && strip.ActualHeight <= 30.1, "Compact remark width/height is incorrect.");
                        Check(Descendants((DependencyObject)strip.Content).OfType<RemarkText>().First().Text == shot.Remark, "Tracking altered accessible/stored text.");
                        editor.Input.Text = ""; editor.SaveAndClose();
                        Check(shelf.FlyingCount == 1 && String.IsNullOrEmpty(shot.Remark) && store.Items.Contains(shot), "Clearing remark did not animate only the strip."); phase = 8;
                        CheckRemovalPreviewBlocked(shelf);
                    }
                    else if (phase == 8 && shelf.LastPaper.Progress >= .3)
                    {
                        Check(shelf.LastFlight.Y > 0 && File.ReadAllBytes(store.FullPath(shot)).SequenceEqual(pixels), "Strip did not fall or changed original pixels.");
                        CheckRemovalPreviewBlocked(shelf);
                        shelf.LastPaper.Snapshot(Path.Combine(root, "remark-fall-preview.png")); phase = 9;
                    }
                    else if (phase == 9 && shelf.FlyingCount == 0)
                    {
                        shelf.RemoveMemo(note);
                        Check(shelf.FlyingCount == 1 && store.Notes.Count == 0 && new Store(root).Notes.Count == 0, "Memo deletion did not commit/animate."); phase = 10;
                        CheckRemovalPreviewBlocked(shelf);
                    }
                    else if (phase == 10 && shelf.LastPaper.Progress >= .35)
                    {
                        Check(shelf.LastFlight.Y > 0, "Memo did not fall.");
                        CheckRemovalPreviewBlocked(shelf);
                        int styles = Native.GetWindowLong(new WindowInteropHelper(shelf.LastPaper).Handle, -20);
                        Check((styles & 0x08000020) == 0x08000020 && Native.GetForegroundWindow() != new WindowInteropHelper(shelf.LastPaper).Handle, "Falling surface intercepts clicks or steals focus.");
                        var flying = (BitmapSource)shelf.LastFlying.Source;
                        var pixelsOnFlight = new byte[flying.PixelWidth * flying.PixelHeight * 4]; flying.CopyPixels(pixelsOnFlight, flying.PixelWidth * 4, 0);
                        Check(Enumerable.Range(0, pixelsOnFlight.Length / 4).Count(i => pixelsOnFlight[i * 4 + 3] > 0) > 200, "Flying card is transparent.");
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(flying));
                        using (var output = File.Create(Path.Combine(root, "flying-memo-source.png"))) encoder.Save(output);
                        report.Add("Flight layout: image=" + shelf.LastFlying.Width + "x" + shelf.LastFlying.Height + ", down=" + shelf.LastFlight.Y + ", opacity=" + shelf.LastPaper.Opacity);
                        shelf.LastPaper.Snapshot(Path.Combine(root, "memo-fall-preview.png")); phase = 11;
                    }
                    else if (phase == 11 && shelf.FlyingCount == 0)
                    {
                        using (var locked = File.Open(Path.Combine(root, "items.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            shelf.RemoveShot(shot);
                            Check(shelf.FlyingCount == 0 && store.Items.Contains(shot) && File.Exists(store.FullPath(shot)), "Failed deletion hid/animated the retained screenshot.");
                        }
                        shelf.RemoveShot(shot);
                        Check(shelf.FlyingCount == 1 && store.Items.Count == 0 && new Store(root).Items.Count == 0, "Screenshot deletion did not commit/animate: flying=" + shelf.FlyingCount + ", items=" + store.Items.Count + "."); phase = 12;
                    }
                    else if (phase == 12 && shelf.LastPaper.Progress >= .55)
                    {
                        Check(shelf.LastFlight.Y > 0 && shelf.LastPaper.ActualHeight > shelf.ActualHeight, "Screenshot fall is clipped to shelf.");
                        CheckRemovalPreviewBlocked(shelf);
                        shelf.LastPaper.Snapshot(Path.Combine(root, "screenshot-fall-preview.png")); phase = 13;
                    }
                    else if (phase == 13 && shelf.FlyingCount == 0)
                    {
                        Check(File.ReadAllBytes(external).SequenceEqual(pixels), "Deletion changed the external original.");
                        Check(shelf.LastFlying == null && shelf.LastFlight == null && shelf.LastPaper == null, "Fall retained overlay/bitmap references.");
                        report.Add("PASS: compact strip stays image-width; original text intact; strip/memo/screenshot fall below shelf on click-through surfaces and release resources");
                        report.Add("PASS: cleared remark preserves screenshot; persisted deletions survive restart; failed deletion has no exit animation; external original unchanged");
                        hoverSurvivor = store.Add(Program.Demo("SURVIVOR", "删除后，等待掉落结束再预览。", "#315B75"), "预览保留截图");
                        dropFirst = store.Add(Program.Demo("FIRST FALL", "第一次删除。", "#746DA1"), "删除第一张");
                        dropSecond = store.Add(Program.Demo("SECOND FALL", "连续删除。", "#447A61"), "删除第二张");
                        hoverMemo = store.AddNote("连续删除期间，便签也不抢先预览。", "Mint");
                        testPointer = OnCard(shelf, hoverSurvivor, .4);
                        shelf.PointerPosition = delegate { return new Native.Point { X = (int)Math.Round(testPointer.X), Y = (int)Math.Round(testPointer.Y) }; };
                        ((ToolTip)Card(shelf, hoverSurvivor).ToolTip).IsOpen = true;
                        phase = 130;
                    }
                    else if (phase == 130)
                    {
                        Check(((ToolTip)Card(shelf, hoverSurvivor).ToolTip).IsOpen, "Removal preview fixture did not open its original preview.");
                        shelf.RemoveShot(dropFirst);
                        Check(shelf.FlyingCount == 1, "First preview-blocking removal failed.");
                        CheckRemovalPreviewBlocked(shelf);
                        testPointer = OnCard(shelf, hoverSurvivor, .4);
                        blockedPreview = (ToolTip)Card(shelf, hoverSurvivor).ToolTip;
                        blockedPreview.IsOpen = true; // Simulate an already queued tooltip-open request after reflow.
                        motionAt = DateTime.UtcNow; phase = 131;
                    }
                    else if (phase == 131 && (DateTime.UtcNow-motionAt).TotalMilliseconds > 300)
                    {
                        Check(!blockedPreview.IsOpen, "Queued reflow tooltip opened while screenshot was falling.");
                        shelf.RemoveShot(dropSecond);
                        Check(shelf.FlyingCount == 2, "Consecutive falls did not overlap.");
                        CheckRemovalPreviewBlocked(shelf);
                        testPointer = stationaryPointer = OnCard(shelf, hoverSurvivor, .4);
                        blockedPreview = (ToolTip)Card(shelf, hoverSurvivor).ToolTip;
                        shelf.LastPaper.Closed += delegate { lastFallEndedAt = DateTime.UtcNow; };
                        phase = 132;
                    }
                    else if (phase == 132)
                    {
                        if (shelf.FlyingCount > 0)
                        {
                            CheckRemovalPreviewBlocked(shelf);
                            Check(!blockedPreview.IsOpen, "Preview opened before the last falling window closed.");
                            if (shelf.FlyingCount == 1) sawOnlySecondFall = true;
                        }
                        else
                        {
                            Check(sawOnlySecondFall, "Did not test suppression after the first of two falls finished.");
                            phase = 133;
                        }
                    }
                    else if (phase == 133)
                    {
                        Check(testPointer == stationaryPointer, "Stationary preview test moved its pointer.");
                        if (!blockedPreview.IsOpen)
                        {
                            Check((DateTime.UtcNow-lastFallEndedAt).TotalMilliseconds < 1200, "Preview did not restore for an unmoved pointer.");
                            return;
                        }
                        Check(shelf.HoverPreviewAllowed && ToolTipService.GetIsEnabled(Card(shelf, hoverSurvivor)) &&
                            (DateTime.UtcNow-lastFallEndedAt).TotalMilliseconds >= 280, "Preview skipped the fresh normal hover delay after the last fall.");
                        report.Add("PASS: existing/queued previews close before deletion; reflowed screenshots and memos stay disabled through overlapping falls; stationary pointer previews only after last real fall ends plus normal hover delay");
                        blockedPreview.IsOpen = false;
                        shelf.RemoveShot(hoverSurvivor);
                        testPointer = new Point(-10000, -10000); phase = 134;
                    }
                    else if (phase == 134 && shelf.FlyingCount == 0)
                    {
                        Check(shelf.HoverPreviewAllowed, "Leaving the shelf during removal permanently disabled previews.");
                        var memoCard = Descendants((DependencyObject)shelf.Content).OfType<Grid>().First(x => AutomationProperties.GetAutomationId(x) == "NoteCard-" + hoverMemo.Id);
                        Check(ToolTipService.GetIsEnabled(memoCard) && !((ToolTip)memoCard.ToolTip).IsOpen, "Pointer outside caused a stray preview or left memo disabled.");
                        store.RemoveNote(hoverMemo);
                        store.Add(Program.Demo("SLIDE UP", "晾台向上收回屏幕。", "#315B75"), "收起测试");
                        shelf.ToggleFromTray(); motionAt = DateTime.UtcNow; phase = 14;
                    }
                    else if (phase == 14)
                    {
                        Check(shelf.Collapsing && shelf.Expanded && shelf.ActualHeight > 200 && shelf.ShelfOffset < -1, "Shelf disappeared immediately instead of sliding upward.");
                        Snapshot((FrameworkElement)shelf.Content, Path.Combine(root, "shelf-closing-preview.png")); phase = 15;
                    }
                    else if (phase == 15 && (DateTime.UtcNow-motionAt).TotalMilliseconds > 400)
                    {
                        Check(!shelf.Collapsing && !shelf.Expanded && shelf.ActualHeight <= 8 && shelf.ShelfOffset == 0, "Collapse did not restore blue-line window.");
                        shelf.Reveal(); motionAt = DateTime.UtcNow; phase = 16;
                        Check(shelf.Opening && shelf.ShelfOffset <= Shelf.HiddenShelfOffset + 1 && ((Grid)shelf.Content).Children.OfType<Canvas>().First().Opacity == 1,
                            "Opening did not start fully above screen at full opacity.");
                    }
                    else if (phase == 16)
                    {
                        Check(shelf.Opening && shelf.ShelfOffset > Shelf.HiddenShelfOffset && shelf.ShelfOffset < -5 && shelf.ActualHeight > 200,
                            "Shelf flashed into place instead of descending through intermediate positions.");
                        Snapshot((FrameworkElement)shelf.Content, Path.Combine(root, "shelf-opening-preview.png")); phase = 160;
                    }
                    else if (phase == 160 && (DateTime.UtcNow-motionAt).TotalMilliseconds > 400)
                    {
                        Check(!shelf.Opening && Math.Abs(shelf.ShelfOffset) < .1, "Opening did not settle at its final position.");
                        report.Add("PASS: full-opacity opening starts fully hidden above screen, passes intermediate downward positions and settles after 300ms");
                        shelf.ToggleFromTray(); phase = 17;
                    }
                    else if (phase == 17)
                    {
                        Check(shelf.Collapsing && shelf.ShelfOffset < 0, "Reversal test did not start closing.");
                        shelf.Reveal(); motionAt = DateTime.UtcNow; phase = 18;
                    }
                    else if (phase == 18 && (DateTime.UtcNow-motionAt).TotalMilliseconds > 400)
                    {
                        Check(!shelf.Collapsing && shelf.Expanded && shelf.ActualHeight > 200 && Math.Abs(shelf.ShelfOffset) < .1, "Old completion hid reopened shelf.");
                        shelf.CaptureLauncher = delegate(Action<BitmapSource> complete) { complete(null); };
                        shelf.RemoveShot(store.Items[0]);
                        Check(shelf.FlyingCount == 1, "Active-fall capture test did not start falling.");
                        shelf.StartCapture();
                        Native.Rect bounds; Native.GetWindowRect(new WindowInteropHelper(shelf).Handle, out bounds);
                        Check(shelf.FlyingCount == 0 && !shelf.Collapsing && bounds.Bottom-bounds.Top <= 12 && shelf.ShelfOffset == 0, "Capture left slide/falling paper visible.");
                        report.Add("PASS: shelf slides upward then restores blue line; reopening cancels stale completion; capture hides instantly");
                        phase = 19; motionAt = DateTime.UtcNow;
                    }
                    else if (phase == 19 && (DateTime.UtcNow-motionAt).TotalMilliseconds > 500)
                    {
                        shelf.Reveal(); motionAt = DateTime.UtcNow; phase = 20;
                    }
                    else if (phase == 20 && (DateTime.UtcNow-motionAt).TotalMilliseconds > 400)
                    {
                        var closingShot = store.Add(Program.Demo("EXIT DURING FALL", "掉落期间正常退出。", "#315B75"), "退出测试");
                        shelf.RemoveShot(closingShot);
                        Check(shelf.FlyingCount > 0, "Shutdown fixture did not start a fall.");
                        exit = 0; timer.Stop(); shelf.Close();
                        Check(shelf.FlyingCount == 0, "Application exit retained a falling window.");
                        report.Add("PASS: application exit during an active fall releases preview holds without accessing a disconnected window");
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
            finally { if (lockFile != null) lockFile.Dispose(); File.WriteAllLines(Path.Combine(root, "interaction-results.txt"), report, Encoding.UTF8); }
            return exit;
        }
    }
}
