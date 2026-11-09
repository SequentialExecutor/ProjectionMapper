using System;
using System.IO;
using System.Text.Json;

namespace ProjectionMapper.Services
{
    public static class CameraCalibrationSettings
    {
        public static double DefaultTolerance()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json"); double value = 3;
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.TryGetProperty("CameraCalibration", out var section) && section.TryGetProperty("DefaultTolerancePixels", out var tolerance)) value = tolerance.GetDouble();
            }
            if (!double.IsFinite(value) || value < .25 || value > 25) throw new InvalidDataException("Camera calibration tolerance must be between 0.25 and 25 projector pixels.");
            return value;
        }
    }
}
