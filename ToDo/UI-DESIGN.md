# Workspace design

The editor uses a quiet slate palette with teal for the primary action. Light and Follow Windows appearances are available under View > Appearance; the header offers a quick light/dark switch. The choice is saved locally, separately from projects. Windows High Contrast uses system colors for application controls.

## Find the next action

- Import media starts a project; the empty workspace explains how to add a surface.
- Create contains photo mapping and the AI artwork library.
- Mapping contains manual/camera calibration and surface masks/grids.
- Show setup contains output assignment, color/blending, and cue settings.
- The project tree and selected-item inspector share the left column.
- Source and Projection previews sit beside one another. Each has its own zoom and Fit button. Drag the dividers to allocate space.
- Playback, Go, performance lock, and blackout stay together at the bottom. Existing keyboard shortcuts remain available.

Shared styles provide consistent typography, spacing, rounded controls, focus indicators, and disabled/selected states across the editor and dialogs. Smaller dialogs scroll when necessary.

## Coordinate and rendering boundaries

Preview LayoutTransforms scale the native frame coordinate space and expose correct scroll extents. Fit changes only the editor zoom. It does not change mesh geometry, output dimensions, calibration, artwork, or projector color. Projector fullscreen canvases, mesh handles, and calibration patterns retain their rendering colors. Performance mode disables the complete editing command row, including import, and the editor area.

## Verification

Build the WPF application and run both regression programs. The Windows regression initializes the actual theme dictionary and renders the real empty editor at 1366×860 and 1024×680 in both palettes. It checks that Go, blackout, performance, and both previews remain within the layout. CI publishes PNGs as `ui-previews`, along with the Windows application artifact. These snapshots do not open physical output windows or imply a projector acceptance test.

Still verify actual projector alignment, keyboard navigation, high-DPI/mixed-display use, and assistive technology on the installation's Windows equipment before a live show.
