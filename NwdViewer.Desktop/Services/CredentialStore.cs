using CredentialManagement;

namespace NwdViewer.Desktop.Services;

public sealed class CredentialStore
{
    private const string ClientIdTarget = "NwdViewer.ApsClientId";
    private const string ClientSecretTarget = "NwdViewer.ApsClientSecret";
    private const string BucketKeyTarget = "NwdViewer.ApsBucketKey";

    public ApsCredentials? Load()
    {
        var clientId = ReadPassword(ClientIdTarget);
        var secret = ReadPassword(ClientSecretTarget);
        var bucket = ReadPassword(BucketKeyTarget);
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(bucket))
            return null;
        return new ApsCredentials(clientId, secret, bucket);
    }

    public void Save(ApsCredentials creds)
    {
        WritePassword(ClientIdTarget, creds.ClientId);
        WritePassword(ClientSecretTarget, creds.ClientSecret);
        WritePassword(BucketKeyTarget, creds.BucketKey);
    }

    private static string? ReadPassword(string target)
    {
        using var cred = new Credential { Target = target };
        return cred.Load() ? cred.Password : null;
    }

    private static void WritePassword(string target, string value)
    {
        using var cred = new Credential
        {
            Target = target,
            Password = value,
            Username = target,
            PersistanceType = PersistanceType.LocalComputer,
        };
        cred.Save();
    }
}

public sealed record ApsCredentials(string ClientId, string ClientSecret, string BucketKey);
