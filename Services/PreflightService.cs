using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>Read-only checks with explicit evidence limits; no playback, projection, credential access or provider calls.</summary>
    public sealed class PreflightService
    {
        public Task<PreflightReport> CheckAsync(ProjectModel detachedProject, OutputLayout hardware, string? projectPath, bool inspectMedia,
            CancellationToken token = default, bool outputReviewConfirmed = false) => Task.Run(async () =>
        {
            var report = new PreflightReport { ProjectName = detachedProject.Name ?? "Untitled" };
            void Add(CheckSeverity level, string code, string message, string action = "") => report.Checks.Add(new(level, code, message, action));
            DeploymentSettings settings;
            try { settings = DeploymentSettings.Load(); ProjectService.Validate(detachedProject); }
            catch (Exception ex) { Add(CheckSeverity.Error, "project.invalid", ex.Message, "Fix project data or restore a valid backup."); return report; }
            Add(CheckSeverity.Info, "project.compatible", $"Project format {detachedProject.ProjectVersion} is supported; saves use format 7.");
            Add(OperatingSystem.IsWindows() && Environment.Is64BitProcess ? CheckSeverity.Info : CheckSeverity.Error, "runtime.platform", $"{RuntimeInformation.OSDescription}; .NET {Environment.Version}; {RuntimeInformation.ProcessArchitecture}.", "The application requires supported 64-bit Windows and .NET 9 Desktop Runtime.");
            if (DeploymentProject.NeedsOutputSetup(detachedProject, hardware) && !(outputReviewConfirmed && DeploymentProject.SameLayout(detachedProject.OutputLayout, hardware))) Add(CheckSeverity.Error, "outputs.reassign", "Saved output hardware does not match the current computer/layout.", "Open Output setup and explicitly select destination projectors or Preview only.");
            else Add(CheckSeverity.Info, "outputs.layout", "Output layout matches, or all assignments are Preview only.");
            foreach (int index in DeploymentProject.AssignedOutputs(detachedProject))
                if (hardware.Displays.All(d => d.Index != index)) Add(CheckSeverity.Error, "outputs.missing", $"Saved output {index + 1} is unavailable.", "Reassign this output before performance.");
            if (detachedProject.ImportedVideos.SelectMany(v => v.MeshLayers).Any() && detachedProject.ImportedVideos.SelectMany(v => v.MeshLayers).All(m => m.TargetMonitorIndex < 0))
                Add(CheckSeverity.Warning, "outputs.preview", "All mapped surfaces are assigned to Preview only.", "Assign projectors if this is a live installation.");
            var sourceIds = detachedProject.ImportedVideos.Select(v => v.Id).ToHashSet();
            foreach (var cue in detachedProject.PlaylistGroups)
                if (cue.SourceIds.Count == 0 || cue.SourceIds.Any(id => !sourceIds.Contains(id))) Add(CheckSeverity.Error, "cue.sources", $"Cue '{cue.Name}' has empty or missing source references.", "Edit this cue's sources before running it.");
            if (detachedProject.PlaylistMode && detachedProject.PlaylistGroups.Count == 0) Add(CheckSeverity.Error, "cue.empty", "Playlist mode has no cues.", "Create a cue or use legacy playback.");
            var media = DeploymentProject.Media(detachedProject).GroupBy(m => m.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
            foreach (var group in media)
            {
                token.ThrowIfCancellationRequested();
                if (!Path.IsPathFullyQualified(group.Key)) { Add(CheckSeverity.Error, "media.relative", "A media path has not been resolved.", "Load this project before running preflight."); continue; }
                if (File.Exists(group.Key) && DeploymentProject.IsExternalReferenceMedia(group.Key)) { Add(CheckSeverity.Error, "media.external", $"External-reference media: {Path.GetFileName(group.Key)}.", "Flatten playlists/streams into a self-contained video before collecting or performing offline."); continue; }
                if (!File.Exists(group.Key)) Add(group.Any(m => m.Required) ? CheckSeverity.Error : CheckSeverity.Warning, "media.missing", $"Missing original media: {Path.GetFileName(group.Key)}.", group.Any(m => m.Required) ? "Relink this source before collecting or performing." : "Embedded artwork can play offline; original video/audio is unavailable.");
                else
                {
                    try { using var read = File.OpenRead(group.Key); if (read.Length == 0) throw new InvalidDataException("The file is empty."); Add(CheckSeverity.Info, "media.readable", $"Readable: {Path.GetFileName(group.Key)} ({read.Length:N0} bytes)."); }
                    catch (Exception ex) { Add(CheckSeverity.Error, "media.unreadable", $"Cannot read {Path.GetFileName(group.Key)}: {ex.Message}", "Check permissions and restore or relink the file."); }
                }
            }
            string location = projectPath == null ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            long? free = PortableProjectService.AvailableBytes(location);
            long reserve = settings.MinimumFreeBytes + PortableProjectService.EstimateProjectBytes(detachedProject);
            Add(!free.HasValue || free < reserve ? CheckSeverity.Warning : CheckSeverity.Info, "storage.free", free.HasValue ? $"Available project-volume storage: {free:N0} bytes; approximate save/reserve need: {reserve:N0} bytes." : "Available project-volume storage could not be measured.", "Keep reserve space for saves, recovery and downloaded artwork.");
            bool needsTools = media.Any(m => Path.IsPathFullyQualified(m.Key) && File.Exists(m.Key) && !DeploymentProject.IsExternalReferenceMedia(m.Key));
            if (needsTools)
            {
                string ffmpeg = FfmpegTools.Resolve(), ffprobe = FfmpegTools.ProbeFor(ffmpeg);
                foreach (var tool in new[] { (Name: "ffmpeg", Path: ffmpeg), (Name: "ffprobe", Path: ffprobe) })
                    try
                    {
                        string version = await FfmpegTools.RunAsync(tool.Path, new[] { "-version" }, TimeSpan.FromSeconds(settings.ProbeTimeoutSeconds), token).ConfigureAwait(false);
                        string line = version.Split('\n')[0].Trim();
                        if (!line.StartsWith(tool.Name + " version", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected media tool response.");
                        Add(CheckSeverity.Info, "decoder." + tool.Name, line);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Add(CheckSeverity.Error, "decoder." + tool.Name, $"{tool.Name} unavailable: {ex.Message}", "Install matching FFmpeg/ffprobe, configure FFmpeg.Path, then rerun preflight."); }
                if (inspectMedia && !report.Checks.Any(c => c.Code.StartsWith("decoder.") && c.Severity == CheckSeverity.Error))
                    foreach (var file in media.Where(m => Path.IsPathFullyQualified(m.Key) && File.Exists(m.Key) && !DeploymentProject.IsExternalReferenceMedia(m.Key)))
                    {
                        try
                        {
                            string json = await FfmpegTools.RunAsync(ffprobe, new[] { "-v", "error", "-show_entries", "format=duration,format_name:stream=codec_type", "-of", "json", "-protocol_whitelist", "file,pipe", file.Key }, TimeSpan.FromSeconds(settings.ProbeTimeoutSeconds), token).ConfigureAwait(false);
                            using var metadata = JsonDocument.Parse(json);
                            if (metadata.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("format_name", out var formatName) && new[] { "hls", "dash", "concat", "sdp" }.Contains(formatName.GetString()))
                                throw new InvalidDataException("External-reference media must be flattened before offline use.");
                            bool video = metadata.RootElement.TryGetProperty("streams", out var streams) && streams.EnumerateArray().Any(s => s.TryGetProperty("codec_type", out var kind) && kind.GetString() == "video");
                            Add(video ? CheckSeverity.Info : CheckSeverity.Error, "media.metadata", $"{Path.GetFileName(file.Key)}: {(video ? "video metadata readable" : "no readable video stream") }.", "Metadata inspection does not prove complete decoding; rehearse the full media.");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Add(CheckSeverity.Error, "media.metadata", $"Cannot inspect {Path.GetFileName(file.Key)}: {ex.Message}", "Transcode or relink this media and rehearse it."); }
                    }
            }
            else Add(CheckSeverity.Info, "decoder.offline", "No external media is present; accepted artwork playback needs no decoder or AI credentials.");
            if (projectPath != null && File.Exists(Path.Combine(Path.GetDirectoryName(projectPath)!, PortableProjectService.ManifestName)))
                report.Checks.AddRange(await new PortableProjectService().VerifyAsync(Path.GetDirectoryName(projectPath)!, token, Path.GetFileName(projectPath)).ConfigureAwait(false));
            foreach (var group in detachedProject.CameraCalibrations.GroupBy(c => c.MeshId))
            {
                var calibration = group.OrderByDescending(c => c.CreatedUtc).First();
                var mesh = detachedProject.ImportedVideos.SelectMany(v => v.MeshLayers).FirstOrDefault(m => m.Id == group.Key);
                if (mesh == null) continue; // Historical records may outlive deleted surfaces.
                var output = hardware.Displays.FirstOrDefault(d => d.Index == mesh.TargetMonitorIndex);
                bool matches = mesh.OutputMeshPoints.Length == 8 && mesh.OutputMeshPoints.Where((value, i) => Math.Abs(value - calibration.OutputCorners[i]) > 1e-6).Any() == false;
                bool hardwareMatches = output != null && calibration.ComputerId == hardware.ComputerId && output == calibration.Output;
                if (!matches || !hardwareMatches) Add(CheckSeverity.Warning, "calibration.stale", $"Camera evidence for '{mesh.Name}' no longer matches its geometry or output environment.", "Recalibrate or verify the edited mapping on the physical surface; old residuals are historical evidence.");
            }
            Add(CheckSeverity.Info, "acceptance.manual", "Physical alignment, color/blending, audio timing and a four-hour offline rehearsal require operator acceptance.");
            token.ThrowIfCancellationRequested();
            return report;
        }, token);
    }
}
