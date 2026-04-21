using System.Net.Http.Json;

namespace NwdViewer.Aps;

public sealed class AuthClient
{
    private readonly HttpClient _http;
    private readonly ApsOptions _options;
    private TokenResponse? _cached;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AuthClient(HttpClient http, ApsOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<string> GetInternalTokenAsync(CancellationToken ct = default)
        => await GetTokenAsync("data:read data:write data:create bucket:create bucket:read", ct);

    public async Task<string> GetViewerTokenAsync(CancellationToken ct = default)
        => await GetTokenAsync("viewables:read", ct);

    private async Task<string> GetTokenAsync(string scope, CancellationToken ct)
    {
        _options.Validate();
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached != null && DateTimeOffset.UtcNow < _expiresAt)
                return _cached.AccessToken;

            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", scope),
            });

            var auth = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/authentication/v2/token")
            {
                Content = body,
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var token = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("APS auth returned an empty body.");

            _cached = token;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds - 60);
            return token.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}
