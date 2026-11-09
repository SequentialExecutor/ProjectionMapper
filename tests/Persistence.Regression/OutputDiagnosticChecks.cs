using System.Text.Json;
using ProjectionMapper.Models;
using ProjectionMapper.Services;

internal static class OutputDiagnosticChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("Output diagnostics: " + message); }
    private static OutputDiagnosticReport Sample() => new()
    {
        ProjectName = "Diagnostic fixture",
        Surfaces = new() { new("mesh", "Front panel", "Clip", 0, true, true, 1) },
        Displays = new() { new(0, "display", true, 1920, 1080, true, 1920, 1080, 100, DateTimeOffset.UtcNow, 5) }
    };
    public static async Task Run()
    {
        var report = OutputDiagnosticService.Analyze(Sample());
        Check(report.Displays[0].State == "Frames available", "software frames are described without a hardware-success claim");
        report = Sample(); report.Blackout = true; report.OutputReviewRequired = true;
        report = OutputDiagnosticService.Analyze(report);
        Check(report.Displays[0].State == "Output review required" && report.Displays[0].Findings.Count == 2, "review and blackout are both explained with review taking priority");
        report = Sample(); report.Displays.Clear(); report = OutputDiagnosticService.Analyze(report);
        Check(report.Displays.Count == 1 && !report.Displays[0].Connected && report.Displays[0].State == "Display unavailable", "assigned missing outputs remain visible in diagnostics");
        report = Sample(); report.Surfaces[0] = report.Surfaces[0] with { ActiveInCue = false }; report = OutputDiagnosticService.Analyze(report);
        Check(report.Displays[0].State == "No active surfaces in this cue", "inactive cue is distinct from missing content");
        report = Sample(); report.Surfaces[0] = report.Surfaces[0] with { Opacity = 0 }; report = OutputDiagnosticService.Analyze(report);
        Check(report.Displays[0].State == "Surfaces are transparent", "opacity can explain an empty output");
        report = Sample(); report.Surfaces[0] = report.Surfaces[0] with { HasCachedContent = false }; report = OutputDiagnosticService.Analyze(report);
        Check(report.Displays[0].State == "Waiting for content", "missing cached content suggests media checks");
        report = Sample(); report.Displays[0] = report.Displays[0] with { WindowOpen = false }; report = OutputDiagnosticService.Analyze(report);
        Check(report.Displays[0].State == "Output window closed", "closed window is visible in the report");
        report = Sample(); report.Displays[0] = report.Displays[0] with { RenderWidth = 1280 }; report = OutputDiagnosticService.Analyze(report);
        Check(report.Displays[0].State == "Resolution mismatch", "actual render and detected Windows modes are compared");
        report = Sample(); report.Displays[0] = report.Displays[0] with { LastFrameUtc = report.CapturedUtc.AddSeconds(-10) }; report = OutputDiagnosticService.Analyze(report);
        Check(report.Displays[0].State == "No recent rendered frame", "stalled software frame events are reported");
        report = Sample(); report.Surfaces[0] = report.Surfaces[0] with { TargetDisplay = -1 }; report = OutputDiagnosticService.Analyze(report);
        Check(report.Displays[0].State == "No assigned surfaces" && report.Summary.Contains("1 preview-only"), "preview-only does not falsely imply projection");
        var folder = Path.Combine(Path.GetTempPath(), "output-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "report.json");
            await DeploymentReports.SaveJsonAsync(report, path);
            string json = await File.ReadAllTextAsync(path);
            var loaded = JsonSerializer.Deserialize<OutputDiagnosticReport>(json)!;
            Check(loaded.Displays[0].State == report.Displays[0].State && loaded.Surfaces.Count == 1, "actual JSON export preserves diagnostic evidence");
            Check(!json.Contains("SourcePath") && !json.Contains("ImageBase64") && !json.Contains("Token"), "report has no media-path, image, or credential fields");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Output diagnostics routing, state explanations, and real JSON export checks passed.");
    }
}
