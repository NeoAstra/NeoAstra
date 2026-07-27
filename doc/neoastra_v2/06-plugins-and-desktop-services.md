# Step 6 — Plugin Model and Desktop Essentials

**Parent:** [`neoastra_v2_specs.md`](../neoastra_v2_specs.md)
**Depends on:** [Step 3](03-capabilities-and-security.md), [Step 5](05-application-lifecycle-and-hosting.md)
**Outcome:** Applications gain common native desktop services through statically composed, capability-gated, cross-platform contracts.

## 1. Scope

This step defines the plugin contract and the first official desktop-service packages. Services are first-class C# APIs. Optional generated frontend commands use the common RPC transport and remain unavailable until explicitly granted.

The initial desktop essentials are dialogs, menus/context menus, tray/status items, clipboard, notifications, global shortcuts, theme/display/app metadata, scoped external opener, drag/drop brokering, safe storage, and window-state persistence. Platform-specific polish is exposed through capabilities, not false emulation.

## 2. Plugin contract

A plugin MUST provide, as applicable:

- stable reverse-DNS or package-aligned plugin ID;
- semantic managed API version and frontend protocol version;
- explicit static C# registration extension;
- lifecycle hooks for configure, app ready, stopping, and disposal;
- platform adapter selected without runtime reflection;
- capability probe and support details;
- declared RPC commands, permissions, permission sets, scope JSON Schemas, risk levels, and audit policy;
- generated/handwritten TypeScript module depending only on `@neoastra/client`;
- per-app/view/document/resource ownership and cleanup;
- source-generated JSON metadata and trimming/AOT declarations;
- RID-native assets and third-party notices when required;
- fake adapter, frontend mock, contract tests, native conformance tests, support matrix, and security notes.

Plugin registration MUST NOT grant renderer access. Backend-only use MUST avoid registering frontend command handlers where possible. Dynamic native plugin loading is not required and SHOULD NOT be the standard extension model.

## 3. Runtime registration and lifecycle

A conceptual registration model:

```csharp
builder.AddNeoAstraPlugin<DialogsPlugin>();
builder.AddNeoAstraClipboard(options => ...);
```

The runtime resolves a dependency graph before `Starting` completes. Duplicate IDs, incompatible versions, cycles, missing adapters, permission conflicts, or serializer gaps fail startup with actionable diagnostics.

Lifecycle order:

1. validate metadata/permissions/configuration;
2. create application-scoped plugin state;
3. attach platform adapter on UI thread if required;
4. signal app ready after core readiness;
5. create/dispose view/session state with its owner;
6. revoke renderer access and cancel operations at app stopping;
7. dispose child resources before plugin/application adapter.

Plugin callbacks MUST not run under core locks. UI-thread requirements are explicit. Shutdown is bounded and exceptions are contained/logged.

## 4. Common contract rules

- Async methods accept `CancellationToken` when an operation can wait.
- Parent windows are explicit for modal native UX.
- User-cancel is a normal typed result, not an exception, where appropriate.
- Data returned from OS APIs is copied/owned safely and size-limited.
- Each feature reports support and relevant limitations.
- Renderer calls use intent-specific DTOs and permissions; arbitrary OS command strings are forbidden.
- Native object IDs are opaque and owned by app/view/session as declared.
- Event subscriptions are bounded and removed on owner disposal.
- Accessibility, localization, theme, DPI, and app identity are delegated to native OS behavior where possible.

## 5. Dialogs (`NeoAstra.Desktop.Dialogs`)

Required C# operations:

- open one/many files;
- save file;
- select one/many folders where supported;
- message/confirmation dialog with portable button/icon roles and optional platform details.

Options include owner window, title, initial location token/path under backend policy, suggested filename, validated extension/MIME filters, multi-select, and cancellation. Result paths are absolute canonical strings/typed paths with clear ownership; no file is opened implicitly.

Renderer permissions are distinct (`dialogs:open-file`, `dialogs:save-file`, `dialogs:open-folder`, `dialogs:message`) and SHOULD scope filters/initial roots. Browser HTML file chooser remains a separate WebView decision API.

Tests cover owner destruction, cancellation, invalid filters, unavailable dialog kind, symlink/path policy, and headless CI skip.

## 6. Menus and command routing (`NeoAstra.Desktop.Menus`)

Define immutable/diffable descriptors:

```csharp
NeoMenuItem.Command(id, text, commandId, accelerator, enabled, isChecked);
NeoMenuItem.Submenu(id, text, children);
NeoMenuItem.Separator(id);
NeoMenuItem.RoleItem(id, NeoMenuRole.Copy);
NeoMenuItem.RoleItem(id, NeoMenuRole.Copy, localizedText);
```

Requirements:

- stable item IDs distinct from display text;
- application menu, window menu, and context menu;
- native roles for standard edit/window/app actions where supported;
- enabled/visible/checked state updates without rebuilding unnecessarily;
- validated platform accelerator syntax and conflict diagnostics;
- command activation routed through a shared backend command service, not arbitrary JavaScript;
- optional targeted frontend event only after capability matching;
- localized standard role labels through reliable OS/framework resources, or explicit application-localized Unicode labels where no such complete resource exists;
- menu updates marshaled to UI thread and safe during activation.

GTK3 framework resources provide implicit labels for Copy, Cut, Paste, Select All, Undo, Redo, Close, and Quit. GTK3 Minimize, all Win32 roles, and all AppKit roles require the explicit `localizedText` overload because those presenters do not expose a reliable complete localized label source; the implicit overload is rejected rather than silently substituting English. AppKit selectors, Win32 commands, and GTK/WebKit commands still provide native behavior and never evaluate application JavaScript. Linux edit-role targets are selected by actual window ownership, then by ordinal view label (and native handle only for unlabeled ties); missing or stale native targets disable or safely ignore the role.

macOS application-menu conventions, Windows accelerator behavior, and Linux desktop/global-menu differences are documented. Unsupported roles degrade only to explicit app commands, not silently wrong behavior.

## 7. Tray/status items (`NeoAstra.Desktop.Tray`)

Support create/update/dispose, icon/template-image policy, tooltip, native menu, primary/secondary activation, optional bounds, and attention state where available. Items are app-owned and survive window closure according to shutdown policy. Duplicate activation and menu events are ordered.

Renderer creation/control requires separate permissions; normal templates SHOULD create tray items from trusted C# startup. App quit from tray uses normal quit negotiation.

## 8. Clipboard (`NeoAstra.Desktop.Clipboard`)

Initial formats: plain text, HTML with documented sanitization responsibility, common image representation, and file-list where supported. Read and write permissions are separate by format. APIs MUST define thread affinity, delayed rendering/ownership, maximum bytes, image encoding, line-ending/HTML metadata behavior, and clear operation.

Sensitive clipboard reads are high-risk and default-denied. Returned content is never logged. Unsupported formats return capability-aware results. Clipboard-change observation is deferred unless reliable across all target systems.

## 9. Notifications (`NeoAstra.Desktop.Notifications`)

Support permission/status query, display request, stable notification ID/tag, title/body, app icon, bounded action buttons, activation/dismiss events, and remove/clear where available. Activation payload is an opaque application-defined bounded ID/data DTO, never executable code.

Platform app identity/packaging requirements are surfaced by `doctor` and Step 7. Events arriving before app ready enter the Step 5 launch queue. Persistence/background delivery differences are capability details. Renderer notification authority is separate from browser web-notification permission.

## 10. Global shortcuts (`NeoAstra.Desktop.GlobalShortcuts`)

Support register/unregister/query and activation event with normalized accelerator. Registration is app-owned, conflict-aware, and released on stopping/crash as OS permits. Renderer permission scopes allowed accelerator values and cannot register arbitrary keys by default. Reserved/system shortcuts are rejected. Wayland/desktop-environment limitations are reported explicitly.

## 11. Theme, display, and application metadata (`NeoAstra.Desktop.SystemInfo`)

Expose immutable snapshots and change events for:

- light/dark/high-contrast theme and optional accent color;
- reduced motion/transparency where reliable;
- displays with stable-in-session ID, bounds, work area, scale factor, primary flag, orientation/refresh where reliable;
- app identifier/name/version, OS/architecture/backend;
- locale/preferred languages and locale-change where available;
- standard app directories through typed path categories.

Display coordinates use one documented logical coordinate system with conversion helpers. Events are coalesced and UI-dispatched. Standard paths are C# APIs; renderer access requires scoped read/use permissions and MUST not reveal unrelated user paths casually.

## 12. External opener (`NeoAstra.Desktop.Opener`)

Intent-specific operations:

- open an allowed URL in the default handler;
- open an allowed existing file;
- reveal an allowed file/folder in the file manager.

No arbitrary shell command, executable, verb, or unvalidated scheme is accepted. URL scope validates scheme/host/port and rejects credentials/control characters. File scope uses canonical paths and distinguishes open from reveal. Confirmation policy MAY be application-defined. Results distinguish denied, no handler, not found, and OS failure.

## 13. Drag and drop (`NeoAstra.Desktop.DragDrop`)

Inbound drop events provide typed data kinds and brokered file tokens/canonical paths according to capability policy, not untrusted DOM strings treated as authority. Events include target view/window and logical position. Limits apply to item count and metadata size.

Outbound drag requires an explicit user gesture, declared data/files, optional drag image, and completion result. It MUST not expose unrestricted filesystem access or retain resources after drag completion. Backend/WebView DOM drag interactions need conformance tests.

## 14. Safe storage (`NeoAstra.Desktop.SafeStorage`)

Provide OS-backed encryption/credential storage for small secrets:

- `StoreAsync(key, bytes)`, `RetrieveAsync`, `DeleteAsync`, `ContainsAsync` or a safer typed equivalent;
- explicit application namespace and optional account/service partition;
- no secret enumeration to renderer by default;
- maximum size and user-interaction/locked-keychain behavior;
- memory zeroing where practical and no logging/string conversion;
- clear error distinctions for unavailable, locked/denied, not found, and corrupt.

Use DPAPI/Credential Manager as selected on Windows, Keychain on macOS, and a reviewed Secret Service/libsecret path on Linux. No insecure plaintext fallback. NativeAOT and package dependencies are documented.

## 15. Window state and polish

### 15.1 Persistence

Store/restore normal bounds, state, display affinity hint, and optional visibility using an application-chosen store. Restore MUST clamp to current work areas, account for DPI/topology changes, avoid restoring minimized by default, and preserve user recoverability. Writes are debounced and atomic.

### 15.2 Window additions

Core/desktop adapters add capability-gated:

- icon;
- monitor selection/centering and correct startup location wiring;
- effective state-change events;
- modal/enabled semantics without application-started nested loops;
- attention/flash;
- taskbar/dock progress and badge;
- theme/title-bar controls with draggable-region safety;
- content protection where the OS provides meaningful support.

Unsupported features report `None`; partial semantics report `Limited` with details.

## 16. Frontend modules and mocks

Each renderer-facing plugin exports a focused package or `@neoastra/client/<plugin>` module with typed calls, errors, events, and testing mock. Modules MUST not expose commands beyond the plugin contract or infer grants. Frontend mocks model cancellation and events but do not claim to reproduce native UX.

## 17. Implementation waves

Wave A (needed by most reference apps):

- [x] freeze plugin registration/lifecycle/metadata/permission contract;
- [x] dialogs;
- [x] menus/context menus and shared command routing;
- [x] tray/status items;
- [x] clipboard text;
- [x] theme/display/app metadata;
- [x] scoped opener.

Wave B:

- [x] notifications and early activation routing;
- [x] global shortcuts;
- [x] clipboard HTML/image/files;
- [x] drag/drop broker;
- [x] safe storage;
- [x] window-state persistence and polish.

> Step 6 implementation note (2026-07-27): explicit source-generated renderer handlers and DTO JSON,
> static metadata/schemas, grant-free registration, TypeScript modules/mocks, owner/session cleanup,
> Windows/macOS/Linux dialogs, multi-format clipboards, shortcuts, system snapshots, opener, Keychain/
> DPAPI/Secret Service storage, package assets, NativeAOT fixtures, and cross-platform CI coverage are present.
> Native menu/context-menu and tray/status presenters now cover Win32, AppKit, and GTK with immutable
> replacement, command/role activation, ownership, and teardown. Notifications use transactional Win32
> generations, modern `UNUserNotificationCenter`, and identity-bound Freedesktop D-Bus lifecycle handling.
> Native inbound file/text/URL drops are captured at the WebView boundary on Win32/AppKit/GTK, then canonicalized
> and routed only to the target view's active registered renderer document session. Tokens are revoked on
> navigation, session rotation, renderer/view/window teardown, and cannot be resolved by another session. Trusted
> C# outbound calls retain explicit one-shot tokens; renderer calls expose no authority token and instead consume
> a current native gesture bound to the source view and document session. Shell OLE, AppKit, and GTK presenters
> reject absent, expired, mismatched, reused, and navigation-invalidated gestures. Custom drag-image
> presentation remains Limited. macOS/Linux runtime conformance executes only in the target CI
> desktop-session jobs and is not claimed from a Windows development host.

Every plugin proceeds contract -> fake adapter/tests -> Windows -> macOS -> Linux -> conformance/support docs -> renderer API/security tests. One platform implementation MUST NOT establish portable release support by itself.

## 18. Verification

Each plugin requires unit tests for validation/lifecycle/permissions/scopes, fake-adapter contract tests, NativeAOT, frontend mock tests, and platform integration tests. Cross-plugin tests cover shutdown while dialog/menu/drag is active, view disposal, command routing, notification activation before ready, capability revocation, duplicate IDs, and diagnostic snapshot/version listing.

Accessibility tests cover keyboard menu/accelerators, native dialog accessibility assumptions, high contrast/theme changes, title-bar keyboard behavior, and tray/menu labels. Localization tests verify standard labels/resources and Unicode round trips.

## 19. Exit criteria

The reference application uses dialog, menu, tray, clipboard, notification, shortcut, theme/display, and opener APIs without platform-specific branches in ordinary C# or frontend code. Every renderer operation is separately capability-gated and scoped. Unsupported platform behavior is truthful, all resources clean up during navigation/shutdown, and all official plugins pass AOT, conformance, security, accessibility, and support-matrix requirements.
