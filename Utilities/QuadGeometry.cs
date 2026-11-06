using System;
using System.Numerics;

namespace ProjectionMapper.Utilities
{
    /// <summary>Shared geometry rules for TL, TR, BL, BR quadrilaterals.</summary>
    public static class QuadGeometry
    {
        public static bool IsValid(Vector2[]? points)
        {
            if (points == null || points.Length != 4) return false;
            int[] order = { 0, 1, 3, 2 };
            double sign = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = points[order[i]];
                var b = points[order[(i + 1) % 4]];
                var c = points[order[(i + 2) % 4]];
                if (!float.IsFinite(a.X) || !float.IsFinite(a.Y)) return false;
                double cross = ((double)b.X - a.X) * ((double)c.Y - b.Y) - ((double)b.Y - a.Y) * ((double)c.X - b.X);
                if (Math.Abs(cross) < 1e-10) return false;
                if (i == 0) sign = Math.Sign(cross);
                else if (Math.Sign(cross) != sign) return false;
            }
            return true;
        }

        public static Vector2 FromPixels(double x, double y, int width, int height, double snapStep = 0)
        {
            if (width <= 0 || height <= 0 || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(snapStep) || snapStep < 0)
                throw new ArgumentOutOfRangeException(nameof(x), "Coordinates and output dimensions must be finite and valid.");
            if (snapStep > 0)
            {
                x = Math.Round(x / snapStep, MidpointRounding.AwayFromZero) * snapStep;
                y = Math.Round(y / snapStep, MidpointRounding.AwayFromZero) * snapStep;
            }
            return new Vector2((float)(Math.Clamp(x, 0, width) / width), (float)(Math.Clamp(y, 0, height) / height));
        }
    }
}
