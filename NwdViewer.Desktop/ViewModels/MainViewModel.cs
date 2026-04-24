using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using NwdViewer.Aps;
using NwdViewer.Desktop.Services;
using Serilog;

namespace NwdViewer.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly CredentialStore _credentials;
    private ApsServices? _aps;
    private int _nextTabId = 1;

    [ObservableProperty] private string statusText = "Ready.";
    [ObservableProperty] private int progressPercent;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private TabViewModel? activeTab;

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public MainViewModel(CredentialStore credentials)
    {
        _credentials = credentials;
    }

    public bool HasCredentials => _credentials.Load() != null;
    public ApsCredentials? GetCredentials() => _credentials.Load();
    public void SaveCredentials(ApsCredentials creds)
    {
        _credentials.Save(creds);
        _aps?.Dispose();
        _aps = null;
    }

    public int NewTabId() => _nextTabId++;

    public TabViewModel AddOfflineTab(string filePath)
    {
        var tab = new TabViewModel(NewTabId(), TabMode.Offline)
        {
            Title = Path.GetFileName(filePath),
            FilePath = filePath,
        };
        AddTab(tab);
        return tab;
    }

    public TabViewModel AddApsTab(string filePath, string urn, string token)
    {
        var tab = new TabViewModel(NewTabId(), TabMode.Aps)
        {
            Title = Path.GetFileName(filePath) + " (APS)",
            FilePath = filePath,
            Urn = urn,
            Token = token,
        };
        AddTab(tab);
        return tab;
    }

    private void AddTab(TabViewModel tab)
    {
        Tabs.Add(tab);
        SetActive(tab);
    }

    public void CloseTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.Remove(tab);
        if (ReferenceEquals(ActiveTab, tab))
        {
            var next = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
            SetActive(next);
        }
    }

    public void SetActive(TabViewModel? tab)
    {
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, tab);
        ActiveTab = tab;
    }

    private ApsServices EnsureServices()
    {
        if (_aps != null) return _aps;
        var creds = _credentials.Load() ?? throw new InvalidOperationException("APS credentials are not configured.");
        _aps = new ApsServices(creds);
        return _aps;
    }

    public async Task<(string urn, string token, string? modelGuid)> TranslateAsync(string localPath, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var aps = EnsureServices();
            StatusText = "Ensuring APS bucket...";
            ProgressPercent = 0;
            await aps.Oss.EnsureBucketAsync(ct);

            var objectKey = Path.GetFileName(localPath);
            StatusText = $"Uploading {objectKey} to APS...";
            var progress = new Progress<int>(p => ProgressPercent = Math.Min(p, 95));
            var urn = await aps.Oss.UploadAsync(localPath, objectKey, progress, ct);
            Log.Information("Uploaded {Path} urn={Urn}", localPath, urn);

            StatusText = "Starting translation...";
            ProgressPercent = 0;
            await aps.ModelDerivative.StartTranslationAsync(urn, ct);

            StatusText = "Translating (can take several minutes for large files)...";
            await aps.ModelDerivative.WaitForTranslationAsync(urn, new Progress<int>(p => ProgressPercent = p), ct);

            var metadata = await aps.ModelDerivative.GetMetadataAsync(urn, ct);
            var primary = metadata.FirstOrDefault(m => m.Role == "3d") ?? metadata.FirstOrDefault();

            StatusText = "Fetching viewer token...";
            var token = await aps.Auth.GetViewerTokenAsync(ct);

            StatusText = $"Ready. URN: {urn}";
            ProgressPercent = 100;
            return (urn, token, primary?.Guid);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void PopulateOfflineProperties(TabViewModel tab, string name, string meshType, string material, int vertexCount)
    {
        tab.Properties.Clear();
        tab.Properties.Add(new PropertyNode { Key = "Name", Value = name });
        tab.Properties.Add(new PropertyNode { Key = "Type", Value = meshType });
        tab.Properties.Add(new PropertyNode { Key = "Material", Value = material });
        tab.Properties.Add(new PropertyNode { Key = "Vertices", Value = vertexCount.ToString() });
        StatusText = $"Selected: {name}";
    }

    public async Task LoadApsPropertiesAsync(TabViewModel tab, int objectId, string modelGuid, CancellationToken ct = default)
    {
        if (_aps == null || tab.Urn == null) return;
        try
        {
            var props = await _aps.ModelDerivative.GetObjectPropertiesAsync(tab.Urn, modelGuid, objectId, ct);
            tab.Properties.Clear();

            var entry = props?.Data?.Collection?.FirstOrDefault();
            if (entry == null)
            {
                // APS sometimes returns 200 with no collection while still indexing.
                // Log the raw body so we can see the actual shape and diagnose.
                var raw = _aps.ModelDerivative.LastPropertiesRawBody ?? "(no body)";
                Log.Information("APS properties: no collection for dbId={Id}. Raw: {Body}",
                    objectId, raw.Length > 400 ? raw[..400] + "..." : raw);
                tab.Properties.Add(new PropertyNode { Key = "Info",
                    Value = "Properties not available yet — APS may still be indexing. Try again in a moment." });
                StatusText = $"Object #{objectId}: properties not yet available.";
                return;
            }

            tab.Properties.Add(new PropertyNode { Key = "Name", Value = entry.Name ?? "(unnamed)" });
            if (!string.IsNullOrEmpty(entry.ExternalId))
                tab.Properties.Add(new PropertyNode { Key = "External ID", Value = entry.ExternalId });
            if (entry.Properties != null)
            {
                foreach (var category in entry.Properties)
                {
                    var catNode = new PropertyNode { Key = category.Key };
                    if (category.Value != null)
                    {
                        foreach (var prop in category.Value)
                            catNode.Children.Add(new PropertyNode { Key = prop.Key, Value = prop.Value?.ToString() ?? string.Empty });
                    }
                    tab.Properties.Add(catNode);
                }
            }
            StatusText = $"Selected: {entry.Name ?? $"#{objectId}"}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load properties for object {Id}", objectId);
            StatusText = $"Properties error: {ex.Message}";
        }
    }

    public void Dispose() => _aps?.Dispose();
}
