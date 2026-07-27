# Step 5 — Application Lifecycle, Launch Routing, and Hosting

**Parent:** [`neoastra_v2_specs.md`](../neoastra_v2_specs.md)
**Depends on:** v1 application/window core and [Step 3](03-capabilities-and-security.md)
**Outcome:** Real desktop applications can negotiate close/quit, receive every launch reason, route second instances, and integrate cleanly with .NET hosting.

## 1. Scope

This step fixes the ineffective v1 cancelable-close surface, defines app lifecycle ordering, queues early operating-system launch events, adds secure single-instance routing, and supplies optional `Microsoft.Extensions.Hosting` integration. It does not create a new background-worker framework.

## 2. Application state machine

The application SHALL expose behavior equivalent to:

```text
Created
  -> Starting
  -> Ready
  -> QuitRequested
  -> ClosingWindows
  -> Stopping
  -> Stopped
```

A fatal startup failure transitions `Starting -> Stopping -> Stopped`. Calls invalid in the current state fail predictably. State transitions occur exactly once and are observable through diagnostics.

Definitions:

- **Starting:** native event loop exists and early launch data can be queued; startup services are not ready.
- **Ready:** startup callback/host has completed and queued launch events may dispatch.
- **QuitRequested:** one logical quit negotiation is active; additional requests join it.
- **ClosingWindows:** approved quit closes windows in deterministic ownership order.
- **Stopping:** renderer/RPC/plugin/background services are being canceled and disposed.
- **Stopped:** native loop has exited and no new work is accepted.

## 3. Cancelable window close

### 3.1 Native contract

Window close MUST become a deferred native decision rather than a notification that ignores `Cancel`. The event supplies a close request ID/decision with:

- reason (`user`, `owner`, `application_quit`, `session_end`, `system`, `programmatic`);
- deadline and default action;
- whether cancellation is permitted;
- a completion operation accepting allow/cancel;
- exactly-once resolution and safe timeout behavior.

The native backend MUST prevent destruction while a cancelable decision is pending. Repeated OS close signals for the same window are coalesced. Programmatic force-close used during approved teardown bypasses repeated negotiation only through an internal explicit path.

Default on timeout/error:

- ordinary user/programmatic close: cancel, preserving potential unsaved work;
- approved app teardown after all handlers accepted: allow;
- non-cancelable OS session termination: acknowledge the platform constraint and continue best-effort shutdown without claiming cancellation succeeded.

Exact ABI additions require size/versioning, generated interop, compatibility tests, and platform-specific event-order tests.

### 3.2 Managed API

A managed shape SHOULD be asynchronous and explicit:

```csharp
public event Func<NeoWindowCloseRequest, ValueTask>? CloseRequested;

public sealed class NeoWindowCloseRequest
{
    public NeoWindowCloseReason Reason { get; }
    public CancellationToken DeadlineToken { get; }
    public void Cancel();
}
```

If .NET events cannot express async aggregation safely, use a registration API or application-level close coordinator. Multiple handlers execute in documented order; any cancellation cancels close. Exceptions are logged and apply the safe default. Handlers MUST NOT block the native UI thread synchronously while waiting for web UI.

A common unsaved-work flow can ask the renderer through RPC, but renderer failure/navigation/timeout MUST not silently discard data.

## 4. Quit negotiation

Public concepts SHOULD include `QuitRequested`/`BeforeQuit`, `Stopping`, `Stopped`, `RequestQuitAsync`, and a deliberate force/urgent shutdown path.

Normal quit sequence:

1. accept/coalesce quit request and record reason/exit code;
2. stop accepting new top-level work that would defeat shutdown policy;
3. invoke application before-quit handlers;
4. if canceled, return to `Ready` and keep windows/RPC active;
5. request close negotiation for top-level windows in deterministic order (owned children before owners or an explicitly documented equivalent);
6. if any cancel, restore `Ready`; already closed windows remain closed and this partial outcome is documented;
7. once approved, enter `Stopping`, revoke renderer capabilities, cancel calls/subscriptions/resources, and stop plugins/background services;
8. destroy views/windows/profiles/environment/application in existing safe ownership order;
9. exit native loop and raise stopped notification.

The implementation SHOULD support a preflight mode that asks all windows before closing any, avoiding partial close where feasible. It MUST define behavior when a window is created or close is requested during negotiation.

`RequestQuitAsync` completion distinguishes canceled, completed, and forced/system termination. Reentrant quit requests join the active operation. A force API MUST be clearly named, backend-only by default, and unavailable to renderer code without a high-risk permission.

## 5. Activation and launch events

Required launch reasons:

- initial activation with command-line arguments and working directory;
- normal activation/reopen (including macOS dock activation with no window);
- open one or more files;
- open one or more URLs/deep links;
- second-instance activation;
- platform session-end/shutdown request where observable;
- optional notification/update activation through later plugins using the same queue model.

Use immutable DTOs containing reason, timestamp/order, validated paths/URIs where appropriate, and platform metadata. Raw unbounded environment data MUST not be included.

### 5.1 Early-event queue

Operating systems may deliver open-file/open-URL before managed startup is ready. The native/core layer MUST queue bounded events in arrival order from event-loop initialization. On transition to `Ready`, dispatch them serially through the UI/application dispatcher. Queue overflow is logged and handled by a documented coalescing/failure policy; events MUST not be silently lost.

Duplicate events MAY be coalesced only when identity and ordering semantics remain correct. Launch event delivery remains backend-authoritative and is not synthesized from process arguments twice.

### 5.2 Reopen behavior

Default template behavior may create/restore/focus the main window when activation occurs with no visible windows, but core MUST expose the event rather than impose that policy. On Windows/Linux, explicit app activation can map to the same portable event when available.

## 6. Single-instance routing

Single instance SHOULD be an official core-adjacent service used by launch/deep-link/update flows.

### 6.1 Lock and identity

- identity derives from explicit application identifier plus user/session scope, not display name;
- acquire occurs early enough to prevent duplicate normal startup;
- stale lock recovery is robust against crashes;
- multiple installed channels/versions MAY use separate identities according to bundle policy;
- elevated/non-elevated and sandbox boundaries are documented per OS.

### 6.2 IPC

A second process sends a bounded versioned launch envelope to the first and waits for acknowledgement with timeout. The channel MUST:

- be local-user authenticated using OS primitives/permissions where possible;
- reject remote/network peers;
- validate protocol/version, lengths, path/URL syntax, and message count;
- avoid deserializing arbitrary types;
- contain arguments, working directory, open files/URLs, and activation metadata only;
- never transfer environment secrets or capability grants;
- queue into the same early/ready launch event pipeline;
- focus/restore windows only through application policy after acceptance.

If the first instance is hung/unreachable, the second follows a documented policy (fail, retry, or launch isolated) selected by the application/bundle, never silently starts a competing instance in a way that can corrupt shared data.

## 7. OS session termination

Windows session end, macOS termination, and Linux desktop session behavior differ. NeoAstra MUST expose whether cancellation is supported and the available deadline. It MUST not promise async work can finish when the OS forbids delay. Applications SHOULD persist incrementally and treat shutdown hooks as best-effort.

The runtime SHALL prioritize bounded state flush, resource cancellation, and clean plugin teardown, but MUST honor platform deadlines and avoid deadlocking logout/shutdown.

## 8. Generic Host integration

`NeoAstra.Hosting` SHALL remain optional and SHOULD support:

```csharp
Host.CreateApplicationBuilder(args)
    .UseNeoAstra(options => ...)
    .Services.AddNeoAstraRpc()
    .AddNeoAstraApplication<App>();
```

Required integration:

- configuration and options bind before native app start;
- logging routes NeoAstra categories/correlation IDs to `ILogger` without forcing a provider;
- singleton/application, per-view, per-document-session, and per-invocation scopes are explicit;
- `IHostedService` starts in a documented relation to `Ready` and stops before native teardown;
- `IHostApplicationLifetime.StopApplication` requests normal NeoAstra quit, not force exit;
- NeoAstra quit triggers host stopping exactly once;
- background exceptions follow configured host policy and cannot unwind through native callbacks;
- UI dispatcher is injectable as an abstraction without exposing native handles;
- NativeAOT registration is generated/static.

A hosted service MUST NOT assume it runs on the UI thread. UI work uses `NeoDispatcher`.

## 9. Multi-window and view identity

Window/view labels used by capabilities are assigned during creation and immutable. Application APIs SHOULD permit deterministic lookup by label in addition to numeric ID. Duplicate labels fail before native object creation.

This phase also specifies:

- owner close behavior and ordering;
- modal/enabled behavior deferred to Step 6 but compatible with close decisions;
- popup-created views receive explicit labels/capabilities before bridge activation;
- no remote popup inherits opener permissions automatically;
- app shutdown can enumerate windows without collection mutation races.

## 10. Persistence and crash recovery hooks

Core MAY expose bounded lifecycle checkpoints (`starting`, `ready`, `quit requested`, `stopping`) and a previous-unclean-exit indicator. Actual document/window-state persistence belongs to application/plugin services. Crash recovery MUST NOT automatically restore privileged remote content or stale resource handles.

## 11. Implementation order

- [x] Specify native close decision ABI, reasons, deadlines, and platform mappings.
- [ ] Implement cancelable close on Windows, macOS, and Linux with native tests. (Implementation and compile/CI coverage are present; macOS/Linux runtime verification and the full native close matrix remain unavailable on this Windows host.)
- [x] Replace/repair managed close API and add async aggregation/reentrancy rules.
- [x] Implement application state machine and normal/forced quit coordinator.
- [x] Add bounded early activation/open-file/open-URL/reopen queues and ordering.
- [x] Implement secure single-instance lock and local IPC adapters.
- [x] Add immutable window/view labels and deterministic lookup.
- [x] Implement optional Generic Host scopes/lifetimes/logging/shutdown integration.
- [x] Integrate RPC/plugin revocation with navigation and app stopping.
- [x] Update reference app with unsaved-work and second-launch scenarios.

## 12. Verification

Tests MUST cover allow/cancel/timeout/exception close; duplicate/reentrant close; owner/child ordering; programmatic versus user close; app quit canceled by app or any window; repeated quit callers; new window during quit; renderer unresponsive during save prompt; process failure; OS session end; early open-file/open-URL before ready; ordered multiple events; macOS reopen; single-instance concurrent races, stale lock, invalid peer/message, first-instance startup delay/hang; host start/stop exception/cancellation; scoped-service disposal; and NativeAOT.

- [x] Windows-focused lifecycle, direct owner-close, host ordering/exception, replay suppression, malformed peer, concurrent-process election, and abandoned-lock recovery tests pass.
- [ ] Complete the full cross-platform matrix above. macOS/Linux runtime execution is not available on this Windows host; its runtime-only cases remain delegated to platform CI.

Platform integration tests launch real secondary processes and verify focus/restore policy through observable app state, not timing-only assertions.

## 13. Exit criteria

An editor can asynchronously save or cancel window close and application quit. Every early launch event is delivered once in order. A second launch securely routes bounded arguments/files/URLs to the ready or starting first instance. Generic Host services and NeoAstra teardown exactly once without deadlock or leaked UI state on all supported platforms.
