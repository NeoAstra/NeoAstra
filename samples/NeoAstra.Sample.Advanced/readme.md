# NeoAstra Advanced Sample

`NeoAstra.Sample.Advanced` is the maintained advanced sample for the application-platform work in
[`doc/neoastra_specs.md`](../../doc/neoastra_specs.md). It uses a normal React/Vite frontend and
public NeoAstra APIs; it does not contain raw WebView bridge code.

## Run the prebuilt frontend

From the repository root:

```powershell
dotnet run --project samples/NeoAstra.Sample.Advanced/NeoAstra.Sample.Advanced.csproj
```

In Visual Studio, set `NeoAstra.Sample.Advanced` as the startup project and run it without command-line
arguments. A second launch does not create another app instance: it securely routes the launch to the
existing window.

## Frontend development with HMR

Install the repository frontend dependencies as documented in the root readme, then run Vite:

```powershell
cd samples/NeoAstra.Sample.Advanced/ClientApp
npm install
npm run dev
```

In another terminal, point the managed host at Vite:

```powershell
$env:NEOASTRA_DEV_URL = "http://127.0.0.1:5173"
dotnet run --project samples/NeoAstra.Sample.Advanced/NeoAstra.Sample.Advanced.csproj
```

For Visual Studio, add `NEOASTRA_DEV_URL=http://127.0.0.1:5173` to the project's debug environment.
Without that variable, the application intentionally serves the deterministic prebuilt `ClientApp/dist`
files through `app://neoastra`.

## What to try

- **Portable transport:** inspect negotiated protocol, backend, platform, view label, and document session.
- **Generated RPC:** invoke a typed C# method, cancel an invocation, consume an ordered channel, and watch
  events emitted by a lightweight application-owned background pulse.
- **Capabilities:** open, close, and reopen the restricted preview window. It can call the read-only tour RPC
  but a desktop call is denied before dispatch; user-close hides the reusable preview until application exit.
- **Lifecycle:** mark work as unsaved and close the main window; the asynchronous renderer confirmation can
  cancel close. Relaunch the executable to exercise authenticated single-instance routing.
- **Desktop essentials:** native dialogs, window and context menus, tray/status items, clipboard, notifications,
  global shortcuts, system metadata, scoped URL opening, safe storage, drag-and-drop, and window polish. Drop a
  file from your user profile into the main window to observe its document-session-scoped token in the activity log.
- **Frontend/release path:** React, dynamic imports, a module worker, manifest-backed local assets, restrictive
  CSP, generated contracts, NativeAOT, and the bundle workflow.

Native results are intentionally displayed as returned. For example, notifications, global shortcuts, tray
items, content protection, and safe storage can report `Unsupported`, `Denied`, or another platform-specific
status when the operating system, desktop session, application identity, or packaging does not provide the
feature.

## Validation and publish

```powershell
dotnet run --project samples/NeoAstra.Sample.Advanced -- --validate-advanced
dotnet publish samples/NeoAstra.Sample.Advanced/NeoAstra.Sample.Advanced.csproj `
  -c Release -r win-x64 --self-contained
```

The noninteractive validation checks the complete Vite module graph, accessibility markers, secure asset
manifest, two-view capability model, and generated RPC contract without opening a WebView.

## Code map

- `Program.cs` — standalone NeoAstra process entry point and deterministic cleanup.
- `AdvancedApplication.cs` — windows, views, lifecycle, native menu, plugins, and bindings.
- `AdvancedCapabilities.cs` and `capabilities/main.json` — explicit permission catalog and per-view grants.
- `AdvancedRpc.cs` — generated RPC service, channel, event, cancellation, and state.
- `AdvancedValidation.cs` — noninteractive release validation.
- `ClientApp/src/App.tsx` — tour shell and restricted preview.
- `ClientApp/src/*Tour.tsx` — focused, readable feature demonstrations.
