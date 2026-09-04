# A .NET-backed CodeAlta desktop GUI

This is an **illustrative, source-reviewed adoption recipe, not an executable GUI** or a completed
CodeAlta migration. It targets CodeAlta source at `7ef14f72e128798e4134036d462eff16158d1f7e` as reviewed
on 2026-09-04. Recheck the APIs when adopting it. No CodeAlta configuration, stored sessions, providers,
or plugins were executed for this guide. Start with the [consumer workflow](frontend-tooling-and-assets.md#consumer-path-create-run-develop-publish).

## 1. Compose one backend, not one per browser document

Use `CodeAlta.Orchestration.Hosting.CodeAltaHost.CreateAsync(CodeAltaHostOptions, CancellationToken)`.
Do not embed terminal `CodeAltaApp` or `CodeAltaFrontendComposition`. The reusable/headless composition
seam also serves an interactive GUI: `IsHeadless` should be false, and `HasInteractiveUi` true.

The following is **backend composition only**. Paths, provider descriptor/factory and cancellation
token are application-owned inputs; it does not define the GUI adapter or its lifetime owner:

```csharp
using CodeAlta.Orchestration.Hosting;

var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
{
    GlobalRoot = approvedDataRoot,
    CurrentProjectPath = approvedProjectPath,
    IsHeadless = false,
    HasInteractiveUi = true,
    StartPlugins = false,
    ConfigureModelProviders = registry =>
        registry.RegisterOrReplace(providerDescriptor, createProviderRuntime)
}, applicationStartupToken);
```

Creation can create/update the data root and project catalog; choose isolated test data for the first
slice, not a real user's profile. `StartPlugins` defaults to **true**, hence the explicit minimal
baseline above. Enable plugins only after reviewing `PluginSafeMode`, `PluginServices`, `PluginBuiltIns`
and interactive behavior. `PrestartedPluginRuntime` remains caller-owned; `OwnsLogging` controls
process-wide logging ownership.

`createProviderRuntime` is a `Func<IModelProviderRuntime>` returning a **fresh owned runtime**, not a
shared singleton. Registry probe runtimes and session runtimes are different instances. Creation
registers providers but does not initialize them. Keep the shell and stored-history browsing usable
while an application-owned, observed `ModelProviderInitializationService.InitializeAllAsync` task
runs. Project `CurrentStates` and `StreamStateChangesAsync` into small status DTOs; use
`RefreshProviderAsync` and `GetModelsAsync` for explicit refresh/model selection.

Initialization cancellation cancels the **wait**, not the underlying probe. Probes have an independent
cooperative timeout (30 seconds by default); a noncooperative provider can outlive it. Do not dispose
the registry underneath initialization simply because a document was closed or its wait canceled.
The inspected host also lacks encompassing creation rollback, and its sequential disposal does not
aggregate failures. Fault-injected bootstrap/shutdown hardening is CodeAlta follow-up, not a guarantee
provided by wrapping it in NeoAstra.

## 2. Put a narrow, authorized adapter behind generated RPC

Register small C# service methods and source-generated JSON/TypeScript contracts. The host exposes
`RuntimeService`, not a ready-made public GUI `ISessionOrchestrator`. Application adapters still need
to own execution tasks, authorization, interaction state, projections and reconnection.

| GUI need | Source-verified backend seam | Adapter responsibility |
| --- | --- | --- |
| Projects | `ProjectCatalog.LoadAsync`, `GetByIdAsync` | Project only allowed IDs and safe summaries |
| Sessions | `RuntimeService.ListRecoverableSessionsAsync`, `TryGetActiveSessionDescriptorAsync` | Resolve and authorize project/global ownership |
| Local transcript | `TryReadStoredHistoryAsync` | Handle nullable result; bound/project history before returning web DTOs |
| Attach/resume | `GetHistoryAsync`, `GetOrResumeHistoryAsync` | May attach/resume; do not substitute for provider-independent stored browsing |
| New session | `CreateProjectSessionAsync`, `CreateGlobalSessionAsync` | Construct approved provider/model and execution options in the backend |
| Run/stop | `SendAsync`, `AbortAsync` | Own run task/token independently of replaceable requests |
| Observe | `StreamEventsAsync`, `DroppedRuntimeEventCount` | One backend pump, bounded authorized fan-out, explicit resync |

For a send, the underlying shape is
`host.RuntimeService.SendAsync(session, executionOptions, new AgentSendOptions { Input = AgentInput.Text(prompt) }, token)`.
Do not accept a renderer-supplied `SessionExecutionOptions` or authoritative session descriptor.
Resolve IDs server-side, check `ProjectRef` (including explicit global-session handling), and authorize
**reads, commands, subscriptions and approval responses**. Existing descriptor consistency checks are
not application authorization. Never expose an unrestricted `alta` command gateway to the renderer.

`SendAsync` is not universally an immediate acceptance response: the raw agent path awaits the turn
and links its supplied token to active-run cancellation. Do not simply return that long-running task
from a document-scoped RPC method with its request token. Own and observe command tasks/run tokens in
the application, with a bounded admission policy and correlated status DTOs. An explicit Stop calls
`AbortAsync`; cancelling transcript observation must not silently stop durable work.

After bridge admission, permissionless registered NeoAstra methods are allowed in the trusted app.
A manifest does not filter them. Use restricted registrations or explicitly permissioned operations
for a restricted trusted view. Browser capabilities, project/session authorization, agent tool approval
and provider policy are independent gates. See [capabilities and security](capabilities-and-security.md).

## 3. Give runtime events one reader and a recovery contract

`BoundedRuntimeEventStream` uses a shared channel with **competing readers**, not broadcast subscribers.
The default capacity is 1024, and publication drops incoming events under pressure. Two windows calling
`StreamEventsAsync` directly split events. Keep exactly one application-owned runtime event pump and
fan out allowlisted, size-bounded DTO projections into bounded, nonblocking per-window queues. A slow
window must not stall that pump or every other window.

NeoAstra channel credits acknowledge client **buffer admission**, not application consumption or DOM
rendering. A slow iterator can exhaust its bounded client buffer. Handle terminal overflow, document
replacement and reconnect explicitly: invalidate stale observations and reconcile authoritative
history/session snapshots before resubscribing. `DroppedRuntimeEventCount` is aggregate, and the
runtime has no universal replay cursor; RPC channel sequence numbers cannot recover upstream drops.
Snapshot/subscription ordering and deduplication are adapter responsibilities, not a lossless-delivery
guarantee. Pending approval recovery is separate from transcript reconciliation.

Use one cancellation scope for each observation/document, and a distinct application/run scope for
durable work. `invokeChannel` abort remains effective after opening. Disposing the observation releases
its service/channel resources, not the application host. Backend `channel_close` acknowledges
cancellation acceptance; actual enumerator/service cleanup can still be pending. Direct users of
owned `NeoRpcChannel<T>` values must dispose the enumerator before disposing the channel, including
explicit abandonment of unenumerated channels. See [RPC and bindings](rpc-and-bindings.md).

## 4. Retain approvals outside the lossy transcript

Construct `SessionExecutionOptions.OnPermissionRequest` in the backend; it returns
`Task<AgentPermissionDecision>` (`AllowOnce`, `AllowForSession`, `Deny`, `Cancel`).
`OnUserInputRequest` is optional and needs the same lifetime/correlation discipline when enabled.
Do not translate a browser capability grant into approval of arbitrary tool execution.

The inspected public host/runtime has no pending-approval list/resolve API. Maintain an application-owned
registry of session/run/interaction IDs and pending callbacks: exactly-once completion, cancellation
invalidation, authorized reconnect snapshots, and an explicit policy for window loss and application
quit. Reject stale, replayed and wrong-session decisions. Historical permission events do not prove
the request is still actionable. `ReconcileRecoverableSessionCacheAsync` is **not** approval recovery.

## 5. Keep web content and native ownership distinct

- Never expose raw provider/event graphs, credentials, unrestricted filesystem paths, tool payloads or
  unbounded histories as web DTOs. Review what data is needed before serialization, not after rendering.
- CodeAlta's runtime “sanitization” removes scheduling markup, **not HTML/XSS**. Render text safely;
  disable raw Markdown HTML or use a reviewed sanitizer, restrict link schemes, and review SVG/plugin
  cards/attachments. Open external URLs only through a scoped backend action.
- Active untrusted previews require an isolated view with **no bridge**. An origin string/current
  top-level URL does not authenticate a subframe on Linux's whole-view trust path. Same-document XSS
  inherits that document's bridge authority. Keep production CSP and local asset verification enabled.
- Distinguish durable session/run, native window/view, replaceable RPC document session and frontend
  tab. Never use any one ID as authority for the others.
- Marshal window/dialog work through `NeoDispatcher`; keep provider, history and event processing off
  the UI loop. Start native application operations on the platform UI thread (STA on Windows), not on
  an arbitrary continuation after awaiting host creation.
- `NeoAppBuilder.ConfigureMainWindow` can attach typed close/quit/launch handlers before browser
  creation. For application-owned backend services/multiple windows, prefer explicit `NeoApplication`
  composition. Generic Host's dedicated UI thread is not yet qualified for AppKit main-thread rules.

Quit policy must settle drafts and pending interactions, stop new commands, then cancel/join owned
work and observation pumps while the native loop can still process lifecycle callbacks. Dispose the
host only when no work still uses it. Its current order is `RuntimeService`, `AgentHub`,
`ModelProviderRegistry`, owned plugin runtime, owned logging; do not separately dispose its exposed
services. Report cleanup failures/timeouts without pretending cancellation forcibly stopped a
noncooperative provider, iterator or transport. See [application lifecycle](application-lifecycle-and-hosting.md).

## Adoption acceptance (still to be executed)

1. Use isolated test data/providers; load project/session summaries and stored history without waiting
   for providers. Show offline/initializing/error states without blocking the shell.
2. Send and stop one real run; reload a document during it without implicitly aborting it. Check explicit
   run cancellation, service ownership and shutdown with a pending provider initialization.
3. Open two windows on one session, pause one consumer and force buffer loss. Verify no competing-reader
   event theft, explicit reconciliation, authorization isolation and bounded memory.
4. Reload with an approval pending; resolve once from an authorized window. Reject stale/wrong-session
   decisions, handle tool cancellation, and apply the documented no-window/quit policy.
5. Exercise hostile Markdown/links/attachments and no-bridge previews; test keyboard/IME/accessibility,
   large-transcript virtualization and native close/quit on each actual supported engine/RID.
6. Execute packaged framework-dependent and NativeAOT consumers outside this checkout, including
   install/launch/upgrade/uninstall. Source review and mock RPC tests do not qualify this GUI.

NeoAstra supplies the window/view/dispatcher, local asset policy, generated RPC, document resources and
channel lifecycle. The adapter, durable-run policy, project authorization, approval registry, recovery
and renderer remain CodeAlta application work. Platform/package evidence remains a release gate.

### Source review map (paths within CodeAlta)

- `src/CodeAlta.Orchestration/Hosting/CodeAltaHost.cs`, `CodeAltaHostOptions.cs`: composition/ownership.
- `src/CodeAlta.Agent/ModelProviderRegistry.cs`, `ModelProviderInitializationService.cs`: runtime factories and probes.
- `src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs`, `BoundedRuntimeEventStream.cs`,
  `SessionExecutionOptions.cs`: public operations, event delivery, projection and interaction callbacks.
- `src/CodeAlta.Agent/Runtime/AgentSession.cs`: raw send/run cancellation lifetime.
