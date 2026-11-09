# Project persistence and recovery milestone

## Goals delivered

- Explicit saves write a detached snapshot into a unique same-directory staging
  file, flush it, then atomically replace the destination. A valid previous save
  becomes `<project>.bak`. Serialization errors, cancellation, and failed staging
  do not truncate the previous save. A corrupt primary cannot replace a good backup.
- Saves on a service instance are serialized; staging filenames are unique.
- Versions 1–5 remain readable; unsupported versions and malformed project
  data produce a useful error before the active editor is cleared. Saving writes v4, including embedded photo references, surface proposals, and portable artwork.
- Surface/layer collections and vector/matrix fields round-trip correctly.
- UI snapshots preserve identity, creation date, surface metadata, display
  assignments, playlists, zoom, and overlay preferences. Existing per-mesh
  visibility is retained during loading. Async saves leave newer edits dirty.
- Media paths are stored relative to the project and resolved against its folder
  when opened. Missing imported media can be relinked before current state is
  discarded; canceling relink keeps the current editor intact.
- Loading a damaged project offers its previous saved backup explicitly. Recovery
  and relinking mark the opened project dirty, requiring a deliberate save.
- A separate recovery service writes periodic snapshots in the user's local
  application data under `ProjectionMapper/Recovery`. Snapshot capture occurs on
  the dispatcher, while periodic scheduling and disk I/O use a background worker.
  Session locks exclude live app instances. A subsequent launch offers the newest
  interrupted-session snapshot, opening it as unsaved work with Save As required.
- Normal close deletes that session's recovery data only after save/discard is
  decided. The async Closing handler cancels closure synchronously while saving,
  preventing shutdown before the save completes. Ordinary project backups remain.

## Configuration

`appsettings.json` is copied into build/publish output. Set
`ProjectRecovery.IntervalSeconds` to the desired interval (default 60 seconds), or
0 to disable periodic recovery. Recovery failures appear in the status bar.

Snapshots preserve work only as of their last successful interval. Explicit saves
remain necessary. If several old interrupted sessions exist, the newest is offered
at startup; older snapshots remain available in the Recovery directory. Recovery
files contain local media paths and should be treated as private project data.

## Verification

Run:

```text
dotnet run --project tests/Persistence.Regression/Persistence.Regression.csproj --configuration Release
dotnet build ProjectionMapper.csproj --configuration Release
```

The cross-platform executable uses the actual project persistence/recovery services
and real temporary files. It checks surface/vector/matrix round trips, playlists,
output assignments, relative media paths, good-backup retention, failed/canceled
saves, future/legacy versions, overlapping saves, periodic recovery, live-session
exclusion, orphan discovery, and normal-shutdown cleanup. No FFmpeg or projectors
are required. The Windows desktop workflow runs it alongside routing regressions.

Persistence/recovery regression checks pass in the Linux development environment.
The WPF application must also pass Windows CI; interactive behavior below remains
manual acceptance work, not claimed as verified by the filesystem checks.

## Windows acceptance checklist

1. Edit input/output corners, visibility, target displays, zoom, and playlist groups;
   save/reopen and compare all settings and projected geometry.
2. Move a project with its media folder, reopen it, then move one source elsewhere;
   relink without losing meshes or cues. Cancel relink and verify current work stays.
3. Save twice, damage a copy of the primary JSON, and open it. Accept the backup
   offer and verify it opens dirty; cancel the offer and verify nothing is cleared.
4. Make unsaved edits, wait a recovery interval, end the process, and restart.
   Restore the snapshot, verify media/mapping, and save it under a deliberate name.
5. Close while choosing Save; verify the application waits for completion. Cancel
   Save As or force an unwritable destination and verify the window stays open.
6. Run two instances: neither should offer the other's active recovery snapshots.

## Scope remaining

This milestone establishes durable project storage and isolates recovery work from
explicit saves. Broad decoder/playback refactoring, audio synchronization, and
physical display identity handling remain separate roadmap tasks. Legacy MapMap
import remains the pre-existing placeholder; this change does not claim to implement
that file format. Project collection/packaging is a later feature, distinct from
relative path support and individual missing-media relinking.

Format 5 adds surface masks/feathering/deformation grids, cue advancement/fades and per-output color/blending. See [Artist controls](ARTIST-CONTROLS.md).
