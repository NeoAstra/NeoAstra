# NeoAstra v2 security threat model

## Assets and boundaries

Protected assets include native filesystem/process/network/clipboard/dialog/notification access, RPC data, backend credentials, persistent grants, and host availability. The renderer, loaded web content, subframes, JavaScript dependencies, message payloads, current URL, redirects, and plugin packages are untrusted. Native platform adapters and application code that create views/sessions and resolve reviewed capability files are trusted. OS and web-engine sandboxing are independent defense layers, not replacements for RPC authorization.

## Threats and controls

| Threat | Required control |
| --- | --- |
| Compromised renderer invokes native functionality | explicit versioned permission on every command/event; default-deny authorization before dispatch |
| Renderer spoofs view, session, origin, platform, or plugin | immutable backend-created session identity; never derive identity from payload/current URL; plugin registration grants nothing |
| Navigation or iframe retains privilege | authenticate top-level source per message where supported; reject subframes/unknown origin; Linux uses explicit whole-view trust only |
| Selector confusion / manifest broadening | exact canonical selectors, no release wildcards, overlap detection, strict unknown-field/version rejection, deterministic reviewed manifest/hash |
| Path traversal, symlink, reparse, or TOCTOU | absolute canonical root containment plus secure no-follow OS handle opening and final-handle verification by the resource implementation |
| URL parser confusion / redirect escape | hierarchical HTTP(S), canonical host/port/path matching, no user-info/fragment; reauthorize each redirect in the native network implementation |
| Command/process injection | exact executable and argument vector, no shell/PATH lookup, bounded allowlisted environment and working directory |
| Scope bypass with malformed JSON | source-generated bounded JSON parsing; family-specific exact-field validation; deny on ambiguity |
| Flooding, slow handlers, leaked resources/channels | payload/depth/rate/burst/concurrency/time/resource/channel limits, linked cancellation, synchronized reservations, abuse closure |
| Policy/secret leakage through errors or telemetry | stable generic renderer errors; no renderer arguments/scope values/URLs/paths/bodies; bounded snapshots and explicitly documented operational identifiers |
| Build/runtime policy drift | build-time resolver, checked schema/catalog/capability fixtures, byte-determinism checks, package-content checks, cross-platform tests, NativeAOT publish/run |

## Platform assumptions and residual risk

WebView2 can provide useful native source/frame metadata, but application integration must pass it explicitly and reject events it cannot authenticate. WKWebView provenance varies by API/OS and should be treated as unavailable unless the adapter can prove top-level source; an origin-constrained grant then denies. WebKitGTK is modeled as unable to authenticate per-message origin: Linux origin selectors are rejected and only a wholly trusted view is eligible. Applications mixing trusted and remote content must isolate them into different views or processes.

Capability checks constrain RPC. They do not sanitize HTML, guarantee browser sandbox correctness, prevent malicious trusted application code, or implement OS-level filesystem/network/process containment. High-risk resource implementations must preserve the checked typed values, avoid stringly-typed reinterpretation, apply OS access control and sandboxing, enforce final-handle/redirect/byte constraints, and close resources on cancellation/disposal.

## Security review checklist

- Review catalog changes, permission risk/scope/version, plugin compatibility, and exact capability diff.
- Confirm release resolution for all Windows/macOS/Linux targets and compare canonical bytes/hash.
- Confirm every generated operation has a bounded permission ID and runtime registration matches the catalog.
- Exercise wrong view/platform/origin, unknown origin, subframe, spoofed argument, out-of-scope values, rate/concurrency/resource races, cancellation, and disposal.
- Inspect renderer errors, diagnostics, crash reports, snapshots, and build logs for sensitive values.
- Reassess platform provenance whenever web-engine/native adapters change.
