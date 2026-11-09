using System;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProjectionMapper;
using ProjectionMapper.Models;
using ProjectionMapper.Views;

internal static class OutputVerificationWindowsChecks
{
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("Output verification: " + message); }
    private static void Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { if (clock.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Output status did not refresh."); Drain(); Thread.Sleep(10); }
    }
    public static void Run(MainWindow main)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { RunOnDispatcher(main); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunOnDispatcher(MainWindow main)
    {
        // Explicit color fixtures exercise the actual live WPF binding, without starting a decoder or projecting.
        var first = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4); first.Freeze();
        var second = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4); second.Freeze();
        var source = new RenderHostControl(); source.SetFrame(first);
        var preview = new ProjectionPreviewWindow(source);
        var image = (Image)preview.FindName("PreviewImage"); Drain();
        Check(ReferenceEquals(image.Source, first), "preview initially observes the existing composed frame");
        source.SetFrame(second); Drain();
        Check(ReferenceEquals(image.Source, second), "preview updates as actual host frames change");
        // Exercise the real F5 event path and its binding cleanup.
        using var input = new HwndSourceFixture();
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, input.Source, Environment.TickCount, Key.F5) { RoutedEvent = Keyboard.KeyDownEvent };
        preview.RaiseEvent(key);
        Check(key.Handled && BindingOperations.GetBindingExpression(image, Image.SourceProperty) == null && ReferenceEquals(source.CurrentFrame, second), "F5 closes preview and detaches it without clearing the editor frame");
        var capture = typeof(MainWindow).GetMethod("CaptureOutputDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var report = (OutputDiagnosticReport)capture.Invoke(main, null)!;
        int captures = 0;
        var window = new OutputDiagnosticsWindow(() => { captures++; return (OutputDiagnosticReport)capture.Invoke(main, null)!; });
        Check(report.Surfaces.Count == 0, "real empty editor reports no invented source routes");
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Until(() => captures >= 2);
        window.Close(); Drain();
        int afterClose = captures;
        Thread.Sleep(1100); Drain();
        Check(captures == afterClose, "closing output status cancels further automatic snapshot captures");
        source.Dispose();
        Console.WriteLine("Windows composed-preview binding, close cleanup and actual output-status capture checks passed.");
    }
    private sealed class HwndSourceFixture : IDisposable
    {
        public System.Windows.Interop.HwndSource Source { get; } = new(new System.Windows.Interop.HwndSourceParameters("preview-key-fixture") { Width = 1, Height = 1 });
        public void Dispose() => Source.Dispose();
    }
}
