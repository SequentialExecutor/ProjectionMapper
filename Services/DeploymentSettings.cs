using System;
using System.IO;
using System.Text.Json;

namespace ProjectionMapper.Services
{
    public sealed class DeploymentSettings
    {
        public long MinimumFreeBytes { get; set; } = 64 * 1024 * 1024;
        public int ProbeTimeoutSeconds { get; set; } = 10;
        public int RehearsalSampleSeconds { get; set; } = 5;
        public int MaximumSamples { get; set; } = 7200;
        public static DeploymentSettings Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return new();
            using var json = JsonDocument.Parse(File.ReadAllText(path), new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var result = json.RootElement.TryGetProperty("Deployment", out var section) ? section.Deserialize<DeploymentSettings>() ?? new() : new();
            if (result.MinimumFreeBytes < 0 || result.ProbeTimeoutSeconds < 1 || result.ProbeTimeoutSeconds > 60 || result.RehearsalSampleSeconds < 1 || result.MaximumSamples < 1 || result.MaximumSamples > 100000)
                throw new InvalidDataException("Deployment settings contain invalid limits.");
            return result;
        }
    }
}
