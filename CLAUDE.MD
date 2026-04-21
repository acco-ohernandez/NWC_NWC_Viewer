# NwdViewer — 3D Model Viewer

Standalone desktop viewer for **NWD, NWC, FBX, IFC, glTF, OBJ, STL**. WPF (.NET 8) shell hosting a WebView2 that runs three.js locally and the Autodesk APS Viewer (7.x) via CDN. Multi-tab: each opened file is its own tab with independent state.

Solution root: `C:\Visual Studio Files\NWC_NWC_Viewer\`

## Solution layout

- `NwdViewer.Aps/` — thin REST wrapper over Autodesk Platform Services (OAuth, OSS, Model Derivative). Pure .NET 8 library.
- `NwdViewer.Desktop/` — WPF (.NET 8 Windows) app. The actual UI + WebView2 host.
- `NwdViewer.Tests/` — xUnit tests for the APS client.

## File-to-feature map

When you need to change X, look in Y:

| Topic | File | What's there |
|---|---|---|
| Tab strip UI, menus, status bar | `NwdViewer.Desktop/MainWindow.xaml` | XAML layout, TabItem data template |
| Tab open/close/switch, messaging bridge, file serving | `NwdViewer.Desktop/MainWindow.xaml.cs` | `OnOpenApsFile`, `OnOpenOfflineFile`, `OnViewerMessage`, `OnWebResourceRequested` |
| Tab / active-tab ObservableCollection, APS translate pipeline, property-panel data | `NwdViewer.Desktop/ViewModels/MainViewModel.cs` | `AddOfflineTab`, `AddApsTab`, `TranslateAsync`, `LoadApsPropertiesAsync`, `PopulateOfflineProperties` |
| Per-tab DTO bound to the tab strip | `NwdViewer.Desktop/ViewModels/TabViewModel.cs` | TabId, Title, Mode, Urn, Properties |
| Property tree node | `NwdViewer.Desktop/ViewModels/PropertyNode.cs` | Key, Value, recursive Children |
| **All three.js + APS Viewer code** | `NwdViewer.Desktop/viewer.html` | Single merged HTML — don't split without good reason |
| APS credential storage | `NwdViewer.Desktop/Services/CredentialStore.cs` | Windows Credential Manager wrapper |
| APS HttpClient + client wiring | `NwdViewer.Desktop/Services/ApsServices.cs` | One HttpClient, three APS clients |
| APS Client ID / Secret dialog | `NwdViewer.Desktop/Views/SettingsWindow.xaml[.cs]` | |
| In-app user guide | `NwdViewer.Desktop/Views/HelpWindow.xaml[.cs]` | 5 tabs: Usage / Keys / Formats / Limitations / About |
| App startup, Serilog bootstrap | `NwdViewer.Desktop/App.xaml.cs` | |
| APS OAuth token + caching | `NwdViewer.Aps/AuthClient.cs` | 2-legged flow, separate internal vs viewer scopes |
| APS file upload (signed S3) | `NwdViewer.Aps/OssClient.cs` | EnsureBucket, 2-phase upload |
| APS translation + poll + metadata | `NwdViewer.Aps/ModelDerivativeClient.cs` | StartTranslationAsync, WaitForTranslationAsync, GetObjectPropertiesAsync |
| APS DTOs / JSON records | `NwdViewer.Aps/ApsModels.cs` | |
| APS client configuration | `NwdViewer.Aps/ApsOptions.cs` | ClientId, ClientSecret, BucketKey |
| AuthClient unit tests | `NwdViewer.Tests/AuthClientTests.cs` | Only test file — token caching covered |
| Vendored three.js + web-ifc | `NwdViewer.Desktop/vendor/**` | Do NOT hand-edit except the `mergeGeometries` patch |

## Dependencies

**Runtime (.NET 8):**
- `Microsoft.Web.WebView2` — Chromium host control
- `CommunityToolkit.Mvvm` — `[ObservableProperty]` source generators
- `Serilog` + `Serilog.Sinks.File` + `Serilog.Sinks.Debug`
- `CredentialManagement` — Windows Credential Manager. NU1701 expected (.NET Framework package via P/Invoke).
- `Microsoft.Extensions.Http` — plain HttpClient

**Test:** `xunit`, `Moq`

**Vendored JS (`NwdViewer.Desktop/vendor/`):**
- three.js **0.149.0** (pinned — web-ifc-three peer-requires ^0.149)
- web-ifc **0.0.44**
- web-ifc-three **0.0.126** (deprecated upstream but functional)
- fflate **0.8.2** (FBXLoader dep)

**CDN (only for APS tabs):**
- `https://developer.api.autodesk.com/modelderivative/v2/viewers/7.*/viewer3D.min.js`

## Build / run

```bash
# From solution root:
dotnet build NWC_NWC_Viewer.sln
dotnet test NwdViewer.Tests/NwdViewer.Tests.csproj
# Output exe:
NwdViewer.Desktop\bin\Debug\net8.0-windows\NwdViewer.Desktop.exe
```

The exe holds a lock on itself while running — close the app before rebuilding.

## Testing

One test project (`NwdViewer.Tests`) with one test class (`AuthClientTests.cs`). Coverage is **thin on purpose** — only the APS auth token-caching logic is tested because it has non-obvious state. Everything else is UI or thin wrappers.

If adding tests:
- The APS clients (`AuthClient`, `OssClient`, `ModelDerivativeClient`) are designed for easy `HttpMessageHandler` stubbing — see `AuthClientTests.StubHandler` for the pattern.
- Don't try to test WPF/WebView2 — there's no headless mode that's worth the setup cost. For UI bugs, add diagnostic logging and reproduce manually.

Run: `dotnet test NwdViewer.Tests/NwdViewer.Tests.csproj`

## Architecture

### Two rendering paths in one WebView2

`viewer.html` (single merged page) contains **both** three.js (offline formats) and the Autodesk Viewer (APS). The active tab's `mode` ('offline' | 'aps') toggles which DOM container is visible via `body.aps-active` CSS class. Only one WebView2 instance is ever created.

### Tab state lives in JS

`Map<tabId, TabState>` in [viewer.html](NwdViewer.Desktop/viewer.html). Each TabState owns its own three.js `Scene` + `Camera` (for offline) or stores `urn`/`token` (for APS). Switching tabs swaps the camera on the **shared** `OrbitControls` + shared `WebGLRenderer`. Tab tree, section plane, selection — all per-tab.

**Fields on a `TabState`:**

Offline mode:
- `tabId: number`, `mode: 'offline'`
- `scene: THREE.Scene`, `camera: THREE.PerspectiveCamera`
- `loadedObject: THREE.Object3D | null` — root of the loaded model
- `nodeIndex: Array<{ id, parentId, object3d, depth, expanded }>` — flat array for the virtualized tree
- `selectedId: number | null` — index into nodeIndex
- `ifcModelID: number | null` — only set for IFC tabs; passed to web-ifc manager
- `hiddenIfcSubsets: Map<typeName, THREE.Mesh>` — for IFC type-filter hide state
- `clipActive, clipAxis, clipSlider, clipDir, clipPlane, clipRange` — section-plane state
- `cameraState: { pos, target } | null` — snapshotted on tab switch so camera resumes

APS mode:
- `tabId: number`, `mode: 'aps'`
- `urn: string | null`, `token: string | null`
- (No scene/camera — the Autodesk Viewer manages its own)

The Autodesk Viewer itself is a **singleton** at the module scope (`apsViewer`, `apsInitialized`). Switching between two APS tabs unloads the prior model before loading the new URN.

### WPF <-> JS bridge

Every message carries `tabId` (except `ready` and global errors). `MainWindow.xaml.cs` queues outgoing messages in `_pendingMessages` until JS posts `ready`.

**WPF → JS payloads:**

| type | fields | purpose |
|---|---|---|
| `createTab` | `tabId, mode` ('offline'/'aps') | allocate a TabState in JS |
| `switchTab` | `tabId` | make this tab active (swap camera, toggle containers) |
| `closeTab` | `tabId` | dispose TabState, free geometries + IFC model |
| `loadOffline` | `tabId, url, format` | start three.js load (format = gltf/glb/obj/fbx/stl/ifc) |
| `loadAps` | `tabId, urn, token` | load APS document in the singleton Autodesk Viewer |

**JS → WPF payloads:**

| type | fields | purpose |
|---|---|---|
| `ready` | — | JS has attached its listener; host flushes pending queue |
| `loaded` | `tabId` | model finished loading; status updates |
| `selection` | `tabId, dbId` | APS tab: element clicked → host fetches properties |
| `selectionOffline` | `tabId, name, meshType, material, vertexCount, distance` | offline tab: mesh clicked → host populates panel |
| `error` | `message` | APS viewer internal error (document load failure, etc.) |
| `jsError` | `message, filename, lineno, stack, tabId?` | uncaught JS throw or rejected promise; logged + shown in status bar |

### APS translation pipeline

When the user picks **File → Open .NWD / .NWC (via APS)**, `MainViewModel.TranslateAsync` does (in order):

1. **Auth** — `AuthClient.GetInternalTokenAsync` — 2-legged OAuth, token cached until expiry. Scope: `data:read data:write data:create bucket:create bucket:read`.
2. **Bucket** — `OssClient.EnsureBucketAsync` — creates `BucketKey` on APS if missing (409 = already exists = fine). Policy `transient` (auto-delete after 24h).
3. **Upload** — `OssClient.UploadAsync` — signed-S3 two-phase upload: GET `/signeds3upload` → PUT file to returned URL → POST `/signeds3upload` with `uploadKey` to complete. Returns a base64 URN.
4. **Translate** — `ModelDerivativeClient.StartTranslationAsync` — POSTs a job for output format SVF2 (2d + 3d views).
5. **Poll** — `ModelDerivativeClient.WaitForTranslationAsync` — re-reads `/manifest` every 4s; resolves on `status: success`, throws on `failed` or `timeout`.
6. **Metadata** — `ModelDerivativeClient.GetMetadataAsync` — grabs the first `"3d"` role's GUID, stored as `TabViewModel.ApsModelGuid` (needed later for property lookup).
7. **Viewer token** — `AuthClient.GetViewerTokenAsync` — a separate token scoped only to `viewables:read`, passed to the JS Autodesk Viewer.

The tab is created with the URN + viewer token; JS calls `Autodesk.Viewing.Document.load('urn:' + urn)` → `loadDocumentNode(doc, defaultGeometry)`. Clicks in the viewer post `{type:'selection', tabId, dbId}` → host calls `GetObjectPropertiesAsync(urn, modelGuid, dbId)` → property panel.

### Credentials storage

APS Client ID + Secret + BucketKey live in **Windows Credential Manager** (not in a config file). Target names:

- `NwdViewer.ApsClientId`
- `NwdViewer.ApsClientSecret`
- `NwdViewer.ApsBucketKey`

Persistence type: `LocalComputer` (per-user, this machine). Managed by `Services/CredentialStore.cs`. To inspect/delete: `rundll32.exe keymgr.dll,KRShowKeyMgr` or **Control Panel → Credential Manager → Windows Credentials**.

### Offline file serving (CORS workaround)

Local files are served via `CoreWebView2.WebResourceRequested` (NOT virtual host mapping — the cross-origin `nwdviewer.app` → `nwdviewer.local` was blocked even with `Allow`). URL format: `https://nwdviewer.local/{tabId}/{filename}`. Handler is in `MainWindow.xaml.cs::OnWebResourceRequested`. **Path-traversal is validated**: rejects `:`, `\`, leading `/`, or `IsPathRooted` names, then re-checks the resolved path stays inside the tab's folder.

The exe's own static files (`viewer.html`, `vendor/`) ARE served via virtual host mapping (`https://nwdviewer.app/`) — same-origin, no CORS issues.

### Vendored JS dependencies (offline-capable)

`NwdViewer.Desktop/vendor/` ships bundled for true offline support:

- `three/` — three.js 0.149 + examples/jsm loaders (GLTF, OBJ, MTL, FBX, STL), fflate, NURBS, BufferGeometryUtils
- `web-ifc/` — WASM IFC parser (~5.7 MB)
- `web-ifc-three/` — IFCLoader for three.js

**Pinned to three.js 0.149** because web-ifc-three peer-requires `^0.149`. Upgrade path blocked until web-ifc-three is updated.

**Important patch**: our local `vendor/three/examples/jsm/utils/BufferGeometryUtils.js` has an appended alias `export { mergeBufferGeometries as mergeGeometries };` — web-ifc-three imports the new name (three.js 0.157+) but three.js 0.149 only has the old name. Don't re-run `npm install` on this vendor folder without re-applying that alias.

Autodesk APS Viewer 7.x is loaded from CDN (not vendored), only needed when an APS tab is active.

### Resilience pattern

Every JS event handler is wrapped in `guard(fn, name)`. Thrown errors and rejected promises post a `jsError` message that lands in WPF `StatusText` + Serilog. The render loop has its own try/catch so a bad frame never kills the viewer.

Serilog log: `%LOCALAPPDATA%\NwdViewer\logs\nwdviewer-YYYYMMDD.log` (daily rolling, 7 kept).

## Known gotchas (learned the hard way)

1. **ES modules don't work from `file://`** in Chromium-based WebView2. The HTML must be served from a non-file origin. That's why we navigate to `https://nwdviewer.app/viewer.html` via virtual host mapping, not to a local file path.
2. **Cross-origin fetch between two virtual hosts is blocked** even with `CoreWebView2HostResourceAccessKind.Allow`. Use `WebResourceRequested` with explicit `Access-Control-Allow-Origin: *` headers for model files.
3. **`NavigationCompleted` fires too early** for module scripts — use the JS-side `ready` handshake instead of posting load messages on `NavigationCompleted`.
4. **Navisworks FBX exports are lossy.** Geometry scatters, materials drop, curves coarsen. The Bake FBX button (flatten transforms + hide camera/light artifacts) partially compensates, but use the APS path for real fidelity.
5. **`CredentialManagement` NuGet is .NET Framework-only** (NU1701 warning). It works at runtime on .NET 8-Windows because it only P/Invokes `advapi32.dll`. If it ever misbehaves, swap to `Meziantou.Framework.Win32.CredentialManager`.
6. **`CreateWebResourceResponse` does NOT dispose the content stream.** Use `MemoryStream` (byte[] backed) so the file handle closes immediately after reading.

## Common tasks

**Add a new open-format loader:**
1. Make sure the loader's three.js module + its transitive deps are in `vendor/`.
2. Add the import to `viewer.html` module script.
3. Add a branch in `loadOfflineFile(st, url, format)`.
4. Add the extension to the filter in `MainWindow.xaml.cs::OnOpenOfflineFile`.
5. Add an entry to the Help → File Formats table in `Views/HelpWindow.xaml`.

**Add a tab-scoped UI control (e.g. grid visibility):**
1. Add the state field to the `TabState` class constructor in `viewer.html`.
2. Update `updateUiFromActiveTab()` to read it into the UI control.
3. Wire the control's event to write back to `getActive()`.
4. Apply the state in `switchTab` where the control is synced.

**Change APS credentials scope:** `NwdViewer.Aps/AuthClient.cs` — `GetInternalTokenAsync` and `GetViewerTokenAsync` have hardcoded scope strings.

**Rebuild blocked by "file locked"**: the running exe is holding it. Either close the app, or skip the rebuild and just copy the changed HTML file into `bin/Debug/net8.0-windows/` — WebView2 re-reads the HTML on each navigation, so HTML-only changes don't require a C# rebuild.

## What this app is NOT

- No editing / saving / export of model data.
- No measurement, markup, or annotation tools.
- No multi-select.
- No support for .rvt, .dwg, .3dm, .step, .skp — convert to IFC / glTF / OBJ first.
- Not optimized for batch operations; one user, one window.
