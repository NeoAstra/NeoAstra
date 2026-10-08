# Capabilities and security

NeoAstra trusts registered application RPC in a controlled local application view by default. Ordinary app methods and events therefore need no permission ID or capability file. Transport admission, immutable backend-created session identity, navigation policy, CSP, payload limits, and remote-content isolation still apply before application code runs. Remote content is never made trusted by this default.

Explicit capabilities are an opt-in boundary for restricted views, plugin/native services, argument scopes, or applications that want operations reviewed individually. Set a bounded `Permission` on each operation in that boundary and configure an authorization service; a permission-bearing operation remains fail-closed if the service is absent.

A restrictive manifest alone does not hide permissionless application methods registered on the same
RPC host. For a restricted trusted view, register only its intended operations on a separate host or
make every operation in the restricted surface explicitly permissioned. Active untrusted previews
should have no bridge at all. Do not bind a preview to the main application's unrestricted registry.
Capability checks also do not establish application project/session ownership; validate that in the
backend for every command, read, subscription, and approval response.

## Permission catalog and capability file

Applications that use an explicit boundary build a `NeoPermissionCatalog` from `NeoPermissionDeclaration` records. A declaration binds a versioned, colon-separated ID to command/event names, risk, scope family, platform availability, timeout, concurrency, redaction, and documentation. Plugin catalogs are registered explicitly with an ID and compatibility range. Their permissions and permission sets become discoverable, but **grant nothing** until the application capability file names them. Application-owned RPC without a permission declaration is not added to this catalog.

Catalog and capability files are each limited to 1 MiB by the resolver tool. Catalog parsing uses depth and structural-node bounds, rejects duplicate JSON properties recursively, and caps application permissions, plugins, plugin permissions, permission sets, and set entries before expansion. Programmatic catalog builders enforce the same count families so generated or plugin-provided enumerables cannot bypass tool limits.

Capability files use [`neoastra-capabilities-v1.schema.json`](../schemas/neoastra-capabilities-v1.schema.json):

```json
{
  "$schema": "neoastra-capabilities-v1.schema.json",
  "version": 1,
  "capabilities": [{
    "id": "main",
    "views": ["main"],
    "platforms": ["windows", "macos", "linux"],
    "permissions": ["documents:open"]
  }]
}
```

Selectors are exact by default. Reviewed `prefix:*` view patterns are development-only and never accepted in release resolution. Origin selectors are canonical exact `(scheme, IDN host, explicit/default port)` tuples; paths, fragments, wildcards, opaque origins, user information, and renderer claims are rejected. Overlapping capabilities that could union the same permission fail resolution unless the permission explicitly declares a union-safe scope family.

When a manifest is used to restrict only selected views, construct `NeoCapabilityAuthorizationService` with `trustUnconfiguredViews: true`. A view label that has any matching capability record is then governed entirely by that record, while controlled local views absent from the manifest retain the normal trusted-application behavior. The strict constructor remains default-deny for every unlisted view.

Resolve at build/CI time with the source-generated, reflection-free tool:

```sh
dotnet neoastra capabilities resolve \
  --capabilities app.capabilities.json --catalog app.permissions.json \
  --platform windows --configuration Release obj/capabilities.windows.resolved.json
```

Resolution strictly rejects unknown fields, versions, schemas, IDs, permission versions, platform combinations, release development grants, unsafe duplicate/union semantics, and malformed scope data. Output is canonical UTF-8 JSON ordered independently of input order, with no timestamps, plus a SHA-256 hash. It intentionally contains canonical exact scope policy (including configured paths) for security review; do not publish it as a diagnostic or place user-specific secrets in capability files. Generate twice and byte-compare in CI. The resolved file is review/audit evidence; runtime authorization uses the corresponding immutable `NeoCapabilityManifest` object, not reparsed renderer data.

Resolve filesystem/process scopes on the target operating system: .NET path parsing is host-platform-specific. The CI matrix runs scoped tests natively on Windows, macOS, and Linux; a simple path-free fixture is additionally cross-resolved for all targets on every runner.

## Trusted invocation context

Create a session with backend-owned metadata:

```csharp
var identity = new NeoRpcSessionIdentity("main", generatedSessionId)
{
    Platform = NeoCapabilityPlatform.Windows,
    SourceOrigin = trustedTopLevelOrigin,
    WholeViewTrust = true,
};
```

`ViewId`, `SessionId`, platform, authenticated top-level source origin, and Linux whole-view trust come only from native/application lifecycle code. A command argument named `origin`, the browser's current URL, redirects, iframe data, or renderer-provided identity never changes this metadata. Navigations must call the trusted `ReceiveAsync(..., trustedOrigin, topLevelDocument)` path; subframes are denied by default. Sessions are invalid after disposal and capabilities cannot be changed by navigation.

Configure `NeoCapabilityAuthorizationService` and the same manifest on `NeoRpcOptions` when using explicit capabilities. Within that boundary, unknown permissions, unmatched view/platform/origin, absent authenticated origin, malformed arguments, scope mismatch, cancellation, rate exhaustion, and resource exhaustion all deny before dispatch. Unknown commands always deny. Denials use stable codes such as `permission_denied`, `scope_denied`, `too_many_requests`, and `cancelled` without leaking policy detail.

### Platform provenance

- **Windows / WebView2:** application integration may attach authenticated top-level source origin from the native navigation/message source. It must not read JavaScript fields or use the mutable current URL as identity.
- **macOS / WKWebView:** use backend-owned top-level frame/source metadata when the integration can prove it. If unavailable, any capability with `origins` denies. WKWebView process/frame provenance is not a sandbox boundary by itself.
- **Linux / WebKitGTK:** authenticated per-message origin and sender-frame provenance are not considered available. Resolution rejects Linux capabilities containing `origins`. Only an explicitly trusted whole view (`WholeViewTrust = true`) can invoke; that trust covers every script in the view, so use separate views/processes for differently trusted content. This is an honest limitation, not an origin fallback.

## Scope families

Scopes are immutable normalized records. Unknown fields fail validation. Runtime matching parses bounded typed command arguments and never consults ambient process state.

| Family | Required policy | Runtime invariants |
| --- | --- | --- |
| Filesystem | absolute roots with opaque tokens; operations; symlink policy | fully qualified canonical paths, boundary-safe containment, traversal rejection; final symlink/reparse protection must also be enforced at OS handle open |
| URL opener | `http`/`https`, canonical hosts, ports, path prefixes | absolute hierarchical URL; user-info/fragment rejected; exact host (no substring/wildcard) |
| Process | absolute executable, exact argument vector, optional exact working directory and environment-name allowlist | no PATH lookup, shell, argument concatenation, inherited working directory, or undeclared environment variables |
| Clipboard | format and operation allowlists | exact normalized enum values |
| Notifications | app identity, categories, payload bound, persistence, urgency | bounded payload and exact category/urgency |
| Dialogs | kinds, approved initial-location tokens, extensions | no arbitrary renderer-selected initial path |
| Network | URL restrictions plus methods, headers, redirect policy, request/response byte limits | exact method/header set; redirect and size policy remain native-side responsibilities |
| Persistence | identities, grant kinds, maximum duration | explicit identity/kind and bounded duration; no ambient browser persistence grant |

Path canonicalization is platform-sensitive. Windows comparisons are ordinal-ignore-case and reject device/UNC roots; macOS/Linux comparisons are ordinal. Normalization is lexical; secure filesystem implementations must additionally open handles without following disallowed links and verify final handles beneath approved roots to avoid TOCTOU races.

## Profiles and release validation

`ProductionLocalApp` is the default: no development server, no wildcard patterns, no detailed renderer errors, and release-safe capability resolution. The conventional `NeoApp` host also allows only the exact `app://neoastra` origin, including its local routes, and denies unexpected new windows. `DevelopmentLocalApp` requires `Release = false` and an explicit loopback HTTP(S) origin; `NeoApp` accepts only the exact configured `127.0.0.1` or `::1` origin and port. Development mode must never be selected by renderer input. Release validation rejects development-only grants, reviewed patterns, development origins, and detailed error output.

Profiles expose resolved navigation, popup, DevTools, asset, bridge, and error posture. `NeoApp` applies the conventional local-app navigation and popup policy, but applications using the low-level API must install equivalent handlers themselves. Applications must also apply CSP when serving assets. Trusted RPC assumes that this controlled-content boundary is intact; capability policy is not a browser sandbox and cannot protect a privileged command from script already executing in the same trusted document.

## Links that open outside the web view

External navigation in `NeoApp` is blocked unless the application calls `OpenExternalLinksInSystemBrowser` with exact HTTP(S) origins, or says that its content links anywhere with `OpenAnyWebLinkInSystemBrowser()`, which is `OpenExternalLinksInSystemBrowser(NeoUrlScope.AnyWebAddress)`. That scope accepts an absolute HTTP or HTTPS address with a host, without credentials, without a control or white-space character, and of at most 4096 characters; it is the validation an application would otherwise write itself, and it says nothing about where an address leads. The native navigation and new-window handlers compare parsed scheme, host, and port components against the origins, or check the address against that scope, require a user action, cancel the WebView navigation, and then open matching links in the system browser. Renderer JavaScript is not the policy boundary. Credentials, non-HTTP(S) schemes, requests that the engine does not attribute to the user, and subframe navigation are not forwarded to an OS opener.

A view asks its host about a link in one of two requests: `NavigationRequested` when the link would replace a document of the view, and `NewWindowRequested` when it asks for a new window, as a link with `target="_blank"` or `window.open` does. Either can be answered with `OpenExternal`. Both carry `IsUserInitiated`, which is what "a user action" means above, and `IsLinkActivation`. The browser engines do not tell the same, so the two values are separate:

| What happened | Windows / WebView2 | macOS / WKWebView | Linux / WebKitGTK 6.0 |
| --- | --- | --- | --- |
| The user clicks a link, or presses Enter on it | User-initiated | User-initiated; link activation | User-initiated; link activation |
| A script navigates or opens a window while it handles a click or a key press (`location.href`, `window.open`, `click()` on a link) | User-initiated | Not user-initiated; link activation for `click()` on a link | User-initiated; link activation for `click()` on a link |
| A script does the same on its own, from a timer or while the page loads | Not user-initiated | Not user-initiated; link activation for `click()` on a link | Not user-initiated; link activation for `click()` on a link |
| The server redirects a navigation that was user-initiated | Not user-initiated | Reported like the navigation it redirects | Not user-initiated |
| `IsLinkActivation` | `null`: not reported | Reported | Reported |

The macOS column was observed, on macOS 26. The Windows and Linux columns are what WebView2 and WebKitGTK document for the values NeoAstra passes on; they have not been run.

- **Windows.** `IsUserInitiated` is the `IsUserInitiated` of WebView2 for a request that is not a redirect: the user gesture of the engine. WebView2 does not tell that a request comes from a link.
- **Linux.** `IsUserInitiated` is the user gesture that WebKitGTK reports (`webkit_navigation_action_is_user_gesture`) for a request that is not a redirect, and `IsLinkActivation` is its navigation type.
- **macOS.** The public interface of WKWebView has no user-gesture flag. `IsUserInitiated` is `true` only when a link was activated with a trusted click or key press, which WKWebView tells by the mouse button of the request: a link that a script clicks comes without one. It is therefore the strictest of the three, and a page that opens its links with `window.open` or `location.href` from a click handler does not pass for user-initiated on macOS; use a link (`<a href>`) for an address that should open outside the view. WKWebView does not tell the redirect of a clicked link from the link either.

An application that decides for itself and wants every activated link, whoever activated it, reads `IsLinkActivation` where it is not `null`. That is a weaker test: a script can click a link.

`OpenExternal` leaves the view where it is and hands the requested address to the system, which opens it in the default browser. The native library checks the address a second time, behind the policy of the host: it hands over only an absolute `http` or `https` address with a host, without credentials, and without a control or white-space character. Anything else, such as `mailto:`, `file:`, or the scheme of another application, is not opened whatever the handler decided, because the system could start a program for it instead of showing a page. Use `NeoExternalOpener` for the other intents an application has.

The view then raises `ExternalOpenCompleted`, on the UI thread, with the address and what became of it:

| `NeoExternalOpenStatus` | Meaning |
| --- | --- |
| `Opened` | The system took the address. It does not say that a page was shown. |
| `Refused` | The address is not a web address, and the system was not asked. |
| `Failed` | The system did not open the address, for example because no application handles web addresses. `NativeCode` is the error of `ShellExecute` on Windows and of GLib on Linux, and zero on macOS, where the system gives none. |

```csharp
view.ExternalOpenCompleted += (_, outcome) =>
{
    if (outcome.Status != NeoExternalOpenStatus.Opened) ShowLinkCouldNotBeOpened(outcome.Uri);
};
```

A `NeoApp` application registers the same callback with `NeoAppBuilder.OnExternalLinkOpenCompleted`.

The check and the event are the work of the native library. `NeoEnvironment.RuntimeInfo.ChecksExternalOpen` is `false` for a library from before them, which hands any address to the system and reports nothing: a handler that may run with one validates the address itself, with `NeoUrlScope.AnyWebAddress.TryAuthorize` for example, before it answers `OpenExternal`.

## Abuse controls and diagnostics

`NeoRpcOptions` bounds payload, parse depth, request-ID retention, global/session/command concurrency, token-bucket request rate/burst, abuse closure threshold, resources, resource bytes, channels, channel buffers, and default/permission timeouts. Counters are synchronized; reservations are released in `finally`; cancellation/disposal races yield one terminal response and reclaim resources. Policy exhaustion returns stable retryable errors and repeated abuse closes the session.

Session and host disposal are idempotent shared operations: every concurrent caller awaits the same teardown task. Abuse-triggered closure also awaits that operation, so resource and scoped-service cleanup cannot be observed as complete early by another disposer.

`INeoCapabilityDiagnosticSink` receives structured allow/deny events containing timestamp, view label, a bounded document-session suffix, operation, permission, stable decision code, origin-presence/trust flags, platform, and correlation ID. It does not receive renderer arguments, scope values, URLs, paths, or response bodies. Labels, permission/operation names, correlation IDs, and the session suffix are still operational identifiers—not anonymized data—so applications must avoid secrets in identifiers and apply retention/access controls. `GetDiagnosticSnapshot()` exposes bounded counts, manifest hash/profile, and grant count summaries; capability IDs are included but exact scope values are not.

See [the threat model](security-threat-model.md) for trust boundaries and review its deployment checklist before enabling renderer authority.
