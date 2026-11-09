# Per-display resolutions

Open **Show setup > Display resolutions…** outside Performance mode. Choose the display by its Windows device name, then select a resolution and refresh rate from the driver-reported list. The current Windows mode appears above the picker. Each display is configured independently; modes are not guessed from a list of generic presets.

Choose **Try resolution**. Output windows close while the mode changes. Choose **Keep resolution** within 15 seconds to save the mode in Windows, or **Revert now** to restore it immediately. Timeout or closing the confirmation also restores the previous mode. Unsupported modes and driver failures report an error; partial failures attempt rollback. If Windows cannot restore a disconnected or failed output, use Windows Display settings and reconnect the display.

After changing a mode, open **Show setup > Assign projector outputs…**, review the assignments, and restore output with F12. Check physical alignment and any overlap blending at the new resolution. Each projector renderer is rebuilt with the active Windows mode's pixel dimensions, and cached mesh content is refreshed even if playback is paused. Inactive playlist sources remain hidden.

The picker changes the actual Windows display signal mode, including refresh rate. It does not merely scale the editor preview or resize a video file. Native window placement still follows the display's desktop bounds. Render sizing now uses `EnumDisplaySettingsExW`'s active dimensions directly, rather than selecting the larger of mode dimensions and desktop bounds. Desktop DPI scaling is not an additional multiplier on the render target.

The selected Windows mode persists when explicitly kept. A saved project records its output layout as before; opening a project does not silently change Windows resolutions. Transferring or changing displays still requires output review. No project schema change is needed.

## Implementation and verification

`WindowsDisplayModes` uses a complete Unicode DEVMODE structure and changes only width, height, bit depth, display flags, and refresh rate for the chosen device. Display position, rotation, primary status, and other devices are not requested to change. Native driver test runs before the temporary switch. Confirmation saves to Windows only after operator input. The countdown uses monotonic elapsed time and asynchronous delays; closing while a native call is in progress waits for rollback. Native changes run off the UI thread.

Windows regression checks exercise temporary/kept transactions, unsupported modes, driver rejection, partial failure, persistence failure, idempotent rollback, closing the actual confirmation UI, and its actual 15-second timeout through an explicit backend fixture. Native enumeration is read-only in CI and checks that reported render dimensions match the active mode. The real dialog is captured for visual review. Tests do not switch CI resolutions or establish that a particular projector accepts a mode.

On hardware, check supported modes, refresh rate, timeout/revert/close behavior, native-size test grids, paused playback after reassigning outputs, and repeated changes on separate projectors. An empty output can also result from blackout, unreviewed assignments, an inactive cue, or missing media; resolution selection is not a diagnosis of every black screen.
