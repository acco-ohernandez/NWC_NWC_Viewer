using System.Text.Json.Serialization;

namespace NwdViewer.Aps;

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresInSeconds);

public sealed record SignedS3UploadInit(
    [property: JsonPropertyName("uploadKey")] string UploadKey,
    [property: JsonPropertyName("urls")] List<string> Urls);

public sealed record SignedS3UploadComplete(
    [property: JsonPropertyName("objectId")] string ObjectId,
    [property: JsonPropertyName("objectKey")] string ObjectKey,
    [property: JsonPropertyName("bucketKey")] string BucketKey,
    [property: JsonPropertyName("size")] long Size);

public sealed record TranslationManifest(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("progress")] string Progress,
    [property: JsonPropertyName("urn")] string Urn);

public sealed record MetadataListResponse(
    [property: JsonPropertyName("data")] MetadataList Data);

public sealed record MetadataList(
    [property: JsonPropertyName("metadata")] List<MetadataEntry> Metadata);

public sealed record MetadataEntry(
    [property: JsonPropertyName("guid")] string Guid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role);

public sealed record ObjectProperties(
    [property: JsonPropertyName("data")] ObjectPropertiesData Data);

public sealed record ObjectPropertiesData(
    [property: JsonPropertyName("collection")] List<ObjectPropertiesCollection> Collection);

public sealed record ObjectPropertiesCollection(
    [property: JsonPropertyName("objectid")] int ObjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("externalId")] string? ExternalId,
    [property: JsonPropertyName("properties")] Dictionary<string, Dictionary<string, object>>? Properties);
