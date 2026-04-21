using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using NwdViewer.Desktop.Services;
using NwdViewer.Desktop.ViewModels;
using NwdViewer.Desktop.Views;
using Serilog;

namespace NwdViewer.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _webViewReady;
    private bool _jsReady;
    private readonly Queue<string> _pendingMessages = new();
    private readonly Dictionary<int, string> _tabFolders = new();

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(new CredentialStore());
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Loaded += OnLoaded;
        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        try
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            if (Viewer?.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnViewerMessage;
                core.WebResourceRequested -= OnWebResourceRequested;
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Cleanup during close failed."); }
        finally { _vm.Dispose(); }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab) && _vm.ActiveTab != null)
        {
            PostOrQueue(new { type = "switchTab", tabId = _vm.ActiveTab.TabId });
            if (_vm.ActiveTab.Mode == TabMode.Aps && _vm.ActiveTab.Urn != null && _vm.ActiveTab.Token != null)
                PostOrQueue(new { type = "loadAps", tabId = _vm.ActiveTab.TabId, urn = _vm.ActiveTab.Urn, token = _vm.ActiveTab.Token });
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Viewer.EnsureCoreWebView2Async();
            Viewer.CoreWebView2.WebMessageReceived += OnViewerMessage;
            Viewer.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "nwdviewer.app", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.Allow);
            Viewer.CoreWebView2.AddWebResourceRequestedFilter(
                "https://nwdviewer.local/*", CoreWebView2WebResourceContext.All);
            Viewer.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            _webViewReady = true;
            Viewer.CoreWebView2.Navigate("https://nwdviewer.app/viewer.html");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 initialization failed.");
            MessageBox.Show(this, $"WebView2 failed to initialize: {ex.Message}",
                "Viewer Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PostOrQueue(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        if (_jsReady && _webViewReady)
            Viewer.CoreWebView2.PostWebMessageAsString(json);
        else
            _pendingMessages.Enqueue(json);
    }

    private void OnViewerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;

            switch (typeProp.GetString())
            {
                case "ready":
                    _jsReady = true;
                    while (_pendingMessages.Count > 0)
                        Viewer.CoreWebView2.PostWebMessageAsString(_pendingMessages.Dequeue());
                    break;

                case "loaded":
                    _vm.StatusText = "Loaded.";
                    break;

                case "selection":
                {
                    var tabId = doc.RootElement.GetPropertyOrNull("tabId")?.GetInt32();
                    var dbId  = doc.RootElement.GetPropertyOrNull("dbId")?.GetInt32();
                    var tab   = FindTab(tabId);
                    if (tab != null && tab.Mode == TabMode.Aps && dbId.HasValue && tab.ApsModelGuid != null)
                        _ = _vm.LoadApsPropertiesAsync(tab, dbId.Value, tab.ApsModelGuid);
                    break;
                }

                case "selectionOffline":
                {
                    var tabId = doc.RootElement.GetPropertyOrNull("tabId")?.GetInt32();
                    var tab   = FindTab(tabId);
                    if (tab != null)
                        _vm.PopulateOfflineProperties(tab,
                            name:        doc.RootElement.GetPropertyOrNull("name")?.GetString() ?? "(unknown)",
                            meshType:    doc.RootElement.GetPropertyOrNull("meshType")?.GetString() ?? string.Empty,
                            material:    doc.RootElement.GetPropertyOrNull("material")?.GetString() ?? string.Empty,
                            vertexCount: doc.RootElement.GetPropertyOrNull("vertexCount")?.GetInt32() ?? 0);
                    break;
                }

                case "error":
                    _vm.StatusText = "Viewer error: " + (doc.RootElement.GetPropertyOrNull("message")?.GetString() ?? "unknown");
                    break;

                case "jsError":
                    var jsMsg   = doc.RootElement.GetPropertyOrNull("message")?.GetString();
                    var jsFile  = doc.RootElement.GetPropertyOrNull("filename")?.GetString();
                    var jsLine  = doc.RootElement.GetPropertyOrNull("lineno")?.GetInt32();
                    var jsStack = doc.RootElement.GetPropertyOrNull("stack")?.GetString();
                    Log.Error("JS error: {Msg} @ {File}:{Line}\n{Stack}", jsMsg, jsFile, jsLine, jsStack);
                    _vm.StatusText = $"JS error: {jsMsg}";
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse viewer message.");
        }
    }

    private TabViewModel? FindTab(int? id)
        => id.HasValue ? _vm.Tabs.FirstOrDefault(t => t.TabId == id.Value) : null;

    private async void OnOpenApsFile(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        if (_vm.GetCredentials() == null)
        {
            MessageBox.Show(this, "Configure APS credentials first (Settings > APS Credentials).",
                "Missing credentials", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Filter = "Navisworks files (*.nwd;*.nwc)|*.nwd;*.nwc|All files (*.*)|*.*",
            Title = "Open Navisworks file",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var (urn, token, modelGuid) = await _vm.TranslateAsync(dlg.FileName);
            var tab = _vm.AddApsTab(dlg.FileName, urn, token);
            tab.ApsModelGuid = modelGuid;
            PostOrQueue(new { type = "createTab", tabId = tab.TabId, mode = "aps" });
            PostOrQueue(new { type = "loadAps", tabId = tab.TabId, urn, token });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "APS open failed.");
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenOfflineFile(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        var dlg = new OpenFileDialog
        {
            Filter = "3D model files (*.ifc;*.gltf;*.glb;*.obj;*.fbx;*.stl)|*.ifc;*.gltf;*.glb;*.obj;*.fbx;*.stl|All files (*.*)|*.*",
            Title = "Open 3D model file",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var path = dlg.FileName;
            var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var folder = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("File has no directory.");
            var fileName = Path.GetFileName(path);

            var tab = _vm.AddOfflineTab(path);
            _tabFolders[tab.TabId] = folder;
            PostOrQueue(new { type = "createTab", tabId = tab.TabId, mode = "offline" });
            PostOrQueue(new
            {
                type = "loadOffline",
                tabId = tab.TabId,
                url = $"https://nwdviewer.local/{tab.TabId}/{Uri.EscapeDataString(fileName)}",
                format,
            });
            _vm.StatusText = $"Loading {fileName} (offline)...";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Offline open failed.");
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is TabViewModel tab)
            _vm.SetActive(tab);
    }

    private void OnCloseTabButton(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TabViewModel tab)
            CloseTab(tab);
    }

    private void OnCloseActiveTab(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveTab != null) CloseTab(_vm.ActiveTab);
    }

    private void CloseTab(TabViewModel tab)
    {
        PostOrQueue(new { type = "closeTab", tabId = tab.TabId });
        _tabFolders.Remove(tab.TabId);
        _vm.CloseTab(tab);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            // Path form: /{tabId}/{filename}
            var parts = uri.AbsolutePath.TrimStart('/').Split('/', 2);
            if (parts.Length < 2 || !int.TryParse(parts[0], out var tabId) || !_tabFolders.TryGetValue(tabId, out var folder))
            {
                e.Response = Viewer.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            var rawName = Uri.UnescapeDataString(parts[1]);

            // Reject drive letters, absolute paths, or anything resembling a path separator/traversal.
            // Path.Combine would ignore our folder if rawName is absolute on Windows.
            if (rawName.Contains(':') || rawName.Contains('\\') || rawName.StartsWith('/') || Path.IsPathRooted(rawName))
            {
                Log.Warning("Rejected suspicious filename '{Name}' for tab {Tab}", rawName, tabId);
                e.Response = Viewer.CoreWebView2.Environment.CreateWebResourceResponse(null, 400, "Bad Request", "");
                return;
            }

            var folderFull = Path.GetFullPath(folder);
            var fullPath = Path.GetFullPath(Path.Combine(folderFull, rawName));

            // Final check: resolved path must still be inside the tab's folder (prevents ../ traversal).
            var sep = Path.DirectorySeparatorChar.ToString();
            var folderPrefix = folderFull.EndsWith(sep) ? folderFull : folderFull + sep;
            if (!fullPath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                e.Response = Viewer.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            // Read into MemoryStream so we don't leak a FileStream handle (CreateWebResourceResponse
            // does not dispose the supplied stream). The underlying byte[] is GC-managed.
            byte[] bytes = File.ReadAllBytes(fullPath);
            var ms = new MemoryStream(bytes, writable: false);

            var mime = GetMimeType(Path.GetExtension(rawName));
            var headers = $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store";
            e.Response = Viewer.CoreWebView2.Environment.CreateWebResourceResponse(ms, 200, "OK", headers);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebResourceRequested failed for {Uri}", e.Request.Uri);
            _vm.StatusText = "File serving error: " + ex.Message;
            try { e.Response = Viewer.CoreWebView2.Environment.CreateWebResourceResponse(null, 500, "Error", ""); } catch { }
        }
    }

    private static string GetMimeType(string ext) => ext.ToLowerInvariant() switch
    {
        ".fbx" or ".glb" or ".stl" or ".bin" => "application/octet-stream",
        ".obj" or ".mtl" or ".ifc" => "text/plain",
        ".gltf" => "model/gltf+json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".tga" => "image/x-tga",
        _ => "application/octet-stream",
    };

    private void OnConfigureCredentials(object sender, RoutedEventArgs e)
    {
        var existing = _vm.GetCredentials();
        var window = new SettingsWindow(existing) { Owner = this };
        if (window.ShowDialog() == true && window.Result != null)
            _vm.SaveCredentials(window.Result);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnShowHelp(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = this };
        help.Show();
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NwdViewer", "logs");
        Directory.CreateDirectory(dir);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open log folder.");
            MessageBox.Show(this, dir, "Log folder path", MessageBoxButton.OK);
        }
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F1)
        {
            OnShowHelp(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}

internal static class JsonDocumentExtensions
{
    public static JsonElement? GetPropertyOrNull(this JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) ? prop : null;
}
