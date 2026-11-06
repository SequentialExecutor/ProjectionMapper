using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;
using ProjectionMapper.Utilities;

namespace ProjectionMapper.Services
{
    public sealed record ArtworkRequest(ArtworkProvider Provider, string Model, string Prompt, string Style, string Palette,
        string Size, string Quality, string Background, int Variants, List<ArtworkCell> Layout, string GuidePngBase64, List<ArtworkReference> References);
    public sealed record GeneratedArtwork(string ImageBase64, string EffectivePrompt, string? RequestId);
    public sealed record ArtworkBatch(List<GeneratedArtwork> Images, string? Notice);
    public interface IArtworkGenerator { Task<ArtworkBatch> GenerateAsync(ArtworkRequest request, string token, CancellationToken cancellationToken); }

    /// <summary>Provider-specific image APIs. Tokens are headers only; requests are never silently retried.</summary>
    public sealed class ArtworkGenerationService(HttpClient http) : IArtworkGenerator
    {
        public static string DefaultModel(ArtworkProvider provider) => provider switch
        {
            ArtworkProvider.OpenAI => "gpt-image-1.5", ArtworkProvider.GoogleGemini => "gemini-2.5-flash-image", _ => "stable-image-core"
        };
        public static string BuildPrompt(ArtworkRequest r) =>
            "Create finished artwork for a projection installation. Generate unwarped rectangular textures, not projector distortion. " +
            "Use a consistent composition and style across the listed cells. Fill each cell; do not draw guide borders, labels, or cell numbers. " +
            $"Theme: {r.Prompt}\nStyle: {r.Style}\nPalette: {r.Palette}\n" +
            string.Join("\n", r.Layout.Select((cell, i) => FormattableString.Invariant($"Cell {i + 1} ({cell.Name}): x={cell.Region.X:0.####}, y={cell.Region.Y:0.####}, width={cell.Region.Width:0.####}, height={cell.Region.Height:0.####}; normalized canvas coordinates.")));

        public async Task<ArtworkBatch> GenerateAsync(ArtworkRequest r, string token, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Trim().Any(c => c < 33 || c > 126)) throw new ArgumentException("Enter this provider's API access token.");
            if (!Enum.IsDefined(r.Provider) || string.IsNullOrWhiteSpace(r.Model) || r.Model.Length > 120 ||
                r.Model.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '.' && c != '_')) throw new ArgumentException("Invalid provider or model name.");
            if (string.IsNullOrWhiteSpace(r.Prompt) || r.Prompt.Length > 5000 || r.Style.Length > 1000 || r.Palette.Length > 500 ||
                r.Variants < 1 || r.Variants > 4 || r.Layout.Count < 1 || r.Layout.Count > 8 || r.References.Count > 2)
                throw new ArgumentException("Use a prompt of up to 5000 characters, 1–4 variants, and 1–8 surfaces.");
            if (!new[] { "1024x1024", "1536x1024", "1024x1536" }.Contains(r.Size) || !new[] { "low", "medium", "high" }.Contains(r.Quality) ||
                !new[] { "opaque", "auto", "transparent" }.Contains(r.Background)) throw new ArgumentException("Unsupported generation settings.");
            foreach (var cell in r.Layout) ArtworkRules.ValidateRegion(cell.Region);
            if (r.Provider != ArtworkProvider.OpenAI && r.Background == "transparent") throw new ArgumentException("Choose opaque/auto for this provider; transparent output is supported by the OpenAI adapter.");
            if (r.Provider == ArtworkProvider.StabilityAI && (r.References.Count != 0 || r.Model != "stable-image-core"))
                throw new ArgumentException("Stability Core currently supports text, palette, and style guidance. Use OpenAI or Gemini for uploaded references.");
            string prompt = BuildPrompt(r);
            if (prompt.Length > 10000) throw new ArgumentException("Shorten the combined prompt and surface names.");
            var results = new List<GeneratedArtwork>();
            void AddImage(string? encoded, string effectivePrompt, string? requestId)
            {
                if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 24_000_000) throw new InvalidDataException("The provider returned an empty or oversized image.");
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.Length > 8 && bytes[0] == 137 && bytes[1] == 80) ArtworkRules.PngDimensions(bytes);
                else if (bytes.Length < 3 || bytes[0] != 255 || bytes[1] != 216 || bytes[2] != 255)
                    throw new InvalidDataException("The provider did not return a supported PNG/JPEG image.");
                results.Add(new(encoded, effectivePrompt, requestId));
            }
            int calls = r.Provider == ArtworkProvider.OpenAI ? 1 : r.Variants;
            for (int call = 0; call < calls && results.Count < r.Variants; call++)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int beforeCount = results.Count;
                    using var message = BuildMessage(r, prompt, token.Trim());
                    using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        string hint = response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Check this provider's API token and model access.",
                            HttpStatusCode.TooManyRequests => "Provider quota or rate limit reached.",
                            HttpStatusCode.PaymentRequired => "Provider billing or account balance requires attention.", _ => "Check model availability and generation settings."
                        };
                        throw new HttpRequestException($"{r.Provider} returned HTTP {(int)response.StatusCode}. {hint} No automatic retry was attempted.");
                    }
                    var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                    response.Headers.TryGetValues("x-request-id", out var ids);
                    string? requestId = ids?.FirstOrDefault();
                    using var document = JsonDocument.Parse(bytes);
                    if (r.Provider == ArtworkProvider.GoogleGemini)
                    {
                        if (document.RootElement.TryGetProperty("candidates", out var candidates))
                            foreach (var candidate in candidates.EnumerateArray())
                                if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                                    foreach (var part in parts.EnumerateArray())
                                        if (part.TryGetProperty("inlineData", out var inline) || part.TryGetProperty("inline_data", out inline))
                                        {
                                            if (!inline.TryGetProperty("data", out var data)) continue;
                                            AddImage(data.GetString(), prompt, requestId);
                                        }
                    }
                    else if (r.Provider == ArtworkProvider.StabilityAI)
                    {
                        var root = document.RootElement;
                        if (root.TryGetProperty("finish_reason", out var finish) && finish.GetString() != "SUCCESS")
                            throw new InvalidDataException("The provider did not return usable artwork. Adjust the prompt and retry explicitly.");
                        AddImage(root.GetProperty("image").GetString(), prompt, requestId);
                    }
                    else
                        foreach (var image in document.RootElement.GetProperty("data").EnumerateArray())
                            AddImage(image.GetProperty("b64_json").GetString(),
                                image.TryGetProperty("revised_prompt", out var revised) ? revised.GetString() ?? prompt : prompt, requestId);
                    if (results.Count == beforeCount) throw new InvalidDataException("The provider returned no image. It may have declined the request; revise the prompt or choose another model.");
                    if (results.Count > r.Variants) results = results.Take(r.Variants).ToList();
                    foreach (var image in results)
                        if (string.IsNullOrWhiteSpace(image.ImageBase64) || image.ImageBase64.Length > 24_000_000)
                            throw new InvalidDataException("The provider returned an oversized or empty image.");
                }
                catch (Exception ex) when (results.Count > 0 && (ex is HttpRequestException || ex is OperationCanceledException || ex is InvalidDataException || ex is JsonException || ex is KeyNotFoundException || ex is FormatException || ex is InvalidOperationException))
                { return new(results, $"Stopped after {results.Count} completed image(s): {ex.Message} Completed images are retained; no retry was made."); }
            }
            return new(results, null);
        }
        private static HttpRequestMessage BuildMessage(ArtworkRequest r, string prompt, string token)
        {
            string aspect = r.Size == "1536x1024" ? "3:2" : r.Size == "1024x1536" ? "2:3" : "1:1";
            HttpRequestMessage message;
            if (r.Provider == ArtworkProvider.GoogleGemini)
            {
                message = new(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{r.Model}:generateContent");
                message.Headers.Add("x-goog-api-key", token);
                var parts = new List<object> { new { text = prompt }, new { inlineData = new { mimeType = "image/png", data = r.GuidePngBase64 } } };
                parts.AddRange(r.References.Select(reference => (object)new { inlineData = new { mimeType = reference.MimeType, data = reference.ImageBase64 } }));
                message.Content = Json(new { contents = new[] { new { parts } }, generationConfig = new { responseModalities = new[] { "TEXT", "IMAGE" }, imageConfig = new { aspectRatio = aspect } } });
            }
            else
            {
                message = new(HttpMethod.Post, r.Provider == ArtworkProvider.OpenAI ? "https://api.openai.com/v1/images/edits" : "https://api.stability.ai/v2beta/stable-image/generate/core");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(prompt), "prompt"); form.Add(new StringContent("png"), "output_format");
                if (r.Provider == ArtworkProvider.OpenAI)
                {
                    form.Add(new StringContent(r.Model), "model"); form.Add(new StringContent(r.Size), "size");
                    form.Add(new StringContent(r.Quality), "quality"); form.Add(new StringContent(r.Background), "background");
                    form.Add(new StringContent(r.Variants.ToString(System.Globalization.CultureInfo.InvariantCulture)), "n");
                    void Image(string data, string type, string file)
                    {
                        var content = new ByteArrayContent(Convert.FromBase64String(data)); content.Headers.ContentType = new MediaTypeHeaderValue(type);
                        form.Add(content, "image[]", file);
                    }
                    Image(r.GuidePngBase64, "image/png", "layout.png");
                    for (int i = 0; i < r.References.Count; i++) Image(r.References[i].ImageBase64, r.References[i].MimeType, $"reference-{i + 1}." + (r.References[i].MimeType == "image/png" ? "png" : "jpg"));
                }
                else { form.Add(new StringContent(aspect), "aspect_ratio"); message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); }
                message.Content = form;
            }
            return message;
        }
        private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
        private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
        {
            await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[65536]; int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > 80_000_000) throw new InvalidDataException("Image response exceeded the supported size.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }
}
