# Bundled WebView2 Runtime (Fixed Version)

**This folder is empty by default and ignored by git (except this README).**
When populated with a Fixed-Version WebView2 Runtime, the app will use it instead of the system-installed Evergreen runtime. This makes the built app work on machines that don't have WebView2 installed (e.g., Windows Sandbox, locked-down kiosks, air-gapped workstations).

## What to put here

The extracted contents of the Microsoft WebView2 **Fixed Version Runtime** CAB. The folder layout the app looks for is:

```
webview2-runtime/
└── Microsoft.WebView2.FixedVersionRuntime.<version>.x64/
    ├── msedgewebview2.exe
    ├── msedge.dll
    ├── ...
```

The app also works if you drop `msedgewebview2.exe` and its siblings directly into `webview2-runtime/` without the versioned subfolder — the app searches both layouts.

## How to get the runtime

1. Visit https://developer.microsoft.com/en-us/microsoft-edge/webview2/
2. Scroll to **"Get the WebView2 Runtime"** → **"Download the Fixed Version (CBS)"**.
3. Pick your architecture (usually **x64**) and the latest version → accept Microsoft's terms → download.
   The file is named something like `Microsoft.WebView2.FixedVersionRuntime.<ver>.x64.cab` (≈600 MB extracted (≈180 MB compressed .cab)).

## How to extract the CAB into this folder

**PowerShell (recommended):**

```powershell
cd "<path-to-solution>\NwdViewer.Desktop\webview2-runtime"
expand.exe -F:* "<path-to-downloaded>\Microsoft.WebView2.FixedVersionRuntime.<ver>.x64.cab" .
```

**7-Zip** (right-click the CAB → 7-Zip → Extract to "Microsoft.WebView2.FixedVersionRuntime.<ver>.x64\") works too.

**Windows Explorer** can also open CAB files directly — drag the contents into this folder.

After extraction, rebuild the app. The runtime (~180 MB) will be copied to `bin\Debug\net8.0-windows\webview2-runtime\`, and `MainWindow.OnLoaded` will pick it up automatically.

## How to check it's working

Launch the app. Check `%LOCALAPPDATA%\NwdViewer\logs\` — a line reading `Using bundled WebView2 runtime at ...` means the bundle is being used. Without the bundle, the log says `Using installed WebView2 runtime`.

## When to include the bundle vs. skip it

| Deployment target | Include bundle? |
|---|---|
| Dev machine (has Edge / WebView2 installed) | No — save disk space |
| Corporate Windows laptop | Usually no — IT pushes WebView2 via Intune |
| Windows Sandbox / clean VM / air-gapped | **Yes** — no runtime is present by default |
| Redistributable installer for external users | **Yes** — don't rely on end-user having it |

The bundle ships ≈600 MB of extracted binaries; only enable it when needed.
To trim, you can safely remove `Locales\*.pak` (except `en-US.pak`), `MEIPreload\`, `swiftshader\`, and `WidevineCdm\` from the extracted folder — that gets it to ≈450 MB with no user-visible loss for this app.
