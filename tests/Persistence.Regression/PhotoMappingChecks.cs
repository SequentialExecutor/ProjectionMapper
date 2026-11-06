using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectionMapper.Models;
using ProjectionMapper.Services;
using ProjectionMapper.Utilities;

internal static class PhotoMappingChecks
{
    public static async Task Run()
    {
        static void Check(bool value, string description)
        {
            if (!value) throw new Exception("Photo mapping check failed: " + description);
        }
        var quad = new List<PhotoPoint> { new(.1, .1), new(.9, .1), new(.9, .9), new(.1, .9) };
        PhotoGeometry.ValidatePolygon(quad);
        var output = PhotoGeometry.ToOutputQuad(quad);
        Check(output[0].X == .1f && output[2].X == .1f && output[2].Y == .9f, "perimeter points become TL/TR/BL/BR output coordinates");
        var split = PhotoGeometry.Split(quad, 0, 2);
        Check(split.First.Count == 3 && split.Second.Count == 3, "manual polygon split");
        Check(PhotoGeometry.Overlaps(quad, split.First), "overlap review includes contained/shared-edge surfaces");
        Check(!PhotoGeometry.Overlaps(quad, new PhotoPoint[] { new(0, 0), new(.05, 0), new(.05, .05), new(0, .05) }), "separate surfaces do not overlap");
        try { PhotoGeometry.ValidatePolygon(new PhotoPoint[] { new(0, 0), new(.1, .1), new(0, .1), new(.1, 0) }); throw new Exception("Crossed polygon accepted"); }
        catch (InvalidDataException) { }
        try { PhotoGeometry.Split(quad, 0, 1); throw new Exception("Adjacent split accepted"); }
        catch (InvalidDataException) { }
        var photo = new PhotoMappingModel { ImageBase64 = PhotoFixture.Jpeg, Width = 4, Height = 3, OriginalFileName = "scene.jpg",
            ImageSha256 = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(PhotoFixture.Jpeg))) };
        PhotoGeometry.ValidateMap(photo);

        // Synthetic provider responses test the HTTP contract, not real scene-recognition quality.
        string SurfaceJson(double confidence = .4) => JsonSerializer.Serialize(new { surfaces = new[] {
            new { name = "Wall", confidence, points = quad.Select(p => new { x = p.X, y = p.Y }) }
        }});
        string Envelope(string data) => JsonSerializer.Serialize(new { id = "response-contract-test", status = "completed", output = new[] {
            new { type = "message", content = new[] { new { type = "output_text", text = data } } }
        }});
        var handler = new ContractHandler(async (request, token) =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/responses", "fixed HTTPS provider endpoint");
            Check(request.Headers.Authorization?.Parameter == "contract-test-key", "key is in authorization header");
            var body = await request.Content!.ReadAsStringAsync(token);
            Check(!body.Contains("contract-test-key"), "key is absent from body");
            using var document = JsonDocument.Parse(body);
            Check(!document.RootElement.GetProperty("store").GetBoolean(), "provider response storage disabled");
            Check(document.RootElement.GetProperty("input")[0].GetProperty("content")[1].GetProperty("image_url").GetString()!.StartsWith("data:image/jpeg;base64,"), "normalized JPEG image input");
            Check(document.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean(), "structured output schema");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope(SurfaceJson()), Encoding.UTF8, "application/json") };
        });
        using (var client = new HttpClient(handler))
        {
            var result = await new PhotoVisionService(client).AnalyzeAsync(photo, "Find the wall", "contract-test-key", "gpt-4.1-mini", CancellationToken.None);
            Check(result.Surfaces.Count == 1 && !result.Surfaces[0].Accepted, "AI outlines start as unaccepted proposals");
            Check(result.Surfaces[0].OriginalConfidence == .4 && result.Surfaces[0].ReferenceImageSha256 == photo.ImageSha256, "uncertain proposals retain provenance");
            photo.Surfaces.Add(result.Surfaces[0]);
        }
        Check(handler.Requests == 1, "exactly one provider request");
        using (var client = new HttpClient(new ContractHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope(SurfaceJson(-1))) }))))
        {
            var rejected = await new PhotoVisionService(client).AnalyzeAsync(photo, "Find the wall", "test", "gpt-4.1-mini", CancellationToken.None);
            Check(rejected.Surfaces.Count == 0 && rejected.Warnings.Count > 0 && photo.Surfaces.Count == 1, "invalid proposals are rejected without replacing existing geometry");
        }
        var refusalBody = JsonSerializer.Serialize(new { status = "completed", output = new[] { new { content = new[] { new { type = "refusal", refusal = "declined" } } } } });
        using (var client = new HttpClient(new ContractHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(refusalBody) }))))
        {
            try { await new PhotoVisionService(client).AnalyzeAsync(photo, "Find wall", "test", "gpt-4.1-mini", CancellationToken.None); throw new Exception("Refusal ignored"); }
            catch (InvalidDataException) { Check(photo.Surfaces.Count == 1, "refusal preserves existing map"); }
        }
        using (var client = new HttpClient(new ContractHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope("{\"surfaces\":[]}")) }))))
        {
            var empty = await new PhotoVisionService(client).AnalyzeAsync(photo, "Find wall", "test", "gpt-4.1-mini", CancellationToken.None);
            Check(empty.Surfaces.Count == 0 && empty.Warnings.Count > 0, "empty detections preserve manual fallback");
        }
        var limited = new ContractHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("private provider response") }));
        using (var client = new HttpClient(limited))
        {
            try { await new PhotoVisionService(client).AnalyzeAsync(photo, "Find wall", "test", "gpt-4.1-mini", CancellationToken.None); throw new Exception("HTTP failure ignored"); }
            catch (HttpRequestException ex) { Check(ex.Message.Contains("429") && !ex.Message.Contains("private"), "safe provider error"); }
        }
        Check(limited.Requests == 1, "billable requests are not silently retried");
        var cancellationHandler = new ContractHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        using (var client = new HttpClient(cancellationHandler))
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = new PhotoVisionService(client).AnalyzeAsync(photo, "Find wall", "test", "gpt-4.1-mini", cancellation.Token);
            cancellation.Cancel();
            try { await pending; throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check(cancellationHandler.Requests == 1, "cancellation reaches HTTP request"); }
        }
        string path = Path.Combine(Path.GetTempPath(), "photo-map-check-" + Guid.NewGuid().ToString("N") + ".pmproj");
        try
        {
            var service = new ProjectService();
            var project = new ProjectModel { PhotoMaps = new() { photo } };
            Check(await service.SaveAsync(project, path), "photo-only project saves");
            var loaded = (await service.LoadAsync(path))!;
            Check(loaded.ProjectVersion == 4 && loaded.PhotoMaps[0].ImageBase64 == photo.ImageBase64, "reference image round trip and schema version");
            Check(loaded.PhotoMaps[0].Surfaces[0].Points.SequenceEqual(quad) && loaded.PhotoMaps[0].Surfaces[0].Model == "gpt-4.1-mini", "accepted geometry and provenance round trip");
            await File.WriteAllTextAsync(path, "{\"ProjectVersion\":2,\"Name\":\"Existing\"}");
            Check((await service.LoadAsync(path))!.PhotoMaps.Count == 0, "existing version-2 projects remain readable");
            photo.ImageSha256 = "invalid";
            Check(!await service.SaveAsync(project, path), "tampered reference checksum rejected");
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
        Console.WriteLine("Photo geometry, persistence, and HTTP contract checks passed. No live AI request was made.");
    }

    private sealed class ContractHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return reply(request, token);
        }
    }
}
