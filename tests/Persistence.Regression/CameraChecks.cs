using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;

internal static class CameraChecks
{
    public static async Task Run()
    {
        static void Check(bool value, string description) { if (!value) throw new Exception("Camera calibration regression: " + description); }
        static bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        var pattern = CameraFixture.Pattern(); var matches = CameraFixture.Matches(pattern); var fit = PlanarCalibration.Fit(pattern, matches, 3);
        Check(fit.Reliable && fit.ValidationMaxPixels < 1e-7 && fit.FitMaxPixels < 1e-7, "independent validation of known projective transform");
        foreach (var p in new[] { new PhotoPoint(.2, .3), new(.6, .7), new(.5, .5) })
        {
            var expected = PlanarCalibration.Transform(CameraFixture.Transform, p); var actual = PlanarCalibration.Transform(fit.Matrix, p);
            Check(Math.Abs(actual.X - expected.X) < 1e-9 && Math.Abs(actual.Y - expected.Y) < 1e-9, "QR estimate preserves projective mapping beyond fit points");
        }
        var bad = matches.Select(m => m.Copy()).ToList(); var wrong = bad.First(m => m.ValidationOnly); wrong.Photo = new(wrong.Photo.X + .02, wrong.Photo.Y);
        var rejected = PlanarCalibration.Fit(pattern, bad, 3);
        Check(!rejected.Reliable && rejected.FitMaxPixels < 1e-7 && rejected.ValidationMaxPixels > 3, "low training error cannot hide a bad withheld marker");
        var noChecks = matches.Select(m => { var c = m.Copy(); c.ValidationOnly = false; return c; }).ToList();
        Check(Reject(() => PlanarCalibration.Fit(pattern, noChecks, 3)), "independent validation is mandatory");
        var line = matches.Select(m => { var c = m.Copy(); c.Photo = new(c.Photo.X, .5); return c; }).ToList();
        Check(Reject(() => PlanarCalibration.Fit(pattern, line, 3)), "collinear observations rejected");
        var cluster = matches.Select(m => { var c = m.Copy(); c.Photo = new(.5 + c.Photo.X * .01, .5 + c.Photo.Y * .01); return c; }).ToList();
        Check(Reject(() => PlanarCalibration.Fit(pattern, cluster, 3)), "small camera coverage rejected");
        var record = CameraFixture.Record(); PlanarCalibration.ValidateRecord(record);
        var inverse = PlanarCalibration.Inverse(CameraFixture.Transform);
        var edge = new[] { new PhotoPoint(.01, .1), new(.99, .1), new(.99, .9), new(.01, .9) }.Select(p => PlanarCalibration.Transform(inverse, p)).ToList();
        Check(Reject(() => PlanarCalibration.MapTarget(pattern, matches, fit, edge, false)), "unreviewed extrapolation rejected");
        Check(PlanarCalibration.MapTarget(pattern, matches, fit, edge, true).Length == 4, "bounded extrapolation requires explicit opt-in");
        var outside = new[] { new PhotoPoint(.8, .3), new(1.05, .3), new(1.05, .7), new(.8, .7) }.Select(p => PlanarCalibration.Transform(inverse, p)).ToList();
        Check(Reject(() => PlanarCalibration.MapTarget(pattern, matches, fit, outside, true)), "off-projector targets are rejected rather than clamped");
        var wrongDimensions = record.Photo.Copy(); wrongDimensions.Width++;
        Check(Reject(() => PlanarCalibration.ValidatePhoto(wrongDimensions)), "calibration JPEG dimensions verified before decompression");
        var copied = record.Copy(); copied.Matches[0].Enabled = false;
        Check(record.Matches[0].Enabled && ReferenceEquals(copied.Photo.ImageBase64, record.Photo.ImageBase64), "evidence snapshots copy editable matches and share immutable photo bytes");
        string directory = Path.Combine(Path.GetTempPath(), "Camera-record-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var project = new ProjectModel { Name = "Synthetic calibration evidence", CameraCalibrations = new() { record } };
            var service = new ProjectService(); string path = Path.Combine(directory, "show.pmproj");
            Check(await service.SaveAsync(project, path), "calibration evidence saves");
            var loaded = await service.LoadAsync(path) ?? throw new Exception(service.LastError);
            Check(loaded.ProjectVersion == 7 && loaded.CameraCalibrations[0].Photo.ImageBase64 == record.Photo.ImageBase64 && loaded.CameraCalibrations[0].Matches.Count == 9, "photo, matches and fit survive format-7 reload");
            loaded.CameraCalibrations[0].ValidationMaxPixels = 0.2;
            Check(!await service.SaveAsync(loaded, path) && (await service.LoadAsync(path))!.CameraCalibrations[0].ValidationMaxPixels < 1e-7, "falsified residuals cannot replace a valid saved project");
            var collected = await new PortableProjectService().CollectAsync(project, Path.Combine(directory, "collected"));
            var portable = await service.LoadAsync(collected.ProjectPath) ?? throw new Exception(service.LastError);
            Check(portable.CameraCalibrations[0].Photo.ImageSha256 == record.Photo.ImageSha256 && (await new PortableProjectService().VerifyAsync(collected.Directory)).All(c => c.Severity != CheckSeverity.Error), "collection retains embedded calibration evidence without external photo dependencies");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Planar fitting, withheld-marker rejection, bounds, evidence integrity and portable calibration checks passed. Synthetic geometry; no physical accuracy claim.");
    }
}
