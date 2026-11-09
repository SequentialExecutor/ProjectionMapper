# Camera-assisted planar calibration

This milestone implements the roadmap's marker/camera workflow. It is local:
OpenCV detects coded markers, a managed planar solver estimates the transform,
and the user reviews a projected target outline before applying one mesh. No AI
key, upload or network request is involved. Mathematical/image-fixture tests are
not a measurement of a real installation's alignment accuracy.

## Workflow

1. Review **Output setup**, select a mesh, unlock it, and assign its projector.
   Exit performance mode. Disable any curved deformation grid on this mesh;
   this operation calibrates one plane, not a curved or depth-varying object.
2. Open **Camera calibration…**. Set the native-pixel marker region so all markers
   land on the **same physical plane**. Use a region of at least 320×320 output
   pixels. A rectangle spanning different walls/depths cannot establish one valid
   homography. Generate/project the pattern and keep the camera/projector fixed.
3. The pattern contains nine random, unique `DICT_6X6_250` marker IDs. Quiet borders
   preserve detectability; printed IDs assist manual review. The panel lists IDs
   in projector row order. Other output windows are blanked during camera pattern
   or outline projection. Unrelated markers and duplicate IDs are excluded from
   automatic matches. An old photograph is not automatically associated with a
   new marker set. Export the current pattern PNG if needed; it must be shown
   unscaled, uncropped, unrotated and at the intended native output placement.
4. Photograph the pattern on the plane and import that image. EXIF orientation
   is normalized, metadata stripped, and the photograph resized to at most 2048
   pixels per axis using the existing scene-photo importer. Calibration photos
   must retain at least 320 pixels on each axis. All marker and target coordinates
   refer to this exact normalized photo, not an earlier scene-photo camera pose.
5. Inspect detected centers/IDs. Select a row and click the photo to add/correct a
   center, or edit normalized X/Y. Use photo zoom/pan for precise work. Uncheck
   observations only deliberately; enough independent evidence must remain.
   A center is the intersection of the tag's diagonals, which remains correct
   under perspective; it is not the arithmetic mean of four perspective corners.
6. Gold/check markers are withheld from fitting. At least **five fit markers**
   and **two independent check markers** are required. The default pattern uses
   six fit/three check markers distributed around the plane. Fit markers must
   cover at least 30% of the requested projector region and a meaningful area of
   the photo. Collinear, clustered, singular or ill-conditioned evidence is rejected.
7. Choose **Mark target corners** and click content **TL, TR, BR, BL** in this same
   photo. These labels define texture orientation. Alternatively, place the current
   mapped quad on the photo as a starting point, then correct it using the marked
   target corners workflow. Do not reuse geometry from a photo taken from another
   camera position. A single arbitrary photo does not independently calibrate a
   projector; the known projected correspondences supply the missing relationship.
8. Fit and review errors. The panel reports fit RMS/max and independent-check
   RMS/max in **native projector pixels**, plus individual marker errors. The
   default tolerance is 3 px, configurable in `CameraCalibration` settings and
   editable between 0.25 and 25 px per session. This default is not an owner-agreed
   physical accuracy guarantee. Both fit and check maximum errors must pass it.
9. Target corners normally must stay inside marker coverage. An explicit option
   permits at most 10% of the requested region outside its marker hull. Use this
   sparingly and verify the physical edges; validation inside the hull does not
   prove extrapolated accuracy. Invalid/nonconvex quads, projective-horizon crossing
   and off-projector points are rejected. Coordinates are never silently clamped.
10. Project the target outline. Verify all four physical corners and the chosen
    texture orientation. Confirm that the setup stayed fixed and the surface is
    planar, then Apply. Changes invalidate confirmation and require a fresh fit/
    preview. F12 blackout remains available throughout. Merely importing, fitting,
    showing a pattern, loading evidence, or projecting an outline never changes
    saved mesh geometry or recovery snapshots.

Apply changes all four output corners together and records one Undo/Redo action,
including its evidence. Video input crop, masks, opacity, artwork and other meshes
are preserved. Closing/cancelling restores normal output/cue state; playback and
AI generation can continue behind the modal panel. Display/project changes reject
late results and require output review. Existing calibration overlays are hidden
for the camera session and restored afterwards.

## Evidence and reuse

Project format **7** embeds up to 32 calibration records: the normalized photo,
checksum, pattern region/placements/IDs, matched coordinates and their origins,
fit vs withheld markers, transform, errors/tolerance, target, output hardware,
and operator confirmation. Formats 1–6 remain readable. Saved evidence is
recomputed/validated on load/save; a changed residual/quad/matrix cannot masquerade
as the result of different stored correspondences. Collection includes these
records inside the project with no external photo dependency or AI credential.

The panel can load saved evidence as an **unconfirmed draft**. This does not prove
that the camera, projector or plane remained fixed. Fit/project/physically verify
again, or generate new markers and recapture. A saved record from different
hardware/dimensions cannot be reused through this shortcut. Older evidence can be
removed from the project through the panel; main Undo restores it. Archive a
working/collected project before removing records needed for long-term provenance.

Preflight warns when the latest retained evidence for a mesh no longer matches
its edited output quad or hardware. The recorded error is historical, not a live
accuracy meter. Manually calibrated projects do not need camera evidence to play.
Accepted geometry and evidence remain usable offline without OpenCV detection.

## Implementation and limits

Coded-marker generation/detection uses OpenCvSharp **4.13.0.20261107** and its
Windows x64 native runtime. The published Windows application includes the native
files. Install the Microsoft Visual C++ 2015–2022 x64 redistributable if the native
library cannot load. This dependency is needed for marker imaging/detection, not
for playing an already calibrated show. Native objects are owned/disposed within
each worker operation. Large image work stays off the dispatcher; a cancellation
request waits for the bounded native detection to return rather than aborting a
thread in native code.

The managed solver uses centered/scaled coordinates and reorthogonalized QR least
squares. It fits only designated training centers, evaluates withheld centers,
and refuses failed evidence instead of automatically discarding outliers or
loosening the requested tolerance. Bad matches can be corrected/disabled manually.
The photo's JPEG header/dimensions are checked before native decompression.

This model does **not** correct camera lens distortion, estimate 3D geometry,
track camera/projector movement, automate camera capture, or calibrate several
depths with one transform. Lens distortion, uneven surfaces, bloom, blur, glare,
poor marker coverage and low photo resolution can invalidate a fit. Keep the
lens/camera framing fixed, reshoot or calibrate smaller planes when checks fail.
There is no automatic physical accuracy claim based on a low training residual.

## Verification and hardware acceptance

Cross-platform tests use explicitly synthetic coordinates/images to verify QR
mapping, withheld-marker rejection, coverage/degeneracy, bounds/extrapolation,
immutable snapshots, evidence integrity, schema migration and collected evidence.
Windows tests run the actual native dictionary generator/detector on known marker
images, perspective/JPEG image fixtures, unrelated IDs, PNG export, capture
isolation/blackout precedence, atomic quad assignment and window construction.
Existing routing, masks, cues, persistence, preflight and artwork checks stay on.

Real hardware acceptance remains pending:

- Agree on a native-projector-pixel tolerance for the actual installation, then
  measure independent physical corner/edge errors on a flat target.
- Capture real patterns under normal room/projector lighting; inspect failed,
  occluded, duplicate and unrelated tags and test manual correction with zoom.
- Test lens distortion, oblique views, partial coverage and separate depth planes;
  confirm unreliable fits are rejected and no invalid mapping is silently applied.
- Verify texture orientation, source crop/masks/artwork, Undo/Redo, save/reopen and
  collection transfer, including historical evidence after manual edits.
- Test F12 during marker/outline projection, cancellation, window close and display
  disconnect/reconnect. No pattern should bypass blackout or destination review.
