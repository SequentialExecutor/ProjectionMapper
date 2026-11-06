namespace ProjectionMapper.Rendering
{
    /// <summary>Shared native WPF composition guard for video, calibration patterns, and artwork guides.</summary>
    internal static class WpfCompositionGate
    {
        internal static object SyncRoot { get; } = new();
    }
}
