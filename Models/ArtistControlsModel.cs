using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectionMapper.Models
{
    /// <summary>Local surface coordinates: mask perimeter and row-major deformation grid, both normalized.</summary>
    public sealed class SurfaceAppearance
    {
        public List<PhotoPoint> Mask { get; set; } = new();
        public double Feather { get; set; }
        public int GridColumns { get; set; }
        public int GridRows { get; set; }
        public List<PhotoPoint> Grid { get; set; } = new();
        public bool IsDefault => Mask.Count == 0 && Grid.Count == 0;
        public SurfaceAppearance Copy() => new() { Mask = Mask.ToList(), Feather = Feather, GridColumns = GridColumns, GridRows = GridRows, Grid = Grid.ToList() };
    }

    /// <summary>Final projector correction; display identity prevents applying another projector's settings.</summary>
    public sealed class OutputAppearance
    {
        public int MonitorIndex { get; set; } = -1;
        public string DisplayName { get; set; } = string.Empty;
        public double Brightness { get; set; } = 1;
        public double Gamma { get; set; } = 1;
        public double Red { get; set; } = 1;
        public double Green { get; set; } = 1;
        public double Blue { get; set; } = 1;
        public double BlendLeft { get; set; }
        public double BlendRight { get; set; }
        public double BlendTop { get; set; }
        public double BlendBottom { get; set; }
        public double BlendPower { get; set; } = 1;
        public bool IsDefault => Brightness == 1 && Gamma == 1 && Red == 1 && Green == 1 && Blue == 1 && BlendLeft == 0 && BlendRight == 0 && BlendTop == 0 && BlendBottom == 0;
        public OutputAppearance Copy() => (OutputAppearance)MemberwiseClone();
    }
    public enum CueAdvanceMode { MediaEnd, Manual, Timed }
}
