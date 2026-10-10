using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Snapline
{
    sealed class ShortcutEditor : Window
    {
        readonly TextBox input;
        readonly TextBlock status;
        readonly Button save;
        readonly Func<ShortcutSpec, string> apply;
        ShortcutSpec candidate;
        internal ShortcutSpec Candidate { get { return candidate; } }
        internal ShortcutEditor(ShortcutSpec current, Func<ShortcutSpec, string> saveChoice)
        {
            apply = saveChoice; candidate = current;
            Title = "自定义快捷键 · 截图晾台";
            Width = 484; Height = 382; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; WindowStyle = WindowStyle.None;
            AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI"); UseLayoutRounding = true;
            var outer = new Grid { Margin = new Thickness(14) };
            var panel = new Border { CornerRadius = new CornerRadius(14), Background = MemoTheme.Brush("#F8FCFF"),
                BorderBrush = MemoTheme.Brush("#BEDDF0"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = MemoTheme.Hex("#354155"), BlurRadius = 24, ShadowDepth = 6, Opacity = 0.19 } };
            outer.Children.Add(panel);
            var content = new Grid { Margin = new Thickness(25, 22, 25, 20) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Children.Add(content);
            var header = new Grid { Margin = new Thickness(0, 0, 0, 17) };
            var heading = new StackPanel();
            heading.Children.Add(MemoTheme.Text("截图晾台  /  SHORTCUT", 10, "#7394AB"));
            var title = MemoTheme.Text("自定义快捷键", 23, "#285572"); title.Margin = new Thickness(0, 6, 0, 0); heading.Children.Add(title);
            heading.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (e.ClickCount == 1) DragMove(); };
            header.Children.Add(heading);
            var close = MemoTheme.Button("×", "取消快捷键设置");
            close.Width = 30; close.Height = 30; close.FontSize = 20; close.Padding = new Thickness(0);
            close.Foreground = MemoTheme.Brush("#7696A9"); close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top;
            close.Click += delegate { Close(); }; header.Children.Add(close); content.Children.Add(header);
            var description = new TextBlock { Text = "点击下面的按键框，再按下你想用的组合键。",
                FontSize = 12, Foreground = MemoTheme.Brush("#638093"), Margin = new Thickness(0, 0, 0, 13) };
            Grid.SetRow(description, 1); content.Children.Add(description);
            input = new TextBox { IsReadOnly = true, Text = current == null ? "点击这里，按下组合键" : current.Label,
                FontSize = current == null ? 16 : 24, FontFamily = new FontFamily("Segoe UI Semibold"),
                TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Height = 68, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(9),
                Foreground = MemoTheme.Brush("#327CAA"), Cursor = Cursors.Hand, IsReadOnlyCaretVisible = false };
            System.Windows.Automation.AutomationProperties.SetAutomationId(input, "ShortcutInput");
            System.Windows.Automation.AutomationProperties.SetName(input, "按下截图快捷键组合");
            input.PreviewKeyDown += RecordKey;
            input.GotKeyboardFocus += delegate { input.Select(0, 0); };
            var keyPanel = new Border { Child = input, CornerRadius = new CornerRadius(10),
                Background = MemoTheme.Brush("#EDF7FE"), BorderBrush = MemoTheme.Brush("#90C9EA"), BorderThickness = new Thickness(1.5) };
            Grid.SetRow(keyPanel, 2); content.Children.Add(keyPanel);
            var guidance = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
            guidance.Children.Add(new TextBlock { Text = "Ctrl、Shift、Alt + 字母 / 数字 / 符号 / 功能键",
                FontSize = 10.5, Foreground = MemoTheme.Brush("#8196A5") });
            status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(status, "ShortcutStatus");
            guidance.Children.Add(status); Grid.SetRow(guidance, 3); content.Children.Add(guidance);
            var footer = new Grid();
            var cancel = MemoTheme.Button("取消", "取消快捷键设置"); cancel.Width = 75; cancel.Foreground = MemoTheme.Brush("#638093");
            cancel.HorizontalAlignment = HorizontalAlignment.Left; cancel.Click += delegate { Close(); }; footer.Children.Add(cancel);
            save = MemoTheme.Button("保存并启用", "保存并启用快捷键"); save.Width = 124; save.Height = 35;
            save.Background = MemoTheme.Brush("#418DBB"); save.Foreground = Brushes.White; save.HorizontalAlignment = HorizontalAlignment.Right;
            System.Windows.Automation.AutomationProperties.SetAutomationId(save, "ShortcutSave");
            save.Click += delegate { SaveChoice(); }; footer.Children.Add(save);
            Grid.SetRow(footer, 4); content.Children.Add(footer); Content = outer;
            UpdateCandidate();
            Loaded += delegate { Activate(); input.Focus(); input.Select(0, 0); };
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Close(); }
            };
        }
        void RecordKey(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Tab && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift)) return;
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Close(); return; }
            e.Handled = true;
            uint modifiers = ShortcutSpec.ModifiersFrom(Keyboard.Modifiers);
            if (ShortcutSpec.IsModifier(key))
            {
                candidate = null;
                input.FontSize = 23; input.Text = ShortcutSpec.ModifierLabel(modifiers) + " + …";
                input.Select(0, 0);
                status.Text = "继续按下另一个键；若组合未显示，可换一组再试。";
                status.Foreground = MemoTheme.Brush("#7892A3"); save.IsEnabled = false; return;
            }
            Record(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        }
        internal void Record(uint modifiers, uint key)
        {
            string error = ShortcutSpec.Validate(modifiers, key);
            candidate = error == null ? new ShortcutSpec(modifiers, key) : null;
            input.FontSize = 24; input.Text = new ShortcutSpec(modifiers, key).Label;
            input.Select(0, 0);
            if (error == null) UpdateCandidate();
            else { status.Text = error; status.Foreground = MemoTheme.Brush("#BA7950"); save.IsEnabled = false; }
        }
        void UpdateCandidate()
        {
            save.IsEnabled = candidate != null;
            status.Text = candidate == null ? "保存时检查全局快捷键占用；设置会在下次启动保留。" : candidate.Warning;
            status.Foreground = MemoTheme.Brush(candidate != null && candidate.Warning.StartsWith("这组") ? "#B18543" : "#6D8B9F");
        }
        internal void SaveChoice()
        {
            if (candidate == null) return;
            string error = apply(candidate);
            if (error == null) { DialogResult = true; }
            else { status.Text = error; status.Foreground = MemoTheme.Brush("#BA7950"); input.Focus(); }
        }
        internal void Snapshot(string file)
        {
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = System.IO.File.Create(file)) encoder.Save(stream);
        }
    }
}
