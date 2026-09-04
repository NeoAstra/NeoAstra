# Application lifecycle, launch routing, and hosting

NeoAstra uses a versioned `NEOASTRA_DECISION_WINDOW_CLOSE` decision for window close. User and programmatic requests default to **cancel** after 30 seconds; repeated requests coalesce while one decision is pending. Windows `WM_QUERYENDSESSION` is reported separately as a non-cancelable best-effort lifecycle event and is never blocked for asynchronous work. macOS window close and GTK delete events are deferred without blocking their UI thread. An approved managed quit uses the internal `neoastra_window_force_close` path only after application and window policy has completed. That function is backend API, is absent from browser transport/RPC, and should not be used for ordinary close.

## Lifecycle policy in the simple host

`NeoAppBuilder.ConfigureMainWindow` provides one synchronous, backend-only configuration callback.
It runs on the native UI thread after `application.MainWindow` is assigned, while the window is
hidden, before RPC/browser creation or navigation. Register existing close/quit/launch handlers or
set window properties here without rebuilding the secure host. For example, with an application-owned
`editor` that implements saving and launch handling:

```csharp
app.ConfigureMainWindow((application, window) =>
{
    window.CloseRequested += async request =>
    {
        if (request.CanCancel && !await editor.ConfirmAndSaveAsync(request.DeadlineToken))
            request.Cancel();
    };
    application.LaunchReceived += editor.HandleLaunchAsync;
});
```

The configuration callback itself must not be `async void` or block the UI loop; asynchronous work
belongs in the typed lifecycle handlers. Duplicate/null registration fails early. A callback exception
fails startup, closes application-owned native windows through normal application teardown, and
propagates from `NeoApp.Run`. Bridge, capability, and navigation policies remain unchanged.
For custom application options, single-instance acquisition before window creation, multiple views,
or independently owned backend services, use explicit `NeoApplication` composition instead.

## Managed ordering

`NeoApplication.State` transitions through `Created`, `Starting`, `Ready`, `QuitRequested`, `ClosingWindows`, `Stopping`, and `Stopped`. `Run` enters `Ready` after its startup callback; attached hosts call `NotifyReady`. State changes and launch delivery are serialized by the application dispatcher.

`NeoWindow.CloseRequested` handlers run in registration order. `Cancel()` by any handler cancels an ordinary close; exceptions, renderer loss, and deadline expiration also cancel. The legacy synchronous `Closing` event runs first and now has an effective `Cancel` value. `NeoQuitRequest.CanCancel` and `Deadline` describe the particular request: normal AppKit termination is cancelable and deferred, while Windows' query notification is non-cancelable because Windows cannot wait for asynchronous policy. Final/forced phases ignore cancellation.

`RequestQuitAsync` coalesces reentrant/concurrent callers. `BeforeQuit` runs first. Windows are snapshotted child-before-owner and then either:

- preflighted completely before any destruction (the default), or
- negotiated and closed one at a time, where already closed windows remain closed if a later window cancels.

No new window is accepted once quit negotiation starts. A canceled normal negotiation returns to `Ready`. Approved windows are closed according to the selected preflight/partial policy; quit then raises `Stopping`, revokes bound RPC document sessions/capabilities, coordinates hosted-service stop, and exits. `ForceShutdown` is an urgent backend escape hatch. It is not renderer-accessible.

Directly closing an owner snapshots its current descendant tree, preflights that snapshot child-first, rejects descendants created under that tree during negotiation, and, only after every request is approved, explicitly closes descendants child-before-owner. This does not depend on incidental native owner-destruction behavior.

## Launch events and single instance

`NeoLaunchEvent` is immutable and bounded: at most 256 arguments/files/URLs, 4 KiB per value, 32 metadata entries, and no environment block. Paths must be absolute and URLs must be absolute non-file URIs. Initial process activation is queued once by managed core. macOS reopen/open-file/open-URL and Windows session end enter the same queue. Linux desktop activation varies by launcher and is normally supplied by the single-instance endpoint. Before `Ready`, at most `MaximumPendingLaunchEvents` (128 by default) are retained. Overflow returns `false`, raises `LaunchQueueOverflow`, and is never silent. Ready delivery is one event at a time in monotonic `Order`.

`NeoSingleInstance` derives an opaque endpoint from an explicit application identifier, OS user identity, and Windows session. It uses an OS-abandonable named mutex whose ownership remains on one dedicated thread plus a non-network `NamedPipe` restricted with `CurrentUserOnly`. Envelopes use an explicit version and request identity, a four-byte bounded frame, strict known fields, validated paths/URIs/counts, and a deterministic one-byte acknowledgement. The primary retains a bounded replay table so retry after a lost acknowledgement returns the original result without enqueueing twice. Explicit rejection is not retried; only transport unreachability is eligible for the bounded retry policy. Envelopes cannot encode environment data or capability grants. A stale mutex is recovered by the OS, and an unreachable primary never causes a competing normal instance to start silently.

Elevated and unelevated Windows processes may be separated by pipe ACL/integrity policy even for one user. Sandboxed macOS/Linux apps may require platform container/app-group routing supplied by a future plugin; core does not weaken the local-user boundary to cross a sandbox.

## Optional Generic Host package

The `NeoAstra.Hosting` namespace is included in `NeoAstra.dll`, which depends on `NeoAstra.Core` and `Microsoft.Extensions.Hosting.Abstractions`. Registration is static and NativeAOT-safe:

```csharp
Host.CreateApplicationBuilder(args)
    .UseNeoAstra(options => options.Application.ApplicationName = "Editor")
    .Services.AddNeoAstraRpc(builder => GeneratedRpcRegistration.Register(builder))
    .AddNeoAstraApplication<EditorApplication>();
```

Configuration under `NeoAstra` binds before the native UI thread starts. `NeoHostedService` starts the native loop, makes `INeoUiDispatcher` available, invokes `INeoHostedApplication.StartAsync` on that dispatcher, and only publishes host readiness after successful startup. Startup code may therefore await injected services that dispatch UI work without waiting on its own completion. Dispatchability is not application readiness: launch delivery and `Ready` still wait for the startup callback. Native-creation failures complete pending dispatcher and startup waits with the failure; startup failure or cancellation does not publish successful host readiness, even when the callback returns normally after handling cancellation. Callers that manage `IHost.StartAsync` directly must still stop/dispose their host after failed or canceled startup.

Services must use injectable `INeoUiDispatcher`; hosted services do not otherwise run on the UI thread. Startup callbacks must observe their cancellation token, preserve the UI context when accessing native objects after an await, and avoid blocking the native loop. Native categories are sent through `ILoggerFactory` without adding a provider. `NeoViewScopeFactory`, `NeoDocumentSessionScopeFactory`, and `NeoInvocationScopeFactory` make scope ownership explicit; callers dispose each returned `AsyncServiceScope` at the named boundary.

Host `ApplicationStopping` requests normal NeoAstra quit. A stop recorded before the native application exists is replayed when it becomes dispatchable; an already-completed host stop instead requests urgent shutdown. NeoAstra `Stopping` calls `StopApplication` exactly once. Native teardown then waits, within the configured quit deadline, for the host's authoritative `ApplicationStopped` signal, which is raised only after the host has invoked all hosted-service stop methods regardless of their registration position. The NeoAstra hosted service therefore does not block reverse-order service traversal in its own `StopAsync`. On the stopped signal, a canceled quit is escalated to urgent shutdown and the native UI thread is joined with bounded fallback waits. Startup failures remain observable through host startup tasks rather than being rethrown from the host's stopped callback. Unexpected native-loop failures after successful startup emit an `ILogger` error; logging-provider exceptions remain contained. An exit during unfinished startup also completes its pending readiness wait instead of leaving it suspended.

The native hosting tests exercise this path on Windows with staged native assets; unsupported hosts or missing assets are inconclusive, not passing native validation. The Generic Host integration still creates its own UI thread. macOS/AppKit main-thread ownership and Linux startup/shutdown behavior require separate target-host qualification; these Windows checks do not establish those guarantees. Use explicit native-main-loop ownership where the Generic Host path has not been qualified.

## Platform shutdown limitations

- **Windows:** `WM_QUERYENDSESSION` cannot wait for asynchronous save. NeoAstra reports one non-cancelable query phase with a two-second managed deadline and returns success to Windows immediately. The hook may perform best-effort flush work while the process remains alive, but Windows does not guarantee that time. `WM_ENDSESSION` is a separate exact-once final phase and forces teardown; an aborted end-session resets query delivery.
- **macOS:** normal AppKit termination is chained through the pre-existing application delegate and returns `NSTerminateLater`. NeoAstra supplies a cancelable request and a 30-second internal decision deadline, then calls `replyToApplicationShouldTerminate:`. AppKit or the OS may impose a shorter external deadline, so this is not a persistence guarantee. Other delegate selectors continue to the pre-existing delegate. Dock reopen and file/URL activation remain authoritative native events.
- **Linux:** window close is deferred through GTK. Desktop session-end and activation protocols differ by compositor/session manager and may not be observable; single-instance launch routing remains available.

Window activation is a best-effort request. In particular, Windows foreground-stealing policy can reject
`SetForegroundWindow` even though the target window is valid and has been shown; that policy outcome does not
turn the request into an invalid-state failure.

Applications should persist incrementally and treat every session-end hook as best effort.
