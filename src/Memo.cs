using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Snapline
{
    [DataContract] sealed class Memo
    {
        [DataMember] public string Id;
        [DataMember] public string Text;
        [DataMember] public string Color;
        [DataMember] public DateTime Created;
        [DataMember] public DateTime Updated;
    }
    sealed partial class Store
    {
        internal readonly List<Memo> Notes = new List<Memo>();
        internal const int MaxNoteText = 20000;
        bool notesUnreadable;
        void RestoreNotes()
        {
            string file = System.IO.Path.Combine(Root, "notes.json");
            if (!File.Exists(file)) return;
            try
            {
                if (new FileInfo(file).Length > 32L * 1024 * 1024) throw new InvalidDataException("便签文件过大。");
                List<Memo> saved;
                using (var stream = File.OpenRead(file)) saved = (List<Memo>)new DataContractJsonSerializer(typeof(List<Memo>)).ReadObject(stream);
                if (saved == null) throw new InvalidDataException("便签文件格式错误。");
                foreach (var memo in saved)
                {
                    if (memo == null) continue;
                    if (memo.Text == null) memo.Text = "";
                    Guid id;
                    if (!Guid.TryParseExact(memo.Id, "N", out id)) memo.Id = Guid.NewGuid().ToString("N");
                    if (Notes.Any(x => x.Id == memo.Id)) continue;
                    memo.Color = MemoTheme.Normalize(memo.Color);
                    if (memo.Created == DateTime.MinValue) memo.Created = DateTime.Now;
                    if (memo.Updated == DateTime.MinValue) memo.Updated = memo.Created;
                    Notes.Add(memo);
                }
            }
            catch (Exception ex) { Notes.Clear(); notesUnreadable = true; Log("Restore notes", ex); }
        }
        void SaveNotes()
        {
            if (notesUnreadable) throw new InvalidOperationException("原便签文件暂时无法读取，已保留原记录。请先恢复 notes.json 后重启。");
            Write(System.IO.Path.Combine(Root, "notes.json"), Notes);
        }
        internal Memo AddNote(string text, string color)
        {
            if (text == null) text = "";
            if (text.Length > MaxNoteText) throw new InvalidOperationException("便签最多支持 20000 个字符。");
            var memo = new Memo { Id = Guid.NewGuid().ToString("N"), Text = text, Color = MemoTheme.Normalize(color), Created = DateTime.Now, Updated = DateTime.Now };
            Notes.Insert(0, memo);
            try { SaveNotes(); } catch { Notes.Remove(memo); throw; }
            if (Changed != null) Changed();
            return memo;
        }
        internal void UpdateNote(Memo memo, string text, string color)
        {
            if (!Notes.Contains(memo)) throw new InvalidOperationException("这张便签已被删除。");
            if (text == null) text = "";
            if (text.Length > MaxNoteText) throw new InvalidOperationException("便签最多支持 20000 个字符。");
            color = MemoTheme.Normalize(color);
            if (memo.Text == text && memo.Color == color) return;
            string oldText = memo.Text, oldColor = memo.Color;
            DateTime oldTime = memo.Updated;
            memo.Text = text; memo.Color = color; memo.Updated = DateTime.Now;
            try { SaveNotes(); }
            catch { memo.Text = oldText; memo.Color = oldColor; memo.Updated = oldTime; throw; }
            if (Changed != null) Changed();
        }
        internal void RemoveNote(Memo memo)
        {
            int index = Notes.IndexOf(memo);
            if (index < 0) return;
            Notes.RemoveAt(index);
            try { SaveNotes(); } catch { Notes.Insert(index, memo); throw; }
            if (Changed != null) Changed();
        }
    }
    static class MemoTheme
    {
        internal static readonly string[] Colors = { "Butter", "Mint", "Lavender" };
        internal static string Normalize(string color) { return Colors.Contains(color) ? color : "Butter"; }
        internal static Color Hex(string value) { return (Color)ColorConverter.ConvertFromString(value); }
        internal static SolidColorBrush Brush(string value) { var b = new SolidColorBrush(Hex(value)); b.Freeze(); return b; }
        internal static string Top(string color) { return color == "Mint" ? "#F0F7E9" : color == "Lavender" ? "#F2EBFA" : "#FFF3BE"; }
        internal static string Bottom(string color) { return color == "Mint" ? "#E5F0D9" : color == "Lavender" ? "#E8DCF5" : "#FFF8D9"; }
        internal static string Accent(string color) { return color == "Mint" ? "#75966B" : color == "Lavender" ? "#9680B2" : "#B69A4D"; }
        internal static string Edge(string color) { return color == "Mint" ? "#CDDDBC" : color == "Lavender" ? "#D8C7EB" : "#E9D799"; }
        internal static Brush Paper(string color) { return new LinearGradientBrush(Hex(Top(color)), Hex(Bottom(color)), 90); }
        internal static Button Button(string text, string name)
        {
            var b = new Button { Content = text, Height = 32, Padding = new Thickness(12, 4, 12, 4), FontSize = 12, Cursor = Cursors.Hand,
                Background = Brush("#18FFFFFF"), Foreground = Brush("#665E48"), BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0) };
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            var frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            frame.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            content.SetBinding(FrameworkElement.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            frame.AppendChild(content); b.Template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
            b.MouseEnter += delegate { b.Opacity = 0.78; }; b.MouseLeave += delegate { b.Opacity = 1; };
            return b;
        }
        internal static TextBlock Text(string text, double size, string color)
        { return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), FontFamily = new FontFamily("Microsoft YaHei UI") }; }
    }

    sealed class MemoEditor : Window
    {
        readonly Store store;
        readonly Memo memo;
        readonly TextBox input;
        readonly TextBlock status, placeholder;
        readonly Border paper;
        readonly DispatcherTimer saveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(480) };
        readonly Dictionary<string, Ellipse> swatches = new Dictionary<string, Ellipse>();
        string color;
        bool dirty;
        internal MemoEditor(Store data, Memo note)
        {
            store = data; memo = note; color = note.Color;
            Title = "编辑便签 · 截图晾台"; Width = 524; Height = 462;
            ShowInTaskbar = false; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            var outer = new Grid { Margin = new Thickness(14) };
            paper = new Border { CornerRadius = new CornerRadius(12), Background = MemoTheme.Paper(color), BorderBrush = MemoTheme.Brush(MemoTheme.Edge(color)), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = MemoTheme.Hex("#354155"), BlurRadius = 22, ShadowDepth = 6, Opacity = 0.20 } };
            outer.Children.Add(paper);
            var content = new Grid { Margin = new Thickness(25, 22, 25, 18) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Children.Add(content);
            var heading = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            var title = new StackPanel();
            title.Children.Add(MemoTheme.Text("随手记", 22, "#514A36"));
            title.Children.Add(new TextBlock { Text = "把想到的事，轻轻挂在这里。", FontSize = 11, Foreground = MemoTheme.Brush("#9A8E6A"), Margin = new Thickness(0, 6, 0, 0) });
            title.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 1) DragMove(); };
            heading.Children.Add(title);
            var close = MemoTheme.Button("×", "收起便签编辑窗口"); close.Width = 30; close.Height = 30; close.FontSize = 20;
            close.Padding = new Thickness(0); close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top;
            close.Click += delegate { Close(); }; heading.Children.Add(close);
            content.Children.Add(heading);
            var palette = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            palette.Children.Add(new TextBlock { Text = "纸色", FontSize = 11, Foreground = MemoTheme.Brush("#93856A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
            for (int i = 0; i < MemoTheme.Colors.Length; i++)
            {
                string mode = MemoTheme.Colors[i];
                string label = new[] { "暖黄", "薄荷绿", "淡紫" }[i];
                var circle = new Ellipse { Width = 21, Height = 21, Fill = MemoTheme.Brush(MemoTheme.Bottom(mode)), StrokeThickness = 2 };
                swatches.Add(mode, circle);
                var choice = MemoTheme.Button("", label + "纸色"); choice.Content = circle; choice.Width = 32; choice.Height = 32; choice.Padding = new Thickness(0); choice.Margin = new Thickness(0, 0, 4, 0); choice.ToolTip = label;
                System.Windows.Automation.AutomationProperties.SetAutomationId(choice, "NoteColor-" + mode);
                choice.Click += delegate { color = mode; UpdatePaper(); MarkDirty(); };
                palette.Children.Add(choice);
            }
            Grid.SetRow(palette, 1); content.Children.Add(palette);
            var writing = new Grid { Margin = new Thickness(0, 0, 0, 15) };
            writing.Children.Add(new Border { Background = MemoTheme.Brush("#16FFFFFF"), CornerRadius = new CornerRadius(6) });
            var rules = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            for (int i = 0; i < 14; i++) rules.Children.Add(new Line { X1 = 11, X2 = 445, Y1 = 34 + 25 * i, Y2 = 34 + 25 * i, Stroke = MemoTheme.Brush("#187F7454"), StrokeThickness = 0.5 });
            writing.Children.Add(rules);
            input = new TextBox { Text = note.Text, MaxLength = Store.MaxNoteText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(11, 10, 10, 10),
                FontSize = 15, Foreground = MemoTheme.Brush("#514C3C"), CaretBrush = MemoTheme.Brush(MemoTheme.Accent(color)) };
            TextBlock.SetLineHeight(input, 25);
            System.Windows.Automation.AutomationProperties.SetAutomationId(input, "NoteText");
            System.Windows.Automation.AutomationProperties.SetName(input, "便签内容");
            writing.Children.Add(input);
            placeholder = new TextBlock { Text = "记下一件事、一个想法，\n或今天想完成的小目标…", FontSize = 15, LineHeight = 25,
                Foreground = MemoTheme.Brush("#AD9E78"), IsHitTestVisible = false, Margin = new Thickness(13, 10, 10, 0), Visibility = note.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
            writing.Children.Add(placeholder);
            input.TextChanged += delegate { placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; MarkDirty(); };
            Grid.SetRow(writing, 2); content.Children.Add(writing);
            var footer = new Grid();
            status = MemoTheme.Text("自动保存到本机", 11, "#978A68"); status.VerticalAlignment = VerticalAlignment.Center; status.MaxWidth = 290; status.TextWrapping = TextWrapping.Wrap; status.HorizontalAlignment = HorizontalAlignment.Left; footer.Children.Add(status);
            var done = MemoTheme.Button("完成  ↵", "完成便签编辑"); done.Width = 92; done.HorizontalAlignment = HorizontalAlignment.Right;
            done.Background = MemoTheme.Brush("#FFFDF8"); done.Foreground = MemoTheme.Brush("#756341"); done.BorderBrush = MemoTheme.Brush("#70C7B17A"); done.BorderThickness = new Thickness(1);
            System.Windows.Automation.AutomationProperties.SetAutomationId(done, "NoteDone");
            done.ToolTip = "Ctrl+Enter 或 Esc 收起，内容会保存"; done.Click += delegate { Close(); }; footer.Children.Add(done);
            Grid.SetRow(footer, 3); content.Children.Add(footer);
            Content = outer;
            UpdatePaper();
            saveDelay.Tick += delegate { saveDelay.Stop(); FlushChanges(); };
            Loaded += delegate { Activate(); input.Focus(); input.CaretIndex = input.Text.Length; };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape || (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)) { e.Handled = true; Close(); }
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!FlushChanges()) e.Cancel = true; };
            Closed += delegate { saveDelay.Stop(); };
        }
        void UpdatePaper()
        {
            paper.Background = MemoTheme.Paper(color); paper.BorderBrush = MemoTheme.Brush(MemoTheme.Edge(color));
            if (input != null) input.CaretBrush = MemoTheme.Brush(MemoTheme.Accent(color));
            foreach (var entry in swatches) entry.Value.Stroke = MemoTheme.Brush(entry.Key == color ? MemoTheme.Accent(color) : "#40FFFFFF");
        }
        void MarkDirty()
        {
            dirty = true; status.Text = "正在保存…"; status.Foreground = MemoTheme.Brush("#978A68");
            saveDelay.Stop(); saveDelay.Start();
        }
        internal bool FlushChanges()
        {
            saveDelay.Stop();
            if (!dirty) return true;
            try
            {
                store.UpdateNote(memo, input.Text, color);
                dirty = false; status.Text = "已保存 · " + DateTime.Now.ToString("HH:mm"); status.Foreground = MemoTheme.Brush("#859064"); return true;
            }
            catch (Exception ex)
            {
                store.Log("Save memo", ex); status.Text = "未能保存，窗口会保留内容，请稍后重试。"; status.Foreground = MemoTheme.Brush("#B56D53"); return false;
            }
        }
        internal bool IsBlank { get { return String.IsNullOrWhiteSpace(input.Text); } }
    }

    sealed partial class Shelf
    {
        ToolTip noteTip;
        MemoEditor memoEditor;
        internal List<object> Deck()
        {
            return store.Items.Cast<object>().Concat(store.Notes.Cast<object>()).OrderByDescending(x => x is Memo ? ((Memo)x).Created : ((Shot)x).Created).ToList();
        }
        internal void NewNote()
        {
            if (dialogOpen || capturePending) return;
            try { var note = store.AddNote("", "Butter"); Reveal(); EditNote(note, true); }
            catch (Exception ex) { store.Log("Create memo", ex); Notify(ex.Message); }
        }
        internal void EditNote(Memo note, bool fresh)
        {
            if (dialogOpen || capturePending || !store.Notes.Contains(note)) return;
            CloseImagePreview(); CloseNotePreview();
            if (helpTip != null) helpTip.IsOpen = false;
            dialogOpen = true;
            try
            {
                memoEditor = new MemoEditor(store, note) { Owner = this };
                memoEditor.ShowDialog();
                if (fresh && memoEditor.IsBlank) store.RemoveNote(note);
            }
            catch (Exception ex) { store.Log("Edit memo", ex); Notify(ex.Message); }
            finally
            {
                memoEditor = null; dialogOpen = false;
                keepUntil = DateTime.Now.AddSeconds(3); lastInside = DateTime.Now; Reveal(); Render();
            }
        }
        void CopyNote(Memo note)
        {
            try { SetClipboard(new DataObject(DataFormats.UnicodeText, note.Text)); Notify("便签文字已复制。"); }
            catch (Exception ex) { store.Log("Copy memo", ex); Notify("复制失败，请稍后重试。"); }
        }
        void DeleteNote(Memo note)
        {
            CloseNotePreview(); dialogOpen = true;
            try
            {
                if (MessageBox.Show(this, "删除这张便签？内容将不会保留。", "删除便签", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    store.RemoveNote(note);
            }
            catch (Exception ex) { store.Log("Delete memo", ex); Notify(ex.Message); }
            finally { dialogOpen = false; keepUntil = DateTime.Now.AddSeconds(3); lastInside = DateTime.Now; }
        }
        void CloseNotePreview()
        {
            var current = noteTip; noteTip = null;
            if (current != null) current.IsOpen = false;
        }
        ToolTip NotePopup(Memo note, FrameworkElement target)
        {
            double maxHeight = Math.Max(90, Math.Min(440, screen.WorkingArea.Height / Dpi() - 315));
            double width = Math.Max(200, Math.Min(360, screen.WorkingArea.Width / Dpi() - 60));
            var content = new StackPanel { Width = width };
            var title = MemoTheme.Text("便签", 13, MemoTheme.Accent(note.Color)); title.Margin = new Thickness(0, 0, 0, 12); content.Children.Add(title);
            var text = MemoTheme.Text(String.IsNullOrWhiteSpace(note.Text) ? "还没有内容，双击写点什么。" : note.Text, 14, "#534F41");
            text.TextWrapping = TextWrapping.Wrap; text.LineHeight = 23;
            var scroll = new ScrollViewer { Content = text, MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            content.Children.Add(scroll);
            content.Children.Add(new TextBlock { Text = "双击编辑 · 右键复制或删除", FontSize = 11, Foreground = MemoTheme.Brush("#988D71"), Margin = new Thickness(0, 13, 0, 0) });
            var tip = new ToolTip { Content = new Border { Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(9), Background = MemoTheme.Paper(note.Color),
                    BorderBrush = MemoTheme.Brush(MemoTheme.Edge(note.Color)), BorderThickness = new Thickness(1) },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HasDropShadow = true,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, PlacementTarget = target,
                HorizontalOffset = (target.Width - width - 38) / 2, VerticalOffset = 10, FontFamily = FontFamily };
            System.Windows.Automation.AutomationProperties.SetName(tip, "便签全文");
            tip.Opened += delegate
            {
                if (!expanded || dialogOpen || capturePending || dragging || shuttingDown || !store.Notes.Contains(note)) { tip.IsOpen = false; return; }
                CloseNotePreview(); CloseImagePreview(); if (helpTip != null) helpTip.IsOpen = false; noteTip = tip;
            };
            tip.Closed += delegate { if (Object.ReferenceEquals(noteTip, tip)) noteTip = null; lastInside = DateTime.Now; };
            return tip;
        }
        void MakeNoteCard(Memo note, double center, double y, int index)
        {
            const double width = 182, height = 153;
            double lean = new[] { -2.4, 1.6, -1.1, 2.2 }[index % 4];
            var swing = new RotateTransform(lean, width / 2, -15);
            var grid = new Grid { Width = width, Height = height, Cursor = Cursors.Hand, RenderTransform = swing };
            System.Windows.Automation.AutomationProperties.SetName(grid, "便签 " + (note.Text.Length > 30 ? note.Text.Substring(0, 30) : note.Text));
            System.Windows.Automation.AutomationProperties.SetAutomationId(grid, "NoteCard-" + note.Id);
            grid.Children.Add(new Border { Background = MemoTheme.Paper(note.Color), BorderBrush = MemoTheme.Brush(MemoTheme.Edge(note.Color)), BorderThickness = new Thickness(0.6), CornerRadius = new CornerRadius(3),
                Effect = new DropShadowEffect { Color = MemoTheme.Hex("#555D6B"), Opacity = 0.16, ShadowDepth = 4, BlurRadius = 9 } });
            var details = new Grid { Margin = new Thickness(15, 12, 15, 13), IsHitTestVisible = false };
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid(); header.Children.Add(MemoTheme.Text("便签", 10, MemoTheme.Accent(note.Color)));
            var date = MemoTheme.Text(note.Created.ToString("M月d日"), 9, "#AB9C79"); date.HorizontalAlignment = HorizontalAlignment.Right; header.Children.Add(date);
            details.Children.Add(header);
            var body = MemoTheme.Text(String.IsNullOrWhiteSpace(note.Text) ? "双击，写下第一件事…" : note.Text, 13, "#5D5541");
            body.TextWrapping = TextWrapping.Wrap; body.TextTrimming = TextTrimming.CharacterEllipsis; body.LineHeight = 22;
            body.Margin = new Thickness(0, 9, 0, 5); body.ClipToBounds = true;
            Grid.SetRow(body, 1); details.Children.Add(body);
            var footer = MemoTheme.Text("双击编辑", 9, MemoTheme.Accent(note.Color)); footer.Opacity = 0.75; Grid.SetRow(footer, 2); details.Children.Add(footer);
            grid.Children.Add(details);
            grid.Children.Add(new Polygon { Points = new PointCollection { new Point(width - 20, height), new Point(width - 20, height - 20), new Point(width, height - 20) },
                Fill = MemoTheme.Brush(MemoTheme.Edge(note.Color)), Opacity = 0.48, IsHitTestVisible = false });
            grid.Children.Add(new Line { X1 = width - 20, Y1 = height, X2 = width, Y2 = height - 20, Stroke = MemoTheme.Brush(MemoTheme.Edge(note.Color)), StrokeThickness = 0.7, IsHitTestVisible = false });
            AddHangingPeg(grid);
            var remove = MemoTheme.Button("×", "删除便签"); remove.Width = 21; remove.Height = 21; remove.FontSize = 16; remove.Padding = new Thickness(0); remove.Background = MemoTheme.Brush("#DDFFFDF5");
            remove.HorizontalAlignment = HorizontalAlignment.Right; remove.VerticalAlignment = VerticalAlignment.Top; remove.Margin = new Thickness(0, -9, -8, 0); remove.Opacity = 0; remove.IsHitTestVisible = false;
            remove.Click += delegate { DeleteNote(note); }; grid.Children.Add(remove);
            var menu = new ContextMenu(); menu.Items.Add(Item("编辑便签", delegate { EditNote(note, false); })); menu.Items.Add(Item("复制文字", delegate { CopyNote(note); }));
            menu.Items.Add(new Separator()); menu.Items.Add(Item("删除便签…", delegate { DeleteNote(note); }));
            menu.Opened += delegate { CloseNotePreview(); }; grid.ContextMenu = menu;
            grid.ToolTip = NotePopup(note, grid); ToolTipService.SetInitialShowDelay(grid, 280); ToolTipService.SetBetweenShowDelay(grid, 100); ToolTipService.SetShowDuration(grid, 60000);
            var breeze = new DoubleAnimation(lean - 0.45, lean + 0.45, TimeSpan.FromSeconds(4.4 + index * 0.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(breeze, 24); swing.BeginAnimation(RotateTransform.AngleProperty, breeze);
            grid.MouseEnter += delegate { remove.IsHitTestVisible = true; remove.Opacity = 1; swing.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))); };
            grid.MouseLeave += delegate { remove.IsHitTestVisible = false; remove.Opacity = 0; swing.BeginAnimation(RotateTransform.AngleProperty, breeze); };
            grid.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { CloseNotePreview(); if (e.ClickCount == 2) { e.Handled = true; EditNote(note, false); } };
            Place(grid, center - width / 2, y);
        }
    }
    static class MemoTests
    {
        static void Check(bool passed, string text) { if (!passed) throw new Exception("Memo test failed: " + text); }
        internal static int Run(string root)
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Memo tests require an empty isolated directory.");
            var report = new List<string>();
            var store = new Store(root);
            Check(store.Notes.Count == 0, "Existing image store starts without notes");
            string body = "明天记得带相机 📷\n\n买一束花，给生活留一点颜色。\n<b>文字按原样保存</b>";
            var memo = store.AddNote(body, "Mint");
            string id = memo.Id; DateTime created = memo.Created;
            var restored = new Store(root);
            Check(restored.Notes.Count == 1 && restored.Notes[0].Text == body && restored.Notes[0].Color == "Mint" && restored.Notes[0].Id == id, "Unicode, multiline and paper color survive restart");
            report.Add("PASS: Unicode, emoji, multiline text and paper color persist across restart");
            store.UpdateNote(memo, body + "\n完成整理", "Lavender");
            Check(memo.Id == id && memo.Created == created && new Store(root).Notes[0].Color == "Lavender", "Editing preserves identity and chronological position");
            report.Add("PASS: editing preserves creation order, ID and chosen color");
            string textBefore = memo.Text, colorBefore = memo.Color;
            string notesFile = System.IO.Path.Combine(root, "notes.json");
            byte[] durableBefore = File.ReadAllBytes(notesFile);
            using (var locked = File.Open(notesFile + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                bool failed = false; try { store.UpdateNote(memo, "not committed", "Butter"); } catch (IOException) { failed = true; }
                Check(failed && memo.Text == textBefore && memo.Color == colorBefore && File.ReadAllBytes(notesFile).SequenceEqual(durableBefore), "Failed save preserves memory and durable note");
                failed = false; try { store.AddNote("not committed", "Butter"); } catch (IOException) { failed = true; }
                Check(failed && store.Notes.Count == 1, "Failed addition rolled back");
                failed = false; try { store.RemoveNote(memo); } catch (IOException) { failed = true; }
                Check(failed && store.Notes.Contains(memo), "Failed deletion rolled back");
            }
            report.Add("PASS: interrupted saves, additions and deletions preserve existing notes");
            var second = store.AddNote("周末出门走走", "unknown");
            Check(second.Color == "Butter", "Unknown color uses safe default");
            for (int i = 0; i < Store.MaxItems + 1; i++) store.Add(Program.Demo("MEMO TEST " + i, "截图与便签", i % 2 == 0 ? "#315B75" : "#426C60"), "fixture");
            Check(store.Items.Count == Store.MaxItems && store.Notes.Count == 2, "Image retention leaves notes intact");
            store.Clear();
            Check(store.Items.Count == 0 && store.Notes.Count == 2 && new Store(root).Notes.Count == 2, "Clearing screenshots preserves notes");
            report.Add("PASS: notes survive the 60-image retention limit and clearing screenshots");
            store.RemoveNote(second);
            Check(new Store(root).Notes.Count == 1 && File.Exists(notesFile), "Delete only selected memo");
            bool tooLong = false; try { store.UpdateNote(memo, new string('x', Store.MaxNoteText + 1), "Mint"); } catch (InvalidOperationException) { tooLong = true; }
            Check(tooLong && memo.Text == textBefore, "Oversize edit preserves saved text");
            report.Add("PASS: selected-note deletion and oversize-input validation preserve other content");
            string corruptRoot = System.IO.Path.Combine(root, "corrupt-notes"); Directory.CreateDirectory(corruptRoot);
            string corruptFile = System.IO.Path.Combine(corruptRoot, "notes.json"); File.WriteAllText(corruptFile, "{broken-notes");
            var damaged = new Store(corruptRoot); bool refused = false;
            try { damaged.AddNote("replacement", "Butter"); } catch (InvalidOperationException) { refused = true; }
            Check(refused && damaged.Notes.Count == 0 && File.ReadAllText(corruptFile) == "{broken-notes", "Unreadable notes never overwritten");
            report.Add("PASS: unreadable notes file is preserved rather than overwritten");
            File.WriteAllLines(System.IO.Path.Combine(root, "memo-results.txt"), report, Encoding.UTF8);
            return 0;
        }
    }
}
