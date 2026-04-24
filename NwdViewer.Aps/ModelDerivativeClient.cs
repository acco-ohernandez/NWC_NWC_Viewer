using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NwdViewer.Aps;

public sealed class ModelDerivativeClient
{
    private readonly HttpClient _http;
    private readonly AuthClient _auth;
    private readonly ApsOptions _options;

    public ModelDerivativeClient(HttpClient http, AuthClient auth, ApsOptions options)
    {
        _http = http;
        _auth = auth;
        _options = options;
    }

    public async Task StartTranslationAsync(string urn, CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/modelderivative/v2/designdata/job")
        {
            Content = JsonContent.Create(new
            {
                input = new { urn = OssClient.WithoutPrefix(urn) },   // POST body wants just the base64, no "urn:" prefix
                output = new
                {
                    formats = new[]
                    {
                        new { type = "svf2", views = new[] { "2d", "3d" } }
                    }
                }
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("x-ads-force", "true");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"APS translation job POST failed with {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }
    }

    public async Task<TranslationManifest> GetManifestAsync(string urn, CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_options.BaseUrl}/modelderivative/v2/designdata/{Uri.EscapeDataString(OssClient.WithoutPrefix(urn))}/manifest");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"APS manifest GET failed with {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }
        return await resp.Content.ReadFromJsonAsync<TranslationManifest>(cancellationToken: ct)
            ?? throw new InvalidOperationException("APS returned an empty manifest.");
    }

    public async Task<TranslationManifest> WaitForTranslationAsync(
        string urn,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var manifest = await GetManifestAsync(urn, ct);

            if (int.TryParse(manifest.Progress?.Replace("%", "").Replace("complete", "").Trim(), out var pct))
                progress?.Report(pct);

            switch (manifest.Status)
            {
                case "success":
                    progress?.Report(100);
                    return manifest;
                case "failed":
                case "timeout":
                    throw new InvalidOperationException($"APS translation {manifest.Status} for urn {urn}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
    }

    public async Task<List<MetadataEntry>> GetMetadataAsync(string urn, CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_options.BaseUrl}/modelderivative/v2/designdata/{Uri.EscapeDataString(OssClient.WithoutPrefix(urn))}/metadata");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var wrapper = await resp.Content.ReadFromJsonAsync<MetadataListResponse>(cancellationToken: ct);
        return wrapper?.Data?.Metadata ?? new List<MetadataEntry>();
    }

    public async Task<ObjectProperties?> GetObjectPropertiesAsync(
        string urn,
        string modelGuid,
        int objectId,
        CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_options.BaseUrl}/modelderivative/v2/designdata/{Uri.EscapeDataString(OssClient.WithoutPrefix(urn))}/metadata/{Uri.EscapeDataString(modelGuid)}/properties?objectid={objectId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"APS properties GET failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");

        // Stash the raw body so the caller can log it if deserialization produces
        // a null Data/Collection — APS sometimes returns 200 with a body that's
        // still indexing and doesn't have the { data: { collection: [...] } } shape.
        LastPropertiesRawBody = body;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ObjectProperties>(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Raw body of the most recent GetObjectPropertiesAsync call; for diagnostics when
    /// the typed response is unexpectedly empty.</summary>
    public string? LastPropertiesRawBody { get; private set; }
}
