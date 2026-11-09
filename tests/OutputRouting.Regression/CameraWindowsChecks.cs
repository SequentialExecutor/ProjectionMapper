using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using ProjectionMapper.Models;
using ProjectionMapper.Rendering;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;
using ProjectionMapper.ViewModels;
using ProjectionMapper.Views;

internal static class CameraWindowsChecks
{
    private static void Check(bool value, string description) { if (!value) throw new Exception("Windows camera regression: " + description); }
    private static PhotoMappingModel Photo(Mat image)
    {
        byte[] jpeg = image.ImEncode(".jpg");
        return new() { Width = image.Width, Height = image.Height, ImageBase64 = Convert.ToBase64String(jpeg), ImageSha256 = Convert.ToHexString(SHA256.HashData(jpeg)), OriginalFileName = "synthetic-marker-test.jpg" };
    }
    public static void Run()
    {
        // Actual OpenCV generation/detection on explicitly synthetic image fixtures. This is not a real projector accuracy report.
        var pattern = CameraMarkerService.NewPattern(1280, 960, new(0, 0, 1280, 960));
        using var board = CameraMarkerService.Render(pattern);
        var detected = CameraMarkerService.Detect(pattern, Photo(board));
        Check(detected.Matches.Count == 9 && detected.DuplicateIds.Count == 0, "actual native dictionary generation and detection identify all session tags");
        var identity = PlanarCalibration.Fit(pattern, detected.Matches.ToList(), 3);
        Check(identity.Reliable && identity.ValidationMaxPixels < 1, "identity capture uses independent check tags");
        using var transform = Mat.FromArray(new double[,] { { 1.05, .05, 60 }, { .04, 1.03, 40 }, { .00005, -.00003, 1 } });
        using var photographed = new Mat();
        Cv2.WarpPerspective(board, photographed, transform, new Size(1600, 1200), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
        var perspective = CameraMarkerService.Detect(pattern, Photo(photographed));
        Check(perspective.Matches.Count == 9, "coded markers survive perspective and JPEG encoding");
        var fit = PlanarCalibration.Fit(pattern, perspective.Matches.ToList(), 3);
        Check(fit.Reliable && fit.ValidationMaxPixels < 2, "perspective marker centers meet synthetic withheld-point tolerance");
        var stale = pattern.Copy();
        int[] otherIds = Enumerable.Range(0, 250).Where(id => pattern.Markers.All(m => m.Id != id)).Take(9).ToArray();
        stale.Markers = stale.Markers.Select((m, i) => m with { Id = otherIds[i] }).ToList();
        Check(CameraMarkerService.Detect(stale, Photo(board)).Matches.Count == 0, "unrelated marker IDs cannot be silently reused as current correspondences");
        var quad = new[] { new Vector2(.3f, .3f), new Vector2(.7f, .3f), new Vector2(.3f, .7f), new Vector2(.7f, .7f) };
        var bitmap = CameraMarkerService.Bitmap(pattern); var outline = CameraMarkerService.Outline(pattern, quad);
        Check(bitmap.IsFrozen && outline.IsFrozen && bitmap.PixelWidth == 1280 && bitmap.PixelHeight == 960, "native-sized frozen pattern and outline frames");
        string export = Path.Combine(Path.GetTempPath(), "markers-" + Guid.NewGuid().ToString("N") + ".png");
        try { CameraMarkerService.ExportAsync(pattern, export).GetAwaiter().GetResult(); Check(File.ReadAllBytes(export).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "pattern export writes a real PNG"); }
        finally { File.Delete(export); }
        using var renderer = new SoftwareRenderer(); using var manager = new RendererManager(renderer);
        manager.SetCalibrationPattern(0, outline); manager.SetCameraCalibrationSession(true); manager.SetCameraCalibrationPattern(0, bitmap);
        var method = typeof(RendererManager).GetMethod("ResolveMonitorPresentation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        BitmapSource? Visible(int index) => (BitmapSource?)method.Invoke(manager, new object?[] { index, outline });
        Check(ReferenceEquals(Visible(0), bitmap) && !ReferenceEquals(Visible(1), outline), "camera capture isolates one projector and blanks other outputs");
        manager.SetBlackout(true); Check(!ReferenceEquals(Visible(0), bitmap), "blackout dominates camera patterns");
        manager.SetBlackout(false); manager.SetCameraCalibrationSession(false);
        Check(ReferenceEquals(Visible(0), outline), "closing camera session restores the underlying calibration state");
        var layer = new LayerViewModel(new() { Id = "fixture-mesh" }); int changes = 0;
        layer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LayerViewModel.OutputMeshPoints)) changes++; };
        layer.SetOutputQuad(quad); quad[0] = Vector2.Zero;
        Check(changes == 1 && layer.OutputMeshPoints[0] == new Vector2(.3f, .3f), "whole-quad assignment is atomic to observers and owns its input array");
        var output = new DisplayDescriptor(0, "fixture-display", "fixture-id", 0, 0, 1280, 960, 1280, 960, true);
        var window = new CameraCalibrationWindow(output, "fixture-computer", "fixture-mesh", "Synthetic plane", layer.OutputMeshPoints, () => true, _ => { });
        window.Close();
        Console.WriteLine("Windows native coded-marker detection, perspective/JPEG fixtures, capture isolation, quad assignment and camera window checks passed. No physical calibration claim.");
    }
}
