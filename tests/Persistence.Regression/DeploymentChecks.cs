using System.Security.Cryptography;
using System.Text.Json;
using ProjectionMapper.Models;
using ProjectionMapper.Services;

internal static class DeploymentChecks
{
    public static async Task Run()
    {
        static void Check(bool value, string description) { if (!value) throw new Exception("Deployment regression: " + description); }
        string root = Path.Combine(Path.GetTempPath(), "Projection-collection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string originals = Path.Combine(root, "originals"); Directory.CreateDirectory(Path.Combine(originals, "one")); Directory.CreateDirectory(Path.Combine(originals, "two"));
            byte[] png = Convert.FromBase64String(ArtworkFixture.Png), jpeg = Convert.FromBase64String(PhotoFixture.Jpeg);
            string first = Path.Combine(originals, "one", "same.png"), second = Path.Combine(originals, "two", "same.png"), legacy = Path.Combine(originals, "legacy.jpg");
            await File.WriteAllBytesAsync(first, png); await File.WriteAllBytesAsync(second, png); await File.WriteAllBytesAsync(legacy, jpeg);
            var artwork = new ArtworkAsset { Id = Guid.NewGuid().ToString("N"), Name = "Portable fixture", ImageBase64 = ArtworkFixture.Png, ThumbnailBase64 = ArtworkFixture.Png,
                Sha256 = Convert.ToHexString(SHA256.HashData(png)), Width = 4, Height = 3, Provider = ArtworkProvider.OpenAI, Model = "gpt-image-1.5", Prompt = "test fixture", Layout = new() { new("mesh-art", "Artwork surface", ArtworkRegion.Full) } };
            var layout = new OutputLayout { ComputerId = "fixture-computer", Displays = new() { new(0, "fixture-display", "fixture-monitor-id", 0, 0, 1920, 1080, 1920, 1080, true) } };
            var project = new ProjectModel { Name = "Transfer regression", OutputLayout = layout, ArtworkAssets = new() { artwork },
                PhotoMaps = new() { new() { Width = 4, Height = 3, ImageBase64 = PhotoFixture.Jpeg, ImageSha256 = Convert.ToHexString(SHA256.HashData(jpeg)) } },
                PlaylistGroups = new() { new() { Name = "Manual", SourceIds = new() { "source-a" }, AdvanceMode = CueAdvanceMode.Manual, FadeSeconds = .25 } } };
            project.ImportedVideos.Add(new() { Id = "source-a", SourcePath = first, MeshLayers = new() { new() { Id = "mesh-a", SourceId = "source-a", TargetMonitorIndex = 0, Appearance = new() { Mask = new() { new(0, 0), new(1, 0), new(.5, 1) }, Feather = .1 } } } });
            project.ImportedVideos.Add(new() { Id = "source-b", SourcePath = second, MeshLayers = new() { new() { Id = "mesh-b", SourceId = "source-b" } } });
            project.ImportedVideos.Add(new() { Id = "source-art", SourcePath = Path.Combine(originals, "missing.mp4"), PlayAudio = true, MeshLayers = new() { new() { Id = "mesh-art", SourceId = "source-art", ArtworkAssetId = artwork.Id } } });
            var surface = new SurfaceModel { Id = "legacy", Name = "Legacy resource" }; surface.Layers.Add(new() { Id = "legacy-layer", SourcePath = legacy }); project.Surfaces.Add(surface);
            var collector = new PortableProjectService(); string collected = Path.Combine(root, "collected");
            var result = await collector.CollectAsync(project, collected);
            Check(result.MediaFiles == 2, "identical media deduplicates while legacy resources are included");
            Check(project.ImportedVideos[0].SourcePath == first, "collection leaves open editor paths unchanged");
            string raw = await File.ReadAllTextAsync(result.ProjectPath);
            Check(!raw.Contains(originals, StringComparison.Ordinal) && !raw.Contains(".projection-collect-", StringComparison.Ordinal), "saved collection contains no original or staging folder dependencies");
            string moved = Path.Combine(root, "moved"); Directory.Move(collected, moved); Directory.Delete(originals, true);
            var persistence = new ProjectService(); var reopened = await persistence.LoadAsync(Path.Combine(moved, "show.pmproj")) ?? throw new Exception("Collected project did not reopen");
            Check(File.Exists(reopened.ImportedVideos[0].SourcePath) && File.Exists(reopened.Surfaces[0].Layers[0].SourcePath), "moved collection reopens after original folders are removed");
            Check(reopened.ProjectVersion == 7 && reopened.ArtworkAssets[0].ImageBase64 == ArtworkFixture.Png && reopened.PhotoMaps[0].ImageBase64 == PhotoFixture.Jpeg && reopened.ImportedVideos[0].MeshLayers[0].Appearance.Mask.Count == 3 && reopened.PlaylistGroups[0].AdvanceMode == CueAdvanceMode.Manual, "embedded artwork/photo/mask/cue settings stay portable");
            Check((await collector.VerifyAsync(moved)).All(c => c.Severity != CheckSeverity.Error), "collection hashes and contained references verify after relocation");
            Check((await collector.VerifyAsync(moved, expectedProjectFile: "different.pmproj")).Any(c => c.Severity == CheckSeverity.Error), "unlisted project cannot borrow a neighboring manifest's verification");
            Check(!DeploymentProject.NeedsOutputSetup(reopened, layout), "same fixture routing environment matches");
            var changed = layout.Copy(); changed.ComputerId = "destination-fixture";
            Check(DeploymentProject.NeedsOutputSetup(reopened, changed), "different computer requires explicit routing");
            var remapped = DeploymentProject.Copy(reopened); DeploymentProject.RemapOutputs(remapped, changed, new Dictionary<int, int> { [0] = -1 }, retainColor: false);
            Check(remapped.ImportedVideos[0].MeshLayers[0].TargetMonitorIndex == -1 && !DeploymentProject.NeedsOutputSetup(remapped, changed), "deliberate preview-only assignment is portable and opens no output");
            var preflight = await new PreflightService().CheckAsync(reopened, changed, Path.Combine(moved, "show.pmproj"), inspectMedia: true);
            Check(preflight.Checks.Any(c => c.Code == "outputs.reassign" && c.Severity == CheckSeverity.Error) && preflight.Checks.Any(c => c.Code == "media.missing" && c.Severity == CheckSeverity.Warning), "preflight distinguishes hardware blockers from artwork-covered missing originals");
            Check(preflight.Checks.Any(c => c.Code == "decoder.ffmpeg") && preflight.Checks.Any(c => c.Code == "decoder.ffprobe"), "preflight actually probes installed media dependencies or reports their failure");
            // Tampering, traversal and failed publication use real files. These are integrity tests, not hardware claims.
            await File.AppendAllTextAsync(reopened.ImportedVideos[0].SourcePath, "altered");
            Check((await collector.VerifyAsync(moved)).Any(c => c.Severity == CheckSeverity.Error), "altered media is detected");
            string manifestPath = Path.Combine(moved, PortableProjectService.ManifestName); string manifest = await File.ReadAllTextAsync(manifestPath);
            var malicious = JsonSerializer.Deserialize<CollectionManifest>(manifest)!; malicious.Files[0] = malicious.Files[0] with { Path = "../outside.png" };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(malicious));
            Check((await collector.VerifyAsync(moved)).Any(c => c.Code == "collection.invalid"), "traversing manifest entries are rejected");
            await File.WriteAllTextAsync(manifestPath, manifest);
            string existing = Path.Combine(root, "existing"); Directory.CreateDirectory(existing); await File.WriteAllTextAsync(Path.Combine(existing, "keep.txt"), "keep");
            try { await collector.CollectAsync(remapped, existing); throw new Exception("Expected existing destination rejection"); } catch (IOException) { }
            Check(await File.ReadAllTextAsync(Path.Combine(existing, "keep.txt")) == "keep", "existing destination is never overwritten");
            string huge = Path.Combine(root, "copy-fixture.bin"); await File.WriteAllBytesAsync(huge, RandomNumberGenerator.GetBytes(1_000_000));
            var copying = new ProjectModel(); copying.ImportedVideos.Add(new() { Id = "copy-fixture", SourcePath = huge });
            using var cancellation = new CancellationTokenSource(); string cancelled = Path.Combine(root, "cancelled");
            try { await collector.CollectAsync(copying, cancelled, new CancelProgress(cancellation), cancellation.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            Check(!Directory.Exists(cancelled) && !Directory.EnumerateDirectories(root, ".projection-collect-*").Any(), "cancelled copy leaves no published or staging project");
            if (PortableProjectService.AvailableBytes(root) is long available)
            {
                string full = Path.Combine(root, "insufficient-reserve");
                try { await collector.CollectAsync(copying, full, minimumFreeBytes: available); throw new Exception("Expected storage rejection"); } catch (IOException) { }
                Check(!Directory.Exists(full), "insufficient storage/reserve cannot publish a collection");
            }
            string playlist = Path.Combine(root, "network.m3u8"); await File.WriteAllTextAsync(playlist, "#EXTM3U\nhttps://example.invalid/segment.ts");
            copying.ImportedVideos[0].SourcePath = playlist;
            try { await collector.CollectAsync(copying, Path.Combine(root, "network-playlist")); throw new Exception("Expected external playlist rejection"); } catch (InvalidDataException) { }
            Check(!Directory.Exists(Path.Combine(root, "network-playlist")), "external-reference media cannot be published as a self-contained show");
            string failed = Path.Combine(root, "failed"); copying.ImportedVideos[0].SourcePath = Path.Combine(root, "missing-required.mp4");
            try { await collector.CollectAsync(copying, failed); throw new Exception("Expected missing-media rejection"); } catch (FileNotFoundException) { }
            Check(!Directory.Exists(failed), "missing required media cannot produce a broken collection");
            using var recorder = new RehearsalRecorder("Observed process test", preflight, () => (false, new List<OutputFrameObservation>()), TimeSpan.FromMilliseconds(20), 3);
            recorder.Record("test-notification", "Explicit test event; no physical projector is attached."); await Task.Delay(150);
            var report = await recorder.StopAsync("No physical rehearsal claimed.");
            Check(report.ElapsedSeconds >= .1 && report.TotalSamples > 0 && report.Samples.Count <= 3 && report.PeakWorkingSetBytes > 0 && report.Samples.All(s => s.Outputs.Count == 0), "rehearsal records actual process samples without inventing projector observations");
            string reportPath = Path.Combine(root, "rehearsal.json"); await DeploymentReports.SaveJsonAsync(report, reportPath);
            Check(JsonSerializer.Deserialize<RehearsalReport>(await File.ReadAllTextAsync(reportPath))!.OperatorNotes == "No physical rehearsal claimed.", "observed rehearsal report exports atomically");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Collection relocation/integrity/cancellation, destination routing, real dependency probes and rehearsal observation checks passed.");
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<CollectionProgress>
    { public void Report(CollectionProgress progress) { if (progress.CopiedBytes > 0) cancellation.Cancel(); } }
}
