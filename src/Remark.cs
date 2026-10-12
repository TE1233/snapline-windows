using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Snapline
{
    sealed partial class Store
    {
        internal const int MaxRemarkText = 2000;
        internal Action<Shot> RemarkChanged;
        internal void UpdateRemark(Shot shot, string text)
        {
            if (!Items.Contains(shot)) throw new InvalidOperationException("这张截图已被移除。");
            text = (text ?? "").Trim();
            if (text.Length > MaxRemarkText) throw new InvalidOperationException("截图备注最多支持 2000 个字符。");
            if ((shot.Remark ?? "") == text) return;
            string previous = shot.Remark;
            shot.Remark = text;
            try { Save(); } catch { shot.Remark = previous; throw; }
            if (RemarkChanged != null) RemarkChanged(shot);
            else if (Changed != null) Changed();
        }
    }

    sealed class RemarkEditor : Window
    {
        readonly Store store;
        readonly Shot shot;
        internal readonly TextBox Input;
        readonly TextBlock status;
        readonly Point anchor;
        readonly DispatcherTimer autoClose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        DateTime? outsideSince;
        bool autoCloseSuspended;
        int fadeSerial;
        internal bool IsFading, AutoClosed;
        internal const int DismissDelay = 200;
        internal bool IsDismissPending { get { return outsideSince.HasValue && !IsFading && !autoCloseSuspended; } }
        internal Func<bool> PointerInside;
        internal Func<bool> PointerOnShot = delegate { return false; };
        internal Func<DateTime> Clock = delegate { return DateTime.UtcNow; };
        internal Action<string> Persist;
        internal RemarkEditor(Store store, Shot shot, Point anchor)
        {
            this.store = store; this.shot = shot; this.anchor = anchor;
            Persist = delegate(string value) { store.UpdateRemark(shot, value); }; PointerInside = ContainsPointer;
            Title = "截图备注 · 截图晾台"; Width = 376; Height = 238;
            ShowInTaskbar = false; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Microsoft YaHei UI"); UseLayoutRounding = true;
            var outer = new Grid { Margin = new Thickness(10) };
            outer.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = MemoTheme.Paper("Butter"),
                BorderBrush = MemoTheme.Brush("#E9D799"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = MemoTheme.Hex("#354155"), BlurRadius = 15, ShadowDepth = 4, Opacity = 0.16 } });
            var content = new Grid { Margin = new Thickness(18, 13, 18, 10) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            var title = MemoTheme.Text("这张截图，用来做什么？", 16, "#625435");
            title.FontWeight = FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(title);
            var close = MemoTheme.Button("×", "取消截图备注编辑"); close.Width = 26; close.Height = 26; close.Padding = new Thickness(0); close.FontSize = 19;
            close.HorizontalAlignment = HorizontalAlignment.Right; close.Click += delegate { Close(); }; heading.Children.Add(close); content.Children.Add(heading);
            var writing = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            writing.Children.Add(new Border { Background = MemoTheme.Brush("#66FFFFFF"), CornerRadius = new CornerRadius(6),
                BorderBrush = MemoTheme.Brush("#70D6C695"), BorderThickness = new Thickness(1) });
            Input = new TextBox { Text = shot.Remark ?? "", MaxLength = Store.MaxRemarkText, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Padding = new Thickness(10, 8, 10, 8), FontSize = 13,
                Foreground = Brushes.Black, CaretBrush = MemoTheme.Brush("#AC8D3F") };
            AutomationProperties.SetAutomationId(Input, "RemarkInput"); AutomationProperties.SetName(Input, "截图用途备注");
            TextBlock.SetLineHeight(Input, 21);
            writing.Children.Add(Input);
            var placeholder = MemoTheme.Text("例如：周五做方案时，参考这个布局。", 12, "#AB9C78");
            placeholder.TextWrapping = TextWrapping.Wrap; placeholder.Margin = new Thickness(11, 10, 8, 0); placeholder.IsHitTestVisible = false;
            placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; writing.Children.Add(placeholder);
            Input.TextChanged += delegate { placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; status.Text = "Enter 保存 · Shift+Enter 换行 · Esc 取消"; status.Foreground = MemoTheme.Brush("#9B8C65"); ResumeEditing(); };
            Grid.SetRow(writing, 1); content.Children.Add(writing);
            status = MemoTheme.Text("Enter 保存 · Shift+Enter 换行 · Esc 取消", 10, "#9B8C65");
            status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 0, 0, 5); Grid.SetRow(status, 2); content.Children.Add(status);
            var footer = new Grid();
            var hint = MemoTheme.Text("备注留在晾台，原图照常复制", 10, "#A29576"); hint.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(hint);
            var save = MemoTheme.Button("保存", "保存截图备注"); save.Width = 64; save.HorizontalAlignment = HorizontalAlignment.Right;
            save.Background = MemoTheme.Brush("#FFFDF8"); save.Foreground = MemoTheme.Brush("#756341");
            save.BorderBrush = MemoTheme.Brush("#70C7B17A"); save.BorderThickness = new Thickness(1);
            AutomationProperties.SetAutomationId(save, "RemarkSave"); save.Click += delegate { SaveAndClose(); }; footer.Children.Add(save);
            Grid.SetRow(footer, 3); content.Children.Add(footer); outer.Children.Add(content); Content = outer;
            autoClose.Tick += delegate { ObservePointer(PointerInRemarkArea(), Clock()); };
            MouseEnter += delegate { if (PointerInside()) ResumeEditing(); };
            Loaded += delegate { PositionNearCard(); Activate(); Input.Focus(); Input.CaretIndex = Input.Text.Length; autoClose.Start(); };
            Closed += delegate { autoClose.Stop(); fadeSerial++; IsFading = false; };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                ResumeEditing();
                if (e.Key == Key.Escape) { e.Handled = true; Close(); }
                else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; SaveAndClose(); }
            };
        }
        bool ContainsPointer()
        {
            Native.Point pointer; Native.Rect bounds;
            return !Native.GetCursorPos(out pointer) || !Native.GetWindowRect(new WindowInteropHelper(this).Handle, out bounds) ||
                (pointer.X >= bounds.Left && pointer.X < bounds.Right && pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom);
        }
        internal bool PointerInRemarkArea() { return PointerInside() || PointerOnShot() || IsMouseCaptureWithin; }
        void ResumeEditing()
        {
            outsideSince = null; autoCloseSuspended = false; fadeSerial++; IsFading = false;
            BeginAnimation(OpacityProperty, null); Opacity = 1;
        }
        internal void ObservePointer(bool inside, DateTime now)
        {
            if (!IsVisible) return;
            if (inside)
            {
                if (outsideSince != null || IsFading || autoCloseSuspended) ResumeEditing();
                return;
            }
            if (autoCloseSuspended || IsFading) return;
            if (outsideSince == null) outsideSince = now;
            if ((now - outsideSince.Value).TotalMilliseconds < DismissDelay) return;
            if (!TrySave()) return;
            IsFading = true;
            int serial = ++fadeSerial;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
            fade.Completed += delegate
            {
                if (!IsFading || serial != fadeSerial) return;
                if (PointerInRemarkArea()) { ResumeEditing(); return; }
                AutoClosed = true; Close();
            };
            BeginAnimation(OpacityProperty, fade);
        }
        void PositionNearCard()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            var area = Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y)).WorkingArea;
            for (int i = 0; i < 2; i++)
            {
                double dpi = 1;
                try { uint value = Native.GetDpiForWindow(handle); if (value > 0) dpi = value / 96.0; } catch (EntryPointNotFoundException) { }
                int width = Math.Min(area.Width, (int)Math.Ceiling(Width * dpi));
                int height = Math.Min(area.Height, (int)Math.Ceiling(Height * dpi));
                int left = Math.Max(area.Left, Math.Min(area.Right - width, (int)anchor.X - width / 2));
                int top = Math.Max(area.Top, Math.Min(area.Bottom - height, (int)anchor.Y));
                Native.SetWindowPos(handle, new IntPtr(-1), left, top, width, height, 0x0010);
            }
        }
        internal bool TrySave()
        {
            try { Persist(Input.Text); return true; }
            catch (Exception ex)
            {
                store.Log("Save screenshot remark", ex);
                autoCloseSuspended = true;
                status.Text = "未能保存，文字已保留，请稍后重试。"; status.Foreground = MemoTheme.Brush("#B56D53"); return false;
            }
        }
        internal void SaveAndClose() { if (TrySave()) Close(); }
        internal void Snapshot(string path)
        {
            var element = (FrameworkElement)Content;
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }

    sealed partial class Shelf
    {
        internal RemarkEditor remarkEditor;
        internal const double RemarkStripHeight = 30;
        FrameworkElement RemarkStrip(Shot shot, double width, double imageHeight)
        {
            bool blank = String.IsNullOrWhiteSpace(shot.Remark);
            double left = width < 110 ? 8 : 11, right = width < 110 ? 10 : 16;
            var button = new Button
            {
                Width = width, Height = RemarkStripHeight, Padding = new Thickness(left, 3, right, 3),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, imageHeight, 0, 0),
                Focusable = false, Cursor = Cursors.Hand, Foreground = Brushes.Black,
                Background = new LinearGradientBrush(MemoTheme.Hex("#EFF8FE"), MemoTheme.Hex("#E0F0FB"), 90),
                BorderBrush = MemoTheme.Brush("#C6E2F5"), BorderThickness = new Thickness(1, 0, 1, 1)
            };
            AutomationProperties.SetName(button, blank ? "添加截图备注" : "编辑截图备注");
            var paper = new FrameworkElementFactory(typeof(Grid));
            var frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(0, 0, 5, 5));
            frame.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            paper.AppendChild(frame);
            var seam = new FrameworkElementFactory(typeof(System.Windows.Shapes.Line));
            seam.SetValue(System.Windows.Shapes.Line.X1Property, 1.0); seam.SetValue(System.Windows.Shapes.Line.X2Property, width - 1);
            seam.SetValue(System.Windows.Shapes.Line.Y1Property, 0.7); seam.SetValue(System.Windows.Shapes.Line.Y2Property, 0.7);
            seam.SetValue(System.Windows.Shapes.Shape.StrokeProperty, MemoTheme.Brush("#9DBED3"));
            seam.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 0.7);
            seam.SetValue(System.Windows.Shapes.Shape.StrokeDashArrayProperty, new DoubleCollection(new[] { 2.0, 2.0 }));
            seam.SetValue(UIElement.IsHitTestVisibleProperty, false); paper.AppendChild(seam);
            var rule = new FrameworkElementFactory(typeof(Border));
            rule.SetValue(FrameworkElement.HeightProperty, 0.7);
            rule.SetValue(Border.BackgroundProperty, MemoTheme.Brush("#248BA8BD"));
            rule.SetValue(FrameworkElement.MarginProperty, new Thickness(left, 0, right, 4));
            rule.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
            rule.SetValue(UIElement.IsHitTestVisibleProperty, false); paper.AppendChild(rule);
            var fold = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            fold.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,10 L 10,0 L 10,8 Q 10,10 8,10 Z"));
            fold.SetValue(System.Windows.Shapes.Shape.FillProperty, new LinearGradientBrush(MemoTheme.Hex("#CADFED"), MemoTheme.Hex("#F9FDFF"), 45));
            fold.SetValue(System.Windows.Shapes.Shape.StrokeProperty, MemoTheme.Brush("#BBD5E5"));
            fold.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 0.5);
            fold.SetValue(FrameworkElement.WidthProperty, 10.0); fold.SetValue(FrameworkElement.HeightProperty, 10.0);
            fold.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            fold.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
            fold.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 1, 1));
            fold.SetValue(UIElement.IsHitTestVisibleProperty, false); paper.AppendChild(fold);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetBinding(FrameworkElement.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            paper.AppendChild(presenter); button.Template = new ControlTemplate(typeof(Button)) { VisualTree = paper };
            UpdateRemarkStrip(button, shot);
            AutomationProperties.SetAutomationId(button, "Remark-" + shot.File);
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; EditRemark(shot, button); };
            return button;
        }
        void UpdateRemarkStrip(Button button, Shot shot)
        {
            bool blank = String.IsNullOrWhiteSpace(shot.Remark);
            double width = button.Width, lead = blank ? (width < 110 ? 8 : 11) : 3, gap = width < 110 ? 4 : 7;
            string summary = blank ? "添加备注" : shot.Remark.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(lead + gap) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement marker;
            if (blank)
                marker = new System.Windows.Shapes.Path
                {
                    Width = lead, Height = lead, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center, Stroke = MemoTheme.Brush("#6B97B2"), StrokeThickness = 1,
                    StrokeLineJoin = PenLineJoin.Round, Data = Geometry.Parse("M 1,11 L 2,7 L 9,0 L 12,3 L 5,10 Z M 2,7 L 5,10 M 8,1 L 11,4")
                };
            else
                marker = new Border { Width = lead, Height = 12, CornerRadius = new CornerRadius(1.5),
                    Background = MemoTheme.Brush("#72C7FA"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            marker.IsHitTestVisible = false; content.Children.Add(marker);
            var label = new RemarkText { Text = summary, FontSize = 12, FontFamily = new FontFamily("Microsoft YaHei UI"), Foreground = Brushes.Black };
            label.MaxHeight = 20; label.ClipToBounds = true;
            label.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(label, 1); content.Children.Add(label); button.Content = content;
            AutomationProperties.SetName(button, blank ? "添加截图备注" : "编辑截图备注");
            button.ToolTip = blank ? "记一下这张截图的用途" : "点击修改备注 · 悬停图片查看全文";
        }
        void RefreshRemark(Shot shot)
        {
            try
            {
                var card = HangingElement(shot) as Grid;
                if (card == null) return;
                var strip = card.Children.OfType<Button>().FirstOrDefault();
                if (strip != null) UpdateRemarkStrip(strip, shot);
                var photo = card.Children.OfType<Grid>().FirstOrDefault();
                if (photo == null) return;
                var previous = photo.ToolTip as ToolTip;
                if (previous != null) previous.IsOpen = false;
                photo.ToolTip = ImagePopup(shot, photo);
            }
            catch (Exception ex) { store.Log("Refresh screenshot remark", ex); renderDeferred = true; }
        }
        internal void EditRemark(Shot shot, FrameworkElement target)
        {
            if (dialogOpen || capturePending || !store.Items.Contains(shot)) return;
            CloseImagePreview(); CloseNotePreview(); if (helpTip != null) helpTip.IsOpen = false;
            var anchor = target != null ? target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight + 4)) :
                new Point(screen.WorkingArea.Left + screen.WorkingArea.Width / 2, screen.WorkingArea.Top + 220 * Dpi());
            dialogOpen = true;
            PauseHoverEffects(true);
            try
            {
                remarkEditor = new RemarkEditor(store, shot, anchor) { Owner = this,
                    PointerOnShot = delegate { Native.Point pointer; return !Native.GetCursorPos(out pointer) ||
                        IsPointerOnRemarkShot(shot, new Point(pointer.X, pointer.Y)); },
                    Persist = delegate(string text) { SaveRemark(shot, text); } };
                var opened = remarkEditor;
                remarkEditor.Closed += delegate { remarkEditor = null; dialogOpen = false;
                    keepUntil = opened.AutoClosed ? DateTime.Now : DateTime.Now.AddSeconds(3);
                    lastInside = opened.AutoClosed ? DateTime.Now.AddMilliseconds(-700) : DateTime.Now;
                    PauseHoverEffects(false); if (renderDeferred) Render(); };
                remarkEditor.Show();
            }
            catch (Exception ex) { remarkEditor = null; dialogOpen = false; PauseHoverEffects(false); store.Log("Edit screenshot remark", ex); Notify(ex.Message); }
        }
        internal bool IsPointerOnRemarkShot(Shot shot, Point screenPoint)
        {
            if (!expanded || !IsVisible) return false;
            // Find the current rendered card, so redraws cannot leave a stale hit region.
            var card = HangingElement(shot);
            if (card == null || !card.IsLoaded || !card.IsVisible) return false;
            var point = card.PointFromScreen(screenPoint);
            // PointFromScreen accounts for DPI and the card's gentle rotation.
            return point.X >= 0 && point.X < card.ActualWidth && point.Y >= 0 && point.Y < card.ActualHeight;
        }
    }
}
