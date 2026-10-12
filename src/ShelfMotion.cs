using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Snapline
{
    // Real tracking, keeping the stored string and accessibility text intact.
    sealed class RemarkText : FrameworkElement
    {
        internal const double LetterSpacing = 0.55;
        internal string Text;
        internal double FontSize = 12;
        internal FontFamily FontFamily = new FontFamily("Microsoft YaHei UI");
        internal Brush Foreground = Brushes.Black;
        FormattedText Format(string value)
        {
            return new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), FontSize, Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
        protected override Size MeasureOverride(Size available)
        {
            string value = Text ?? "";
            double width = Format(value).WidthIncludingTrailingWhitespace + Math.Max(0, StringInfo.ParseCombiningCharacters(value).Length - 1) * LetterSpacing;
            return new Size(Math.Min(available.Width, width), Math.Min(available.Height, 20));
        }
        protected override AutomationPeer OnCreateAutomationPeer() { return new RemarkTextPeer(this); }
        sealed class RemarkTextPeer : FrameworkElementAutomationPeer
        {
            internal RemarkTextPeer(RemarkText owner) : base(owner) { }
            protected override string GetNameCore() { return ((RemarkText)Owner).Text ?? ""; }
            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.Text; }
        }
        protected override void OnRender(DrawingContext context)
        {
            double available = Math.Max(0, ActualWidth);
            if (available <= 0 || String.IsNullOrEmpty(Text)) return;
            var glyphs = new List<FormattedText>();
            var enumerator = StringInfo.GetTextElementEnumerator(Text);
            double total = 0;
            bool clipped = false;
            while (enumerator.MoveNext())
            {
                var glyph = Format((string)enumerator.Current);
                double advance = glyph.WidthIncludingTrailingWhitespace + (glyphs.Count > 0 ? LetterSpacing : 0);
                if (total + advance > available) { clipped = true; break; }
                total += advance; glyphs.Add(glyph);
            }
            var ellipsis = Format("…");
            if (clipped)
                while (glyphs.Count > 0 && total + LetterSpacing + ellipsis.Width > available)
                {
                    total -= glyphs[glyphs.Count - 1].WidthIncludingTrailingWhitespace + (glyphs.Count > 1 ? LetterSpacing : 0);
                    glyphs.RemoveAt(glyphs.Count - 1);
                }
            context.PushClip(new RectangleGeometry(new Rect(0, 0, available, ActualHeight)));
            double x = 0;
            foreach (var glyph in glyphs)
            {
                context.DrawText(glyph, new Point(x, Math.Max(0, (ActualHeight - glyph.Height) / 2)));
                x += glyph.WidthIncludingTrailingWhitespace + LetterSpacing;
            }
            if (clipped) context.DrawText(ellipsis, new Point(x, Math.Max(0, (ActualHeight - ellipsis.Height) / 2)));
            context.Pop();
        }
    }

    sealed partial class Shelf
    {
        readonly Canvas motionLayer = new Canvas { IsHitTestVisible = false };
        readonly List<FallingPaper> fallingPapers = new List<FallingPaper>();
        internal int FlyingCount { get { return fallingPapers.Count; } }
        internal TranslateTransform LastFlight;
        internal Image LastFlying;
        internal FallingPaper LastPaper;
        FrameworkElement HangingElement(object item)
        { return canvas.Children.OfType<FrameworkElement>().FirstOrDefault(x => Object.ReferenceEquals(x.Tag, item)); }

        Image FlightImage(FrameworkElement target)
        {
            if (!expanded || target == null) return null;
            canvas.UpdateLayout(); target.UpdateLayout();
            if (target.ActualWidth <= 0 || target.ActualHeight <= 0) return null;
            const double padding = 12;
            double width = target.ActualWidth + padding * 2, height = target.ActualHeight + padding * 2;
            var decorations = FlightDecorations(target).ToArray();
            var visibility = decorations.Select(x => x.Visibility).ToArray();
            var transform = target.RenderTransform; var cache = target.CacheMode;
            RenderTargetBitmap bitmap;
            try
            {
                for (int i = 0; i < decorations.Length; i++) decorations[i].Visibility = Visibility.Hidden;
                target.RenderTransform = Transform.Identity; target.CacheMode = null; target.UpdateLayout();
                var visual = new DrawingVisual();
                // VisualBrush includes the root's layout offset in its content coordinates.
                var offset = VisualTreeHelper.GetOffset(target);
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(target) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(offset.X - padding, offset.Y - padding, width, height) }, null,
                        new Rect(0, 0, width, height));
                double dpi = Dpi();
                bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi), (int)Math.Ceiling(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                bitmap.Render(visual); bitmap.Freeze();
            }
            finally
            {
                target.RenderTransform = transform; target.CacheMode = cache;
                for (int i = 0; i < decorations.Length; i++) decorations[i].Visibility = visibility[i];
            }
            var point = target.TranslatePoint(new Point(-padding, -padding), canvas);
            var image = new Image { Source = bitmap, Width = width, Height = height, IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.3) };
            Canvas.SetLeft(image, point.X); Canvas.SetTop(image, point.Y);
            return image;
        }
        static IEnumerable<FrameworkElement> FlightDecorations(DependencyObject root)
        {
            var element = root as FrameworkElement;
            if (element != null && (Object.Equals(element.Tag, "HangingPeg") || Object.Equals(element.Tag, "DeleteButton"))) yield return element;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in FlightDecorations(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        void RemoveWithFlight(FrameworkElement target, Action remove)
        {
            BeginRemovalPreviewHold();
            bool animationRunning = false, released = false;
            Action releasePreview = delegate {
                if (released) return;
                released = true; EndRemovalPreviewHold();
            };
            try
            {
                Image image = null;
                Point center = new Point(); double angle = 0;
                var monitor = screen;
                try
                {
                    image = FlightImage(target);
                    if (image != null)
                    {
                        center = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                        var a = target.PointToScreen(new Point(0, 0)); var b = target.PointToScreen(new Point(target.ActualWidth, 0));
                        angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                    }
                }
                catch (Exception ex) { store.Log("Prepare removal animation", ex); }
                remove(); // Commit first; a failed save must leave the original card visible.
                if (image == null || shuttingDown || !expanded) return;
                try
                {
                    var paper = new FallingPaper(image, center, angle, monitor) { Owner = this };
                    fallingPapers.Add(paper); LastPaper = paper; LastFlying = image; LastFlight = paper.Travel;
                    paper.Closed += delegate {
                        fallingPapers.Remove(paper);
                        if (Object.ReferenceEquals(LastPaper, paper)) { LastPaper = null; LastFlying = null; LastFlight = null; }
                        releasePreview();
                    };
                    keepUntil = DateTime.Now.AddMilliseconds(PaperFallMotion.Duration + 100); lastInside = DateTime.Now;
                    try { paper.Show(); animationRunning = true; } catch { paper.Close(); throw; }
                }
                catch (Exception ex) { store.Log("Play removal animation", ex); }
            }
            finally { if (!animationRunning) releasePreview(); }
        }
        void CloseFallingPapers()
        {
            foreach (var paper in fallingPapers.ToArray()) paper.Close();
        }
        internal void RemoveShot(Shot shot)
        {
            if (!store.Items.Contains(shot)) return;
            try { RemoveWithFlight(HangingElement(shot), delegate { store.Remove(shot); }); }
            catch (Exception ex) { store.Log("Remove screenshot", ex); Notify("未能移除，截图已保留，请稍后重试。"); }
        }
        internal void RemoveMemo(Memo note)
        {
            if (!store.Notes.Contains(note)) return;
            try { RemoveWithFlight(HangingElement(note), delegate { store.RemoveNote(note); }); }
            catch (Exception ex) { store.Log("Remove memo", ex); Notify("未能删除，便签已保留，请稍后重试。"); }
        }
        void SaveRemark(Shot shot, string text)
        {
            if (!String.IsNullOrWhiteSpace(shot.Remark) && String.IsNullOrWhiteSpace(text))
            {
                var card = HangingElement(shot) as Grid;
                var strip = card == null ? null : card.Children.OfType<Button>().FirstOrDefault();
                RemoveWithFlight(strip, delegate { store.UpdateRemark(shot, text); });
            }
            else store.UpdateRemark(shot, text);
        }
    }
}
