using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ProjectionMapper.Models;
using ProjectionMapper.Services;

namespace ProjectionMapper.Views
{
    public partial class OutputDiagnosticsWindow : Window
    {
        private readonly Func<OutputDiagnosticReport> _capture;
        private readonly CancellationTokenSource _lifetime = new();
        private OutputDiagnosticReport? _report;
        private bool _started, _closed, _captureError;
        public OutputDiagnosticsWindow(Func<OutputDiagnosticReport> capture)
        {
            InitializeComponent(); _capture = capture;
            RefreshReport();
            Loaded += async (_, _) => { if (_closed || _started) return; _started = true; await ObserveAsync(); };
            Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
        }
        private void RefreshReport()
        {
            if (_closed) return;
            try
            {
                int? selected = (Displays.SelectedItem as DiagnosticDisplay)?.Index;
                _report = OutputDiagnosticService.Analyze(_capture());
                if (_captureError) Status.Text = string.Empty; _captureError = false;
                Summary.Text = _report.Summary; Scope.Text = _report.Scope;
                Displays.ItemsSource = _report.Displays;
                Displays.SelectedItem = _report.Displays.FirstOrDefault(d => d.Index == selected) ?? _report.Displays.FirstOrDefault();
                if (_report.Displays.Count == 0) Details.Text = "No connected or assigned displays. You can still use F5 to preview the composed editor frame.";
                Timestamp.Text = "Captured " + _report.CapturedUtc.ToLocalTime().ToString("T");
            }
            catch (Exception error) { Debug.WriteLine($"Output status failed: {error}"); _captureError = true; Status.Text = error.Message; }
        }
        private async Task ObserveAsync()
        {
            var token = _lifetime.Token;
            try { while (!token.IsCancellationRequested) { await Task.Delay(1000, token); if (!token.IsCancellationRequested) RefreshReport(); } }
            catch (OperationCanceledException) { Debug.WriteLine("Output status observer closed."); }
        }
        private void Display_Changed(object sender, SelectionChangedEventArgs e) => Details.Text = (Displays.SelectedItem as DiagnosticDisplay)?.Details ?? string.Empty;
        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshReport();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            Export.IsEnabled = false;
            try
            {
                var path = await new FileDialogService().ShowSaveFileDialogAsync("Export output diagnostics", "output-status.json", "JSON (*.json)|*.json");
                if (path == null || _lifetime.IsCancellationRequested) return;
                var report = OutputDiagnosticService.Analyze(_capture());
                await DeploymentReports.SaveJsonAsync(report, path, _lifetime.Token);
                if (!_lifetime.IsCancellationRequested) Status.Text = "Diagnostic report exported. It includes project/source names and software observations, without media paths or credentials.";
            }
            catch (OperationCanceledException) { Debug.WriteLine("Output diagnostic export cancelled."); }
            catch (Exception error) { Debug.WriteLine($"Output diagnostic export failed: {error}"); if (!_lifetime.IsCancellationRequested) Status.Text = error.Message; }
            finally { if (!_lifetime.IsCancellationRequested) Export.IsEnabled = true; }
        }
    }
}
