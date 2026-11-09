using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ProjectionMapper.Models;
using ProjectionMapper.Utilities;

namespace ProjectionMapper.Views
{
    /// <summary>Edits a detached draft. Explicit preview is reversible; only Apply changes the project.</summary>
    public partial class SurfaceControlsWindow : Window
    {
        public SurfaceAppearance Result { get; private set; }
        private readonly Action<SurfaceAppearance> _preview;
        private readonly Stack<SurfaceAppearance> _undo = new();
        private bool _drawing, _ready, _dragging;
        private int _selected = -1;
        private List<PhotoPoint> Points => EditGrid.IsChecked == true ? Result.Grid : Result.Mask;
        public SurfaceControlsWindow(SurfaceAppearance current, BitmapSource? frame, Action<SurfaceAppearance> preview)
        {
            InitializeComponent(); Result = current.Copy(); _preview = preview; Reference.Source = frame;
            Feather.Value = Result.Feather * 100; _ready = true; Redraw();
        }
        private void Remember() => _undo.Push(Result.Copy());
        private PhotoPoint Position(MouseEventArgs e)
        { var p = e.GetPosition(Editor); return new(Math.Clamp(p.X / 560, 0, 1), Math.Clamp(p.Y / 420, 0, 1)); }
        private void Redraw()
        {
            if (!_ready) return;
            Editor.Children.Clear(); FeatherValue.Text = $"{Result.Feather * 100:0.#}%";
            void Line(PhotoPoint a, PhotoPoint b, Brush color)
            { Editor.Children.Add(new Line { X1 = a.X * 560, Y1 = a.Y * 420, X2 = b.X * 560, Y2 = b.Y * 420, Stroke = color, StrokeThickness = 1.5, IsHitTestVisible = false }); }
            for (int i = 0; i < Result.Mask.Count; i++)
                if (!_drawing || i + 1 < Result.Mask.Count) Line(Result.Mask[i], Result.Mask[(i + 1) % Result.Mask.Count], Brushes.Gold);
            int stride = Result.GridColumns + 1;
            for (int i = 0; i < Result.Grid.Count; i++)
            {
                if (i % stride < Result.GridColumns) Line(Result.Grid[i], Result.Grid[i + 1], Brushes.DeepSkyBlue);
                if (i + stride < Result.Grid.Count) Line(Result.Grid[i], Result.Grid[i + stride], Brushes.DeepSkyBlue);
            }
            for (int i = 0; i < Points.Count; i++)
            {
                var node = new Ellipse { Width = 11, Height = 11, Fill = i == _selected ? Brushes.White : EditGrid.IsChecked == true ? Brushes.DeepSkyBlue : Brushes.Gold, Stroke = Brushes.Black, IsHitTestVisible = false };
                Canvas.SetLeft(node, Points[i].X * 560 - 5.5); Canvas.SetTop(node, Points[i].Y * 420 - 5.5); Editor.Children.Add(node);
            }
            if (_selected >= 0 && _selected < Points.Count) { VertexX.Text = Points[_selected].X.ToString("0.####", CultureInfo.InvariantCulture); VertexY.Text = Points[_selected].Y.ToString("0.####", CultureInfo.InvariantCulture); }
        }
        private bool ValidateDraft()
        {
            try { ArtistRules.Validate(Result); Status.Text = string.Empty; return true; }
            catch (Exception ex) { Status.Text = ex.Message; return false; }
        }
        private void Draw_Click(object sender, RoutedEventArgs e) { Remember(); Result.Mask.Clear(); EditGrid.IsChecked = false; _drawing = true; _selected = -1; Redraw(); }
        private void Finish_Click(object sender, RoutedEventArgs e) { if (ValidateDraft()) _drawing = false; Redraw(); }
        private void ClearMask_Click(object sender, RoutedEventArgs e) { Remember(); Result.Mask.Clear(); _drawing = false; _selected = -1; Redraw(); }
        private void Grid_Click(object sender, RoutedEventArgs e)
        {
            Remember(); int cells = int.Parse(((ComboBoxItem)Cells.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
            var grid = ArtistRules.RegularGrid(cells, cells); Result.GridColumns = cells; Result.GridRows = cells; Result.Grid = grid.Grid;
            _selected = -1; EditGrid.IsChecked = true; _drawing = false; Redraw();
        }
        private void ClearGrid_Click(object sender, RoutedEventArgs e) { Remember(); Result.Grid.Clear(); Result.GridRows = Result.GridColumns = 0; _selected = -1; Redraw(); }
        private void Mode_Changed(object sender, RoutedEventArgs e) { if (!_ready) return; _drawing = false; _selected = -1; Redraw(); }
        private void Feather_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_ready) return; Result.Feather = Feather.Value / 100; Redraw(); }
        private void Editor_Down(object sender, MouseButtonEventArgs e)
        {
            var p = Position(e);
            if (_drawing)
            {
                if (Result.Mask.Count >= 32) { Status.Text = "A mask supports up to 32 vertices."; return; }
                Remember(); Result.Mask.Add(p); _selected = Result.Mask.Count - 1; Redraw(); return;
            }
            _selected = Enumerable.Range(0, Points.Count).OrderBy(i => Math.Pow((Points[i].X - p.X) * 560, 2) + Math.Pow((Points[i].Y - p.Y) * 420, 2)).DefaultIfEmpty(-1).First();
            if (_selected >= 0 && Math.Abs(Points[_selected].X - p.X) < .04 && Math.Abs(Points[_selected].Y - p.Y) < .04)
            { Remember(); _dragging = true; Editor.CaptureMouse(); }
            else _selected = -1;
            Redraw();
        }
        private void Editor_Move(object sender, MouseEventArgs e) { if (!_dragging || _selected < 0) return; Points[_selected] = Position(e); Redraw(); }
        private void Editor_Up(object sender, MouseButtonEventArgs e) { _dragging = false; Editor.ReleaseMouseCapture(); ValidateDraft(); }
        private void SetVertex_Click(object sender, RoutedEventArgs e)
        {
            if (_selected < 0 || _selected >= Points.Count) return;
            if (!double.TryParse(VertexX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(VertexY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1)
            { Status.Text = "Enter X and Y between 0 and 1."; return; }
            Remember(); Points[_selected] = new(x, y); Redraw(); ValidateDraft();
        }
        private void DeleteVertex_Click(object sender, RoutedEventArgs e) { if (EditGrid.IsChecked == true || _selected < 0 || _selected >= Points.Count) return; Remember(); Points.RemoveAt(_selected); _selected = -1; Redraw(); }
        private void Undo_Click(object sender, RoutedEventArgs e) { if (_undo.Count == 0) return; Result = _undo.Pop(); Feather.Value = Result.Feather * 100; _selected = -1; Redraw(); ValidateDraft(); }
        private void Preview_Click(object sender, RoutedEventArgs e) { if (ValidateDraft()) { _drawing = false; _preview(Result.Copy()); } }
        private void Apply_Click(object sender, RoutedEventArgs e) { if (ValidateDraft()) { DialogResult = true; } }
    }
}
