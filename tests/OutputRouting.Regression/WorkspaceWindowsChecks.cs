using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper;
using ProjectionMapper.Services;

internal static class WorkspaceWindowsChecks
{
    private static Slider? FindSliderOrNull(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Slider slider && System.Windows.Automation.AutomationProperties.GetName(slider) == "Source preview zoom") return slider;
            var nested = FindSliderOrNull(child);
            if (nested != null) return nested;
        }
        return null;
    }
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
                var sourceCanvas = (FrameworkElement)window.FindName("PART_InputCanvas");
                var logicalWidth = sourceCanvas.Width;
                var slider = FindSliderOrNull(content) ?? throw new InvalidOperationException("Workspace preview slider is missing.");
                slider.Value = 1;
                Slider.IncreaseSmall.Execute(null, slider);
                if (slider.Value <= 1) throw new InvalidOperationException("Slider keyboard command did not change its value.");
                slider.ApplyTemplate();
                var track = (Track)slider.Template.FindName("PART_Track", slider);
                content.UpdateLayout();
                if (Math.Abs(track.Value - slider.Value) > .001 || track.ValueFromDistance(10, 0) <= 0)
                    throw new InvalidOperationException("Slider thumb and value must remain connected.");
                slider.Value = 1;
                if (sourceCanvas.Width != logicalWidth) throw new InvalidOperationException("Preview zoom changed native source dimensions.");
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                image.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create($"artifacts/ui-previews/{appearance.ToString().ToLowerInvariant()}-{size.Width}.png");
                encoder.Save(stream);
            }
        }
        UiThemeService.SetAppearance(UiAppearance.Dark, persist: false);
        if (Environment.GetEnvironmentVariable("PROJECTIONMAPPER_CAPTURE_TUTORIAL") == "1") TutorialScreenshots.Capture(window);
        Console.WriteLine("Workspace layout and appearance checks passed; actual WPF previews saved.");
    }
}
