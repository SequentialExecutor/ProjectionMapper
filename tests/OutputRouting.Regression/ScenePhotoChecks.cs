using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;

internal static class ScenePhotoChecks
{
    public static void Run()
    {
        string path = Path.Combine(Path.GetTempPath(), "ProjectionMapper-exif-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            // A real JPEG with EXIF orientation 6: left red/right blue becomes top red/bottom blue.
            var pixels = new byte[16 * 8 * 4];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 16; x++)
                {
                    int offset = (y * 16 + x) * 4;
                    pixels[offset + (x < 8 ? 2 : 0)] = 255; pixels[offset + 3] = 255;
                }
            var image = BitmapSource.Create(16, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            image.Freeze();
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            metadata.SetQuery("/app1/ifd/{ushort=315}", "Metadata stripping check");
            var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
            encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
            using (var file = File.Create(path)) encoder.Save(file);
            var photo = ScenePhotoService.ImportAsync(path).GetAwaiter().GetResult();
            PhotoGeometry.ValidateMap(photo);
            if (photo.Width != 8 || photo.Height != 16) throw new Exception("EXIF dimensions were not corrected.");
            var decoded = ScenePhotoService.Decode(photo);
            var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            var normalized = new byte[8 * 16 * 4]; converted.CopyPixels(normalized, 8 * 4, 0);
            int top = (2 * 8 + 4) * 4, bottom = (13 * 8 + 4) * 4;
            if (normalized[top + 2] < 180 || normalized[bottom] < 180) throw new Exception("EXIF pixel orientation was not corrected.");
            // BitmapImage does not expose metadata; inspect the encoded JPEG frame itself.
            using var normalizedStream = new MemoryStream(Convert.FromBase64String(photo.ImageBase64));
            var normalizedFrame = BitmapFrame.Create(normalizedStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (normalizedFrame.Metadata is BitmapMetadata normalizedMetadata && normalizedMetadata.ContainsQuery("/app1/ifd/{ushort=315}"))
                throw new Exception("Original metadata was retained in the upload image.");
            if (photo.OriginalFileName != Path.GetFileName(path)) throw new Exception("Photo provenance should contain only the file name.");
            Console.WriteLine("Scene photo orientation, normalization, and metadata-stripping checks passed.");
        }
        finally { File.Delete(path); }
    }
}
