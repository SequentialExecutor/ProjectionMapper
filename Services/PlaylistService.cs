using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;
using ProjectionMapper.Utilities;

namespace ProjectionMapper.Services
{
    /// <summary>Serial cue controller. Commands, EOFs and timer callbacks share one queue; stale visits cannot advance a newer cue.</summary>
    public sealed class PlaylistService : IDisposable
    {
        private readonly VideoService _video;
        private readonly object _state = new();
        private readonly SemaphoreSlim _commands = new(1, 1);
        private List<PlaylistGroupModel> _groups = new();
        private List<string> _active = new();
        private readonly HashSet<string> _finished = new();
        private int _index = -1, _sequence;
        private long _visit, _controlGeneration;
        private bool _playing, _paused, _disposed;
        private CancellationTokenSource _session = new();
        private StillPlaybackClock? _clock;
        public event Action<int>? GroupChanged;
        public event Action? PlaylistCompleted;
        public event Action<int, int>? GroupAdvancing;
        public event Action<PlaylistPlaybackState>? PlaybackStateChanged;
        public event Action<string>? ErrorOccurred;
        public Func<double, TimeSpan, CancellationToken, Task>? FadeRequestedAsync { get; set; }
        public int GroupTransitionDelayMs { get; set; }
        public bool EnableLooping { get; set; } = true;
        public int CurrentGroupIndex { get { lock (_state) return _index; } }
        public PlaylistGroupModel? CurrentGroup { get { lock (_state) return _index >= 0 && _index < _groups.Count ? Copy(_groups[_index]) : null; } }
        public PlaylistGroupModel? NextGroup { get { lock (_state) { int next = NextIndex(); return next >= 0 ? Copy(_groups[next]) : null; } } }
        public bool IsPlaying { get { lock (_state) return _playing && !_paused; } }
        public bool IsPaused { get { lock (_state) return _paused; } }
        public PlaylistService(VideoService videoService)
        { _video = videoService ?? throw new ArgumentNullException(nameof(videoService)); _video.VideoCompleted += OnVideoCompleted; }
        public static PlaylistGroupModel Copy(PlaylistGroupModel group) => new()
        {
            Id = group.Id, Name = group.Name, Order = group.Order, SourceIds = group.SourceIds.ToList(), PlaybackMode = group.PlaybackMode,
            AdvanceMode = group.AdvanceMode, HoldSeconds = group.HoldSeconds, FadeSeconds = group.FadeSeconds
        };
        private int NextIndex() => _groups.Count == 0 ? -1 : _index + 1 < _groups.Count ? _index + 1 : EnableLooping ? 0 : -1;
        private Task Run(Func<Task> action)
        {
            // Reserve the queue position synchronously; Task.Run scheduling must not reorder Go/Stop/Load commands.
            var ready = _commands.WaitAsync();
            return Task.Run(async () =>
            {
                await ready.ConfigureAwait(false);
                try { if (!_disposed) await action().ConfigureAwait(false); }
                catch (OperationCanceledException) { Debug.WriteLine("Cue operation cancelled."); }
                catch (Exception ex) { Debug.WriteLine($"Cue operation failed: {ex}"); ErrorOccurred?.Invoke(ex.Message); throw; }
                finally { _commands.Release(); }
            });
        }
        private CancellationToken Token { get { lock (_state) return _session.Token; } }
        private long Visit { get { lock (_state) return _visit; } }
        private bool IsCurrent(long visit) { lock (_state) return !_disposed && _playing && !_paused && _visit == visit; }
        private async Task Fade(double level, double seconds, CancellationToken token)
        { if (FadeRequestedAsync != null) await FadeRequestedAsync(level, TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false); }
        public Task StartPlaylistAsync(List<PlaylistGroupModel> groups)
        {
            long generation; lock (_state) generation = _controlGeneration;
            var snapshot = groups.OrderBy(g => g.Order).Select(Copy).ToList();
            foreach (var cue in snapshot) ArtistRules.Validate(cue);
            return Run(async () =>
            {
                lock (_state) { if (generation != _controlGeneration) return; }
                if (snapshot.Count == 0) throw new InvalidOperationException("Create a cue with available media before starting the playlist.");
                ResetSession();
                lock (_state) { _groups = snapshot; _index = 0; _playing = true; _paused = false; }
                _video.EnablePlaylistMode(); _video.DisableLoopingForAll();
                await Fade(0, 0, Token).ConfigureAwait(false);
                await ActivateAsync(Token).ConfigureAwait(false);
            });
        }
        private void ResetSession()
        {
            lock (_state) { _session.Cancel(); _session.Dispose(); _session = new(); _visit++; }
            _clock?.Dispose(); _clock = null;
        }
        public Task PauseCurrentGroupAsync() => Run(async () =>
        {
            lock (_state) { if (!_playing || _paused) return; _paused = true; }
            _clock?.Pause();
            await Task.WhenAll(_active.Select(_video.PauseLayerAsync)).ConfigureAwait(false);
            PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Paused);
        });
        public Task ResumeCurrentGroupAsync() => Run(async () =>
        {
            lock (_state) { if (!_playing || !_paused) return; _paused = false; }
            var ids = CurrentGroup?.PlaybackMode == GroupPlaybackMode.Sequential ? _active.Skip(_sequence).Take(1).ToList() : _active;
            await Task.WhenAll(ids.Where(id => !_finished.Contains(id)).Select(_video.ResumeLayerAsync)).ConfigureAwait(false);
            EnableAudio(ids.Where(id => !_finished.Contains(id)).ToList());
            _clock?.Start(restart: false);
            PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Playing);
        });
        public Task StopPlaylistAsync(bool reEnableLooping = false, bool preserveSources = false)
        {
            // Cancel waits/fades immediately, before the stop reaches the command queue.
            lock (_state) { _session.Cancel(); _visit++; _controlGeneration++; _playing = false; _paused = false; }
            return Run(async () =>
            {
                _clock?.Dispose(); _clock = null;
                _video.SetActiveRenderLayers(Array.Empty<string>());
                await _video.HideAllExceptGroupAsync(new()).ConfigureAwait(false);
                if (preserveSources) await _video.PauseAllAsync().ConfigureAwait(false);
                else await _video.StopAllAsync().ConfigureAwait(false);
                if (reEnableLooping) { _video.DisablePlaylistMode(); _video.EnableLoopingForAll(); }
                lock (_state) _index = -1;
                _finished.Clear(); _active.Clear();
                await Fade(1, 0, CancellationToken.None).ConfigureAwait(false);
                PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Stopped); GroupChanged?.Invoke(-1);
            });
        }
        public Task RestartPlaylistAsync()
        {
            List<PlaylistGroupModel> copy; lock (_state) copy = _groups.Select(Copy).ToList();
            return StartPlaylistAsync(copy);
        }
        public Task AdvanceToNextGroupAsync()
        {
            long visit = Visit;
            return Run(async () => { if (IsCurrent(visit)) await AdvanceAsync(Token).ConfigureAwait(false); });
        }
        public Task JumpToGroupAsync(int groupIndex)
        {
            long generation; lock (_state) generation = _controlGeneration;
            return Run(async () =>
            {
                lock (_state) { if (generation != _controlGeneration) return; }
            lock (_state) { if (groupIndex < 0 || groupIndex >= _groups.Count) return; }
            ResetSession();
            var cue = CurrentGroup;
            await Fade(0, cue?.FadeSeconds ?? 0, Token).ConfigureAwait(false);
            lock (_state) { _index = groupIndex; _playing = true; _paused = false; }
            _video.EnablePlaylistMode(); _video.DisableLoopingForAll();
            await ActivateAsync(Token).ConfigureAwait(false);
            });
        }
        private async Task AdvanceAsync(CancellationToken token)
        {
            var outgoing = CurrentGroup; int next; int old;
            lock (_state) { old = _index; next = NextIndex(); _visit++; }
            _clock?.Dispose(); _clock = null;
            GroupAdvancing?.Invoke(old, next);
            await Fade(0, outgoing?.FadeSeconds ?? 0, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (next < 0)
            {
                _video.SetActiveRenderLayers(Array.Empty<string>());
                await _video.HideAllExceptGroupAsync(new()).ConfigureAwait(false);
                await _video.PauseAllAsync().ConfigureAwait(false);
                lock (_state) { _playing = false; _paused = false; _index = -1; }
                await Fade(1, 0, token).ConfigureAwait(false);
                PlaylistCompleted?.Invoke(); GroupChanged?.Invoke(-1); PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Stopped); return;
            }
            if (next == 0) PlaylistCompleted?.Invoke();
            lock (_state) { _index = next; _paused = false; }
            if (GroupTransitionDelayMs > 0) await Task.Delay(GroupTransitionDelayMs, token).ConfigureAwait(false);
            await ActivateAsync(token).ConfigureAwait(false);
        }
        private async Task ActivateAsync(CancellationToken token)
        {
            var cue = CurrentGroup; if (cue == null) return;
            _clock?.Dispose(); _clock = null; _finished.Clear(); _sequence = 0;
            await _video.PauseAllAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested();
            _video.StopAllAudio();
            _active = _video.GetActiveLayerIds(cue.SourceIds).Distinct().ToList();
            _video.SetActiveRenderLayers(Array.Empty<string>());
            await _video.HideAllExceptGroupAsync(new()).ConfigureAwait(false);
            if (_active.Count == 0)
            {
                lock (_state) { _playing = false; _paused = false; }
                await Fade(1, 0, token).ConfigureAwait(false);
                PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Stopped);
                throw new InvalidOperationException($"Cue '{cue.Name}' has no available sources. Relink its media before running the show.");
            }
            var visible = cue.PlaybackMode == GroupPlaybackMode.Sequential ? _active.Take(1).ToList() : _active.ToList();
            // Filter first; a frame queued before the transition cannot expose an inactive source.
            _video.SetActiveRenderLayers(visible);
            await _video.RestartGroupVideosAsync(visible).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            EnableAudio(visible);
            await Fade(1, cue.FadeSeconds, token).ConfigureAwait(false);
            if (cue.AdvanceMode == CueAdvanceMode.Timed)
            {
                long visit = Visit;
                _clock = new StillPlaybackClock(TimeSpan.FromSeconds(cue.HoldSeconds), () => QueueCompletion(visit, null)); _clock.Start(true);
            }
            GroupChanged?.Invoke(CurrentGroupIndex); PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Playing);
        }
        private void EnableAudio(List<string> ids)
        {
            _video.StopAllAudio();
            var preferred = ids.FirstOrDefault(_video.ShouldPlayAudio);
            if (preferred != null) _video.StartAudioForLayer(preferred);
        }
        private void OnVideoCompleted(string id) => QueueCompletion(Visit, id);
        private void QueueCompletion(long visit, string? source)
        {
            if (!IsCurrent(visit)) return;
            _ = Run(async () =>
            {
                if (!IsCurrent(visit)) return;
                var cue = CurrentGroup; if (cue == null) return;
                if (source == null)
                { if (cue.AdvanceMode == CueAdvanceMode.Timed) await AdvanceAsync(Token).ConfigureAwait(false); return; }
                if (!_active.Contains(source) || _finished.Contains(source)) return;
                if (cue.PlaybackMode == GroupPlaybackMode.Sequential && _active[_sequence] != source) return;
                _finished.Add(source);
                if (cue.PlaybackMode == GroupPlaybackMode.Sequential && _sequence + 1 < _active.Count)
                {
                    await _video.PauseLayerAsync(source).ConfigureAwait(false); _video.StopAudioForLayer(source);
                    _sequence++;
                    var visible = new List<string> { _active[_sequence] };
                    _video.SetActiveRenderLayers(visible); await _video.HideAllExceptGroupAsync(visible).ConfigureAwait(false);
                    await _video.RestartGroupVideosAsync(visible).ConfigureAwait(false); EnableAudio(visible);
                }
                else if (_finished.Count == _active.Count && cue.AdvanceMode == CueAdvanceMode.MediaEnd)
                    await AdvanceAsync(Token).ConfigureAwait(false);
                // Manual/timed cues hold their final frames until Go/the cue timer.
            }).ContinueWith(t => Debug.WriteLine($"Cue completion failed: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
        }
        public void Dispose()
        {
            lock (_state) { if (_disposed) return; _disposed = true; _playing = false; _session.Cancel(); _visit++; }
            _video.VideoCompleted -= OnVideoCompleted; _clock?.Dispose();
            // A queued command may still inspect the cancellation token; do not dispose its source underneath it.
        }
    }
    public enum PlaylistPlaybackState { Stopped, Playing, Paused }
}
