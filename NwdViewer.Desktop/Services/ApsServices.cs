using System.Net.Http;
using NwdViewer.Aps;

namespace NwdViewer.Desktop.Services;

public sealed class ApsServices : IDisposable
{
    public HttpClient Http { get; }
    public ApsOptions Options { get; }
    public AuthClient Auth { get; }
    public OssClient Oss { get; }
    public ModelDerivativeClient ModelDerivative { get; }

    public ApsServices(ApsCredentials creds)
    {
        Options = new ApsOptions
        {
            ClientId = creds.ClientId,
            ClientSecret = creds.ClientSecret,
            BucketKey = creds.BucketKey,
        };
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Auth = new AuthClient(Http, Options);
        Oss = new OssClient(Http, Auth, Options);
        ModelDerivative = new ModelDerivativeClient(Http, Auth, Options);
    }

    public void Dispose() => Http.Dispose();
}
