# NeoAstra v1 readiness for a CodeAlta GUI

**Assessment:** 2026-09-04

**NeoAstra baseline:** `d9703f7520894a7ece8cde3024ba937fb3570c49`

**Execution checklist:** [neoastra_next_improvements_for_v1.md](neoastra_next_improvements_for_v1.md)

## Decision

**Keep NeoAstra's architecture and continue with a controlled CodeAlta GUI integration. Do not yet
call the complete cross-platform product release-qualified.** The highest-value work is now reliable
hosting, streaming ownership, a coherent consumer workflow, and retained target-host evidence—not
another RPC architecture, more package layers, or an Electron-sized API inventory.

NeoAstra's strongest position is a **.NET-first, frontend-neutral system-WebView desktop platform**:
ordinary C# services, generated TypeScript/JavaScript contracts, optional NativeAOT, secure local
assets, and explicit native/application ownership. It is architecturally closest to Tauri. It does
not inherit Electron's browser consistency, ecosystem, or process isolation merely by offering web
UI, and there is no measured evidence here that it is faster than either comparison project.

The July v2 report remains useful historical context, but is not current. It predates package
consolidation, the `NeoApp` entry point, integrated static frontend materialization, additional window
management, and simplified trusted-local RPC. In particular, its repeated claim that *all* custom
application RPC is default-deny is now false. Preserve that report rather than rewriting its history.

## Evidence and limits

| Checkout | Reviewed baseline | Scope |
| --- | --- | --- |
| NeoAstra | `d9703f7520894a7ece8cde3024ba937fb3570c49` | Source, docs, tests, build/tooling, workflows, local Windows execution |
| InfiniFrame | `1f4ade4f2c30c4837df31f59f46180d7c3ea457d` | Read-only source/docs |
| tauri-apps/tauri | `3f5d3984bc8916b5dd31289b19284637ede37e3d` | Read-only CLI, frontend API, ACL/runtime source/docs |
| tauri-apps/wry | `4ee8c38651683ca69530a837a830d0e4028a6c44` | Read-only WebView/IPC source/docs |
| CodeAlta | `7ef14f72e128798e4134036d462eff16158d1f7e` | Read-only reusable host, runtime/event/interaction architecture; no private configuration or session data |

The local `tauri-apps` folder contains `tauri` and `wry`, not the separate scaffold generator, official
plugin catalog, or documentation repositories. No comparison application was built or benchmarked.
Electron/Wails/Photino/Blazor remain architectural context from v2, not newly verified release claims.
Two read-only research sub-sessions covered comparisons and CodeAlta independently; implementation
decisions and verification remain the driving session's responsibility.

Use four distinct labels throughout release work: **implemented** (source), **configured** (CI exists),
**executed** (named command/host ran), and **release-qualified** (actual supported artifact/scenario
matrix passed with retained evidence). Silent native-test early returns and harness skips are not
positive runtime evidence.

### Executed baseline on Windows x64

- `dotnet build -c Release --no-restore` in `src`: success, zero warnings/errors.
- `dotnet test -c Release --no-build --no-restore`: 187 pass; some existing native tests can silently
  return on unsupported hosts/missing assets, so this count alone does not prove native coverage.
- `npm run check` in `frontend`: 34 tests pass; TypeScript, package/license/provenance, fixture and
  template checks pass. Production client ESM is 9,891 gzip bytes against a 20,480-byte budget.
- Hidden Windows WebView2 conformance: 14 cases pass, 20 explicitly skip. Real local navigation,
  scripts, messaging, storage/profile isolation, teardown and view lifecycle ran. Promise evaluation,
  JS exception reporting, interactive native decisions, process failure and other skips remain gaps.
- Quick benchmarks execute successfully. Illustrative warm figures: 35.5 ms/view creation,
  258 microseconds/script evaluation, 30.7k small inbound messages/s. In-memory generated RPC is
  10.8 microseconds/op and about 2.8 KiB managed allocation/op. These short same-machine probes
  include engine/scheduler noise, exclude browser child-process memory, and are **not** a CodeAlta
  workload or a competitor performance comparison.
- `python -m unittest discover -s eng/tests`: **fails** because a test still expects ABI 1.9 while
  the intentionally reset pre-release public header says 1.0; one symlink test skips on Windows.

Logs are in ignored `tmp/v1-baseline-*.log`; final results belong in the execution checklist below.
No dependencies, signing identities, runtime installers, or external publishing were needed.

## What the current product does well

1. **Low ceremony without losing an escape hatch.** `NeoAstra` is the ordinary application package;
   `NeoAstra.Core` is for embedders. `NeoApp.Run` creates a secure local one-window app. Static HTML
   needs no Node pipeline; React/Vue/vanilla use ordinary tooling. The SDK carries its aligned client
   package and generated bindings (`readme.md`, `src/NeoAstra/Application/NeoApp.cs`,
   `doc/frontend-tooling-and-assets.md`). This is a material improvement since v2.
2. **End-to-end application contracts.** Explicit services plus source-generated serializers and
   TS/JS binding hashes are a stronger normal path than caller-asserted generic `invoke<T>` results.
   Keep the AOT-safe JSON context requirement rather than hiding reflection behind convenience.
3. **Honest trust boundaries.** Same-origin navigation policy, manifest-verified assets, CSP, exact
   origin admission where available, and explicit Linux whole-view trust are good foundations.
   Permissionless trusted application RPC and explicitly scoped sensitive operations are different
   policies, not contradictory goals (`NeoRpcHost.AuthorizeAsync`, capability/security guide).
4. **Ownership is designed in.** Document-session revocation, bounded calls/channels/resources,
   async close/quit, single instance, desktop-service ownership and static composition fit an editor
   with long-running .NET work. The remaining lifetime bugs should be fixed inside that model.
5. **Delivery is inspectable.** Deterministic bundles, installer inputs and authenticity mechanisms
   exist. The experimental updater and unqualified installers are correctly not stable promises.

## Local comparison: what to borrow, what not to copy

| Concern | NeoAstra | InfiniFrame source evidence | Tauri/wry source evidence | v1 action |
| --- | --- | --- | --- | --- |
| First run | One app package, `NeoApp`, SDK-owned frontend runtime, static/no-Node path | Very concise window/Blazor examples (`README.md:31–67`) | Prominent creation command; CLI owns dev/watch/readiness (`README.md:20–28`, `crates/tauri-cli/src/dev.rs`) | Borrow presentation economy and a tested consumer golden path, not more dependencies |
| App bindings | Generated DTO methods and contract hash | General named-handler bridge takes string payloads and returns `Promise<string>` (`InfiniFrameEvents.Messaging.cs:66–107`, `InfiniFrameHostMessaging.ts:144–175`) | `packages/api/src/core.ts:227–256` has `invoke<T>(cmd: string, args)`; channels have ordering/cleanup | Preserve generated contracts; make streaming examples easier to discover |
| Ordinary command trust | Permissionless registered operations are callable within an admitted view | General handler dispatcher does not inspect its origin argument before dispatch (`...Messaging.cs:18–107`) | Local app commands can run without app ACL; plugin/remote/app-ACL commands resolve authority (`crates/tauri/src/webview/mod.rs:1845–1908`) | Do not describe NeoAstra or Tauri as universally default-denying local application methods |
| Linux sender provenance | Reports unknown origin and requires explicit whole-view trust | Reads `window.location.href`; Blazor substitutes app URI when origin missing (`WebKitMessaging.Gtk.cpp:62–72`, `InfiniFrameWebViewManager.cs:186–217`) | wry documents iframe IPC using main-frame URL (`src/lib.rs:1179`, `src/webkitgtk/mod.rs:721–740`) | Never infer sender/frame authority from current top-level URL |
| CSP | Controlled production asset policy | Not fully audited here | Local CLI config template emits `csp: null` (`crates/tauri-cli/templates/tauri.conf.json:22–24`) | Retain secure production defaults; do not copy permissiveness to simplify onboarding |
| Lifecycle | Backend-coordinated async close/quit; simple host hides the application/window | Inspected closing handler path is synchronous (`InfiniFrameEvents.cs:191–203`) | Async frontend close handler (`packages/api/src/window.ts:1905–1937`) | Small backend setup hook; don't hand final shutdown authority to the renderer |
| Shipping | Several implemented formats, qualification open | Single-file packaging advertised, not qualified by this review | Broader integrated bundling, not independently qualified here | Validate actual artifacts, not format counts |

The provenance differences above are source observations, **not demonstrated exploits** in the other
projects. They reinforce NeoAstra's existing threat model rather than justify competitive security
superlatives. There is no reason to port InfiniFrame's Blazor model into a frontend-neutral platform.

## Prioritized baseline findings

These findings describe the starting revision, not the fixed working tree. See the implementation
outcome and companion checklist for completed changes and their verification.

### P0 before CodeAlta relies on the affected path

**Hosted startup circular wait.** `NeoHostedService.InvokeAsync` awaits `_ready`; `_ready` is set
only after `INeoHostedApplication.StartAsync` returns (`src/NeoAstra/Hosting/NeoHosting.cs:229–247`).
Startup that awaits a dependency using the injected dispatcher deadlocks. Separate dispatchability
from readiness; preserve original startup exceptions and authoritative host-stop ordering. Also
cover a host stop recorded before the application becomes available. Existing tests mostly store
the application and return, so they miss this combination.

**Lazy channel/service lifetime mismatch.** Generated channel methods use the ordinary activator;
`PerInvocation` disposes its service immediately when the method returns, before the host enumerates
the returned channel (`NeoRpcServices.cs:44–56`, `NeoRpcHost.cs:569–608`, generator registration around
`NeoRpcGenerator.cs:491–540`). A channel over disposable dependencies can read a disposed service.
Retain ownership through streaming and release it on admission failure, cancellation, session close,
and normal/faulted completion. Test composed lifetimes, not just activation and channels separately.

**Restricted-view misunderstanding.** `NeoRpcHost.AuthorizeAsync` permits operations with null
`Permission` even when an authorization service exists. Adding a restrictive capability manifest
does not hide such methods. `doc/rpc-and-bindings.md:43` still says every operation needs a permission.
Fix the documentation, preserve intended trusted-local semantics, and explain separate restricted
registries/explicitly permissioned sensitive operations. Never give active untrusted previews the
main GUI's bridge. Capability checks do not establish CodeAlta project/session ownership.

### P1: dependable day-to-day experience

**Channel cancellation ends too early.** Frontend `invokeChannel` passes its signal to the opening
invocation only; its listener is removed on result, not channel completion (`frontend/packages/client/src/rpc.ts`).
Keep cancellation attached to the returned observation, with exact-once cleanup. Separately, ACKs
mean transport admission, not iterator consumption: slow consumers can overflow the bounded client
buffer. Preserve a clear failure/resync policy rather than claiming lossless backpressure.

**Development readiness can launch a broken page.** `NeoReadinessProbe.ValidateResponse` accepts
4xx as ready; the 2-second HTTP request timeout escapes instead of retrying until the 60-second
overall deadline. An early frontend exit leaves the readiness task running. Require success status,
retry transient request timeout, cancel/observe abandoned work, and retain redirect rejection.
An exact loopback URL is not proof of server ownership; use Vite strict-port settings and do not run
untrusted local development servers with the privileged bridge.

**Simple-host lifecycle cliff.** The builder exposes dimensions/RPC/permissions/links but hides its
main application/window. A single backend-only setup callback before showing/navigation can attach
existing close/quit/launch handlers without duplicating the secure hosting code. Advanced multi-view
or long-running-service applications should still use explicit `NeoApplication` ownership.

**Validation and onboarding inconsistencies.** The opt-in `conformance.yml` currently runs a desktop
service smoke fixture, not the browser conformance executable. Engineering ABI tests are stale.
The tooling guide says generated templates need `npm install`, then says they already include a
lockfile. README shows `1.0.0` while saying unavailable. Fix these contradictions and retain real
browser logs/skips; do not hide known gaps behind green aggregate counts.

## CodeAlta integration contract

### Use the headless composition seam

Compose `CodeAlta.Orchestration.Hosting.CodeAltaHost`, not `CodeAltaApp`, terminal controls, or
`CodeAltaFrontendComposition`. The headless host and options own runtime/provider/plugin seams
(`CodeAltaHost.cs:55–110,119,320–327`, `CodeAltaHostOptions.cs:48–75`). Some bootstrap is still frontend
owned (`CodeAltaOwnedServices.cs:95–176`); a web-ready application host is not already complete.
Keep provider initialization separate from history/session browsing so a slow/offline provider does
not block the shell. All CodeAlta work remains outside this NeoAstra-only implementation pass.

The [public adoption recipe](../doc/codealta-integration.md) records the source-verified API shapes:
interactive GUI flags even though the reusable seam is called headless, plugins disabled explicitly
for a minimal baseline, fresh owned provider-runtime factories, and independently observed provider
initialization. Cancellation of the initialization wait does not stop the probe. The inspected host
lacks encompassing creation rollback and failure-aggregating disposal, so failure-injected ownership
qualification is still CodeAlta work. Stored history should use `TryReadStoredHistoryAsync` before
considering provider-dependent attach/resume operations.

### Stream projections, not the runtime object graph

CodeAlta's `SessionRuntimeService.StreamEventsAsync` has one bounded competing-reader stream;
publication can drop new events (`BoundedRuntimeEventStream.cs:29–62`, `SessionRuntimeService.cs:35,75–78,118`).
Two windows reading it directly divide the events rather than receive broadcasts. Use one
application-owned consumer, bounded per-view projections, coalesced rendering, and explicit
overflow/resync against authoritative snapshots/history. Current events lack a universal replay
cursor; NeoAstra's channel sequence cannot repair upstream dropped events.

Use small generated DTOs for summaries, paged history, run controls, interactions and attachments.
Raw provider events can contain polymorphic/unbounded/private payloads (`CodeAlta.Agent/AgentEvent.cs`)
and are not an appropriate renderer contract. Do not expose an unrestricted `alta` command gateway.
Measure real transcript latency/allocation/DOM costs before adding native binary transport.

### Separate authority, observation, and execution

- A durable CodeAlta session/run is not a replaceable NeoAstra document session or frontend tab.
- Closing/reloading an observation releases RPC resources; it does not implicitly abort durable work.
  An explicit Stop action calls the backend abort command after ownership/policy validation.
  `SendAsync` is not universally an immediate acceptance response; its raw agent path can await the
  turn and link the supplied token to run cancellation. Keep that task/token application-owned.
- Agent permission/input requests need an application-owned exact-once registry correlated by
  session/run/interaction, not just events on a lossy transcript stream (`SessionExecutionOptions.cs:70–78`,
  `SessionPermissionRequestCoordinator.cs:32–72`). Decide explicitly what window loss does to them.
  No public pending-approval list/resolve API was found in the inspected host/runtime;
  `ReconcileRecoverableSessionCacheAsync` is not approval reconciliation. Backend authorization must
  resolve project/session ownership for reads, subscriptions and decisions as well as commands.
- Browser capability, agent/tool approval and provider policy are separate gates. A capability grant
  must never become blanket approval of tool actions.
- CodeAlta's existing “sanitized” event projection strips scheduling markup, not HTML. Markdown,
  tool/repository content, SVG, links and plugin cards remain untrusted (`SessionRuntimeService.cs:2881–2947`).
  Render text safely, disable/sanitize raw HTML, restrict URLs, and isolate active previews with no
  bridge. Same-document XSS inherits bridge authority, especially under Linux whole-view trust.
- Use the native dispatcher only for UI-owned work; keep streaming/provider/filesystem work off it.
  Preserve the UI synchronization context in UI callbacks. Quit must explicitly settle/persist drafts,
  interactions and backend ownership while the native loop still pumps.

## What blocks an honest excellent v1.0 claim

1. **Actual platform matrix:** WebView behavior, native services, close/reload/quit and generated
   application transport on all claimed RIDs; Windows ARM64 execution and macOS/Linux release
   browser evidence remain open. Generic Host's dedicated UI thread requires explicit AppKit
   main-thread qualification; Windows-only tests cannot certify it.
2. **Shipped package/API/ABI:** freeze the macOS minimum and public API policy; replace temporary
   major-only ABI compatibility with reviewed release pairing; rebuild every native asset; validate
   consumers outside this checkout and execute NativeAOT artifacts. Do not simply bump a version.
3. **Distribution:** real install/launch/upgrade/uninstall, signing/notarization and artifact evidence.
   Keep updates experimental until target-host authenticity/interruption/rollback acceptance passes.
   This session does not dispatch publishing workflows or inspect credentials.
4. **Real application slice:** two windows, streaming, slow/paused renderer, reload/reconnect,
   pending approval, explicit abort and orderly shutdown against CodeAlta's actual backend.
5. **Product quality:** keyboard/IME/screen-reader/high-DPI, transcript virtualization, CSP/content
   review and cross-engine visual behavior. Neither low bridge latency nor an API inventory proves UX.

These remain unchecked release gates in the companion plan. New diagnostics/DevTools and richer
browser operations are worthwhile follow-up, but broad Step 8/9 completion is not a prerequisite
to a useful first CodeAlta GUI. Conversely, adding those APIs cannot substitute for these gates.

## Implementation outcome

**The scoped implementation pass is complete. NeoAstra is a better foundation for the first CodeAlta
slice, not yet a release-qualified excellent v1.** Baseline findings above intentionally preserve the
evidence that drove the changes; the companion checklist records commands, regressions and deviations.

- Hosted startup now separates dispatcher availability from readiness; startup can await dispatched
  work without circular waiting. Failure/cancellation/early-stop tests cover native cleanup and
  original exception propagation. Post-readiness loop-fault logging was source-reviewed, not injected.
- Backend channels retain invocation-specific service leases through enumerator cleanup, reserve
  capacity before publishing IDs, and release abandoned results. Reentrant close accepts cancellation
  without awaiting its own pump. Frontend abort remains effective after opening and cleans terminal
  listeners/buffers. This is not durable/lossless application-consumption backpressure.
- Development readiness now requires 2xx, retries request timeouts within the total deadline, and
  cancels/observes readiness after early frontend exit, including simultaneous ready/exit completion.
- Engineering tests now validate the actual header plus explicit parser fixtures. Ordinary CI runs
  them; opt-in conformance runs browser stress and retains logs/skips and host/artifact metadata.
  These workflow changes were parsed locally, not dispatched remotely.
- `ConfigureMainWindow` adds one backend-only simple-host lifecycle setup point without changing
  browser trust/navigation policy. The public [CodeAlta guide](../doc/codealta-integration.md), included
  in the application package, documents real composition APIs and application-owned policy still
  needed. Consumer documentation now distinguishes included lockfiles, reviewed versions and
  restricted registrations from permissionless trusted-local RPC.

### Executed final checks

| Check | Result on this Windows x64 host |
| --- | --- |
| Release solution build | Success, zero warnings/errors; no restore/install |
| Managed suite | 209 passed, 0 failed/reported skipped; not blanket proof of every legacy native guard |
| Frontend checks | 38 tests pass; TypeScript, package/license/provenance, fixtures/templates pass; 10,213 gzip bytes |
| Engineering suite | 7 pass; 1 explicitly skipped Windows symlink case |
| Advanced sample | Deterministic contract/asset/feature-tour validation passes, not browser acceptance of every feature |
| Windows WebView2 stress | 15 pass, 19 explicitly skipped, including a passing 100,000-message case |
| Desktop/native | Desktop smoke exits 0; 4 existing native CTest executables pass |
| NativeAOT | Fresh win-x64 compile and default RPC/desktop fixture plus native desktop smoke executions pass |
| Package/docs | Local package contains exact adoption guide; relative document targets and changed workflow YAML validate |

NativeAOT initially failed because no-restore assets referenced compiler `10.0.10` while the current
SDK selected runtime `10.0.11`. Aligning this invocation to the cached `10.0.10` runtime and using a new
native intermediate directory produced a passing fresh compile/run; dependencies were not changed.
A raw generated-TypeScript comparison also exposed local LF/CRLF differences only; normalized contracts
match and tracked generated files are unchanged. These deviations are retained rather than concealed.

The checklist retains host/artifact identities and full reproduction commands. Browser/desktop smoke
used the staged native DLL; CTests used a different prebuilt DLL. Neither was freshly rebuilt or
release-paired here. NativeAOT smoke still uses the staged library, not a clean-host installation.
The real CodeAlta GUI, macOS/Linux hosting, full RID/browser/UX matrix, strict release package/ABI
policy and installer/update qualification remain open. No comparison checkout or CodeAlta data was
modified, and historical v2 remains untouched.
