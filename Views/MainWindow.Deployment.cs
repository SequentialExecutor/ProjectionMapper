using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Views;

namespace ProjectionMapper
{
    public partial class MainWindow
    {
        private readonly List<DisplayDescriptor> _detectedDisplays = new();
        private OutputLayout? _approvedOutputLayout;
        private bool _outputsNeedSetup;
        private int _performanceRequest;
        private CancellationTokenSource? _performanceCancellation;
        private DeploymentWindow? _deploymentWindow;
        private RehearsalRecorder? _rehearsalRecorder;
        private RehearsalReport? _lastRehearsal;
        private string _rehearsalNotes = string.Empty;
        private OutputLayout CaptureOutputLayout() => new() { ComputerId = WindowsDisplayIdentity.ComputerId, Displays = _detectedDisplays.Select(d => d with { }).ToList() };
        private void InitializeDeployment()
        {
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            _playlistService.GroupChanged += _ => _rehearsalRecorder?.Record("cue", _playlistService.CurrentGroup?.Name ?? "Stopped");
            _playlistService.ErrorOccurred += message => _rehearsalRecorder?.Record("cue-error", message);
        }
        private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_closingInProgress) return;
                bool routed = _activeMonitorWindows.Count > 0 || _rendererManager.CalibrationMonitorIndices.Length > 0 ||
                    _vm.ImportedVideos.Any(v => (v.HostLayer?.TargetMonitorIndex ?? -1) >= 0 || v.MeshLayers.Any(m => m.TargetMonitorIndex >= 0)) || _vm.ActiveProject?.OutputAppearances.Count > 0;
                if (routed) RequireOutputReview(); else { _approvedOutputLayout = null; _outputsNeedSetup = false; }
                EnumerateMonitors();
            }
            catch (Exception ex) { Debug.WriteLine($"Display change handling failed: {ex}"); _vm.StatusText = ex.Message; }
        });
        private void RequireOutputReview()
        {
            _outputsNeedSetup = true; _rendererManager.SetBlackout(true);
            PART_Blackout.Content = "OUTPUT SETUP REQUIRED";
            PART_Performance.IsChecked = false;
            foreach (int index in _activeMonitorWindows.Keys.ToArray()) HideMonitorWindow(index);
            _rehearsalRecorder?.Record("display-change", "Output layout changed; projector output blocked pending explicit reassignment.");
            _vm.StatusText = "Displays changed. Open Output setup; projector output stays black until reviewed.";
        }
        private bool OutputsMayOpen()
        {
            if (_outputsNeedSetup) return false;
            var current = CaptureOutputLayout();
            if (_approvedOutputLayout != null && !DeploymentProject.SameLayout(_approvedOutputLayout, current) && _vm.ImportedVideos.Any(v => v.MeshLayers.Any(m => m.TargetMonitorIndex >= 0)))
            { RequireOutputReview(); return false; }
            _approvedOutputLayout = current; return true;
        }
        private bool PrepareProjectOutputs(ProjectModel project)
        {
            var current = CaptureOutputLayout();
            if (!DeploymentProject.NeedsOutputSetup(project, current)) { project.OutputLayout = current.Copy(); return true; }
            var window = new OutputSetupWindow(project, current) { Owner = this };
            if (window.ShowDialog() != true) return false;
            DeploymentProject.RemapOutputs(project, current, window.Mapping, window.CopyColor); return true;
        }
        private void OutputSetup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnumerateMonitors(); var snapshot = BuildProjectModel(); var current = CaptureOutputLayout();
                var window = new OutputSetupWindow(snapshot, current) { Owner = this };
                if (window.ShowDialog() != true) return;
                DeploymentProject.RemapOutputs(snapshot, current, window.Mapping, window.CopyColor);
                foreach (var video in _vm.ImportedVideos)
                {
                    var saved = snapshot.ImportedVideos.Single(v => v.Id == video.Id);
                    if (video.HostLayer != null) video.HostLayer.TargetMonitorIndex = saved.TargetMonitorIndex;
                    foreach (var mesh in video.MeshLayers)
                    { mesh.TargetMonitorIndex = saved.MeshLayers.Single(m => m.Id == mesh.Id).TargetMonitorIndex; _videoService.RefreshMeshLayerRendering(mesh.Id!); }
                }
                _vm.ActiveProject ??= new ProjectModel();
                for (int i = 0; i < _vm.ActiveProject.Surfaces.Count && i < snapshot.Surfaces.Count; i++)
                    for (int j = 0; j < _vm.ActiveProject.Surfaces[i].Layers.Count && j < snapshot.Surfaces[i].Layers.Count; j++)
                        _vm.ActiveProject.Surfaces[i].Layers[j].TargetMonitorIndex = snapshot.Surfaces[i].Layers[j].TargetMonitorIndex;
                _vm.ActiveProject.OutputAppearances = snapshot.OutputAppearances;
                _vm.ActiveProject.OutputLayout = current.Copy(); _approvedOutputLayout = current.Copy(); _outputsNeedSetup = false;
                ApplySavedOutputAppearances(_vm.ActiveProject); EnsureMonitorWindows(force: true); _vm.MarkProjectDirty();
                PART_Blackout.Content = _rendererManager.IsBlackout ? "RESTORE OUTPUT (F12)" : "BLACKOUT (F12)";
                _vm.StatusText = "Output assignments reviewed. Verify physical alignment; F12 restores output if blacked out.";
            }
            catch (Exception ex) { Debug.WriteLine($"Output setup failed: {ex}"); _vm.StatusText = ex.Message; }
        }
        private void Deployment_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_deploymentWindow?.IsVisible == true) { _deploymentWindow.Activate(); return; }
                _deploymentWindow = new DeploymentWindow(BuildProjectModel, CaptureOutputLayout, () => _currentProjectPath,
                    async report =>
                    {
                        if (_rehearsalRecorder != null) throw new InvalidOperationException("A rehearsal recording is already running.");
                        _lastRehearsal = null; _rehearsalNotes = _deploymentWindow?.OperatorNotes ?? string.Empty;
                        var settings = DeploymentSettings.Load();
                        _rehearsalRecorder = new RehearsalRecorder(_vm.ActiveProject?.Name ?? "Untitled", report,
                            () => (_rendererManager.IsBlackout, _rendererManager.ObserveOutputs()), TimeSpan.FromSeconds(settings.RehearsalSampleSeconds), settings.MaximumSamples);
                        await Task.CompletedTask;
                    }, StopRehearsalAsync, () => _rehearsalRecorder != null, () => !_outputsNeedSetup && DeploymentProject.SameLayout(_approvedOutputLayout, CaptureOutputLayout())) { Owner = this };
                var window = _deploymentWindow;
                window.OperatorNotes = _rehearsalNotes;
                window.Closed += (_, _) => _rehearsalNotes = window.OperatorNotes;
                if (_lastRehearsal != null) _deploymentWindow.ShowRehearsal(_lastRehearsal);
                _deploymentWindow.Show();
            }
            catch (Exception ex) { Debug.WriteLine($"Deployment window failed: {ex}"); _vm.StatusText = ex.Message; }
        }
        private async Task<RehearsalReport?> StopRehearsalAsync(string notes)
        {
            if (_rehearsalRecorder == null) return _lastRehearsal;
            var recorder = _rehearsalRecorder; _rehearsalRecorder = null;
            _rehearsalNotes = notes;
            _lastRehearsal = await recorder.StopAsync(notes); recorder.Dispose();
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectionMapper", "Rehearsals");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
            await DeploymentReports.SaveJsonAsync(_lastRehearsal, path);
            _vm.StatusText = "Rehearsal observations saved: " + path; return _lastRehearsal;
        }
        private async Task CloseDeploymentAsync()
        {
            _performanceCancellation?.Cancel();
            string notes = _deploymentWindow?.OperatorNotes ?? string.Empty;
            if (_deploymentWindow != null) await _deploymentWindow.StopAndCloseAsync();
            _deploymentWindow = null;
            if (_rehearsalRecorder != null) await StopRehearsalAsync(notes);
        }
        private async Task<bool> CheckBeforePerformanceAsync(CancellationToken token)
        {
            if (_outputsNeedSetup) { _vm.StatusText = "Review Output setup before performance."; return false; }
            var session = _projectSession; var snapshot = BuildProjectModel(); string fingerprint = ProjectFingerprint(snapshot);
            _vm.StatusText = "Running preflight before performance…";
            var report = await new PreflightService().CheckAsync(snapshot, CaptureOutputLayout(), _currentProjectPath, inspectMedia: true, token: token, outputReviewConfirmed: !_outputsNeedSetup && DeploymentProject.SameLayout(_approvedOutputLayout, CaptureOutputLayout()));
            if (_closingInProgress || session != _projectSession || fingerprint != ProjectFingerprint(BuildProjectModel()) || _outputsNeedSetup)
            { _vm.StatusText = "The project or displays changed during preflight. Run it again."; return false; }
            if (!report.HasErrors) return true;
            Deployment_Click(this, new RoutedEventArgs()); _deploymentWindow?.ShowPreflight(report);
            _vm.StatusText = "Resolve the preflight blockers before entering performance mode."; return false;
        }
        private static bool IsCollectedLocation(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, PortableProjectService.ManifestName));
        private async Task<bool> VerifyCollectionBeforeOpenAsync(string path)
        {
            if (!IsCollectedLocation(path)) return true;
            var checks = await new PortableProjectService().VerifyAsync(Path.GetDirectoryName(path)!, expectedProjectFile: Path.GetFileName(path));
            if (checks.All(c => c.Severity != CheckSeverity.Error)) return true;
            MessageBox.Show(string.Join("\n", checks.Select(c => c.Message)) + "\nRestore or recollect this folder before opening it.", "Collection integrity", MessageBoxButton.OK, MessageBoxImage.Error); return false;
        }
    }
}
