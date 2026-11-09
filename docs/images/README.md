# Tutorial screenshot sources

These PNGs are captures of the actual WPF application controls, rendered on Windows with the dark palette. They show starting states and example text inputs, not a successful AI request, imported scene, measured projector calibration, or rehearsal result.

To regenerate, set `PROJECTIONMAPPER_CAPTURE_TUTORIAL=1` and run:

```powershell
dotnet run --project tests/OutputRouting.Regression/OutputRouting.Regression.csproj --configuration Release
```

The Windows regression must pass. Captures are written to `artifacts/ui-previews/tutorial/`; `.NET Desktop CI` includes them in its `ui-previews` artifact. Review each capture before copying it here. The helper never sends AI requests, enters credentials, or projects calibration patterns. Display names come from the actual Windows capture environment and will vary on another machine. Window content is captured without the native title bar or operating-system file pickers.

`workspace-dark.png` is the earlier empty-editor capture from the UI regression. The tutorial uses captions alongside unmodified screenshots so controls and labels remain authentic.
