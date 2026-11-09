using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ProjectionMapper.Models;
using ProjectionMapper.Services;

namespace ProjectionMapper.Views
{
    /// <summary>Modeless operations use detached snapshots; close cancels file work and retains the last rehearsal in the owner.</summary>
    public partial class DeploymentWindow : Window
    {
        private readonly Func<ProjectModel> _snapshot;
        private readonly Func<OutputLayout> _hardware;
        private readonly Func<string?> _path;
        private readonly Func<PreflightReport, Task> _start;
        private readonly Func<string, Task<RehearsalReport?>> _stop;
        private readonly Func<bool> _recording;
        private readonly Func<bool> _outputReviewed;
        private CancellationTokenSource? _cancellation;
        private TaskCompletionSource<bool>? _idle;
        private bool _busy, _closeAfterCancel;
        private PreflightReport? _preflight;
        private RehearsalReport? _rehearsal;
        public DeploymentWindow(Func<ProjectModel> snapshot, Func<OutputLayout> hardware, Func<string?> path,
            Func<PreflightReport, Task> start, Func<string, Task<RehearsalReport?>> stop, Func<bool> recording, Func<bool>? outputReviewed = null)
        {
            InitializeComponent(); _snapshot = snapshot; _hardware = hardware; _path = path; _start = start; _stop = stop; _recording = recording; _outputReviewed = outputReviewed ?? (() => false);
            UpdateButtons();
            if (_recording()) Status.Text = "Rehearsal recording is running in the background.";
            Closing += (_, e) => { if (!_busy) return; _closeAfterCancel = true; _cancellation?.Cancel(); e.Cancel = true; };
        }
        public string OperatorNotes { get => Notes.Text; set => Notes.Text = value; }
        public void ShowPreflight(PreflightReport report) { _preflight = report; _rehearsal = null; Report.Text = DeploymentReports.Describe(report); UpdateButtons(); }
        public void ShowRehearsal(RehearsalReport report) { _rehearsal = report; Notes.Text = report.OperatorNotes; Report.Text = DeploymentReports.Describe(report); UpdateButtons(); }
        private void UpdateButtons()
        { Collect.IsEnabled = Preflight.IsEnabled = !_busy; StartRehearsal.IsEnabled = !_busy && !_recording(); StopRehearsal.IsEnabled = !_busy && _recording(); Export.IsEnabled = !_busy && (_preflight != null || _rehearsal != null); CancelOperation.IsEnabled = _busy; }
        private async Task Run(Func<CancellationToken, Task> operation)
        {
            if (_busy) return; _busy = true; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _cancellation = new(); UpdateButtons();
            try { await operation(_cancellation.Token); }
            catch (OperationCanceledException) { Status.Text = "Operation cancelled. No partial collection was published."; }
            catch (Exception ex) { Debug.WriteLine($"Deployment operation failed: {ex}"); Status.Text = ex.Message; }
            finally { _busy = false; _cancellation.Dispose(); _cancellation = null; UpdateButtons(); _idle.TrySetResult(true); if (_closeAfterCancel) Close(); }
        }
        public async Task StopAndCloseAsync()
        { _closeAfterCancel = true; _cancellation?.Cancel(); if (_busy && _idle != null) await _idle.Task; if (IsVisible) Close(); }
        private async void Collect_Click(object sender, RoutedEventArgs e)
        {
            ProjectModel snapshot;
            try { snapshot = _snapshot(); } catch (Exception ex) { Status.Text = ex.Message; return; }
            using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose the parent folder for a new collected project", UseDescriptionForTitle = true };
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string destination = Path.Combine(picker.SelectedPath, "ProjectionMapper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            await Run(async token =>
            {
                var progress = new Progress<CollectionProgress>(p => { Status.Text = p.Message; Progress.Value = p.TotalBytes > 0 ? Math.Clamp(100.0 * p.CopiedBytes / p.TotalBytes, 0, 100) : 0; });
                var result = await new PortableProjectService().CollectAsync(snapshot, destination, progress, token, DeploymentSettings.Load().MinimumFreeBytes);
                Report.Text = $"Collected and verified:\n{result.Directory}\n\nProject: {result.ProjectPath}\nUnique media files: {result.MediaFiles}\nProject/media bytes: {result.Bytes:N0}\n\nCopy the entire folder. The open project has not changed. Embedded artwork, photos, masks, cues and settings are inside show.pmproj.\n\nRun preflight and output setup on the destination; hardware acceptance remains required.";
                Status.Text = "Collection complete"; Progress.Value = 100;
            });
        }
        private async void Preflight_Click(object sender, RoutedEventArgs e) => await Run(async token =>
        {
            var snapshot = _snapshot(); var hardware = _hardware(); string? path = _path();
            _preflight = await new PreflightService().CheckAsync(snapshot, hardware, path, InspectMedia.IsChecked == true, token, _outputReviewed()); _rehearsal = null;
            Report.Text = DeploymentReports.Describe(_preflight); Status.Text = _preflight.HasErrors ? "Resolve preflight blockers before performance." : "Automated checks complete.";
        });
        private async void Start_Click(object sender, RoutedEventArgs e) => await Run(async token =>
        {
            // Always take a fresh preflight at recording start, rather than reusing an outdated report.
            _preflight = await new PreflightService().CheckAsync(_snapshot(), _hardware(), _path(), InspectMedia.IsChecked == true, token, _outputReviewed());
            await _start(_preflight); _rehearsal = null;
            Report.Text = DeploymentReports.Describe(_preflight) + "\n\nRecording started. Operate the installation in the main window, then stop to inspect actual observations. A failed preflight is retained as evidence.";
            Status.Text = "Recording rehearsal observations";
        });
        private async void Stop_Click(object sender, RoutedEventArgs e) => await Run(async _ =>
        { _rehearsal = await _stop(Notes.Text); if (_rehearsal != null) Report.Text = DeploymentReports.Describe(_rehearsal); Status.Text = "Recording stopped"; });
        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            var picker = new SaveFileDialog { Filter = "JSON report (*.json)|*.json", FileName = _rehearsal != null ? "rehearsal.json" : "preflight.json" };
            if (picker.ShowDialog(this) != true) return;
            await Run(async token => { if (_rehearsal != null) { _rehearsal.OperatorNotes = Notes.Text; await DeploymentReports.SaveJsonAsync(_rehearsal, picker.FileName, token); } else if (_preflight != null) await DeploymentReports.SaveJsonAsync(_preflight, picker.FileName, token); Status.Text = "Report exported"; });
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    }
}
