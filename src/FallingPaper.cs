using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Forms = System.Windows.Forms;

namespace Snapline
{
    sealed class FallPose
    {
        internal double X, Y, Angle, Time, Free, Hold;
    }

    // The approved preview's motion: a short release, gentle sway, then a quadratic fall.
    static class PaperFallMotion
    {
        internal const double Duration = 1200, ReleaseFraction = 250.0 / 1680;
        const int Steps = 360;
        const double Seconds = 1.43;
        static readonly FallPose[] samples = Build();
        internal static double Clamp(double value) { return Math.Max(0, Math.Min(1, value)); }
        static double Ease(double value) { value = Clamp(value); return value * value * (3 - 2 * value); }
        static double Mix(double a, double b, double t) { return a + (b - a) * t; }
        static FallPose[] Build()
        {
            var result = new FallPose[Steps + 1];
            double step = Seconds / Steps, x = 0, vx = 0;
            double angle = .075 * Math.Sin(Math.PI * .7), sway = -.35;
            result[0] = new FallPose { Angle = angle };
            for (int i = 1; i <= Steps; i++)
            {
                double time = (i - .5) * step;
                double wind = -340 * Math.Exp(-time * 2.2) + 360 * Math.Sin(time * 6) * Math.Exp(-time * .85);
                vx += (wind - vx * 2.6) * step;
                sway += (wind / 115 - 10.5 * (angle + .015) - 4 * sway) * step;
                x += vx * step; angle += sway * step;
                result[i] = new FallPose { X = x, Angle = angle };
            }
            return result;
        }
        internal static FallPose At(double progress, double distance)
        {
            progress = Clamp(progress);
            double hold = Clamp(progress / ReleaseFraction);
            double free = Clamp((progress - ReleaseFraction) / (1 - ReleaseFraction));
            double index = free * Steps;
            int first = (int)index, next = Math.Min(Steps, first + 1);
            return new FallPose {
                X = -3 * Ease(hold) + Mix(samples[first].X, samples[next].X, index - first),
                Y = distance * free * free,
                Angle = progress < ReleaseFraction ? .075 * Math.Sin(hold * Math.PI * .7) : Mix(samples[first].Angle, samples[next].Angle, index - first),
                Time = free * Seconds, Free = free, Hold = hold
            };
        }
        internal static Point Vertex(double u, double v, double width, double height, double progress, FallPose pose, double initialAngle)
        {
            double time = Clamp(progress) * 1680;
            double blend = Ease(pose.Time / .24), curlScale = Math.Min(1, height / 154);
            double delayed = Ease((time - (1 - u) * 95) / 180);
            double flutter = Math.Sin(pose.Time * 10 - 2.2 * u + 1.4 * v);
            double x = (u - .5) * width, y = (v - .5) * height;
            x += Mix(-5 * delayed, -4 * flutter, blend) * v * v * curlScale;
            double held = 29 * delayed * Math.Pow(v, 2.4) * (.15 + .85 * Math.Pow(u, 4));
            double loose = 7 * flutter * Math.Pow(v, 1.7) * (.3 + .7 * u);
            y -= Mix(held, loose, blend) * curlScale;
            x *= 1 - Mix(.01 * delayed, .035 * Math.Sin(pose.Time * 8 + u * 2), blend) * v;
            double z = blend * (6 * Math.Sin(Math.PI * u) * Math.Sin(Math.PI * v + pose.Time * 9) +
                7 * Math.Sin(pose.Time * 8 + u * 3) * v * v) * curlScale;
            double perspective = 700 / (700 - z), angle = pose.Angle + initialAngle;
            return new Point((x * Math.Cos(angle) - y * Math.Sin(angle)) * perspective,
                (x * Math.Sin(angle) + y * Math.Cos(angle)) * perspective);
        }
    }

    // A narrow, click-through surface lets the paper fall below the shelf's 270-DIP window.
    sealed class FallingPaper : Window
    {
        internal readonly Image Image;
        internal readonly TranslateTransform Travel = new TranslateTransform();
        internal double Progress;
        readonly BitmapSource bitmap;
        readonly Point anchor;
        readonly double initialAngle;
        readonly Forms.Screen screen;
        readonly Stopwatch watch = new Stopwatch();
        readonly MeshGeometry3D mesh = new MeshGeometry3D();
        readonly Viewport3D viewport = new Viewport3D();
        const int Columns = 16, Rows = 12;
        double centerX, centerY, distance, lastFrame = -100;
        bool rendering;
        internal FallingPaper(Image image, Point center, double angle, Forms.Screen monitor)
        {
            Image = image; bitmap = (BitmapSource)image.Source; anchor = center;
            initialAngle = angle; screen = monitor;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; ShowActivated = false; Topmost = true; IsHitTestVisible = false;
            Width = image.Width + 110; Height = screen.WorkingArea.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Content = viewport;
            var texture = new ImageBrush(bitmap) { Stretch = Stretch.Fill }; texture.Freeze();
            var material = new DiffuseMaterial(texture); material.Freeze();
            var model = new Model3DGroup();
            model.Children.Add(new AmbientLight(Colors.White));
            model.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
            viewport.Children.Add(new ModelVisual3D { Content = model });
            for (int j = 0; j <= Rows; j++) for (int i = 0; i <= Columns; i++)
                mesh.TextureCoordinates.Add(new Point(i / (double)Columns, j / (double)Rows));
            for (int j = 0; j < Rows; j++) for (int i = 0; i < Columns; i++)
            {
                int a = j * (Columns + 1) + i, b = a + 1, c = a + Columns + 1, d = c + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(d);
            }
            SourceInitialized += delegate {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                Native.SetWindowLong(handle, -20, Native.GetWindowLong(handle, -20) | 0x08000000 | 0x80 | 0x20);
                Native.SetWindowPos(handle, new IntPtr(-1), screen.WorkingArea.Left, screen.WorkingArea.Top, 1, 1, 0x0010);
            };
            Loaded += delegate { Position(); Draw(0); watch.Start(); rendering = true; CompositionTarget.Rendering += Frame; };
            Closed += delegate { if (rendering) CompositionTarget.Rendering -= Frame; rendering = false; watch.Stop(); viewport.Children.Clear(); Image.Source = null; };
        }
        void Position()
        {
            var area = screen.WorkingArea; IntPtr handle = new WindowInteropHelper(this).Handle;
            double dpi = 1;
            for (int i = 0; i < 2; i++)
            {
                try { uint value = Native.GetDpiForWindow(handle); if (value > 0) dpi = value / 96.0; } catch (EntryPointNotFoundException) { }
                int width = Math.Min(area.Width, (int)Math.Ceiling((Image.Width + 110) * dpi));
                int left = Math.Max(area.Left, Math.Min(area.Right - width, (int)Math.Round(anchor.X - width / 2.0)));
                Native.SetWindowPos(handle, new IntPtr(-1), left, area.Top, width, area.Height, 0x0010);
                centerX = (anchor.X - left) / dpi; centerY = (anchor.Y - area.Top) / dpi;
                Width = width / dpi; Height = area.Height / dpi;
            }
            distance = Height - centerY + Image.Height * .65 + 24;
            viewport.Camera = new OrthographicCamera(new Point3D(0, 0, 500), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), Width);
        }
        void Frame(object sender, EventArgs args)
        {
            double elapsed = watch.Elapsed.TotalMilliseconds;
            if (elapsed - lastFrame < 1000.0 / 60 && elapsed < PaperFallMotion.Duration) return;
            lastFrame = elapsed; Draw(elapsed / PaperFallMotion.Duration);
            if (Progress >= 1) Close();
        }
        void Draw(double progress)
        {
            Progress = PaperFallMotion.Clamp(progress);
            var pose = PaperFallMotion.At(Progress, distance);
            Travel.X = pose.X; Travel.Y = pose.Y;
            var points = new Point3DCollection((Columns + 1) * (Rows + 1));
            for (int j = 0; j <= Rows; j++) for (int i = 0; i <= Columns; i++)
            {
                var point = PaperFallMotion.Vertex(i / (double)Columns, j / (double)Rows, Image.Width, Image.Height, Progress, pose, initialAngle);
                points.Add(new Point3D(centerX + pose.X + point.X - Width / 2, Height / 2 - centerY - pose.Y - point.Y, 0));
            }
            mesh.Positions = points;
        }
        internal void Snapshot(string path)
        {
            viewport.UpdateLayout();
            var target = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
            target.Render(viewport); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            using (var output = System.IO.File.Create(path)) encoder.Save(output);
        }
    }
}
