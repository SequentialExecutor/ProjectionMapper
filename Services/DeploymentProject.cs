using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>Shared reference and routing rules used by collection, preflight and destination setup.</summary>
    public static class DeploymentProject
    {
        public static ProjectModel Copy(ProjectModel project)
        {
            if (project.PhotoMaps == null || project.ArtworkAssets == null) throw new InvalidDataException("Project image collections are missing.");
            // Clone editable metadata while sharing immutable embedded-image strings; large libraries must not be serialized just to snapshot.
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info =>
            {
                if (info.Type == typeof(ProjectModel)) foreach (var property in info.Properties)
                    if (property.Name is nameof(ProjectModel.PhotoMaps) or nameof(ProjectModel.ArtworkAssets)) property.ShouldSerialize = (_, _) => false;
            });
            var options = new JsonSerializerOptions { IncludeFields = true, TypeInfoResolver = resolver };
            var copy = JsonSerializer.Deserialize<ProjectModel>(JsonSerializer.Serialize(project, options), options) ?? throw new InvalidDataException("The project snapshot is empty.");
            copy.PhotoMaps = project.PhotoMaps.Select(p => p.Copy()).ToList(); copy.ArtworkAssets = project.ArtworkAssets.Select(a => a.Copy()).ToList();
            return copy;
        }
        public static bool IsExternalReferenceMedia(string path) => new[] { ".m3u8", ".m3u", ".pls", ".ffconcat", ".concat", ".mpd", ".sdp", ".url" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        public static bool HasOfflineArtwork(ImportedVideoData video, ProjectModel project) => video.MeshLayers.Count > 0 &&
            video.MeshLayers.All(m => m.ArtworkAssetId != null && project.ArtworkAssets.Any(a => a.Id == m.ArtworkAssetId));
        public static IEnumerable<(string Path, bool Required, string Name)> Media(ProjectModel project)
        {
            foreach (var video in project.ImportedVideos) yield return (video.SourcePath, !HasOfflineArtwork(video, project), video.Name);
            foreach (var surface in project.Surfaces) foreach (var layer in surface.Layers)
                if (!string.IsNullOrWhiteSpace(layer.SourcePath)) yield return (layer.SourcePath, true, layer.Name ?? "Legacy layer");
        }
        public static List<int> AssignedOutputs(ProjectModel project) => project.ImportedVideos.Select(v => v.TargetMonitorIndex)
            .Concat(project.ImportedVideos.SelectMany(v => v.MeshLayers).Select(m => m.TargetMonitorIndex))
            .Concat(project.Surfaces.SelectMany(s => s.Layers).Select(l => l.TargetMonitorIndex))
            .Concat(project.OutputAppearances.Select(o => o.MonitorIndex)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        public static bool SameLayout(OutputLayout? saved, OutputLayout current) => saved != null &&
            saved.ComputerId == current.ComputerId && saved.Displays.SequenceEqual(current.Displays);
        public static bool NeedsOutputSetup(ProjectModel project, OutputLayout current) => AssignedOutputs(project).Count > 0 &&
            (!SameLayout(project.OutputLayout, current) || current.Displays.Any(d => string.IsNullOrWhiteSpace(d.HardwareId)) || AssignedOutputs(project).Any(i => current.Displays.All(d => d.Index != i)));
        public static void ValidateMapping(ProjectModel project, OutputLayout destination, IReadOnlyDictionary<int, int> mapping)
        {
            foreach (int old in AssignedOutputs(project))
                if (!mapping.TryGetValue(old, out int target) || (target != -1 && destination.Displays.All(d => d.Index != target)))
                    throw new InvalidDataException("Every saved output needs an explicit destination or Preview only.");
            var chosen = mapping.Values.Where(i => i >= 0).ToList();
            if (chosen.Distinct().Count() != chosen.Count) throw new InvalidDataException("Choose a different destination for each saved output.");
        }
        public static void RemapOutputs(ProjectModel project, OutputLayout destination, IReadOnlyDictionary<int, int> mapping, bool retainColor)
        {
            ValidateMapping(project, destination, mapping);
            int Map(int index) => index < 0 ? -1 : mapping[index];
            foreach (var video in project.ImportedVideos) { video.TargetMonitorIndex = Map(video.TargetMonitorIndex); foreach (var mesh in video.MeshLayers) mesh.TargetMonitorIndex = Map(mesh.TargetMonitorIndex); }
            foreach (var surface in project.Surfaces) foreach (var layer in surface.Layers) layer.TargetMonitorIndex = Map(layer.TargetMonitorIndex);
            project.OutputAppearances = retainColor ? project.OutputAppearances.Where(o => Map(o.MonitorIndex) >= 0).Select(o =>
            { var copy = o.Copy(); copy.MonitorIndex = Map(o.MonitorIndex); copy.DisplayName = destination.Displays.Single(d => d.Index == copy.MonitorIndex).DeviceName; return copy; }).ToList() : new();
            project.OutputLayout = destination.Copy();
        }
    }
}
