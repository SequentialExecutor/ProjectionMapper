using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectionMapper;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Views;

internal static class DisplayModeWindowsChecks
{
    private static readonly DisplayMode Original = new(1920, 1080, 60, 32, 0);
    private static readonly DisplayMode Requested = new(1280, 720, 60, 32, 0);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Display modes: " + message); }
    private static void Fails(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected display-mode rejection."); }
    private static void Until(Func<bool> done, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > timeout) throw new TimeoutException("Display-mode UI did not complete.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(10);
        }
    }
    private static DisplayResolutionWindow PendingWindow(ModeFixture backend)
    {
        var displays = new List<DisplayDescriptor> { new(0, "projector", "fixture", 0, 0, 1920, 1080, 1920, 1080, false) };
        var dialog = new DisplayResolutionWindow(() => displays, () => { }, backend: backend);
        ((ComboBox)dialog.FindName("Modes")).SelectedItem = Requested;
        ((Button)dialog.FindName("Apply")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => ((FrameworkElement)dialog.FindName("Confirmation")).Visibility == Visibility.Visible, TimeSpan.FromSeconds(5));
        return dialog;
    }
    public static void Run(MainWindow window)
    {
        // Explicit backend fixture: mode switches, confirmation, failure, and rollback without changing CI hardware.
        var backend = new ModeFixture();
        using (var change = DisplayModeChange.Begin(backend, "projector", Requested))
        { Check(backend.Value == Requested && backend.Writes.SequenceEqual(new[] { false }), "temporary change is not persisted"); }
        Check(backend.Value == Original, "closing an unconfirmed change restores the previous mode");
        backend = new ModeFixture();
        using (var change = DisplayModeChange.Begin(backend, "projector", Requested)) { change.Keep(); }
        Check(backend.Value == Requested && backend.Writes.SequenceEqual(new[] { false, true }), "confirmed mode persists and disposal does not undo it");
        backend = new ModeFixture();
        using (var change = DisplayModeChange.Begin(backend, "projector", Requested)) { change.Revert(); change.Revert(); }
        Check(backend.Value == Original && backend.Writes.Count == 2, "rollback is idempotent");
        backend = new ModeFixture();
        Fails(() => DisplayModeChange.Begin(backend, "projector", Requested with { Width = 999 }));
        Check(backend.Writes.Count == 0 && backend.Tests == 0, "unsupported modes never reach the native change operation");
        backend = new ModeFixture { RejectTest = true };
        Fails(() => DisplayModeChange.Begin(backend, "projector", Requested));
        Check(backend.Writes.Count == 0, "driver test rejection leaves the display unchanged");
        backend = new ModeFixture { FailTemporaryOnce = true };
        Fails(() => DisplayModeChange.Begin(backend, "projector", Requested));
        Check(backend.Value == Original, "a partially applied failure rolls back");
        backend = new ModeFixture { RejectPersist = true };
        using (var change = DisplayModeChange.Begin(backend, "projector", Requested)) { Fails(change.Keep); }
        Check(backend.Value == Original, "failed keep remains eligible for rollback");

        backend = new ModeFixture();
        var pending = PendingWindow(backend);
        pending.Close();
        Until(() => backend.Value == Original, TimeSpan.FromSeconds(5));
        Check(!backend.Writes.Any(persist => persist), "closing the actual confirmation UI reverts without persistence");
        backend = new ModeFixture();
        pending = PendingWindow(backend);
        Until(() => backend.Value == Original, TimeSpan.FromSeconds(18));
        Until(() => ((FrameworkElement)pending.FindName("Confirmation")).Visibility == Visibility.Collapsed, TimeSpan.FromSeconds(2));
        pending.Close();
        Check(backend.Writes.Count == 2, "the actual 15-second UI countdown automatically restores the original mode");

        // Real native read-only enumeration validates DEVMODE layout and renderer-mode selection.
        typeof(MainWindow).GetMethod("EnumerateMonitors", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        var displays = ((IEnumerable)typeof(MainWindow).GetField("_detectedDisplays", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Cast<DisplayDescriptor>().ToList();
        Check(displays.Count > 0, "Windows capture environment exposes a display");
        var native = new WindowsDisplayModes();
        foreach (var display in displays)
        {
            var current = native.Current(display.DeviceName); var modes = native.Supported(display.DeviceName);
            Check(current.Width == display.RenderWidth && current.Height == display.RenderHeight, "renderer pixel dimensions equal the active Windows mode");
            Check(modes.Contains(current) && modes.Count == modes.Distinct().Count() && modes.All(m => m.Width > 0 && m.Height > 0), "supported list contains the current mode and no duplicates or empty dimensions");
        }
        var dialog = new DisplayResolutionWindow(() => displays, () => throw new Exception("Read-only check must not change modes."));
        dialog.Close();
        Console.WriteLine("Display mode transactions, rollback, native read-only mode enumeration and window checks passed. No Windows resolution was changed.");
    }
    private sealed class ModeFixture : IDisplayModeBackend
    {
        public DisplayMode Value = Original;
        public List<bool> Writes = new();
        public int Tests;
        public bool RejectTest, FailTemporaryOnce, RejectPersist;
        public DisplayMode Current(string device) { Check(device == "projector", "only the chosen device is queried"); return Value; }
        public IReadOnlyList<DisplayMode> Supported(string device) => new[] { Original, Requested };
        public void Test(string device, DisplayMode mode) { Tests++; if (RejectTest) throw new InvalidOperationException("Test fixture driver rejection"); }
        public void Apply(string device, DisplayMode mode, bool persist)
        {
            Check(device == "projector", "only the chosen device is changed"); Writes.Add(persist);
            if (persist && RejectPersist) throw new InvalidOperationException("Test fixture persistence rejection");
            Value = mode;
            if (FailTemporaryOnce) { FailTemporaryOnce = false; throw new InvalidOperationException("Test fixture partial failure"); }
        }
    }
}
