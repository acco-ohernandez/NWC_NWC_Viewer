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
    private string? _pendingSavePath;
    private string? _pendingPdfPath;

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
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            // Hide the Properties panel + splitter when active tab is APS — the Autodesk
            // viewer has its own Model Browser and Properties panels in its bottom toolbar.
            var isAps = _vm.ActiveTab?.Mode == TabMode.Aps;
            PropertiesPanel.Visibility        = isAps ? Visibility.Collapsed : Visibility.Visible;
            PropertiesSplitter.Visibility     = isAps ? Visibility.Collapsed : Visibility.Visible;
            PropertiesColumn.Width            = isAps ? new GridLength(0) : new GridLength(360);
            PropertiesSplitterColumn.Width    = isAps ? new GridLength(0) : new GridLength(5);

            if (_vm.ActiveTab != null)
            {
                PostOrQueue(new { type = "switchTab", tabId = _vm.ActiveTab.TabId });
                if (_vm.ActiveTab.Mode == TabMode.Aps && _vm.ActiveTab.Urn != null && _vm.ActiveTab.Token != null)
                    PostOrQueue(new
                    {
                        type  = "loadAps",
                        tabId = _vm.ActiveTab.TabId,
                        urn   = NwdViewer.Aps.OssClient.WithoutPrefix(_vm.ActiveTab.Urn),
                        token = _vm.ActiveTab.Token
                    });
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var bundled = FindBundledRuntime(Path.Combine(AppContext.BaseDirectory, "webview2-runtime"));
            CoreWebView2Environment env;
            if (bundled != null)
            {
                Log.Information("Using bundled WebView2 runtime at {Path}", bundled);
                env = await CoreWebView2Environment.CreateAsync(bundled, userDataFolder: null, options: null);
            }
            else
            {
                Log.Information("Using installed WebView2 runtime");
                env = await CoreWebView2Environment.CreateAsync();
            }
            await Viewer.EnsureCoreWebView2Async(env);
            // Let files dropped on the window reach WPF's Drop handler instead of being
            // swallowed by WebView2's default file-drop behavior.
            try { Viewer.AllowExternalDrop = false; } catch { /* property not available on older WebView2; ignore */ }
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

    /// <summary>
    /// Looks for a Fixed-Version WebView2 runtime next to the exe. Returns the folder that
    /// contains msedgewebview2.exe (either the base folder itself, or its first matching child).
    /// </summary>
    private static string? FindBundledRuntime(string baseDir)
    {
        if (!Directory.Exists(baseDir)) return null;
        if (File.Exists(Path.Combine(baseDir, "msedgewebview2.exe"))) return baseDir;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(baseDir))
            {
                if (File.Exists(Path.Combine(sub, "msedgewebview2.exe"))) return sub;
            }
        }
        catch { /* unreadable dir — fall through */ }
        return null;
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
                    _vm.IsBusy = false;
                    _vm.ProgressPercent = 100;
                    break;

                case "loadStart":
                    _vm.IsBusy = true;
                    _vm.ProgressPercent = 0;
                    var fmt = doc.RootElement.GetPropertyOrNull("format")?.GetString();
                    _vm.StatusText = string.IsNullOrEmpty(fmt) ? "Loading..." : $"Loading {fmt.ToUpperInvariant()}...";
                    break;

                case "loadProgress":
                    var pct = doc.RootElement.GetPropertyOrNull("percent")?.GetInt32() ?? 0;
                    _vm.ProgressPercent = pct;
                    break;

                case "loadEnd":
                    _vm.IsBusy = false;
                    break;

                case "selection":
                {
                    var tabId = doc.RootElement.GetPropertyOrNull("tabId")?.GetInt32();
                    var dbId  = doc.RootElement.GetPropertyOrNull("dbId")?.GetInt32();
                    var tab   = FindTab(tabId);
                    Log.Information("APS selection: tabId={Tab} dbId={Db} tabFound={Found} mode={Mode} modelGuid={Guid}",
                        tabId, dbId, tab != null, tab?.Mode, tab?.ApsModelGuid);
                    if (tab != null && tab.Mode == TabMode.Aps && dbId.HasValue && tab.ApsModelGuid != null)
                        _ = _vm.LoadApsPropertiesAsync(tab, dbId.Value, tab.ApsModelGuid);
                    else if (tab != null && tab.ApsModelGuid == null)
                        _vm.StatusText = "Cannot load properties — APS model GUID not set.";
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

                case "apsDiag":
                    Log.Information("APS diag: {Msg}", doc.RootElement.GetPropertyOrNull("msg")?.GetString());
                    break;

                case "imageData":
                {
                    var purpose = doc.RootElement.GetPropertyOrNull("purpose")?.GetString() ?? "image";
                    var dataUrl = doc.RootElement.GetPropertyOrNull("dataUrl")?.GetString();
                    var bytes = DecodeDataUrl(dataUrl);
                    try
                    {
                        if (purpose == "pdf" && _pendingPdfPath != null)
                        {
                            if (bytes == null || bytes.Length == 0) { _vm.StatusText = "PDF export: capture returned empty data."; }
                            else
                            {
                                PdfExport.WriteSinglePage(_pendingPdfPath, bytes, _vm.ActiveTab?.Title ?? "Model");
                                _vm.StatusText = $"Saved PDF: {Path.GetFileName(_pendingPdfPath)}";
                            }
                            _pendingPdfPath = null;
                        }
                        else if (_pendingSavePath != null)
                        {
                            if (bytes == null || bytes.Length == 0) { _vm.StatusText = "Image capture returned empty data."; }
                            else
                            {
                                File.WriteAllBytes(_pendingSavePath, bytes);
                                _vm.StatusText = $"Saved: {Path.GetFileName(_pendingSavePath)}";
                            }
                            _pendingSavePath = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Save failed ({Purpose})", purpose);
                        _vm.StatusText = "Save failed: " + ex.Message;
                        _pendingSavePath = null; _pendingPdfPath = null;
                    }
                    break;
                }

                case "jsError":
                    var jsMsg   = doc.RootElement.GetPropertyOrNull("message")?.GetString();
                    var jsFile  = doc.RootElement.GetPropertyOrNull("filename")?.GetString();
                    var jsLine  = doc.RootElement.GetPropertyOrNull("lineno")?.GetInt32();
                    var jsStack = doc.RootElement.GetPropertyOrNull("stack")?.GetString();
                    Log.Error("JS error: {Msg} @ {File}:{Line}\n{Stack}", jsMsg, jsFile, jsLine, jsStack);
                    _vm.StatusText = $"JS error: {jsMsg}";
                    _vm.IsBusy = false;
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

    private static readonly HashSet<string> OfflineExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ifc", ".gltf", ".glb", ".obj", ".fbx", ".stl"
    };
    private static readonly HashSet<string> ApsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nwd", ".nwc"
    };

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
            Title = "Open Navisworks file(s)",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        await OpenPathsAsync(dlg.FileNames);
    }

    private async void OnOpenOfflineFile(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        var dlg = new OpenFileDialog
        {
            Filter = "3D model files (*.ifc;*.gltf;*.glb;*.obj;*.fbx;*.stl)|*.ifc;*.gltf;*.glb;*.obj;*.fbx;*.stl|All files (*.*)|*.*",
            Title = "Open 3D model file(s)",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        await OpenPathsAsync(dlg.FileNames);
    }

    /// <summary>
    /// Opens a mixed batch of files. Offline formats open in parallel (cheap local loads).
    /// APS formats open sequentially to avoid hammering the Model Derivative API with
    /// parallel translation jobs.
    /// </summary>
    private async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        if (!_webViewReady) return;

        var offline = new List<string>();
        var aps     = new List<string>();
        var unknown = new List<string>();
        foreach (var p in paths)
        {
            var ext = Path.GetExtension(p);
            if      (OfflineExtensions.Contains(ext)) offline.Add(p);
            else if (ApsExtensions.Contains(ext))     aps.Add(p);
            else                                       unknown.Add(p);
        }

        // Offline: open all in one pass; three.js handles parallel loads
        foreach (var path in offline) OpenOfflinePath(path);

        // APS: check credentials once, then queue sequentially
        if (aps.Count > 0 && _vm.GetCredentials() == null)
        {
            MessageBox.Show(this,
                $"Configure APS credentials first (Settings > APS Credentials).\n\n{aps.Count} Navisworks file(s) skipped.",
                "Missing credentials", MessageBoxButton.OK, MessageBoxImage.Information);
            aps.Clear();
        }
        for (int i = 0; i < aps.Count; i++)
        {
            var path = aps[i];
            _vm.StatusText = $"Opening APS file {i + 1} of {aps.Count}: {Path.GetFileName(path)}";
            try { await OpenApsPathAsync(path); }
            catch (Exception ex)
            {
                Log.Error(ex, "APS open failed for {Path}", path);
                var cont = MessageBox.Show(this,
                    $"Failed to open '{Path.GetFileName(path)}':\n\n{ex.Message}\n\nContinue with the remaining files?",
                    "APS open failed",
                    aps.Count - i > 1 ? MessageBoxButton.YesNo : MessageBoxButton.OK,
                    MessageBoxImage.Error);
                if (cont == MessageBoxResult.No) break;
            }
        }

        if (unknown.Count > 0)
        {
            _vm.StatusText = $"Skipped {unknown.Count} unsupported file(s).";
        }
    }

    private void OpenOfflinePath(string path)
    {
        try
        {
            var format   = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var folder   = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("File has no directory.");
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

    private async Task OpenApsPathAsync(string path)
    {
        var (urn, token, modelGuid) = await _vm.TranslateAsync(path);
        Log.Information("APS translate complete: urn={Urn} modelGuid={Guid}", urn, modelGuid ?? "(null)");
        var tab = _vm.AddApsTab(path, urn, token);
        tab.ApsModelGuid = modelGuid;
        PostOrQueue(new { type = "createTab", tabId = tab.TabId, mode = "aps" });
        PostOrQueue(new { type = "loadAps", tabId = tab.TabId, urn = NwdViewer.Aps.OssClient.WithoutPrefix(urn), token });
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

    private void OnSaveImage(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveTab == null)
        {
            MessageBox.Show(this, "Open a model first.", "No active tab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var baseName = Path.GetFileNameWithoutExtension(_vm.ActiveTab.Title).Replace(" (APS)", "");
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            DefaultExt = ".png",
            FileName = $"{baseName}.png",
            Title = "Save viewport image",
        };
        if (dlg.ShowDialog(this) != true) return;
        _pendingSavePath = dlg.FileName;
        var format = Path.GetExtension(dlg.FileName).ToLowerInvariant() == ".jpg" ? "image/jpeg" : "image/png";
        PostOrQueue(new { type = "captureImage", format });
    }

    private void OnExportPdf(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveTab == null)
        {
            MessageBox.Show(this, "Open a model first.", "No active tab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var baseName = Path.GetFileNameWithoutExtension(_vm.ActiveTab.Title).Replace(" (APS)", "");
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            FileName = $"{baseName}.pdf",
            Title = "Export viewport to PDF",
        };
        if (dlg.ShowDialog(this) != true) return;
        _pendingPdfPath = dlg.FileName;
        _vm.StatusText = "Rendering hi-res image for PDF...";
        PostOrQueue(new { type = "captureImage", format = "image/png", scale = 3, purpose = "pdf" });
    }

    private static byte[]? DecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl)) return null;
        var comma = dataUrl.IndexOf(',');
        var base64 = comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl;
        return Convert.FromBase64String(base64);
    }

    private void OnConfigureCredentials(object sender, RoutedEventArgs e)
    {
        var existing = _vm.GetCredentials();
        var window = new SettingsWindow(existing) { Owner = this };
        if (window.ShowDialog() == true && window.Result != null)
            _vm.SaveCredentials(window.Result);
    }

    private void OnToggleDarkMode(object sender, RoutedEventArgs e)
    {
        var dark = DarkModeItem.IsChecked;
        PostOrQueue(new { type = "setTheme", theme = dark ? "dark" : "light" });
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnShowHelp(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = this };
        help.Show();
    }

    private void OnShowAbout(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow(startTab: "About") { Owner = this };
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

    private void OnPreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;
        await OpenPathsAsync(files);
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F1)
        {
            OnShowHelp(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.S &&
                 (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            OnSaveImage(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.P &&
                 (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            OnExportPdf(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}

internal static class JsonDocumentExtensions
{
    public static JsonElement? GetPropertyOrNull(this JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) ? prop : null;
}
