using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;
using Timer = System.Threading.Timer;

namespace ProjectionMapper.Services
{
    /// <summary>Samples real process/render observations on a worker timer. Never infers hardware acceptance from elapsed time.</summary>
    public sealed class RehearsalRecorder : IDisposable
    {
        private readonly object _gate = new();
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly Func<(bool Blackout, List<OutputFrameObservation> Outputs)> _observe;
        private readonly Queue<RehearsalSample> _samples = new();
        private readonly List<RehearsalEvent> _events = new();
        private readonly Timer _timer;
        private readonly int _limit;
        private readonly RehearsalReport _report;
        private TimeSpan _lastCpu, _lastTime;
        private bool _stopped;
        public RehearsalRecorder(string name, PreflightReport preflight, Func<(bool, List<OutputFrameObservation>)> observe, TimeSpan interval, int maximumSamples)
        {
            if (interval <= TimeSpan.Zero || maximumSamples < 1) throw new ArgumentOutOfRangeException(nameof(interval));
            _observe = observe; _limit = maximumSamples;
            _report = new() { ProjectName = name, StartedUtc = DateTime.UtcNow, InitialPreflight = preflight };
            using var process = Process.GetCurrentProcess(); _lastCpu = process.TotalProcessorTime;
            _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, interval);
        }
        private void Sample()
        {
            lock (_gate)
            {
                if (_stopped) return;
                try
                {
                    using var process = Process.GetCurrentProcess(); process.Refresh();
                    var now = _elapsed.Elapsed; var cpu = process.TotalProcessorTime;
                    double percent = now > _lastTime ? Math.Clamp((cpu - _lastCpu).TotalMilliseconds / (now - _lastTime).TotalMilliseconds * 100 / Environment.ProcessorCount, 0, 100) : 0;
                    _lastCpu = cpu; _lastTime = now;
                    var output = _observe();
                    var sample = new RehearsalSample(DateTime.UtcNow, now.TotalSeconds, process.PrivateMemorySize64, process.WorkingSet64, percent, output.Blackout, output.Outputs);
                    _samples.Enqueue(sample); while (_samples.Count > _limit) _samples.Dequeue();
                    _report.TotalSamples++; _report.PeakPrivateBytes = Math.Max(_report.PeakPrivateBytes, sample.PrivateBytes); _report.PeakWorkingSetBytes = Math.Max(_report.PeakWorkingSetBytes, sample.WorkingSetBytes);
                }
                catch (Exception ex) { Record("sampling-error", ex.Message); Debug.WriteLine($"Rehearsal sampling failed: {ex}"); }
            }
        }
        public void Record(string kind, string message)
        { lock (_gate) { if (_stopped) return; if (_events.Count < 1000) _events.Add(new(DateTime.UtcNow, kind, message)); } }
        public Task<RehearsalReport> StopAsync(string notes = "") => Task.Run(() =>
        {
            lock (_gate)
            {
                if (!_stopped)
                {
                    Sample(); _stopped = true; _elapsed.Stop(); _timer.Dispose();
                    _report.EndedUtc = DateTime.UtcNow; _report.ElapsedSeconds = _elapsed.Elapsed.TotalSeconds; _report.OperatorNotes = notes;
                    _report.Samples = _samples.ToList(); _report.Events = _events.ToList();
                }
                return _report;
            }
        });
        public void Dispose() { lock (_gate) { _stopped = true; _elapsed.Stop(); _timer.Dispose(); } }
    }
}
