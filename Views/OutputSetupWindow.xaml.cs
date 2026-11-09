using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ProjectionMapper.Models;
using ProjectionMapper.Services;

namespace ProjectionMapper.Views
{
    public sealed record OutputChoice(int Index, string Label);
    public sealed class OutputAssignmentRow
    {
        public int SavedIndex { get; init; }
        public string Label { get; init; } = string.Empty;
        public List<OutputChoice> Choices { get; init; } = new();
        public int Target { get; set; } = -1;
    }
    public partial class OutputSetupWindow : Window
    {
        private readonly ProjectModel _project;
        private readonly OutputLayout _destination;
        private readonly List<OutputAssignmentRow> _rows;
        public Dictionary<int, int> Mapping => _rows.ToDictionary(r => r.SavedIndex, r => r.Target);
        public bool CopyColor => RetainColor.IsChecked == true;
        public OutputSetupWindow(ProjectModel project, OutputLayout destination)
        {
            InitializeComponent(); _project = project; _destination = destination;
            var choices = new List<OutputChoice> { new(-1, "Preview only — no projector") };
            choices.AddRange(destination.Displays.Select(d => new OutputChoice(d.Index, $"{d.Index + 1}: {d.DeviceName} {d.RenderWidth}×{d.RenderHeight}{(d.Primary ? " (Primary)" : "")}")));
            _rows = DeploymentProject.AssignedOutputs(project).Select(i => new OutputAssignmentRow { SavedIndex = i, Label = $"{i + 1}: {project.OutputLayout?.Displays.FirstOrDefault(d => d.Index == i)?.DeviceName ?? "Previous display"}", Choices = choices }).ToList();
            Assignments.ItemsSource = _rows;
        }
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Confirmed.IsChecked != true) throw new InvalidOperationException("Review the assignments and confirm them before continuing.");
                DeploymentProject.ValidateMapping(_project, _destination, Mapping);
                DialogResult = true;
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
    }
}
