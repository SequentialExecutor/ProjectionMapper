using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SystemColors = System.Windows.SystemColors;

namespace ProjectionMapper.Services
{
    public enum UiAppearance { Dark, Light, System }
    /// <summary>Application chrome only. Palettes never enter the renderer, saved projects, or calibration images.</summary>
    public static class UiThemeService
    {
        private static Application? _application;
        private static bool _subscribed;
        public static bool IsInitialized => _application != null;
        public static UiAppearance Appearance { get; private set; } = UiAppearance.Dark;
        public static bool IsDark { get; private set; } = true;
        public static event Action? Changed;
        private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectionMapper", "appearance.json");
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void Initialize(Application application, bool loadPreference = true)
        {
            _application = application;
            if (!application.Resources.MergedDictionaries.Any(d => d.Source?.OriginalString.Contains("Themes/Controls.xaml", StringComparison.Ordinal) == true))
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ProjectionMapper;component/Themes/Controls.xaml", UriKind.Relative) });
            if (loadPreference)
                try { if (File.Exists(PreferencePath) && Enum.TryParse<UiAppearance>(JsonSerializer.Deserialize<string>(File.ReadAllText(PreferencePath)), out var selected) && Enum.IsDefined(selected)) Appearance = selected; }
                catch (Exception ex) { Debug.WriteLine($"Appearance preference unavailable: {ex.Message}"); }
            if (!_subscribed)
            {
                SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
                SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
                application.Exit += (_, _) => { SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged; SystemEvents.UserPreferenceChanged -= UserPreferenceChanged; _subscribed = false; };
                _subscribed = true;
            }
            ApplyPalette();
        }
        public static void SetAppearance(UiAppearance appearance, bool persist = true)
        {
            if (!Enum.IsDefined(appearance)) throw new ArgumentOutOfRangeException(nameof(appearance));
            Appearance = appearance; ApplyPalette();
            if (persist)
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
                    string temporary = PreferencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllText(temporary, JsonSerializer.Serialize(appearance.ToString())); File.Move(temporary, PreferencePath, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                catch (Exception ex) { Debug.WriteLine($"Could not save appearance preference: {ex.Message}"); }
        }
        private static void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
        { if (e.PropertyName == nameof(SystemParameters.HighContrast)) RefreshOnDispatcher(); }
        private static void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => RefreshOnDispatcher();
        private static void RefreshOnDispatcher()
        { var dispatcher = _application?.Dispatcher; if (dispatcher != null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(ApplyPalette); }
        private static bool WindowsUsesDark()
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("AppsUseLightTheme") is int value && value == 0; }
            catch (Exception ex) { Debug.WriteLine($"System appearance unavailable: {ex.Message}"); return true; }
        }
        private static void ApplyPalette()
        {
            if (_application == null) return;
            _application.Dispatcher.VerifyAccess();
            IsDark = Appearance == UiAppearance.Dark || Appearance == UiAppearance.System && WindowsUsesDark();
            string[] names = { "Background", "Surface", "Raised", "Input", "Button", "Border", "Text", "Muted", "Accent", "AccentText", "OnAccent", "Hover", "Selection", "Danger", "Warning", "Canvas", "SelectionText", "OnDanger" };
            string[] dark = { "#10151D", "#171F2B", "#202B3A", "#111923", "#263244", "#3C4B61", "#EDF3FA", "#B2BFD1", "#6BDACA", "#8CEBDD", "#07332D", "#2A394D", "#254B4B", "#AC303D", "#FFCF7A", "#151A22", "#EDF3FA", "#FFFFFF" };
            string[] light = { "#EDF1F6", "#FFFFFF", "#FFFFFF", "#F7F9FC", "#F0F3F8", "#B5C1D0", "#182436", "#53627A", "#087F72", "#006B60", "#FFFFFF", "#E6ECF3", "#D8EFEB", "#AE2837", "#8A5000", "#151A22", "#182436", "#FFFFFF" };
            for (int i = 0; i < names.Length; i++)
            {
                var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString((IsDark ? dark : light)[i]);
                if (SystemParameters.HighContrast)
                    color = names[i] switch
                    {
                        "Text" or "Muted" => SystemColors.WindowTextColor,
                        "AccentText" => SystemColors.HotTrackColor,
                        "Accent" or "Selection" => SystemColors.HighlightColor,
                        "OnAccent" or "OnDanger" or "SelectionText" => SystemColors.HighlightTextColor,
                        "Border" => SystemColors.WindowTextColor,
                        "Danger" => SystemColors.HighlightColor,
                        "Warning" => SystemColors.WindowTextColor,
                        "Canvas" => Colors.Black,
                        _ => SystemColors.WindowColor
                    };
                var brush = new SolidColorBrush(color); brush.Freeze(); _application.Resources["Ui." + names[i]] = brush;
            }
            // Defaults used by framework controls which retain their standard interaction templates.
            _application.Resources[SystemColors.HighlightBrushKey] = _application.Resources["Ui.Selection"];
            _application.Resources[SystemColors.HighlightTextBrushKey] = _application.Resources["Ui.SelectionText"];
            _application.Resources[SystemColors.ControlBrushKey] = _application.Resources["Ui.Surface"];
            _application.Resources[SystemColors.ControlTextBrushKey] = _application.Resources["Ui.Text"];
            foreach (Window window in _application.Windows) ApplyTitleBar(window);
            Changed?.Invoke();
        }
        public static readonly DependencyProperty UseThemedCaptionProperty = DependencyProperty.RegisterAttached("UseThemedCaption", typeof(bool), typeof(UiThemeService), new PropertyMetadata(false, CaptionChanged));
        public static bool GetUseThemedCaption(DependencyObject value) => (bool)value.GetValue(UseThemedCaptionProperty);
        public static void SetUseThemedCaption(DependencyObject value, bool enabled) => value.SetValue(UseThemedCaptionProperty, enabled);
        private static void CaptionChanged(DependencyObject value, DependencyPropertyChangedEventArgs e)
        { if (value is Window window && e.NewValue is true) { window.SourceInitialized += (_, _) => ApplyTitleBar(window); ApplyTitleBar(window); } }
        public static void ApplyTitleBar(Window window)
        {
            try { var handle = new WindowInteropHelper(window).Handle; if (handle == IntPtr.Zero || window.WindowStyle == WindowStyle.None) return; int dark = IsDark && !SystemParameters.HighContrast ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); }
            catch (Exception ex) { Debug.WriteLine($"Title bar appearance unavailable: {ex.Message}"); }
        }
    }
}
