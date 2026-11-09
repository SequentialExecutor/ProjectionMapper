using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using ProjectionMapper.Models;
using ProjectionMapper.Views;

namespace ProjectionMapper
{
    public partial class MainWindow
    {
        private ProjectionPreviewWindow? _projectionPreview;
        private OutputDiagnosticsWindow? _outputDiagnostics;
        private void OpenProjectionPreview()
        {
            try
            {
                if (_projectionPreview != null) { _projectionPreview.Close(); return; }
                var preview = new ProjectionPreviewWindow(PART_OutputHost) { Owner = this, DataContext = _vm };
                _projectionPreview = preview;
                preview.Closed += (_, _) => { if (ReferenceEquals(_projectionPreview, preview)) _projectionPreview = null; };
                // Place on the editor's actual monitor, using native pixel bounds to avoid DPI virtualization.
                var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Windows could not locate the editor's display for preview.");
                var handle = new WindowInteropHelper(preview).EnsureHandle();
                preview.Show();
                if (!SetWindowPos(handle, IntPtr.Zero, info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top, SWP_SHOWWINDOW | 0x0004))
                    throw new InvalidOperationException("Windows could not position the preview. Check the display connection.");
                preview.Activate();
            }
            catch (Exception error)
            {
                _projectionPreview?.Close(); _projectionPreview = null;
                Debug.WriteLine($"Projection preview failed: {error}"); _vm.StatusText = error.Message;
            }
        }
        private OutputDiagnosticReport CaptureOutputDiagnostics()
        {
            Dispatcher.VerifyAccess();
            var surfaces = _vm.ImportedVideos.SelectMany(source => source.MeshLayers.Select(mesh => new DiagnosticSurface(
                mesh.Id ?? string.Empty, mesh.Name, source.Name, mesh.TargetMonitorIndex,
                _videoService.IsSourceActiveForRendering(source.Id),
                (mesh.Id != null && _rendererManager.TryGetArtwork(mesh.Id, out _)) || _videoService.TryGetLastFrame(source.Id, out _), mesh.Opacity))).ToList();
            return new OutputDiagnosticReport
            {
                ApplicationVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "Unknown",
                RuntimeVersion = Environment.Version.ToString(), OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                RendererBackend = _softwareRenderer.GetType().Name,
                ProjectName = _vm.ActiveProject?.Name ?? "Untitled project", CurrentCue = _playlistService.CurrentGroup?.Name ?? string.Empty,
                Playing = _vm.IsPlaying, PlaylistMode = _vm.IsPlaylistMode, Blackout = _rendererManager.IsBlackout, OutputReviewRequired = _outputsNeedSetup,
                Surfaces = surfaces,
                Displays = _detectedDisplays.Select(display =>
                {
                    var size = _rendererManager.GetMonitorRendererSize(display.Index);
                    return new DiagnosticDisplay(display.Index, display.DeviceName, true, display.RenderWidth, display.RenderHeight,
                        _activeMonitorWindows.TryGetValue(display.Index, out var window) && window.IsVisible,
                        size.Width, size.Height, _rendererManager.GetOutputFrameCount(display.Index), _rendererManager.GetLastOutputFrameUtc(display.Index),
                        _rendererManager.GetMonitorRenderMilliseconds(display.Index));
                }).ToList()
            };
        }
        private void OutputStatus_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_outputDiagnostics != null) { _outputDiagnostics.Activate(); return; }
                var window = new OutputDiagnosticsWindow(CaptureOutputDiagnostics) { Owner = this };
                _outputDiagnostics = window;
                window.Closed += (_, _) => { if (ReferenceEquals(_outputDiagnostics, window)) _outputDiagnostics = null; };
                window.Show();
            }
            catch (Exception error) { Debug.WriteLine($"Output status failed: {error}"); _vm.StatusText = error.Message; }
        }
    }
}
