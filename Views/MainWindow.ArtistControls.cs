using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Views;

namespace ProjectionMapper
{
    public partial class MainWindow
    {
        private bool _liveCommandBusy;
        private void InitializeArtistControls()
        {
            _playlistService.FadeRequestedAsync = _rendererManager.FadeToAsync;
            _playlistService.GroupChanged += _ => Dispatcher.BeginInvoke(UpdateCueDisplay);
            _playlistService.PlaybackStateChanged += _ => Dispatcher.BeginInvoke(() => { _vm.SetPlaybackState(_playlistService.IsPlaying); UpdateCueDisplay(); });
            _playlistService.ErrorOccurred += message => Dispatcher.BeginInvoke(() => _vm.StatusText = $"Cue error: {message}");
            InputManager.Current.PreProcessInput += ArtistInput;
            Closed += (_, _) => InputManager.Current.PreProcessInput -= ArtistInput;
            UpdateCueDisplay();
        }
        private void ArtistInput(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is not KeyEventArgs key || key.RoutedEvent != Keyboard.PreviewKeyDownEvent) return;
            if (key.Key == Key.F12 && !key.IsRepeat) { ToggleBlackout(); key.Handled = true; return; }
            if (key.Key == Key.F10 && !key.IsRepeat && IsActive) { PART_Performance.IsChecked = PART_Performance.IsChecked != true; key.Handled = true; return; }
            if (!_rendererManager.IsPerformanceMode) return;
            if (key.Key == Key.Space && !key.IsRepeat) { Go_Click(this, new RoutedEventArgs()); key.Handled = true; return; }
            // Editor command bindings must not bypass the disabled editor via keyboard shortcuts.
            if (key.Key is Key.Delete or Key.Back || ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && key.Key != Key.S)) key.Handled = true;
        }
        private void ToggleBlackout()
        {
            _rendererManager.SetBlackout(!_rendererManager.IsBlackout);
            PART_Blackout.Content = _rendererManager.IsBlackout ? "RESTORE OUTPUT (F12)" : "BLACKOUT (F12)";
            _vm.StatusText = _rendererManager.IsBlackout ? "Blackout — playback continues; F12 restores the current cue" : "Output restored to current cue";
        }
        private void Blackout_Click(object sender, RoutedEventArgs e) => ToggleBlackout();
        private void Performance_Changed(object sender, RoutedEventArgs e)
        {
            if (_rendererManager == null) return;
            bool enabled = PART_Performance.IsChecked == true;
            // Generation may finish while editing is locked; accepting or projecting new variants waits until unlock.
            _artworkWindow?.SetEditingLocked(enabled);
            _rendererManager.SetPerformanceMode(enabled);
            PART_EditorContent.IsEnabled = PART_EditToolbar.IsEnabled = PART_FileMenu.IsEnabled = PART_EditMenu.IsEnabled = PART_PlaylistMenu.IsEnabled = !enabled;
            PART_CueSettings.IsEnabled = !enabled; PART_OutputSettings.IsEnabled = !enabled;
            _vm.StatusText = enabled ? "Performance mode — editing locked; Space = Go, F12 = blackout" : "Editing enabled";
        }
        private async void Go_Click(object sender, RoutedEventArgs e)
        {
            if (_liveCommandBusy) return;
            _liveCommandBusy = true; PART_Go.IsEnabled = false;
            try
            {
                if (_vm.PlaylistGroups.Count == 0) { _vm.StatusText = "Create a playlist group to run cues."; return; }
                if (!_vm.IsPlaylistMode) _vm.IsPlaylistMode = true;
                if (_playlistService.CurrentGroup == null || (!_playlistService.IsPlaying && !_playlistService.IsPaused))
                    await _playlistService.StartPlaylistAsync(_vm.BuildPlaylistGroupModels());
                else
                {
                    if (_playlistService.IsPaused) await _playlistService.ResumeCurrentGroupAsync();
                    await _playlistService.AdvanceToNextGroupAsync();
                }
                UpdateCueDisplay();
            }
            catch (Exception ex) { Debug.WriteLine($"Go failed: {ex}"); _vm.StatusText = $"Could not run cue: {ex.Message}"; }
            finally { _liveCommandBusy = false; PART_Go.IsEnabled = true; }
        }
        private void UpdateCueDisplay()
        {
            var current = _playlistService.CurrentGroup; var next = _playlistService.NextGroup;
            foreach (var group in _vm.PlaylistGroups) group.IsActive = group.Id == current?.Id;
            foreach (var item in _vm.ProjectTree.OfType<ProjectionMapper.ViewModels.PlaylistGroupTreeViewModel>()) item.IsActive = item.Id == current?.Id;
            PART_CurrentCue.Text = current == null ? "Current cue: stopped" : $"Current: {current.Name} · {current.AdvanceMode}{(_playlistService.IsPaused ? " · paused" : string.Empty)}";
            PART_NextCue.Text = next == null ? "Next: end of playlist" : $"Next: {next.Name} · {next.SourceIds.Count} source(s)";
            BitmapSource? frame = null;
            if (next != null) foreach (var source in next.SourceIds)
            {
                var mesh = _vm.ImportedVideos.FirstOrDefault(v => v.Id == source)?.MeshLayers.FirstOrDefault();
                if (mesh != null && _rendererManager.TryGetArtwork(mesh.Id!, out var artwork, includePreview: false)) frame = artwork;
                else _videoService.TryGetLastFrame(source, out frame);
                if (frame != null) break;
            }
            PART_NextCueImage.Source = frame;
            PART_NextCueNote.Text = frame == null ? "No cached next-cue image yet" : "Cached source preview — first available surface; unwarped";
        }
        private void SurfaceControls_Click(object sender, RoutedEventArgs e)
        {
            var mesh = _vm.SelectedMeshLayer;
            if (mesh == null) { _vm.StatusText = "Select a mapped surface first."; return; }
            if (mesh.IsLocked || _rendererManager.IsPerformanceMode) { _vm.StatusText = "Unlock this surface to edit its mask or curved mesh."; return; }
            var before = mesh.Model.Appearance.Copy();
            try
            {
                BitmapSource? frame;
                if (!_rendererManager.TryGetArtwork(mesh.Id!, out frame, includePreview: false)) _videoService.TryGetLastFrame(mesh.Model.SourceId!, out frame);
                var window = new SurfaceControlsWindow(before, frame, draft => { _rendererManager.SetSurfaceAppearance(mesh.Id!, draft); _videoService.RefreshMeshLayerRendering(mesh.Id!); }) { Owner = this };
                bool apply = window.ShowDialog() == true;
                _rendererManager.SetSurfaceAppearance(mesh.Id!, before);
                if (apply)
                {
                    var after = window.Result.Copy();
                    void Set(SurfaceAppearance value)
                    { mesh.Model.Appearance = value.Copy(); _rendererManager.SetSurfaceAppearance(mesh.Id!, value); _videoService.RefreshMeshLayerRendering(mesh.Id!); _vm.MarkProjectDirty(); }
                    Set(after); _vm.UndoRedoService.RecordAction(new ArtworkAssignmentAction("Edit surface mask and curved mesh", () => Set(before), () => Set(after)));
                }
                _videoService.RefreshMeshLayerRendering(mesh.Id!);
            }
            catch (Exception ex) { _rendererManager.SetSurfaceAppearance(mesh.Id!, before); Debug.WriteLine($"Surface controls failed: {ex}"); _vm.StatusText = ex.Message; }
        }
        private static List<ArtistOutput> ArtistOutputs() => System.Windows.Forms.Screen.AllScreens.Select((screen, index) => new ArtistOutput(index, screen.DeviceName, $"{index + 1}: {screen.DeviceName} ({screen.Bounds.Width}×{screen.Bounds.Height})")).ToList();
        private void ApplySavedOutputAppearances(ProjectModel project)
        {
            foreach (var output in ArtistOutputs())
            {
                var saved = project.OutputAppearances.FirstOrDefault(s => s.DisplayName == output.Name);
                if (saved != null) { var actual = saved.Copy(); actual.MonitorIndex = output.Index; _rendererManager.SetOutputAppearance(output.Index, actual); }
                else _rendererManager.SetOutputAppearance(output.Index, null);
            }
        }
        private void OutputControls_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.ActiveProject == null) return;
            var before = _vm.ActiveProject.OutputAppearances.Select(s => s.Copy()).ToList();
            try
            {
                var window = new OutputControlsWindow(ArtistOutputs(), before, _rendererManager.SetOutputAppearance) { Owner = this };
                bool apply = window.ShowDialog() == true;
                _vm.ActiveProject.OutputAppearances = before; ApplySavedOutputAppearances(_vm.ActiveProject);
                if (apply)
                {
                    var after = window.Result;
                    void Set(List<OutputAppearance> settings) { _vm.ActiveProject.OutputAppearances = settings.Select(s => s.Copy()).ToList(); ApplySavedOutputAppearances(_vm.ActiveProject); _vm.MarkProjectDirty(); }
                    Set(after); _vm.UndoRedoService.RecordAction(new ArtworkAssignmentAction("Adjust projector color and blending", () => Set(before), () => Set(after)));
                }
            }
            catch (Exception ex) { _vm.ActiveProject.OutputAppearances = before; ApplySavedOutputAppearances(_vm.ActiveProject); Debug.WriteLine($"Output controls failed: {ex}"); _vm.StatusText = ex.Message; }
        }
        private void CueControls_Click(object sender, RoutedEventArgs e)
        {
            var group = _vm.SelectedPlaylistGroup;
            if (group == null) { _vm.StatusText = "Select a playlist group to edit its cue settings."; return; }
            try
            {
                var before = PlaylistService.Copy(group.Model); var window = new CueControlsWindow(before) { Owner = this };
                if (window.ShowDialog() != true) return;
                var after = window.Result;
                void Set(PlaylistGroupModel value)
                {
                    group.Name = value.Name; group.PlaybackMode = value.PlaybackMode; group.Model.AdvanceMode = value.AdvanceMode;
                    group.Model.HoldSeconds = value.HoldSeconds; group.Model.FadeSeconds = value.FadeSeconds;
                    _vm.RefreshProjectTree(); _vm.MarkProjectDirty();
                }
                Set(after); _vm.UndoRedoService.RecordAction(new ArtworkAssignmentAction("Edit cue settings", () => Set(before), () => Set(after)));
                _vm.StatusText = "Cue settings saved. Restart the playlist to use the updated cue list.";
            }
            catch (Exception ex) { Debug.WriteLine($"Cue settings failed: {ex}"); _vm.StatusText = ex.Message; }
        }
    }
}
