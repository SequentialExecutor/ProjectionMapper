using System;
using System.Globalization;
using System.Windows;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;

namespace ProjectionMapper.Views
{
    public partial class CueControlsWindow : Window
    {
        public PlaylistGroupModel Result { get; private set; }
        public CueControlsWindow(PlaylistGroupModel cue)
        {
            InitializeComponent(); Result = PlaylistService.Copy(cue); CueName.Text = cue.Name;
            Playback.ItemsSource = Enum.GetValues<GroupPlaybackMode>(); Playback.SelectedItem = cue.PlaybackMode;
            Advance.ItemsSource = Enum.GetValues<CueAdvanceMode>(); Advance.SelectedItem = cue.AdvanceMode;
            Seconds.Text = cue.HoldSeconds.ToString(CultureInfo.InvariantCulture); Fade.Text = cue.FadeSeconds.ToString(CultureInfo.InvariantCulture);
        }
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(CueName.Text)) throw new InvalidOperationException("Give this cue a name.");
                Result.Name = CueName.Text.Trim(); Result.PlaybackMode = (GroupPlaybackMode)Playback.SelectedItem; Result.AdvanceMode = (CueAdvanceMode)Advance.SelectedItem;
                Result.HoldSeconds = double.Parse(Seconds.Text, CultureInfo.InvariantCulture); Result.FadeSeconds = double.Parse(Fade.Text, CultureInfo.InvariantCulture);
                ArtistRules.Validate(Result); DialogResult = true;
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
    }
}
