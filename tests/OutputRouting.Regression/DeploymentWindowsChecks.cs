using System;
using System.Collections.Generic;
using System.Windows;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Views;

internal static class DeploymentWindowsChecks
{
    public static void Run()
    {
        // Explicit layout fixtures exercise WPF templates without inventing physical projector observations.
        var layout = new OutputLayout { ComputerId = "window-fixture", Displays = new() { new(0, "fixture-display", "fixture-id", 0, 0, 800, 600, 800, 600, true) } };
        var project = new ProjectModel { Name = "Window fixture", OutputLayout = layout };
        project.ImportedVideos.Add(new() { Id = "fixture", SourcePath = "not-opened.mp4", TargetMonitorIndex = 0 });
        var setup = new OutputSetupWindow(project, layout); setup.Close();
        var report = new RehearsalReport { ProjectName = "Window fixture" };
        var window = new DeploymentWindow(() => project, () => layout, () => null, _ => System.Threading.Tasks.Task.CompletedTask,
            _ => System.Threading.Tasks.Task.FromResult<RehearsalReport?>(report), () => false);
        window.ShowPreflight(new() { ProjectName = "Window fixture" }); window.ShowRehearsal(report); window.Close();
        Console.WriteLine("Windows destination-output and deployment/report window construction checks passed.");
    }
}
