# Install, transfer and rehearse ProjectionMapper

## Application installation

The Windows CI workflow builds and tests the application, then publishes the
**ProjectionMapper-win-x64** artifact. Download it from a successful `.NET Desktop
CI` run, extract the whole application folder, and run `ProjectionMapper.exe`.
This artifact is self-contained: its .NET runtime is included. Do not copy only
the EXE. Keep `appsettings.json` with it. This is an unsigned application build;
code signing and a signed installer are separate release work.

For a framework-dependent developer build, install the .NET **9 SDK** to build,
or the matching x64 .NET **9 Desktop Runtime** to run it. Use an x64 Windows
version supported by that runtime; the project targets Windows build 19041 or
later. GPU acceleration is not implemented: the software renderer is the current
baseline. Verify performance on your actual output resolutions and hardware.

Video/audio playback requires matching Windows **ffmpeg.exe** and **ffprobe.exe**.
Obtain a trusted build and comply with its license. Place both in
`Native/ffmpeg/` under the application folder, put both on PATH, or set
`FFmpeg.Path` in `appsettings.json` to the executable or its containing directory.
The decoder and preflight share this resolution. Preflight runs both tools and
can inspect local video metadata. It does not download executables, contact an
AI provider, or prove that every frame decodes correctly.

An artwork-only show whose original videos are absent can use embedded still
playback without FFmpeg. A mixed show, or one retaining original video audio,
still needs its media and decoder tools. Credentials are required only for new
AI requests, never for accepted artwork playback.

## Collect and transfer a show

1. Save your working project and confirm its media is readable. Open
   **Collect / preflight / rehearsal…** and run preflight. The window is modeless;
   playback can continue while file work runs in the background.
2. Choose **Collect Project…** and select a parent folder. A uniquely named new
   collection is created there. Existing folders are never replaced.
3. The collector snapshots the project, copies imported and legacy-layer media,
   deduplicates identical content with the same extension, and saves
   `show.pmproj` using only contained relative media paths. Embedded photos,
   generated artwork/references, calibration, masks, grids, cues and output
   corrections remain inside the project. API keys and machine configuration
   files are not collected.
4. `collection.json` records sizes and SHA-256 hashes for the project and each
   collected media file. Files are verified before a staging directory is renamed
   to the final folder. Cancel, copy failures, changed sources and insufficient
   storage do not publish a partial show. Cleanup failures are logged; an orphan
   `.projection-collect-*` directory is incomplete and can be removed after
   confirming no collection is running.
5. Copy the **entire collection folder** to the destination. The application,
   decoder tools, AI credentials and external artwork download folder are separate
   from the show. The accepted project artwork is already embedded.
6. Open `show.pmproj`. The app verifies the collection before replacing the current
   editor state. Altered/missing files, escaping paths and links/junctions are
   rejected. Checksums detect changes, not authenticity: verify the source of any
   project you receive.

If an original is missing but every mesh under its source has accepted artwork,
collection stores a contained missing-original placeholder. That source uses
embedded artwork offline; its original audio is unavailable. Missing required
video stops collection. HLS/DASH/concat/network playlists must be flattened to
self-contained local media first; sidecar/remote dependencies are not bundled.
Media metadata inspection restricts ffprobe to local file/pipe protocols.

Collections are immutable transfer snapshots. **Save** on an opened collection
uses **Save As** outside that folder, preserving its verified copy. Edit a working
copy, then collect again. Do not edit `show.pmproj` or media in place and expect
its old manifest to remain valid. Reports should be exported outside the verified
collection. The original open project is not switched to the collected copy.

## Destination outputs and preflight

Project format **7** records the computer fingerprint and display device/interface
identities, order, positions, dimensions and render sizes in the same order as
the application's output picker. Versions 1–6 still load. Older builds reject
format 7 rather than discarding routing information.

When the destination differs, **Output setup** requires a deliberate destination
for each saved output, or **Preview only**. No new projector windows open until
that review is complete. Color/blending is discarded by default on reassignment;
copy it only when you intend to verify it on the chosen outputs. This does not
automatically recalibrate geometry. Device identities can be unavailable or
reused by display drivers; verify physical projector identity and alignment.
Unknown identities require review again on the next load.

Display-change events block projector windows and enable blackout until output
setup is reviewed. F12 cannot restore unreviewed output. After reassignment,
verify alignment and explicitly restore output with F12. Display notification
and UI scheduling are software mechanisms, not a hardware emergency interlock.

Run **preflight** on the destination. It checks project compatibility, readable
media, artwork-covered missing originals, cue references, output availability and
layout, free-space observation, FFmpeg/ffprobe execution, optional media metadata,
and collection integrity. Entering **Performance mode** runs fresh checks and
blocks on errors; warnings still require operator judgement. Unchecking Performance mode cancels its pending probes. Edits or display
changes during that check require a new check. Manual Go/play remains available
for editing/rehearsal; performance readiness is checked at performance-mode entry.

## Rehearsal and offline acceptance

**Start rehearsal recording** takes a fresh preflight and records observations
while you operate the actual show. A failed preflight is retained in the report;
recording is allowed for diagnosis and does not imply approval to perform.

The worker sampler records actual process private memory/working set, sampled CPU
utilization, blackout state, output renderer frame-event counters and sampled
render times. Cue/error/display-change events are recorded. Counters describe
rendered frames delivered through the software pipeline, not measured projector
photons, dropped frames or audiovisual synchronization. No physical or four-hour
pass is inferred from a timer. CPU utilization is normalized across logical CPUs.

Stop recording to view/export JSON and add operator notes. Completed reports are
also saved under `%LOCALAPPDATA%/ProjectionMapper/Rehearsals/`. Closing this
preparation window alone leaves recording running. Switching projects or normal
application shutdown stops and saves it. Abrupt process/power failure can lose
an in-memory recording. Sampling defaults to five seconds, retaining the most
recent 7200 samples and up to 1000 events; whole-session duration/sample count and
memory peaks remain in the summary. Configure limits in `Deployment` settings.

Before calling an installation release-ready, perform and record these checks on
real hardware:

- Transfer to a second supported Windows machine. Remove access to the original
  media/download folders; verify the full collected folder and explicit routing.
- Disconnect networking and run accepted artwork and actual media. Verify every
  cue, manual Go, timed advancement, pause/resume, restart, visual fades and audio
  source switching. AI-generated content needs no network during playback.
- Check physical calibration, masks/feathering, curved grids, per-output color,
  overlap blending, and absence of light spill or cross-output leakage.
- Exercise F12 during video, calibration, generation and fades. Type rehearsal
  notes while performance is active; child-window input must not accidentally Go.
- Disconnect/reconnect an output; verify blocking and deliberate reassignment.
- Run a representative **four-hour offline rehearsal**. Observe glitches, memory
  growth, stalls, hardware temperature and audiovisual drift. Record measurements
  and problems in operator notes; the recorder cannot measure AV drift itself.
- Keep the verified collection, working project, backup and exported rehearsal
  report. Practice restoring the previous known-good collection.

These hardware acceptance steps remain pending until performed by the owner.
CI verifies software behavior and real file operations, not physical projection.

## Camera-assisted calibration

**Camera calibration…** projects local coded markers and validates a plane using independent withheld points before applying a selected mesh. Format 7 embeds its photo and evidence. The Windows package includes OpenCV native files; install the Microsoft Visual C++ 2015–2022 x64 redistributable if marker imaging cannot load. See [camera workflow and physical acceptance](CAMERA-CALIBRATION.md). Native imaging is unnecessary for playback of already accepted geometry.
