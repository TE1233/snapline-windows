using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Snapline
{
    sealed partial class Store
    {
        internal const int MaxRemarkText = 2000;
        internal void UpdateRemark(Shot shot, string text)
        {
            if (!Items.Contains(shot)) throw new InvalidOperationException("这张截图已被移除。");
            text = (text ?? "").Trim();
            if (text.Length > MaxRemarkText) throw new InvalidOperationException("截图备注最多支持 2000 个字符。");
            if ((shot.Remark ?? "") == text) return;
            string previous = shot.Remark;
            shot.Remark = text;
            try { Save(); } catch { shot.Remark = previous; throw; }
            if (Changed != null) Changed();
        }
    }

    sealed class RemarkEditor : Window
    {
        readonly Store store;
        readonly Shot shot;
        internal readonly TextBox Input;
        readonly TextBlock status;
        readonly Point anchor;
        internal RemarkEditor(Store store, Shot shot, Point anchor)
        {
            this.store = store; this.shot = shot; this.anchor = anchor;
            Title = "截图备注 · 截图晾台"; Width = 376; Height = 248;
            ShowInTaskbar = false; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Microsoft YaHei UI"); UseLayoutRounding = true;
            var outer = new Grid { Margin = new Thickness(10) };
            outer.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = MemoTheme.Paper("Butter"),
                BorderBrush = MemoTheme.Brush("#E9D799"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = MemoTheme.Hex("#354155"), BlurRadius = 15, ShadowDepth = 4, Opacity = 0.16 } });
            var content = new Grid { Margin = new Thickness(18, 15, 18, 13) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            var title = MemoTheme.Text("这张截图，用来做什么？", 16, "#625435");
            title.FontWeight = FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(title);
            var close = MemoTheme.Button("×", "取消截图备注编辑"); close.Width = 26; close.Height = 26; close.Padding = new Thickness(0); close.FontSize = 19;
            close.HorizontalAlignment = HorizontalAlignment.Right; close.Click += delegate { Close(); }; heading.Children.Add(close); content.Children.Add(heading);
            var writing = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            writing.Children.Add(new Border { Background = MemoTheme.Brush("#66FFFFFF"), CornerRadius = new CornerRadius(6),
                BorderBrush = MemoTheme.Brush("#70D6C695"), BorderThickness = new Thickness(1) });
            Input = new TextBox { Text = shot.Remark ?? "", MaxLength = Store.MaxRemarkText, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Padding = new Thickness(10, 8, 10, 8), FontSize = 13,
                Foreground = MemoTheme.Brush("#64573B"), CaretBrush = MemoTheme.Brush("#AC8D3F") };
            AutomationProperties.SetAutomationId(Input, "RemarkInput"); AutomationProperties.SetName(Input, "截图用途备注");
            writing.Children.Add(Input);
            var placeholder = MemoTheme.Text("例如：周五做方案时，参考这个布局。", 12, "#AB9C78");
            placeholder.TextWrapping = TextWrapping.Wrap; placeholder.Margin = new Thickness(11, 10, 8, 0); placeholder.IsHitTestVisible = false;
            placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; writing.Children.Add(placeholder);
            Input.TextChanged += delegate { placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; status.Text = "Enter 保存 · Shift+Enter 换行 · Esc 取消"; };
            Grid.SetRow(writing, 1); content.Children.Add(writing);
            status = MemoTheme.Text("Enter 保存 · Shift+Enter 换行 · Esc 取消", 10, "#9B8C65");
            status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(status, 2); content.Children.Add(status);
            var footer = new Grid();
            var hint = MemoTheme.Text("备注留在晾台，原图照常复制", 10, "#A29576"); hint.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(hint);
            var save = MemoTheme.Button("保存", "保存截图备注"); save.Width = 64; save.HorizontalAlignment = HorizontalAlignment.Right;
            save.Background = MemoTheme.Brush("#FFFDF8"); save.Foreground = MemoTheme.Brush("#756341");
            save.BorderBrush = MemoTheme.Brush("#70C7B17A"); save.BorderThickness = new Thickness(1);
            AutomationProperties.SetAutomationId(save, "RemarkSave"); save.Click += delegate { SaveAndClose(); }; footer.Children.Add(save);
            Grid.SetRow(footer, 3); content.Children.Add(footer); outer.Children.Add(content); Content = outer;
            Loaded += delegate { PositionNearCard(); Activate(); Input.Focus(); Input.CaretIndex = Input.Text.Length; };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { e.Handled = true; Close(); }
                else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; SaveAndClose(); }
            };
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
            try { store.UpdateRemark(shot, Input.Text); return true; }
            catch (Exception ex)
            {
                store.Log("Save screenshot remark", ex);
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
        FrameworkElement RemarkStrip(Shot shot, double width, double imageHeight)
        {
            bool blank = String.IsNullOrWhiteSpace(shot.Remark);
            var button = MemoTheme.Button("", blank ? "添加截图备注" : "编辑截图备注");
            button.Width = width - 12; button.Height = blank ? 28 : 42; button.Padding = new Thickness(8, 4, 8, 4);
            button.VerticalAlignment = VerticalAlignment.Top; button.Margin = new Thickness(0, imageHeight + 8, 0, 0); button.Focusable = false;
            button.Background = MemoTheme.Brush(blank ? "#DCFFF9E9" : "#FFFFF7DD"); button.Foreground = MemoTheme.Brush("#8E7B4D");
            button.BorderBrush = MemoTheme.Brush("#C8E8D9AD"); button.BorderThickness = new Thickness(0.7);
            button.Effect = new DropShadowEffect { Color = MemoTheme.Hex("#746747"), BlurRadius = 4, ShadowDepth = 2, Opacity = 0.11 };
            var text = MemoTheme.Text(blank ? "＋ 添加备注" : shot.Remark, blank ? 11 : 11.5, blank ? "#A18C59" : "#766744");
            text.Width = button.Width - 18; text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.LineHeight = 16; text.MaxHeight = blank ? 17 : 32; text.ClipToBounds = true;
            text.TextAlignment = blank ? TextAlignment.Center : TextAlignment.Left; button.Content = text;
            AutomationProperties.SetAutomationId(button, "Remark-" + shot.File);
            button.ToolTip = blank ? "记一下这张截图的用途" : "点击修改备注 · 悬停图片查看全文";
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; EditRemark(shot, button); };
            return button;
        }
        internal void EditRemark(Shot shot, FrameworkElement target)
        {
            if (dialogOpen || capturePending || !store.Items.Contains(shot)) return;
            CloseImagePreview(); CloseNotePreview(); if (helpTip != null) helpTip.IsOpen = false;
            var anchor = target != null ? target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight + 4)) :
                new Point(screen.WorkingArea.Left + screen.WorkingArea.Width / 2, screen.WorkingArea.Top + 220 * Dpi());
            dialogOpen = true;
            try
            {
                remarkEditor = new RemarkEditor(store, shot, anchor) { Owner = this };
                remarkEditor.Closed += delegate { remarkEditor = null; dialogOpen = false; keepUntil = DateTime.Now.AddSeconds(3); lastInside = DateTime.Now; Render(); };
                remarkEditor.Show();
            }
            catch (Exception ex) { remarkEditor = null; dialogOpen = false; store.Log("Edit screenshot remark", ex); Notify(ex.Message); }
        }
    }
}
