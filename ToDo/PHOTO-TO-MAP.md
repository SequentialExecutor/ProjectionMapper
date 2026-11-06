# Assisted photo to map

## Workflow

1. Open **Photo to map…**. Import a JPEG, PNG, or BMP scene reference. Import runs
   on a separate STA worker: it corrects JPEG EXIF orientation, limits the long edge
   to 2048 pixels, and creates a JPEG from clean pixels without original metadata.
   Up to eight scene references are embedded in each project, including recovery
   snapshots. No reference photo is sent anywhere merely by importing it.
2. Draw polygons manually, or describe the target objects and request AI proposals.
   The first provider is OpenAI's Responses API. The displayed model is configurable
   in the dialog and defaults to `PhotoMapping.VisionModel` in `appsettings.json`.
3. For AI analysis, enter an API key and explicitly enable the upload/billing notice.
   Only the selected normalized JPEG and description are sent to OpenAI. Keys stay
   in dialog memory, are cleared when it closes, and are never written to project
   files, configuration, recovery snapshots, or logs. The Responses request sets
   `store:false`; provider account policies still govern processing/retention.
4. Review every proposal. AI estimates are not calibrated confidence scores and
   never automatically accept a shape. Invalid proposals are rejected, uncertain
   proposals remain drafts, and overlapping/touching outlines receive a review
   notice. New results append to the current reference instead of replacing work.
5. Drag numbered vertices or edit their photo percentages. Rename, delete, split
   along non-adjacent vertices, or undo/redo. Draw perimeter vertices in order and
   choose **Finish polygon**; Escape cancels drawing. Undo during drawing removes
   its latest vertex. Completed-outline undo history is local to the current
   reference/dialog; switching references starts a new local history.
6. Mark useful shapes **Reviewed / accepted**, then save the photo maps into the
   project. This commits editor state; use normal Save/Save As to write the project
   to disk. Photo-only projects also receive periodic recovery snapshots.
7. To create projection content, select an imported video in the main window before
   opening Photo to map. Enable mesh creation and acknowledge the manual alignment
   step. Accepted convex four-corner surfaces become meshes for that source with
   full-source input coordinates and photo-derived destination starting positions.
   They start unassigned to physical outputs (`-1`, preview only).
8. Open **Calibrate output…**, align against the physical scene, assign the actual
   projector, and save the project. Photo coordinates, media crop coordinates, and
   calibrated projector coordinates remain separate.

Repeated imports do not replace earlier references. Reopening a photo map and
creating meshes skips surface IDs already used by the selected source. Editing or
removing an outline does not move/delete an existing projection mesh; this preserves
manual alignment. Existing meshes retain their surface ID and reference checksum.
Undo for created meshes uses the main application's existing history and unregisters
removed meshes from rendering. Switching projects clears history to avoid applying
old actions to a new project. Derived splits and bounding quads retain their source
surface ID and original AI provenance where available.

## Polygon limits

Each reference supports up to 64 simple polygons with 3–32 vertices. Numeric and
split operations validate finite normalized coordinates, area, self-intersections,
and split geometry before committing changes. Vertex drags that produce invalid
polygons are reverted. Overlap is a review notice, not an automatic rejection:
adjacent surfaces may intentionally share edges.

The current projection renderer accepts convex quadrilaterals, not arbitrary masks.
Other accepted polygons remain editable annotations in the project. **Fit bounding
quad** adds an explicit rectangular approximation as a separate draft; it does not
mask the original shape or crop away surrounding space. Polygon masks and curved
surface meshes remain later roadmap work.

## Provider behavior and configuration

```json
"PhotoMapping": {
  "VisionModel": "gpt-4.1-mini",
  "TimeoutSeconds": 120
}
```

Choose an account-accessible model supporting image input and structured JSON
outputs. The endpoint is fixed to `https://api.openai.com/v1/responses`; redirects
are disabled. Requests have a bounded output schema and response size, and no
silent retries. Cancellation and timeout leave existing proposals intact, but the
provider may still charge for a request already submitted. The UI explains account
billing before submission; it does not invent a per-request cost estimate.

A provider interface separates analysis from the photo editor. No credentials or
live-provider access were available in the development environment, so automated
checks exercise the HTTP contract with explicit synthetic provider responses, not
actual recognition quality or billing. The production adapter makes real requests
when invoked from the app with an account key.

API contract references:
- https://developers.openai.com/api/reference/resources/responses/methods/create
- https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses

## Project compatibility and provenance

Project format version 3 stores normalized JPEG bytes, dimensions, reference IDs,
checksums, surface names/vertices, acceptance state, provider/model identifiers,
original estimates, descriptions, response IDs, and creation times. Project loading
validates reference checksums and geometry. Versions 1 and 2 still load; saving
upgrades them to version 3. The preceding version-2 application rejects version-3
files rather than opening and silently discarding the new photo data.

Photos and descriptions are private project content; accepted results need no API
key or network connection to reopen, edit, calibrate, or play back.

## Automated checks

The persistence regression executable covers manual geometry, splitting, overlap,
quad conversion, photo/provenance round trips, version-2 compatibility, invalid
checksums, request schema and authorization placement, invalid-provider output,
rate-limit handling, cancellation, and the absence of automatic retries.

The Windows regression executable additionally creates a real JPEG with EXIF
orientation, imports it on the STA normalization worker, verifies rotated dimensions
and pixels, and checks that original metadata is absent. Existing rendering,
project-recovery, and routing checks remain enabled in Windows CI.

## Manual acceptance still required

- Use actual scene photos and an account key to evaluate detection quality, names,
  uncertain/empty results, model availability, and provider billing behavior.
- Check JPEG orientations, large/portrait photos, and reference selection visually.
- Draw, drag, split, accept, undo/redo, save/reopen, and recover multiple references.
- Create preview-only meshes, align them on real projectors, save/reopen, and verify
  that changing the photo annotations never overwrites calibrated mesh positions.
- Exercise network loss, invalid keys, cancellation, and closing during analysis;
  verify manual mapping remains available and no keys appear in saved files.

Automatic camera-to-projector calibration, depth reconstruction, image generation,
and arbitrary projection masks are not part of this assisted first version.
