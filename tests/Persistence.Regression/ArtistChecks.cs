using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;

internal static class ArtistChecks
{
    public static async Task Run()
    {
        static void Check(bool value, string description) { if (!value) throw new Exception("Artist regression: " + description); }
        static bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        byte[] pixels = Enumerable.Range(0, 16 * 16).SelectMany(_ => new byte[] { 64, 128, 255, 255 }).ToArray();
        var mask = new SurfaceAppearance { Mask = new() { new(0, 0), new(.5, 0), new(.5, 1), new(0, 1) } };
        var masked = new SurfacePixelMap(mask, 16, 16).Apply(pixels);
        Check(masked[(8 * 16 + 3) * 4 + 3] == 255 && masked[(8 * 16 + 12) * 4 + 3] == 0, "polygon alpha excludes spill");
        mask.Feather = .25; var feathered = new SurfacePixelMap(mask, 16, 16).Apply(pixels);
        Check(feathered[(8 * 16) * 4 + 3] > 0 && feathered[(8 * 16) * 4 + 3] < feathered[(8 * 16 + 3) * 4 + 3], "feather falls inward from perimeter");
        var grid = ArtistRules.RegularGrid(2, 2);
        Check(new SurfacePixelMap(grid, 16, 16).Apply(pixels).SequenceEqual(pixels), "identity grid has no seams or gaps");
        var gradient = new byte[pixels.Length];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) { gradient[(y * 16 + x) * 4 + 2] = (byte)(x * 16); gradient[(y * 16 + x) * 4 + 3] = 255; }
        grid.Grid[4] = new(.65, .5);
        var deformed = new SurfacePixelMap(grid, 16, 16).Apply(gradient);
        Check(deformed[(8 * 16 + 8) * 4 + 2] < gradient[(8 * 16 + 8) * 4 + 2], "interior control point bends texture");
        grid.Grid[4] = new(.99, .99);
        Check(Reject(() => ArtistRules.Validate(grid)), "folded cells rejected");
        var crossed = new SurfaceAppearance { Mask = new() { new(0, 0), new(1, 1), new(0, 1), new(1, 0) } };
        Check(Reject(() => ArtistRules.Validate(crossed)), "crossed mask rejected");
        var copy = mask.Copy(); copy.Mask.Clear(); Check(mask.Mask.Count == 4, "surface snapshots own their vertices");
        var correction = new OutputAppearance { MonitorIndex = 0, DisplayName = "display-test", Red = .5, Brightness = .5 };
        var corrected = OutputPixels.Apply(pixels, 16, 16, correction, 1);
        Check(corrected[0] == 32 && corrected[1] == 64 && corrected[2] == 64, "output balance and brightness applied once");
        Check(OutputPixels.Apply(pixels, 16, 16, null, 0).Where((_, i) => i % 4 != 3).All(b => b == 0), "zero fade is opaque black");
        correction.Gamma = 2; Check(OutputPixels.Apply(pixels, 16, 16, correction, 1)[0] > corrected[0], "gamma lifts midtones");
        for (int i = 0; i <= 10; i++)
        { double t = i / 10.0; Check(Math.Abs(OutputPixels.EdgeWeight(t * .2, .2, 0, 1) + OutputPixels.EdgeWeight(1 - (1 - t) * .2, 0, .2, 1) - 1) < 1e-8, "opposing linear-light blend weights complement"); }
        string folder = Path.Combine(Path.GetTempPath(), "Artist-project-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var project = new ProjectModel { OutputAppearances = new() { correction }, PlaylistGroups = new() { new() { Id = "cue", Name = "Hold", AdvanceMode = CueAdvanceMode.Manual, HoldSeconds = 10, FadeSeconds = .5, SourceIds = new() { "source" } } } };
            project.ImportedVideos.Add(new() { Id = "source", SourcePath = Path.Combine(folder, "absent.mp4"), MeshLayers = new() { new() { Id = "mesh", SourceId = "source", Appearance = mask } } });
            var service = new ProjectService(); string path = Path.Combine(folder, "show.pmproj");
            Check(await service.SaveAsync(project, path), "artist project saves");
            var loaded = (await service.LoadAsync(path))!;
            Check(loaded.ProjectVersion == 6 && loaded.ImportedVideos[0].MeshLayers[0].Appearance.Mask.Count == 4 && loaded.OutputAppearances[0].Gamma == 2 && loaded.PlaylistGroups[0].AdvanceMode == CueAdvanceMode.Manual, "masks, output correction and cues survive reopen");
            loaded.ImportedVideos[0].MeshLayers[0].Appearance = crossed;
            Check(!await service.SaveAsync(loaded, path) && (await service.LoadAsync(path))!.ImportedVideos[0].MeshLayers[0].Appearance.Mask.Count == 4, "bad mask cannot replace good project");
            foreach (int version in new[] { 1, 2, 3, 4, 5 })
            {
                string old = Path.Combine(folder, $"v{version}.pmproj");
                await File.WriteAllTextAsync(old, $"{{\"ProjectVersion\":{version}}}");
                var migrated = await service.LoadAsync(old);
                Check(migrated != null && migrated.OutputAppearances.Count == 0, "older project defaults are compatible");
            }
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Artist geometry, mask/mesh pixels, output correction, blending and schema checks passed.");
    }
}
