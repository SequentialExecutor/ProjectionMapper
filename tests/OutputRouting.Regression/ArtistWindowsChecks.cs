using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProjectionMapper.Models;
using ProjectionMapper.Rendering;
using ProjectionMapper.Services;
using ProjectionMapper.Views;

internal static class ArtistWindowsChecks
{
    private static void Check(bool value, string description) { if (!value) throw new Exception("Windows artist regression: " + description); }
    private static void Drain()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    public static void Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        byte[] white = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray();
        var source = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, white, 64); source.Freeze();
        using var software = new SoftwareRenderer(); software.InitializeAsync(16, 16).GetAwaiter().GetResult();
        using var manager = new RendererManager(software);
        manager.ResizeAsync(16, 16).GetAwaiter().GetResult();
        var mask = new SurfaceAppearance { Mask = new() { new(0, 0), new(.5, 0), new(.5, 1), new(0, 1) } };
        manager.SetSurfaceAppearance("mesh", mask);
        manager.SubmitLayerFrameForMonitor("mesh", source, new Rect(0, 0, 16, 16), null, 1, -1);
        BitmapSource? output = null; software.FrameReady += frame => output = frame;
        software.RenderFrameAsync().GetAwaiter().GetResult(); Drain();
        var bytes = new byte[16 * 16 * 4]; output!.CopyPixels(bytes, 64, 0);
        Check(bytes[(8 * 16 + 2) * 4] == 255 && bytes[(8 * 16 + 12) * 4] == 0, "real composition respects mask and opaque black background");
        manager.RemoveLayer("mesh");
        manager.SubmitLayerFrameForMonitor("mesh", source, new Rect(0, 0, 16, 16), null, 1, -1);
        software.RenderFrameAsync().GetAwaiter().GetResult(); Drain(); output!.CopyPixels(bytes, 64, 0);
        Check(bytes[(8 * 16 + 12) * 4] == 0, "content replacement preserves permanent mask settings");
        manager.SetSurfaceAppearance("mesh", new()); software.RenderFrameAsync().GetAwaiter().GetResult(); Drain(); output!.CopyPixels(bytes, 64, 0);
        Check(bytes[(8 * 16 + 12) * 4] == 255, "clearing a mask restores full content");
        using var projector = new SoftwareRenderer(); projector.InitializeAsync(16, 16).GetAwaiter().GetResult();
        var outputs = (ConcurrentDictionary<int, IRenderer>)typeof(RendererManager).GetField("_monitorRenderers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        outputs[0] = projector;
        manager.SetOutputAppearance(0, new() { MonitorIndex = 0, DisplayName = "pixel-test", Brightness = .5, Red = 0 });
        manager.SubmitLayerFrameForMonitor("mesh", source, new Rect(0, 0, 16, 16), null, 1, 0);
        BitmapSource? corrected = null; projector.FrameReady += frame => corrected = frame;
        projector.RenderFrameAsync().GetAwaiter().GetResult(); Drain(); corrected!.CopyPixels(bytes, 64, 0);
        Check(bytes[0] == 128 && bytes[1] == 128 && bytes[2] == 0, "per-output correction runs on the composed projector image");
        manager.SetCalibrationPattern(0, source);
        var presentation = typeof(RendererManager).GetMethod("ResolveMonitorPresentation", BindingFlags.NonPublic | BindingFlags.Instance)!;
        BitmapSource? Presented() => (BitmapSource?)presentation.Invoke(manager, new object?[] { 0, corrected });
        Check(ReferenceEquals(Presented(), source), "calibration overrides video before blackout");
        manager.SetBlackout(true); var black = Presented()!; var blackPixel = new byte[4]; black.CopyPixels(blackPixel, 4, 0);
        Check(blackPixel.SequenceEqual(new byte[] { 0, 0, 0, 255 }), "blackout suppresses retained calibration and queued frames");
        manager.SetBlackout(false); Check(ReferenceEquals(Presented(), source), "blackout restore returns to current calibration state");
        manager.SetPerformanceMode(true); Check(ReferenceEquals(Presented(), corrected), "performance mode hides calibration override");
        manager.SetPerformanceMode(false); manager.SetCalibrationPattern(0, null);
        CueChecks(manager, source).GetAwaiter().GetResult();
        new SurfaceControlsWindow(mask, source, _ => { }).Close();
        new CueControlsWindow(new() { Name = "Window smoke" }).Close();
        new OutputControlsWindow(new() { new(0, "window-test", "Window test display") }, new(), (_, _) => { }).Close();
        Console.WriteLine("Windows artist composition, blackout gate, cue scheduling/cancellation and window checks passed.");
    }
    private static async Task CueChecks(RendererManager manager, BitmapSource image)
    {
        using var video = new VideoService(manager);
        async Task Sources()
        {
            foreach (string id in new[] { "cue-a", "cue-b" })
            {
                video.ConfigureStillFallback(id, image, 1);
                await video.RegisterLayerAsync(new() { Id = id, SourcePath = "absent-" + Guid.NewGuid().ToString("N") + ".mp4", PreviewOnly = true });
            }
        }
        await Sources();
        using var cues = new PlaylistService(video) { EnableLooping = false, FadeRequestedAsync = manager.FadeToAsync };
        PlaylistGroupModel Cue(string id, CueAdvanceMode mode, string source) => new() { Id = id, Name = id, SourceIds = new() { source }, PlaybackMode = GroupPlaybackMode.Simultaneous, AdvanceMode = mode, HoldSeconds = 1 };
        async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Check(condition(), "cue state reached before timeout");
        }
        var manual = Cue("hold", CueAdvanceMode.Manual, "cue-a"); manual.FadeSeconds = .1;
        await cues.StartPlaylistAsync(new() { manual, Cue("next", CueAdvanceMode.Manual, "cue-b") });
        await Task.Delay(1250);
        Check(cues.CurrentGroupIndex == 0 && cues.NextGroup!.Name == "next", "manual cue holds after media end and exposes next cue");
        await Task.WhenAll(cues.AdvanceToNextGroupAsync(), cues.AdvanceToNextGroupAsync(), cues.AdvanceToNextGroupAsync());
        Check(cues.CurrentGroupIndex == 1, "duplicate Go commands cannot skip past a newer cue visit");
        await cues.StartPlaylistAsync(new() { Cue("timed", CueAdvanceMode.Timed, "cue-a"), Cue("hold", CueAdvanceMode.Manual, "cue-b") });
        await Task.Delay(200); await cues.PauseCurrentGroupAsync(); await Task.Delay(1100);
        Check(cues.CurrentGroupIndex == 0 && cues.IsPaused, "paused timed cue does not advance");
        await cues.ResumeCurrentGroupAsync(); await Until(() => cues.CurrentGroupIndex == 1);
        await cues.StartPlaylistAsync(new() { Cue("end", CueAdvanceMode.MediaEnd, "cue-a"), Cue("hold", CueAdvanceMode.Manual, "cue-b") });
        await Until(() => cues.CurrentGroupIndex == 1);
        var sequence = Cue("sequence", CueAdvanceMode.MediaEnd, "cue-a"); sequence.PlaybackMode = GroupPlaybackMode.Sequential; sequence.SourceIds.Add("cue-b");
        await cues.StartPlaylistAsync(new() { sequence, Cue("after-sequence", CueAdvanceMode.Manual, "cue-a") });
        await Task.Delay(1250); Check(cues.CurrentGroupIndex == 0, "sequential cue waits for its last source");
        await Until(() => cues.CurrentGroupIndex == 1);
        var fade = Cue("cancel-fade", CueAdvanceMode.Manual, "cue-a"); fade.FadeSeconds = 1;
        var started = cues.StartPlaylistAsync(new() { fade }); await Task.Delay(75); await cues.StopPlaylistAsync(); await started;
        await Task.Delay(1100); Check(cues.CurrentGroupIndex == -1 && !cues.IsPlaying && cues.NextGroup?.Name == "cancel-fade", "stop cancels a fade without resurrecting the cue");
    }
}
