namespace NwdViewer.Aps;

public sealed class ApsOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string BucketKey { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "https://developer.api.autodesk.com";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
            throw new InvalidOperationException("APS ClientId is not configured.");
        if (string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException("APS ClientSecret is not configured.");
        if (string.IsNullOrWhiteSpace(BucketKey))
            throw new InvalidOperationException("APS BucketKey is not configured.");
    }
}
