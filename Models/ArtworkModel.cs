using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectionMapper.Models
{
    public enum ArtworkProvider { OpenAI, GoogleGemini, StabilityAI }
    public readonly record struct ArtworkRegion(double X, double Y, double Width, double Height)
    {
        public static ArtworkRegion Full => new(0, 0, 1, 1);
    }
    public sealed record ArtworkCell(string MeshId, string Name, ArtworkRegion Region);
    public sealed record ArtworkReference(string FileName, string MimeType, string ImageBase64, string Sha256);

    /// <summary>Actual PNG bytes and generation history; credentials never belong in this model.</summary>
    public sealed class ArtworkAsset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Artwork";
        public string ImageBase64 { get; set; } = string.Empty;
        public string ThumbnailBase64 { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public ArtworkProvider Provider { get; set; }
        public string Model { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string Style { get; set; } = string.Empty;
        public string Palette { get; set; } = string.Empty;
        public string EffectivePrompt { get; set; } = string.Empty;
        public string? ProviderRequestId { get; set; }
        public string Quality { get; set; } = "medium";
        public string Background { get; set; } = "opaque";
        public string RequestedSize { get; set; } = "1024x1024";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<ArtworkCell> Layout { get; set; } = new();
        public List<ArtworkReference> References { get; set; } = new();
        public ArtworkAsset Copy() => new()
        {
            Id = Id, Name = Name, ImageBase64 = ImageBase64, ThumbnailBase64 = ThumbnailBase64, Sha256 = Sha256,
            Width = Width, Height = Height, Provider = Provider, Model = Model, Prompt = Prompt, Style = Style, Palette = Palette,
            EffectivePrompt = EffectivePrompt, ProviderRequestId = ProviderRequestId, Quality = Quality, Background = Background,
            RequestedSize = RequestedSize, CreatedAt = CreatedAt, Layout = Layout.ToList(), References = References.ToList()
        };
        public override string ToString() => $"{Name} · {Provider} · {Width}×{Height}";
    }
}
