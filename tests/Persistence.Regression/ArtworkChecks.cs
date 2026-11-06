using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;

internal static class ArtworkChecks
{
    public static async Task Run()
    {
        static void Check(bool value, string description) { if (!value) throw new Exception("Artwork check failed: " + description); }
        var layout = ArtworkRules.Layout(new List<(string, string, double)> { ("mesh-a", "Left", 2), ("mesh-b", "Right", .5) }, 1536, 1024);
        Check(layout.Count == 2 && layout[0].Region.Width > layout[0].Region.Height, "unwarped coordinated layout respects surface aspect");
        foreach (var cell in layout) ArtworkRules.ValidateRegion(cell.Region);
        var reference = new ArtworkReference("reference.jpg", "image/jpeg", PhotoFixture.Jpeg, Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(PhotoFixture.Jpeg))));
        ArtworkRequest Request(ArtworkProvider provider) => new(provider, ArtworkGenerationService.DefaultModel(provider), "Botanical flowers", "watercolor", "ivory gold",
            "1024x1024", "medium", "opaque", 2, new() { new("mesh", "Wall", ArtworkRegion.Full) }, ArtworkFixture.Png,
            provider == ArtworkProvider.StabilityAI ? new() : new() { reference });
        foreach (var provider in Enum.GetValues<ArtworkProvider>())
        {
            var handler = new Handler(async (request, token) =>
            {
                Check(!request.RequestUri!.Query.Contains("test-access-token"), "tokens do not enter URLs");
                if (provider == ArtworkProvider.GoogleGemini)
                {
                    Check(request.RequestUri.Host == "generativelanguage.googleapis.com" && request.Headers.GetValues("x-goog-api-key").Single() == "test-access-token", "Gemini endpoint and header");
                    using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    Check(json.RootElement.GetProperty("contents")[0].GetProperty("parts").GetArrayLength() == 3, "Gemini gets prompt, guide, reference");
                    return Json(new { candidates = new[] { new { content = new { parts = new[] { new { inlineData = new { mimeType = "image/png", data = ArtworkFixture.Png } } } } } } });
                }
                Check(request.Headers.Authorization?.Parameter == "test-access-token", "bearer token header");
                var form = (MultipartFormDataContent)request.Content!;
                var images = form.Where(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "image[]").ToList();
                if (provider == ArtworkProvider.OpenAI)
                {
                    Check(request.RequestUri.Host == "api.openai.com" && images.Count == 2, "OpenAI receives guide and reference");
                    return Json(new { data = new[] { new { b64_json = ArtworkFixture.Png }, new { b64_json = ArtworkFixture.Png } } });
                }
                Check(request.RequestUri.Host == "api.stability.ai" && images.Count == 0, "Stability Core uses declared text-only capability");
                return Json(new { image = ArtworkFixture.Png, finish_reason = "SUCCESS" });
            });
            using var client = new HttpClient(handler);
            var batch = await new ArtworkGenerationService(client).GenerateAsync(Request(provider), "test-access-token", CancellationToken.None);
            Check(batch.Images.Count == 2 && handler.Requests == (provider == ArtworkProvider.OpenAI ? 1 : 2), "variant count and billed request count");
        }
        int attempt = 0;
        var partialHandler = new Handler((_, _) => Task.FromResult(++attempt == 1 ? Json(new { image = ArtworkFixture.Png, finish_reason = "SUCCESS" }) : new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        using (var client = new HttpClient(partialHandler))
        {
            var partial = await new ArtworkGenerationService(client).GenerateAsync(Request(ArtworkProvider.StabilityAI), "token", CancellationToken.None);
            Check(partial.Images.Count == 1 && partial.Notice != null && partialHandler.Requests == 2, "partial batch preserves completed images without retry");
        }
        var cancellationHandler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json(new { }); });
        using (var client = new HttpClient(cancellationHandler))
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = new ArtworkGenerationService(client).GenerateAsync(Request(ArtworkProvider.GoogleGemini), "token", cancellation.Token);
            cancellation.Cancel();
            try { await pending; throw new Exception("Artwork cancellation ignored"); }
            catch (OperationCanceledException) { Check(cancellationHandler.Requests == 1, "cancellation reaches provider"); }
        }
        var asset = new ArtworkAsset { Name = "Test", ImageBase64 = ArtworkFixture.Png, ThumbnailBase64 = ArtworkFixture.Png,
            Sha256 = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(ArtworkFixture.Png))), Width = 4, Height = 3,
            Provider = ArtworkProvider.OpenAI, Model = "gpt-image-1.5", Prompt = "flowers", Layout = new() { new("mesh", "Wall", ArtworkRegion.Full) } };
        ArtworkRules.ValidateAsset(asset);
        string path = Path.Combine(Path.GetTempPath(), "Artwork-check-" + Guid.NewGuid().ToString("N") + ".pmproj");
        try
        {
            var project = new ProjectModel { ArtworkAssets = new() { asset } };
            project.ImportedVideos.Add(new() { Id = "source", Name = "Artwork-backed", SourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"),
                MeshLayers = new() { new() { Id = "mesh", SourceId = "source", ArtworkAssetId = asset.Id } } });
            var service = new ProjectService(); Check(await service.SaveAsync(project, path), "artwork project saves");
            var saved = (await service.LoadAsync(path))!;
            Check(saved.ProjectVersion == 4 && saved.ArtworkAssets[0].ImageBase64 == asset.ImageBase64 && saved.ImportedVideos[0].MeshLayers[0].ArtworkAssetId == asset.Id, "portable PNG and assignments round trip");
            Check(!File.Exists(saved.ImportedVideos[0].SourcePath), "artwork project retains missing-video metadata without requiring it during persistence");
            project.ImportedVideos[0].MeshLayers[0].ArtworkAssetId = "missing";
            Check(!await service.SaveAsync(project, path), "dangling artwork assignment rejected");
            Check((await service.LoadAsync(path))!.ArtworkAssets.Count == 1, "failed save preserves embedded image");
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var clock = new StillPlaybackClock(TimeSpan.FromMilliseconds(80), () => ended.TrySetResult(true)))
        {
            clock.SetLoop(true); clock.Start(restart: true); await Task.Delay(100);
            Check(!ended.Task.IsCompleted, "legacy still loops do not end");
            clock.Pause(); clock.SetLoop(false); clock.Start(restart: true);
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var clock = new StillPlaybackClock(TimeSpan.FromMilliseconds(150), () => resumed.TrySetResult(true)))
        {
            clock.Start(true); clock.Pause(); await Task.Delay(180); Check(!resumed.Task.IsCompleted, "paused still cannot finish");
            clock.Start(false); await resumed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Console.WriteLine("Artwork provider contracts, persistence, layout, and offline-clock checks passed. No live AI request was made.");
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) { Requests++; return respond(message, token); }
    }
}
