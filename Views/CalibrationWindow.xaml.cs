using System;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ProjectionMapper.Rendering;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;
using ProjectionMapper.ViewModels;

namespace ProjectionMapper.Views
{
    public record CalibrationOutput(int Index, string Name, int Width, int Height);

    public partial class CalibrationWindow : Window
    {
        private readonly LayerViewModel? _layer;
        private readonly UndoRedoService _undo;
        private readonly Action<int, BitmapSource?> _showPattern;
        private readonly Action _refresh;
        private readonly Func<int, double> _timing;
        private int? _patternOutput;
        private bool _ready;
        private int _coordinateUndos;
        private int _coordinateRedos;

        public CalibrationWindow(IReadOnlyList<CalibrationOutput> outputs, LayerViewModel? layer,
            UndoRedoService undo, Action<int, BitmapSource?> showPattern, Action refresh, Func<int, double> timing)
        {
            InitializeComponent();
            _layer = layer; _undo = undo; _showPattern = showPattern; _refresh = refresh; _timing = timing;
            Output.ItemsSource = outputs;
            Output.SelectedItem = outputs.FirstOrDefault(o => o.Index == layer?.TargetMonitorIndex) ?? outputs.FirstOrDefault();
            MeshName.Text = layer == null ? "Select a mesh in the main window to edit its output corners. Test grids work without media." : layer.Name;
            Locked.IsChecked = layer?.IsLocked == true;
            _ready = true;
            UpdateFields();
            Closed += (_, _) => ClearPattern();
        }

        private CalibrationOutput? SelectedOutput => Output.SelectedItem as CalibrationOutput;
        private void ClearPattern()
        {
            if (_patternOutput.HasValue) _showPattern(_patternOutput.Value, null);
            _patternOutput = null;
        }
        private void ShowPattern()
        {
            try
            {
                ClearPattern();
                if (Pattern.IsChecked != true || SelectedOutput is not { } output) return;
                if (!int.TryParse(GridSpacing.Text, out int spacing) || spacing < 8)
                    throw new ArgumentException("Grid spacing must be at least 8 pixels.");
                _showPattern(output.Index, CalibrationPattern.Create(output.Width, output.Height, output.Index + 1, spacing));
                _patternOutput = output.Index;
                Activate();
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void UpdateFields()
        {
            if (!_ready) return;
            bool editable = _layer != null && !_layer.IsLocked && SelectedOutput != null;
            Coordinates.IsEnabled = editable;
            Nudges.IsEnabled = editable;
            Locked.IsEnabled = _layer != null;
            if (_layer != null && SelectedOutput is { } output && Corner.SelectedIndex >= 0)
            {
                var point = _layer.OutputMeshPoints[Corner.SelectedIndex];
                XValue.Text = (point.X * output.Width).ToString("0.###", CultureInfo.CurrentCulture);
                YValue.Text = (point.Y * output.Height).ToString("0.###", CultureInfo.CurrentCulture);
            }
        }
        private static double Number(string text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number) || !double.IsFinite(number))
                throw new ArgumentException("Enter a finite numeric coordinate or step.");
            return number;
        }
        private void Move(double x, double y, bool snap)
        {
            try
            {
                if (_layer == null || _layer.IsLocked || SelectedOutput is not { } output) return;
                if (_layer.TargetMonitorIndex != output.Index)
                    throw new InvalidOperationException("Assign the mesh to this display before editing its pixel coordinates.");
                double step = snap && Snap.IsChecked == true ? Number(SnapStep.Text) : 0;
                if (snap && Snap.IsChecked == true && step <= 0) throw new ArgumentException("Snap spacing must be positive.");
                int corner = Corner.SelectedIndex;
                var before = _layer.OutputMeshPoints[corner];
                var after = QuadGeometry.FromPixels(x, y, output.Width, output.Height, step);
                var candidate = (Vector2[])_layer.OutputMeshPoints.Clone();
                candidate[corner] = after;
                if (!QuadGeometry.IsValid(candidate)) throw new ArgumentException("Corners must form a non-crossing, convex quadrilateral.");
                if (before == after) return;
                _layer.SetOutputMeshPoint(corner, after);
                _undo.RecordAction(new MeshPointChangeAction(_layer, corner, before, after, isOutputMesh: true));
                _coordinateUndos++;
                _coordinateRedos = 0;
                _refresh();
                Status.Text = "Output corner updated.";
                UpdateFields();
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void Nudge(int dx, int dy)
        {
            try
            {
                if (_layer == null || SelectedOutput is not { } output) return;
                double step = Number(NudgeStep.Text);
                if (step <= 0) throw new ArgumentException("Nudge step must be positive.");
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) step *= 10;
                var point = _layer.OutputMeshPoints[Corner.SelectedIndex];
                Move(point.X * output.Width + dx * step, point.Y * output.Height + dy * step, snap: false);
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            try { Move(Number(XValue.Text), Number(YValue.Text), snap: true); }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void Assign_Click(object sender, RoutedEventArgs e)
        {
            if (_layer == null || SelectedOutput == null) return;
            if (_layer.IsLocked) { Status.Text = "Unlock the mapping before changing its output."; return; }
            _layer.TargetMonitorIndex = SelectedOutput.Index;
            _refresh();
            UpdateFields();
        }
        private void Locked_Click(object sender, RoutedEventArgs e)
        {
            if (_layer == null) return;
            _layer.IsLocked = Locked.IsChecked == true;
            _refresh();
            UpdateFields();
        }
        private void Nudge_Click(object sender, RoutedEventArgs e)
        {
            var values = ((string)((Button)sender).Tag).Split(',');
            Nudge(int.Parse(values[0], CultureInfo.InvariantCulture), int.Parse(values[1], CultureInfo.InvariantCulture));
        }
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is ComboBox) return;
            switch (e.Key)
            {
                case Key.Left: Nudge(-1, 0); break;
                case Key.Right: Nudge(1, 0); break;
                case Key.Up: Nudge(0, -1); break;
                case Key.Down: Nudge(0, 1); break;
                default: return;
            }
            e.Handled = true;
        }
        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_coordinateUndos == 0) { Status.Text = "No coordinate edits to undo in this panel."; return; }
            _undo.Undo(); _coordinateUndos--; _coordinateRedos++;
            _refresh(); UpdateFields();
        }
        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_coordinateRedos == 0) { Status.Text = "No coordinate edits to redo in this panel."; return; }
            _undo.Redo(); _coordinateRedos--; _coordinateUndos++;
            _refresh(); UpdateFields();
        }
        private void Timing_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedOutput != null)
            {
                double milliseconds = _timing(SelectedOutput.Index);
                Status.Text = milliseconds > 0
                    ? $"Last software composition: {milliseconds:0.00} ms. A 30 fps frame budget is 33.33 ms; decoder/display latency is excluded."
                    : "No composition timing is available. Assign an output or show a test grid first.";
            }
        }
        private void Pattern_Click(object sender, RoutedEventArgs e) => ShowPattern();
        private void Output_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) { ShowPattern(); UpdateFields(); } }
        private void Corner_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFields();
        private void Done_Click(object sender, RoutedEventArgs e) => Close();
    }
}
