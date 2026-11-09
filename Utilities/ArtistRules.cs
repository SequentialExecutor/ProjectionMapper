using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ProjectionMapper.Models;

namespace ProjectionMapper.Utilities
{
    /// <summary>Fail closed on malformed masks or folded grids instead of projecting an unmasked fallback.</summary>
    public static class ArtistRules
    {
        private static void Range(double v, double min, double max, string name)
        { if (!double.IsFinite(v) || v < min || v > max) throw new InvalidDataException($"{name} must be between {min} and {max}."); }
        public static void Validate(SurfaceAppearance style)
        {
            if (style == null || style.Mask == null || style.Grid == null) throw new InvalidDataException("Surface controls are missing.");
            Range(style.Feather, 0, .25, "Mask feather");
            if (style.Mask.Count != 0) PhotoGeometry.ValidatePolygon(style.Mask);
            if (style.Grid.Count == 0)
            {
                if (style.GridColumns != 0 || style.GridRows != 0) throw new InvalidDataException("Disabled grid dimensions must be zero.");
                return;
            }
            if (style.GridColumns < 1 || style.GridColumns > 8 || style.GridRows < 1 || style.GridRows > 8 || style.Grid.Count != (style.GridColumns + 1) * (style.GridRows + 1))
                throw new InvalidDataException("A deformation grid supports one to eight cells per axis.");
            foreach (var p in style.Grid) { Range(p.X, 0, 1, "Grid X"); Range(p.Y, 0, 1, "Grid Y"); }
            // Monotonic rows/columns and positive convex cells exclude overlapping/folded patches.
            for (int y = 0; y <= style.GridRows; y++)
                for (int x = 0; x <= style.GridColumns; x++)
                {
                    var a = style.Grid[y * (style.GridColumns + 1) + x];
                    if (x > 0 && a.X <= style.Grid[y * (style.GridColumns + 1) + x - 1].X + .0001) throw new InvalidDataException("Grid rows must run left to right without folds.");
                    if (y > 0 && a.Y <= style.Grid[(y - 1) * (style.GridColumns + 1) + x].Y + .0001) throw new InvalidDataException("Grid columns must run top to bottom without folds.");
                    if (x < style.GridColumns && y < style.GridRows)
                    {
                        var b = style.Grid[y * (style.GridColumns + 1) + x + 1];
                        var c = style.Grid[(y + 1) * (style.GridColumns + 1) + x + 1];
                        var d = style.Grid[(y + 1) * (style.GridColumns + 1) + x];
                        var cell = new[] { a, b, c, d };
                        for (int i = 0; i < 4; i++) if (Cross(cell[i], cell[(i + 1) % 4], cell[(i + 2) % 4]) <= .000001)
                            throw new InvalidDataException("Grid cells must stay convex and cannot collapse or overlap.");
                    }
                }
        }
        private static double Cross(PhotoPoint a, PhotoPoint b, PhotoPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        public static SurfaceAppearance RegularGrid(int columns, int rows)
        {
            var style = new SurfaceAppearance { GridColumns = columns, GridRows = rows };
            if (columns < 1 || columns > 8 || rows < 1 || rows > 8) throw new ArgumentOutOfRangeException(nameof(columns));
            for (int y = 0; y <= rows; y++) for (int x = 0; x <= columns; x++) style.Grid.Add(new((double)x / columns, (double)y / rows));
            return style;
        }
        public static void Validate(OutputAppearance style)
        {
            if (style == null || style.MonitorIndex < 0 || string.IsNullOrWhiteSpace(style.DisplayName)) throw new InvalidDataException("Output correction needs a display identity.");
            Range(style.Brightness, 0, 2, "Brightness"); Range(style.Gamma, .1, 4, "Gamma");
            Range(style.Red, 0, 2, "Red"); Range(style.Green, 0, 2, "Green"); Range(style.Blue, 0, 2, "Blue");
            Range(style.BlendLeft, 0, .5, "Left blend"); Range(style.BlendRight, 0, .5, "Right blend");
            Range(style.BlendTop, 0, .5, "Top blend"); Range(style.BlendBottom, 0, .5, "Bottom blend"); Range(style.BlendPower, .1, 4, "Blend power");
        }
        public static void Validate(PlaylistGroupModel cue)
        {
            if (cue == null || !Enum.IsDefined(cue.AdvanceMode) || !Enum.IsDefined(cue.PlaybackMode) || cue.SourceIds == null) throw new InvalidDataException("Cue settings are invalid.");
            Range(cue.HoldSeconds, 1, 86400, "Cue duration"); Range(cue.FadeSeconds, 0, 10, "Cue fade");
        }
    }
}
