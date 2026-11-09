using System;
using System.Threading.Tasks;
using ProjectionMapper.Models;

namespace ProjectionMapper.Utilities
{
    /// <summary>Compiled, immutable surface sampling map. Reused across frames until geometry or size changes.</summary>
    public sealed class SurfacePixelMap
    {
        private readonly int _width, _height;
        private readonly float[]? _uv;
        private readonly byte[]? _alpha;
        public SurfacePixelMap(SurfaceAppearance style, int width, int height)
        {
            ArtistRules.Validate(style);
            if (width < 1 || height < 1 || (long)width * height > 8_400_000) throw new ArgumentOutOfRangeException(nameof(width));
            _width = width; _height = height;
            if (style.Grid.Count > 0)
            {
                _uv = new float[width * height * 2]; Array.Fill(_uv, -1);
                int stride = style.GridColumns + 1;
                for (int y = 0; y < style.GridRows; y++) for (int x = 0; x < style.GridColumns; x++)
                {
                    int tl = y * stride + x, tr = tl + 1, bl = tl + stride, br = bl + 1;
                    var u0 = new PhotoPoint((double)x / style.GridColumns, (double)y / style.GridRows);
                    var u1 = new PhotoPoint((double)(x + 1) / style.GridColumns, (double)y / style.GridRows);
                    var u2 = new PhotoPoint((double)x / style.GridColumns, (double)(y + 1) / style.GridRows);
                    var u3 = new PhotoPoint(u1.X, u2.Y);
                    Triangle(style.Grid[tl], style.Grid[tr], style.Grid[br], u0, u1, u3);
                    Triangle(style.Grid[tl], style.Grid[br], style.Grid[bl], u0, u3, u2);
                }
            }
            if (style.Mask.Count > 0)
            {
                _alpha = new byte[width * height];
                // Feather is a fraction of the shorter surface dimension, independent of image aspect.
                double aspectX = (double)width / Math.Min(width, height), aspectY = (double)height / Math.Min(width, height);
                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        var p = new PhotoPoint((x + .5) / width, (y + .5) / height);
                        if (!PhotoGeometry.Contains(style.Mask, p)) continue;
                        double distance = double.MaxValue;
                        if (style.Feather > 0)
                            for (int i = 0; i < style.Mask.Count; i++)
                            {
                                var a = style.Mask[i]; var b = style.Mask[(i + 1) % style.Mask.Count];
                                double ax = (a.X - p.X) * aspectX, ay = (a.Y - p.Y) * aspectY;
                                double dx = (b.X - a.X) * aspectX, dy = (b.Y - a.Y) * aspectY;
                                double t = Math.Clamp(-(ax * dx + ay * dy) / (dx * dx + dy * dy), 0, 1);
                                distance = Math.Min(distance, Math.Sqrt(Math.Pow(ax + t * dx, 2) + Math.Pow(ay + t * dy, 2)));
                            }
                        double opacity = style.Feather > 0 ? Math.Clamp(distance / style.Feather, 0, 1) : 1;
                        _alpha[y * width + x] = (byte)Math.Round(opacity * 255);
                    }
                });
            }
        }
        private void Triangle(PhotoPoint a, PhotoPoint b, PhotoPoint c, PhotoPoint ua, PhotoPoint ub, PhotoPoint uc)
        {
            double denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) * _width));
            int maxX = Math.Min(_width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X)) * _width));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)) * _height));
            int maxY = Math.Min(_height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y)) * _height));
            for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
            {
                double px = (x + .5) / _width, py = (y + .5) / _height;
                double wa = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denominator;
                double wb = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denominator;
                double wc = 1 - wa - wb;
                if (wa < -1e-8 || wb < -1e-8 || wc < -1e-8) continue;
                int index = (y * _width + x) * 2;
                _uv![index] = (float)(wa * ua.X + wb * ub.X + wc * uc.X);
                _uv[index + 1] = (float)(wa * ua.Y + wb * ub.Y + wc * uc.Y);
            }
        }
        /// <summary>Input/output are straight-alpha BGRA. Sampling interpolates premultiplied colors to avoid fringes.</summary>
        public byte[] Apply(byte[] input)
        {
            if (input.Length != _width * _height * 4) throw new ArgumentException("Pixel buffer dimensions do not match.");
            var output = new byte[input.Length];
            Parallel.For(0, _height, y =>
            {
                for (int x = 0; x < _width; x++)
                {
                    int p = y * _width + x, dest = p * 4;
                    if (_uv == null) Buffer.BlockCopy(input, dest, output, dest, 4);
                    else
                    {
                        double u = _uv[p * 2], v = _uv[p * 2 + 1];
                        if (u < 0 || v < 0) continue;
                        double sx = Math.Clamp(u * _width - .5, 0, _width - 1), sy = Math.Clamp(v * _height - .5, 0, _height - 1);
                        int ix = (int)sx, iy = (int)sy;
                        double fx = sx - ix, fy = sy - iy;
                        int p0 = (iy * _width + ix) * 4, p1 = (iy * _width + Math.Min(ix + 1, _width - 1)) * 4;
                        int p2 = (Math.Min(iy + 1, _height - 1) * _width + ix) * 4, p3 = (Math.Min(iy + 1, _height - 1) * _width + Math.Min(ix + 1, _width - 1)) * 4;
                        double w0 = (1 - fx) * (1 - fy) * input[p0 + 3], w1 = fx * (1 - fy) * input[p1 + 3];
                        double w2 = (1 - fx) * fy * input[p2 + 3], w3 = fx * fy * input[p3 + 3];
                        double alpha = w0 + w1 + w2 + w3;
                        output[dest + 3] = (byte)Math.Clamp(Math.Round(alpha), 0, 255);
                        if (alpha > 0) for (int channel = 0; channel < 3; channel++)
                            output[dest + channel] = (byte)Math.Clamp(Math.Round((input[p0 + channel] * w0 + input[p1 + channel] * w1 + input[p2 + channel] * w2 + input[p3 + channel] * w3) / alpha), 0, 255);
                    }
                    if (_alpha != null) output[dest + 3] = (byte)((output[dest + 3] * _alpha[p] + 127) / 255);
                    if (output[dest + 3] == 0) { output[dest] = 0; output[dest + 1] = 0; output[dest + 2] = 0; }
                }
            });
            return output;
        }
    }

    /// <summary>Final projector correction, with complementary linear-light edge ramps (approximate display gamma 2.2).</summary>
    public static class OutputPixels
    {
        public static double EdgeWeight(double coordinate, double startWidth, double endWidth, double power)
        {
            double a = startWidth > 0 ? Math.Clamp(coordinate / startWidth, 0, 1) : 1;
            double b = endWidth > 0 ? Math.Clamp((1 - coordinate) / endWidth, 0, 1) : 1;
            return Math.Pow(Math.Min(a, b), power);
        }
        public static byte[] Apply(byte[] input, int width, int height, OutputAppearance? style, double gain)
        {
            if (width < 1 || height < 1 || input.Length != (long)width * height * 4) throw new ArgumentException("Invalid output buffer.");
            if (!double.IsFinite(gain) || gain < 0 || gain > 1) throw new ArgumentOutOfRangeException(nameof(gain));
            if (style != null) ArtistRules.Validate(style);
            style ??= new OutputAppearance();
            var lookup = new byte[3, 256]; double[] balance = { style.Blue, style.Green, style.Red };
            for (int c = 0; c < 3; c++) for (int value = 0; value < 256; value++)
                lookup[c, value] = (byte)Math.Clamp(Math.Round(255 * Math.Pow(value / 255.0, 1 / style.Gamma) * style.Brightness * balance[c]), 0, 255);
            var output = new byte[input.Length];
            var columns = new double[width]; var rows = new double[height];
            for (int x = 0; x < width; x++) columns[x] = Math.Pow(EdgeWeight((x + .5) / width, style.BlendLeft, style.BlendRight, style.BlendPower), 1 / 2.2);
            for (int y = 0; y < height; y++) rows[y] = Math.Pow(EdgeWeight((y + .5) / height, style.BlendTop, style.BlendBottom, style.BlendPower), 1 / 2.2);
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    int p = (y * width + x) * 4;
                    for (int c = 0; c < 3; c++) output[p + c] = (byte)Math.Clamp(Math.Round(lookup[c, input[p + c]] * gain * columns[x] * rows[y]), 0, 255);
                    output[p + 3] = input[p + 3];
                }
            });
            return output;
        }
    }
}
