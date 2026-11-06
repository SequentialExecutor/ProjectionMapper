using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper.Models;
using ProjectionMapper.Rendering;
using ProjectionMapper.Utilities;
using Pen = System.Windows.Media.Pen;

namespace ProjectionMapper.Services
{
    public static class ArtworkImaging
    {
        public static Task<T> OnStaAsync<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => { try { completion.SetResult(work()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true, Name = "Artwork image processing" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
        }
        public static BitmapSource Decode(string imageBase64)
        {
            var bytes = Convert.FromBase64String(imageBase64);
            using var input = new MemoryStream(bytes);
            var frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096 || (long)frame.PixelWidth * frame.PixelHeight > 8_400_000)
                throw new InvalidDataException("Generated image dimensions exceed supported limits.");
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int stride = frame.PixelWidth * 4; var pixels = new byte[stride * frame.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
            var clean = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride); clean.Freeze();
            return clean;
        }
        private static string EncodePng(BitmapSource frame)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
            using var stream = new MemoryStream(); encoder.Save(stream); return Convert.ToBase64String(stream.ToArray());
        }
        public static ArtworkAsset Normalize(GeneratedArtwork image, ArtworkRequest request)
        {
            var frame = Decode(image.ImageBase64);
            var thumbnail = new TransformedBitmap(frame, new ScaleTransform(256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight), 256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight)));
            thumbnail.Freeze(); string encoded = EncodePng(frame);
            string title = request.Prompt.Trim().Replace('\r', ' ').Replace('\n', ' ');
            var asset = new ArtworkAsset
            {
                Name = title.Length > 80 ? title[..80] : title, ImageBase64 = encoded,
                ThumbnailBase64 = EncodePng(thumbnail), Sha256 = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(encoded))),
                Width = frame.PixelWidth, Height = frame.PixelHeight, Provider = request.Provider, Model = request.Model,
                Prompt = request.Prompt, Style = request.Style, Palette = request.Palette, EffectivePrompt = image.EffectivePrompt,
                ProviderRequestId = image.RequestId, Quality = request.Provider == ArtworkProvider.OpenAI ? request.Quality : "model-default", Background = request.Background,
                RequestedSize = request.Size, Layout = request.Layout, References = request.References
            };
            ArtworkRules.ValidateAsset(asset); return asset;
        }
        public static ArtworkReference Reference(ArtworkAsset asset)
        {
            var image = Decode(asset.ImageBase64);
            double scale = Math.Min(1, 1024.0 / Math.Max(image.PixelWidth, image.PixelHeight));
            var resized = new TransformedBitmap(image, new ScaleTransform(scale, scale)); resized.Freeze();
            string data = EncodePng(resized);
            return new ArtworkReference(asset.Name + ".png", "image/png", data,
                Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(data))));
        }
        public static BitmapSource Crop(ArtworkAsset asset, ArtworkRegion region)
        {
            ArtworkRules.ValidateRegion(region);
            var image = Decode(asset.ImageBase64);
            int x = Math.Clamp((int)Math.Round(region.X * image.PixelWidth), 0, image.PixelWidth - 1);
            int y = Math.Clamp((int)Math.Round(region.Y * image.PixelHeight), 0, image.PixelHeight - 1);
            int width = Math.Clamp((int)Math.Round(region.Width * image.PixelWidth), 1, image.PixelWidth - x);
            int height = Math.Clamp((int)Math.Round(region.Height * image.PixelHeight), 1, image.PixelHeight - y);
            var cropped = new CroppedBitmap(image, new Int32Rect(x, y, width, height)); cropped.Freeze(); return cropped;
        }
        public static string Guide(ArtworkRequest request)
        {
            var size = request.Size.Split('x'); int width = int.Parse(size[0]), height = int.Parse(size[1]);
            RenderTargetBitmap bitmap;
            lock (WpfCompositionGate.SyncRoot)
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                    foreach (var cell in request.Layout)
                        drawing.DrawRectangle(Brushes.LightGray, new Pen(Brushes.White, 2), new Rect(cell.Region.X * width, cell.Region.Y * height, cell.Region.Width * width, cell.Region.Height * height));
                }
                bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            }
            return EncodePng(bitmap);
        }
        public static async Task<string> DownloadAsync(ArtworkAsset asset, string folder)
        {
            ArtworkRules.ValidateAsset(asset);
            string full = Path.GetFullPath(folder); Directory.CreateDirectory(full);
            string path = Path.Combine(full, asset.Id + ".png");
            if (File.Exists(path))
            {
                await using var existing = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(existing).ConfigureAwait(false));
                if (string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase)) return path;
                path = Path.Combine(full, asset.Id + "-" + Guid.NewGuid().ToString("N")[..8] + ".png");
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, Convert.FromBase64String(asset.ImageBase64)).ConfigureAwait(false);
                File.Move(temporary, path); return path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
