using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper.Rendering;

internal static class RenderingChecks
{
    public static void Run()
    {
        using var renderer = new SoftwareRenderer();
        var method = typeof(SoftwareRenderer).GetMethod("WarpBitmapToQuad", BindingFlags.NonPublic | BindingFlags.Instance)!;
        BitmapSource Warp(BitmapSource source, Point[] quad, int width, int height) =>
            (BitmapSource)method.Invoke(renderer, new object[] { source, quad, 0, 0, width, height })!;
        static BitmapSource Image(int width, int height, byte[] pixels)
        {
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            image.Freeze();
            return image;
        }
        static byte[] Pixels(BitmapSource image)
        {
            var data = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(data, image.PixelWidth * 4, 0);
            return data;
        }
        static void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException("Rendering regression: " + description);
        }

        var colors = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 };
        var image = Image(2, 2, colors);
        var identity = new[] { new Point(0, 0), new Point(2, 0), new Point(0, 2), new Point(2, 2) };
        Check(Pixels(Warp(image, identity, 2, 2)).SequenceEqual(colors), "identity warp preserves texel centers");

        var trapezoid = new[] { new Point(0, 0), new Point(100, 0), new Point(20, 100), new Point(80, 100) };
        var warped = Pixels(Warp(image, trapezoid, 100, 100));
        Check(warped[(99 * 100 + 19) * 4 + 3] == 0, "pixels outside quad stay transparent");
        Check(warped[(50 * 100 + 50) * 4 + 3] == 255, "quad interior is rendered");

        // Transparent green must not tint interpolated opaque red.
        var alpha = Image(2, 1, new byte[] { 0, 0, 255, 255, 0, 255, 0, 0 });
        var alphaQuad = new[] { new Point(0, 0), new Point(4, 0), new Point(0, 2), new Point(4, 2) };
        var sampled = Pixels(Warp(alpha, alphaQuad, 4, 2));
        Check(sampled[2 * 4 + 1] == 0 && sampled[2 * 4 + 2] == 255 && sampled[2 * 4 + 3] == 64,
            "alpha-aware bilinear sampling avoids colored fringes");

        renderer.InitializeAsync(32, 32).GetAwaiter().GetResult();
        BitmapSource? output = null;
        renderer.FrameReady += bitmap => output = bitmap;
        void CheckBlack(string description)
        {
            renderer.RenderFrameAsync().GetAwaiter().GetResult();
            Check(output != null, "composited frame emitted");
            var data = Pixels(output!);
            Check(Enumerable.Range(0, 32 * 32).All(i => data[i * 4] == 0 && data[i * 4 + 1] == 0 && data[i * 4 + 2] == 0), description);
        }
        renderer.SubmitLayerFrame("test", image, new Rect(0, 0, 32, 32),
            new[] { new Point(0, 0), new Point(32, 32), new Point(0, 32), new Point(32, 0) }, 1);
        CheckBlack("crossed quad does not fall back to rectangular projection");
        renderer.SubmitLayerFrame("test", image, new Rect(0, 0, 32, 32), new Point[4], 1);
        CheckBlack("degenerate quad does not render");
        renderer.SubmitLayerFrame("test", image, new Rect(0, 0, 32, 32),
            new[] { new Point(100, 100), new Point(132, 100), new Point(100, 132), new Point(132, 132) }, 1);
        CheckBlack("offscreen quad does not render");
        var unsupported = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr24, null,
            new byte[] { 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255 }, 6);
        unsupported.Freeze();
        renderer.SubmitLayerFrame("test", unsupported, new Rect(0, 0, 32, 32),
            new[] { new Point(0, 0), new Point(28, 0), new Point(2, 28), new Point(26, 28) }, 1);
        CheckBlack("failed warp does not leak unwarped source");
        // The renderer owns the submitted geometry even if the caller reuses its array.
        var submitted = new[] { new Point(0, 0), new Point(32, 0), new Point(0, 32), new Point(32, 32) };
        renderer.SubmitLayerFrame("test", image, new Rect(0, 0, 32, 32), submitted, 1);
        Array.Fill(submitted, new Point(0, 0));
        renderer.RenderFrameAsync().GetAwaiter().GetResult();
        Check(Pixels(output!).Where((_, i) => i % 4 != 3).Any(value => value != 0), "submission snapshots mutable quad coordinates");
        Check(renderer.LastRenderMilliseconds > 0, "actual render timing recorded");
        var pattern = CalibrationPattern.Create(320, 180, 2, 32);
        Check(pattern.IsFrozen && pattern.PixelWidth == 320 && pattern.PixelHeight == 180, "native-resolution immutable calibration pattern");
        Console.WriteLine("Rendering calibration checks passed.");
    }
}
