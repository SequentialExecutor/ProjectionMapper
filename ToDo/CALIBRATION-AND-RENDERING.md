# Calibration and rendering quality

## Using calibration

1. Open **Calibrate output…** in the main toolbar. A selected mesh enables precise
   positioning; projector identification grids also work without imported media.
2. Choose an output and enable **Show numbered grid**. The native-resolution grid
   has a display number, resolution, center lines, a border, and colored TL/TR/BL/BR
   labels. Grid spacing is editable. Video is temporarily replaced only on this
   output, and is restored when the grid is disabled or the dialog closes.
3. Select a mesh before opening the panel, then use **Assign mesh to selected
   display**. Output corner coordinates use that display's physical pixel dimensions,
   not preview dimensions or Windows logical/DPI units.
4. Select a corner and apply X/Y coordinates. Optional snapping rounds numeric edits
   to a user-selected pixel grid. Arrow buttons/keys nudge by the chosen step;
   Shift multiplies that step by ten. Nudges intentionally bypass snapping for fine
   alignment. Arrow keys keep their normal behavior in text fields and lists.
5. Lock the mapping once aligned. Locking prevents accidental source/output dragging,
   numeric edits, and output reassignment. Disabled handles reflect the locked state. It persists with project saves, recovery,
   and copied meshes. Explicit undo/redo remains available, including while locked.
   The dialog limits undo/redo to its coordinate edits; main-window history remains
   available after closing it.

The input editor continues to control source cropping; calibration edits destination
warping only. Invalid numeric edits are rejected with a message. Dragging may
briefly produce invalid geometry; the projector leaves that layer blank until the
quad becomes valid again, rather than showing a misleading unwarped rectangle.

## Rendering correctness changes

- Reject crossed, concave, non-finite, or degenerate destination quads.
- Skip fully offscreen quads and failed warps without falling back to rectangular
  content that could spill outside the intended physical surface.
- Use strict texture boundaries and correct texel-center sampling.
- Interpolate premultiplied colors during bilinear sampling to avoid colored fringes
  from transparent source pixels, then return straight-alpha BGRA for composition.
- Snapshot submitted destination arrays so later edits cannot mutate an in-flight
  frame's geometry.
- Scale destination rectangles into the combined preview even when no quad is supplied.
- Use the real composited renderer image behind the output editor; the editor's
  isolated stretched-image approximation no longer covers the true warped preview.

## Performance and GPU decision

**Refresh timing** reports the actual last software composition duration for the
selected output. It includes warp/composition work and contention for the WPF
composition lock, but excludes decoding, frame scheduling, display transport, and
projector latency. It is a single sample, not an average or guaranteed frame rate.
A 30 fps output has a 33.33 ms frame budget; decoder/output costs must also fit.

GPU composition remains a separate implementation gate, not a completed feature.
The current GPU class is still a placeholder and is not enabled by this milestone.
Benchmark representative videos, mesh counts, and resolutions on the installation
computer before choosing a backend or claiming throughput improvements. Software
composition remains the working baseline. No physical-hardware performance results
are claimed from the Linux editing environment.

## Automated verification

```text
dotnet run --project tests/Persistence.Regression/Persistence.Regression.csproj -c Release
dotnet run --project tests/OutputRouting.Regression/OutputRouting.Regression.csproj -c Release
```

The first executable checks geometry validation, output-space snapping, bounds, and
saved lock state alongside the existing persistence/recovery tests. It runs on Linux
or Windows. The second requires Windows/WPF and checks exact identity-warp sampling,
trapezoid interior/exterior pixels, transparent-edge color interpolation, invalid and
offscreen mappings, failed-warp suppression, immutable submitted geometry, native
calibration patterns, rectangle-preview scaling, and existing routing behavior.
Both are already invoked by Windows desktop CI.

## Manual acceptance still required

- Identify two real projectors and confirm grid isolation, numbers, corners, and
  native resolution, including mixed DPI/resolutions and a primary-display output.
- Toggle grids during playback and while paused, switch outputs, close the dialog,
  and confirm video resumes without leftover patterns or unused fullscreen windows.
- Align a known checkerboard to a trapezoid, compare preview and projected content,
  and inspect edges for spill, transparency artifacts, and subpixel placement.
- Verify numeric coordinates, snapping, one-pixel nudges, Shift steps, lock behavior,
  undo/redo, and save/reopen/recovery at the actual output resolution.
- Record render timing with real media at representative mesh counts and resolution;
  use extended runs to assess frame pacing and memory, not a single timing sample.

This milestone does not add curved meshes, masks, edge blending, photo detection,
or generated artwork; those remain later roadmap features.
