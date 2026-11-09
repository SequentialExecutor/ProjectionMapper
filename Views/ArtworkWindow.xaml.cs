using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;
using ComboBox = System.Windows.Controls.ComboBox;

namespace ProjectionMapper.Views
{
    public sealed record ArtworkProviderOption(ArtworkProvider Provider, string Name);
    public sealed record ArtworkSurface(string Id, string Name, double Aspect);
    /// <summary>Modeless generation/library window. No secret is part of a project or asset.</summary>
    public partial class ArtworkWindow : Window
    {
        private readonly Func<List<ArtworkSurface>> _surfaces;
        private readonly Func<List<ArtworkAsset>> _library;
        private readonly Func<ArtworkAsset, List<ArtworkCell>, double, Task> _accept;
        private readonly Func<ArtworkAsset?, List<ArtworkCell>, Task> _preview;
        private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        private readonly Dictionary<ArtworkProvider, string> _sessionTokens = new();
        private readonly List<ArtworkReference> _references = new();
        private readonly List<ArtworkAsset> _drafts = new();
        private ArtworkProvider? _previousProvider;
        private CancellationTokenSource? _cancellation;
        private bool _ready, _busy, _closeAfterCancel, _editingLocked;
        private TaskCompletionSource<bool>? _idle;
        private ArtworkAsset? Selected => Images.SelectedItem as ArtworkAsset;
        private ArtworkProvider SelectedProvider => Provider.SelectedItem is ArtworkProviderOption option ? option.Provider : ArtworkProvider.OpenAI;
        private static string Value(ComboBox control) => ((ComboBoxItem)control.SelectedItem).Content.ToString()!;

        public ArtworkWindow(Func<List<ArtworkSurface>> surfaces, Func<List<ArtworkAsset>> library,
            Func<ArtworkAsset, List<ArtworkCell>, double, Task> accept, Func<ArtworkAsset?, List<ArtworkCell>, Task> preview,
            string folder, string style, string palette, string? selectedId)
        {
            InitializeComponent(); _surfaces = surfaces; _library = library; _accept = accept; _preview = preview;
            Folder.Text = folder; ArtStyle.Text = style; Palette.Text = palette;
            _ready = true; Provider.DisplayMemberPath = "Name";
            Provider.ItemsSource = new[] { new ArtworkProviderOption(ArtworkProvider.OpenAI, "OpenAI"), new ArtworkProviderOption(ArtworkProvider.GoogleGemini, "Google Gemini"), new ArtworkProviderOption(ArtworkProvider.StabilityAI, "Stability AI") };
            Provider.SelectedIndex = 0;
            RefreshSurfaces(selectedId); RefreshImages();
            Closing += (_, e) =>
            {
                if (!_busy) return;
                _closeAfterCancel = true; _cancellation?.Cancel(); e.Cancel = true;
            };
            Closed += async (_, _) =>
            {
                try { await _preview(null, new()); } catch (Exception) { Debug.WriteLine("Artwork preview was already cleared after a project change."); }
                Token.Password = string.Empty; _sessionTokens.Clear(); _http.Dispose();
            };
        }
        public void SetEditingLocked(bool locked)
        {
            _editingLocked = locked;
            Apply.IsEnabled = Keep.IsEnabled = Preview.IsEnabled = !_busy && !locked;
        }
        private void Busy(bool busy)
        {
            _busy = busy;
            if (busy) _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            else _idle?.TrySetResult(true);
            RequestFields.IsEnabled = !busy; CancelRequest.IsEnabled = busy && _cancellation != null;
            Apply.IsEnabled = Keep.IsEnabled = Preview.IsEnabled = !busy && !_editingLocked;
        }
        public async Task StopAndCloseAsync()
        {
            _closeAfterCancel = true;
            _cancellation?.Cancel();
            if (_busy && _idle != null) await _idle.Task;
            if (IsVisible) Close();
        }
        private void RefreshSurfaces(string? selected = null)
        {
            try
            {
                var ids = Surfaces.SelectedItems.Cast<ArtworkSurface>().Select(s => s.Id).ToHashSet();
                if (selected != null) ids.Add(selected);
                var items = _surfaces(); Surfaces.ItemsSource = items;
                foreach (var surface in items.Where(s => ids.Contains(s.Id))) Surfaces.SelectedItems.Add(surface);
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private List<ArtworkCell> Bindings(ArtworkAsset asset)
        {
            var selected = Surfaces.Items.Cast<ArtworkSurface>().Where(s => Surfaces.SelectedItems.Contains(s)).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("Select mapped surfaces first.");
            if (asset.Layout.Count == 1) return selected.Select(s => new ArtworkCell(s.Id, s.Name, asset.Layout[0].Region)).ToList();
            if (selected.Count != asset.Layout.Count) throw new InvalidOperationException($"Select {asset.Layout.Count} surfaces for this coordinated composition, or choose a single-surface image.");
            // Match original IDs when possible; otherwise apply cells in the displayed surface order.
            return selected.Select((surface, i) => new ArtworkCell(surface.Id, surface.Name,
                asset.Layout.FirstOrDefault(cell => cell.MeshId == surface.Id)?.Region ?? asset.Layout[i].Region)).ToList();
        }
        private void RefreshImages(string? selectId = null)
        {
            try
            {
                var images = _library().Concat(_drafts).GroupBy(a => a.Id).Select(g => g.First()).ToList();
                Images.ItemsSource = images; Images.SelectedItem = images.FirstOrDefault(a => a.Id == selectId) ?? images.LastOrDefault();
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void Provider_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (_previousProvider.HasValue) _sessionTokens[_previousProvider.Value] = Token.Password;
            var provider = SelectedProvider; _previousProvider = provider;
            Token.Password = _sessionTokens.GetValueOrDefault(provider, string.Empty); Remember.IsChecked = false;
            try
            {
                if (!_sessionTokens.ContainsKey(provider))
                {
                    var saved = ProviderTokenStore.Load(provider);
                    if (saved != null) { Token.Password = saved; Remember.IsChecked = true; }
                }
            }
            catch (Exception) { Status.Text = "Stored token unavailable. You can enter a session-only API token."; }
            Model.Text = ArtworkGenerationService.DefaultModel(provider);
            Model.IsReadOnly = provider == ArtworkProvider.StabilityAI;
            Quality.IsEnabled = provider == ArtworkProvider.OpenAI;
            TransparentOption.IsEnabled = provider == ArtworkProvider.OpenAI;
            if (provider != ArtworkProvider.OpenAI) ImageBackground.SelectedIndex = 0;
            ReferenceButton.IsEnabled = provider != ArtworkProvider.StabilityAI;
            Capabilities.Text = provider switch
            {
                ArtworkProvider.OpenAI => "OpenAI API key · reference images, layout guide, quality and transparency controls.",
                ArtworkProvider.GoogleGemini => "Google AI Studio API key · reference images and layout guide; image resolution/quality is model-dependent.",
                _ => "Stability API key · Stable Image Core text, style and palette guidance. Uploaded references require OpenAI or Gemini. Layout instructions are text-only."
            };
            ReferenceNames.Text = provider == ArtworkProvider.StabilityAI && _references.Count > 0
                ? "Reference images are not sent to Stability Core." : string.Join(", ", _references.Select(r => r.FileName));
            Consent.IsChecked = false;
        }
        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (Consent.IsChecked != true) { Status.Text = "Review the selected provider, upload and billing notice first."; return; }
            if (_drafts.Count >= 8) { Status.Text = "Keep useful variants or discard unaccepted variants before generating more."; return; }
            var selected = Surfaces.Items.Cast<ArtworkSurface>().Where(s => Surfaces.SelectedItems.Contains(s)).ToList();
            if (selected.Count < 1 || selected.Count > 8) { Status.Text = "Select one to eight mapped surfaces."; return; }
            _cancellation = new CancellationTokenSource(); Busy(true);
            try
            {
                string size = Value(ImageSize); var dimensions = size.Split('x');
                var cells = ArtworkRules.Layout(selected.Select(s => (s.Id, s.Name, s.Aspect)).ToList(), int.Parse(dimensions[0]), int.Parse(dimensions[1]));
                string folder = Path.GetFullPath(Folder.Text);
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(folder);
                    string probe = Path.Combine(folder, ".artwork-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try { using var writable = File.Create(probe); }
                    finally { if (File.Exists(probe)) File.Delete(probe); }
                }).WaitAsync(TimeSpan.FromSeconds(30), _cancellation.Token);
                var request = new ArtworkRequest(SelectedProvider, Model.Text.Trim(), Prompt.Text, ArtStyle.Text, Palette.Text, size,
                    Value(Quality), Value(ImageBackground), Math.Min(int.Parse(Value(Variants), CultureInfo.InvariantCulture), 8 - _drafts.Count), cells, string.Empty,
                    SelectedProvider == ArtworkProvider.StabilityAI ? new() : _references.ToList());
                string key = Token.Password.Trim();
                if (string.IsNullOrWhiteSpace(key) || key.Length > 8192 || key.Any(c => c < 33 || c > 126)) throw new ArgumentException("Enter the selected provider's API access token.");
                request = request with { GuidePngBase64 = await ArtworkImaging.OnStaAsync(() => ArtworkImaging.Guide(request)) };
                if (Remember.IsChecked == true) ProviderTokenStore.Save(request.Provider, key);
                Status.Text = $"Generating {request.Variants} variant(s) with {request.Provider}. Main editing and playback remain available.";
                var batch = await Task.Run(() => new ArtworkGenerationService(_http).GenerateAsync(request, key, _cancellation.Token));
                int completed = 0; var errors = new List<string>();
                foreach (var image in batch.Images)
                {
                    try
                    {
                        var asset = await ArtworkImaging.OnStaAsync(() => ArtworkImaging.Normalize(image, request));
                        _drafts.Add(asset);
                        var path = await ArtworkImaging.DownloadAsync(asset, folder);
                        completed++; Status.Text = $"Downloaded {Path.GetFileName(path)}";
                    }
                    catch (Exception ex) { errors.Add($"One result could not be downloaded: {ex.Message} Any decoded variant remains available; choose another folder and download it without another AI call."); }
                }
                RefreshImages(_drafts.LastOrDefault()?.Id);
                Status.Text = $"Downloaded {completed} image(s) to {folder}. Compare variants, preview mapping, then keep or apply your choice. " + batch.Notice + " " + string.Join(" ", errors.Distinct());
            }
            catch (OperationCanceledException) { Status.Text = "Generation canceled or timed out. The provider may still charge for submitted work; no retry was made."; }
            catch (Exception ex) { Status.Text = $"Generation failed: {ex.Message} Existing artwork is unchanged."; }
            finally { _cancellation.Dispose(); _cancellation = null; Busy(false); if (_closeAfterCancel) Close(); }
        }
        private async void AddReference_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _references.Count >= 2) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Reference images|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog(this) != true) return;
            Busy(true);
            try
            {
                var photo = await ScenePhotoService.ImportAsync(dialog.FileName);
                _references.Add(new(photo.OriginalFileName, "image/jpeg", photo.ImageBase64, photo.ImageSha256));
                ReferenceNames.Text = string.Join(", ", _references.Select(r => r.FileName));
            }
            catch (Exception ex) { Status.Text = ex.Message; }
            finally { Busy(false); if (_closeAfterCancel) Close(); }
        }
        private async void Image_Changed(object sender, SelectionChangedEventArgs e)
        {
            var asset = Selected;
            if (asset == null) { ArtworkPreview.Source = null; return; }
            try
            {
                var frame = await ArtworkImaging.OnStaAsync(() => ArtworkImaging.Decode(asset.ImageBase64));
                if (Selected?.Id != asset.Id) return;
                ArtworkPreview.Source = frame;
                Metadata.Text = $"{asset.Provider} · {asset.Model} · {asset.Width}×{asset.Height} · {asset.CreatedAt.ToLocalTime():g}\n{(asset.Prompt.Length > 220 ? asset.Prompt[..220] + "…" : asset.Prompt)}";
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private double Duration()
        {
            if (!double.TryParse(HoldSeconds.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || !double.IsFinite(value) || value < 1 || value > 86400)
                throw new ArgumentException("Offline still duration must be between 1 and 86400 seconds.");
            return value;
        }
        private async Task SaveChoice(bool assign)
        {
            if (_busy || _editingLocked || Selected == null) return;
            var asset = Selected; Busy(true);
            try
            {
                var bindings = assign ? Bindings(asset) : new List<ArtworkCell>();
                await ArtworkImaging.DownloadAsync(asset, Folder.Text);
                await _accept(asset, bindings, Duration());
                await _preview(null, new());
                _drafts.RemoveAll(a => a.Id == asset.Id); RefreshImages(asset.Id);
                Status.Text = assign ? "Artwork applied to the current mappings. Save the project for portable offline playback." : "Artwork kept in the project library. Save the project to retain it.";
            }
            catch (Exception ex) { Status.Text = ex.Message; }
            finally { Busy(false); if (_closeAfterCancel) Close(); }
        }
        private async void Apply_Click(object sender, RoutedEventArgs e) => await SaveChoice(true);
        private async void Keep_Click(object sender, RoutedEventArgs e) => await SaveChoice(false);
        private async void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _editingLocked || Selected == null) return;
            Busy(true);
            try { await _preview(Selected, Bindings(Selected)); Status.Text = "Temporary artwork preview on assigned outputs. Restore preview or close this window to return to saved content."; }
            catch (Exception ex) { Status.Text = ex.Message; }
            finally { Busy(false); if (_closeAfterCancel) Close(); }
        }
        private async void RestorePreview_Click(object sender, RoutedEventArgs e)
        {
            try { await _preview(null, new()); } catch (Exception ex) { Status.Text = ex.Message; }
        }
        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null) return;
            try { Status.Text = "Downloaded: " + await ArtworkImaging.DownloadAsync(Selected, Folder.Text); }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void UseSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || Selected == null) return;
            var asset = Selected;
            Provider.SelectedItem = Provider.Items.Cast<ArtworkProviderOption>().First(p => p.Provider == asset.Provider);
            Model.Text = asset.Model; Prompt.Text = asset.Prompt; ArtStyle.Text = asset.Style; Palette.Text = asset.Palette;
            void Select(ComboBox control, string value)
            {
                var option = control.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == value);
                if (option != null) control.SelectedItem = option;
            }
            Select(ImageSize, asset.RequestedSize); Select(Quality, asset.Quality); Select(ImageBackground, asset.Background);
            _references.Clear(); _references.AddRange(asset.References);
            ReferenceNames.Text = string.Join(", ", _references.Select(r => r.FileName));
            Surfaces.SelectedItems.Clear();
            foreach (ArtworkSurface surface in Surfaces.Items)
                if (asset.Layout.Any(cell => cell.MeshId == surface.Id)) Surfaces.SelectedItems.Add(surface);
            Consent.IsChecked = false;
            Status.Text = "Generation settings restored. Select surfaces and generate explicitly to create another variant.";
        }
        private async void UseReference_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || Selected == null) return;
            if (SelectedProvider == ArtworkProvider.StabilityAI) { Status.Text = "Use OpenAI or Gemini for uploaded image references."; return; }
            if (_references.Count >= 2) { Status.Text = "Clear a reference before adding another."; return; }
            var asset = Selected; Busy(true);
            try
            {
                _references.Add(await ArtworkImaging.OnStaAsync(() => ArtworkImaging.Reference(asset)));
                ReferenceNames.Text = string.Join(", ", _references.Select(r => r.FileName));
            }
            catch (Exception ex) { Status.Text = ex.Message; }
            finally { Busy(false); if (_closeAfterCancel) Close(); }
        }
        private void ForgetToken_Click(object sender, RoutedEventArgs e)
        {
            try { ProviderTokenStore.Forget(SelectedProvider); _sessionTokens.Remove(SelectedProvider); Token.Password = string.Empty; Remember.IsChecked = false; Status.Text = "Stored provider token removed."; }
            catch (Exception ex) { Status.Text = ex.Message; }
        }
        private void Folder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Artwork download folder" };
            if (dialog.ShowDialog(this) == true) Folder.Text = dialog.FolderName;
        }
        private void ClearReferences_Click(object sender, RoutedEventArgs e) { _references.Clear(); ReferenceNames.Text = string.Empty; }
        private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
        private void Discard_Click(object sender, RoutedEventArgs e) { if (!_busy) { _drafts.Clear(); RefreshImages(); } }
        private void RefreshSurfaces_Click(object sender, RoutedEventArgs e) => RefreshSurfaces();
    }
}
