using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper;
using ProjectionMapper.Services;

internal static class WorkspaceWindowsChecks
{
    public static void Run()
    {
        if (Math.Abs(WorkspaceViewMath.FitZoom(968, 548, 1920, 1080) - .5) > .001)
            throw new InvalidOperationException("Fit must preserve the media aspect ratio.");
        // Do not show the window: Loaded starts physical output enumeration and recovery UI.
        var window = new MainWindow();
        var content = (FrameworkElement)window.Content;
        Directory.CreateDirectory("artifacts/ui-previews");
        foreach (var appearance in new[] { UiAppearance.Dark, UiAppearance.Light })
        {
            UiThemeService.SetAppearance(appearance, persist: false);
            var background = (SolidColorBrush)Application.Current.Resources["Ui.Background"];
            ((Panel)content).Background = background;
            foreach (var size in new[] { new Size(1366, 860), new Size(1024, 680) })
            {
                content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                foreach (var name in new[] { "PART_Go", "PART_Blackout", "PART_Performance", "PART_InputScroll", "PART_OutputScroll" })
                {
                    var element = (FrameworkElement)window.FindName(name);
                    var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                    if (bounds.Width < 20 || bounds.Height < 20 || bounds.Left < -1 || bounds.Right > size.Width + 1 || bounds.Bottom > size.Height + 1)
                        throw new InvalidOperationException($"Workspace control clipped: {name} at {size}: {bounds}");
                }
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                image.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create($"artifacts/ui-previews/{appearance.ToString().ToLowerInvariant()}-{size.Width}.png");
                encoder.Save(stream);
            }
        }
        UiThemeService.SetAppearance(UiAppearance.Dark, persist: false);
        Console.WriteLine("Workspace layout and appearance checks passed; actual WPF previews saved.");
    }
}
