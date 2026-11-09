# Multi-provider AI artwork and portable project library

## Generate and download

1. Select mapped surfaces, then open **AI artwork…**. The window is modeless:
   generation does not pause playback or prevent editing in the main window.
2. Select **OpenAI**, **Google Gemini**, or **Stability AI** and enter that provider's
   API access token/key. Use an API key, not a web-login/session token or a chat
   subscription credential. Keys are provider-specific.
3. Optionally enable **Remember in Windows Credential Manager**. Otherwise the key
   stays in this window's session memory. **Forget stored token** deletes the selected
   provider's saved credential. No key is written to projects, images, metadata,
   configuration files, recovery snapshots, download URLs, or logs.
4. Select one to eight mapped surfaces. Enter a prompt, shared project style, palette,
   output aspect, and variant count. The generator makes an unwarped content layout;
   projector distortion is applied only by the existing renderer.
5. Choose a download folder. Unsaved projects default to the application's local
   Artwork folder; saved projects default to an Artwork folder alongside the project.
   The app checks that it can write there before submitting a billable request.
6. Review the upload/billing notice and choose **Generate and download**. Completed
   variants are decoded, normalized to PNG, and downloaded using stable unique names.
   Artist-edited files already occupying a generated name are preserved. If a download
   fails after generation, a decoded variant remains available to save elsewhere.
7. Compare variants, use **Preview on projectors**, then **Keep in project library**
   or **Apply to selected surfaces**. Temporary preview follows playlist visibility;
   Restore preview/closing returns to the current permanent content. Acceptance
   preserves calibrated corners and the saved video crop; artwork uses the full
   assigned image cell before the output warp.
8. Save the project. Accepted PNGs, thumbnails, generation metadata, reference images,
   and assignments are embedded in the project. External download files are useful
   exports, not required dependencies for reopening the artwork.

## Supported provider capabilities

| Provider | Default model | Image references/layout guide | Quality/transparency | Requests for N variants |
| --- | --- | --- | --- | --- |
| OpenAI | gpt-image-1.5 | Guide plus up to two references | Quality and PNG transparency controls | One batch request |
| Google Gemini | gemini-2.5-flash-image | Guide plus up to two references | Model-dependent quality; opaque/auto in this adapter | Up to N requests |
| Stability AI | Stable Image Core | Text layout, style, palette; no uploaded references in this adapter | Core defaults; opaque/auto | Up to N requests |

Model names are editable for OpenAI/Gemini so an account-accessible compatible model
can be selected. Stability Core uses its dedicated endpoint. The UI makes unsupported
controls explicit. Reference imports are normalized and stripped of original metadata.
Selected artwork can also become a PNG reference for a later variation.

There is no fabricated price estimate. The notice explains account billing and the
number of requests before submission. No application-level automatic retries occur.
Cancellation/timeouts may still incur provider charges. Completed images from a
partial multi-request batch are retained; remaining requests stop after a failure.

## Reuse and regenerate

The image list combines current unaccepted variants with the accepted project
library. **Use selected image's settings** restores its prompt, style, palette,
model, references, and original surface selection without calling a provider.
Choose a subset/new surface and explicitly generate again to make another variation.
A single-cell image may be reused on multiple surfaces. Coordinated atlas images
use their original cell/surface matches when possible, otherwise the displayed
surface order. Review projected results before accepting an AI composition: a model
may not follow cell layout instructions precisely.

Assignment changes participate in main-window Undo/Redo. Undo restores the prior
content and offline still duration without deleting downloaded or accepted images.
The inspector's **Restore video content** relinks a missing original if needed,
then restores that surface's video content. Artwork never overwrites the saved
source crop or calibrated geometry. Deleting a mesh unregisters its content so it
cannot leave a ghost image on another surface.

## Offline behavior

Accepted artwork needs no token or provider connection for playback. If an original
video exists, its playback/audio/playlist timing continues while artwork replaces
selected mesh content. If the original is missing and all meshes under that source
have accepted artwork, the source uses saved still images instead of FFmpeg or a
network request. Legacy playback holds the still indefinitely; playlist cues use
the assigned **Offline still cue duration** (default 30 seconds), including pause,
resume, restart, and group completion.

Mixed sources with video-dependent meshes still require their original media.
Missing-video fallback cannot reproduce the original video's audio; other available
video/audio sources keep their existing behavior. Saved still assignments stay
restricted to active playlist groups. A copied artwork-covered project can therefore
open and run offline without its original videos or the external download folder.

## Storage, limits, and threading

Project format 6 retains artwork PNGs/thumbnails, hashes, provider/model, prompt,
style/palette, generation settings, request ID when provided, timestamp, layout,
reference data, and mesh image-cell bindings. Formats 1–5 remain readable; older
builds reject format 6 instead of silently dropping artwork. Projects support up to
32 accepted images; the generator keeps at most eight unaccepted variants in memory
and limits each batch to four. Archive extra results in the download folder.

Full-size image processing uses separate STA workers; the library decodes only its small saved thumbnails on the UI thread. Native WPF composition is synchronized
across video rendering, calibration grids, and generated layout guides. Project
snapshots share immutable image strings and clone editable metadata, avoiding large
image copies on the dispatcher. Explicit save serialization runs off the UI thread.
Project switching cancels/waits for pending artwork work and invalidates old preview
callbacks before another project can receive an assignment.

## Verification

Automated provider tests use explicit synthetic HTTP responses, not live paid calls.
They cover endpoint/authentication shape for all three adapters, reference handling,
batch request count, partial failure preservation, cancellation, layout validation,
portable image/assignment round trips, bad references, and still clock behavior.
Windows regression checks cover real PNG normalization/cropping, actual downloads
and edited-file preservation, preview restoration, inactive-group isolation, offline
registration without FFmpeg, still cue completion, and window markup construction.
Existing photo, persistence/recovery, calibration, and routing checks remain enabled.

No provider account keys were available in development. Live generation and visual
recognition/composition quality are not claimed as verified by contract tests.

Manual acceptance:
- Generate and download with an account key for each provider; confirm model access,
  billing, supported settings, and reference behavior.
- Compare/regenerate variants and inspect real projected crops, alpha, and warping.
- Edit geometry during generation; accept against current destinations without moving
  corners. Remove a surface/change projects during work and verify stale results are
  rejected, while completed files remain in the download folder.
- Save an artwork-covered project, copy only the project file, disconnect network,
  remove access to original videos/downloads, and verify playback/playlist timings.
- Test optional Credential Manager save/load/forget under the intended Windows user.

API references:
- https://developers.openai.com/api/reference/resources/images/methods/edit
- https://ai.google.dev/gemini-api/docs/image-generation
- https://platform.stability.ai/docs/api-reference
