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
| PDF export (PDFsharp) | `NwdViewer.Desktop/Services/PdfExport.cs` | Single-page landscape US Letter, embedded hi-res PNG |
| PDFsharp font resolver | `NwdViewer.Desktop/Services/WindowsFontResolver.cs` | Reads TTFs from `%WINDIR%\Fonts` |
| Optional bundled WebView2 runtime | `NwdViewer.Desktop/webview2-runtime/` | Gitignored drop-folder + README |

## Dependencies

**Runtime (.NET 8):**
- `Microsoft.Web.WebView2` — Chromium host control
- `CommunityToolkit.Mvvm` — `[ObservableProperty]` source generators
- `Serilog` + `Serilog.Sinks.File` + `Serilog.Sinks.Debug`
- `CredentialManagement` — Windows Credential Manager. NU1701 expected (.NET Framework package via P/Invoke).
- `Microsoft.Extensions.Http` — plain HttpClient
- `PDFsharp` 6.2.x — single-page PDF export (uses a custom `IFontResolver` to read Windows fonts on .NET Core)

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
- `grid: THREE.GridHelper` — re-created when theme changes
- `loadedObject: THREE.Object3D | null` — root of the loaded model
- `bakeBackup: THREE.Object3D | null` — pre-Bake-FBX scene graph kept for single-level Undo
- `nodeIndex: Array<{ id, parentId, object3d, depth, expanded }>` — flat array for the virtualized tree
- `selectedIds: Set<number>` — indices into nodeIndex (multi-select)
- `lastSelId: number | null` — anchor for Shift+click ranges; last-clicked row
- `ifcModelID: number | null` — only set for IFC tabs; passed to web-ifc manager
- `hiddenIfcSubsets: Map<typeName, THREE.Mesh>` — for IFC type-filter hide state
- `clipActive, clipAxis, clipSlider, clipDir, clipPlane, clipRange` — section-plane state
- `measure: { active, points: Vector3[], group: THREE.Group | null }` — measurement tool state
- `cameraState: { pos, target } | null` — snapshotted on tab switch so camera resumes

APS mode:
- `tabId: number`, `mode: 'aps'`
- `urn: string | null`, `token: string | null`
- `apsCameraState: object | null` — snapshot from `apsViewer.getState({ viewport: true })` so camera
  position/target survives tab switches (restored in `loadDocumentNode.then(...)` via `restoreState`).
- (No scene/camera — the Autodesk Viewer manages its own)

The Autodesk Viewer itself is a **singleton** at the module scope (`apsViewer`, `apsInitialized`). Switching between two APS tabs unloads the prior model before loading the new URN.

**Lighting state is shared across all offline tabs** (module scope, not per-tab):
- `lightingPreset: 'neutral' | 'studio' | 'outdoor' | 'headlight'`
- `lightAzimuth: number` (degrees, 0–360)
- `lightElevation: number` (degrees, −30 to 90)
- `applyLightingToScene(st)` adds preset-based lights tagged `userData.isAppLight = true`.
- `applyLightingToAllTabs()` re-applies to every offline tab after a preset/angle change.
- Headlight: the render loop copies `st.camera.position` onto a light tagged `userData.isHeadlight = true`.

**Theme state is also module-scope** (`currentTheme: 'light' | 'dark'`). `applyTheme(theme)` recolors each tab's scene background and recreates the grid with new colors. A CSS class `body.dark` toggles all HTML panel colors.

### WPF <-> JS bridge

Every message carries `tabId` (except `ready` and global errors). `MainWindow.xaml.cs` queues outgoing messages in `_pendingMessages` until JS posts `ready`.

**WPF → JS payloads:**

| type | fields | purpose |
|---|---|---|
| `createTab` | `tabId, mode` ('offline'/'aps') | allocate a TabState in JS |
| `switchTab` | `tabId` | make this tab active (swap camera, toggle containers) |
| `closeTab` | `tabId` | dispose TabState, free geometries + IFC model |
| `loadOffline` | `tabId, url, format` | start three.js load (format = gltf/glb/obj/fbx/stl/ifc) |
| `loadAps` | `tabId, urn, token` | load APS document in the singleton Autodesk Viewer. **`urn` must be the base64-only form** (no `urn:` prefix) — the JS side prepends its own prefix before calling `Autodesk.Viewing.Document.load`. Use `OssClient.WithoutPrefix(urn)` on the C# side. |
| `setTheme` | `theme` ('light'/'dark') | applies dark-mode CSS class + recolors scenes / grids in every tab |
| `captureImage` | `format, scale?, purpose?` | asks JS to return a data-URL snapshot. `scale` defaults to 1; PDF export uses 3. `purpose` is round-tripped in `imageData`. |

**JS → WPF payloads:**

| type | fields | purpose |
|---|---|---|
| `ready` | — | JS has attached its listener; host flushes pending queue |
| `loadStart` | `tabId, format` | offline load began — WPF flips `IsBusy=true`, `ProgressPercent=0` |
| `loadProgress` | `tabId, percent` | three.js loader onProgress callback — updates status-bar progress |
| `loadEnd` | `tabId` | offline load failed (success path uses `loaded` instead) |
| `loaded` | `tabId` | model finished loading; status updates, `IsBusy=false` |
| `selection` | `tabId, dbId` | APS tab: element clicked → host fetches properties |
| `selectionOffline` | `tabId, name, meshType, material, vertexCount, distance` | offline tab: mesh clicked → host populates panel |
| `imageData` | `dataUrl, purpose` | response to `captureImage` — WPF either writes to the pending image path or wraps it in a PDF, based on `purpose` ('image' or 'pdf') |
| `apsDiag` | `msg` | diagnostic string from APS load path; logged at Information level — used during debugging |
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

### Optional bundled WebView2 Fixed-Version runtime

`NwdViewer.Desktop/webview2-runtime/` is a gitignored drop-folder for an **optional** Fixed-Version WebView2 Runtime. When populated (instructions in the folder's README.md), the runtime gets copied to the output next to the exe, and `MainWindow.FindBundledRuntime(...)` picks it up automatically at startup — the app no longer depends on Edge/WebView2 being installed on the host machine. Useful for Windows Sandbox, clean VMs, air-gapped workstations, and standalone redistributables.

If the folder is empty (default), the app silently falls back to the system-installed WebView2 runtime. Serilog records which path is taken: look for `Using bundled WebView2 runtime at ...` vs. `Using installed WebView2 runtime`.

Cost of bundling: ≈600 MB of extra binaries shipped with the app (CAB is ≈180 MB compressed; disk footprint is the expanded ≈600 MB). Locale packs and unused Edge components can be pruned down to ≈450 MB — see `webview2-runtime/README.md`.

### Panels hide on APS tabs

When the active tab is APS, the WPF Properties panel + splitter are collapsed (`PropertiesColumn.Width=0`, `PropertiesColumn.MinWidth=0`) and the HTML scene-tree panel + resizer are hidden via `body.aps-active` CSS. The viewport is repositioned with `position: absolute; inset: 0` so the APS canvas gets its full measurement *before* the viewer's WebGL context is created — otherwise the Autodesk viewer latches onto a 0×0 canvas and renders nothing. Use the **Autodesk viewer's own** Model Browser (tree icon in its bottom toolbar) and Properties panel (i icon) for APS tabs.

### Multi-file open and drag-and-drop

`OpenPathsAsync(IEnumerable<string>)` is the single entry point — called by both menu handlers and the `Drop` event on the Window. It routes by extension:
- **Offline formats** open in parallel (three.js loaders are independent).
- **APS formats** are queued sequentially — each costs APS credits and parallel translation requests hit rate limits. On a per-file failure the user gets a "continue with remaining?" prompt.

`OpenFileDialog` has `Multiselect = true` for both handlers. Drag-and-drop requires setting `Viewer.AllowExternalDrop = false` so the WebView2 control doesn't swallow the drop before WPF sees it.

### Lighting presets and sun control

The `Lighting:` overlay in the bottom-left of the viewport (only visible on offline tabs) drives global lighting state:
- **Preset dropdown** — `Neutral` / `Studio` (3-point key+fill+rim) / `Outdoor` (hemisphere + sun) / `Headlight` (follows camera).
- **Azimuth slider** (0–360°), **Elevation slider** (−30° to 90°) — disabled when preset is Headlight.
- Applies to *all* open offline tabs at once, not per-tab, because users typically want a consistent look across compared models.
- Lights are tagged `userData.isAppLight = true` and removed on reapply, so the app doesn't accumulate lights or disturb any native model lights.

### Measurement tool

📏 **Measure** button in the bottom-left overlay. When active:
1. Cursor becomes crosshair.
2. First click places a red sphere at the ray-cast hit point. Status shows "pick 2nd point…".
3. Second click places a second sphere, draws a red line, and displays the distance (in model units) next to the button.
4. A third click clears and restarts. Clicking the button again exits and clears.

Per-tab state in `TabState.measure`. Markers render with `renderOrder=9999, depthTest:false` so they're always visible on top of geometry.

### Bake FBX + Undo

Navisworks (and some other exporters) produce FBX with duplicated transform nodes, camera/light placeholders, and ambiguous pivots — resulting in "exploded" models. The **Bake FBX** button:
- Walks every `isMesh`, skipping any whose ancestor chain includes a `THREE.Camera`, `THREE.Light`, or a name matching the artifact regex (`default_camera`, `current_viewport`, `distant_light`, `target_cam`, `*_light`).
- Clones remaining geometries, bakes their world transform into the vertex data, reparents them into a flat `Group`.
- Replaces `loadedObject` with the flat group; keeps the previous root alive in `st.bakeBackup` for Undo.
- **↶ Undo** button next to Bake restores the backup, disposes the baked group. Single level — pressing Bake a second time disposes the previous backup.

### Save Image / Export PDF

- **File → Save Image As...** (Ctrl+S) — WPF picks a path, posts `captureImage` to JS with `purpose='image'`, JS returns a PNG data URL from the 3D viewport only (not panels/overlays). `renderer.domElement.toDataURL('image/png')` for offline; `apsViewer.getScreenShot(w,h,cb)` for APS. Requires `preserveDrawingBuffer: true` on the three.js renderer (already set).
- **File → Export to PDF...** (Ctrl+P) — same `captureImage` path but with `scale=3` (renders at 3× native size, ≈4500×2500 px for a typical window). WPF decodes the PNG and assembles a landscape US Letter PDF with `PdfExport.WriteSinglePage` (PDFsharp). Title header, timestamp, footer with image dimensions.

### Fit button behavior

`frameCameraToBox` preserves the *current* view direction and computes the exact distance needed to fill the viewport, using `camera.fov` and `camera.aspect`. It does **not** snap to an ISO view on click. The "visible" bounding box is built by `getVisibleBoundingBox(root)` which traverses meshes but skips anything whose ancestor chain has `visible === false`, so hidden/isolated geometry is ignored by Fit.

### Dark mode

`Settings → Dark Mode` toggles a session-only theme:
- WPF menu stays native Windows-themed (untouched).
- `viewer.html` adds/removes `body.dark` class → all panels and overlays recolor via CSS selectors scoped to `body.dark`.
- Each offline scene's `background` color is changed and the `GridHelper` is rebuilt with dark colors via `applyTheme(theme)`.

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
7. **APS URN formats differ by endpoint:** `OssClient.UploadAsync` returns `"urn:<base64>"` (with prefix) using URL-safe base64. The Model Derivative POST body's `input.urn` and the GET path segments (`/manifest`, `/metadata`, `.../properties`) need the base64 *without* the `urn:` prefix — strip with `OssClient.WithoutPrefix(urn)`. The APS Viewer's JS-side `Autodesk.Viewing.Document.load('urn:' + urn)` prepends its own prefix, so C# must send the stripped form in the `loadAps` message.
8. **`.gitignore`'s `*.aps`** (for Visual C++ resource files) matches the `NwdViewer.Aps/` directory on case-insensitive Windows. There's an explicit re-include `!NwdViewer.Aps/**` followed by `NwdViewer.Aps/bin/` + `NwdViewer.Aps/obj/` to keep source tracked while excluding build output.
9. **APS viewer canvas sizing**: if the APS viewer's container (`#aps-canvas`) is measured at 0×0 when `new GuiViewer3D()` runs, it renders nothing even after the container later becomes full-sized. Fix: use double-`requestAnimationFrame` after CSS layout changes, call `apsViewer.resize()` + `impl.invalidate(true,true,true)` before and after `loadDocumentNode`.
10. **Don't call `apsViewer.fitToView()` in the load `.then`** — it can override APS's home view and zoom into empty space. Let `loadDocumentNode` set the initial framing; only resize().
11. **Click vs drag**: three.js `click` events fire on a drag-release if movement is within the browser's click threshold. We track `pointerdown`/`pointerup` with a 4-px movement threshold to avoid selecting the object under the cursor after orbiting.
12. **PDFsharp 6.x on .NET Core needs an `IFontResolver`** — the default implementation only ships in the GDI/WPF variants. We register `WindowsFontResolver` in `PdfExport`'s static constructor; it reads `arial.ttf` / `segoeui.ttf` / etc. directly from `%WINDIR%\Fonts`.

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

**Add a new WPF↔JS message type:**
1. **Both** sides read `tabId` from the payload unless the message is global.
2. JS → WPF: `postHost({ type: '…', tabId, … })`. Add a `case` to the `OnViewerMessage` switch in `MainWindow.xaml.cs`.
3. WPF → JS: `PostOrQueue(new { type = "…", … })`. Add a `case` to the host-message switch in `viewer.html`.
4. Update the two tables in this doc's "WPF ↔ JS bridge" section.
5. If the message carries a Promise-like call/response (e.g. `captureImage` → `imageData`), include a `purpose` field that's round-tripped so multiple callers can share the channel.

**Add a bottom-of-viewport overlay (e.g. a new tool row):**
1. Add the `<div class="overlay">` inside `<main id="viewport">` in `viewer.html`.
2. Position with `bottom: Npx; left: 8px` — stack above `#section-controls` + `#lighting-controls` (currently at 8px and 44px).
3. Add a `body.aps-active #your-overlay { display: none; }` CSS rule so it hides on APS tabs.
4. Wire events in the module script; use the `guard(fn, 'your-handler')` wrapper for resilience.

**Rebuild blocked by "file locked"**: the running exe is holding it. Either close the app, or skip the rebuild and just copy the changed HTML file into `bin/Debug/net8.0-windows/` — WebView2 re-reads the HTML on each navigation, so HTML-only changes don't require a C# rebuild.

## What this app is NOT

- No editing / saving / export of **model data** (image + PDF export of the rendered viewport is supported).
- No markup or annotation tools (distance measurement is supported).
- No support for .rvt, .dwg, .3dm, .step, .skp — convert to IFC / glTF / OBJ first.
- Not optimized for batch operations; one user, one window.
- No cross-platform support (Windows only — WPF + WebView2).

## Current verification status (as of this commit)

**Verified by hand:**
- Offline loading: FBX, glTF/GLB, OBJ, STL (multi-file OBJ+MTL, glTF+bin+textures).
- Multi-tab open/switch/close with per-tab camera + selection state.
- Multi-select in Open dialogs + drag-and-drop; mixed offline + APS batches route correctly.
- Scene tree (hierarchy, search, visibility, Isolate/Hide/Show All, Shift+click range, Ctrl+click toggle).
- Section plane, view presets, Rotate model, Bake FBX + Undo, "Keep view on Isolate" toggle.
- Properties panel for offline selection; double-click to frame; orbit pivot follows selection.
- Fit preserves view direction and fills the viewport using the visible bounding box only.
- Lighting presets (Neutral/Studio/Outdoor/Headlight) + azimuth/elevation sliders.
- Dark Mode toggle (panels + scene backgrounds + grid recolor).
- Measurement tool (two-point distance).
- Save Image (PNG/JPEG) via Ctrl+S.
- Export to PDF (landscape US Letter, 3× hi-res) via Ctrl+P.
- Help window (F1 or Help → User Guide); About item opens on the About tab.
- Tab resizer + collapsible panel.
- JS errors routed to status bar + Serilog.
- **APS path end-to-end**: auth → bucket → upload → translate → manifest → metadata → viewer render. Tested with multiple `.nwd` files. Model selection → `GetObjectPropertiesAsync` populates properties panel (with null-safe handling when APS returns an empty collection).
- APS camera state preserved across tab switches (via `apsViewer.getState` / `restoreState`).
- APS Properties + Scene Tree panels auto-hide on APS tabs; the Autodesk viewer's own browser UI is used instead.
- Bundled WebView2 Fixed-Version runtime startup path — confirmed via log line.

**Implemented but NOT yet verified end-to-end:**
- **IFC (`.ifc`)** — web-ifc WASM parser is bundled and wired (`IFCLoader`, per-type filter, element property lookup), but no `.ifc` file has been opened in the viewer yet.
- **Large models** (50+ MB, 100k+ nodes) — tree virtualization is in place but only tested with small sub-MB FBX files.

**Known non-bugs:**
- APS translations cost cloud credits. A `403 Forbidden: Token exchange denied. Policy 'ProductAccessRequiresCapacity' has effect: deny` response means the APS account is out of capacity — not a code issue.
- Navisworks FBX exports look worse than the same model via APS. That's the exporter, not this viewer.
