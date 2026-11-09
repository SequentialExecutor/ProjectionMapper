using System;
using System.Diagnostics;
using System.Windows;
using ProjectionMapper.Services;

namespace ProjectionMapper
{
    public partial class MainWindow
    {
        private void InitializeWorkspaceUi()
        {
            if (Application.Current != null && !UiThemeService.IsInitialized) UiThemeService.Initialize(Application.Current);
            UiThemeService.Changed += UpdateAppearanceLabels;
            Closed += (_, _) => UiThemeService.Changed -= UpdateAppearanceLabels;
            SizeChanged += (_, _) => PART_InspectorScroll.MaxHeight = Math.Clamp(PART_EditorContent.ActualHeight * .4, 120, 280);
            ContentRendered += (_, _) =>
            {
                // Fit only a fresh empty workspace; loading a saved project retains its persisted zoom.
                if (_vm.ImportedVideos.Count == 0 && !_loadingProject && !_vm.HasUnsavedChanges && !string.IsNullOrEmpty(_savedFingerprint))
                {
                    FitInput_Click(this, new RoutedEventArgs()); FitOutput_Click(this, new RoutedEventArgs());
                    _savedFingerprint = ProjectFingerprint(BuildProjectModel());
                }
            };
            UpdateAppearanceLabels(); UpdateDisplaySummary();
        }
        private void UpdateAppearanceLabels()
        {
            PART_ThemeToggle.Content = UiThemeService.IsDark ? "Light mode" : "Dark mode";
            PART_DarkAppearance.IsChecked = UiThemeService.Appearance == UiAppearance.Dark;
            PART_LightAppearance.IsChecked = UiThemeService.Appearance == UiAppearance.Light;
            PART_SystemAppearance.IsChecked = UiThemeService.Appearance == UiAppearance.System;
        }
        private void UpdateDisplaySummary() => PART_DisplaySummary.Text = _detectedDisplays.Count == 0 ? "Outputs not configured" : $"{_detectedDisplays.Count} display{(_detectedDisplays.Count == 1 ? "" : "s")} available";
        private void ToggleAppearance_Click(object sender, RoutedEventArgs e) => UiThemeService.SetAppearance(UiThemeService.IsDark ? UiAppearance.Light : UiAppearance.Dark);
        private void DarkAppearance_Click(object sender, RoutedEventArgs e) => UiThemeService.SetAppearance(UiAppearance.Dark);
        private void LightAppearance_Click(object sender, RoutedEventArgs e) => UiThemeService.SetAppearance(UiAppearance.Light);
        private void SystemAppearance_Click(object sender, RoutedEventArgs e) => UiThemeService.SetAppearance(UiAppearance.System);
        private void FitInput_Click(object sender, RoutedEventArgs e)
        {
            try { _vm.InputZoom = WorkspaceViewMath.FitZoom(PART_InputScroll.ViewportWidth, PART_InputScroll.ViewportHeight, PART_InputCanvas.Width, PART_InputCanvas.Height); PART_InputScroll.ScrollToHome(); }
            catch (Exception ex) { Debug.WriteLine($"Source fit unavailable: {ex.Message}"); }
        }
        private void FitOutput_Click(object sender, RoutedEventArgs e)
        {
            try { _vm.OutputZoom = WorkspaceViewMath.FitZoom(PART_OutputScroll.ViewportWidth, PART_OutputScroll.ViewportHeight, PART_OutputCanvas.Width, PART_OutputCanvas.Height); PART_OutputScroll.ScrollToHome(); }
            catch (Exception ex) { Debug.WriteLine($"Projection fit unavailable: {ex.Message}"); }
        }
    }
}
