using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;
using Brush = System.Windows.Media.Brush;

namespace ProjectionMapper.Views
{
    public sealed class CalibrationMarkerRow : INotifyPropertyChanged
    {
        private readonly MarkerObservation _value;
        private readonly Action _changed;
        public int Id => _value.Id;
        public bool Enabled { get => _value.Enabled; set { _value.Enabled = value; Notify(nameof(Enabled)); } }
        public bool ValidationOnly { get => _value.ValidationOnly; set { _value.ValidationOnly = value; Notify(nameof(ValidationOnly)); } }
        public double X { get => _value.Photo.X; set { _value.Photo = new(value, Y); _value.Origin = "Adjusted"; Notify(nameof(X)); } }
        public double Y { get => _value.Photo.Y; set { _value.Photo = new(X, value); _value.Origin = "Adjusted"; Notify(nameof(Y)); } }
        public MarkerObservation Snapshot() => _value.Copy();
        public event PropertyChangedEventHandler? PropertyChanged;
        public CalibrationMarkerRow(MarkerObservation value, Action changed) { _value = value.Copy(); _changed = changed; }
        private void Notify(string property) { PropertyChanged?.Invoke(this, new(property)); _changed(); }
    }
    public sealed record SavedCameraEvidence(CameraCalibrationRecord Record, string Label);
    public partial class CameraCalibrationWindow : Window
    {
        private readonly DisplayDescriptor _output;
        private readonly string _computer, _meshId;
        private readonly Vector2[] _currentQuad;
        private readonly Func<bool> _current;
        private readonly Action<System.Windows.Media.Imaging.BitmapSource?> _project;
        private readonly ObservableCollection<CalibrationMarkerRow> _rows = new();
        private readonly ObservableCollection<SavedCameraEvidence> _evidence = new();
        private readonly Action<string>? _removeEvidence;
        private readonly Func<bool> _outputVisible;
        private CameraPattern? _pattern;
        private PhotoMappingModel? _photo;
        private CalibrationFit? _fit;
        private readonly List<PhotoPoint> _target = new();
        private bool _ready, _busy, _targetMode, _closeAfterCancel;
        private int _revision, _previewRevision = -1;
        private CancellationTokenSource? _cancellation;
        public CameraCalibrationRecord? Result { get; private set; }
        public CameraCalibrationWindow(DisplayDescriptor output, string computer, string meshId, string meshName, Vector2[] currentQuad,
            Func<bool> environmentCurrent, Action<System.Windows.Media.Imaging.BitmapSource?> project, IReadOnlyList<CameraCalibrationRecord>? evidence = null, Action<string>? removeEvidence = null, Func<bool>? outputVisible = null)
        {
            InitializeComponent(); _output = output; _computer = computer; _meshId = meshId; _currentQuad = (Vector2[])currentQuad.Clone(); _current = environmentCurrent; _project = project;
            Surface.Text = meshName + $" · {output.DeviceName} ({output.RenderWidth}×{output.RenderHeight})";
            RegionWidth.Text = output.RenderWidth.ToString(CultureInfo.InvariantCulture); RegionHeight.Text = output.RenderHeight.ToString(CultureInfo.InvariantCulture);
            foreach (var record in (evidence ?? Array.Empty<CameraCalibrationRecord>()).OrderByDescending(c => c.CreatedUtc)) _evidence.Add(new(record, $"{record.CreatedUtc.ToLocalTime():g} · check max {record.ValidationMaxPixels:0.##} px"));
            Evidence.ItemsSource = _evidence; _removeEvidence = removeEvidence; _outputVisible = outputVisible ?? (() => true); DeleteEvidence.IsEnabled = removeEvidence != null;
            Tolerance.Text = CameraCalibrationSettings.DefaultTolerance().ToString(CultureInfo.InvariantCulture);
            Markers.ItemsSource = _rows; _ready = true;
            Closing += (_, e) => { if (!_busy) return; _closeAfterCancel = true; _cancellation?.Cancel(); e.Cancel = true; };
            Closed += (_, _) => _project(null);
        }
        private void EnsureCurrent() { if (!_current()) throw new InvalidOperationException("The project, mesh, or display environment changed. Close this panel, review output setup, then reopen calibration."); }
        private static double Number(string text)
        { if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) throw new InvalidDataException("Enter finite numeric coordinates using a decimal point."); return value; }
        private List<MarkerObservation> Matches() => _rows.Select(r => r.Snapshot()).ToList();
        private void Invalidate()
        {
            if (!_ready) return;
            if (_previewRevision >= 0) Status.Text = "Inputs changed. The projected outline is the previous preview; fit and project again before applying.";
            _revision++; _fit = null; _previewRevision = -1; Confirmed.IsChecked = false; Metrics.Text = string.Empty; Redraw();
        }
        private async Task Run(Func<CancellationToken, Task> work)
        {
            if (_busy) return; _busy = true; Controls.IsEnabled = Apply.IsEnabled = false; CancelWork.IsEnabled = true; _cancellation = new();
            try { EnsureCurrent(); await work(_cancellation.Token); }
            catch (OperationCanceledException) { Status.Text = "Calibration operation cancelled; mapping was not changed."; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Camera calibration failed: {ex}"); Status.Text = ex.Message; }
            finally { _busy = false; _cancellation.Dispose(); _cancellation = null; Controls.IsEnabled = Apply.IsEnabled = true; CancelWork.IsEnabled = false; if (_closeAfterCancel) Close(); }
        }
        private async void Generate_Click(object sender, RoutedEventArgs e) => await Run(async token =>
        {
            Invalidate();
            var region = new CalibrationRegion(Number(RegionX.Text), Number(RegionY.Text), Number(RegionWidth.Text), Number(RegionHeight.Text));
            var pattern = CameraMarkerService.NewPattern(_output.RenderWidth, _output.RenderHeight, region);
            var bitmap = await Task.Run(() => CameraMarkerService.Bitmap(pattern), token); token.ThrowIfCancellationRequested(); EnsureCurrent();
            _pattern = pattern; _photo = null; _target.Clear(); _rows.Clear(); PhotoImage.Source = null; Invalidate();
            _project(bitmap); MarkerKey.Text = "Pattern " + pattern.Id[..8] + " · IDs (row order): " + string.Join(", ", pattern.Markers.Select(m => m.Id));
            Status.Text = "New session-specific marker pattern projected. Photograph this one planar region; keep camera/projector fixed. Pattern overrides saved artwork/color correction.";
        });
        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_pattern == null) { Status.Text = "Generate a pattern first."; return; }
            var picker = new SaveFileDialog { Filter = "PNG marker pattern (*.png)|*.png", FileName = "markers-" + _pattern.Id[..8] + ".png" };
            if (picker.ShowDialog(this) != true) return;
            await Run(async token => { await CameraMarkerService.ExportAsync(_pattern, picker.FileName, token); Status.Text = "Current marker pattern exported at native output resolution."; });
        }
        private void Hide_Click(object sender, RoutedEventArgs e) { _project(null); _previewRevision = -1; Confirmed.IsChecked = false; }
        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (_pattern == null) { Status.Text = "Generate/project the marker pattern before importing its photograph."; return; }
            var picker = new OpenFileDialog { Filter = "Scene photo|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff" };
            if (picker.ShowDialog(this) != true) return;
            await Run(async token =>
            {
                var photo = await ScenePhotoService.ImportAsync(picker.FileName); token.ThrowIfCancellationRequested(); EnsureCurrent();
                CameraMarkerService.ValidatePhoto(photo);
                _photo = photo; _target.Clear(); _rows.Clear(); Invalidate();
                PhotoImage.Source = await ArtworkImaging.OnStaAsync(() => ScenePhotoService.Decode(photo));
                PhotoArea.Height = 900.0 * photo.Height / photo.Width; PhotoInfo.Text = $"{photo.OriginalFileName} · normalized {photo.Width}×{photo.Height} · same-photo coordinates only";
                FitPhoto();
                await DetectAsync(token);
            });
        }
        private async void Evidence_Click(object sender, RoutedEventArgs e) => await Run(async token =>
        {
            if (Evidence.SelectedItem is not SavedCameraEvidence selected) throw new InvalidOperationException("Select a saved calibration record first.");
            var record = selected.Record.Copy(); PlanarCalibration.ValidateRecord(record); CameraMarkerService.ValidatePhoto(record.Photo);
            if (record.Output != _output || record.ComputerId != _computer) throw new InvalidOperationException("This evidence belongs to different output hardware or dimensions. Generate a new pattern and recapture.");
            _project(null); _pattern = record.Pattern.Copy(); _photo = record.Photo.Copy(); _target.Clear(); _target.AddRange(record.TargetPhotoCorners); _rows.Clear();
            foreach (var match in record.Matches) _rows.Add(new(match, Invalidate));
            Tolerance.Text = record.TolerancePixels.ToString(CultureInfo.InvariantCulture); Extrapolate.IsChecked = record.AllowLimitedExtrapolation;
            var region = _pattern.Region; RegionX.Text = region.X.ToString(CultureInfo.InvariantCulture); RegionY.Text = region.Y.ToString(CultureInfo.InvariantCulture); RegionWidth.Text = region.Width.ToString(CultureInfo.InvariantCulture); RegionHeight.Text = region.Height.ToString(CultureInfo.InvariantCulture);
            PhotoImage.Source = await ArtworkImaging.OnStaAsync(() => ScenePhotoService.Decode(_photo)); token.ThrowIfCancellationRequested(); EnsureCurrent();
            PhotoArea.Height = 900.0 * _photo.Height / _photo.Width; FitPhoto(); Invalidate();
            MarkerKey.Text = "Saved pattern " + _pattern.Id[..8] + " · " + string.Join(", ", _pattern.Markers.Select(m => m.Id));
            PhotoInfo.Text = "Saved evidence photo — original camera pose. Recapture if the camera/projector or scene moved.";
            Status.Text = "Saved evidence loaded as an unconfirmed draft. Fit, project and physically verify again, or generate a new pattern and photograph it.";
        });
        private void FitPhoto()
        { PhotoZoom.Value = Math.Clamp(Math.Min(Math.Max(1, PhotoScroll.ViewportWidth - 10) / PhotoArea.Width, Math.Max(1, PhotoScroll.ViewportHeight - 10) / PhotoArea.Height), .25, 8); }
        private void FitPhoto_Click(object sender, RoutedEventArgs e) => FitPhoto();
        private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Redraw();
        private async Task DetectAsync(CancellationToken token)
        {
            if (_pattern == null || _photo == null) throw new InvalidOperationException("Generate a pattern and import its photo first.");
            var detections = await Task.Run(() => CameraMarkerService.Detect(_pattern, _photo, token), token); token.ThrowIfCancellationRequested(); EnsureCurrent();
            _rows.Clear();
            foreach (var marker in _pattern.Markers)
            {
                var observation = detections.Matches.FirstOrDefault(m => m.Id == marker.Id) ?? new() { Id = marker.Id, Enabled = false, ValidationOnly = marker.ValidationOnly, Origin = "Manual" };
                _rows.Add(new(observation, Invalidate));
            }
            Invalidate(); Status.Text = $"Detected {detections.Matches.Count}/9 current markers. Duplicate IDs excluded: {string.Join(", ", detections.DuplicateIds)}. Unrelated IDs ignored: {string.Join(", ", detections.UnrelatedIds)}. Select a row and click the photo to correct/add its center; keep independent check markers withheld.";
        }
        private async void Detect_Click(object sender, RoutedEventArgs e) => await Run(DetectAsync);
        private void Target_Click(object sender, RoutedEventArgs e) { if (_photo == null) return; _target.Clear(); _targetMode = true; Invalidate(); Status.Text = "Click content TL, then TR, BR, BL on this calibration photograph."; }
        private void Photo_Click(object sender, MouseButtonEventArgs e)
        {
            if (_busy || _photo == null) return; var position = e.GetPosition(Overlay); var point = new PhotoPoint(Math.Clamp(position.X / PhotoArea.Width, 0, 1), Math.Clamp(position.Y / PhotoArea.Height, 0, 1));
            if (_targetMode)
            {
                _target.Add(point); if (_target.Count == 4) _targetMode = false; Invalidate(); Status.Text = _targetMode ? $"Target corner {_target.Count}/4 marked." : "Four target corners marked. Fit/review, then project the outline.";
            }
            else if (Markers.SelectedItem is CalibrationMarkerRow row) { row.X = point.X; row.Y = point.Y; row.Enabled = true; Invalidate(); }
            else Status.Text = "Select a marker row to correct its center, or choose Mark target corners.";
        }
        private void DeleteEvidence_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Evidence.SelectedItem is not SavedCameraEvidence selected || _removeEvidence == null) return;
                _removeEvidence(selected.Record.Id); _evidence.Remove(selected); Status.Text = "Saved evidence removed from the project. Main Undo restores it. Current mapping is unchanged.";
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void MarkerEdit_Starting(object sender, DataGridPreparingCellForEditEventArgs e) => Invalidate();
        private static bool HasValidationError(DependencyObject control)
        {
            if (Validation.GetHasError(control)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(control); i++) if (HasValidationError(VisualTreeHelper.GetChild(control, i))) return true;
            return false;
        }
        private async Task<CalibrationFit> FitAsync(CancellationToken token)
        {
            if (_pattern == null || _photo == null) throw new InvalidOperationException("Generate a pattern and import its photo first.");
            if (!Markers.CommitEdit(DataGridEditingUnit.Cell, true) || !Markers.CommitEdit(DataGridEditingUnit.Row, true) || HasValidationError(Markers)) throw new InvalidDataException("Correct invalid marker coordinates before fitting.");
            double tolerance = Number(Tolerance.Text); var observations = Matches();
            var fit = await Task.Run(() => PlanarCalibration.Fit(_pattern, observations, tolerance), token); token.ThrowIfCancellationRequested(); EnsureCurrent(); _fit = fit;
            Metrics.Text = $"Fit RMS/max: {fit.FitRmsPixels:0.###}/{fit.FitMaxPixels:0.###} px\nIndependent check RMS/max: {fit.ValidationRmsPixels:0.###}/{fit.ValidationMaxPixels:0.###} px\nTolerance: {tolerance:0.###} projector px";
            Status.Text = fit.Explanation + " Errors by ID: " + string.Join("; ", fit.Errors.Select(p => $"{p.Key}={p.Value:0.##}px")); Redraw(); return fit;
        }
        private async void Fit_Click(object sender, RoutedEventArgs e) => await Run(async token => { await FitAsync(token); });
        private async void CurrentQuad_Click(object sender, RoutedEventArgs e) => await Run(async token =>
        {
            var fit = _fit ?? await FitAsync(token); if (!fit.Reliable) return;
            var inverse = PlanarCalibration.Inverse(fit.Matrix); int[] order = { 0, 1, 3, 2 };
            var points = order.Select(i => PlanarCalibration.Transform(inverse, new(_currentQuad[i].X, _currentQuad[i].Y))).ToList(); PhotoGeometry.ValidatePolygon(points);
            _target.Clear(); _target.AddRange(points); _targetMode = false; Invalidate(); Status.Text = "Current mapping placed on this photo as an editable starting point. Mark physical surface corners if it is misaligned.";
        });
        private async void Preview_Click(object sender, RoutedEventArgs e) => await Run(async token =>
        {
            var fit = _fit ?? await FitAsync(token); if (_pattern == null) return;
            var quad = PlanarCalibration.MapTarget(_pattern, Matches(), fit, _target, Extrapolate.IsChecked == true);
            if (!_outputVisible()) throw new InvalidOperationException("Blackout is enabled. Restore output with F12 before projecting the verification outline.");
            var outline = await Task.Run(() => CameraMarkerService.Outline(_pattern, quad), token); token.ThrowIfCancellationRequested(); EnsureCurrent();
            if (!_outputVisible()) throw new InvalidOperationException("Output was blacked out while preparing the outline. Restore with F12 and project it again.");
            _project(outline); _previewRevision = _revision; Confirmed.IsChecked = false; Status.Text = "Target outline projected. Inspect its physical alignment; confirm only if the plane, camera pose and tolerance are appropriate. Mapping is still unchanged.";
        });
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureCurrent(); if (_pattern == null || _photo == null || _fit == null || _previewRevision != _revision || Confirmed.IsChecked != true) throw new InvalidOperationException("Fit and project the current target, then confirm the physical review before applying.");
                var quad = PlanarCalibration.MapTarget(_pattern, Matches(), _fit, _target, Extrapolate.IsChecked == true);
                var record = new CameraCalibrationRecord { MeshId = _meshId, MeshName = Surface.Text, ComputerId = _computer, Output = _output with { }, Pattern = _pattern.Copy(), Photo = _photo.Copy(), Matches = Matches(),
                    PhotoToProjector = (double[])_fit.Matrix.Clone(), FitRmsPixels = _fit.FitRmsPixels, ValidationRmsPixels = _fit.ValidationRmsPixels, ValidationMaxPixels = _fit.ValidationMaxPixels,
                    TolerancePixels = Number(Tolerance.Text), AllowLimitedExtrapolation = Extrapolate.IsChecked == true, OperatorConfirmed = true, TargetPhotoCorners = _target.ToList(), OutputCorners = quad.SelectMany(p => new[] { p.X, p.Y }).ToArray() };
                PlanarCalibration.ValidateRecord(record); Result = record; DialogResult = true;
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void Input_Changed(object sender, RoutedEventArgs e) => Invalidate();
        private void CancelWork_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
        private void Redraw()
        {
            if (!_ready) return; Overlay.Children.Clear();
            void Node(PhotoPoint point, string name, Brush color)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
                double radius = 4.5 / PhotoZoom.Value;
                var dot = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = color, Stroke = Brushes.Black, IsHitTestVisible = false }; Canvas.SetLeft(dot, point.X * PhotoArea.Width - radius); Canvas.SetTop(dot, point.Y * PhotoArea.Height - radius); Overlay.Children.Add(dot);
                var label = new TextBlock { Text = name, FontSize = 12 / PhotoZoom.Value, Foreground = color, Background = Brushes.Black, IsHitTestVisible = false }; Canvas.SetLeft(label, point.X * PhotoArea.Width + 6); Canvas.SetTop(label, point.Y * PhotoArea.Height); Overlay.Children.Add(label);
            }
            foreach (var row in _rows.Where(r => r.Enabled)) Node(new(row.X, row.Y), row.Id.ToString(CultureInfo.InvariantCulture), row.ValidationOnly ? Brushes.Gold : Brushes.Lime);
            string[] labels = { "TL", "TR", "BR", "BL" };
            for (int i = 0; i < _target.Count; i++)
            {
                Node(_target[i], labels[i], Brushes.DeepSkyBlue);
                if (i > 0 || _target.Count == 4)
                { var a = _target[(i + _target.Count - 1) % _target.Count]; var b = _target[i]; Overlay.Children.Add(new Line { X1 = a.X * PhotoArea.Width, Y1 = a.Y * PhotoArea.Height, X2 = b.X * PhotoArea.Width, Y2 = b.Y * PhotoArea.Height, Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, IsHitTestVisible = false }); }
            }
        }
    }
}
