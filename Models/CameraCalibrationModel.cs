using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectionMapper.Models
{
    public readonly record struct CalibrationRegion(double X, double Y, double Width, double Height);
    public sealed record CalibrationMarker(int Id, int X, int Y, int Size, bool ValidationOnly)
    { public PhotoPoint Center => new(X + (Size - 1) / 2.0, Y + (Size - 1) / 2.0); }
    public sealed class CameraPattern
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Width { get; set; }
        public int Height { get; set; }
        public CalibrationRegion Region { get; set; }
        public string Dictionary { get; set; } = "DICT_6X6_250";
        public List<CalibrationMarker> Markers { get; set; } = new();
        public CameraPattern Copy() => new() { Id = Id, Width = Width, Height = Height, Region = Region, Dictionary = Dictionary, Markers = Markers.ToList() };
    }
    public sealed class MarkerObservation
    {
        public int Id { get; set; }
        public PhotoPoint Photo { get; set; }
        public bool Enabled { get; set; } = true;
        public bool ValidationOnly { get; set; }
        public string Origin { get; set; } = "Detected";
        public MarkerObservation Copy() => (MarkerObservation)MemberwiseClone();
    }
    public sealed class CameraCalibrationRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string MeshId { get; set; } = string.Empty;
        public string MeshName { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string ComputerId { get; set; } = string.Empty;
        public DisplayDescriptor Output { get; set; } = new(-1, "", "", 0, 0, 1, 1, 1, 1, false);
        public CameraPattern Pattern { get; set; } = new();
        public PhotoMappingModel Photo { get; set; } = new();
        public List<MarkerObservation> Matches { get; set; } = new();
        public double[] PhotoToProjector { get; set; } = Array.Empty<double>();
        public double FitRmsPixels { get; set; }
        public double ValidationRmsPixels { get; set; }
        public double ValidationMaxPixels { get; set; }
        public double TolerancePixels { get; set; } = 3;
        public bool AllowLimitedExtrapolation { get; set; }
        public bool OperatorConfirmed { get; set; }
        // Content TL, TR, BR, BL in the SAME photograph used for marker fitting.
        public List<PhotoPoint> TargetPhotoCorners { get; set; } = new();
        // Renderer order TL, TR, BL, BR; normalized to the selected physical output.
        public float[] OutputCorners { get; set; } = Array.Empty<float>();
        public CameraCalibrationRecord Copy() => new()
        {
            Id = Id, MeshId = MeshId, MeshName = MeshName, CreatedUtc = CreatedUtc, ComputerId = ComputerId, Output = Output with { }, Pattern = Pattern.Copy(),
            Photo = Photo.Copy(), Matches = Matches.Select(m => m.Copy()).ToList(), PhotoToProjector = (double[])PhotoToProjector.Clone(), FitRmsPixels = FitRmsPixels,
            ValidationRmsPixels = ValidationRmsPixels, ValidationMaxPixels = ValidationMaxPixels, TolerancePixels = TolerancePixels,
            AllowLimitedExtrapolation = AllowLimitedExtrapolation, OperatorConfirmed = OperatorConfirmed, TargetPhotoCorners = TargetPhotoCorners.ToList(), OutputCorners = (float[])OutputCorners.Clone()
        };
    }
}
