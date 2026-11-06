using System;
using Pen = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;
using FlowDirection = System.Windows.FlowDirection;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Rendering
{
    /// <summary>A native-pixel test grid; generate once on the UI thread and freeze for display.</summary>
    public static class CalibrationPattern
    {
        public static BitmapSource Create(int width, int height, int displayNumber, int spacing = 64)
        {
            if (width <= 0 || height <= 0 || spacing < 8) throw new ArgumentOutOfRangeException(nameof(width));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                var grid = new Pen(Brushes.DimGray, 1);
                for (int x = 0; x < width; x += spacing) dc.DrawLine(grid, new Point(x + .5, 0), new Point(x + .5, height));
                for (int y = 0; y < height; y += spacing) dc.DrawLine(grid, new Point(0, y + .5), new Point(width, y + .5));
                dc.DrawRectangle(null, new Pen(Brushes.White, 2), new Rect(1, 1, Math.Max(0, width - 2), Math.Max(0, height - 2)));
                dc.DrawLine(new Pen(Brushes.White, 1), new Point(width / 2.0, 0), new Point(width / 2.0, height));
                dc.DrawLine(new Pen(Brushes.White, 1), new Point(0, height / 2.0), new Point(width, height / 2.0));
                double size = Math.Clamp(Math.Min(width, height) / 22.0, 12, 60);
                void Label(string text, double x, double y, Brush color)
                {
                    var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), size, color, 1);
                    dc.DrawText(label, new Point(x, y));
                }
                Label("TL", 12, 12, Brushes.Red);
                Label("TR", Math.Max(12, width - size * 2), 12, Brushes.Lime);
                Label("BL", 12, Math.Max(12, height - size * 1.7), Brushes.DodgerBlue);
                Label("BR", Math.Max(12, width - size * 2), Math.Max(12, height - size * 1.7), Brushes.Yellow);
                Label($"Display {displayNumber}  |  {width} × {height}", width * .2, height * .4, Brushes.White);
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
