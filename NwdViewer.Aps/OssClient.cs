using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace NwdViewer.Aps;

public sealed class OssClient
{
    private readonly HttpClient _http;
    private readonly AuthClient _auth;
    private readonly ApsOptions _options;

    public OssClient(HttpClient http, AuthClient auth, ApsOptions options)
    {
        _http = http;
        _auth = auth;
        _options = options;
    }

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/oss/v2/buckets")
        {
            Content = JsonContent.Create(new { bucketKey = _options.BucketKey, policyKey = "transient" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Conflict)
            return;
        resp.EnsureSuccessStatusCode();
    }

    public async Task<string> UploadAsync(
        string localPath,
        string objectKey,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        var fileSize = new FileInfo(localPath).Length;

        var initUrl = $"{_options.BaseUrl}/oss/v2/buckets/{Uri.EscapeDataString(_options.BucketKey)}/objects/{Uri.EscapeDataString(objectKey)}/signeds3upload";
        using var initReq = new HttpRequestMessage(HttpMethod.Get, initUrl);
        initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var initResp = await _http.SendAsync(initReq, ct);
        initResp.EnsureSuccessStatusCode();
        var init = await initResp.Content.ReadFromJsonAsync<SignedS3UploadInit>(cancellationToken: ct)
            ?? throw new InvalidOperationException("APS signeds3upload returned empty init payload.");

        if (init.Urls.Count == 0)
            throw new InvalidOperationException("APS signeds3upload returned no upload URLs.");

        await using (var fs = File.OpenRead(localPath))
        {
            using var putReq = new HttpRequestMessage(HttpMethod.Put, init.Urls[0])
            {
                Content = new StreamContent(fs),
            };
            putReq.Content.Headers.ContentLength = fileSize;

            using var putResp = await _http.SendAsync(putReq, ct);
            putResp.EnsureSuccessStatusCode();
            progress?.Report(90);
        }

        var completeUrl = $"{_options.BaseUrl}/oss/v2/buckets/{Uri.EscapeDataString(_options.BucketKey)}/objects/{Uri.EscapeDataString(objectKey)}/signeds3upload";
        using var completeReq = new HttpRequestMessage(HttpMethod.Post, completeUrl)
        {
            Content = JsonContent.Create(new { uploadKey = init.UploadKey })
        };
        completeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var completeResp = await _http.SendAsync(completeReq, ct);
        completeResp.EnsureSuccessStatusCode();
        var completed = await completeResp.Content.ReadFromJsonAsync<SignedS3UploadComplete>(cancellationToken: ct)
            ?? throw new InvalidOperationException("APS signeds3upload complete returned empty payload.");

        progress?.Report(100);
        return ToUrn(completed.ObjectId);
    }

    /// <summary>
    /// APS "URN" form: URL-safe base64 of the object ID with a "urn:" prefix. The prefix
    /// is kept for URL path usage (/manifest, /metadata). Strip it with <see cref="WithoutPrefix"/>
    /// when putting the URN into JSON request bodies — the Model Derivative API rejects it there.
    /// </summary>
    private static string ToUrn(string objectId)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(objectId))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "urn:" + b64;
    }

    public static string WithoutPrefix(string urn)
        => urn.StartsWith("urn:", StringComparison.Ordinal) ? urn[4..] : urn;
}
