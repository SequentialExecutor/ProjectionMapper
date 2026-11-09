using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    public sealed record ResolutionDisplay(string DeviceName, string Label);
    public partial class DisplayResolutionWindow : Window
    {
        private readonly IDisplayModeBackend _backend;
        private readonly Func<List<DisplayDescriptor>> _displays;
        private readonly Action _beforeChange;
        private DisplayModeChange? _change;
        private CancellationTokenSource? _countdown;
        private bool _busy, _closeRequested, _closed;
        private long _started;
        public bool ModeChanged { get; private set; }
        public string? RestoreError { get; private set; }
        public DisplayResolutionWindow(Func<List<DisplayDescriptor>> displays, Action beforeChange, string? selectedDevice = null, IDisplayModeBackend? backend = null)
        {
            InitializeComponent(); _displays = displays; _beforeChange = beforeChange; _backend = backend ?? new WindowsDisplayModes();
            Closing += ClosingWindow;
            RefreshDisplays(selectedDevice);
        }
        private void Busy(bool busy)
        {
            _busy = busy; Displays.IsEnabled = Modes.IsEnabled = Refresh.IsEnabled = Apply.IsEnabled = !busy && _change == null;
            Keep.IsEnabled = Revert.IsEnabled = !busy;
        }
        private void CancelCountdown()
        {
            var countdown = _countdown; _countdown = null;
            countdown?.Cancel(); countdown?.Dispose();
        }
        private void Error(Exception error) { Debug.WriteLine($"Display resolution: {error}"); Status.Text = error.Message; }
        private void RefreshDisplays(string? selected = null)
        {
            try
            {
                selected ??= (Displays.SelectedItem as ResolutionDisplay)?.DeviceName;
                var items = _displays().Select(d => new ResolutionDisplay(d.DeviceName, $"Display {d.Index + 1} · {d.DeviceName}{(d.Primary ? " · Primary" : "")}")).ToList();
                Displays.ItemsSource = items;
                Displays.SelectedItem = items.FirstOrDefault(d => d.DeviceName == selected) ?? items.FirstOrDefault();
                if (items.Count == 0) { Modes.ItemsSource = null; CurrentMode.Text = "No connected displays detected."; Apply.IsEnabled = false; }
            }
            catch (Exception error) { Error(error); }
        }
        private void Display_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_backend == null || Displays.SelectedItem is not ResolutionDisplay display) return;
            try
            {
                var current = _backend.Current(display.DeviceName);
                CurrentMode.Text = "Current Windows mode: " + current.Label;
                Modes.ItemsSource = _backend.Supported(display.DeviceName);
                Modes.SelectedItem = ((IReadOnlyList<DisplayMode>)Modes.ItemsSource).FirstOrDefault(m => m == current);
                Status.Text = string.Empty;
            }
            catch (Exception error) { Modes.ItemsSource = null; CurrentMode.Text = "Mode unavailable — refresh or check the connection."; Error(error); }
        }
        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDisplays();
        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _change != null) return;
            try
            {
                if (Displays.SelectedItem is not ResolutionDisplay display || Modes.SelectedItem is not DisplayMode mode) throw new InvalidOperationException("Choose a connected display and one of its supported modes.");
                if (_backend.Current(display.DeviceName) == mode) { Status.Text = "This display is already using that mode."; return; }
                _beforeChange(); Busy(true); Status.Text = "Applying Windows display mode…";
                _change = await Task.Run(() => DisplayModeChange.Begin(_backend, display.DeviceName, mode));
                ModeChanged = true;
                if (_closeRequested) { await RestoreAsync(); if (!_closed) Close(); return; }
                _started = Stopwatch.GetTimestamp(); _countdown = new CancellationTokenSource();
                Confirmation.Visibility = Visibility.Visible; Status.Text = string.Empty; Busy(false);
                // Keep the confirmation reachable if the operator changed the primary screen to a smaller mode.
                var area = SystemParameters.WorkArea;
                Width = Math.Min(Width, Math.Max(MinWidth, area.Width)); Height = Math.Min(Height, Math.Max(MinHeight, area.Height));
                Left = area.Left + Math.Max(0, (area.Width - Width) / 2); Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
                Keep.Focus();
                await ConfirmCountdownAsync(_countdown.Token);
            }
            catch (Exception error) { Error(error); Busy(false); if (_closeRequested && _change == null && !_closed) Close(); }
        }
        private async Task ConfirmCountdownAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int remaining = (int)Math.Ceiling(15 - Stopwatch.GetElapsedTime(_started).TotalSeconds);
                    if (remaining <= 0) { await RestoreAsync(); return; }
                    Countdown.Text = $"Keep this resolution? Reverting in {remaining} seconds.";
                    await Task.Delay(250, token);
                }
            }
            catch (OperationCanceledException) { Debug.WriteLine("Display-mode confirmation completed or closed."); }
        }
        private async Task RestoreAsync()
        {
            CancelCountdown(); Busy(true);
            try
            {
                var change = _change;
                if (change != null) await Task.Run(change.Revert);
                _change = null; Confirmation.Visibility = Visibility.Collapsed;
                RefreshDisplays(); Status.Text = "Previous resolution restored. Review output assignments if projector output is blocked.";
            }
            catch (Exception error) { Error(error); throw; }
            finally { Busy(false); if (_closeRequested && _change == null && !_closed) Close(); }
        }
        private async void Keep_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _change == null) return;
            try
            {
                if (Stopwatch.GetElapsedTime(_started).TotalSeconds >= 15) { await RestoreAsync(); return; }
                CancelCountdown(); Busy(true);
                await Task.Run(_change.Keep);
                _change = null; Confirmation.Visibility = Visibility.Collapsed; RefreshDisplays();
                Status.Text = "Resolution kept in Windows. Review projector assignments and alignment, then use F12 to restore output.";
            }
            catch (Exception error)
            {
                Error(error);
                try { await RestoreAsync(); } catch (Exception rollback) { Error(rollback); }
            }
            finally { Busy(false); if (_closeRequested && _change == null && !_closed) Close(); }
        }
        private async void Revert_Click(object sender, RoutedEventArgs e)
        { if (_busy) return; try { await RestoreAsync(); } catch (Exception error) { Error(error); } }
        private void Done_Click(object sender, RoutedEventArgs e) => Close();
        private async void ClosingWindow(object? sender, CancelEventArgs e)
        {
            if (_closed) return;
            if (_busy) { _closeRequested = true; e.Cancel = true; return; }
            if (_change == null) { _closed = true; CancelCountdown(); return; }
            e.Cancel = true; _closeRequested = true;
            try { await RestoreAsync(); if (!_closed) Close(); }
            catch (Exception error)
            {
                RestoreError = error.Message; Error(error); _change = null; _closed = true;
                CancelCountdown(); Close();
            }
        }
    }
}
