# Milestone 6 — Artist controls and live operation

## Surface masks and curved meshes

Select a mesh and open **Surface mask / curved mesh…**. A locked mesh must be
unlocked first. Click **Draw mask**, place 3–32 vertices around the permitted
area, and **Finish**. Drag vertices or enter normalized X/Y coordinates. Clear
mask restores the full surface. Feathering falls inward from the perimeter;
the slider is a percentage of the shorter content dimension.

Initialize a 2–8-cell grid to deform content for curved surfaces. Drag its nodes,
or select a node and set X/Y. The grid uses two triangles per cell; control points
must remain ordered and cells must stay convex. Folded, crossed, collapsed,
nonfinite or out-of-range geometry is rejected. Disable grid restores the
original content geometry. **Undo draft edit** restores preceding vertex/grid
edits. Main Undo/Redo restores applied settings.

The canvas shows the cached unwarped source with the mask/grid guides. **Preview
on outputs** shows the actual result through the existing calibrated mapping.
Cancel restores the previous settings. Apply stores a detached copy in the
project. Grid deformation happens first, then the local mask, then the existing
quad's perspective transform. Video crop, calibration corners and artwork
assignments are independent. Image sampling interpolates premultiplied colors
to prevent transparent colored fringes. Geometry sampling maps are cached between
frames; unmodified surfaces bypass this processing.

This is a two-dimensional piecewise mesh for manually aligning curved content,
not a 3D reconstruction or automatic projector calibration. The software renderer
remains the rendering path; measure frame time at installation resolution before
choosing grid density and feathering. No new frame-rate claim is made.

## Cues and performance mode

Existing named playlist groups are the cues. Select a group and open **Cue
settings…** to choose its name, sequential/simultaneous source playback, advancement
mode, timed hold and visual fade. Restart the playlist to load an edited cue list;
running cues use detached settings so editing cannot change a transition midway.

- **MediaEnd** advances when the last source finishes (all sources in simultaneous
  mode, final source in sequential mode).
- **Manual** holds the final image until Go.
- **Timed** advances after the configured hold, beginning after fade-in. Pause
  suspends the cue timer and Resume continues its remaining time.

**Go / next cue** starts the first cue if stopped, otherwise advances once. While
paused, Go resumes and advances; Play / pause resumes without advancing. At the
end the existing playlist looping setting applies. The next-cue panel shows the
next cue's name/source count and first available cached artwork/source image. It
is an unwarped content reference, not an independently running rehearsal output;
no additional decoder or AI request is started for this panel.

Transitions fade the outgoing composed image to black, switch the active source
filter, then fade in. Each fade leg uses the cue's configured 0–10 seconds. Audio
keeps the existing preferred-source routing and switches at the cue boundary;
this release does not crossfade audio. New cue visits restart source media at
zero; pause/resume retains position. Serialized commands and cue-visit checks
prevent duplicate EOF/timer/Go events from skipping a newer cue. No-active-source
cues stop with an actionable error rather than recursively skipping forever.

**Performance mode (F10)** disables the editors, editing menus/shortcuts and
calibration overlays. Space issues Go. Live Go, Play/pause, Save and Blackout
remain available. AI requests already in progress continue; new artwork cannot
be accepted or projected until editing is unlocked. Leaving performance mode
restores editing without moving mapping or restarting media.

## Blackout

**BLACKOUT / F12** immediately gates all fullscreen projector presentations to
opaque black, including retained frames, calibration patterns and AI previews.
The gate is evaluated when queued UI presentations execute, so an older frame
cannot overwrite a newly requested blackout. Overlay refreshes also obey it.
The keyboard handler operates across this application's WPF windows, including
the surface, calibration and generation windows.

Playback, cue timers and generation continue while blacked out. F12 restores the
current cue (or current calibration override when editing), rather than replaying
the frame that was visible before blackout. Blackout and performance mode are
session controls; they are not saved into projects or recovery files.

## Projector correction and edge blending

Open **Projector color / blend…**. Select an output, then adjust brightness,
gamma and RGB gains. Corrections preview live on that projector's composed image;
Cancel restores all previous settings. Apply is undoable. The combined editor
preview remains uncorrected so independent projector adjustments cannot distort
its shared composition.

Align identical content in the physical overlap first. Set opposing edge widths
as percentages of each output's width or height. Linear ramps at blend power 1
are complementary; their light attenuation uses an approximate display gamma of
2.2. Blend power allows tuning. This is manual overlap blending, with no camera
measurement or projector black-level compensation. Room conditions and actual
projector response must be checked on real hardware.

Settings record the Windows display device name and saved index. On reopen the
current index is resolved by device name; absent names receive no correction.
Windows device names are not EDID hardware identities: reconnecting/replacing
hardware can change or reuse them. Verify projector identity and correction
whenever the hardware configuration changes. Disconnected settings are retained
in the project for deliberate reuse. New output renderers receive their saved
settings when opened.

## Persistence and verification

Project format **5** adds surface appearance, cue settings and output corrections.
Versions 1–4 load with neutral artist settings. Older builds reject format 5 rather
than dropping its controls. Save, backups, recovery and mesh copy/paste retain
the new fields; mutable vertex lists are copied. Invalid artist settings cannot
replace a valid saved project. Artwork replacement and temporary previews retain
permanent mask/mesh settings.

The cross-platform regression program exercises polygon spill, inward feathering,
identity/deformed mesh pixels, invalid geometry, output correction, complementary
blend weights, detached copies, schema round trips and older-version defaults.
Windows regression checks exercise real WPF composition, output correction,
blackout/calibration precedence, content replacement, the new window markup and
cue timing/pause/EOF/Go/cancellation using actual offline still clocks. Tests do not
pretend to provide physical projector or live audio timing measurements.

Manual acceptance before performance:

1. Align a nonrectangular mask on a real surface, feather its edge, edit the grid
   on a curved target, save/reopen, and compare the physical result.
2. Run simultaneous and sequential real-media cues, pause/resume, Go, timed holds,
   restart and visual fades. Verify audio source switching on the intended device.
3. Black out during video, calibration, cue fades and AI generation; restore and
   check that no overlay, stale frame or inactive group leaks onto an output.
4. Use performance mode, verify editing stays locked and Go/Save/blackout stay
   available. Finish an AI generation while locked, then unlock and accept it.
5. Match two projector outputs and overlap ramps on physical projectors, including
   after disconnect/reconnect. Test a representative four-hour show and record
   frame time, memory and audiovisual drift.
