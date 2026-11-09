using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper.Models;
using ProjectionMapper.Utilities;

namespace ProjectionMapper.Rendering
{
    /// <summary>Shared runtime state is separate from saved show data. Blackout never changes the show or playback position.</summary>
    public sealed class LiveOutputState
    {
        private double _gain = 1;
        public double Gain { get => Volatile.Read(ref _gain); set => Volatile.Write(ref _gain, Math.Clamp(value, 0, 1)); }
    }
    public sealed partial class SoftwareRenderer
    {
        internal LiveOutputState LiveState { get; set; } = new();
        private OutputAppearance? _outputAppearance;
        private readonly ConcurrentDictionary<string, SurfaceState> _surfaceStates = new();
        internal void SetSurfaceStyle(string id, SurfaceAppearance? style)
        {
            if (style == null || style.IsDefault) { _surfaceStates.TryRemove(id, out _); return; }
            if (_surfaceStates.TryGetValue(id, out var prior) && ReferenceEquals(prior.Style, style)) return;
            _surfaceStates[id] = new SurfaceState(style);
        }
        internal void SetOutputStyle(OutputAppearance? style) => Volatile.Write(ref _outputAppearance, style);
        private BitmapSource CorrectOutput(BitmapSource frame)
        {
            var style = Volatile.Read(ref _outputAppearance); double gain = LiveState.Gain;
            if ((style == null || style.IsDefault) && gain == 1) return frame;
            var input = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(input, frame.PixelWidth * 4, 0);
            // The compositor draws onto opaque black, so Pbgra32 and Bgra32 have identical pixel values here.
            var pixels = OutputPixels.Apply(input, frame.PixelWidth, frame.PixelHeight, style, gain);
            var result = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, frame.PixelWidth * 4);
            result.Freeze(); return result;
        }
        private sealed class SurfaceState
        {
            public SurfaceAppearance Style { get; }
            private SurfacePixelMap? _map;
            private BitmapSource? _lastSource, _lastResult;
            private int _width, _height;
            public SurfaceState(SurfaceAppearance style) { Style = style; }
            public BitmapSource Process(BitmapSource source)
            {
                if (ReferenceEquals(source, _lastSource) && _lastResult != null) return _lastResult;
                if (_map == null || _width != source.PixelWidth || _height != source.PixelHeight)
                {
                    _width = source.PixelWidth; _height = source.PixelHeight; _map = new SurfacePixelMap(Style, _width, _height);
                }
                BitmapSource input = source;
                if (input.Format != PixelFormats.Bgra32)
                {
                    var converted = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0); converted.Freeze(); input = converted;
                }
                var pixels = new byte[_width * _height * 4]; input.CopyPixels(pixels, _width * 4, 0);
                var result = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, _map.Apply(pixels), _width * 4);
                result.Freeze(); _lastSource = source; _lastResult = result; return result;
            }
        }
    }

    public sealed partial class RendererManager
    {
        private readonly ConcurrentDictionary<string, SurfaceAppearance> _surfaceAppearances = new();
        private readonly ConcurrentDictionary<int, OutputAppearance> _outputAppearances = new();
        private readonly LiveOutputState _liveState = new();
        private volatile bool _blackout, _performanceMode;
        public bool IsBlackout => _blackout;
        public bool IsPerformanceMode => _performanceMode;
        private static readonly BitmapSource Black = CreateBlack();
        private static BitmapSource CreateBlack()
        {
            var frame = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4); frame.Freeze(); return frame;
        }
        public void SetSurfaceAppearance(string id, SurfaceAppearance style)
        {
            ArtistRules.Validate(style); var snapshot = style.Copy(); _surfaceAppearances[id] = snapshot;
            RouteSurfaceStyle(id, _renderer);
            foreach (var renderer in _monitorRenderers.Values) RouteSurfaceStyle(id, renderer);
        }
        public void ForgetSurfaceAppearance(string id)
        {
            _surfaceAppearances.TryRemove(id, out _);
            RouteSurfaceStyle(id, _renderer); foreach (var renderer in _monitorRenderers.Values) RouteSurfaceStyle(id, renderer);
        }
        private void RouteSurfaceStyle(string id, IRenderer renderer)
        {
            if (renderer is SoftwareRenderer software)
            {
                _surfaceAppearances.TryGetValue(id, out var style); software.SetSurfaceStyle(id, style);
            }
        }
        public void SetOutputAppearance(int index, OutputAppearance? style)
        {
            if (style == null) _outputAppearances.TryRemove(index, out _);
            else { ArtistRules.Validate(style); _outputAppearances[index] = style.Copy(); }
            if (_monitorRenderers.TryGetValue(index, out var renderer) && renderer is SoftwareRenderer software) ConfigureArtistOutput(index, software);
        }
        private void ConfigureArtistOutput(int index, SoftwareRenderer renderer)
        {
            renderer.LiveState = _liveState;
            _outputAppearances.TryGetValue(index, out var style); renderer.SetOutputStyle(style);
        }
        public void ClearArtistSettings()
        {
            foreach (var id in _surfaceAppearances.Keys) { if (_renderer is SoftwareRenderer software) software.SetSurfaceStyle(id, null); foreach (var renderer in _monitorRenderers.Values) if (renderer is SoftwareRenderer output) output.SetSurfaceStyle(id, null); }
            _surfaceAppearances.Clear();
            foreach (var index in _outputAppearances.Keys) SetOutputAppearance(index, null);
            _liveState.Gain = 1;
        }
        internal BitmapSource? ResolveMonitorPresentation(int index, BitmapSource? frame) => _blackout ? Black :
            (!_performanceMode && _calibrationPatterns.TryGetValue(index, out var pattern) ? pattern : frame);
        public void SetBlackout(bool blackout)
        {
            _blackout = blackout;
            // Re-evaluate at dispatcher execution time too: queued frames cannot bypass the current gate.
            foreach (var index in _fullscreenWindows.Keys) { _latestMonitorFrames.TryGetValue(index, out var latest); OnMonitorFrameReady(index, latest); RefreshMeshOverlaysForMonitor(index); }
            RefreshMeshOverlaysForMainHost();
        }
        public void SetPerformanceMode(bool enabled)
        {
            _performanceMode = enabled;
            foreach (var index in _fullscreenWindows.Keys) RefreshMeshOverlaysForMonitor(index);
            RefreshMeshOverlaysForMainHost();
        }
        public async Task FadeToAsync(double target, TimeSpan duration, CancellationToken token)
        {
            if (!double.IsFinite(target) || target < 0 || target > 1 || duration < TimeSpan.Zero || duration > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(target));
            double start = _liveState.Gain;
            if (duration <= TimeSpan.Zero) { _liveState.Gain = target; return; }
            var time = Stopwatch.StartNew();
            while (time.Elapsed < duration)
            {
                token.ThrowIfCancellationRequested();
                _liveState.Gain = start + (target - start) * Math.Clamp(time.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                await Task.Delay(20, token).ConfigureAwait(false);
            }
            _liveState.Gain = target;
        }
    }
}
