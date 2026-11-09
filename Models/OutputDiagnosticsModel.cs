using System;
using System.Collections.Generic;

namespace ProjectionMapper.Models
{
    public sealed record DiagnosticSurface(string Id, string Name, string SourceName, int TargetDisplay,
        bool ActiveInCue, bool HasCachedContent, double Opacity);
    public sealed record DiagnosticDisplay(int Index, string DeviceName, bool Connected, int WindowsWidth, int WindowsHeight,
        bool WindowOpen, int RenderWidth, int RenderHeight, long RenderedFrames, DateTimeOffset? LastFrameUtc, double RenderMilliseconds)
    {
        public string Title { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
        public string Metrics { get; init; } = string.Empty;
        public List<string> Findings { get; init; } = new();
    }
    /// <summary>Detached software observations only. No credentials, paths, image bytes, or provider payloads.</summary>
    public sealed class OutputDiagnosticReport
    {
        public DateTimeOffset CapturedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string ProjectName { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = string.Empty;
        public string RendererBackend { get; set; } = string.Empty;
        public string CurrentCue { get; set; } = string.Empty;
        public bool Playing { get; set; }
        public bool PlaylistMode { get; set; }
        public bool Blackout { get; set; }
        public bool OutputReviewRequired { get; set; }
        public List<DiagnosticSurface> Surfaces { get; set; } = new();
        public List<DiagnosticDisplay> Displays { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public string Scope { get; set; } = "Software observations describe routing, cached content, output windows and rendered frame events. They do not measure the physical projector signal, visible light or audiovisual synchronization.";
    }
}
