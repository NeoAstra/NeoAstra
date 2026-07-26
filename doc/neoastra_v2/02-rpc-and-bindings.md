# Step 2 — Typed RPC Runtime and Generated Bindings

**Parent:** [`neoastra_v2_specs.md`](../neoastra_v2_specs.md)
**Depends on:** [Step 1](01-frontend-transport.md)
**Outcome:** Explicit C# services become generated, typed, cancelable frontend APIs without runtime reflection.

## 1. Scope

This step defines `NeoAstra.Rpc`, `NeoAstra.Rpc.Generator`, generated TypeScript contracts, and the corresponding `@neoastra/client` RPC primitives. Basic invoke/result/error/cancel and typed events are REQUIRED for the first usable milestone. Channel and resource frame shapes MUST be reserved now; their full scalable implementation may be completed in Step 9, but no incompatible protocol redesign may be required.

## 2. C# programming model

The authoritative contract is explicit C# source. An application SHOULD declare services as follows:

```csharp
[NeoRpcService("documents", Version = 1)]
public sealed class DocumentsService
{
    [NeoRpcMethod("open", Permission = "documents:open")]
    public ValueTask<DocumentDto> OpenAsync(
        OpenDocumentRequest request,
        NeoRpcContext context,
        CancellationToken cancellationToken);
}
```

Required public concepts:

```csharp
public sealed class NeoRpcOptions;
public sealed class NeoRpcBuilder;
public readonly struct NeoRpcContext;
public interface INeoRpcServiceRegistration;
public sealed class NeoRpcException : Exception;
public readonly record struct NeoRpcError(string Code, string Message, string? CorrelationId);
public interface INeoRpcErrorMapper;
public interface INeoRpcAuthorizationService;
```

Exact API shapes MAY be adjusted for .NET guidelines, but the following rules are normative:

- every service and method has an explicit stable wire name;
- service instances are registered explicitly or through generated registration extensions;
- overloads sharing a wire name are forbidden;
- arbitrary public methods and inherited methods are not exported;
- service construction MAY use DI but RPC does not require DI;
- one request DTO is preferred over many positional parameters;
- `NeoRpcContext` and `CancellationToken` are framework parameters and never serialized;
- methods return `void`, a DTO, `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, or approved channel/resource abstractions;
- synchronous methods MAY be supported only if dispatch behavior and exception containment match async methods;
- `async void`, pointer/ref-like values, delegates, reflection types, arbitrary object graphs, and open generics are forbidden.

## 3. Immutable invocation context

`NeoRpcContext` MUST be created from trusted host state when the message arrives and contain:

- originating view and owned window references, when still alive;
- immutable view label;
- opaque document-session ID;
- authenticated source origin and main-frame indicator when provided by the backend;
- explicit `WholeViewTrust` indicator;
- correlation/trace ID;
- request cancellation token;
- optional per-view or per-session service provider scope;
- negotiated protocol/features and application contract hash.

The context MUST NOT expose renderer-supplied replacements for these values. Authorization MUST NOT query the mutable current view URL as sender proof. Context references MUST not keep a disposed view/session alive after request completion.

## 4. Wire protocol

Every frame uses the Step 1 discriminator and negotiated document session. IDs are opaque non-empty ASCII strings with bounded length and uniqueness within a document session.

### 4.1 Invoke

```json
{
  "neoastra": 1,
  "kind": "invoke",
  "id": "01J...",
  "command": "documents.open",
  "args": { "id": "readme" },
  "trace": { "parent": "optional-bounded-value" }
}
```

The host validates frame/session, command length/syntax, request-ID uniqueness, command registration, permission, scope, JSON shape, and concurrency before dispatch. Unknown commands return `command_not_found` without invoking application code.

### 4.2 Result

Success:

```json
{
  "neoastra": 1,
  "kind": "result",
  "id": "01J...",
  "ok": true,
  "value": { "title": "Notes" }
}
```

Failure:

```json
{
  "neoastra": 1,
  "kind": "result",
  "id": "01J...",
  "ok": false,
  "error": {
    "code": "documents:not_found",
    "message": "The document was not found.",
    "correlationId": "...",
    "retryable": false
  }
}
```

Exactly one terminal result is sent for every accepted invoke unless the connection is already closed. Duplicate completion is contained and logged. Serialization/send failure closes or fails the invocation deterministically; it MUST NOT result in a second application invocation.

### 4.3 Cancel

```json
{ "neoastra": 1, "kind": "cancel", "id": "01J..." }
```

Cancel is idempotent. It cancels the request-specific token but cannot guarantee application rollback. If cancellation wins before a result is committed, the terminal error is `operation_canceled`. A result already committed wins over a late cancel. Unknown/completed IDs are ignored with bounded diagnostic accounting.

The JavaScript API maps `AbortSignal` to cancel:

```ts
const result = await documents.open(request, { signal });
```

A signal already aborted MUST reject without sending invoke. Navigation, session close, view disposal, and application shutdown cancel all outstanding invocations.

The shared frontend call timeout also bounds pending subscription acknowledgement. If that timeout wins, the client MUST remove the pending subscription exactly once, send an idempotent `unsubscribe`, and reject with `timeout`; a late `subscribed` frame cannot restore client state.

### 4.4 Events

Subscriptions are explicit:

```json
{ "neoastra": 1, "kind": "subscribe", "id": "sub-1", "event": "documents.changed" }
{ "neoastra": 1, "kind": "subscribed", "id": "sub-1" }
{ "neoastra": 1, "kind": "event", "subscription": "sub-1", "sequence": 1, "value": {} }
{ "neoastra": 1, "kind": "unsubscribe", "id": "sub-1" }
```

Events MUST be ordered per subscription. Pending authorization and active subscriptions share the same bounded ID/slot lifecycle; an unsubscribe or session teardown that wins its terminal transition cannot be reversed by a late authorization result. Each subscription has bounded buffering and a declared overflow behavior (`drop_oldest`, `drop_newest`, `coalesce`, or `fail`) selected by the backend event declaration, not by an untrusted caller. Session teardown unsubscribes automatically. Global broadcast APIs MUST resolve recipients against capability grants before enqueueing.

### 4.5 Channels and resources

Reserve frame kinds `channel_item`, `channel_ack`, `channel_complete`, `channel_error`, `channel_close`, and `resource_close`, with session-owned opaque IDs and monotonic sequence numbers. Step 2 MAY implement a bounded JSON `IAsyncEnumerable<T>` channel. Resource/binary scaling requirements are completed by Step 9.

## 5. Error contract

Exceptions MUST NOT be serialized directly. The runtime maps failures in this order:

1. protocol/validation errors;
2. authorization errors from Step 3;
3. explicit application `NeoRpcException` or result-error abstraction;
4. configured `INeoRpcErrorMapper` mappings;
5. cancellation/timeout;
6. safe `internal_error` fallback.

Stable framework codes include:

- `invalid_request`, `command_not_found`, `duplicate_request`;
- `permission_denied`, `scope_denied`;
- `payload_too_large`, `too_many_requests`, `timeout`;
- `operation_canceled`, `connection_closed`, `protocol_mismatch`;
- `serialization_failed`, `internal_error`.

Request deserialization (including rejection of a JSON `null` required DTO), application execution, and response serialization are distinct phases. `invalid_request` applies only to malformed requests, application exceptions use the mapping order above even when their CLR type is serialization-related, and a failure to serialize a successful application result uses `serialization_failed`.

Release responses MUST omit exception type, stack, paths, connection strings, and nested exception text. The backend logs full diagnostic data according to application logging policy and returns a correlation ID. Development details require an explicit development profile and MUST remain bounded.

## 6. Serialization and supported contract types

The generator MUST emit `System.Text.Json` source-generation metadata for every reachable request, response, event, channel, and structured error type. Runtime reflection fallback is forbidden in the standard path.

Serialization viability is direction-sensitive across the complete reachable graph: request DTOs require deserialization construction/setter capability, while response, event, and channel DTOs require public serialization getters. A type reachable in both directions MUST satisfy both sets of requirements; inherited and nested members retain the direction of the root that reaches them.

Supported initial types:

- Boolean, string, numeric primitives with explicitly supported ranges;
- `Guid`, `DateTime`, `DateTimeOffset`, and `TimeSpan` with documented invariant wire formats;
- nullable value/reference types;
- enums using the Step 2 string-name policy, with `UseStringEnumConverter = true` on the selected source-generated context;
- arrays, `List<T>`, `IReadOnlyList<T>`, and dictionaries with string keys;
- records/classes/struct DTOs composed only of supported types;
- explicitly annotated discriminated unions using a stable discriminator;
- byte data only up to a low documented JSON threshold; larger binary data uses resources.

Unsupported or ambiguous constructs MUST produce compile-time diagnostics, including polymorphism without an explicit discriminator, object-typed members, unsupported dictionary keys, cycles, duplicated or hidden JSON names, indexers and non-serializable accessors, non-public required constructors/members, omission/nullability contradictions, collisions among generated TypeScript service/event members or C# event registration methods, and platform-width integers with unclear TypeScript range. Step 2 supports only canonical decimal-string `long`/`ulong`, declared with `[NeoRpcInt64(NeoRpcInt64Policy.String)]` and the matching signed/unsigned NeoAstra JSON converter; generated `bigint` adapters are not part of this frozen policy.

Nullability maps as follows:

- non-nullable required C# property -> required TypeScript property;
- nullable required value -> required property with `| null` unless omission is explicitly configured;
- optional/defaulted value -> optional property only when the wire contract permits omission;
- generator diagnostics flag impossible null/default combinations.

## 7. Source generator

### 7.1 Inputs and outputs

Inputs are explicit RPC attributes/registration declarations, DTO source, generator options, permission metadata, and an optional prior contract manifest for compatibility diagnostics.

Outputs:

- strongly typed C# registration code;
- allocation-conscious command lookup and deserialization;
- serializer context metadata;
- application contract manifest/hash;
- deterministic TypeScript DTOs, service methods, event helpers, errors, and imports;
- JSON Schema fragments consumed by Step 3 tooling;
- diagnostics with stable IDs and actionable locations.

TypeScript output MAY be written through an MSBuild target driven by generator metadata rather than directly by the source generator, because Roslyn generators do not own arbitrary project files reliably. The pipeline MUST remain deterministic and incremental.

### 7.2 Diagnostics

At minimum diagnose:

- missing/invalid/duplicate service or method wire names;
- overload collision and command collision across assemblies;
- unsupported parameter/return/member type;
- missing serializer metadata;
- command permission missing or malformed once Step 3 is enabled;
- unstable inferred name where an explicit name is required;
- conflicting TypeScript symbol/file names;
- accidental contract deletion/change against an optional baseline manifest;
- inaccessible service construction path;
- invalid cancellation/context parameter placement or duplication;
- indexers, inaccessible serializer constructors/accessors, hidden JSON properties, and contradictory null/default/omission metadata;
- collisions after generated TypeScript method/event normalization or generated C# event-registration naming.

Warnings about likely breaking contract changes MUST explain how to assign a new wire version or retain an alias. The generator MUST NOT silently generate a different command.

## 8. Runtime dispatch and threading

- Parsing and command lookup MAY occur off the UI thread if no UI-owned object is accessed.
- Authorization and service dispatch use a documented scheduler. A service MAY declare UI-thread dispatch; otherwise it SHOULD execute without blocking the UI thread.
- UI-bound commands use the existing NeoAstra dispatcher and remain cancellation-aware while queued.
- No application callback is invoked while core/native locks are held.
- Host shutdown snapshots lifecycle state under its lock, then signals cancellation after releasing that lock. A new document session is installed before prior-document teardown is awaited, while all prior teardown remains tracked by binding disposal.
- Per-view and global concurrency limits are enforced before service dispatch.
- Fairness prevents one view from starving others.
- A timeout is policy-controlled and distinct from caller cancellation in logs, even if both map to canceled task semantics internally.
- Request state is removed exactly once after terminal completion and cannot be reused by late frames.

## 9. Frontend generated API

Generated use SHALL look like normal TypeScript:

```ts
import { documents } from "./generated/neoastra";

const document = await documents.open({ id: selectedId }, { signal });
const unsubscribe = await documents.onChanged(value => render(value));
```

Requirements:

- methods return `Promise<T>` and accept a final optional call-options object;
- DTOs preserve C# nullability and documented numeric/date policy;
- framework and application errors are typed by stable code, not concrete CLR exception;
- generated files contain source/version metadata and are deterministic across paths/machines;
- imports target `@neoastra/client`, never private bridge globals;
- generated APIs do not include commands unavailable solely because the current capability file omits a grant—the API contract and runtime authority remain separate;
- comments/XML docs SHOULD flow into TypeScript documentation without injecting unsafe markup.

## 10. Registration and DI

RPC MUST work with direct registration:

```csharp
var rpc = new NeoRpcBuilder()
    .AddDocumentsService(new DocumentsService(...))
    .Build();
```

`NeoAstra.Hosting` MAY add generated extensions such as `services.AddNeoAstraRpc().AddDocumentsService()`. Service lifetimes must be explicit:

- application singleton;
- per-view scope;
- per-document-session scope;
- per-invocation transient.

Disposing a view/session scope cancels its requests before disposing scoped services. Scoped services MUST NOT be resolved from a disposed provider. Singleton services must not accidentally retain a view through `NeoRpcContext`.

## 11. Implementation order

- [x] Freeze protocol frame kinds, ID rules, error codes, version negotiation, and race semantics.
- [x] Implement invoke/result/cancel state machines with in-memory transport tests.
- [x] Define C# attributes, builder, context, service lifetimes, and direct registration.
- [x] Implement generator command discovery, diagnostics, dispatcher, and serializer context.
- [x] Implement deterministic contract manifest and TypeScript emission pipeline.
- [x] Add typed event subscriptions and bounded queues.
- [x] Reserve and prototype channels/resource ownership.
- [x] Integrate UI-thread dispatch, timeouts, navigation/disposal cancellation, and logging correlation.
- [x] Add `@neoastra/client` invoke/event APIs and testing mocks.
- [x] Validate JIT, trimming, and NativeAOT in a multi-view fixture.

## 12. Verification

Tests MUST cover successful sync/async/value/void returns; malformed DTOs; nullability; every supported type; generator diagnostics; deterministic snapshots; duplicate IDs; unknown commands; cancellation races; timeout; service exceptions; serialization failure; connection loss; navigation; renderer crash; scope disposal; UI-thread dispatch; concurrency exhaustion; event ordering/overflow/unsubscribe; contract mismatch; JIT/NativeAOT parity; and release error redaction.

A benchmark suite SHOULD track handshake, small invoke round-trip, serialization, generated dispatch lookup, event fan-out, cancellation, and allocations. Performance work MUST not weaken validation or bounded queues.

## 13. Exit criteria

A C# contract change produces compiler/generator feedback and deterministic TypeScript. Vanilla/React/Vue code invokes a typed method, receives a typed event, and propagates cancellation to C#. Unknown or malformed commands never reach application code. The complete standard path passes NativeAOT with no reflection fallback and closes all request/subscription state on navigation and disposal.

## 14. Implementation status (2026-07-26)

All checklist items above have corresponding production source, bounded runtime paths, generated artifacts,
tests/fixtures, documentation, and CI wiring. The implementation is split between `NeoAstra.Rpc`,
`NeoAstra.Rpc.Generator`, `@neoastra/client`, and the common Step 1 view binding. Checked status records
implemented source rather than claiming that every platform was exercised on one machine.

The in-memory .NET/runtime, generator, TypeScript, deterministic artifact, and NativeAOT fixture paths are
portable. Browser-backed execution remains conditional on the backend runtime installed on each CI host.
A Windows development host cannot run WKWebView or WebKitGTK integration. WebKitGTK 4.1 does not provide
authenticated sender-origin data, so Linux reports an unknown source origin and requires an
application-controlled whole-view trust policy; NeoAstra never substitutes the mutable current URL.
See [`../rpc-and-bindings.md`](../rpc-and-bindings.md) for setup, security, wire-format, teardown, mocking,
and migration details.
