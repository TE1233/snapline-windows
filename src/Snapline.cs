// Snapline: a local screenshot shelf for Windows. MIT License.
// Independent Windows implementation inspired by Tendedero's interaction.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Path = System.IO.Path;

[assembly: System.Reflection.AssemblyTitle("Snapline - 截图晾台")]
[assembly: System.Reflection.AssemblyDescription("Private, local screenshot shelf for Windows")]
[assembly: System.Reflection.AssemblyVersion("0.5.2.0")]

namespace Snapline
{
    static class Native
    {
        internal const int Hotkey = 0x0312, MouseActivate = 0x0021;
        internal static readonly uint ShowMessage = RegisterWindowMessage("Snapline.Windows.Show.v1");


        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder name, int count);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rect r, int size);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        internal static bool CaptureKeysReleased(uint triggerKey)
        {
            return new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0) &&
                (triggerKey == 0 || (GetAsyncKeyState((int)triggerKey) & 0x8000) == 0);
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    }

    [DataContract] sealed class Shot
    {
        [DataMember] public string File;
        [DataMember] public string Digest;
        [DataMember] public string Label;
        [DataMember] public DateTime Created;
        internal BitmapSource Thumb;
    }

    [DataContract] sealed class Preferences
    {
        [DataMember] public bool ClipboardImages = false;
        [DataMember] public bool ScreenshotFolder = false;
        [DataMember] public List<string> Folders = new List<string>();
        [DataMember] public bool WelcomeShown;
        [DataMember] public string CaptureShortcut = "CtrlB";
        [DataMember] public int CaptureShortcutRevision = 1;
        [DataMember] public uint CustomCaptureModifiers;
        [DataMember] public uint CustomCaptureKey;
    }

    sealed partial class Store
    {
        internal readonly string Root, Inbox;
        internal List<Shot> Items = new List<Shot>();
        internal Preferences Settings = new Preferences();
        internal const int MaxItems = 60;
        internal Action Changed;
        internal Store(string root)
        {
            Root = Path.GetFullPath(root);
            Inbox = Path.Combine(Root, "Inbox");
            Directory.CreateDirectory(Inbox);
            Settings = Read<Preferences>(Path.Combine(Root, "settings.json")) ?? new Preferences();
            if (Settings.Folders == null) Settings.Folders = new List<string>();
            Settings.ClipboardImages = false; Settings.ScreenshotFolder = false; // Legacy settings cannot enable other sources.
            // Apply the new default once. Afterwards, preserve explicit alternate choices.
            if (Settings.CaptureShortcutRevision < 1)
            {
                if (Settings.CaptureShortcut == "CtrlPrintScreen") Settings.CaptureShortcut = "CtrlB";
                Settings.CaptureShortcutRevision = 1;
            }
            if (Settings.CaptureShortcut != "CtrlB" && Settings.CaptureShortcut != "CtrlPrintScreen" && Settings.CaptureShortcut != "ShiftPrintScreen" && Settings.CaptureShortcut != "None" && Settings.CaptureShortcut != "Custom")
                Settings.CaptureShortcut = "CtrlB";
            if (Settings.CaptureShortcut == "Custom" && ShortcutSpec.Validate(Settings.CustomCaptureModifiers, Settings.CustomCaptureKey) != null)
                Settings.CaptureShortcut = "CtrlB";
            RestoreNotes();
            var saved = Read<List<Shot>>(Path.Combine(Root, "items.json")) ?? new List<Shot>();
            foreach (var shot in saved.Take(MaxItems))
            {
                try
                {
                    if (IsSafe(shot.File) && System.IO.File.Exists(FullPath(shot)))
                    {
                        // Recompute after an external editor changes the cached file.
                        var image = Load(FullPath(shot));
                        shot.Digest = Hash(image);
                        shot.Thumb = Thumbnail(image);
                        Items.Add(shot);
                    }
                }
                catch (Exception ex) { Log("Restore image", ex); }
            }
        }
        static T Read<T>(string file) where T : class
        {
            if (!System.IO.File.Exists(file)) return null;
            try { using (var s = System.IO.File.OpenRead(file)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(s); }
            catch { return null; }
        }
        static void Write<T>(string file, T value)
        {
            string temp = file + ".tmp";
            using (var s = System.IO.File.Create(temp)) new DataContractJsonSerializer(typeof(T)).WriteObject(s, value);
            if (System.IO.File.Exists(file)) System.IO.File.Replace(temp, file, null);
            else System.IO.File.Move(temp, file);
        }
        internal void SaveSettings() { Write(Path.Combine(Root, "settings.json"), Settings); }
        internal void Save() { Write(Path.Combine(Root, "items.json"), Items); }
        internal void Log(string context, Exception error)
        {
            try { System.IO.File.AppendAllText(Path.Combine(Root, "error.log"), DateTime.Now.ToString("s") + " " + context + ": " + error.Message + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
        internal bool IsSafe(string file)
        {
            return !String.IsNullOrWhiteSpace(file) && file == Path.GetFileName(file) &&
                file.StartsWith("snap-", StringComparison.Ordinal) && file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
        internal string FullPath(Shot shot)
        {
            if (!IsSafe(shot.File)) throw new InvalidOperationException("Invalid cache filename");
            return Path.Combine(Inbox, shot.File);
        }
        internal static BitmapSource Load(string file)
        {
            if (new FileInfo(file).Length > 64L * 1024 * 1024) throw new InvalidOperationException("图片超过 64 MB。");
            using (var stream = System.IO.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                CheckSize(frame);
                frame.Freeze();
                return frame;
            }
        }
        internal static BitmapSource Preview(string file, int maxWidth, int maxHeight, double aspect)
        {
            if (new FileInfo(file).Length > 64L * 1024 * 1024) throw new InvalidOperationException("图片超过 64 MB。");
            using (var stream = System.IO.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                if (aspect >= maxWidth / (double)maxHeight) image.DecodePixelWidth = Math.Max(1, maxWidth);
                else image.DecodePixelHeight = Math.Max(1, maxHeight);
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
        static void CheckSize(BitmapSource image)
        {
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0 || (long)image.PixelWidth * image.PixelHeight > 50000000)
                throw new InvalidOperationException("图片尺寸过大（最多 5000 万像素）。");
        }
        internal static string Hash(BitmapSource image)
        {
            CheckSize(image);
            var rgba = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int stride = checked(rgba.PixelWidth * 4);
            byte[] bytes = new byte[checked(stride * rgba.PixelHeight)];
            rgba.CopyPixels(bytes, stride, 0);
            using (var sha = SHA256.Create())
            {
                byte[] dimensions = Encoding.ASCII.GetBytes(rgba.PixelWidth + "x" + rgba.PixelHeight + ":");
                sha.TransformBlock(dimensions, 0, dimensions.Length, dimensions, 0);
                sha.TransformFinalBlock(bytes, 0, bytes.Length);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        internal static BitmapSource Thumbnail(BitmapSource image)
        {
            double scale = Math.Min(1, 360.0 / Math.Max(image.PixelWidth, image.PixelHeight));
            var small = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            // A standalone thumbnail does not retain every full-resolution source in memory.
            var thumbnail = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            thumbnail.Freeze();
            return thumbnail;
        }
        internal Shot Add(BitmapSource image, string label)
        {
            string hash = Hash(image);
            if (Items.Any(x => x.Digest == hash)) return null;
            string filename = "snap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png";
            string full = Path.Combine(Inbox, filename);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = System.IO.File.Create(full)) encoder.Save(stream);
            var shot = new Shot { File = filename, Digest = hash, Label = label, Created = DateTime.Now, Thumb = Thumbnail(image) };
            Items.Insert(0, shot);
            // Commit first; do not delete an older cache image until the new list is durable.
            var overflow = Items.Skip(MaxItems).ToList();
            Items = Items.Take(MaxItems).ToList();
            Save();
            foreach (var old in overflow) DeleteCache(old);
            if (Changed != null) Changed();
            return shot;
        }
        internal Shot Import(string file) { return Add(Load(file), Path.GetFileName(file)); }
        void DeleteCache(Shot item)
        {
            try { if (IsSafe(item.File)) System.IO.File.Delete(FullPath(item)); }
            catch (Exception ex) { Log("Delete cached image", ex); }
        }
        internal void Remove(Shot item)
        {
            Items.Remove(item);
            Save();
            DeleteCache(item);
            if (Changed != null) Changed();
        }
        internal void Refresh(Shot item)
        {
            if (!Items.Contains(item)) return;
            string file = FullPath(item);
            if (!System.IO.File.Exists(file)) { Remove(item); return; }
            var image = Load(file);
            item.Digest = Hash(image);
            item.Thumb = Thumbnail(image);
            Save();
            if (Changed != null) Changed();
        }
        internal void Clear()
        {
            var removed = Items.ToList();
            Items.Clear();
            Save();
            foreach (var item in removed) DeleteCache(item);
            if (Changed != null) Changed();
        }
    }

    sealed partial class Shelf : Window
    {
        readonly Store store;
        readonly bool noCapture, demo;
        readonly Canvas canvas = new Canvas();
        readonly DispatcherTimer tick = new DispatcherTimer();

        readonly Dictionary<string, DateTime> pending = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);


        Forms.NotifyIcon tray;
        Forms.ToolStripMenuItem captureMenu, pinMenu;
        readonly Dictionary<string, Forms.ToolStripMenuItem> shortcutChoices = new Dictionary<string, Forms.ToolStripMenuItem>();
        readonly DispatcherTimer captureDelay = new DispatcherTimer();
        ToolTip helpTip, imageTip;
        bool helpReading, capturePending;
        DateTime captureRequested;
        int captureSerial;
        bool collectCapture;
        internal Action<Action<BitmapSource>> CaptureLauncher = delegate(Action<BitmapSource> complete) { complete(RegionCapture.Select()); };
        FileSystemWatcher inboxWatcher;
        IntPtr handle;
        HwndSource source;
        Forms.Screen screen;
        bool expanded, pinned, dragging, dialogOpen, shuttingDown;
        DateTime keepUntil, hideUntil, lastInside;
        readonly TopHoverGate hoverGate = new TopHoverGate();
        internal Func<IntPtr> ForegroundWindow = Native.GetForegroundWindow;

        int page;
        double shelfWidth;
        string toast = "";
        DateTime toastUntil;
        internal bool ClipboardRegistered, HotkeyRegistered, CaptureHotkeyRegistered;
        internal string CaptureShortcutLabel { get { var shortcut = ShortcutSpec.From(store.Settings); return shortcut == null ? "快捷键已关闭" : shortcut.Label; } }
        internal Shelf(Store data, bool noCapture, bool demo)
        {
            store = data;
            this.noCapture = noCapture;
            this.demo = demo;
            Title = "截图晾台 Snapline";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Height = 4; Width = 1000;
            Content = canvas;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            int knownCount = store.Items.Count + store.Notes.Count;
            store.Changed = delegate { int count = store.Items.Count + store.Notes.Count; if (count > knownCount) page = 0; knownCount = count; Render(); };
            canvas.MouseWheel += delegate(object sender, MouseWheelEventArgs e) { if (expanded) { ChangePage(e.Delta < 0 ? 1 : -1); e.Handled = true; } };
            SourceInitialized += Initialize;
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (memoEditor != null && !memoEditor.FlushChanges()) e.Cancel = true; };
            Closed += Cleanup;
            AllowDrop = true;
            DragOver += delegate(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Drop += delegate(object sender, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                foreach (string file in (string[])e.Data.GetData(DataFormats.FileDrop)) Import(file);
                e.Handled = true;
            };
        }
        static SolidColorBrush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
        internal void Initialize(object sender, EventArgs e)
        {
            handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle);
            source.AddHook(Message);
            Native.SetWindowLong(handle, -20, Native.GetWindowLong(handle, -20) | 0x08000000 | 0x00000080);

            ClipboardRegistered = false; // Only direct capture results are collected.
            HotkeyRegistered = Native.RegisterHotKey(handle, 1, 0x4003, 0x54); // Ctrl+Alt+T, no repeat.
            RegisterCaptureShortcut();
            screen = ScreenUnderPointer();
            MakeTray();
            store.SaveSettings(); // Persist disabled legacy collection preferences on upgrade.
            captureDelay.Interval = TimeSpan.FromMilliseconds(80);
            captureDelay.Tick += CaptureWhenReleased;

            Position(false, screen);
            SetupWatchers();

            tick.Interval = TimeSpan.FromMilliseconds(80);
            tick.Tick += Tick;
            tick.Start();
            if (demo) { pinned = true; Reveal(); }
            else
            {
                Reveal();
                keepUntil = DateTime.Now.AddSeconds(7);
                if (!store.Settings.WelcomeShown)
                {
                    tray.ShowBalloonTip(4500, "截图晾台已启动", CaptureShortcutLabel + " 框选截图。图片右侧「操作提示」卡片可查看说明。", Forms.ToolTipIcon.Info);
                    store.Settings.WelcomeShown = true;
                    store.SaveSettings();
                }
            }

            if (store.Settings.CaptureShortcut != "None" && !CaptureHotkeyRegistered) Notify(CaptureShortcutLabel + " 已被占用，可点击剪刀截图或在托盘更换快捷键。");
            if (!HotkeyRegistered) Notify("Ctrl + Alt + T 已被其他程序占用，可用顶部悬停或托盘展开。");
        }
        IntPtr Message(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.MouseActivate) { handled = true; return new IntPtr(3); }

            if (msg == Native.Hotkey)
            {
                if (w.ToInt32() == 1) { Toggle(); handled = true; }
                else if (w.ToInt32() == captureHotkeyId && CaptureHotkeyRegistered)
                {
                    if (!editingShortcut) StartCapture(ShortcutCollects, ShortcutSpec.From(store.Settings).Key);
                    handled = true;
                }
            }
            if ((uint)msg == Native.ShowMessage) { ToggleFromTray(); handled = true; }
            return IntPtr.Zero;
        }

        static Forms.Screen ScreenUnderPointer()
        {
            Native.Point p; Native.GetCursorPos(out p);
            return Forms.Screen.FromPoint(new Drawing.Point(p.X, p.Y));
        }
        double Dpi()
        {
            try { uint dpi = Native.GetDpiForWindow(handle); return dpi == 0 ? 1 : dpi / 96.0; }
            catch (EntryPointNotFoundException) { return source.CompositionTarget.TransformToDevice.M11; }
        }
        void Position(bool open, Forms.Screen target)
        {
            bool changed = screen == null || screen.DeviceName != target.DeviceName;
            screen = target;
            var area = screen.WorkingArea;
            double scale = Dpi();
            int height = (int)Math.Ceiling((open ? 270 : 4) * scale);
            Native.SetWindowPos(handle, new IntPtr(-1), area.Left, area.Top, area.Width, height, 0x0010);
            // A monitor move can change the WPF presentation source's DPI.
            scale = Dpi();
            int updated = (int)Math.Ceiling((open ? 270 : 4) * scale);
            if (height != updated) Native.SetWindowPos(handle, new IntPtr(-1), area.Left, area.Top, area.Width, updated, 0x0010);
            shelfWidth = area.Width / scale;
            canvas.Width = shelfWidth;
            canvas.Height = open ? 270 : 4;
            if (changed) page = 0;
        }
        bool Fullscreen(Forms.Screen target = null)
        {
            IntPtr foreground = ForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == handle || dialogOpen || dragging) return false;
            return HoverPolicy.Suppresses(foreground, (target ?? screen).Bounds);
        }
        void Tick(object sender, EventArgs e)
        {
            if (shuttingDown) return;
            ProcessPending();
            if (dialogOpen || dragging || helpReading || capturePending) { hoverGate.Reset(); return; }
            Native.Point p; if (!Native.GetCursorPos(out p)) { hoverGate.Reset(); return; }
            Forms.Screen under = Forms.Screen.FromPoint(new Drawing.Point(p.X, p.Y));
            var area = under.WorkingArea;
            DateTime now = DateTime.Now;
            if (!demo && Fullscreen(under))
            {
                hoverGate.Reset();
                if (expanded && screen.DeviceName == under.DeviceName) Collapse(false);
                return;
            }
            if ((imageTip != null && imageTip.IsOpen) || (noteTip != null && noteTip.IsOpen)) { lastInside = now; return; }
            if (now < hideUntil) hoverGate.Reset();
            else if (hoverGate.Observe(under.DeviceName, area, p.X, p.Y, screen.DeviceName == under.DeviceName ? Dpi() : 1, now) &&
                (!expanded || screen.DeviceName != under.DeviceName))
            {
                if (screen.DeviceName != under.DeviceName) { Position(expanded, under); Render(); }
                Reveal();
            }
            bool inside = expanded && p.X >= screen.WorkingArea.Left && p.X < screen.WorkingArea.Right &&
                p.Y >= screen.WorkingArea.Top && p.Y < screen.WorkingArea.Top + 266 * Dpi();
            if (inside) lastInside = now;
            if (expanded && !pinned && now > keepUntil && (now - lastInside).TotalMilliseconds > 700) Collapse(false);
            if (toast != "" && now > toastUntil) { toast = ""; Render(); }
        }
        internal void Reveal()
        {
            bool was = expanded;
            var target = ScreenUnderPointer();
            bool moved = screen == null || target.DeviceName != screen.DeviceName;
            expanded = true;
            if (!was || moved) Position(true, target);
            if (Fullscreen() && !demo) { Collapse(false); return; }
            lastInside = DateTime.Now;
            if (!was || moved) Render();
            if (!was)
            {
                canvas.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                var slide = new TranslateTransform();
                canvas.RenderTransform = slide;
                slide.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        void Collapse(bool manual)
        {
            expanded = false;
            if (manual) { pinned = false; hideUntil = DateTime.Now.AddSeconds(1); }
            hoverGate.Reset();
            Position(false, screen);
            canvas.BeginAnimation(UIElement.OpacityProperty, null);
            canvas.Opacity = 1;
            canvas.RenderTransform = Transform.Identity;
            Render();
        }
        void Toggle()
        {
            if (expanded) Collapse(true);
            else { pinned = true; Reveal(); }
        }
        internal void ToggleFromTray()
        {
            if (expanded) Collapse(true);
            else
            {
                pinned = false;
                Reveal();
                // Allow time to move from the taskbar to the shelf before auto-hiding.
                keepUntil = DateTime.Now.AddSeconds(3);
            }
        }
        internal void TogglePin()
        {
            pinned = !pinned;
            pinMenu.Checked = pinned;
            Reveal();
            if (!pinned) keepUntil = DateTime.Now.AddSeconds(3);
        }
        void Notify(string message)
        {
            toast = message.Length > 80 ? message.Substring(0, 80) : message;
            toastUntil = DateTime.Now.AddSeconds(5);
            keepUntil = toastUntil;
            Reveal(); Render();
        }
        internal void RegisterCaptureShortcut()
        {
            Native.UnregisterHotKey(handle, captureHotkeyId);
            captureHotkeyId = 2;
            var shortcut = ShortcutSpec.From(store.Settings);
            CaptureHotkeyRegistered = shortcut != null && Native.RegisterHotKey(handle, captureHotkeyId, shortcut.Modifiers | 0x4000u, shortcut.Key);
        }
        void SelectCaptureShortcut(string shortcut)
        {
            string error;
            if (!TrySelectCaptureShortcut(shortcut, 0, 0, out error)) Notify(error);
        }
        void UpdateShortcutMenu()
        {
            if (captureMenu != null) captureMenu.Text = "框选截图（仅复制）";
            foreach (var choice in shortcutChoices)
            {
                choice.Value.Checked = choice.Key == store.Settings.CaptureShortcut;
                if (choice.Key == "Custom")
                    choice.Value.Text = store.Settings.CaptureShortcut == "Custom" ? "自定义…（" + CaptureShortcutLabel + "）" : "自定义…";
            }
        }
        internal void StartCapture() { StartCapture(false); }
        internal void StartCapture(bool collect, uint triggerKey = 0)
        {
            if (capturePending || shuttingDown) return;
            CloseImagePreview(); CloseNotePreview();
            if (helpTip != null) helpTip.IsOpen = false;
            pinned = false; Collapse(true);
            canvas.Visibility = Visibility.Hidden;
            capturePending = true; captureSerial++;
            collectCapture = collect; captureTriggerKey = triggerKey;
            captureRequested = DateTime.Now;
            hideUntil = DateTime.Now.AddSeconds(5);
            captureDelay.Start();
        }
        void CaptureWhenReleased(object sender, EventArgs e)
        {
            if (!Native.CaptureKeysReleased(captureTriggerKey))
            {
                if ((DateTime.Now - captureRequested).TotalSeconds < 5) return;
                captureDelay.Stop(); CompleteCapture(captureSerial, null, false);
                Notify("请松开快捷键后重试截图。"); return;
            }
            if ((DateTime.Now - captureRequested).TotalMilliseconds < 240) return;
            captureDelay.Stop();
            int session = captureSerial;
            bool collect = collectCapture;
            try { CaptureLauncher(delegate(BitmapSource result) { CompleteCapture(session, result, collect); }); }
            catch (Exception ex)
            {
                CompleteCapture(session, null, false);
                store.Log("Capture selection", ex); Notify(ex.Message);
            }
        }
        void CompleteCapture(int session, BitmapSource image, bool collect)
        {
            if (shuttingDown || !capturePending || session != captureSerial) return;
            capturePending = false; canvas.Visibility = Visibility.Visible;
            // Cancellation never reads, clears or replaces the clipboard.
            if (image == null) return;
            bool copied = true;
            try { var data = new DataObject(); data.SetImage(image); SetClipboard(data); }
            catch (Exception ex) { copied = false; store.Log("Copy captured image", ex); }
            if (collect && !noCapture)
            {
                store.Add(image, "晾台截图");
                Notify(copied ? "截图已挂上晾台，并复制到剪贴板。" : "截图已挂上晾台；剪贴板暂时被占用，可单击图片重试复制。");
            }
            else Notify(copied ? (ShortcutCollects ? "截图已复制。自动挂上晾台请使用 " + CaptureShortcutLabel + "。" :
                "截图已复制。自动挂上请在托盘选择 Ctrl+B 或自定义快捷键。") : "剪贴板被占用，截图未复制，请重试。");
        }
        ToolTip HelpPopup()
        {
            var content = new StackPanel { Width = 278 };
            var title = Text("截图晾台 · 操作提示", 13, "#FF27668F");
            title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, 10);
            content.Children.Add(title);
            AddHelpRow(content, CaptureShortcutLabel, ShortcutCollects ? "框选截图，完成后自动挂上" : "在托盘设置截图快捷键");
            if (!CaptureHotkeyRegistered && store.Settings.CaptureShortcut != "None")
                content.Children.Add(new TextBlock { Text = CaptureShortcutLabel + " 已被占用，可在托盘更换", FontSize = 10, Foreground = Brush("#FF9C6A3B"), Margin = new Thickness(0, 0, 0, 7) });
            AddHelpRow(content, "卡片截图按钮", "只复制，不自动挂上晾台");
            AddHelpRow(content, "鼠标移到顶部", "临时展开晾台");
            AddHelpRow(content, "Ctrl + Alt + T", "固定展开 / 收起");
            AddHelpRow(content, "＋ 便签", "随手记录，自动保存到本机");
            AddHelpRow(content, "双击便签", "编辑内容与纸色，右键复制或删除");
            AddHelpRow(content, "悬停图片", "放大预览，移开自动收起");
            AddHelpRow(content, "单击 / 双击", "复制图片 / 打开查看");
            AddHelpRow(content, "拖出 / 长按", "拖到文件夹 / 用画图编辑");
            AddHelpRow(content, "右键图片", "另存为、移除等更多操作");
            var footer = Text("仅晾台快捷键截图会自动加入；托盘可自定义", 10, "#FF81909D");
            footer.Margin = new Thickness(0, 7, 0, 0); content.Children.Add(footer);
            if (store.Settings.CaptureShortcut == "CtrlB")
            {
                var shortcutNote = Text("Ctrl + B 会占用加粗键；可在托盘关闭或换键", 10, "#FF9C6A3B");
                shortcutNote.Margin = new Thickness(0, 5, 0, 0); content.Children.Add(shortcutNote);
            }
            var body = new Border { Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(10),
                Background = Brush("#FFF9FCFF"), BorderBrush = Brush("#FFB9E2FA"), BorderThickness = new Thickness(1) };
            var tip = new ToolTip { Content = body, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), HasDropShadow = true, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                HorizontalOffset = -67, VerticalOffset = 7, FontFamily = FontFamily };
            tip.Opened += delegate { helpReading = true; };
            tip.Closed += delegate { helpReading = false; lastInside = DateTime.Now; };
            return tip;
        }
        void AddHelpRow(StackPanel content, string key, string action)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = Text(key, 10.5, "#FF347DA7"); label.TextWrapping = TextWrapping.Wrap; label.Margin = new Thickness(0, 0, 8, 0);
            var detail = new TextBlock { Text = action, FontSize = 10.5, Foreground = Brush("#FF596775"), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(detail, 1); row.Children.Add(label); row.Children.Add(detail); content.Children.Add(row);
        }
        void MakeTray()
        {
            tray = new Forms.NotifyIcon();
            try { tray.Icon = Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); }
            catch { tray.Icon = Drawing.SystemIcons.Application; }
            tray.Text = "截图晾台 Snapline";
            var menu = new Forms.ContextMenuStrip();
            captureMenu = new Forms.ToolStripMenuItem("框选截图（仅复制）", null, delegate { StartCapture(); });
            menu.Items.Add(captureMenu);
            var shortcutMenu = new Forms.ToolStripMenuItem("截图快捷键");
            string[] modes = { "CtrlB", "CtrlPrintScreen", "ShiftPrintScreen", "Custom", "None" };
            string[] labels = { "Ctrl + B（默认）", "Ctrl + PrtSc（仅复制）", "Shift + PrtSc（仅复制）", "自定义…", "关闭快捷键" };
            for (int i = 0; i < modes.Length; i++)
            {
                string selected = modes[i];
                var choice = new Forms.ToolStripMenuItem(labels[i], null, delegate { if (selected == "Custom") EditCaptureShortcut(); else SelectCaptureShortcut(selected); });
                shortcutChoices.Add(selected, choice); shortcutMenu.DropDownItems.Add(choice);
            }
            menu.Items.Add(shortcutMenu);
            UpdateShortcutMenu();
            menu.Items.Add("展开 / 收起（自动收起）", null, delegate { ToggleFromTray(); });
            menu.Items.Add("新增便签", null, delegate { NewNote(); });
            menu.Items.Add("导入图片…", null, delegate { ChooseFiles(); });
            pinMenu = new Forms.ToolStripMenuItem("固定显示", null, delegate { TogglePin(); });
            menu.Items.Add(pinMenu);
            menu.Opening += delegate { pinMenu.Checked = pinned; };
            menu.Items.Add("上一组", null, delegate { ChangePage(-1); Reveal(); });
            menu.Items.Add("下一组", null, delegate { ChangePage(1); Reveal(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem("只自动收集晾台快捷键截图") { Enabled = false });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("打开暂存文件夹", null, delegate { Launch(store.Inbox); });
            menu.Items.Add("清空截图…", null, delegate
            {
                dialogOpen = true;
                try
                {
                    if (Forms.MessageBox.Show("清空应用保存的 " + store.Items.Count + " 张副本？原始截图不会删除。", "截图晾台", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question) == Forms.DialogResult.Yes) store.Clear();
                }
                finally { dialogOpen = false; }
            });
            menu.Items.Add("使用说明", null, delegate { About(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ToggleFromTray(); };
            tray.Visible = true;
        }
        void About()
        {
            dialogOpen = true;
            try
            {
                Forms.MessageBox.Show("截图晾台 Snapline 0.5.2\n\n" + CaptureShortcutLabel + "：框选截图（需快捷键可用）。\n图片右侧「操作提示」：悬停查看说明，点击内部按钮仅截图并复制。\nWin + Shift + S 和其他软件截图不会自动加入。\nCtrl + Alt + T：展开 / 收起，展开后保持显示。\n双击托盘图标：临时展开，离开后自动收起。\n托盘「固定显示」：勾选保持显示，取消后恢复自动收起。\n鼠标停在屏幕顶部：临时展开。\n单击图片：复制。双击：查看。\n拖出：复制为文件。按 Shift 拖到文件夹：移动。\n长按图片：用画图编辑。右键：更多操作。\n\n默认 Ctrl+B 或自定义快捷键发起的框选截图会自动加入。微信截图、普通复制图片和外部截图文件夹均不自动收集。\n最多保留 60 张，超过后清理最早的应用副本。\n完全本地运行，不联网、不上传、不修改系统截图设置。", "截图晾台", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
            }
            finally { dialogOpen = false; }
        }
        internal void SetupWatchers()
        {
            if (noCapture || inboxWatcher != null) return;
            // Refresh existing cached images after editing, without importing other files.
            inboxWatcher = new FileSystemWatcher(store.Inbox, "*.png") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            inboxWatcher.Changed += delegate(object s, FileSystemEventArgs e) { Dispatcher.BeginInvoke(new Action(delegate { if (!shuttingDown) pending[e.FullPath] = DateTime.Now; })); };
            inboxWatcher.EnableRaisingEvents = true;
        }
        internal static bool ImageFile(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(ext);
        }

        void ProcessPending()
        {
            foreach (var file in pending.Keys.ToArray())
            {
                if ((DateTime.Now - pending[file]).TotalMilliseconds < 550) continue;
                try
                {
                    if (!System.IO.File.Exists(file)) { pending.Remove(file); continue; }
                    if (Path.GetDirectoryName(file).Equals(store.Inbox, StringComparison.OrdinalIgnoreCase))
                    {
                        var item = store.Items.FirstOrDefault(x => store.FullPath(x).Equals(file, StringComparison.OrdinalIgnoreCase));
                        if (item != null) store.Refresh(item);
                    }
                    pending.Remove(file);
                }
                catch (Exception ex)
                {
                    // Retry partially written files, bounded so corrupt images cannot cause a busy loop.
                    if ((DateTime.Now - pending[file]).TotalSeconds > 10) { pending.Remove(file); store.Log("Import watched image", ex); }
                }
            }
        }
        void Import(string file)
        {
            try { if (!ImageFile(file)) { Notify("支持 PNG、JPEG、BMP、GIF 和 TIFF 图片。"); return; } var added = store.Import(file); Notify(added == null ? "这张图片已经在晾台上。" : "已加入晾台，原文件保留。"); }
            catch (Exception ex) { store.Log("Import image", ex); Notify("无法导入：" + ex.Message); }
        }
        void ChooseFiles()
        {
            dialogOpen = true;
            try
            {
                using (var picker = new Forms.OpenFileDialog { Title = "导入图片", Multiselect = true, Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" })
                    if (picker.ShowDialog() == Forms.DialogResult.OK) foreach (string file in picker.FileNames) Import(file);
            }
            finally { dialogOpen = false; }
        }

        internal void Copy(Shot shot)
        {
            try
            {

                var data = new DataObject();
                var image = Store.Load(store.FullPath(shot));
                data.SetImage(image);

                SetClipboard(data);

                Notify("已复制，可直接 Ctrl + V 粘贴。");
            }
            catch (Exception ex) { store.Log("Copy image", ex); Notify("复制失败，请稍后重试。"); }

        }
        internal void CopyFile(Shot shot)
        {
            try
            {

                SetClipboard(new DataObject(DataFormats.FileDrop, new[] { store.FullPath(shot) }));

                Notify("已复制 PNG 文件，可粘贴到文件夹。");
            }
            catch (Exception ex) { store.Log("Copy file", ex); Notify("复制文件失败，请稍后重试。"); }

        }
        internal static void SetClipboard(DataObject data)
        {
            // Clipboard owners (including chat apps) can hold the lock briefly.
            for (int attempt = 0; ; attempt++)
            {
                try { Clipboard.SetDataObject(data, true); return; }
                catch (COMException) { if (attempt >= 11) throw; Thread.Sleep(70); }
            }
        }
        void Launch(string file)
        {
            try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
            catch (Exception ex) { store.Log("Open file", ex); Notify("无法打开：" + ex.Message); }
        }
        void Edit(Shot shot)
        {
            try { Process.Start(new ProcessStartInfo("mspaint.exe", "\"" + store.FullPath(shot) + "\"") { UseShellExecute = true }); Notify("已在画图中打开，保存后晾台会更新。"); }
            catch (Exception ex) { store.Log("Open editor", ex); Notify("无法打开画图。可右键选择打开图片。"); }
        }
        void SaveAs(Shot shot)
        {
            dialogOpen = true;
            try
            {
                using (var picker = new Forms.SaveFileDialog { Title = "另存截图", FileName = shot.File, Filter = "PNG 图片|*.png", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
                    if (picker.ShowDialog() == Forms.DialogResult.OK)
                    {
                        if (Path.GetFullPath(picker.FileName).Equals(store.FullPath(shot), StringComparison.OrdinalIgnoreCase)) return;
                        System.IO.File.Copy(store.FullPath(shot), picker.FileName, true); Notify("已保存，晾台上的副本保留。");
                    }
            }
            catch (Exception ex) { store.Log("Save image", ex); Notify("保存失败：" + ex.Message); }
            finally { dialogOpen = false; }
        }
        MenuItem Item(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += delegate { action(); };
            return item;
        }
        ContextMenu ShotMenu(Shot shot)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("复制图片", delegate { Copy(shot); }));
            menu.Items.Add(Item("复制 PNG 文件", delegate { CopyFile(shot); }));
            menu.Items.Add(Item("打开图片", delegate { Launch(store.FullPath(shot)); }));
            menu.Items.Add(Item("用画图编辑", delegate { Edit(shot); }));
            menu.Items.Add(Item("另存为…", delegate { SaveAs(shot); }));
            menu.Items.Add(Item("在文件夹中显示", delegate { Process.Start("explorer.exe", "/select,\"" + store.FullPath(shot) + "\""); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("从晾台移除", delegate { store.Remove(shot); }));
            menu.Opened += delegate { CloseImagePreview(); dialogOpen = true; };
            menu.Closed += delegate { dialogOpen = false; lastInside = DateTime.Now; };
            return menu;
        }
        TextBlock Text(string text, double size, string color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center };
        }
        void Place(UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element); }
        Button Tool(string text, string tip, Action click)
        {
            var button = new Button { Content = text, ToolTip = tip, Height = 28, MinWidth = 28, Padding = new Thickness(8, 2, 8, 2), Background = Brush("#CCF3F5F8"), Foreground = Brush("#FF727B89"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12, Focusable = false };
            AutomationProperties.SetName(button, tip);
            button.Click += delegate { click(); };
            return button;
        }
        // Independent cards hang from a thin, gently sagging line. Empty space stays transparent.
        int CardsPerPage()
        {
            return Math.Max(1, (int)((shelfWidth - 112) / 218) - 1); // Reserve one hanging slot for help.
        }
        int VisibleCardCount()
        {
            return Math.Min(CardsPerPage(), Math.Max(0, Deck().Count - page * CardsPerPage()));
        }
        double HangingCenter(int index, int imageCount)
        {
            // The images and help card form a single centered group.
            return (shelfWidth - (imageCount + 1) * 218) / 2 + (index + 0.5) * 218;
        }
        List<System.Windows.Point> RopeNodes()
        {
            var nodes = new List<System.Windows.Point>();
            nodes.Add(new System.Windows.Point(24, 18));
            int imageCount = VisibleCardCount(), hangingCount = imageCount + 1;
            double sag = 42 + 2 * Math.Min(hangingCount, 4);
            for (int i = 0; i < hangingCount; i++)
            {
                double x = HangingCenter(i, imageCount);
                double loadPosition = (i + 1.0) / (hangingCount + 1.0);
                nodes.Add(new System.Windows.Point(x, 18 + sag * Math.Sin(Math.PI * loadPosition)));
            }
            nodes.Add(new System.Windows.Point(shelfWidth - 24, 18));
            return nodes;
        }
        static double StrandSag(System.Windows.Point a, System.Windows.Point b)
        {
            // A little slack between pegs, with the larger dip caused by the hanging photos.
            return Math.Min(8, Math.Max(1.5, (b.X - a.X) / 80));
        }
        double RopeY(double x)
        {
            var nodes = RopeNodes();
            for (int i = 1; i < nodes.Count; i++)
            {
                var a = nodes[i - 1]; var b = nodes[i];
                if (x > b.X && i < nodes.Count - 1) continue;
                double t = Math.Max(0, Math.Min(1, (x - a.X) / Math.Max(1, b.X - a.X)));
                return a.Y + (b.Y - a.Y) * t + 4 * StrandSag(a, b) * t * (1 - t);
            }
            return 18;
        }

        void ChangePage(int step)
        {
            int lastPage = Math.Max(0, (Deck().Count - 1) / CardsPerPage());
            int next = Math.Max(0, Math.Min(lastPage, page + step));
            if (next == page) return;
            page = next; lastInside = DateTime.Now; Render();
        }
        internal void Render()
        {
            CloseImagePreview(); CloseNotePreview();
            if (helpTip != null) helpTip.IsOpen = false;
            canvas.Children.Clear();
            if (!expanded)
            {
                var mark = new Border { Width = 68, Height = 2, CornerRadius = new CornerRadius(1), Background = Brush("#D671C6FA"), ToolTip = "鼠标在蓝线附近稍停即可展开 · 图片旁「操作提示」查看说明" };
                Place(mark, Math.Max(0, (shelfWidth - 68) / 2), 0);
                return;
            }
            page = Math.Min(page, Math.Max(0, (Deck().Count - 1) / CardsPerPage()));
            double left = 24, right = shelfWidth - 24;
            var nodes = RopeNodes();
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                drawing.BeginFigure(nodes[0], false, false);
                for (int i = 1; i < nodes.Count; i++)
                {
                    var a = nodes[i - 1]; var b = nodes[i];
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    double slack = StrandSag(a, b) * 4 / 3;
                    drawing.BezierTo(new System.Windows.Point(a.X + dx / 3, a.Y + dy / 3 + slack),
                        new System.Windows.Point(a.X + dx * 2 / 3, a.Y + dy * 2 / 3 + slack),
                        b, true, false);
                }
            }

            geometry.Freeze();
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geometry, Stroke = Brush("#8FEAF8FF"), StrokeThickness = 3.5,
                IsHitTestVisible = false, Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 1, Opacity = 0.12 }
            });
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geometry, Stroke = Brush("#FF72C7FA"), StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
            Place(new Ellipse { Width = 4, Height = 4, Fill = Brush("#FFA1DDFC"), IsHitTestVisible = false }, left - 2, 16);
            Place(new Ellipse { Width = 4, Height = 4, Fill = Brush("#FFA1DDFC"), IsHitTestVisible = false }, right - 2, 16);
            int perPage = CardsPerPage();
            var deck = Deck();
            int maxPage = Math.Max(0, (deck.Count - 1) / perPage);
            page = Math.Min(page, maxPage);
            var shown = deck.Skip(page * perPage).Take(perPage).ToArray();
            for (int i = 0; i < shown.Length; i++)
            {
                double center = HangingCenter(i, shown.Length);
                var memo = shown[i] as Memo;
                if (memo != null) MakeNoteCard(memo, center, RopeY(center) + 15, i);
                else MakeCard((Shot)shown[i], center, RopeY(center) + 15, i);
            }
            if (maxPage > 0)
            {
                Place(Tool("‹", "上一组（也可滚动鼠标）", delegate { ChangePage(-1); }), 34, 29);
                Place(Tool("›", "下一组（也可滚动鼠标）", delegate { ChangePage(1); }), shelfWidth - 62, 29);
            }
            double helpCenter = HangingCenter(shown.Length, shown.Length);
            MakeHelpCard(helpCenter, RopeY(helpCenter) + 15, shown.Length);
            if (toast != "")
            {
                var note = Text(toast, 11, "#FF525F6C");
                var feedback = new Border
                {
                    Child = note, Padding = new Thickness(12, 6, 12, 6),
                    CornerRadius = new CornerRadius(12), Background = Brush("#EAF7F8FA"),
                    BorderBrush = Brush("#88D3D7DE"), BorderThickness = new Thickness(0.5),
                    IsHitTestVisible = false
                };
                feedback.Measure(new System.Windows.Size(Double.PositiveInfinity, Double.PositiveInfinity));
                Place(feedback, (shelfWidth - feedback.DesiredSize.Width) / 2, 242);
            }
        }

        void MakeHelpCard(double center, double y, int index)
        {
            const double width = 172, height = 148;
            double lean = 1.8;
            var swing = new RotateTransform(lean, width / 2, -15);
            var grid = new Grid { Width = width, Height = height, RenderTransform = swing, Cursor = Cursors.Hand };
            AutomationProperties.SetName(grid, "操作提示（悬停查看）");
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
            var title = Text("操作提示", 16, "#FF307FAF");
            title.FontWeight = FontWeights.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(title);
            var subtitle = Text("鼠标悬停查看说明", 11, "#FF80929F");
            subtitle.HorizontalAlignment = HorizontalAlignment.Center; subtitle.Margin = new Thickness(0, 4, 0, 0);
            content.Children.Add(subtitle);
            var capture = Tool("✂ 截图（仅复制）", "只复制；自动挂上晾台请按 Ctrl + B", StartCapture);
            capture.FontSize = 11; capture.Height = 26; capture.Margin = new Thickness(0, 10, 0, 0);
            capture.Background = Brush("#FFE4F4FF"); capture.Foreground = Brush("#FF3C89B8");
            capture.ToolTip = null; content.Children.Add(capture);
            var addNote = Tool("＋ 便签", "新增便签", NewNote);
            addNote.FontSize = 12; addNote.Height = 27; addNote.Margin = new Thickness(0, 6, 0, 0);
            addNote.Background = Brush("#FFFFF3C9"); addNote.Foreground = Brush("#FF9D8345");
            System.Windows.Automation.AutomationProperties.SetAutomationId(addNote, "NewNote");
            content.Children.Add(addNote);
            grid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), Background = Brush("#F5F6FBFF"),
                BorderBrush = Brush("#E5C3E7FB"), BorderThickness = new Thickness(1),
                Child = content, Effect = new DropShadowEffect { BlurRadius = 10, Opacity = 0.16, ShadowDepth = 3 }
            });
            AddHangingPeg(grid);
            helpTip = HelpPopup();
            helpTip.PlacementTarget = grid;
            grid.ToolTip = helpTip;
            ToolTipService.SetInitialShowDelay(grid, 220);
            ToolTipService.SetShowDuration(grid, 60000);
            var breeze = new DoubleAnimation(lean - 0.6, lean + 0.6, TimeSpan.FromSeconds(3.8))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(breeze, 24);
            swing.BeginAnimation(RotateTransform.AngleProperty, breeze);
            grid.MouseEnter += delegate { swing.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))); };
            grid.MouseLeave += delegate { swing.BeginAnimation(RotateTransform.AngleProperty, breeze); };
            grid.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; helpTip.IsOpen = !helpTip.IsOpen; };
            Place(grid, center - width / 2, y);
        }
        void AddHangingPeg(Grid grid)
        {
            var pin = new Border
            {
                Width = 6, Height = 22, CornerRadius = new CornerRadius(2),
                Background = new LinearGradientBrush(Color.FromRgb(236, 238, 243), Color.FromRgb(170, 175, 190), 0),
                BorderBrush = Brush("#D8FFFFFF"), BorderThickness = new Thickness(0.5),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -20, 0, 0), IsHitTestVisible = false,
                Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 1, Opacity = 0.16 }
            };
            grid.Children.Add(pin);
            grid.Children.Add(new Border
            {
                Width = 1, Height = 14, Background = Brush("#668A909D"),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -16, 0, 0), IsHitTestVisible = false
            });
        }

        void CloseImagePreview()
        {
            var current = imageTip;
            imageTip = null;
            if (current != null) current.IsOpen = false;
        }
        ToolTip ImagePopup(Shot shot, FrameworkElement target)
        {
            double dpi = Dpi();
            double maxWidth = Math.Max(100, Math.Min(760, screen.WorkingArea.Width / dpi - 64));
            double maxHeight = Math.Max(80, Math.Min(460, screen.WorkingArea.Height / dpi - 300));
            double aspect = shot.Thumb.PixelWidth / (double)Math.Max(1, shot.Thumb.PixelHeight);
            double width = Math.Min(maxWidth, maxHeight * aspect), height = width / aspect;
            double bodyWidth = Math.Max(180, width);
            var preview = new Image { Width = width, Height = height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            var content = new StackPanel { Width = bodyWidth };
            var title = Text("放大预览", 12, "#FF387DA6");
            title.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(title);
            content.Children.Add(new Border { Child = preview, Background = Brush("#FFF0F4F8"), CornerRadius = new CornerRadius(4), ClipToBounds = true });
            content.Children.Add(new TextBlock { Text = "单击复制 · 双击查看 · 拖出图片", FontSize = 11,
                Foreground = Brush("#FF82909B"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            var tip = new ToolTip
            {
                Content = new Border { Child = content, Padding = new Thickness(10), CornerRadius = new CornerRadius(9),
                    Background = Brush("#FFF9FCFF"), BorderBrush = Brush("#FFC1E4F8"), BorderThickness = new Thickness(1) },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                HasDropShadow = true, IsHitTestVisible = false, FontFamily = FontFamily,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, PlacementTarget = target,
                HorizontalOffset = (target.Width - bodyWidth - 22) / 2, VerticalOffset = 10
            };
            tip.Opened += delegate
            {
                if (!expanded || capturePending || dragging || dialogOpen || shuttingDown ||
                    Mouse.LeftButton == MouseButtonState.Pressed || !store.Items.Contains(shot)) { tip.IsOpen = false; return; }
                try
                {
                    CloseImagePreview(); CloseNotePreview();
                    if (helpTip != null) helpTip.IsOpen = false;
                    preview.Source = Store.Preview(store.FullPath(shot), (int)Math.Ceiling(maxWidth * dpi), (int)Math.Ceiling(maxHeight * dpi), aspect);
                    imageTip = tip;
                }
                catch (Exception ex) { tip.IsOpen = false; store.Log("Preview image", ex); }
            };
            tip.Closed += delegate
            {
                preview.Source = null; // Release the enlarged bitmap as soon as the pointer leaves.
                if (Object.ReferenceEquals(imageTip, tip)) imageTip = null;
                lastInside = DateTime.Now;
            };
            return tip;
        }
        void MakeCard(Shot shot, double center, double y, int index)
        {
            double imageWidth = Math.Max(1, shot.Thumb.PixelWidth), imageHeight = Math.Max(1, shot.Thumb.PixelHeight);
            double scale = Math.Min(192 / imageWidth, 144 / imageHeight);
            double width = Math.Max(70, imageWidth * scale) + 7;
            double height = Math.Max(54, imageHeight * scale) + 7;
            double lean = new[] { -2.2, 1.4, -1.1, 2.0, -0.6 }[index % 5];
            var swing = new RotateTransform(lean, width / 2, -15);
            var grid = new Grid
            {
                Width = width, Height = height, RenderTransform = swing,
                Cursor = Cursors.Hand
            };
            AutomationProperties.SetName(grid, "悬挂图片 " + shot.Label);
            grid.ToolTip = ImagePopup(shot, grid);
            ToolTipService.SetInitialShowDelay(grid, 300);
            ToolTipService.SetBetweenShowDelay(grid, 100);
            ToolTipService.SetShowDuration(grid, 60000);
            grid.PreviewMouseDown += delegate { CloseImagePreview(); };
            var frame = new Border
            {
                CornerRadius = new CornerRadius(5), Background = Brush("#E9F7F8FB"),
                BorderBrush = Brush("#C9FFFFFF"), BorderThickness = new Thickness(1),
                Padding = new Thickness(2.5),
                Effect = new DropShadowEffect { BlurRadius = 10, Opacity = 0.18, ShadowDepth = 3 }
            };
            frame.Child = new Border
            {
                CornerRadius = new CornerRadius(3), ClipToBounds = true,
                Child = new Image { Source = shot.Thumb, Stretch = Stretch.Uniform }
            };
            grid.Children.Add(frame);
            AddHangingPeg(grid);
            var remove = new Button
            {
                Content = "×", Width = 19, Height = 19, FontSize = 13,
                Background = Brush("#C948505F"), Foreground = Brushes.White, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 3, 0), Focusable = false, ToolTip = "移除应用副本",
                Opacity = 0, IsHitTestVisible = false
            };
            AutomationProperties.SetName(remove, "移除 " + shot.Label);
            remove.PreviewMouseDown += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; store.Remove(shot); };
            grid.Children.Add(remove);
            var breeze = new DoubleAnimation(lean - 0.65, lean + 0.65, TimeSpan.FromSeconds(3.4 + (index % 3) * 0.6))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(breeze, 24);
            swing.BeginAnimation(RotateTransform.AngleProperty, breeze);
            grid.MouseEnter += delegate
            {
                remove.IsHitTestVisible = true;
                remove.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
                swing.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            };
            grid.MouseLeave += delegate
            {
                CloseImagePreview(); CloseNotePreview();
                remove.IsHitTestVisible = false;
                remove.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
                Timeline.SetDesiredFrameRate(breeze, 24);
            swing.BeginAnimation(RotateTransform.AngleProperty, breeze);
            };
            grid.ContextMenu = ShotMenu(shot);
            bool down = false, moved = false, held = false;
            System.Windows.Point start = new System.Windows.Point();
            var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            hold.Tick += delegate { hold.Stop(); if (down && !moved) { held = true; down = false; grid.ReleaseMouseCapture(); Edit(shot); } };
            grid.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (e.ClickCount == 2) { hold.Stop(); down = false; grid.ReleaseMouseCapture(); Launch(store.FullPath(shot)); return; }
                start = e.GetPosition(grid); down = true; moved = held = false; grid.CaptureMouse(); hold.Start();
            };
            grid.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!down || moved || held || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(grid);
                if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                moved = true; down = false; hold.Stop(); grid.ReleaseMouseCapture();
                try
                {
                    dragging = true;
                    var data = new DataObject(DataFormats.FileDrop, new[] { store.FullPath(shot) });
                    var result = DragDrop.DoDragDrop(grid, data, DragDropEffects.Copy | DragDropEffects.Move);
                    if (result == DragDropEffects.Move || !System.IO.File.Exists(store.FullPath(shot))) store.Remove(shot);
                }
                catch (Exception ex) { store.Log("Drag image", ex); Notify("拖放未完成，可用右键另存为。"); }
                finally { dragging = false; lastInside = DateTime.Now; }
            };
            grid.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                hold.Stop(); grid.ReleaseMouseCapture();
                if (down && !moved && !held) Copy(shot);
                down = false; e.Handled = true;
            };
            grid.LostMouseCapture += delegate { hold.Stop(); };
            Place(grid, center - width / 2, y);
        }

        internal void Snapshot(string file)
        {
            canvas.Measure(new System.Windows.Size(shelfWidth, 270));
            canvas.Arrange(new System.Windows.Rect(0, 0, shelfWidth, 270));
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(shelfWidth), 270, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = System.IO.File.Create(file)) encoder.Save(stream);
        }
        void Cleanup(object sender, EventArgs e)
        {
            shuttingDown = true;
            tick.Stop(); captureDelay.Stop();
            CloseImagePreview(); CloseNotePreview();
            if (helpTip != null) helpTip.IsOpen = false;

            if (inboxWatcher != null) inboxWatcher.Dispose();
            Native.UnregisterHotKey(handle, 1); Native.UnregisterHotKey(handle, 2); Native.UnregisterHotKey(handle, 3);
            if (source != null) source.RemoveHook(Message);
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            store.Save(); store.SaveSettings();
        }
    }

    static class Program
    {
        static string Option(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        [STAThread] static int Main(string[] args)
        {
            string root = Option(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snapline");
            try
            {
                if (args.Contains("--self-test")) { SelfTest(root); return 0; }
                if (args.Contains("--integration-test")) return Integration.Run(root);
                if (args.Contains("--memo-test")) return MemoTests.Run(root);
                if (args.Contains("--shortcut-test")) return ShortcutTests.Run(root);
                if (args.Contains("--hover-test")) return HoverTests.Run(root);
                bool fresh;
                using (var mutex = new Mutex(true, "Local\\Snapline-" + BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root)))).Replace("-", "").Substring(0, 20), out fresh))
                {
                    if (!fresh) { Native.PostMessage(new IntPtr(0xffff), Native.ShowMessage, IntPtr.Zero, IntPtr.Zero); return 0; }
                    var store = new Store(root);
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        store.Log("UI error", e.Exception);
                        e.Handled = true;
                    };
                    bool demo = args.Contains("--demo");
                    if (demo && store.Items.Count == 0)
                    {
                        store.Add(Demo("IDEAS", "把灵感挂起来", "#315B75"), "示例 · 灵感");
                        store.Add(Demo("A LITTLE BREAK", "一杯咖啡的时间", "#8C6B4D"), "示例 · 日常");
                        store.Add(Demo("READY TO SHARE", "截图，随手取用", "#426C60"), "示例 · 分享");
                    }
                    if (args.Contains("--demo-notes") && store.Notes.Count == 0)
                    {
                        store.AddNote("周末的小计划\\n去公园走走，带上相机。\\n\\n给自己留一点慢下来的时间。".Replace("\\n", "\n"), "Butter");
                        store.AddNote("今天先做这几件事\\n整理资料、回一封邮件，\\n再给新想法留个位置。".Replace("\\n", "\n"), "Mint");
                        store.AddNote("一个小灵感\\n把想到的事情写下来，\\n让它像照片一样晾着。".Replace("\\n", "\n"), "Lavender");
                    }
                    var shelf = new Shelf(store, args.Contains("--no-capture"), demo);
                    app.MainWindow = shelf;
                    var quit = new DispatcherTimer();
                    string seconds = Option(args, "--quit-after");
                    if (seconds != null) { quit.Interval = TimeSpan.FromSeconds(Double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)); quit.Tick += delegate { quit.Stop(); shelf.Close(); }; quit.Start(); }
                    string snapshot = Option(args, "--snapshot");
                    string diagnostics = Option(args, "--diagnostics");
                    shelf.Loaded += delegate
                    {
                        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        delay.Tick += delegate
                        {
                            delay.Stop();
                            if (snapshot != null) shelf.Snapshot(snapshot);
                            if (diagnostics != null) System.IO.File.WriteAllText(diagnostics, "collectionMode=ConfiguredShortcutOnly" + Environment.NewLine + "captureUi=BuiltinRegion" + Environment.NewLine + "clipboardListener=" + shelf.ClipboardRegistered + Environment.NewLine + "hotkey=" + shelf.HotkeyRegistered + Environment.NewLine + "captureHotkey=" + shelf.CaptureHotkeyRegistered + Environment.NewLine + "captureShortcut=" + shelf.CaptureShortcutLabel + Environment.NewLine + "items=" + store.Items.Count + Environment.NewLine + "notes=" + store.Notes.Count + Environment.NewLine + "windowHandle=" + new WindowInteropHelper(shelf).Handle.ToInt64() + Environment.NewLine);
                        };
                        delay.Start();
                    };
                    shelf.Show();
                    app.Run();
                    mutex.ReleaseMutex();
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(root);
                System.IO.File.WriteAllText(Path.Combine(root, "startup-error.log"), ex.ToString(), Encoding.UTF8);
                Forms.MessageBox.Show("启动失败：" + ex.Message + "\n详情已写入 " + Path.Combine(root, "startup-error.log"), "Snapline");
                return 1;
            }
        }
        internal static BitmapSource Demo(string title, string caption, string color)
        {
            var visual = new DrawingVisual();
            using (var c = visual.RenderOpen())
            {
                var background = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
                c.DrawRectangle(background, null, new System.Windows.Rect(0, 0, 640, 400));
                c.DrawEllipse(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), null, new System.Windows.Point(505, 45), 170, 170);
                c.DrawEllipse(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), null, new System.Windows.Point(60, 400), 170, 170);
                c.DrawText(new FormattedText("S N A P L I N E", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.White, 1.0), new System.Windows.Point(45, 38));
                c.DrawText(new FormattedText(title, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 35, Brushes.White, 1.0), new System.Windows.Point(45, 170));
                c.DrawText(new FormattedText(caption, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 24, Brushes.White, 1.0), new System.Windows.Point(45, 232));
                c.DrawRectangle(new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), null, new System.Windows.Rect(45, 315, 95, 3));
            }
            var bitmap = new RenderTargetBitmap(640, 400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
        }
        static void Assert(bool condition, string message) { if (!condition) throw new Exception("TEST FAILED: " + message); }
        static void SelfTest(string root)
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Self-test requires an empty, isolated data directory.");
            var results = new List<string>();
            var store = new Store(root);
            BitmapSource image = Demo("TEST", "本地图片", "#426C60");
            Shot first = store.Add(image, "test");
            Assert(first != null && System.IO.File.Exists(store.FullPath(first)), "PNG saved");
            Assert(store.Add(image, "duplicate") == null && store.Items.Count == 1, "Duplicate pixels rejected");
            Assert(Store.Hash(image) == Store.Hash(Store.Load(store.FullPath(first))), "PNG round-trip hash");
            var preview = Store.Preview(store.FullPath(first), 120, 90, image.PixelWidth / (double)image.PixelHeight);
            Assert(preview.PixelWidth <= 120 && preview.PixelHeight <= 90 && preview.IsFrozen, "Preview decode is bounded and frozen");
            using (var exclusive = System.IO.File.Open(store.FullPath(first), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert(exclusive.Length > 0, "Preview releases its source file handle");
            Assert(Store.Hash(image) == Store.Hash(Store.Load(store.FullPath(first))), "Preview preserves cache image");
            results.Add("PASS: enlarged preview decoding is bounded, source handle released and cached image unchanged");
            var restored = new Store(root);
            Assert(restored.Items.Count == 1 && restored.Items[0].Thumb != null, "Persistence and thumbnails");
            string external = Path.Combine(root, "external.png");
            System.IO.File.Copy(store.FullPath(first), external);
            store.Remove(first);
            Assert(System.IO.File.Exists(external), "Remove preserves external original");
            Assert(store.Items.Count == 0 && !System.IO.File.Exists(store.FullPath(first)), "Remove clears only cache");
            Shot imported = store.Import(external);
            Assert(imported != null && store.FullPath(imported) != external, "Import makes own copy");
            Assert(!store.IsSafe("../external.png") && !store.IsSafe("external.png") && !store.IsSafe("C:\\x.png"), "Cache path traversal blocked");
            store.Clear();
            Assert(System.IO.File.Exists(external), "Clear preserves source");
            store.Settings.ClipboardImages = false; store.SaveSettings();
            Assert(!new Store(root).Settings.ClipboardImages, "Preference persistence");
            store.Settings.ClipboardImages = true; store.Settings.ScreenshotFolder = true; store.Settings.Folders.Add("C:\\LegacyScreenshots"); store.SaveSettings();
            var restricted = new Store(root);
            Assert(!restricted.Settings.ClipboardImages && !restricted.Settings.ScreenshotFolder && restricted.Settings.Folders.Count == 1, "Legacy broad collection is disabled without deleting folder preferences");
            results.Add("PASS: legacy clipboard and external-folder collection preferences are disabled");
            store.Settings.CaptureShortcut = "ShiftPrintScreen"; store.SaveSettings();
            Assert(new Store(root).Settings.CaptureShortcut == "ShiftPrintScreen", "Shortcut choice persists");
            store.Settings.CaptureShortcut = "None"; store.SaveSettings();
            Assert(new Store(root).Settings.CaptureShortcut == "None", "Disabled shortcut stays disabled");
            System.IO.File.WriteAllText(Path.Combine(root, "settings.json"), "{\"ClipboardImages\":false,\"ScreenshotFolder\":false,\"Folders\":[],\"WelcomeShown\":true}");
            var migrated = new Store(root);
            Assert(migrated.Settings.CaptureShortcut == "CtrlB" && !migrated.Settings.ClipboardImages && !migrated.Settings.ScreenshotFolder && migrated.Settings.WelcomeShown, "Old settings migrate without resetting preferences");
            System.IO.File.WriteAllText(Path.Combine(root, "settings.json"), "{\"CaptureShortcut\":\"CtrlPrintScreen\",\"ClipboardImages\":false,\"ScreenshotFolder\":false,\"Folders\":[],\"WelcomeShown\":true}");
            var upgraded = new Store(root);
            Assert(upgraded.Settings.CaptureShortcut == "CtrlB" && upgraded.Settings.CaptureShortcutRevision == 1 && !upgraded.Settings.ClipboardImages, "Previous default migrates to Ctrl+B once");
            upgraded.Settings.CaptureShortcut = "CtrlPrintScreen"; upgraded.SaveSettings();
            Assert(new Store(root).Settings.CaptureShortcut == "CtrlPrintScreen", "Explicit alternate choice survives future restarts");
            results.Add("PASS: old Ctrl+PrtSc default migrates to Ctrl+B once; explicit alternatives remain selectable");
            results.Add("PASS: shortcut selection and disabled state persist; old settings migrate preserving preferences");
            Assert(Shelf.ImageFile("x.PNG") && !Shelf.ImageFile("x.exe"), "Image extension filter");
            for (int i = 0; i < Store.MaxItems + 2; i++)
                store.Add(Demo("SHOT " + i, "测试", i % 2 == 0 ? "#426C60" : "#315B75"), "item " + i);
            Assert(store.Items.Count == Store.MaxItems, "Retention capped at 60");
            Assert(Directory.GetFiles(store.Inbox, "*.png").Length == Store.MaxItems, "Old cache files pruned");
            Assert(new Store(root).Items.Count == Store.MaxItems, "Retention survives restart");
            results.Add("PASS: PNG save/load, pixel deduplication, persistence, thumbnails");
            results.Add("PASS: imported source preserved during remove and clear");
            results.Add("PASS: path traversal rejected, settings persisted, extension filtering");
            results.Add("PASS: 60-item retention and cache cleanup");
            System.IO.File.WriteAllLines(Path.Combine(root, "self-test-results.txt"), results, Encoding.UTF8);
        }
    }
}
