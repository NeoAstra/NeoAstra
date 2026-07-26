# Step 8 — Browser Surface Completion, Diagnostics, and Application Testing

**Parent:** [`neoastra_v2_specs.md`](../neoastra_v2_specs.md)
**Depends on:** Stable core and the applicable preceding steps
**Outcome:** Modeled browser features are consumable and truthful, while applications receive mocks, automation, correlated diagnostics, and release security tests.

## 1. Scope

The v1 capability enum and native events model features that managed applications cannot yet consume. This step completes those browser-facing operations/events and builds application-oriented testing/diagnostics above the existing native/managed conformance infrastructure.

A capability identifier MUST NOT imply a usable feature when no public operation exists. Until implemented, report `None`/documented unavailable rather than a misleading nonzero support level.

## 2. Common browser API rules

- All operations validate view/profile state and thread requirements before native work.
- Async methods support cancellation where the backend operation can be canceled or safely ignore completion.
- Capability checks are available but methods still fail predictably if runtime support changes.
- Returned images/PDFs/bodies use streams/resources for large data, not unbounded byte arrays/base64.
- Events are ordered relative to existing navigation/document sessions where specified.
- Backend details are represented through support level/version/details, not platform exceptions hidden as success.
- Public APIs have XML docs describing unsupported behavior, resource ownership, thread context, limits, and exceptions.
- Renderer wrappers, if supplied, are explicit capability-gated plugin/application commands; browser API availability does not grant them automatically.

## 3. Navigation and browser events

Expose managed events for native events already modeled:

- navigation requested, started, redirected, committed, completed, failed;
- loading progress;
- favicon changed;
- console message;
- history/source/title changes with consistent document/navigation IDs;
- renderer process failure and recovery advice (preserving v1 behavior).

Required event data:

- monotonic navigation ID where backend mapping permits;
- URI and redirect source/target as applicable;
- main-frame indicator and phase;
- HTTP/status/error domain/native code where reliable;
- progress normalized to `[0,1]` or unknown;
- favicon URI/data availability separately;
- console level, message, source URL, line/column, and document session where reliable.

Duplicate/backend-noisy events MUST be normalized only according to documented rules. Unknown values remain unknown. Console messages are bounded/redacted by logging policy and cannot inject terminal control sequences into plain logs without sanitization.

## 4. Typed script evaluation

Add overloads using caller/generator-provided `JsonTypeInfo<T>` or source-generated context:

```csharp
ValueTask<T?> EvaluateScriptAsync<T>(
    string script,
    JsonTypeInfo<T> jsonTypeInfo,
    CancellationToken cancellationToken = default);
```

Rules:

- no reflection-based generic fallback under NativeAOT;
- distinguish JavaScript `undefined`, `null`, thrown exception, unsupported result, malformed JSON, timeout/cancel, and renderer loss;
- enforce script and result size/depth limits;
- include safe script exception details in development only;
- cancellation does not claim to interrupt script execution where the backend cannot;
- document world/frame semantics and preserve explicit isolated-world options.

## 5. DevTools

Provide capability-gated backend C# operations to query open state when reliable, open, close, and optionally inspect a supported endpoint. Defaults:

- development profile may enable/open by explicit app action;
- production profile disables renderer-triggered DevTools and SHOULD deny backend open unless application explicitly opts in;
- no remote debugging listener is exposed by default;
- runtime arguments enabling remote debugging trigger security diagnostics.

Support differences (for example WebKit inspector entitlements/developer extras or WebKitGTK settings) are documented and tested without private APIs.

## 6. Find in page

Define a session object or operation supporting query, forward/back direction, case sensitivity where portable, wrap, match count/current index events, and stop/clear. Only one active find session per view is recommended unless a backend supports more. New navigation invalidates it. Results remain truthful when a backend lacks count/index detail.

## 7. Printing and PDF

### 7.1 Print dialog

`ShowPrintDialogAsync` is UI-thread/window aware, requires user/app initiation according to platform, and returns completed/canceled/unavailable. Owner and modal behavior integrate with Step 5/6.

### 7.2 Print to PDF

Options include page size/margins, orientation, backgrounds, scale, page ranges, headers/footers only where portable, and output target (stream/resource or validated file path). Prefer stream/temp artifact API over renderer-supplied path. Validate numeric/range limits.

Cancellation, renderer navigation/loss, partial files, and cleanup are specified. Unsupported options either fail validation as unsupported or are explicitly ignored only when documented in result details—never silently alter security-sensitive output location.

## 8. Capture

Support viewport capture first, then full-page where reliable. Options include image format (PNG initially; JPEG optional with bounded quality), transparent background behavior, scale, and optional region in logical coordinates. Return an owned stream/resource with dimensions/content type/length.

Full-page capture MUST define maximum dimensions/bytes, scroll/layout side effects, fixed-element behavior as backend-specific, cancellation, and restoration. It must not allocate attacker-controlled unbounded buffers.

Desktop/screen capture is not this API and remains Step 9.

## 9. View bounds and composition

Expose post-creation bounds/fill-parent updates and layout events for borrowed parents and owned windows. Requirements:

- logical coordinate system and scale conversion are explicit;
- changes marshal to UI thread and coalesce during resize;
- owned window MAY host multiple independently bounded views only after z-order, focus, hit testing, ownership, and popup policy are specified/tested;
- fill-parent and explicit bounds are mutually clear;
- zero/negative/overflow bounds validate predictably;
- navigation/lifetime is independent from layout.

Transparent background/composition remains capability-gated. Do not expose a capability as native if only window background—not WebView composition—is supported.

## 10. Request navigation and session/profile operations

Complete managed request navigation using method, headers, and body where native support exists. Validate absolute URI, forbidden headers, body limits/ownership, redirect semantics, and backend limitations. The API MUST not imply arbitrary network interception.

Profile additions SHOULD include:

- browser permission query/reset/persistence where portable;
- proxy configuration/query only after clear per-profile/runtime semantics;
- download/session controls exposed by existing native capability;
- storage/cache quota/status when reliable.

Network observation/interception remains Step 9 unless a narrow portable contract is proven.

## 11. Diagnostic snapshot

Provide a safe serializable snapshot containing:

- managed/native/ABI/RPC/client versions and application contract hash;
- OS, architecture/RID, backend and browser runtime version;
- capability table with support/version/details;
- profile mode (not profile data/path secrets);
- window/view labels/states and document-session status;
- security profile, whole-view-trust/origin-authentication status, permission IDs/scope summaries;
- frontend asset manifest hash and CSP summary;
- plugin IDs/versions/support;
- configured/effective limits and aggregate usage;
- bundle/update channel/version/signature identity summary where available.

Snapshots MUST exclude cookies, browsing data, command arguments/results, secrets, clipboard, full user paths, update tokens, signing secrets, and raw environment. Applications can add reviewed sections through hooks.

## 12. Correlated observability

Use stable correlation/operation/navigation/download/update IDs across:

- native logs and managed exceptions;
- RPC invocation/authorization/result;
- browser console and navigation events;
- process failures and recovery;
- plugin operations;
- bundle/update runtime events.

Expose logging/events/activity hooks rather than forcing a vendor. Optional `ILogger` and `System.Diagnostics.ActivitySource` adapters belong in hosting/diagnostics packages. Telemetry is off unless the application configures a listener/exporter. High-volume events use sampling/bounds.

A support-bundle API MAY collect snapshot, recent bounded logs, crash/update metadata, and checksums only after showing exactly what is included and allowing application redaction/consent.

## 13. Unit-testing packages

### 13.1 Backend/RPC tests

`NeoAstra.Testing` SHALL provide:

- in-memory transport/RPC host;
- fake view/window/session identities and lifecycle;
- deterministic clock, ID, scheduler, and capability policy;
- fake plugin platform adapters;
- helpers for invoke/event/channel/cancel/error and security assertions;
- snapshot/contract testing helpers;
- no native library/browser requirement.

Fakes MUST enforce the same relevant state machine and validation instead of always succeeding.

### 13.2 Frontend tests

`@neoastra/client/testing` supports mocked generated services/events/channels/resources, invocation assertions, delays/errors/cancellation/navigation, and grant-denied scenarios. It works with common test runners without DOM when possible and must not be mistaken for browser/native conformance.

## 14. Integration automation

A packaged fixture app per backend SHALL expose a test-only automation surface enabled only in test builds. Candidate drivers include WebDriver/Playwright attachment where supported or a narrow NeoAstra automation broker. Selection may differ by backend, but the portable scenario API SHOULD cover:

- launch/readiness and diagnostic snapshot;
- DOM query/action/script under test controls;
- typed RPC/events/cancellation;
- navigation and SPA routes;
- windows/popups/focus/bounds;
- dialogs/menus/tray/clipboard/notifications via adapter/test hooks where OS automation is unreliable;
- downloads/files/capture/print;
- process failure/restart;
- screenshot and trace artifacts.

The test surface MUST be absent or cryptographically/build-time disabled in release output. A command-line switch alone is insufficient if arbitrary users can enable privileged automation in production.

## 15. Verification matrix

Required variants:

- development loopback assets and production custom-scheme assets;
- JIT and NativeAOT;
- clean and persisted profiles;
- main/settings/remote bridge-disabled views;
- debug and release security profiles;
- Windows x64/ARM64, macOS x64/ARM64, Linux x64/ARM64 where runnable;
- X11 and Wayland for relevant Linux UI tests;
- minimum and current supported browser runtimes;
- installer-launched and direct-publish fixture for release smoke.

Tests skip only through explicit capability/platform reasons printed in results. CI SHALL not convert “not run” into “passed.” GUI tests use bounded timeouts, isolated temp user data, and artifact capture.

## 16. Performance and reliability

Extend benchmarks for RPC latency/allocation, event/channel throughput, asset lookup, multi-view layout, captures/PDF, plugin calls, and diagnostic snapshot. Use same-machine/runtime regression baselines. System WebView startup/rendering is reported separately from NeoAstra overhead.

Stress tests cover navigation during calls/events/capture/print, renderer crash, repeated view creation, bounded queues, resource cleanup, plugin shutdown, and long-running idle. Native sanitizers/static analysis continue from v1.

## 17. Accessibility and localization verification

Templates and reference app test keyboard focus, zoom, high contrast, reduced motion, locale/language propagation, screen-reader-accessible HTML foundations, and custom title-bar behavior. Native menu/dialog/notification surfaces rely on OS accessibility but still receive keyboard/label/order tests. Browser engine differences are documented rather than hidden.

## 18. Implementation order

- [ ] Audit every capability/event against actual native and managed operations; correct dishonest reports first.
- [ ] Surface navigation phase/loading/favicon/console events with ordering and bounds.
- [ ] Add typed script evaluation and source-generated serialization.
- [ ] Implement DevTools, find, print/PDF, and capture contracts/adapters.
- [ ] Implement post-creation view bounds and validate multi-view semantics before exposing them.
- [ ] Complete request navigation and selected profile/session controls.
- [ ] Implement redacted diagnostic snapshot and correlated logging/activity hooks.
- [ ] Build backend/frontend testing packages with deterministic fakes.
- [ ] Select and implement test-only cross-platform automation fixture/driver.
- [ ] Add release security, installer smoke, performance, reliability, accessibility, and localization matrices.

## 19. Exit criteria

Every non-`None` modeled browser capability has a public operation/event and conformance coverage. Print/PDF/capture/find/DevTools/console/navigation events and typed evaluation work where claimed. Applications can unit-test RPC/plugins without a browser and run packaged integration scenarios with explicit skips. Diagnostic snapshots/correlation are useful but secret-free, and release builds contain no activatable test backdoor.
