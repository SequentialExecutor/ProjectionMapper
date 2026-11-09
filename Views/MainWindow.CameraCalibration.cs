using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Windows;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;
using ProjectionMapper.Views;

namespace ProjectionMapper
{
    public partial class MainWindow
    {
        private void CameraCalibration_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rendererManager.IsPerformanceMode || PART_Performance.IsChecked == true) throw new InvalidOperationException("Exit performance mode before camera calibration.");
                EnumerateMonitors();
                if (_outputsNeedSetup) throw new InvalidOperationException("Review Output setup before projecting calibration markers.");
                var mesh = _vm.SelectedMeshLayer ?? throw new InvalidOperationException("Select a planar mapped surface first.");
                if (mesh.IsLocked) throw new InvalidOperationException("Unlock the selected mapping first.");
                if (mesh.Model.Appearance.Grid.Count > 0) throw new InvalidOperationException("Disable this surface's curved grid before planar camera calibration. Different depths require separate planes.");
                var output = _detectedDisplays.SingleOrDefault(d => d.Index == mesh.TargetMonitorIndex) ?? throw new InvalidOperationException("Assign this surface to an available projector first.");
                var session = _projectSession; var hardware = CaptureOutputLayout(); var before = (Vector2[])mesh.OutputMeshPoints.Clone();
                bool Current() => session == _projectSession && !_outputsNeedSetup && !_rendererManager.IsPerformanceMode && !mesh.IsLocked && mesh.Model.Appearance.Grid.Count == 0 &&
                    mesh.TargetMonitorIndex == output.Index && ArtworkMeshes().Any(m => ReferenceEquals(m, mesh)) && DeploymentProject.SameLayout(hardware, CaptureOutputLayout());
                _rendererManager.SetCameraCalibrationSession(true);
                try
                {
                    var window = new CameraCalibrationWindow(output, hardware.ComputerId, mesh.Id!, mesh.Name, before, Current,
                        pattern =>
                        {
                            if (pattern != null && !Current()) throw new InvalidOperationException("Output environment changed. Review output setup and reopen calibration.");
                            _rendererManager.SetCameraCalibrationPattern(pattern == null ? null : output.Index, pattern);
                            EnsureMonitorWindows(force: true);
                        }, _vm.ActiveProject?.CameraCalibrations.Where(c => c.MeshId == mesh.Id).Select(c => c.Copy()).ToList() ?? new(), id =>
                        {
                            if (session != _projectSession || _vm.ActiveProject == null) throw new InvalidOperationException("The project changed.");
                            var library = _vm.ActiveProject.CameraCalibrations; var entry = library.FirstOrDefault(c => c.Id == id); if (entry == null) return;
                            int position = library.IndexOf(entry);
                            void Restore(bool restore)
                            {
                                if (session != _projectSession) throw new InvalidOperationException("Evidence history belongs to a different project.");
                                if (restore) { if (library.Count >= 32) throw new InvalidOperationException("The evidence library is full."); library.Insert(Math.Min(position, library.Count), entry); }
                                else library.RemoveAll(c => c.Id == id);
                                _vm.MarkProjectDirty();
                            }
                            Restore(false); _vm.UndoRedoService.RecordAction(new ArtworkAssignmentAction("Remove camera calibration evidence", () => Restore(true), () => Restore(false)));
                        }, () => !_rendererManager.IsBlackout) { Owner = this };
                    if (window.ShowDialog() != true || window.Result == null) return;
                    if (!Current()) throw new InvalidOperationException("The project or output changed before calibration could be applied.");
                    var record = window.Result.Copy(); PlanarCalibration.ValidateRecord(record);
                    _vm.ActiveProject ??= new ProjectModel(); var project = _vm.ActiveProject;
                    if (project.CameraCalibrations.Count >= 32) throw new InvalidOperationException("This project already has 32 calibration records. Archive or remove older evidence before adding more.");
                    var after = Enumerable.Range(0, 4).Select(i => new Vector2(record.OutputCorners[i * 2], record.OutputCorners[i * 2 + 1])).ToArray();
                    void Set(bool applied)
                    {
                        if (session != _projectSession) throw new InvalidOperationException("Calibration history belongs to a different project.");
                        if (applied && !project.CameraCalibrations.Any(c => c.Id == record.Id))
                        { if (project.CameraCalibrations.Count >= 32) throw new InvalidOperationException("The calibration evidence library is full."); project.CameraCalibrations.Add(record.Copy()); }
                        if (!applied) project.CameraCalibrations.RemoveAll(c => c.Id == record.Id);
                        mesh.SetOutputQuad(applied ? after : before);
                        _videoService.RefreshMeshLayerRendering(mesh.Id!); _vm.MarkProjectDirty();
                    }
                    Set(true); _vm.UndoRedoService.RecordAction(new ArtworkAssignmentAction("Apply camera-assisted planar calibration", () => Set(false), () => Set(true)));
                    _rehearsalRecorder?.Record("camera-calibration", $"{mesh.Name}: independent check RMS {record.ValidationRmsPixels:0.###} px; max {record.ValidationMaxPixels:0.###} px. Physical review confirmed by operator.");
                    _vm.StatusText = $"Camera calibration applied to {mesh.Name}; independent check max {record.ValidationMaxPixels:0.###} projector px. Save to retain the photo and evidence.";
                }
                finally { _rendererManager.SetCameraCalibrationSession(false); EnsureMonitorWindows(force: true); }
            }
            catch (Exception ex) { Debug.WriteLine($"Camera calibration failed: {ex}"); _vm.StatusText = ex.Message; MessageBox.Show(ex.Message, "Camera calibration", MessageBoxButton.OK, MessageBoxImage.Information); }
        }
    }
}
