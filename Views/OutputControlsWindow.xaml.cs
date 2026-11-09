using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ProjectionMapper.Models;
using ProjectionMapper.Utilities;

namespace ProjectionMapper.Views
{
    public sealed record ArtistOutput(int Index, string Name, string Label);
    public partial class OutputControlsWindow : Window
    {
        private readonly Dictionary<string, OutputAppearance> _drafts;
        private readonly Action<int, OutputAppearance?> _preview;
        private bool _ready;
        public List<OutputAppearance> Result => _drafts.Values.Select(s => s.Copy()).ToList();
        public OutputControlsWindow(List<ArtistOutput> outputs, List<OutputAppearance> current, Action<int, OutputAppearance?> preview)
        {
            InitializeComponent(); _drafts = current.ToDictionary(s => s.DisplayName, s => s.Copy()); _preview = preview;
            _ready = true; Output.ItemsSource = outputs; if (outputs.Count > 0) Output.SelectedIndex = 0;
        }
        private void ShowStyle(OutputAppearance s)
        {
            _ready = false;
            Brightness.Value = s.Brightness; Gamma.Value = s.Gamma; Red.Value = s.Red; Green.Value = s.Green; Blue.Value = s.Blue;
            BlendLeftControl.Value = s.BlendLeft * 100; BlendRightControl.Value = s.BlendRight * 100; BlendTopControl.Value = s.BlendTop * 100; BlendBottomControl.Value = s.BlendBottom * 100; Power.Value = s.BlendPower;
            _ready = true; ShowValues();
        }
        private void Output_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || Output.SelectedItem is not ArtistOutput output) return;
            ShowStyle(_drafts.GetValueOrDefault(output.Name) ?? new() { MonitorIndex = output.Index, DisplayName = output.Name });
        }
        private void ShowValues() => Values.Text = $"Brightness {Brightness.Value:0.00} · Gamma {Gamma.Value:0.00} · RGB {Red.Value:0.00}/{Green.Value:0.00}/{Blue.Value:0.00}\nEdges L/R/T/B {BlendLeftControl.Value:0.#}/{BlendRightControl.Value:0.#}/{BlendTopControl.Value:0.#}/{BlendBottomControl.Value:0.#}% · Power {Power.Value:0.00}";
        private void Value_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || Output.SelectedItem is not ArtistOutput output) return;
            try
            {
                var s = new OutputAppearance { MonitorIndex = output.Index, DisplayName = output.Name, Brightness = Brightness.Value, Gamma = Gamma.Value,
                    Red = Red.Value, Green = Green.Value, Blue = Blue.Value, BlendLeft = BlendLeftControl.Value / 100, BlendRight = BlendRightControl.Value / 100,
                    BlendTop = BlendTopControl.Value / 100, BlendBottom = BlendBottomControl.Value / 100, BlendPower = Power.Value };
                ArtistRules.Validate(s); _drafts[output.Name] = s; _preview(output.Index, s); ShowValues(); Status.Text = string.Empty;
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (Output.SelectedItem is not ArtistOutput output) return;
            var s = new OutputAppearance { MonitorIndex = output.Index, DisplayName = output.Name }; _drafts[output.Name] = s; ShowStyle(s); _preview(output.Index, s);
        }
        private void Apply_Click(object sender, RoutedEventArgs e) { foreach (var s in _drafts.Values) ArtistRules.Validate(s); DialogResult = true; }
    }
}
