using System;
using System.Collections.Generic;
using System.Linq;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>Explains observed black-output conditions without changing playback, assignments, or hardware.</summary>
    public static class OutputDiagnosticService
    {
        public static OutputDiagnosticReport Analyze(OutputDiagnosticReport report)
        {
            var displays = report.Displays.ToList();
            foreach (int missing in report.Surfaces.Where(s => s.TargetDisplay >= 0).Select(s => s.TargetDisplay).Distinct().Where(i => displays.All(d => d.Index != i)))
                displays.Add(new(missing, string.Empty, false, 0, 0, false, 0, 0, 0, null, 0));
            report.Displays = displays.OrderBy(d => d.Index).Select(display =>
            {
                var surfaces = report.Surfaces.Where(s => s.TargetDisplay == display.Index).ToList();
                var active = surfaces.Where(s => s.ActiveInCue).ToList();
                var visible = active.Where(s => s.Opacity > 0).ToList();
                var findings = new List<string>();
                string state = "Frames available";
                void Add(string title, string action) { if (findings.Count == 0) state = title; findings.Add(action); }
                if (!display.Connected) Add("Display unavailable", "Reconnect this display or review Show setup > Assign projector outputs.");
                if (report.OutputReviewRequired) Add("Output review required", "Review Show setup > Assign projector outputs, then use F12 to restore output.");
                if (report.Blackout) Add("Blackout active", report.OutputReviewRequired ? "Blackout remains enabled until output assignments are reviewed." : "Use F12 to restore the current output. Playback continues during blackout.");
                if (surfaces.Count == 0) Add("No assigned surfaces", "Select a surface and choose this projector in the inspector.");
                else
                {
                    if (active.Count == 0) Add("No active surfaces in this cue", "Start the intended cue with Go, or check its source assignments. Inactive sources remain hidden.");
                    else if (visible.Count == 0) Add("Surfaces are transparent", "Increase opacity for at least one active surface in the inspector.");
                    else if (!visible.Any(s => s.HasCachedContent)) Add("Waiting for content", "Check the source media and FFmpeg in Preflight & rehearsal, then play the source. Accepted artwork can supply cached content.");
                    if (!display.WindowOpen) Add("Output window closed", "Review output assignments, or re-select the projector for an assigned surface to reopen its output window.");
                    else if (display.RenderWidth <= 0 || display.RenderHeight <= 0) Add("Renderer unavailable", "The output window has no initialized renderer. Reopen the output and check the application logs.");
                    else if (display.RenderWidth != display.WindowsWidth || display.RenderHeight != display.WindowsHeight)
                        Add("Resolution mismatch", "The renderer size differs from the detected Windows mode. Review assignments to rebuild output at the current resolution.");
                    if (display.WindowOpen && display.LastFrameUtc == null) Add("No rendered frame observed", "Wait for initialization, then check media and output setup if no frame arrives.");
                    else if (display.WindowOpen && display.LastFrameUtc is { } last && report.CapturedUtc - last > TimeSpan.FromSeconds(5))
                        Add("No recent rendered frame", "No composed frame event was observed in the last five seconds. Check playback and run preflight; this does not measure a physical signal.");
                }
                double age = display.LastFrameUtc is { } time ? Math.Max(0, (report.CapturedUtc - time).TotalSeconds) : 0;
                string mode = display.Connected ? $"Windows {display.WindowsWidth} × {display.WindowsHeight}" : "Disconnected";
                string render = display.RenderWidth > 0 && display.RenderHeight > 0 ? $"Renderer {display.RenderWidth} × {display.RenderHeight}" : "Renderer not allocated";
                string metrics = $"{mode} · {render}\n{display.RenderedFrames:N0} rendered frame events this session · " +
                    (display.LastFrameUtc == null ? "No frame timestamp · Composition not observed" : $"Last frame {age:0.0}s ago · Last composition {display.RenderMilliseconds:0.0} ms");
                string content = surfaces.Count == 0 ? "No surface routes." : string.Join("\n", surfaces.Select(s =>
                    $"{s.Name} ← {s.SourceName}: {(s.ActiveInCue ? "active" : "inactive cue")}, {(s.HasCachedContent ? "cached content available" : "no cached content")}, opacity {s.Opacity:P0}"));
                return display with
                {
                    Title = $"Display {display.Index + 1}" + (display.DeviceName.Length == 0 ? " · unavailable" : $" · {display.DeviceName}"),
                    State = state, Metrics = metrics, Findings = findings,
                    Details = content + "\n\n" + (findings.Count == 0 ? "Software frames are available. Verify the projector input, cable and physical alignment if the surface remains dark." : string.Join("\n\n", findings))
                };
            }).ToList();
            int preview = report.Surfaces.Count(s => s.TargetDisplay < 0);
            report.Summary = $"{(report.OutputReviewRequired ? "Output review required" : "No output review pending")} · Blackout {(report.Blackout ? "on" : "off")} · Playback {(report.Playing ? "running" : "paused/stopped")}\n" +
                $"{report.Surfaces.Count} surfaces · {preview} preview-only · " + (report.PlaylistMode ? $"Cue: {(report.CurrentCue.Length > 0 ? report.CurrentCue : "none active")}" : "Independent source playback");
            return report;
        }
    }
}
