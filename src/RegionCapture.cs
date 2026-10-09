using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Snapline
{
    // The result belongs to this capture session; no clipboard-source guessing is needed.
    static class RegionCapture
    {
        internal static BitmapSource Select()
        {
            var desktop = Forms.SystemInformation.VirtualScreen;
            if (desktop.Width <= 0 || desktop.Height <= 0 || (long)desktop.Width * desktop.Height > 50000000)
                throw new InvalidOperationException("屏幕总尺寸过大，无法开始框选截图。");
            using (var pixels = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(pixels))
                    graphics.CopyFromScreen(desktop.Location, Point.Empty, desktop.Size, CopyPixelOperation.SourceCopy);
                using (var selection = new SelectionWindow(pixels, desktop))
                {
                    if (selection.ShowDialog() != Forms.DialogResult.OK) return null;
                    using (var cropped = pixels.Clone(selection.Selection, PixelFormat.Format32bppArgb))
                    using (var stream = new MemoryStream())
                    {
                        cropped.Save(stream, ImageFormat.Png);
                        stream.Position = 0;
                        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                        image.Freeze();
                        return image;
                    }
                }
            }
        }
        sealed class SelectionWindow : Forms.Form
        {
            readonly Bitmap pixels;
            readonly Rectangle desktop;
            readonly Font hintFont;
            Point anchor;
            bool selecting;
            internal Rectangle Selection;
            internal SelectionWindow(Bitmap snapshot, Rectangle bounds)
            {
                pixels = snapshot; desktop = bounds;
                Text = "截图晾台 · 框选截图";
                FormBorderStyle = Forms.FormBorderStyle.None;
                AutoScaleMode = Forms.AutoScaleMode.None;
                StartPosition = Forms.FormStartPosition.Manual;
                Bounds = bounds;
                ShowInTaskbar = false; TopMost = true;
                DoubleBuffered = true; KeyPreview = true;
                Cursor = Forms.Cursors.Cross;
                Native.Point pointer; Native.GetCursorPos(out pointer);

                float scale = 1;
                try { scale = Native.GetDpiForWindow(Handle) / 96f; } catch (EntryPointNotFoundException) { }
                hintFont = new Font("Microsoft YaHei UI", Math.Max(16, 16 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
                Shown += delegate
                {
                    // Use physical desktop pixels, including monitors with negative origins.
                    Native.SetWindowPos(Handle, new IntPtr(-1), desktop.Left, desktop.Top, desktop.Width, desktop.Height, 0x0040);
                    Activate(); Focus();
                };
            }
            Point Pointer()
            {
                Native.Point pointer; Native.GetCursorPos(out pointer);
                return new Point(Math.Max(0, Math.Min(desktop.Width, pointer.X - desktop.Left)),
                    Math.Max(0, Math.Min(desktop.Height, pointer.Y - desktop.Top)));
            }
            Point EventPoint(Forms.MouseEventArgs e)
            {
                return new Point(Math.Max(0, Math.Min(desktop.Width, e.X)), Math.Max(0, Math.Min(desktop.Height, e.Y)));
            }
            protected override void OnMouseDown(Forms.MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == Forms.MouseButtons.Right) { DialogResult = Forms.DialogResult.Cancel; return; }
                if (e.Button != Forms.MouseButtons.Left) return;
                anchor = EventPoint(e); selecting = true; Capture = true;
                Selection = Rectangle.Empty; Invalidate();
            }
            protected override void OnMouseMove(Forms.MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!selecting) return;
                var end = EventPoint(e);
                Selection = Rectangle.FromLTRB(Math.Min(anchor.X, end.X), Math.Min(anchor.Y, end.Y),
                    Math.Max(anchor.X, end.X), Math.Max(anchor.Y, end.Y));
                Invalidate();
            }
            protected override void OnMouseUp(Forms.MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (!selecting || e.Button != Forms.MouseButtons.Left) return;
                OnMouseMove(e); selecting = false; Capture = false;
                if (Selection.Width < 2 || Selection.Height < 2) { Selection = Rectangle.Empty; Invalidate(); return; }
                DialogResult = Forms.DialogResult.OK;
            }
            protected override bool ProcessCmdKey(ref Forms.Message message, Forms.Keys key)
            {
                if (key == Forms.Keys.Escape) { DialogResult = Forms.DialogResult.Cancel; return true; }
                return base.ProcessCmdKey(ref message, key);
            }
            protected override void OnPaint(Forms.PaintEventArgs e)
            {
                e.Graphics.DrawImageUnscaled(pixels, Point.Empty);
                using (var shade = new SolidBrush(Color.FromArgb(105, 0, 0, 0))) e.Graphics.FillRectangle(shade, ClientRectangle);
                if (Selection.Width > 0 && Selection.Height > 0)
                {
                    e.Graphics.DrawImage(pixels, Selection, Selection, GraphicsUnit.Pixel);
                    using (var border = new Pen(Color.FromArgb(114, 199, 250), 2))
                        e.Graphics.DrawRectangle(border, Selection.X, Selection.Y, Selection.Width - 1, Selection.Height - 1);
                }
                var pointer = Pointer();
                string hint = selecting ? Selection.Width + " × " + Selection.Height + "   松开鼠标完成 · Esc 取消" : "拖动选择截图区域 · Esc / 右键取消";
                SizeF size = e.Graphics.MeasureString(hint, hintFont);
                int width = (int)Math.Ceiling(size.Width) + 24, height = (int)Math.Ceiling(size.Height) + 18;
                int x = Math.Max(8, Math.Min(desktop.Width - width - 8, pointer.X + 18));
                int y = Math.Max(8, Math.Min(desktop.Height - height - 8, pointer.Y + 22));
                using (var background = new SolidBrush(Color.FromArgb(230, 25, 42, 57))) e.Graphics.FillRectangle(background, x, y, width, height);
                e.Graphics.DrawString(hint, hintFont, Brushes.White, x + 12, y + 9);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) hintFont.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}