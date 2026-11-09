using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProjectionMapper;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.ViewModels;
using ProjectionMapper.Views;

/// <summary>Opt-in captures of actual WPF controls for the tutorial; never submits AI requests or projects patterns.</summary>
internal static class TutorialScreenshots
{
    private const string DirectoryName = "artifacts/ui-previews/tutorial";
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void SaveVisual(FrameworkElement content, string name)
    {
        var margin = content.Margin;
        double width = content.RenderSize.Width + margin.Left + margin.Right;
        double height = content.RenderSize.Height + margin.Top + margin.Bottom;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle((Brush)Application.Current.Resources["Ui.Background"], null, new Rect(0, 0, width, height));
            // Explicit bounds avoid scaling to ink bounds or re-laying out a nested control.
            var brush = new VisualBrush(content)
            {
                AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect((System.Windows.Point)VisualTreeHelper.GetOffset(content), content.RenderSize), Stretch = Stretch.Fill
            };
            context.DrawRectangle(brush, null, new Rect(margin.Left, margin.Top, content.RenderSize.Width, content.RenderSize.Height));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(DirectoryName, name + ".png")); encoder.Save(stream);
    }
    private static void SaveWindow(Window window, string name, double width, double height)
    {
        Drain();
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        SaveVisual(content, name);
    }
    public static void Capture(MainWindow main)
    {
        Directory.CreateDirectory(DirectoryName);
        UiThemeService.SetAppearance(UiAppearance.Dark, persist: false);
        // Enumerate the actual Windows capture machine. No projector identity or alignment is fabricated.
        typeof(MainWindow).GetMethod("EnumerateMonitors", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
        var displays = ((IEnumerable)typeof(MainWindow).GetField("_detectedDisplays", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).Cast<DisplayDescriptor>().ToList();
        var layer = new LayerViewModel(new LayerModel { Name = "Front panel", TargetMonitorIndex = -1 });
        var calibration = new CalibrationWindow(displays.Select(d => new CalibrationOutput(d.Index, d.DeviceName, d.RenderWidth, d.RenderHeight)).ToList(),
            layer, new UndoRedoService(), (_, _) => throw new InvalidOperationException("Documentation capture must not project."), () => { }, _ => 0);
        SaveWindow(calibration, "manual-calibration", 560, 880); calibration.Close();

        var photo = new PhotoMapWindow(new List<PhotoMappingModel>(), canCreateMeshes: false);
        SaveWindow(photo, "photo-map", 1200, 820); photo.Close();

        var artwork = new ArtworkWindow(() => new(), () => new(), (_, _, _) => throw new InvalidOperationException("Capture must not accept artwork."),
            (_, _) => Task.CompletedTask, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ProjectionMapper", "Artwork"),
            "minimal botanical illustration", "teal, warm ivory, deep navy", null);
        // Inputs only: no key, consent, generation, downloaded image, or successful provider result is fabricated.
        ((TextBox)artwork.FindName("Prompt")).Text = "Soft flowing botanical shapes, large simple forms, generous dark negative space, no lettering.";
        ((ComboBox)artwork.FindName("Variants")).SelectedIndex = 0;
        SaveWindow(artwork, "artwork-settings", 1180, 1000);
        Descendants<ScrollViewer>((FrameworkElement)artwork.Content).First().ScrollToBottom();
        SaveWindow(artwork, "artwork-download", 1180, 1000); artwork.Close();

        var surface = new SurfaceControlsWindow(new SurfaceAppearance(), null, _ => throw new InvalidOperationException("Capture must not preview on outputs."));
        SaveWindow(surface, "surface-controls", 930, 780); surface.Close();
        var colors = new OutputControlsWindow(displays.Select(d => new ArtistOutput(d.Index, d.DeviceName, d.DeviceName)).ToList(), new(), (_, _) => { });
        SaveWindow(colors, "projector-color", 570, 860); colors.Close();
        var cue = new CueControlsWindow(new PlaylistGroupModel { Name = "Opening" });
        SaveWindow(cue, "cue-settings", 460, 580); cue.Close();

        var project = new ProjectModel { Name = "Untitled project" };
        var hardware = new OutputLayout { ComputerId = WindowsDisplayIdentity.ComputerId, Displays = displays };
        var deployment = new DeploymentWindow(() => project, () => hardware, () => null,
            _ => throw new InvalidOperationException("Capture must not start rehearsal."), _ => Task.FromResult<RehearsalReport?>(null), () => false);
        SaveWindow(deployment, "preflight-rehearsal", 850, 730); deployment.Close();
        // The show strip and project header are actual controls, without a staged successful show.
        ((Panel)main.Content).Background = (Brush)Application.Current.Resources["Ui.Background"];
        SaveWindow(main, "workspace-controls", 1366, 860);
        var go = (FrameworkElement)main.FindName("PART_Go");
        var strip = (FrameworkElement)((FrameworkElement)go.Parent).Parent;
        SaveVisual(strip, "show-controls");
        Console.WriteLine("Tutorial screenshots captured from real WPF controls. No AI calls or physical projection performed.");
    }
}
