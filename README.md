# ProjectionMapper

Open source projection mapping software, inspired by the MapMap project.

**New here? Follow the [step-by-step tutorial](docs/TUTORIAL.md)** to create your first projection, generate artwork, and run a show.

I attempted to fork MapMap and modernize it, while fixing a bug causing a crash in Win11, as well as tweak features I wanted. However, the various Qt libraries have changed over the years and I rapidly grew annoyed by endless battles between compilation errors and behavioral issues.

This project is a from-scratch build in C# .NET 9. I considered making it in .NET MAUI for cross-platform support, but I'm realistically just trying to get something simple to use for my wedding in <6 months, and being cheap about it (not wanting to buy a license for anything, if possible).

The program icon was generated in Gemini. The project uses FFmpeg for video decoding/processing.

The entire program has been 100% built using CoPilot+ AI agents. Any changes I've actually made to code was to either add or remove a UI element that the AI agents weren't doing and it was simpler to just manually do it. There are for sure bugs in the current code and the audio doesn't work whatsoever (video starts glitching uncontrollably and whatnot). That's something I'll have AI fix, but it's not critical.

The program currently compiles and runs, allowing you to send video output to two different connected screens/projectors. I've been testing with a physical connection via HDMI _and_ wirelessly (WiFi) to two very cheap projectors, and it works fine in short tests.

### Artist controls and live operation

Use **Surface mask / curved mesh…** for polygon masks, inward feathering and subdivided texture grids. **Cue settings…** extends playlist groups with manual/timed advancement and visual fades. **Performance mode** locks editing; **F12** blacks out all projector presentations while playback continues. **Projector color / blend…** provides independent brightness/gamma/RGB and manual overlap ramps. Settings save in project format 7. See [artist controls and acceptance checks](ToDo/ARTIST-CONTROLS.md).

### Portable installations and rehearsal

Use **Collect / preflight / rehearsal…** to create a verified project folder, check dependencies/media/outputs, and record real rehearsal statistics. **Output setup…** explicitly remaps destination projectors when the computer or display layout changes. Windows CI publishes a self-contained **ProjectionMapper-win-x64** application artifact. See [installation, transfer and hardware acceptance](ToDo/INSTALLATION.md). Physical projector and sustained-show acceptance remain operator tasks.

### Camera-assisted planar calibration

Select an unlocked planar mesh assigned to a projector and open **Camera calibration…**. Project coded markers, import their photograph, inspect/correct matches, mark the target corners, and review independent projector-pixel errors before projecting an outline and applying. Accepted photos/evidence persist in format 7 and collected projects. This local workflow requires no AI token; physical accuracy must be measured on the installation. See [camera calibration](ToDo/CAMERA-CALIBRATION.md).

See the [workspace design guide](ToDo/UI-DESIGN.md) for the editor layout and appearance controls.

### Display resolutions

Use **Show setup > Display resolutions…** to choose a supported resolution and refresh rate for each display. Changes have a 15-second keep/revert confirmation. See [per-display resolution setup](ToDo/DISPLAY-RESOLUTIONS.md).

### Preview and output verification

Press **F5** for the composed editor preview; Esc returns to editing. Use **View > Output status…** for per-display routing, resolution and frame observations, suggested checks, and JSON export. See [output verification](ToDo/OUTPUT-VERIFICATION.md).
