# Typed RPC and generated bindings

The `NeoAstra.Rpc` namespace in the `NeoAstra` package is the explicit, reflection-free RPC layer above the portable frontend transport. Low-level raw views remain available from `NeoAstra.Core`; a view must use a non-disabled bridge policy and an immutable `ViewLabel` before `NeoRpcViewBinding.Bind` succeeds.

## Backend contract

Declare only methods that are intended to cross the trust boundary:

```csharp
[assembly: NeoRpcJsonContext(typeof(ApplicationJsonContext))]

[NeoRpcService("documents", Version = 1)]
public sealed class DocumentsService
{
    [NeoRpcMethod("open", Permission = "documents:open")]
    public ValueTask<DocumentDto> OpenAsync(
        OpenDocumentRequest request,
        NeoRpcContext context,
        CancellationToken cancellationToken) => ...;
}

[JsonSerializable(typeof(OpenDocumentRequest))]
[JsonSerializable(typeof(DocumentDto))]
internal partial class ApplicationJsonContext : JsonSerializerContext;
```

The `NeoAstra` package carries `NeoAstra.Generator` as an analyzer automatically; applications must not add a generator package reference. Generated extensions support a direct singleton instance and an AOT-safe factory with `ApplicationSingleton`, `PerView`, `PerDocumentSession`, or `PerInvocation` lifetime. Owned work is canceled before scope disposal. Build without DI:

```csharp
var builder = new NeoRpcBuilder(options).AddDocumentsService(new DocumentsService());
var changed = builder.AddDocumentEventsChangedEvent();
var rpc = builder.Build();
await using var binding = NeoRpcViewBinding.Bind(rpc, view);
```

Factory-activated channel methods retain a service lease beyond the method's return. The host
disposes the asynchronous enumerator before releasing that lease on completion, failure, or close.
`PerInvocation` services are then disposed; document/view/application services remain alive until
their scope closes and all active leases drain. A rejected, canceled, or undeliverable channel result
also releases its lease without starting enumeration. Ordinary DTO and void results keep their
invocation-scoped cleanup. Sync, `Task`, and `ValueTask` channel factories use the same generated
activation path; no reflection or separate streaming service registration is required.

The RPC host owns returned channels. Direct users of `NeoRpcServiceActivator<TService>.InvokeAsync`
must dispose their returned `NeoRpcChannel<T>` after disposing its enumerator, including when they
abandon a channel without enumerating it. Channel disposal is idempotent and concurrent callers await
the same cleanup. Do not dispose a channel while its enumerator is still using the service.

The generator requires an explicit `NeoRpcJsonContext` because Roslyn generators cannot feed generated attributes into the built-in `System.Text.Json` generator during the same compiler pass. It verifies that every request, result, event, and channel root has a matching `[JsonSerializable]` declaration. Missing metadata is a compile error (`NEORPC009`), not a runtime reflection fallback. Other diagnostics reject invalid/duplicate names, collisions in generated TypeScript and C# member names, overload collisions, invalid framework parameters, unsafe types, cycles, non-string dictionary keys, duplicate or hidden JSON properties, indexers, inaccessible serialization constructors or required members, contradictory omission/nullability metadata, and incompletely configured 64-bit integers.

The package supplies the compiler-visible properties automatically and defaults the TypeScript binding, manifest, and schema to `obj/neoastra`. It copies generated artifacts to the build and publish output under `neoastra/`, like other build content, without adding generated files to the project tree. Set `NeoRpcIntermediateOutputPath` or an individual `NeoRpc*Output` property only for a nonstandard layout. Frontends in the conventional `frontend` directory can import the intermediate TypeScript binding through their bundler (the application templates configure the `#neoastra` alias). For a static `frontend/index.html` project, the SDK emits the JavaScript binding under `obj`, imports the staged client automatically, and materializes it as `neoastra.js`; application code can import `./neoastra.js`. Other plain JavaScript layouts can set `NeoRpcJavaScriptOutput` and, when necessary, `NeoRpcJavaScriptImport`. Outputs contain no absolute paths or timestamps. `NeoRpcBaselineManifest` enables compatibility warnings. The generated manifest SHA-256 is suitable for `NeoRpcOptions.ContractHash`.
Generated TypeScript and JavaScript calls and subscriptions send that hash automatically. Application code should use those generated methods rather than copying a hash into a low-level `invoke` call. A host configured with a non-empty hash rejects missing or stale bindings with `protocol_mismatch` before authorization or application dispatch; the returned error directs developers to generated bindings or regeneration instead of failing without an actionable explanation.

## Runtime behavior

The host snapshots trusted view/session identity, source origin, main-frame state, whole-view trust, protocol features, service provider, and contract hash. View/window references are weak and cannot retain disposed UI. Renderer fields cannot replace context identity. Authorization receives `NeoRpcAuthorizationRequest` before application dispatch. Every operation must declare a permission and a missing service denies by default; configure the resolved capability manifest as described in [capabilities and security](capabilities-and-security.md).

`NeoRpcOptions` bounds global/per-session concurrency, retained request IDs, frame depth/bytes, event queues/bytes, subscriptions, unacknowledged channel items, channels, resources, and deadlines. The global invocation limit must exceed the per-session limit; immediate admission reserves capacity for another view instead of letting one hot view occupy every global slot. IDs are printable non-empty ASCII. Request and pending-subscription IDs occupy bounded session slots; cancellation, unsubscribe, or session close that wins the atomic terminal transition cannot be reversed by a late handler or authorization result. Exactly one terminal state atomically wins cancellation/result races: a committed result defeats late cancellation, while cancellation, timeout, or session close that commits first defeats a handler result even when that handler ignores its token. Navigation installs the newly negotiated document session before starting teardown of the prior session, so prior application cleanup cannot block new-document frames; binding disposal still awaits all tracked teardown. Renderer failure, view disposal, binding disposal, and host disposal cancel all calls, subscriptions, channels, and resources. Host lifecycle state and session snapshots are taken under its lifecycle lock, but cancellation occurs after releasing that lock so application cancellation callbacks never run beneath it.

Request deserialization, application execution, and response serialization are separate phases. Malformed or JSON `null` request DTOs fail with `invalid_request` before application code; an application-thrown serialization exception follows normal application mapping/redaction; an unserializable result fails with `serialization_failed`. Errors use bounded lowercase colon-separated identifiers, bounded control-free client messages, and optional bounded printable-ASCII correlation IDs. `NeoRpcException` is an explicit safe application error; `INeoRpcErrorMapper` handles other known failures, but malformed mapper output is rejected. Release defaults redact exception types, stack traces, paths, and nested messages. `IncludeDevelopmentErrorDetails` is an explicit bounded development-only switch.

Events are ordered per subscription and use declaration-owned `DropOldest`, `DropNewest`, `Coalesce`, or `Fail` overflow. Bounded JSON channels use monotonic sequence numbers and acknowledgements to bound unacknowledged transport items. The frontend acknowledges admission into its buffer, not consumption by the application iterator: a slow reader can still exhaust its bounded buffer and receive `too_many_requests`. Applications that observe durable work need a snapshot/resynchronization policy rather than treating the channel as a lossless event log. Resources are opaque session-owned handles closed by `resource_close` or teardown. These channel/resource frames reserve room for a future scalable binary extension.

Channel capacity is reserved before the result ID is sent; enumeration starts only after successful
delivery. A `channel_close` control frame accepts cancellation without waiting for the pump inside
the receive callback, so transports may deliver close reentrantly during an outbound send. The
session continues tracking the channel until cleanup finishes, and session/host disposal awaits it.
Cancellation is cooperative: after a five-second drain warning, a noncooperative enumerator keeps
teardown pending and its service alive rather than being force-disposed while in use. Application
iterators, disposers, and transport callbacks must cooperate with cancellation and avoid blocking;
the warning is not a guaranteed shutdown deadline. Service-lease cleanup failures are contained and
diagnosed as `channel_cleanup_failed` (and reported as a channel error when delivery is possible).

UI commands use the originating application's `NeoDispatcher`; background commands never invoke application callbacks while native/core locks are held. The portable integration is managed and shared across WebView2, WKWebView, and WebKitGTK.

## Frontend

Generated TypeScript imports only public APIs from `@neoastra/client`:

```ts
const controller = new AbortController();
const document = await documents.open({ id: "readme" }, { signal: controller.signal });
const unsubscribe = await documents.onChanged(value => render(value));
```

`NeoRpcClient` supplies `invoke`, `invokeChannel`, `subscribe`, ordered channels, typed stable `NeoRpcError`, `AbortSignal` cancellation, resource close, and connection teardown. `NeoRpcCallOptions.timeoutMilliseconds` bounds invocation completion and pending subscription acknowledgement; a subscription timeout atomically removes pending state, sends `unsubscribe`, and rejects with `timeout`. Generated channel calls use `invokeChannel`, which claims the channel state pre-created by the result frame. Items are retained across that result/claim window, frontend buffering is bounded (overflow fails and closes the channel rather than dropping), and channel or connection errors reject waiting iterators. An already-aborted signal sends no frame. `@neoastra/client/testing` provides `createMockRpcHarness` with handlers, errors, cancellation, events, navigation/close behavior, and outbound-frame assertions without a DOM.

For `invokeChannel`, the signal remains effective after opening: aborting discards buffered items,
rejects pending/future reads with `operation_canceled`, and sends one `channel_close`. The timeout
bounds only the opening invocation, not the channel's duration. Lower-level callers can pass the
same signal to `client.channel(id, signal)`. Iterator `return()` (including a `for await` break)
discards unread items and closes observation; terminal frames, overflow, return, and connection loss
remove abort listeners. A completed channel wins a later abort. Canceling observation does not
implicitly abort an application's durable background job; expose an explicit authorized command for
that separate action. Subscription signals still govern only the pending handshake; retain and call
the returned unsubscribe function for an active subscription.

## Wire formats

`Guid` uses the standard hyphenated string form. `DateTime`/`DateTimeOffset` use invariant ISO 8601 strings and `TimeSpan` uses the invariant `c` form produced by the configured `System.Text.Json` context. Enums use string names and require `[JsonSourceGenerationOptions(UseStringEnumConverter = true)]` on the selected context. `long` properties require `[NeoRpcInt64(NeoRpcInt64Policy.String)]` plus `[JsonConverter(typeof(NeoRpcInt64JsonConverter))]`; `ulong` uses `NeoRpcUInt64JsonConverter`, and nullable properties use the corresponding `NeoRpcNullableInt64JsonConverter`/`NeoRpcNullableUInt64JsonConverter`. These converters write and accept only canonical invariant decimal JSON strings, matching generated TypeScript `string` and schema output. There is no `bigint` wire mode. JSON byte arrays are base64 JSON strings and should remain small and bounded by frame limits; larger binary values should use resources.

## Platform status

The in-memory runtime, generator, frontend package, and NativeAOT fixture are platform-independent. `NeoRpcViewBinding` uses the common authenticated transport on every backend. On WebKitGTK 6.0, authenticated message origin remains unavailable; RPC reports `SourceOrigin == null` and never substitutes the mutable current URL. Consequently Linux production views that enable RPC must explicitly use whole-view trust with controlled local content. Runtime browser integration on macOS and Linux must be exercised by their CI runners; a Windows host cannot verify WKWebView/WebKitGTK runtime behavior.
