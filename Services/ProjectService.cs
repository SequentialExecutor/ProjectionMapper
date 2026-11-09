using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;
using ProjectionMapper.Utilities;

namespace ProjectionMapper.Services
{
    /// <summary>Versioned JSON persistence with atomic replacement and a last-good-save backup.</summary>
    public sealed class ProjectService : IProjectService
    {
        // Serialize operations on this service; callers pass detached UI snapshots to SaveAsync.
        private readonly SemaphoreSlim _operations = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            IncludeFields = true, // Vector2 and Matrix3x2 store their coordinates in public fields.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        public string? LastError { get; private set; }

        public async Task<ProjectModel?> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LastError = null;
                var fullPath = Path.GetFullPath(path);
                var project = await ReadValidatedAsync(fullPath, cancellationToken).ConfigureAwait(false);
                ResolvePaths(project, Path.GetDirectoryName(fullPath)!, relative: false);
                return project;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.WriteLine($"ProjectService.LoadAsync: {ex}");
                return null;
            }
            finally { _operations.Release(); }
        }

        public async Task<bool> SaveAsync(ProjectModel project, string path, CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? temporaryPath = null;
            try
            {
                LastError = null;
                ArgumentNullException.ThrowIfNull(project);
                var fullPath = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath)!;
                // Clone first: relative paths and timestamps must not mutate live editor state on failure.
                var snapshot = JsonSerializer.Deserialize<ProjectModel>(JsonSerializer.Serialize(project, _jsonOptions), _jsonOptions)
                    ?? throw new InvalidDataException("Project snapshot is empty.");
                Validate(snapshot);
                snapshot.ProjectVersion = 6;
                snapshot.LastModified = DateTime.UtcNow;
                ResolvePaths(snapshot, directory, relative: true);
                Directory.CreateDirectory(directory);
                // Same-directory staging keeps the final rename on the same filesystem.
                temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(fullPath))
                {
                    // Never overwrite a good backup with an already-corrupt primary file.
                    bool primaryIsValid;
                    try
                    {
                        await ReadValidatedAsync(fullPath, cancellationToken).ConfigureAwait(false);
                        primaryIsValid = true;
                    }
                    catch (Exception ex) when (ex is JsonException || ex is InvalidDataException)
                    {
                        Debug.WriteLine($"ProjectService: preserving backup because primary is invalid: {ex.Message}");
                        primaryIsValid = false;
                    }
                    if (primaryIsValid) File.Replace(temporaryPath, fullPath, fullPath + ".bak");
                    else File.Move(temporaryPath, fullPath, overwrite: true);
                }
                else File.Move(temporaryPath, fullPath);
                temporaryPath = null;
                project.LastModified = snapshot.LastModified;
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.WriteLine($"ProjectService.SaveAsync: {ex}");
                return false;
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception ex) { Debug.WriteLine($"ProjectService: staging cleanup failed: {ex}"); }
                }
                _operations.Release();
            }
        }

        private async Task<ProjectModel> ReadValidatedAsync(string path, CancellationToken token)
        {
            await using var stream = File.OpenRead(path);
            var project = await JsonSerializer.DeserializeAsync<ProjectModel>(stream, _jsonOptions, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The project file is empty.");
            Validate(project);
            return project;
        }

        public static void Validate(ProjectModel project)
        {
            if (project.ProjectVersion < 1 || project.ProjectVersion > 6)
                throw new InvalidDataException($"Project version {project.ProjectVersion} is unsupported. Use a compatible version of ProjectionMapper.");
            if (project.ImportedVideos == null || project.PlaylistGroups == null || project.Surfaces == null)
                throw new InvalidDataException("Project collections cannot be null.");
            if (!double.IsFinite(project.InputZoom) || project.InputZoom <= 0 || !double.IsFinite(project.OutputZoom) || project.OutputZoom <= 0)
                throw new InvalidDataException("Project zoom must be a positive finite number.");
            if (project.PhotoMaps == null || project.PhotoMaps.Count > 8)
                throw new InvalidDataException("A project supports up to eight photo references.");
            var photoIds = new System.Collections.Generic.HashSet<string>();
            foreach (var photo in project.PhotoMaps)
            {
                if (photo == null || string.IsNullOrWhiteSpace(photo.Id) || !photoIds.Add(photo.Id))
                    throw new InvalidDataException("Photo references need unique IDs.");
                PhotoGeometry.ValidateMap(photo);
            }
            if (project.ArtworkAssets == null || project.ArtworkAssets.Count > 32) throw new InvalidDataException("A project supports up to 32 accepted artwork images.");
            var artworkIds = new System.Collections.Generic.HashSet<string>();
            foreach (var asset in project.ArtworkAssets)
            {
                if (asset == null || !artworkIds.Add(asset.Id)) throw new InvalidDataException("Artwork assets need unique IDs.");
                ArtworkRules.ValidateAsset(asset);
            }
            if (project.OutputLayout != null)
            {
                if (string.IsNullOrWhiteSpace(project.OutputLayout.ComputerId) || project.OutputLayout.Displays == null || project.OutputLayout.Displays.Count > 32 ||
                    project.OutputLayout.Displays.Any(d => d == null || d.Index < 0 || d.Width <= 0 || d.Height <= 0 || d.RenderWidth <= 0 || d.RenderHeight <= 0 || string.IsNullOrWhiteSpace(d.DeviceName)) ||
                    project.OutputLayout.Displays.Select(d => d.Index).Distinct().Count() != project.OutputLayout.Displays.Count)
                    throw new InvalidDataException("Saved output layout is invalid.");
            }
            if (project.OutputAppearances == null || project.OutputAppearances.Count > 32) throw new InvalidDataException("Output corrections are invalid.");
            var outputIds = new System.Collections.Generic.HashSet<string>();
            foreach (var output in project.OutputAppearances)
            {
                ArtistRules.Validate(output);
                if (!outputIds.Add(output.DisplayName)) throw new InvalidDataException("Output corrections must have unique display identities.");
            }
            foreach (var cue in project.PlaylistGroups) ArtistRules.Validate(cue);
            var renderIds = new System.Collections.Generic.HashSet<string>();
            foreach (var video in project.ImportedVideos)
            {
                if (video == null || video.MeshLayers == null || string.IsNullOrWhiteSpace(video.SourcePath) || string.IsNullOrWhiteSpace(video.Id) || !renderIds.Add(video.Id))
                    throw new InvalidDataException("A video source or its mesh list is invalid.");
                if (!double.IsFinite(video.StillDurationSeconds) || video.StillDurationSeconds < 1 || video.StillDurationSeconds > 86400)
                    throw new InvalidDataException("Offline still durations must be between one second and one day.");
                foreach (var mesh in video.MeshLayers)
                {
                    if (mesh == null || string.IsNullOrWhiteSpace(mesh.Id) || !renderIds.Add(mesh.Id))
                        throw new InvalidDataException("Sources and meshes must have distinct, nonempty IDs.");
                    ArtistRules.Validate(mesh.Appearance);
                    if (mesh.ArtworkAssetId != null)
                    {
                        if (!artworkIds.Contains(mesh.ArtworkAssetId)) throw new InvalidDataException("An artwork assignment references a missing library image.");
                        ArtworkRules.ValidateRegion(mesh.ArtworkRegion);
                    }
                    if (mesh == null || mesh.MeshPoints == null || mesh.OutputMeshPoints == null ||
                        mesh.MeshPoints.Length != 8 || mesh.OutputMeshPoints.Length != 8 ||
                        mesh.MeshPoints.Any(p => !float.IsFinite(p)) || mesh.OutputMeshPoints.Any(p => !float.IsFinite(p)))
                        throw new InvalidDataException("Each mesh must contain four finite input and output coordinates.");
                }
            }
        }

        private static void ResolvePaths(ProjectModel project, string directory, bool relative)
        {
            string ConvertPath(string path) => relative
                ? Path.GetRelativePath(directory, Path.GetFullPath(path, directory))
                : Path.GetFullPath(path, directory);
            foreach (var video in project.ImportedVideos) video.SourcePath = ConvertPath(video.SourcePath);
            foreach (var surface in project.Surfaces)
                foreach (var layer in surface.Layers)
                    if (!string.IsNullOrWhiteSpace(layer.SourcePath)) layer.SourcePath = ConvertPath(layer.SourcePath);
        }

        public async Task<ProjectModel?> ImportLegacyAsync(string legacyPath, CancellationToken cancellationToken = default)
        {
            try { return await MapMapProjectImporter.ImportAsync(legacyPath, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.WriteLine($"ProjectService.ImportLegacyAsync: {ex}");
                return null;
            }
        }
    }
}
