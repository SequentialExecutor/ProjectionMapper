using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using ProjectionMapper.Views;

namespace ProjectionMapper
{
    public partial class MainWindow
    {
        private void DisplayResolutions_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rendererManager.IsPerformanceMode || PART_Performance.IsChecked == true)
                    throw new InvalidOperationException("Exit Performance mode before changing display resolutions.");
                EnumerateMonitors();
                var selected = _detectedDisplays.FirstOrDefault(d => d.Index == _vm.SelectedMeshLayer?.TargetMonitorIndex)
                    ?? _detectedDisplays.FirstOrDefault(d => !d.Primary) ?? _detectedDisplays.FirstOrDefault();
                var window = new DisplayResolutionWindow(() =>
                {
                    EnumerateMonitors(); return _detectedDisplays.Select(d => d with { }).ToList();
                }, () =>
                {
                    // Close native output windows before a mode switch and require deliberate routing review.
                    // Geometry remains unchanged; the rebuilt renderer uses the newly detected pixel mode.
                    RequireOutputReview();
                }, selected?.DeviceName) { Owner = this };
                window.ShowDialog();
                EnumerateMonitors();
                if (window.RestoreError != null) _vm.StatusText = window.RestoreError;
                else if (window.ModeChanged)
                {
                    _rehearsalRecorder?.Record("resolution-change", "Windows display resolution changed; verify output assignments and alignment.");
                    _vm.StatusText = "Display mode updated. Open Show setup > Assign projector outputs, verify alignment, then restore output with F12.";
                }
            }
            catch (Exception error) { Debug.WriteLine($"Display resolution settings failed: {error}"); _vm.StatusText = error.Message; }
        }
    }
}
