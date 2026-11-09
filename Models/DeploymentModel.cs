using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectionMapper.Models
{
    /// <summary>Saved routing environment, captured in the same order as the application's output picker.</summary>
    public sealed class OutputLayout
    {
        public string ComputerId { get; set; } = string.Empty;
        public List<DisplayDescriptor> Displays { get; set; } = new();
        public OutputLayout Copy() => new() { ComputerId = ComputerId, Displays = Displays.Select(d => d with { }).ToList() };
    }
    public sealed record DisplayDescriptor(int Index, string DeviceName, string HardwareId, int Left, int Top, int Width, int Height, int RenderWidth, int RenderHeight, bool Primary);
    public enum CheckSeverity { Info, Warning, Error }
    public sealed record DeploymentCheck(CheckSeverity Severity, string Code, string Message, string Action = "");
    public sealed class PreflightReport
    {
        public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
        public string ProjectName { get; set; } = string.Empty;
        public List<DeploymentCheck> Checks { get; set; } = new();
        public bool HasErrors => Checks.Any(c => c.Severity == CheckSeverity.Error);
    }
    public sealed class CollectionManifest
    {
        public int ManifestVersion { get; set; } = 1;
        public string ProjectFile { get; set; } = "show.pmproj";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public List<CollectionFile> Files { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }
    public sealed record CollectionFile(string Path, long Bytes, string Sha256);
    public sealed record CollectionProgress(string Message, long CopiedBytes, long TotalBytes);
    public sealed record CollectionResult(string Directory, string ProjectPath, int MediaFiles, long Bytes);
    public sealed record OutputFrameObservation(int Index, long Frames, double RenderMilliseconds);
    public sealed record RehearsalSample(DateTime Utc, double ElapsedSeconds, long PrivateBytes, long WorkingSetBytes, double CpuPercent, bool Blackout, List<OutputFrameObservation> Outputs);
    public sealed record RehearsalEvent(DateTime Utc, string Kind, string Message);
    public sealed class RehearsalReport
    {
        public string ProjectName { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; }
        public DateTime EndedUtc { get; set; }
        public double ElapsedSeconds { get; set; }
        public long PeakPrivateBytes { get; set; }
        public long PeakWorkingSetBytes { get; set; }
        public long TotalSamples { get; set; }
        public List<RehearsalSample> Samples { get; set; } = new();
        public List<RehearsalEvent> Events { get; set; } = new();
        public PreflightReport? InitialPreflight { get; set; }
        public string OperatorNotes { get; set; } = string.Empty;
        public string Scope { get; set; } = "Observed process/render statistics. Physical projection, dropped frames and audiovisual drift require operator verification; elapsed duration alone is not a pass.";
    }
}
