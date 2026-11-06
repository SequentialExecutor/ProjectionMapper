using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
using Button = System.Windows.Controls.Button;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using Image = System.Windows.Controls.Image;
using Brush = System.Windows.Media.Brush;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace ProjectionMapper.Views
{
    public partial class PhotoMapWindow : Window
    {
        private readonly List<PhotoMappingModel> _maps;
        private readonly bool _canCreateMeshes;
        private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
        private readonly Stack<string> _undo = new(), _redo = new();
        private CancellationTokenSource? _requestCancellation;
        private bool _ready, _busy, _closeAfterCancel, _drawing;
        private readonly List<PhotoPoint> _draft = new();
        private int _dragVertex = -1;
        private string? _beforeDrag;
        private System.Windows.Media.Imaging.BitmapSource? _photo;
        public List<PhotoMappingModel> ResultMaps => _maps;
        public bool CreateMeshesRequested { get; private set; }
        private PhotoMappingModel? Map => References.SelectedItem as PhotoMappingModel;
        private PhotoSurfaceModel? Selected => Surfaces.SelectedItem as PhotoSurfaceModel;

        public PhotoMapWindow(IReadOnlyList<PhotoMappingModel> maps, bool canCreateMeshes)
        {
            InitializeComponent();
            _maps = maps.Select(map => map.Copy()).ToList();
            _canCreateMeshes = canCreateMeshes;
            CreateMeshes.IsEnabled = canCreateMeshes;
            if (!canCreateMeshes) CreateMeshes.ToolTip = "Select an imported video in the main window to create projection meshes.";
            try
            {
                var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (File.Exists(settingsPath))
                {
                    using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                    if (settings.RootElement.TryGetProperty("PhotoMapping", out var options))
                    {
                        if (options.TryGetProperty("VisionModel", out var model)) ModelName.Text = model.GetString() ?? ModelName.Text;
                        if (options.TryGetProperty("TimeoutSeconds", out var timeout)) _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeout.GetInt32(), 10, 600));
                    }
                }
            }
            catch (Exception) { Status.Text = "Photo-mapping defaults could not be read; using the displayed model and a 120-second timeout."; }
            _ready = true;
            RefreshReferences(_maps.LastOrDefault()?.Id);
            SizeChanged += (_, _) => Redraw();
            Closing += (_, e) =>
            {
                if (!_busy) return;
                _closeAfterCancel = true; _requestCancellation?.Cancel(); e.Cancel = true;
            };
            Closed += (_, _) => { ApiKey.Password = string.Empty; _http.Dispose(); };
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            RequestFields.IsEnabled = References.IsEnabled = ImportPhoto.IsEnabled = EditorTools.IsEnabled = Properties.IsEnabled = PhotoCanvas.IsEnabled = SaveMap.IsEnabled = !busy;
            CreateMeshes.IsEnabled = !busy && _canCreateMeshes;
            CancelAnalysis.IsEnabled = busy && _requestCancellation != null;
        }
        private string Snapshot() => JsonSerializer.Serialize(Map?.Surfaces ?? new());
        private void Record(string before)
        {
            if (before == Snapshot()) return;
            if (_undo.Count >= 100)
            {
                var retained = _undo.Take(99).Reverse().ToArray();
                _undo.Clear(); foreach (var state in retained) _undo.Push(state);
            }
            _undo.Push(before); _redo.Clear();
        }
        private void RefreshReferences(string? id)
        {
            References.ItemsSource = null; References.ItemsSource = _maps;
            References.SelectedItem = _maps.FirstOrDefault(m => m.Id == id) ?? _maps.LastOrDefault();
        }
        private void RefreshSurfaces(string? id = null)
        {
            id ??= Selected?.Id;
            Surfaces.ItemsSource = null; Surfaces.ItemsSource = Map?.Surfaces;
            Surfaces.SelectedItem = Map?.Surfaces.FirstOrDefault(s => s.Id == id) ?? Map?.Surfaces.LastOrDefault();
            UpdateSelection(); Redraw(); WarnOverlaps();
        }
        private void Reference_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (_beforeDrag != null && e.RemovedItems.OfType<PhotoMappingModel>().FirstOrDefault() is { } previous)
                previous.Surfaces = JsonSerializer.Deserialize<List<PhotoSurfaceModel>>(_beforeDrag)!;
            _dragVertex = -1; _beforeDrag = null; PhotoCanvas.ReleaseMouseCapture();
            UploadConsent.IsChecked = false;
            _drawing = false; _draft.Clear(); _undo.Clear(); _redo.Clear();
            try { _photo = Map == null ? null : ScenePhotoService.Decode(Map); }
            catch (Exception) { _photo = null; Status.Text = "This reference image could not be decoded. Import another photo."; }
            PhotoCanvas.Width = Map?.Width ?? 800; PhotoCanvas.Height = Map?.Height ?? 500;
            RefreshSurfaces();
        }
        private void Surface_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) { UpdateSelection(); Redraw(); } }
        private void UpdateSelection()
        {
            if (!_ready) return;
            int index = Math.Max(0, Vertex.SelectedIndex);
            SurfaceName.Text = Selected?.Name ?? string.Empty; Accepted.IsChecked = Selected?.Accepted == true;
            Vertex.ItemsSource = Selected == null ? null : Enumerable.Range(1, Selected.Points.Count).ToArray();
            SplitVertex.ItemsSource = Selected == null ? null : Enumerable.Range(1, Selected.Points.Count).ToArray();
            Vertex.SelectedIndex = Selected == null ? -1 : Math.Min(index, Selected.Points.Count - 1);
            SplitVertex.SelectedIndex = Selected?.Points.Count > 2 ? 2 : -1;
            Provenance.Text = Selected == null ? string.Empty : Selected.Origin +
                (Selected.OriginalConfidence.HasValue ? $" · original AI estimate {Selected.OriginalConfidence:0.00} (not calibrated)" : string.Empty) +
                (Selected.Provider == null ? string.Empty : $"\n{Selected.Provider} · {Selected.Model}");
            UpdateVertexFields();
        }
        private void UpdateVertexFields()
        {
            if (!_ready || Selected == null || Vertex.SelectedIndex < 0 || Vertex.SelectedIndex >= Selected.Points.Count) return;
            var p = Selected.Points[Vertex.SelectedIndex];
            XValue.Text = (p.X * 100).ToString("0.###", CultureInfo.CurrentCulture);
            YValue.Text = (p.Y * 100).ToString("0.###", CultureInfo.CurrentCulture);
        }
        private void Vertex_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) { UpdateVertexFields(); Redraw(); } }
        private PhotoPoint Position(MouseEventArgs e)
        {
            var p = e.GetPosition(PhotoCanvas);
            return new PhotoPoint(Math.Clamp(p.X / PhotoCanvas.Width, 0, 1), Math.Clamp(p.Y / PhotoCanvas.Height, 0, 1));
        }
        private void Redraw()
        {
            if (!_ready) return;
            PhotoCanvas.Children.Clear();
            if (_photo != null) PhotoCanvas.Children.Add(new Image { Source = _photo, Width = PhotoCanvas.Width, Height = PhotoCanvas.Height, IsHitTestVisible = false });
            double scale = Math.Max(.02, Math.Min(PhotoView.ActualWidth / PhotoCanvas.Width, PhotoView.ActualHeight / PhotoCanvas.Height));
            double radius = 5 / scale;
            void Outline(IReadOnlyList<PhotoPoint> points, Brush brush, bool selected, string? name)
            {
                var polygon = new Polygon { Stroke = brush, StrokeThickness = (selected ? 2.5 : 1.5) / scale,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 255, 255)), IsHitTestVisible = false };
                foreach (var p in points) polygon.Points.Add(new Point(p.X * PhotoCanvas.Width, p.Y * PhotoCanvas.Height));
                PhotoCanvas.Children.Add(polygon);
                if (selected)
                    for (int i = 0; i < points.Count; i++)
                    {
                        var p = points[i]; var dot = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = i == Vertex.SelectedIndex ? Brushes.Red : Brushes.White, IsHitTestVisible = false };
                        Canvas.SetLeft(dot, p.X * PhotoCanvas.Width - radius); Canvas.SetTop(dot, p.Y * PhotoCanvas.Height - radius); PhotoCanvas.Children.Add(dot);
                        var number = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), Foreground = Brushes.White, Background = Brushes.Black, FontSize = 11 / scale, IsHitTestVisible = false };
                        Canvas.SetLeft(number, p.X * PhotoCanvas.Width + radius); Canvas.SetTop(number, p.Y * PhotoCanvas.Height + radius); PhotoCanvas.Children.Add(number);
                    }
                if (name != null && points.Count > 0)
                {
                    var label = new TextBlock { Text = name, Foreground = brush, Background = Brushes.Black, FontSize = 12 / scale, IsHitTestVisible = false };
                    Canvas.SetLeft(label, points.Average(p => p.X) * PhotoCanvas.Width); Canvas.SetTop(label, points.Average(p => p.Y) * PhotoCanvas.Height); PhotoCanvas.Children.Add(label);
                }
            }
            if (Map != null)
                foreach (var surface in Map.Surfaces) Outline(surface.Points, surface.Accepted ? Brushes.Lime : Brushes.Gold, surface == Selected, surface.Name);
            if (_draft.Count > 0) Outline(_draft, Brushes.Cyan, false, null);
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_maps.Count >= 8) { Status.Text = "This project already contains eight references."; return; }
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Scene photos|*.jpg;*.jpeg;*.png;*.bmp", Title = "Import scene reference" };
            if (dialog.ShowDialog(this) != true) return;
            SetBusy(true);
            try
            {
                var map = await ScenePhotoService.ImportAsync(dialog.FileName);
                _maps.Add(map); RefreshReferences(map.Id);
                Status.Text = "Reference imported locally. Draw surfaces manually or explicitly request AI proposals.";
            }
            catch (Exception ex) { Status.Text = $"Photo import failed: {ex.Message}"; }
            finally { SetBusy(false); if (_closeAfterCancel) Close(); }
        }
        private async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_drawing || _dragVertex >= 0) { Status.Text = "Finish the current outline or vertex move before analysis."; return; }
            if (Map == null) { Status.Text = "Import a scene photo first."; return; }
            if (UploadConsent.IsChecked != true) { Status.Text = "Review the upload and API billing notice, then enable consent to analyze."; return; }
            if (Map.Surfaces.Count >= 64) { Status.Text = "This reference already contains 64 surfaces."; return; }
            var map = Map;
            _requestCancellation = new CancellationTokenSource(); SetBusy(true);
            Status.Text = "Analyzing the selected photo. Editing is paused; projection playback continues.";
            try
            {
                var service = new PhotoVisionService(_http);
                string description = Description.Text, key = ApiKey.Password, model = ModelName.Text;
                var token = _requestCancellation.Token;
                var result = await Task.Run(() => service.AnalyzeAsync(map, description, key, model, token), token);
                token.ThrowIfCancellationRequested();
                string before = Snapshot();
                var added = result.Surfaces.Take(64 - map.Surfaces.Count).ToList();
                map.Surfaces.AddRange(added); Record(before); RefreshSurfaces(added.LastOrDefault()?.Id);
                int low = added.Count(s => s.OriginalConfidence < .7);
                Status.Text = $"Added {added.Count} draft proposals. {low} have low AI estimates. Review every boundary before accepting. " + string.Join(" ", result.Warnings);
                WarnOverlaps();
            }
            catch (OperationCanceledException)
            {
                Status.Text = _requestCancellation.IsCancellationRequested ? "Analysis canceled locally. The provider may still charge for a submitted request." : "Analysis timed out. No automatic retry was attempted; the provider may still charge.";
            }
            catch (Exception ex) { Status.Text = $"Analysis failed: {ex.Message} Manual mapping remains available."; }
            finally
            {
                _requestCancellation.Dispose(); _requestCancellation = null; SetBusy(false);
                if (_closeAfterCancel) Close();
            }
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) => _requestCancellation?.Cancel();
        private void Draw_Click(object sender, RoutedEventArgs e)
        {
            if (Map == null) { Status.Text = "Import a photo first."; return; }
            _drawing = true; _draft.Clear(); Redraw(); Status.Text = "Click perimeter vertices in order, then Finish polygon. Escape cancels.";
        }
        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            if (!_drawing || Map == null) return;
            try
            {
                if (Map.Surfaces.Count >= 64) throw new InvalidOperationException("A reference supports up to 64 surfaces.");
                PhotoGeometry.ValidatePolygon(_draft);
                var surface = new PhotoSurfaceModel { Name = $"Surface {Map.Surfaces.Count + 1}", Points = _draft.ToList(), ReferenceImageSha256 = Map.ImageSha256 };
                string before = Snapshot(); Map.Surfaces.Add(surface); Record(before);
                _drawing = false; _draft.Clear(); RefreshSurfaces(surface.Id); WarnOverlaps();
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void Photo_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (Map == null || _busy) return;
            PhotoCanvas.Focus();
            var p = Position(e);
            if (_drawing)
            {
                if (_draft.Count < 32) _draft.Add(p);
                Redraw(); e.Handled = true; return;
            }
            if (Selected != null)
            {
                double scale = Math.Max(.02, Math.Min(PhotoView.ActualWidth / PhotoCanvas.Width, PhotoView.ActualHeight / PhotoCanvas.Height));
                int index = Enumerable.Range(0, Selected.Points.Count).OrderBy(i => Distance(Selected.Points[i], p)).First();
                if (Distance(Selected.Points[index], p) * scale < 14)
                {
                    _beforeDrag = Snapshot(); _dragVertex = index; Vertex.SelectedIndex = index; PhotoCanvas.CaptureMouse(); e.Handled = true; return;
                }
            }
            Surfaces.SelectedItem = Map.Surfaces.LastOrDefault(s => PhotoGeometry.Contains(s.Points, p));
            e.Handled = true;
        }
        private double Distance(PhotoPoint a, PhotoPoint b) => Math.Sqrt(Math.Pow((a.X - b.X) * PhotoCanvas.Width, 2) + Math.Pow((a.Y - b.Y) * PhotoCanvas.Height, 2));
        private void Photo_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragVertex < 0 || Selected == null || e.LeftButton != MouseButtonState.Pressed) return;
            Selected.Points[_dragVertex] = Position(e); Selected.Accepted = false;
            UpdateVertexFields(); Redraw();
        }
        private void Photo_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragVertex < 0 || Selected == null || _beforeDrag == null) return;
            PhotoCanvas.ReleaseMouseCapture(); _dragVertex = -1;
            try { PhotoGeometry.ValidatePolygon(Selected.Points); Record(_beforeDrag); RefreshSurfaces(); WarnOverlaps(); }
            catch (Exception ex)
            {
                if (Map != null) Map.Surfaces = JsonSerializer.Deserialize<List<PhotoSurfaceModel>>(_beforeDrag)!;
                RefreshSurfaces(); Status.Text = $"Vertex move reverted: {ex.Message}";
            }
            finally { _beforeDrag = null; }
        }
        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null || string.IsNullOrWhiteSpace(SurfaceName.Text)) return;
            string before = Snapshot(); Selected.Name = SurfaceName.Text.Trim(); Record(before); RefreshSurfaces();
        }
        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null) return;
            string before = Snapshot(); Selected.Accepted = Accepted.IsChecked == true; Record(before); Redraw(); WarnOverlaps();
        }
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (Map == null || Selected == null) return;
            string before = Snapshot(); Map.Surfaces.Remove(Selected); Record(before); RefreshSurfaces();
        }
        private void VertexApply_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null || Vertex.SelectedIndex < 0) return;
            try
            {
                double x = double.Parse(XValue.Text, CultureInfo.CurrentCulture) / 100, y = double.Parse(YValue.Text, CultureInfo.CurrentCulture) / 100;
                var candidate = Selected.Points.ToList(); candidate[Vertex.SelectedIndex] = new PhotoPoint(x, y); PhotoGeometry.ValidatePolygon(candidate);
                string before = Snapshot(); Selected.Points = candidate; Selected.Accepted = false; Record(before); RefreshSurfaces(); WarnOverlaps();
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private PhotoSurfaceModel Derived(string name, List<PhotoPoint> points, string operation) => new()
        {
            Name = name, Points = points, Origin = operation,
            ReferenceImageSha256 = Map?.ImageSha256, DerivedFromSurfaceId = Selected?.Id,
            Provider = Selected?.Provider, Model = Selected?.Model, ResponseId = Selected?.ResponseId,
            RequestDescription = Selected?.RequestDescription, OriginalConfidence = Selected?.OriginalConfidence
        };

        private void Fit_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null || Map == null) return;
            if (Map.Surfaces.Count >= 64) { Status.Text = "A reference supports up to 64 surfaces."; return; }
            var p = Selected.Points; double left = p.Min(v => v.X), right = p.Max(v => v.X), top = p.Min(v => v.Y), bottom = p.Max(v => v.Y);
            var fitted = Derived((Selected.Name.Length > 103 ? Selected.Name[..103] : Selected.Name) + " (bounding quad)",
                new() { new(left, top), new(right, top), new(right, bottom), new(left, bottom) }, "Manual bounding-quad approximation");
            string before = Snapshot(); Map.Surfaces.Add(fitted); Record(before); RefreshSurfaces(fitted.Id);
            Status.Text = "A bounding quad was added as a new draft. It includes areas outside the original polygon and is not a shape mask.";
        }
        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (Map == null || Selected == null) return;
            try
            {
                if (Map.Surfaces.Count >= 64) throw new InvalidOperationException("A reference supports up to 64 surfaces.");
                var split = PhotoGeometry.Split(Selected.Points, Vertex.SelectedIndex, SplitVertex.SelectedIndex);
                string name = Selected.Name.Length > 110 ? Selected.Name[..110] : Selected.Name;
                var first = Derived(name + " A", split.First, "Manual split");
                var second = Derived(name + " B", split.Second, "Manual split");
                string before = Snapshot(); Map.Surfaces.Remove(Selected); Map.Surfaces.Add(first); Map.Surfaces.Add(second); Record(before); RefreshSurfaces(first.Id);
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void WarnOverlaps()
        {
            const string warning = " Some outlines overlap or touch; review them before creating meshes.";
            Status.Text = Status.Text.Replace(warning, string.Empty);
            if (Map == null) return;
            for (int i = 0; i < Map.Surfaces.Count; i++)
                for (int j = i + 1; j < Map.Surfaces.Count; j++)
                    if (PhotoGeometry.Overlaps(Map.Surfaces[i].Points, Map.Surfaces[j].Points))
                    { Status.Text += warning; return; }
        }
        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_drawing)
            {
                if (_draft.Count > 0) _draft.RemoveAt(_draft.Count - 1);
                Redraw(); return;
            }
            if (Map == null || _undo.Count == 0) return;
            _redo.Push(Snapshot()); Map.Surfaces = JsonSerializer.Deserialize<List<PhotoSurfaceModel>>(_undo.Pop())!; RefreshSurfaces();
        }
        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (Map == null || _redo.Count == 0) return;
            _undo.Push(Snapshot()); Map.Surfaces = JsonSerializer.Deserialize<List<PhotoSurfaceModel>>(_redo.Pop())!; RefreshSurfaces();
        }
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_beforeDrag != null && Map != null) Map.Surfaces = JsonSerializer.Deserialize<List<PhotoSurfaceModel>>(_beforeDrag)!;
                _dragVertex = -1; _beforeDrag = null; PhotoCanvas.ReleaseMouseCapture();
                _drawing = false; _draft.Clear(); RefreshSurfaces(); e.Handled = true;
            }
            if (_dragVertex >= 0 || _busy || Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is System.Windows.Controls.PasswordBox) return;
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo_Click(sender, e); e.Handled = true; }
        }
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_busy) return;
                if (_drawing || _dragVertex >= 0) throw new InvalidOperationException("Finish or cancel the current polygon or vertex move first.");
                foreach (var map in _maps) PhotoGeometry.ValidateMap(map);
                if (CreateMeshes.IsChecked == true && Uncalibrated.IsChecked != true)
                    throw new InvalidOperationException("Review and acknowledge the manual projector-alignment step before creating meshes.");
                CreateMeshesRequested = CreateMeshes.IsChecked == true; DialogResult = true;
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
    }
}
