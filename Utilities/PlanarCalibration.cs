using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ProjectionMapper.Models;

namespace ProjectionMapper.Utilities
{
    public sealed record CalibrationFit(double[] Matrix, double FitRmsPixels, double FitMaxPixels, double ValidationRmsPixels, double ValidationMaxPixels,
        IReadOnlyDictionary<int, double> Errors, bool Reliable, string Explanation);
    /// <summary>Planar geometry in normalized photo/output coordinates. Error metrics are always native projector pixels.</summary>
    public static class PlanarCalibration
    {
        public static PhotoPoint Transform(double[] h, PhotoPoint p)
        {
            if (h.Length != 9 || h.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Invalid planar transform.");
            double w = h[6] * p.X + h[7] * p.Y + h[8];
            if (!double.IsFinite(w) || Math.Abs(w) < 1e-8) throw new InvalidDataException("The point is too close to the transform's horizon. Reshoot from a less oblique angle.");
            double x = (h[0] * p.X + h[1] * p.Y + h[2]) / w, y = (h[3] * p.X + h[4] * p.Y + h[5]) / w;
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidDataException("The transform produced a nonfinite point.");
            return new(x, y);
        }
        public static double[] Inverse(double[] h)
        {
            if (h.Length != 9) throw new InvalidDataException("Invalid transform.");
            double a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], i = h[7], j = h[8];
            double determinant = a * (e * j - f * i) - b * (d * j - f * g) + c * (d * i - e * g);
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-10) throw new InvalidDataException("The transform is singular.");
            return new[] { e * j - f * i, c * i - b * j, b * f - c * e, f * g - d * j, a * j - c * g, c * d - a * f, d * i - e * g, b * g - a * i, a * e - b * d }.Select(v => v / determinant).ToArray();
        }
        private static double[] Multiply(double[] a, double[] b)
        {
            var result = new double[9]; for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) for (int k = 0; k < 3; k++) result[r * 3 + c] += a[r * 3 + k] * b[k * 3 + c]; return result;
        }
        /// <summary>Reorthogonalized QR least squares. Coordinates are centered/scaled; normal equations are intentionally avoided.</summary>
        private static double[] Solve(IReadOnlyList<(PhotoPoint Photo, PhotoPoint Output)> pairs)
        {
            int rows = pairs.Count * 2; var columns = Enumerable.Range(0, 8).Select(_ => new double[rows]).ToArray(); var rhs = new double[rows];
            for (int k = 0; k < pairs.Count; k++)
            {
                double x = 2 * pairs[k].Photo.X - 1, y = 2 * pairs[k].Photo.Y - 1, u = 2 * pairs[k].Output.X - 1, v = 2 * pairs[k].Output.Y - 1; int r = k * 2;
                columns[0][r] = x; columns[1][r] = y; columns[2][r] = 1; columns[6][r] = -u * x; columns[7][r] = -u * y; rhs[r] = u;
                columns[3][r + 1] = x; columns[4][r + 1] = y; columns[5][r + 1] = 1; columns[6][r + 1] = -v * x; columns[7][r + 1] = -v * y; rhs[r + 1] = v;
            }
            var upper = new double[8, 8]; var projected = new double[8];
            for (int c = 0; c < 8; c++)
            {
                for (int pass = 0; pass < 2; pass++) for (int prior = 0; prior < c; prior++)
                {
                    double dot = 0; for (int r = 0; r < rows; r++) dot += columns[prior][r] * columns[c][r]; upper[prior, c] += dot;
                    for (int r = 0; r < rows; r++) columns[c][r] -= dot * columns[prior][r];
                }
                double norm = Math.Sqrt(columns[c].Sum(v => v * v));
                if (!double.IsFinite(norm) || norm < 1e-7) throw new InvalidDataException("Markers do not constrain a stable plane. Spread them across the surface.");
                upper[c, c] = norm;
                for (int r = 0; r < rows; r++) { columns[c][r] /= norm; projected[c] += columns[c][r] * rhs[r]; }
            }
            var diagonal = Enumerable.Range(0, 8).Select(c => upper[c, c]).ToArray();
            if (diagonal.Max() / diagonal.Min() > 1e7) throw new InvalidDataException("The planar fit is ill-conditioned. Use a clearer, less oblique photograph.");
            var solution = new double[9]; solution[8] = 1;
            for (int c = 7; c >= 0; c--) { double value = projected[c]; for (int k = c + 1; k < 8; k++) value -= upper[c, k] * solution[k]; solution[c] = value / upper[c, c]; }
            var normalized = Multiply(new double[] { .5, 0, .5, 0, .5, .5, 0, 0, 1 }, Multiply(solution, new double[] { 2, 0, -1, 0, 2, -1, 0, 0, 1 }));
            if (Math.Abs(normalized[8]) < 1e-8) throw new InvalidDataException("This viewing angle is unstable. Reshoot the photo.");
            return normalized.Select(v => v / normalized[8]).ToArray();
        }
        public static void ValidatePattern(CameraPattern pattern)
        {
            var r = pattern.Region;
            if (!Guid.TryParseExact(pattern.Id, "N", out _) || pattern.Dictionary != "DICT_6X6_250" || pattern.Width < 320 || pattern.Height < 320 || pattern.Width > 8192 || pattern.Height > 8192 || (long)pattern.Width * pattern.Height > 33_600_000 ||
                new[] { r.X, r.Y, r.Width, r.Height }.Any(v => !double.IsFinite(v)) || r.X < 0 || r.Y < 0 || r.Width < 320 || r.Height < 320 || r.X + r.Width > pattern.Width || r.Y + r.Height > pattern.Height || pattern.Markers == null || pattern.Markers.Count != 9)
                throw new InvalidDataException("Use a valid 3×3 marker region of at least 320×320 native output pixels.");
            var ids = new HashSet<int>();
            foreach (var marker in pattern.Markers)
                if (marker == null || marker.Id < 0 || marker.Id >= 250 || !ids.Add(marker.Id) || marker.Size < 32 || marker.Size > Math.Min(r.Width, r.Height) / 5 || marker.X < r.X || marker.Y < r.Y || marker.X + marker.Size > r.X + r.Width || marker.Y + marker.Size > r.Y + r.Height)
                    throw new InvalidDataException("Marker IDs and native-pixel placements are invalid.");
        }
        private static List<PhotoPoint> Hull(IEnumerable<PhotoPoint> source)
        {
            var points = source.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList(); if (points.Count < 3) throw new InvalidDataException("Markers need two-dimensional coverage.");
            double Cross(PhotoPoint a, PhotoPoint b, PhotoPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            var hull = new List<PhotoPoint>();
            foreach (var p in points) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
            int lower = hull.Count;
            for (int i = points.Count - 2; i >= 0; i--) { var p = points[i]; while (hull.Count > lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
            hull.RemoveAt(hull.Count - 1); return hull;
        }
        private static double Area(IReadOnlyList<PhotoPoint> points) => Math.Abs(Enumerable.Range(0, points.Count).Sum(i => points[i].X * points[(i + 1) % points.Count].Y - points[(i + 1) % points.Count].X * points[i].Y)) / 2;
        public static CalibrationFit Fit(CameraPattern pattern, IReadOnlyList<MarkerObservation> observations, double tolerancePixels)
        {
            ValidatePattern(pattern);
            if (!double.IsFinite(tolerancePixels) || tolerancePixels < .25 || tolerancePixels > 25) throw new InvalidDataException("Choose an error tolerance between 0.25 and 25 native projector pixels.");
            if (observations == null || observations.Count > 9 || observations.Any(m => m == null || !pattern.Markers.Any(p => p.Id == m.Id)) || observations.Select(m => m.Id).Distinct().Count() != observations.Count)
                throw new InvalidDataException("Each observation must reference one unique marker from the current pattern.");
            var used = observations.Where(m => m.Enabled).ToList(); var training = used.Where(m => !m.ValidationOnly).ToList(); var validation = used.Where(m => m.ValidationOnly).ToList();
            if (training.Count < 5 || validation.Count < 2) throw new InvalidDataException("Use at least five fit markers and two independent validation markers.");
            foreach (var m in observations) if (!double.IsFinite(m.Photo.X) || !double.IsFinite(m.Photo.Y) || m.Photo.X < 0 || m.Photo.X > 1 || m.Photo.Y < 0 || m.Photo.Y > 1) throw new InvalidDataException("Marker positions must be inside this photograph.");
            var lookup = pattern.Markers.ToDictionary(m => m.Id);
            PhotoPoint Local(CalibrationMarker marker) => new((marker.Center.X - pattern.Region.X) / pattern.Region.Width, (marker.Center.Y - pattern.Region.Y) / pattern.Region.Height);
            if (Area(Hull(training.Select(m => Local(lookup[m.Id])))) < .3 || Area(Hull(training.Select(m => m.Photo))) < .005)
                throw new InvalidDataException("Fit markers must cover the plane, rather than a small or nearly straight cluster.");
            var matrix = Solve(training.Select(m => (m.Photo, new PhotoPoint(lookup[m.Id].Center.X / pattern.Width, lookup[m.Id].Center.Y / pattern.Height))).ToList());
            Inverse(matrix);
            var errors = used.ToDictionary(m => m.Id, m =>
            {
                var projected = Transform(matrix, m.Photo); var expected = lookup[m.Id].Center;
                return Math.Sqrt(Math.Pow(projected.X * pattern.Width - expected.X, 2) + Math.Pow(projected.Y * pattern.Height - expected.Y, 2));
            });
            double rms = Math.Sqrt(training.Average(m => errors[m.Id] * errors[m.Id])), max = training.Max(m => errors[m.Id]);
            double validationRms = Math.Sqrt(validation.Average(m => errors[m.Id] * errors[m.Id])), validationMax = validation.Max(m => errors[m.Id]);
            bool reliable = max <= tolerancePixels && validationMax <= tolerancePixels;
            int worst = errors.MaxBy(e => e.Value).Key;
            return new(matrix, rms, max, validationRms, validationMax, errors, reliable,
                reliable ? "Fit and independent validation meet the selected tolerance. Inspect the target overlay and verify on the physical surface." : $"Rejected: marker {worst} has {errors[worst]:0.###} px error. Correct matches, restrict the planar region, or reshoot; no mapping will be applied.");
        }
        public static Vector2[] MapTarget(CameraPattern pattern, IReadOnlyList<MarkerObservation> observations, CalibrationFit fit, IReadOnlyList<PhotoPoint> corners, bool allowLimitedExtrapolation)
        {
            if (!fit.Reliable) throw new InvalidDataException("The marker fit has not passed validation.");
            if (corners.Count != 4) throw new InvalidDataException("Mark content TL, TR, BR, BL in the calibration photograph.");
            PhotoGeometry.ValidatePolygon(corners);
            var photoQuad = new[] { corners[0], corners[1], corners[3], corners[2] }.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray();
            if (!QuadGeometry.IsValid(photoQuad)) throw new InvalidDataException("The target must be a convex planar quadrilateral.");
            var used = observations.Where(m => m.Enabled).ToList();
            var signs = used.Select(m => Math.Sign(fit.Matrix[6] * m.Photo.X + fit.Matrix[7] * m.Photo.Y + fit.Matrix[8])).ToHashSet();
            if (signs.Count != 1 || corners.Any(p => !signs.Contains(Math.Sign(fit.Matrix[6] * p.X + fit.Matrix[7] * p.Y + fit.Matrix[8])))) throw new InvalidDataException("The target crosses the projective horizon.");
            PhotoPoint Local(PhotoPoint output) => new((output.X * pattern.Width - pattern.Region.X) / pattern.Region.Width, (output.Y * pattern.Height - pattern.Region.Y) / pattern.Region.Height);
            var lookup = pattern.Markers.ToDictionary(m => m.Id);
            var hull = Hull(used.Select(m => Local(new(lookup[m.Id].Center.X / pattern.Width, lookup[m.Id].Center.Y / pattern.Height))));
            var mapped = corners.Select(p => Transform(fit.Matrix, p)).ToArray();
            foreach (var point in mapped)
            {
                if (point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1) throw new InvalidDataException("The target falls outside the selected projector. No coordinates are silently clamped.");
                var local = Local(point);
                if (PhotoGeometry.Contains(hull, local)) continue;
                double distance = double.MaxValue;
                for (int i = 0; i < hull.Count; i++)
                {
                    var a = hull[i]; var b = hull[(i + 1) % hull.Count]; double dx = b.X - a.X, dy = b.Y - a.Y;
                    double t = Math.Clamp(((local.X - a.X) * dx + (local.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
                    distance = Math.Min(distance, Math.Sqrt(Math.Pow(local.X - a.X - t * dx, 2) + Math.Pow(local.Y - a.Y - t * dy, 2)));
                }
                if (!allowLimitedExtrapolation || distance > .1) throw new InvalidDataException("Target corners extend beyond marker coverage. Reshoot with wider coverage, or explicitly allow at most 10% extrapolation and verify the edges.");
            }
            var result = new[] { mapped[0], mapped[1], mapped[3], mapped[2] }.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray();
            if (!QuadGeometry.IsValid(result)) throw new InvalidDataException("The proposed projector quad is invalid."); return result;
        }
        public static void ValidatePhoto(PhotoMappingModel photo)
        {
            PhotoGeometry.ValidateMap(photo);
            if (photo.Width < 320 || photo.Height < 320 || photo.Width > 2048 || photo.Height > 2048) throw new InvalidDataException("Calibration photos need 320–2048 pixels per axis.");
            var bytes = Convert.FromBase64String(photo.ImageBase64); int offset = 2;
            while (offset + 4 < bytes.Length)
            {
                if (bytes[offset++] != 0xff) break;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) break; int marker = bytes[offset++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
                if (offset + 2 > bytes.Length) break; int length = bytes[offset] * 256 + bytes[offset + 1];
                if (length < 2 || offset + length > bytes.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    if (length < 8) break;
                    int height = bytes[offset + 3] * 256 + bytes[offset + 4], width = bytes[offset + 5] * 256 + bytes[offset + 6];
                    if (bytes[offset + 2] != 8 || width != photo.Width || height != photo.Height) throw new InvalidDataException("Calibration photo dimensions/bit depth do not match its normalized JPEG header.");
                    return;
                }
                offset += length;
            }
            throw new InvalidDataException("The calibration photo has no supported JPEG frame header.");
        }
        public static void ValidateRecord(CameraCalibrationRecord record)
        {
            if (record == null || !Guid.TryParseExact(record.Id, "N", out _) || string.IsNullOrWhiteSpace(record.MeshId) || record.Output == null || record.Output.Index < 0 || string.IsNullOrWhiteSpace(record.Output.DeviceName) || string.IsNullOrWhiteSpace(record.ComputerId) || !record.OperatorConfirmed || record.Photo == null || record.Pattern == null || record.Matches == null || record.OutputCorners == null || record.OutputCorners.Length != 8 || record.PhotoToProjector == null || record.PhotoToProjector.Length != 9 || record.TargetPhotoCorners == null)
                throw new InvalidDataException("Calibration evidence is incomplete.");
            ValidatePhoto(record.Photo);
            if (record.Pattern.Width != record.Output.RenderWidth || record.Pattern.Height != record.Output.RenderHeight) throw new InvalidDataException("Calibration dimensions do not match the saved projector.");
            var fit = Fit(record.Pattern, record.Matches, record.TolerancePixels); var quad = MapTarget(record.Pattern, record.Matches, fit, record.TargetPhotoCorners, record.AllowLimitedExtrapolation);
            bool Near(double a, double b) => double.IsFinite(a) && Math.Abs(a - b) < 1e-5;
            if (!Near(record.FitRmsPixels, fit.FitRmsPixels) || !Near(record.ValidationRmsPixels, fit.ValidationRmsPixels) || !Near(record.ValidationMaxPixels, fit.ValidationMaxPixels) || record.PhotoToProjector.Where((v, i) => !Near(v, fit.Matrix[i])).Any() || record.OutputCorners.Where((v, i) => !Near(v, i % 2 == 0 ? quad[i / 2].X : quad[i / 2].Y)).Any())
                throw new InvalidDataException("Saved calibration results do not match their marker evidence.");
        }
    }
}
