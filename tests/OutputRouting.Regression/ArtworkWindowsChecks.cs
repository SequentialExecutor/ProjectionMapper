using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProjectionMapper.Models;
using ProjectionMapper.Rendering;
using ProjectionMapper.Services;
using ProjectionMapper.Views;

internal static class ArtworkWindowsChecks
{
    public static void Run()
    {
        static void Check(bool value, string description) { if (!value) throw new Exception("Windows artwork check: " + description); }
        // VideoService queues renderer work onto the real WPF application dispatcher.
        // A console test must provide and drain that dispatcher before inspecting output.
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        static void DrainUi()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        var request = new ArtworkRequest(ArtworkProvider.OpenAI, "gpt-image-1.5", "Flowers", "watercolor", "gold", "1024x1024", "medium", "opaque", 1,
            new() { new("mesh", "Wall", ArtworkRegion.Full) }, ArtworkFixture.Png, new());
        var asset = ArtworkImaging.OnStaAsync(() => ArtworkImaging.Normalize(new(ArtworkFixture.Png, "Flowers", "test-request"), request)).GetAwaiter().GetResult();
        var image = ArtworkImaging.Crop(asset, ArtworkRegion.Full);
        Check(image.IsFrozen && image.Format == PixelFormats.Bgra32 && image.PixelWidth == 4, "PNG normalization yields immutable renderer-compatible pixels");
        var half = ArtworkImaging.Crop(asset, new(0, 0, .5, 1));
        Check(half.PixelWidth == 2 && half.PixelHeight == 3, "unwarped image cell is cropped before projection");
        string folder = Path.Combine(Path.GetTempPath(), "Artwork-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = ArtworkImaging.DownloadAsync(asset, folder).GetAwaiter().GetResult();
            Check(File.Exists(path), "completed image downloads to chosen folder");
            File.WriteAllText(path, "artist edited this file");
            var second = ArtworkImaging.DownloadAsync(asset, folder).GetAwaiter().GetResult();
            Check(second != path && File.ReadAllText(path) == "artist edited this file", "repeat downloads preserve externally edited files");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        var recorder = new RecordingRenderer();
        using var manager = new RendererManager(recorder);
        manager.ResizeAsync(16, 16).GetAwaiter().GetResult();
        using var video = new VideoService(manager);
        string sourceId = "offline-artwork-source";
        var source = new LayerModel { Id = sourceId, SourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4"), PreviewOnly = true, Width = 16, Height = 16 };
        var mesh = new LayerModel { Id = "mesh", SourceId = sourceId, ArtworkAssetId = asset.Id, TargetMonitorIndex = -1 };
        manager.SetArtwork("mesh", image);
        video.RegisterMeshLayerAsync(mesh).GetAwaiter().GetResult();
        video.ConfigureStillFallback(sourceId, image, 1);
        Check(video.RegisterLayerAsync(source).GetAwaiter().GetResult(), "source registers offline without FFmpeg or original video");
        Check(video.WaitForDecodersReadyAsync(new[] { sourceId }, 100).GetAwaiter().GetResult(), "still source is immediately ready for playlists");
        video.RefreshMeshLayerRendering("mesh"); DrainUi();
        Check(ReferenceEquals(recorder.Frame, image), "saved artwork renders through original mapping");
        manager.SetArtwork("mesh", half, preview: true);
        video.RefreshMeshLayerRendering("mesh"); DrainUi();
        Check(ReferenceEquals(recorder.Frame, half), "temporary preview overrides permanent artwork");
        manager.TryGetArtwork("mesh", out var permanent, includePreview: false);
        Check(ReferenceEquals(permanent, image), "undo snapshots do not capture temporary previews");
        manager.ClearArtwork(previewsOnly: true); video.RefreshMeshLayerRendering("mesh"); DrainUi();
        Check(ReferenceEquals(recorder.Frame, image), "clearing preview restores permanent image");
        video.EnablePlaylistMode(); video.RefreshMeshLayerRendering("mesh"); DrainUi();
        Check(recorder.Frame == null, "inactive playlist groups cannot resurrect saved artwork");
        video.SetActiveRenderLayers(new[] { sourceId }); video.RefreshMeshLayerRendering("mesh"); DrainUi();
        Check(recorder.Frame != null, "active artwork cue displays");
        video.PauseLayerAsync(sourceId).GetAwaiter().GetResult();
        video.ResumeLayerAsync(sourceId).GetAwaiter().GetResult();
        var decoders = (IDictionary)typeof(VideoService).GetField("_decoders", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(video)!;
        var entry = decoders[sourceId]!;
        Check(entry.GetType().GetField("Item1")!.GetValue(entry) == null, "pause/resume of offline artwork does not create a video decoder");
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        video.VideoCompleted += id => { if (id == sourceId) completed.TrySetResult(true); };
        video.DisableLoopingForAll(); video.StartGroupVideosAsync(new() { sourceId }).GetAwaiter().GetResult();
        completed.Task.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
        video.StopAllAsync().GetAwaiter().GetResult();
        // Empty-library construction exercises XAML/provider switching without remote calls or secret writes.
        var window = new ArtworkWindow(() => new(), () => new(), (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask,
            Path.GetTempPath(), string.Empty, string.Empty, null);
        window.Close();
        Console.WriteLine("Windows artwork normalization, downloads, preview restoration, offline playback, and window checks passed.");
    }
    private sealed class RecordingRenderer : IRenderer
    {
        public BitmapSource? Frame;
        public event Action<BitmapSource?>? FrameReady;
        public void SubmitLayerFrame(string id, BitmapSource? frame, Rect rect, Point[]? quad, double opacity) { if (id == "mesh") Frame = frame; }
        public Task InitializeAsync(int width, int height, CancellationToken token = default) => Task.CompletedTask;
        public Task ResizeAsync(int width, int height, CancellationToken token = default) => Task.CompletedTask;
        public Task RenderFrameAsync(CancellationToken token = default) { FrameReady?.Invoke(Frame); return Task.CompletedTask; }
        public void Dispose() { }
    }
}
