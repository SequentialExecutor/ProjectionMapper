using System.Numerics;
using ProjectionMapper.Utilities;
using System.Text.Json;
using ProjectionMapper.Models;
using ProjectionMapper.Services;

var directory = Path.Combine(Path.GetTempPath(), "ProjectionMapper-persistence-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var service = new ProjectService();
    var path = Path.Combine(directory, "show.pmproj");
    var media = Path.Combine(directory, "media", "video.mp4");
    var project = new ProjectModel { Name = "First", InputZoom = 1.25, OutputZoom = 2, PlaylistMode = true };
    var surface = new SurfaceModel { Id = "surface", Transform = Matrix3x2.CreateTranslation(10, 20) };
    surface.Layers.Add(new LayerModel { Id = "layer", SourcePath = media });
    surface.Layers[0].MeshPoints[0] = new Vector2(.12f, .23f);
    project.Surfaces.Add(surface);
    project.ImportedVideos.Add(new ImportedVideoData { Id = "video", SourcePath = media,
        TargetMonitorIndex = 2, PlayAudio = true, MeshLayers = new() { new MeshLayerData { Id = "mesh", SourceId = "video", TargetMonitorIndex = 1, IsLocked = true, Opacity = .65, Visible = false } } });
    project.PlaylistGroups.Add(new PlaylistGroupModel { Name = "Cue", SourceIds = new() { "video" }, PlaybackMode = GroupPlaybackMode.Simultaneous });
    Check(await service.SaveAsync(project, path), "first save");
    Check(project.ImportedVideos[0].SourcePath == media, "save leaves editor paths absolute");
    var loaded = (await service.LoadAsync(path))!;
    Check(loaded != null && loaded.Name == "First", "load");
    Check(loaded!.Surfaces.Count == 1 && loaded.Surfaces[0].Layers.Count == 1, "getter-only collections restored");
    Check(loaded.Surfaces[0].Transform.M31 == 10 && loaded.Surfaces[0].Layers[0].MeshPoints[0].X == .12f, "numeric fields and mesh points");
    Check(loaded.PlaylistGroups[0].PlaybackMode == GroupPlaybackMode.Simultaneous && loaded.ImportedVideos[0].MeshLayers[0].TargetMonitorIndex == 1, "playlist and output assignment");
    Check(loaded.ImportedVideos[0].SourcePath == media, "relative media resolves against project directory");
    using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        Check(!Path.IsPathRooted(json.RootElement.GetProperty("ImportedVideos")[0].GetProperty("SourcePath").GetString()!), "disk media paths are relative");
    project.Name = "Second";
    Check(await service.SaveAsync(project, path), "replace save");
    Check((await service.LoadAsync(path + ".bak"))!.Name == "First", "backup contains previous valid project");
    var original = await File.ReadAllBytesAsync(path);
    var modified = project.LastModified;
    project.InputZoom = double.NaN;
    Check(!await service.SaveAsync(project, path), "invalid save fails");
    Check(original.SequenceEqual(await File.ReadAllBytesAsync(path)) && project.LastModified == modified, "failed save preserves original and metadata");
    project.InputZoom = 1;
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        try { await service.SaveAsync(project, path, cancellation.Token); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { }
    }
    Check(original.SequenceEqual(await File.ReadAllBytesAsync(path)), "cancel preserves primary");
    await File.WriteAllTextAsync(path, "{broken");
    Check(await service.LoadAsync(path) == null && service.LastError != null, "corrupt project produces actionable failure");
    project.Name = "Recovered";
    Check(await service.SaveAsync(project, path), "save over damaged primary");
    Check((await service.LoadAsync(path + ".bak"))!.Name == "First", "damaged primary cannot overwrite good backup");
    Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "no staging-file leaks");
    await File.WriteAllTextAsync(path, "{\"ProjectVersion\":999}");
    Check(await service.LoadAsync(path) == null && service.LastError!.Contains("unsupported"), "reject future versions");
    await File.WriteAllTextAsync(path, "{\"ProjectVersion\":1,\"Name\":\"Legacy\"}");
    Check((await service.LoadAsync(path))!.Name == "Legacy", "version 1 remains readable");
    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.SaveAsync(project, path)));
    Check(results.All(x => x) && await service.LoadAsync(path) != null, "overlapping saves remain valid");
    var recoveryDirectory = Path.Combine(directory, "Recovery");
    await using (var recovery = new ProjectRecoveryService(recoveryDirectory))
    {
        string? failure = null;
        recovery.Start(TimeSpan.FromMilliseconds(20), () => Task.FromResult<ProjectModel?>(project), message => failure = message);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!Directory.EnumerateFiles(recoveryDirectory, "*.pmproj").Any() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Check(Directory.EnumerateFiles(recoveryDirectory, "*.pmproj").Any() && failure == null, "background snapshot written");
        await using var observer = new ProjectRecoveryService(recoveryDirectory);
        Check(observer.FindLatestRecoverable() == null, "running sessions are excluded from recovery");
    }
    Check(!Directory.EnumerateFiles(recoveryDirectory, "*.pmproj").Any(), "normal shutdown removes session snapshots");
    var orphan = Path.Combine(recoveryDirectory, "interrupted.pmproj");
    Check(await service.SaveAsync(project, orphan), "create recoverable saved snapshot");
    await using (var recovery = new ProjectRecoveryService(recoveryDirectory))
    {
        Check(recovery.FindLatestRecoverable() == orphan, "interrupted session is discoverable");
        Check((await service.LoadAsync(orphan))!.ImportedVideos[0].SourcePath == media, "recovery retains resolved media paths");
        recovery.MarkRestored(orphan);
    }
    Check(!File.Exists(orphan), "restored snapshot cleaned on normal shutdown");
    Check(loaded.ImportedVideos[0].MeshLayers[0].IsLocked, "calibration lock round trips");
    var quad = new Vector2[] { new(0, 0), new(1, 0), new(0, 1), new(1, 1) };
    Check(QuadGeometry.IsValid(quad), "valid quad");
    Check(!QuadGeometry.IsValid(new Vector2[] { new(0, 0), new(1, 1), new(0, 1), new(1, 0) }), "crossed quad rejected");
    Check(!QuadGeometry.IsValid(new Vector2[4]), "degenerate quad rejected");
    quad[0] = new Vector2(float.NaN, 0);
    Check(!QuadGeometry.IsValid(quad), "nonfinite quad rejected");
    var snapped = QuadGeometry.FromPixels(104, 207, 1920, 1080, 10);
    Check(Math.Abs(snapped.X * 1920 - 100) < .001 && Math.Abs(snapped.Y * 1080 - 210) < .001, "pixel snapping uses output dimensions");
    var clamped = QuadGeometry.FromPixels(-10, 9999, 1920, 1080);
    Check(clamped == new Vector2(0, 1), "coordinates clamp to output bounds");
    await ArtworkChecks.Run();
    await PhotoMappingChecks.Run();
    Console.WriteLine("Persistence regression checks passed.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
finally { Directory.Delete(directory, recursive: true); }

static void Check(bool condition, string scenario)
{
    if (!condition) throw new Exception("Failed: " + scenario);
}
