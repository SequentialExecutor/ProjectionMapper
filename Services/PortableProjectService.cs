using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>Collect into a sibling staging directory, verify bytes, then publish by directory rename. Never overwrite a show.</summary>
    public sealed class PortableProjectService
    {
        public const string ManifestName = "collection.json";
        public Task<CollectionResult> CollectAsync(ProjectModel detachedProject, string destination, IProgress<CollectionProgress>? progress = null,
            CancellationToken token = default, long minimumFreeBytes = 64 * 1024 * 1024) => Task.Run(async () =>
        {
            if (minimumFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(minimumFreeBytes));
            var project = DeploymentProject.Copy(detachedProject); ProjectService.Validate(project);
            destination = Path.GetFullPath(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Choose a new collection folder. Existing folders are never replaced.");
            var parent = Path.GetDirectoryName(destination) ?? throw new IOException("Choose a destination beneath an existing folder.");
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The destination parent folder does not exist.");
            var references = DeploymentProject.Media(project).ToList();
            if (references.Any(r => !Path.IsPathFullyQualified(r.Path))) throw new InvalidDataException("Load the project to resolve media paths before collecting it.");
            if (references.Any(r => File.Exists(r.Path) && IsExternalReference(r.Path))) throw new InvalidDataException("External-reference playlists/streams must be flattened to self-contained media before collection.");
            var files = references.GroupBy(r => Path.GetFullPath(r.Path), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Select(g => (Path: g.Key, Required: g.Any(r => r.Required))).ToList();
            foreach (var file in files) if (!File.Exists(file.Path) && file.Required) throw new FileNotFoundException("Relink missing required media before collecting.", file.Path);
            long total = files.Where(f => File.Exists(f.Path)).Sum(f => new FileInfo(f.Path).Length);
            long estimate = checked(total + EstimateProjectBytes(project) + minimumFreeBytes);
            long? free = AvailableBytes(parent);
            if (free.HasValue && free < estimate) throw new IOException($"The destination needs approximately {estimate:N0} free bytes including reserve; {free:N0} are available.");
            string staging = Path.Combine(parent, ".projection-collect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                Directory.CreateDirectory(Path.Combine(staging, "Media"));
                var manifest = new CollectionManifest();
                var replacements = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                var copied = new Dictionary<string, CollectionFile>(StringComparer.OrdinalIgnoreCase);
                long completed = 0;
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(file.Path))
                    {
                        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Path))).ToLowerInvariant();
                        replacements[file.Path] = "Media/unavailable-" + id + ".mp4";
                        manifest.Notes.Add("An artwork-covered source has no original video. Embedded artwork plays offline; original video audio is unavailable.");
                        continue;
                    }
                    string temporary = Path.Combine(staging, "Media", Guid.NewGuid().ToString("N") + ".part");
                    var before = new FileInfo(file.Path); long length = before.Length; DateTime modified = before.LastWriteTimeUtc;
                    string hash;
                    await using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                    using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[131072]; int count;
                        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); digest.AppendData(buffer, 0, count); completed += count;
                            progress?.Report(new("Copying " + Path.GetFileName(file.Path), completed, total));
                        }
                        await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
                        if (input.Length != length || input.Position != length) throw new IOException("Source media changed during collection. Retry with editing tools closed.");
                        hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
                    }
                    var after = new FileInfo(file.Path);
                    if (after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("Source media changed during collection. Retry with editing tools closed.");
                    string extension = Path.GetExtension(file.Path).ToLowerInvariant();
                    if (extension.Length < 2 || extension.Length > 12 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit)) extension = ".media";
                    string relative = "Media/" + hash + extension;
                    if (copied.ContainsKey(relative)) File.Delete(temporary);
                    else
                    {
                        File.Move(temporary, Path.Combine(staging, relative));
                        copied[relative] = new(relative, length, hash);
                    }
                    replacements[file.Path] = relative;
                }
                string Replace(string path) => Path.Combine(staging, replacements[Path.GetFullPath(path)]);
                foreach (var video in project.ImportedVideos) video.SourcePath = Replace(video.SourcePath);
                foreach (var surface in project.Surfaces) foreach (var layer in surface.Layers) if (!string.IsNullOrWhiteSpace(layer.SourcePath)) layer.SourcePath = Replace(layer.SourcePath);
                var persistence = new ProjectService(); string projectPath = Path.Combine(staging, manifest.ProjectFile);
                if (!await persistence.SaveAsync(project, projectPath, token).ConfigureAwait(false)) throw new IOException(persistence.LastError);
                manifest.Files = copied.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
                manifest.Files.Add(new(manifest.ProjectFile, new FileInfo(projectPath).Length, await HashAsync(projectPath, token).ConfigureAwait(false)));
                await File.WriteAllTextAsync(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), token).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(staging, "READ-ME.txt"),
                    "ProjectionMapper collected show\n\nCopy this entire folder. Open show.pmproj on the destination Windows machine.\n" +
                    "Run Preflight / rehearsal, verify the collection, and explicitly assign destination outputs when prompted.\n" +
                    "Install the application and required .NET Desktop Runtime separately. FFmpeg/ffprobe are needed for video/audio; they are not included here.\n" +
                    "Accepted artwork/photos/masks/cues/settings are inside the project. No AI key or network connection is needed for accepted artwork playback.\n" +
                    "A valid checksum report proves byte integrity, not projector alignment, media compatibility, or a successful rehearsal.\n", token).ConfigureAwait(false);
                progress?.Report(new("Verifying collected files", completed, total));
                var checks = await VerifyAsync(staging, token).ConfigureAwait(false);
                if (checks.Any(c => c.Severity == CheckSeverity.Error)) throw new InvalidDataException(string.Join(" ", checks.Where(c => c.Severity == CheckSeverity.Error).Select(c => c.Message)));
                token.ThrowIfCancellationRequested();
                Directory.Move(staging, destination); // Atomic publication; a concurrent existing destination causes failure rather than replacement.
                progress?.Report(new("Collection complete", completed, total));
                return new CollectionResult(destination, Path.Combine(destination, manifest.ProjectFile), copied.Count, manifest.Files.Sum(f => f.Bytes));
            }
            finally
            {
                if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch (Exception ex) { Debug.WriteLine($"Collection staging cleanup failed: {ex.Message}"); }
            }
        }, token);
        private static bool IsExternalReference(string path) => DeploymentProject.IsExternalReferenceMedia(path);
        public static long EstimateProjectBytes(ProjectModel project) => 1024 * 1024L + project.CameraCalibrations.Sum(c => (long)c.Photo.ImageBase64.Length) + project.PhotoMaps.Sum(p => (long)p.ImageBase64.Length) +
            project.ArtworkAssets.Sum(a => (long)a.ImageBase64.Length + a.ThumbnailBase64.Length + a.References.Sum(r => (long)r.ImageBase64.Length));
        public static long? AvailableBytes(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
            catch (Exception ex) { Debug.WriteLine($"Free-space observation unavailable: {ex.Message}"); return null; }
        }
        public static async Task<string> HashAsync(string path, CancellationToken token)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
        }
        /// <summary>Constrain manifest reads and project dependencies to ordinary files inside the collection root.</summary>
        private static string ContainedPath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(p => p is ".." or "." or ""))
                throw new InvalidDataException("Collection paths must be contained relative paths.");
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("A collection path escapes its folder.");
            for (var current = new FileInfo(full) as FileSystemInfo; current != null && current.FullName.Length >= Path.GetFullPath(root).Length; current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Collection paths cannot traverse links or junctions.");
            return full;
        }
        public async Task<List<DeploymentCheck>> VerifyAsync(string directory, CancellationToken token = default, string? expectedProjectFile = null)
        {
            var results = new List<DeploymentCheck>();
            try
            {
                directory = Path.GetFullPath(directory);
                string manifestPath = ContainedPath(directory, ManifestName);
                if (new FileInfo(manifestPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("The collection manifest is too large.");
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<CollectionManifest>(stream, cancellationToken: token).ConfigureAwait(false) ?? throw new InvalidDataException("Empty collection manifest.");
                if (manifest.ManifestVersion != 1 || manifest.Files == null || manifest.Files.Count < 1 || manifest.Files.Count > 20000) throw new InvalidDataException("Unsupported or invalid collection manifest.");
                var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in manifest.Files)
                {
                    token.ThrowIfCancellationRequested();
                    if (file == null || !listed.Add(file.Path) || file.Bytes < 0 || file.Sha256 == null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid or repeated collection file entry.");
                    string path = ContainedPath(directory, file.Path);
                    if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || !string.Equals(await HashAsync(path, token).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                        results.Add(new(CheckSeverity.Error, "collection.integrity", $"Missing or altered collected file: {file.Path}", "Recollect from the original or restore a verified backup."));
                }
                if (expectedProjectFile != null && !string.Equals(expectedProjectFile, manifest.ProjectFile, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This project is not the verified collection project.");
                string projectPath = ContainedPath(directory, manifest.ProjectFile);
                if (!listed.Contains(manifest.ProjectFile)) throw new InvalidDataException("The project is not covered by the collection manifest.");
                var service = new ProjectService(); var project = await service.LoadAsync(projectPath, token).ConfigureAwait(false) ?? throw new InvalidDataException(service.LastError);
                foreach (var media in DeploymentProject.Media(project))
                {
                    string relative = Path.GetRelativePath(directory, media.Path).Replace(Path.DirectorySeparatorChar, '/');
                    ContainedPath(directory, relative);
                    if (File.Exists(media.Path) && !listed.Contains(relative)) throw new InvalidDataException("A project dependency is not covered by the collection manifest.");
                    if (media.Required && !File.Exists(media.Path)) throw new InvalidDataException("A required project dependency is missing from the collection.");
                }
                if (results.Count == 0) results.Add(new(CheckSeverity.Info, "collection.verified", $"Verified {manifest.Files.Count} collected files and contained project references."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { results.Add(new(CheckSeverity.Error, "collection.invalid", ex.Message, "Use a complete, unmodified collection folder.")); }
            return results;
        }
    }
}
