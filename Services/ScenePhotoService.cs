using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>Loads orientation-corrected scene references and strips metadata before any upload.</summary>
    public static class ScenePhotoService
    {
        public static Task<PhotoMappingModel> ImportAsync(string path)
        {
            var completion = new TaskCompletionSource<PhotoMappingModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try { completion.SetResult(Import(path)); }
                catch (Exception ex) { completion.SetException(ex); }
            }) { IsBackground = true, Name = "Scene photo normalization" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return completion.Task;
        }

        public static PhotoMappingModel Import(string path)
        {
            var file = new FileInfo(path);
            if (file.Length > 25_000_000) throw new InvalidDataException("Choose a photo smaller than 25 MB.");
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if ((long)frame.PixelWidth * frame.PixelHeight > 64_000_000) throw new InvalidDataException("Choose a photo with at most 64 megapixels.");
            ushort orientation = 1;
            if (frame.Metadata is BitmapMetadata metadata)
            {
                try { if (metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value) orientation = value; }
                catch (NotSupportedException) { /* Some image codecs expose no EXIF orientation. */ }
            }
            int width = frame.PixelWidth;
            int targetWidth = Math.Max(1, (int)(width * Math.Min(1, 2048.0 / Math.Max(frame.PixelWidth, frame.PixelHeight))));
            stream.Position = 0;
            var decoded = new BitmapImage();
            decoded.BeginInit(); decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.DecodePixelWidth = targetWidth; decoded.StreamSource = stream; decoded.EndInit(); decoded.Freeze();
            var transforms = new TransformGroup();
            if (orientation is 2 or 5 or 7) transforms.Children.Add(new ScaleTransform(-1, 1));
            if (orientation == 4) transforms.Children.Add(new ScaleTransform(1, -1));
            double angle = orientation switch { 3 => 180, 5 => 270, 6 => 90, 7 => 90, 8 => 270, _ => 0 };
            if (angle != 0) transforms.Children.Add(new RotateTransform(angle));
            BitmapSource normalized = decoded;
            if (transforms.Children.Count > 0) { normalized = new TransformedBitmap(decoded, transforms); normalized.Freeze(); }
            if (normalized.PixelHeight > 2048)
            {
                double scale = 2048.0 / normalized.PixelHeight;
                normalized = new TransformedBitmap(normalized, new ScaleTransform(scale, scale)); normalized.Freeze();
            }
            // Rebuild from pixels so no original EXIF/GPS metadata can propagate through
            // bitmap-frame wrappers or codec-specific metadata inheritance.
            var converted = new FormatConvertedBitmap(normalized, PixelFormats.Bgra32, null, 0);
            int stride = normalized.PixelWidth * 4;
            var pixels = new byte[stride * normalized.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            var clean = BitmapSource.Create(normalized.PixelWidth, normalized.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            clean.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(clean));
            using var output = new MemoryStream(); encoder.Save(output);
            if (output.Length > 6_000_000) throw new InvalidDataException("The normalized photo is too large. Choose a smaller image.");
            var bytes = output.ToArray();
            return new PhotoMappingModel { ImageBase64 = Convert.ToBase64String(bytes), OriginalFileName = file.Name,
                ImageSha256 = Convert.ToHexString(SHA256.HashData(bytes)), Width = normalized.PixelWidth, Height = normalized.PixelHeight };
        }
        public static BitmapSource Decode(PhotoMappingModel photo)
        {
            using var stream = new MemoryStream(Convert.FromBase64String(photo.ImageBase64));
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
    }
}
