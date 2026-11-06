using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ProjectionMapper.Models;

namespace ProjectionMapper.Utilities
{
    public static class ArtworkRules
    {
        public static void ValidateRegion(ArtworkRegion region)
        {
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
                region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 || region.X + region.Width > 1.000001 || region.Y + region.Height > 1.000001)
                throw new InvalidDataException("Artwork crop regions must stay inside the unwarped image.");
        }
        public static (int Width, int Height) PngDimensions(byte[] bytes)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidDataException("Artwork must be a PNG image.");
            int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 8_400_000)
                throw new InvalidDataException("Artwork dimensions exceed the supported limit.");
            return (width, height);
        }
        public static void ValidateAsset(ArtworkAsset asset)
        {
            if (!Enum.IsDefined(asset.Provider) || string.IsNullOrWhiteSpace(asset.Model) || asset.Model.Length > 120 ||
                string.IsNullOrWhiteSpace(asset.Prompt) || asset.Prompt.Length > 5000 || asset.Style == null || asset.Style.Length > 1000 || asset.Palette == null || asset.Palette.Length > 500 ||
                !Guid.TryParseExact(asset.Id, "N", out _) || string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 120 ||
                asset.ImageBase64 == null || asset.ImageBase64.Length > 16_000_000 || asset.ThumbnailBase64 == null || asset.ThumbnailBase64.Length > 500_000)
                throw new InvalidDataException("Invalid artwork identity, name, or image size.");
            var image = Convert.FromBase64String(asset.ImageBase64);
            var dimensions = PngDimensions(image);
            if (dimensions.Width != asset.Width || dimensions.Height != asset.Height ||
                !string.Equals(asset.Sha256, Convert.ToHexString(SHA256.HashData(image)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Artwork dimensions or checksum do not match its image.");
            PngDimensions(Convert.FromBase64String(asset.ThumbnailBase64));
            if (asset.Layout == null || asset.Layout.Count < 1 || asset.Layout.Count > 8 || asset.References == null || asset.References.Count > 2)
                throw new InvalidDataException("Artwork needs 1–8 layout cells and at most two reference images.");
            foreach (var cell in asset.Layout) ValidateRegion(cell.Region);
            foreach (var reference in asset.References)
            {
                if (reference == null || reference.ImageBase64 == null || reference.ImageBase64.Length > 8_000_000 || (reference.MimeType != "image/jpeg" && reference.MimeType != "image/png")) throw new InvalidDataException("Unsupported artwork reference.");
                var referenceBytes = Convert.FromBase64String(reference.ImageBase64);
                if (reference.MimeType == "image/png") PngDimensions(referenceBytes);
                else if (referenceBytes.Length < 3 || referenceBytes[0] != 255 || referenceBytes[1] != 216 || referenceBytes[2] != 255)
                    throw new InvalidDataException("Invalid JPEG artwork reference.");
                if (Convert.ToHexString(SHA256.HashData(referenceBytes)) != reference.Sha256)
                    throw new InvalidDataException("Artwork reference checksum mismatch.");
            }
        }
        public static List<ArtworkCell> Layout(IReadOnlyList<(string Id, string Name, double Aspect)> surfaces, int width, int height)
        {
            if (surfaces.Count < 1 || surfaces.Count > 8 || width <= 0 || height <= 0) throw new ArgumentException("Select one to eight mapped surfaces.");
            int columns = (int)Math.Ceiling(Math.Sqrt(surfaces.Count * (double)width / height));
            columns = Math.Clamp(columns, 1, surfaces.Count);
            int rows = (int)Math.Ceiling((double)surfaces.Count / columns);
            var result = new List<ArtworkCell>();
            for (int i = 0; i < surfaces.Count; i++)
            {
                double cellW = (double)width / columns, cellH = (double)height / rows;
                double aspect = Math.Clamp(surfaces[i].Aspect, .15, 7);
                double padding = surfaces.Count == 1 ? 1 : .94;
                double w = cellW * padding, h = w / aspect;
                if (h > cellH * padding) { h = cellH * padding; w = h * aspect; }
                result.Add(new(surfaces[i].Id, surfaces[i].Name, new ArtworkRegion(
                    ((i % columns) * cellW + (cellW - w) / 2) / width,
                    ((i / columns) * cellH + (cellH - h) / 2) / height, w / width, h / height)));
            }
            return result;
        }
    }
}
