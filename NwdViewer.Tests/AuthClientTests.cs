using System.Net;
using System.Text;
using NwdViewer.Aps;

namespace NwdViewer.Tests;

public class AuthClientTests
{
    [Fact]
    public async Task GetInternalTokenAsync_CachesTokenUntilExpiry()
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler);
        var options = new ApsOptions
        {
            ClientId = "id",
            ClientSecret = "secret",
            BucketKey = "bucket",
        };
        var auth = new AuthClient(http, options);

        var first = await auth.GetInternalTokenAsync();
        var second = await auth.GetInternalTokenAsync();

        Assert.Equal("abc123", first);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = "{\"access_token\":\"abc123\",\"token_type\":\"Bearer\",\"expires_in\":3600}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
