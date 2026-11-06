using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using ProjectionMapper.Models;

namespace ProjectionMapper.Utilities
{
    public static class PhotoGeometry
    {
        private const double Epsilon = 1e-9;
        private static double Cross(PhotoPoint a, PhotoPoint b, PhotoPoint c) =>
            (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        private static bool OnSegment(PhotoPoint a, PhotoPoint b, PhotoPoint p) =>
            Math.Abs(Cross(a, b, p)) < Epsilon && p.X >= Math.Min(a.X, b.X) - Epsilon && p.X <= Math.Max(a.X, b.X) + Epsilon &&
            p.Y >= Math.Min(a.Y, b.Y) - Epsilon && p.Y <= Math.Max(a.Y, b.Y) + Epsilon;
        private static bool Intersects(PhotoPoint a, PhotoPoint b, PhotoPoint c, PhotoPoint d)
        {
            double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
            return (((abC > Epsilon && abD < -Epsilon) || (abC < -Epsilon && abD > Epsilon)) &&
                ((cdA > Epsilon && cdB < -Epsilon) || (cdA < -Epsilon && cdB > Epsilon))) ||
                OnSegment(a, b, c) || OnSegment(a, b, d) || OnSegment(c, d, a) || OnSegment(c, d, b);
        }
        public static void ValidatePolygon(IReadOnlyList<PhotoPoint>? points)
        {
            if (points == null || points.Count < 3 || points.Count > 32)
                throw new InvalidDataException("A surface needs 3–32 perimeter vertices.");
            double area = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || a.X < 0 || a.X > 1 || a.Y < 0 || a.Y > 1)
                    throw new InvalidDataException("Photo coordinates must be finite and between 0 and 1.");
                if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < Epsilon)
                    throw new InvalidDataException("Adjacent vertices must be distinct.");
                area += a.X * b.Y - b.X * a.Y;
                for (int j = i + 1; j < points.Count; j++)
                {
                    if (j == (i + 1) % points.Count || (j + 1) % points.Count == i) continue;
                    if (Intersects(a, b, points[j], points[(j + 1) % points.Count]))
                        throw new InvalidDataException("Surface edges cannot cross or touch themselves.");
                }
            }
            if (Math.Abs(area) < 1e-8) throw new InvalidDataException("A surface must enclose a visible area.");
        }
        public static bool Contains(IReadOnlyList<PhotoPoint> points, PhotoPoint p)
        {
            bool inside = false;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                var a = points[i]; var b = points[j];
                if (OnSegment(a, b, p)) return true;
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }
        public static bool Overlaps(IReadOnlyList<PhotoPoint> a, IReadOnlyList<PhotoPoint> b)
        {
            if (a.Count < 3 || b.Count < 3) return false;
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < b.Count; j++)
                    if (Intersects(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
            return Contains(a, b[0]) || Contains(b, a[0]);
        }
        public static (List<PhotoPoint> First, List<PhotoPoint> Second) Split(IReadOnlyList<PhotoPoint> points, int first, int second)
        {
            ValidatePolygon(points);
            if (first < 0 || second < 0 || first >= points.Count || second >= points.Count ||
                first == second || (first + 1) % points.Count == second || (second + 1) % points.Count == first)
                throw new InvalidDataException("Choose two non-adjacent vertices for the split.");
            var a = points[first]; var b = points[second];
            if (!Contains(points, new PhotoPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2)))
                throw new InvalidDataException("The split must stay inside the surface.");
            for (int i = 0; i < points.Count; i++)
            {
                int j = (i + 1) % points.Count;
                if (i == first || j == first || i == second || j == second) continue;
                if (Intersects(a, b, points[i], points[j])) throw new InvalidDataException("The split crosses another edge.");
            }
            List<PhotoPoint> Walk(int start, int end)
            {
                var result = new List<PhotoPoint>();
                for (int i = start; ; i = (i + 1) % points.Count) { result.Add(points[i]); if (i == end) return result; }
            }
            var left = Walk(first, second); var right = Walk(second, first);
            ValidatePolygon(left); ValidatePolygon(right);
            return (left, right);
        }
        public static Vector2[] ToOutputQuad(IReadOnlyList<PhotoPoint> points)
        {
            ValidatePolygon(points);
            if (points.Count != 4) throw new InvalidDataException("Only four-corner surfaces can become projection meshes in this version.");
            double cx = points.Average(p => p.X), cy = points.Average(p => p.Y);
            var ordered = points.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToArray();
            int start = Enumerable.Range(0, 4).OrderBy(i => ordered[i].X + ordered[i].Y).ThenBy(i => ordered[i].Y).First();
            var perimeter = Enumerable.Range(0, 4).Select(i => ordered[(start + i) % 4]).ToArray();
            var quad = new[] { perimeter[0], perimeter[1], perimeter[3], perimeter[2] }.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray();
            if (!QuadGeometry.IsValid(quad)) throw new InvalidDataException("Projection meshes must be convex quadrilaterals.");
            return quad;
        }
        public static void ValidateMap(PhotoMappingModel map)
        {
            if (map.Width < 1 || map.Height < 1 || map.Width > 4096 || map.Height > 4096 || map.ImageBase64 == null || map.ImageBase64.Length > 8_000_000)
                throw new InvalidDataException("The scene reference exceeds supported image limits.");
            byte[] image;
            try { image = Convert.FromBase64String(map.ImageBase64); }
            catch (FormatException) { throw new InvalidDataException("The scene reference is not valid base64."); }
            if (image.Length < 3 || image[0] != 0xff || image[1] != 0xd8 || image[2] != 0xff)
                throw new InvalidDataException("The scene reference must be a normalized JPEG image.");
            if (!string.Equals(map.ImageSha256, Convert.ToHexString(SHA256.HashData(image)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The scene reference checksum does not match its image.");
            if (map.Surfaces == null || map.Surfaces.Count > 64) throw new InvalidDataException("A scene supports up to 64 surfaces.");
            var ids = new HashSet<string>();
            foreach (var surface in map.Surfaces)
            {
                if (surface == null || string.IsNullOrWhiteSpace(surface.Id) || !ids.Add(surface.Id) || string.IsNullOrWhiteSpace(surface.Name) || surface.Name.Length > 120)
                    throw new InvalidDataException("Each surface needs a unique ID and a name of at most 120 characters.");
                ValidatePolygon(surface.Points);
                if (surface.OriginalConfidence.HasValue && (!double.IsFinite(surface.OriginalConfidence.Value) || surface.OriginalConfidence < 0 || surface.OriginalConfidence > 1))
                    throw new InvalidDataException("AI confidence estimates must be between zero and one.");
            }
        }
    }
}
