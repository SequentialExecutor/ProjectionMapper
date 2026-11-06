using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>Background recovery snapshots, isolated from explicit project saves.</summary>
    public sealed class ProjectRecoveryService : IAsyncDisposable
    {
        private readonly ProjectService _store = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly string _directory;
        private readonly string _path;
        private readonly FileStream _sessionLock;
        private Task? _worker;
        private string? _restoredPath;

        public ProjectRecoveryService(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pmproj");
            // A running instance holds its lock, so other instances do not offer its files for recovery.
            _sessionLock = new FileStream(_path + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }

        public string? FindLatestRecoverable()
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*.pmproj").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (path == _path) continue;
                try
                {
                    using var probe = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return path;
                }
                catch (IOException ex) { Debug.WriteLine($"Recovery session unavailable: {ex.Message}"); }
            }
            return null;
        }

        public void MarkRestored(string path) => _restoredPath = path;

        public void Start(TimeSpan interval, Func<Task<ProjectModel?>> capture, Action<string> reportFailure)
        {
            if (_worker != null) throw new InvalidOperationException("Recovery worker is already running.");
            _worker = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(interval);
                try
                {
                    while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            // Only capture touches the UI dispatcher; all disk work stays off it.
                            var snapshot = await capture().ConfigureAwait(false);
                            if (snapshot != null && !await _store.SaveAsync(snapshot, _path, _stop.Token).ConfigureAwait(false))
                                reportFailure(_store.LastError ?? "Unknown recovery save error.");
                        }
                        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Recovery snapshot failed: {ex}");
                            reportFailure(ex.Message);
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                { Debug.WriteLine("Recovery worker stopped."); }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_worker != null) await _worker.ConfigureAwait(false);
            _sessionLock.Dispose();
            _stop.Dispose();
            // A normal close has either saved or explicitly discarded changes. Crash files survive.
            DeleteSnapshot(_path);
            if (_restoredPath != null) DeleteSnapshot(_restoredPath);
        }

        private static void DeleteSnapshot(string path)
        {
            foreach (var file in new[] { path, path + ".bak", path + ".lock" })
            {
                try { File.Delete(file); }
                catch (Exception ex) { Debug.WriteLine($"Recovery cleanup failed for {file}: {ex}"); }
            }
        }
    }
}
