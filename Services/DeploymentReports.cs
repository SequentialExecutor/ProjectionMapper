using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    public static class DeploymentReports
    {
        public static string Describe(PreflightReport report) => $"Preflight: {report.ProjectName}\nChecked (UTC): {report.CheckedUtc:O}\n{(report.HasErrors ? "BLOCKERS FOUND" : "Automated checks complete; operator acceptance still required")}\n\n" +
            string.Join("\n\n", report.Checks.Select(c => $"[{c.Severity}] {c.Message}" + (c.Action.Length > 0 ? "\n" + c.Action : string.Empty)));
        public static string Describe(RehearsalReport report) => $"Rehearsal: {report.ProjectName}\nStarted/ended (UTC): {report.StartedUtc:O} / {report.EndedUtc:O}\nObserved duration: {TimeSpan.FromSeconds(report.ElapsedSeconds)}\nSamples: {report.TotalSamples:N0} ({report.Samples.Count:N0} retained)\nPeak private memory: {report.PeakPrivateBytes:N0} bytes\nPeak working set: {report.PeakWorkingSetBytes:N0} bytes\n\n{report.Scope}\n\nOperator notes:\n{report.OperatorNotes}\n\nEvents:\n" + string.Join("\n", report.Events.Select(e => $"{e.Utc:O} [{e.Kind}] {e.Message}"));
        public static async Task SaveJsonAsync<T>(T report, string path, CancellationToken token = default)
        {
            path = Path.GetFullPath(path); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                { await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true }, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true); }
                token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
