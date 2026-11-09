using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectionMapper.Services
{
    /// <summary>The decoder and preflight use the same executable resolution, never different PATH guesses.</summary>
    public static class FfmpegTools
    {
        public static string Resolve(string? explicitPath = null)
        {
            string? configured = explicitPath;
            string settings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (string.IsNullOrWhiteSpace(configured) && File.Exists(settings))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(settings), new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (json.RootElement.TryGetProperty("FFmpeg", out var section) && section.TryGetProperty("Path", out var property)) configured = property.GetString();
            }
            string executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathRooted(configured) && (!string.IsNullOrEmpty(Path.GetDirectoryName(configured)) || Directory.Exists(Path.Combine(AppContext.BaseDirectory, configured))))
                    configured = Path.GetFullPath(configured, AppContext.BaseDirectory);
                return Directory.Exists(configured) ? Path.Combine(configured, executable) : configured;
            }
            string local = Path.Combine(AppContext.BaseDirectory, "Native", "ffmpeg", executable);
            return File.Exists(local) ? local : executable;
        }
        public static string ProbeFor(string ffmpeg)
        {
            string name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
            return string.IsNullOrEmpty(Path.GetDirectoryName(ffmpeg)) ? name : Path.Combine(Path.GetDirectoryName(ffmpeg)!, name);
        }
        public static async Task<string> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken token)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); cancellation.CancelAfter(timeout);
            using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            try
            {
                if (!process.Start()) throw new IOException("The media tool could not be started.");
                async Task<string> ReadBoundedAsync(StreamReader reader)
                {
                    var text = new StringBuilder(); var buffer = new char[2048]; int count;
                    while ((count = await reader.ReadAsync(buffer, cancellation.Token).ConfigureAwait(false)) > 0)
                    { if (text.Length + count > 1024 * 1024) throw new IOException("The media tool response is too large."); text.Append(buffer, 0, count); }
                    return text.ToString();
                }
                var stdout = ReadBoundedAsync(process.StandardOutput); var stderr = ReadBoundedAsync(process.StandardError);
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellation.Token)).ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException("The media tool rejected this request. Verify installation and media compatibility.");
                return stdout.Result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("The media tool did not respond before the preflight timeout."); }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) { Debug.WriteLine($"Media probe cleanup: {ex.Message}"); }
            }
        }
    }
}
