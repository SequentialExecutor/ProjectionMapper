using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ProjectionMapper.Models
{
    public readonly record struct PhotoPoint(double X, double Y);

    /// <summary>Normalized, orientation-corrected scene reference and editable photo-space proposals.</summary>
    public sealed class PhotoMappingModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ImageBase64 { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string ImageSha256 { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public List<PhotoSurfaceModel> Surfaces { get; set; } = new();

        // Image strings are immutable; clone editable geometry without copying large image payloads.
        public PhotoMappingModel Copy() => new()
        {
            Id = Id, ImageBase64 = ImageBase64, OriginalFileName = OriginalFileName,
            ImageSha256 = ImageSha256, Width = Width, Height = Height,
            Surfaces = JsonSerializer.Deserialize<List<PhotoSurfaceModel>>(JsonSerializer.Serialize(Surfaces)) ?? new()
        };
    }

    public sealed class PhotoSurfaceModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Surface";
        // Perimeter order in PHOTO coordinates, normalized to [0,1]. Not projector calibration.
        public List<PhotoPoint> Points { get; set; } = new();
        public bool Accepted { get; set; }
        public string Origin { get; set; } = "Manual";
        public double? OriginalConfidence { get; set; }
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? RequestDescription { get; set; }
        public string? ResponseId { get; set; }
        public string? DerivedFromSurfaceId { get; set; }
        public string? ReferenceImageSha256 { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
