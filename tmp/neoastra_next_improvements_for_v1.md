# NeoAstra next improvements for v1

**Started:** 2026-09-04

**Baseline:** `d9703f7520894a7ece8cde3024ba937fb3570c49`

**Goal:** a small, dependable .NET/system-WebView application platform for CodeAlta, not feature parity with Electron.

**Status:** assessment and implementation in progress; **not a v1 release certification**.

This is the execution checklist requested by the user. The historical v2 analysis remains unchanged;
the current assessment is `tmp/rich_desktop_webview_app_analysis_v3.md`. These two requested documents
are explicitly tracked despite the repository's general `tmp/` ignore rule. Local verification logs
remain ignored. Each implementation step includes this checklist update in its commit.

## Scope and acceptance

- Preserve the simplified `NeoApp`, single application package, frontend-neutral build, generated
  contracts, and controlled-local trust defaults. Do not reintroduce routine capability boilerplate.
- Fix demonstrated integration/inner-loop faults before adding platform APIs or abstractions.
- Keep CodeAlta and comparison repositories read-only. No publishing, signing, installs, dependency
  upgrades, automatic updates, or production/configuration changes.
- Distinguish source implementation, configured CI, locally executed checks, and target-host release
  qualification. A checked implementation item does not certify another OS or an installer.

## Execution checklist

- [x] **1a — Baseline audit.** Read current docs/source/CI; run Release build, managed tests,
  frontend checks, Windows browser conformance, quick benchmarks, and engineering tests.
  Baseline: build clean; 187 managed and 34 frontend tests pass; 14 browser cases pass, 20 skip;
  engineering suite exposes a stale ABI 1.9 assertion against the intentional pre-release 1.0 header.
- [x] **1b — Current comparative assessment.** Integrate read-only InfiniFrame/Tauri/wry and CodeAlta
  research, document trade-offs and source refs, rank release gates, and commit assessment/checklist.
  v3 records current source baselines, corrects historical default-deny/package claims, and includes
  CodeAlta streaming/interaction/content-isolation requirements. Research exposed streaming lifetime
  defects; steps 2b/2c were added before implementation, alongside the small simple-host lifecycle hook.
- [x] **2 — Hosted UI startup.** Reproduce awaiting the injectable dispatcher from application startup;
  separate dispatchability from application readiness without prematurely publishing `Ready`.
  Cover startup failure/cancellation and retain bounded teardown; update lifecycle XML docs/guide.
  Verify focused hosting tests with the staged Windows native runtime, then review and commit.
  Reproduced the five-second startup deadlock before the fix. All 11 hosting cases now pass on
  Windows (independently rerun by the parent); ten child repeat runs also passed. Added early-stop,
  pre-dispatch failure, and post-dispatch failure/cancellation coverage; old native skips are now
  inconclusive. Post-readiness failure logging is source-reviewed, not fault-injected. Noncooperative
  startup callbacks and macOS/Linux native-thread qualification remain limitations, not solved claims.
- [x] **2b — Streaming service ownership.** Reproduce lazy channel enumeration after premature
  per-invocation service disposal. Retain the service lease until channel termination (including
  failed admission/cancellation), preserving scoped teardown and generated/AOT-safe registration.
  Add focused lifecycle regressions, update RPC docs, verify generated fixtures, and commit.
  Reproduced premature disposal, then retained invocation-specific leases through enumerator cleanup.
  Admission now reserves capacity before ID delivery, and abandoned results release their lease.
  Parent review also reproduced reentrant-close deadlock during pump sends; close now accepts
  cancellation without awaiting its own pump. All 41 RPC/generator tests pass (parent rerun plus
  child repeats), covering all lifetimes, cancellation, admission, conversion/send/enumeration/disposal
  failures, and concurrent teardown. Direct callers must dispose owned channels. Noncooperative
  enumeration/send can keep teardown pending after the warning; no forced safe teardown is claimed.
- [x] **2c — Frontend channel cancellation.** Keep an `invokeChannel` abort signal effective after
  opening the channel, remove listeners on every terminal path, and test cancellation/open races.
  Preserve the existing bounded overflow policy; document that transport ACKs are not durable
  end-consumer backpressure. Verify the frontend suite, then commit.
  Implemented before independent step 2b. The regression first observed zero close frames on abort;
  all 38 frontend tests/checks now pass (10,213 gzip bytes). Added the matching instance convenience
  method, covered result/claim and connection-loss races, all terminal listener cleanup paths, and
  iterator-return buffer disposal. No protocol changes or dependencies.
- [x] **3 — Reliable development readiness.** Regressions first: reject HTTP error pages as ready,
  retry individual request timeouts until the bounded overall deadline, propagate user cancellation,
  and cancel/observe readiness when the frontend exits early. Keep redirects denied and do not
  introduce process spawning abstractions or dependencies. Update tooling docs, verify, and commit.
  Reproduced all three faults before fixing them; also reproduced the simultaneous ready/exit race.
  All 32 tooling tests pass, including a real loopback HTTP request-timeout/retry test and cancellation
  tests. Documented that 2xx reachability is not server-process authentication. No new dependencies.
- [x] **4 — Honest, repeatable validation.** Fix the stale engineering ABI test with explicit parser
  fixtures; execute engineering tests in ordinary CI. Extend the opt-in conformance workflow to
  actually run the browser harness, bound execution, and retain logs including skips (not just the
  desktop-service smoke fixture). Do not present configured jobs as passing runs. Verify locally,
  update support/limitation docs, review, and commit.
  Engineering tests now pass (7 pass, 1 explicit Windows symlink skip), with numeric/malformed parser
  fixtures. Ordinary CI runs them; opt-in conformance now runs browser stress and retains logs,
  OS/SDK metadata and native SHA-256 with bounded steps. Both workflow YAML files parse locally.
  Windows browser stress rerun: 15 pass, 19 explicit skips; no workflow was dispatched. Corrected
  stale strict ABI-minor pairing/package claims in support docs. Other RID evidence remains open.
- [x] **5a — Simple-host lifecycle extension.** Add one backend-only main-window configuration
  callback to `NeoAppBuilder`, before the window is shown/navigation starts, so close/quit/launch
  handlers do not require rebuilding the secure host. Keep existing lifecycle types and security
  policy; validate registration and native callback ordering/failure cleanup. Document and commit.
  Added `ConfigureMainWindow(Action<NeoApplication, NeoWindow>)` with one-registration validation
  and XML docs. All 11 NeoApp tests pass; two native Windows cases verify UI-thread/hidden-window
  ordering, original failure propagation, and window cleanup without creating a browser. Other OS
  guards are inconclusive. The callback attaches existing typed async lifecycle handlers, not async void.
- [x] **5b — CodeAlta adoption guide and golden-path documentation.** Correct the stale mandatory-RPC
  permission claim, explain restricted-view registrations, remove template lockfile/version ambiguity,
  and provide a concrete headless-host integration recipe and
  ownership/security/testing contract: one backend event consumer, bounded per-view fan-out,
  replay/resync, cancellation versus durable agent work, safe untrusted content, and UI dispatch.
  Separate NeoAstra responsibilities from CodeAlta follow-up. Link it from public docs; commit.
  Added `doc/codealta-integration.md` against the reviewed CodeAlta source, not an executable GUI.
  It names actual composition/runtime/provider/approval APIs and their cancellation/ownership limits;
  records missing approval recovery and bootstrap rollback, and requires one runtime event consumer.
  Corrected permissionless RPC wording, template lockfile/bootstrap advice and unqualified version
  examples; linked the consumer path and lifecycle hook. No CodeAlta edits or dependency installations.
- [ ] **6 — Final verification and review.** Re-run managed build/tests, frontend checks, engineering
  tests, advanced deterministic validation, and Windows browser conformance/stress as practical.
  Record actual results and outstanding gates here and in the assessment; leave worktree clean.

## Mandatory release gates (remain unchecked until retained evidence exists)

- [ ] **R1 — Native/browser matrix:** Windows x64/ARM64, macOS x64/ARM64, Linux x64/ARM64 against the
  actual shipped artifacts. Retain OS/engine/RID, command, artifact identity, passes/failures/skips.
  Exercise module/worker/font loading, HMR, close/quit, multi-window/session replacement, and real
  native services; review each skip rather than counting it as a pass.
- [ ] **R2 — Hosting on macOS/Linux:** qualify native thread/main-thread ownership and complete
  startup/quit/failure behavior. Windows-only hosted tests cannot certify this. CodeAlta should use
  explicit native-main-loop ownership where the Generic Host path is not qualified.
- [ ] **R3 — Stable API/ABI/package pair:** review the public surface, freeze the macOS minimum and
  support policy, regenerate all RID assets together, and replace the temporary major-only ABI
  acceptance with a reviewed release compatibility policy. Test real package/template consumers
  outside the checkout, framework-dependent and NativeAOT, without unpublished/source-only paths.
- [ ] **R4 — Distribution:** retain install/launch/upgrade/uninstall, signing/notarization and artifact
  provenance evidence per supported package format. Keep updater experimental/disabled unless
  installation, interruption, rollback and authenticity scenarios are qualified on target hosts.
- [ ] **R5 — CodeAlta vertical slice:** a real GUI drives an existing session, receives streaming
  output, survives reload/reconnect and slow consumers, honors approvals/cancellation, and quits
  without losing work or leaking subscriptions. Benchmark real transcripts, not just raw bridge IPC.
- [ ] **R6 — Product acceptance:** keyboard/IME/accessibility, large transcript virtualization,
  high-DPI/multi-monitor restoration, CSP/Markdown review, and cross-engine visual tests.

## Deferred, not prerequisites for the first CodeAlta slice

General DOM automation, bundled Chromium, mobile, native binary transport, utility-process framework,
plugin marketplace, broad browser API expansion, and UI-component ownership. Add them only when a
measured application need justifies the extra API and release matrix. Diagnostics/DevTools remain
useful follow-up; do not confuse their absence with a need to redesign the typed bridge.

## Verification record / deviations

- Baseline logs: `tmp/v1-baseline-{build,test,frontend,conformance,benchmarks,engineering}.log`.
- No dependencies were installed for the baseline; existing restored/build toolchains were used.
- Windows conformance is real hidden WebView2 execution; benchmarks are same-machine smoke numbers,
  not a comparison to InfiniFrame/Tauri and not total browser-process memory measurements.
- Cross-platform, real package distribution, and CodeAlta GUI qualification require target-host and
  application work; these gates cannot safely be checked off by this Windows-only repository pass.
- Additional baseline checks: advanced deterministic validation passes; Windows browser `--stress`
  passes 15 cases (including 100,000 messages), with 19 explicit skips. All four existing native
  CTest executables pass from `artifacts/native/win-x64` (prebuilt assets, not a fresh native build).
  An initial CTest attempt in the old `windows-x64-release` directory could not run its four tests:
  that stale generated configuration points to the former `NeoWebView` checkout. It was preserved;
  using the current `win-x64` directory resolved the verification-path error.
