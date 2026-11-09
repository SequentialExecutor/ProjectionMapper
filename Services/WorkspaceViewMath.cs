using System;

namespace ProjectionMapper.Services
{
    /// <summary>Fit changes the editor's view scale, never source/output coordinates or projector dimensions.</summary>
    public static class WorkspaceViewMath
    {
        public static double FitZoom(double viewportWidth, double viewportHeight, double contentWidth, double contentHeight)
        {
            if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) || !double.IsFinite(contentWidth) || !double.IsFinite(contentHeight) ||
                viewportWidth <= 0 || viewportHeight <= 0 || contentWidth <= 0 || contentHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
            return Math.Clamp(Math.Min(Math.Max(1, viewportWidth - 8) / contentWidth, Math.Max(1, viewportHeight - 8) / contentHeight), .1, 4);
        }
    }
}
