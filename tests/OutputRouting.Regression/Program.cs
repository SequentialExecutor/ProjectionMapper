using System;
using System.Collections.Concurrent;
using System.Collections;
using ProjectionMapper.Services;
using ProjectionMapper.Models;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper.Rendering;

// Exercise the actual routing manager without opening windows or requiring projectors.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            UiThemeService.Initialize(Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }, loadPreference: false);
            var preview = new RecordingRenderer();
            using var manager = new RendererManager(preview);
            var outputs = (ConcurrentDictionary<int, IRenderer>)typeof(RendererManager)
                .GetField("_monitorRenderers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(manager)!;
            var first = new RecordingRenderer();
            var second = new RecordingRenderer();
            outputs[0] = first;
            outputs[1] = second;
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255 }, 4);
            image.Freeze();
            var bounds = new Rect(0, 0, 1, 1);

            // Two meshes may share the exact same source frame but have different outputs.
            manager.SubmitLayerFrameForMonitor("a", image, bounds, null, 1, 0);
            manager.SubmitLayerFrameForMonitor("b", image, bounds, null, 1, 1);
            Check(first.Has("a") && !first.Has("b"), "first output isolation");
            Check(second.Has("b") && !second.Has("a"), "second output isolation");
            Check(preview.Has("a") && preview.Has("b"), "combined preview");

            manager.SubmitLayerFrameForMonitor("a", image, bounds, null, 1, 1);
            Check(!first.Has("a") && second.Has("a") && second.Has("b"), "reassignment clears old output only");
            manager.SubmitLayerFrameForMonitor("a", image, bounds, null, 1, -1);
            Check(!first.Has("a") && !second.Has("a") && preview.Has("a"), "None means preview only");
            manager.SubmitLayerFrameForMonitor("b", image, bounds, null, 1, 99);
            Check(!second.Has("b") && preview.Has("b"), "unavailable target clears old output");
            manager.SubmitLayerFrameForMonitor("a", null, bounds, null, 0, -1);
            Check(!preview.Has("a"), "null frame clears preview");

            // Inactive playlist sources must stay hidden when a cached mesh is reassigned.
            using (var video = new VideoService(manager))
            {
                var mesh = new LayerModel { Id = "inactive", SourceId = "source", TargetMonitorIndex = 1 };
                var meshes = (ConcurrentDictionary<string, LayerModel>)typeof(VideoService)
                    .GetField("_meshLayers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(video)!;
                meshes[mesh.Id] = mesh;
                var decoderField = typeof(VideoService).GetField("_decoders", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var cachedType = decoderField.FieldType.GetGenericArguments()[1];
                var cached = Activator.CreateInstance(cachedType,
                    new object?[] { null, null, new LayerModel { Id = "source" }, image, true, TimeSpan.Zero });
                ((IDictionary)decoderField.GetValue(video)!).Add("source", cached);
                manager.SubmitLayerFrameForMonitor(mesh.Id, image, bounds, null, 1, 0);
                video.EnablePlaylistMode();
                video.RefreshMeshLayerRendering(mesh.Id);
                Check(!first.Has(mesh.Id) && !second.Has(mesh.Id) && !preview.Has(mesh.Id),
                    "inactive playlist refresh clears retained output without restoring cached image");
            }

            // Verify the real software renderer releases null-submitted layer entries.
            using var software = new SoftwareRenderer();
            software.SubmitLayerFrame("a", image, bounds, null, 1);
            software.SubmitLayerFrame("a", null, bounds, null, 0);
            var layers = typeof(SoftwareRenderer).GetField("_layers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(software)!;
            Check((int)layers.GetType().GetProperty("Count")!.GetValue(layers)! == 0, "software renderer removes cleared entries");
            // Rectangular content must scale into the preview even without a warp quad.
            manager.ResizeAsync(128, 72).GetAwaiter().GetResult();
            var sizes = (ConcurrentDictionary<int, (int Width, int Height)>)typeof(RendererManager)
                .GetField("_monitorRendererSizes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
            sizes[0] = (1920, 1080);
            manager.SubmitLayerFrameForMonitor("rectangle", image, new Rect(960, 540, 192, 108), null, 1, 0);
            Check(Math.Abs(preview.LastRectangle.X - 64) < .001 && Math.Abs(preview.LastRectangle.Width - 12.8) < .001,
                "rectangle scales to preview dimensions without requiring a quad");
            ArtworkWindowsChecks.Run();
            ScenePhotoChecks.Run();
            RenderingChecks.Run();
            ArtistWindowsChecks.Run();
            DeploymentWindowsChecks.Run();
            CameraWindowsChecks.Run();
            WorkspaceWindowsChecks.Run();
            Console.WriteLine("Output routing regression checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + scenario);
    }

    private sealed class RecordingRenderer : IRenderer
    {
        private readonly ConcurrentDictionary<string, BitmapSource> frames = new();
        public event Action<BitmapSource?>? FrameReady;
        public Rect LastRectangle { get; private set; }
        public bool Has(string id) => frames.ContainsKey(id);
        public void SubmitLayerFrame(string id, BitmapSource? frame, Rect rect, Point[]? quad, double opacity)
        {
            LastRectangle = rect;
            if (frame == null) frames.TryRemove(id, out _);
            else frames[id] = frame;
        }
        public Task InitializeAsync(int width, int height, CancellationToken token = default) => Task.CompletedTask;
        public Task ResizeAsync(int width, int height, CancellationToken token = default) => Task.CompletedTask;
        public Task RenderFrameAsync(CancellationToken token = default)
        {
            FrameReady?.Invoke(null);
            return Task.CompletedTask;
        }
        public void Dispose() => frames.Clear();
    }
}
