using System;
using System.Diagnostics;
using System.Threading;
using Timer = System.Threading.Timer;

namespace ProjectionMapper.Services
{
    /// <summary>Offline still-image cue clock. No UI timers, decoding, or network dependency.</summary>
    public sealed class StillPlaybackClock : IDisposable
    {
        private readonly object _gate = new();
        private TimeSpan _duration;
        private readonly Action _completed;
        private readonly Stopwatch _elapsed = new();
        private readonly Timer _timer;
        private TimeSpan _remaining;
        private bool _playing, _loop, _disposed;
        public StillPlaybackClock(TimeSpan duration, Action completed)
        {
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(duration));
            _duration = _remaining = duration; _completed = completed;
            _timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        public void SetDuration(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(duration));
            lock (_gate)
            {
                if (_disposed || _duration == duration) return;
                _duration = _remaining = duration; _elapsed.Reset();
                if (_playing) { _elapsed.Start(); Schedule(); }
            }
        }
        public void SetLoop(bool loop)
        {
            lock (_gate) { _loop = loop; if (_playing) Schedule(); }
        }
        public void Start(bool restart)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (restart) { _remaining = _duration; _elapsed.Reset(); }
                _playing = true; _elapsed.Restart(); Schedule();
            }
        }
        private void Schedule() => _timer.Change(_loop ? Timeout.InfiniteTimeSpan : _remaining, Timeout.InfiniteTimeSpan);
        public void Pause()
        {
            lock (_gate)
            {
                if (_disposed || !_playing) return;
                _remaining = _remaining > _elapsed.Elapsed ? _remaining - _elapsed.Elapsed : TimeSpan.FromMilliseconds(1);
                _playing = false; _elapsed.Reset(); _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        private void Tick()
        {
            lock (_gate)
            {
                if (_disposed || !_playing || _loop) return;
                // A callback queued before Pause/Restart must not end a new cue early.
                if (_elapsed.Elapsed + TimeSpan.FromMilliseconds(1) < _remaining) { _timer.Change(_remaining - _elapsed.Elapsed, Timeout.InfiniteTimeSpan); return; }
                _playing = false; _remaining = _duration; _elapsed.Reset();
            }
            try { _completed(); } catch (Exception ex) { Debug.WriteLine($"Still cue completion failed: {ex.GetType().Name}"); }
        }
        public void Dispose() { lock (_gate) { _disposed = true; _timer.Dispose(); } }
    }
}
