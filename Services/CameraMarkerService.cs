using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.Aruco;
using ProjectionMapper.Models;
using ProjectionMapper.Utilities;
using CvRect = OpenCvSharp.Rect;

namespace ProjectionMapper.Services
{
    public sealed record MarkerDetection(IReadOnlyList<MarkerObservation> Matches, IReadOnlyList<int> DuplicateIds, IReadOnlyList<int> UnrelatedIds);
    /// <summary>Local coded-marker imaging. Each operation owns its native objects; no provider, credential or network access.</summary>
    public static class CameraMarkerService
    {
        public static CameraPattern NewPattern(int width, int height, CalibrationRegion region)
        {
            var result = new CameraPattern { Width = width, Height = height, Region = region };
            var ids = new HashSet<int>(); while (ids.Count < 9) ids.Add(RandomNumberGenerator.GetInt32(250));
            int size = Math.Max(32, 8 * (int)(Math.Min(region.Width, region.Height) * .09 / 8));
            int index = 0;
            foreach (int id in ids)
            {
                int row = index / 3, column = index % 3; double x = region.X + region.Width * (.08 + .42 * column), y = region.Y + region.Height * (.08 + .42 * row);
                result.Markers.Add(new(id, (int)Math.Round(x - (size - 1) / 2.0), (int)Math.Round(y - (size - 1) / 2.0), size, index is 1 or 3 or 5)); index++;
            }
            PlanarCalibration.ValidatePattern(result); return result;
        }
        public static Mat Render(CameraPattern pattern)
        {
            PlanarCalibration.ValidatePattern(pattern);
            var board = new Mat(pattern.Height, pattern.Width, MatType.CV_8UC1, Scalar.Black);
            try
            {
                using var dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryType.Dict6X6_250);
                foreach (var marker in pattern.Markers)
                {
                    int quiet = Math.Max(6, marker.Size / 5);
                    var border = new CvRect(Math.Max(0, marker.X - quiet), Math.Max(0, marker.Y - quiet), Math.Min(pattern.Width, marker.X + marker.Size + quiet) - Math.Max(0, marker.X - quiet), Math.Min(pattern.Height, marker.Y + marker.Size + quiet) - Math.Max(0, marker.Y - quiet));
                    Cv2.Rectangle(board, border, Scalar.White, -1);
                    using var image = new Mat(); dictionary.GenerateImageMarker(marker.Id, marker.Size, image);
                    using var destination = new Mat(board, new CvRect(marker.X, marker.Y, marker.Size, marker.Size)); image.CopyTo(destination);
                    bool bottomRow = marker.Center.Y > pattern.Region.Y + pattern.Region.Height * .7;
                    int labelY = bottomRow ? marker.Y - quiet - 5 : marker.Y + marker.Size + quiet + 16;
                    Cv2.PutText(board, marker.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), new OpenCvSharp.Point(marker.X, labelY), HersheyFonts.HersheySimplex, Math.Clamp(marker.Size / 160.0, .4, 1.2), Scalar.White, 1);
                }
                return board;
            }
            catch { board.Dispose(); throw; }
        }
        public static BitmapSource Bitmap(CameraPattern pattern)
        {
            using var image = Render(pattern); var bytes = new byte[pattern.Width * pattern.Height]; Marshal.Copy(image.Data, bytes, 0, bytes.Length);
            var bitmap = BitmapSource.Create(pattern.Width, pattern.Height, 96, 96, PixelFormats.Gray8, null, bytes, pattern.Width); bitmap.Freeze(); return bitmap;
        }
        public static BitmapSource Outline(CameraPattern pattern, System.Numerics.Vector2[] quad)
        {
            if (!QuadGeometry.IsValid(quad)) throw new InvalidDataException("Invalid target quad.");
            using var image = new Mat(pattern.Height, pattern.Width, MatType.CV_8UC1, Scalar.Black);
            int[] order = { 0, 1, 3, 2 };
            var points = order.Select(i => new OpenCvSharp.Point((int)Math.Round(quad[i].X * pattern.Width), (int)Math.Round(quad[i].Y * pattern.Height))).ToArray();
            Cv2.Polylines(image, new[] { points }, true, Scalar.White, 3);
            string[] labels = { "TL", "TR", "BR", "BL" };
            for (int i = 0; i < points.Length; i++)
            {
                Cv2.Circle(image, points[i], 8, Scalar.White, 2);
                Cv2.PutText(image, labels[i], new OpenCvSharp.Point(Math.Clamp(points[i].X + 10, 0, pattern.Width - 70), Math.Clamp(points[i].Y + 25, 30, pattern.Height - 5)), HersheyFonts.HersheySimplex, .8, Scalar.White, 2);
            }
            var bytes = new byte[pattern.Width * pattern.Height]; Marshal.Copy(image.Data, bytes, 0, bytes.Length);
            var bitmap = BitmapSource.Create(pattern.Width, pattern.Height, 96, 96, PixelFormats.Gray8, null, bytes, pattern.Width); bitmap.Freeze(); return bitmap;
        }
        public static async Task ExportAsync(CameraPattern pattern, string path, CancellationToken token = default)
        {
            byte[] bytes = await Task.Run(() => { using var image = Render(pattern); return image.ImEncode(".png"); }, token);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, bytes, token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static void ValidatePhoto(PhotoMappingModel photo) => PlanarCalibration.ValidatePhoto(photo);
        public static MarkerDetection Detect(CameraPattern pattern, PhotoMappingModel photo, CancellationToken token = default)
        {
            PlanarCalibration.ValidatePattern(pattern); ValidatePhoto(photo);
            var bytes = Convert.FromBase64String(photo.ImageBase64); token.ThrowIfCancellationRequested();
            using var image = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
            if (image.Empty()) throw new InvalidDataException("The calibration photo could not be decoded.");
            using var dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryType.Dict6X6_250);
            var parameters = new DetectorParameters { CornerRefinementMethod = CornerRefineMethod.Subpix };
            using var detector = new ArucoDetector(dictionary, parameters, new RefineParameters());
            detector.DetectMarkers(image, out var corners, out var ids, out _); token.ThrowIfCancellationRequested();
            var repeated = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            var unknown = ids.Where(id => pattern.Markers.All(m => m.Id != id)).Distinct().ToList();
            var matches = new List<MarkerObservation>();
            for (int index = 0; index < ids.Length; index++)
            {
                var marker = pattern.Markers.FirstOrDefault(m => m.Id == ids[index]);
                if (marker == null || repeated.Contains(marker.Id)) continue;
                // The intersection of opposite diagonals is projectively invariant; the arithmetic corner mean is not.
                var p = corners[index]; double ax = p[2].X - p[0].X, ay = p[2].Y - p[0].Y, bx = p[3].X - p[1].X, by = p[3].Y - p[1].Y;
                double cross = ax * by - ay * bx; if (Math.Abs(cross) < 1e-8) continue;
                double t = ((p[1].X - p[0].X) * by - (p[1].Y - p[0].Y) * bx) / cross;
                matches.Add(new() { Id = marker.Id, Photo = new((p[0].X + t * ax) / photo.Width, (p[0].Y + t * ay) / photo.Height), ValidationOnly = marker.ValidationOnly, Origin = "Detected" });
            }
            return new(matches, repeated, unknown);
        }
    }
}
