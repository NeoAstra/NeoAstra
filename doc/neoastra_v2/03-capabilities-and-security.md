# Step 3 — Capabilities, Permissions, Scopes, and Security Profiles

**Parent:** [`neoastra_v2_specs.md`](../neoastra_v2_specs.md)
**Depends on:** [Step 1](01-frontend-transport.md), [Step 2](02-rpc-and-bindings.md)
**Outcome:** Controlled local application RPC is trusted by default; explicit renderer authority remains available for restricted views, plugins/native services, and validated scopes.

## 1. Scope

This step adds optional command-level authorization above v1 bridge admission. Browser permissions such as camera access remain separate. Registering application-owned RPC makes it callable from the controlled local app by default. Referencing a plugin or generating a frontend API MUST NOT implicitly grant restricted native authority.

## 2. Security principles

1. **Trust the controlled application, restrict explicit boundaries.** Registered application RPC is trusted in controlled local views; restricted views and plugin/native operations require grants.
2. **Authenticate before authorize.** Invalid transport/session frames never enter capability evaluation.
3. **Use trusted identity.** Match immutable view labels and authenticated origin metadata, never titles or current URLs.
4. **Constrain dangerous values.** A command grant does not imply unrestricted paths, URLs, executables, formats, or persistence.
5. **Fail closed.** Unknown permission versions, malformed scopes, missing platform adapters, and unavailable origin proof deny access.
6. **Keep backend authority explicit.** Trusted C# code may call registered services directly; capability files control renderer access only.
7. **Revoke with lifetime.** Navigation, renderer replacement, view disposal, plugin unload, and app shutdown revoke session-owned authority/resources.
8. **Audit decisions, not secrets.** Logs identify view/permission/decision/correlation while redacting arguments and sensitive scope values as configured.

## 3. Configuration model

Applications MAY declare versioned capability documents for restricted views or advanced security boundaries. A project MAY split them into files, but the resolved model is equivalent to:

```json
{
  "$schema": "neoastra-capabilities-v1.schema.json",
  "version": 1,
  "capabilities": [
    {
      "id": "main-window",
      "views": ["main"],
      "platforms": ["windows", "macos", "linux"],
      "origins": ["app://acme"],
      "permissions": [
        "documents:open",
        {
          "id": "opener:open-url",
          "scope": { "schemes": ["https"], "hosts": ["docs.acme.test"] }
        }
      ]
    }
  ]
}
```

The exact schema URI is not final, but these semantics are REQUIRED:

- top-level schema version;
- unique stable capability ID;
- one or more exact view labels or explicit reviewed patterns;
- optional platform selector;
- optional authenticated-origin constraints;
- permission identifiers and versioned scope data;
- optional development-only marker that cannot enter release output silently;
- optional deny entries only if precedence is unambiguous and static diagnostics explain overlaps.

Release build MUST validate all capability files against generated schemas. Unknown permission IDs, unsupported scope fields, duplicate IDs, impossible origin requirements on a target, and broadening overlaps are errors unless an explicit documented override exists.

## 4. Permission declarations

Every operation inside an explicit capability boundary MUST declare one permission ID. Ordinary application-owned RPC MAY omit it. Plugins MAY define permission sets that expand to individual permissions, but diagnostics and generated resolved manifests MUST show the expansion.

A declaration contains:

- stable ID, for example `dialogs:open-file`;
- semantic version or schema version;
- command(s) covered;
- risk classification (`low`, `sensitive`, `high`);
- whether scope is required;
- JSON Schema for scope;
- platform availability;
- safe default timeout/concurrency policy;
- audit redaction policy;
- documentation and examples.

Permission IDs SHOULD use `<plugin-or-app>:<operation>`. Wildcards are forbidden in release capability grants unless a separately reviewed permission set resolves to a static list at build time. Adding a new command to an existing permission set is a security-relevant compatibility change and MUST be surfaced in package/build diagnostics.

## 5. Scope system

### 5.1 General rules

Scope parsing occurs once at startup/build-generated registration when possible. Runtime authorization receives immutable validated scope objects, not arbitrary JSON. Scope schemas MUST set explicit additional-property behavior and bounds on arrays/strings/patterns.

A scope validator MUST:

- canonicalize before matching;
- reject malformed, relative, ambiguous, device, alternate-data-stream, traversal, symlink/reparse escape, Unicode-confusable, or unsupported values as relevant;
- account for operating-system case sensitivity and normalization;
- avoid TOCTOU by passing a validated capability/handle to execution where practical;
- return only allow/deny and safe reason code to the renderer;
- log detailed diagnostic data under application policy.

### 5.2 Required scope families

| Family | Minimum semantics |
| --- | --- |
| Filesystem | Root capabilities, read/write/create/delete distinctions, symlink/reparse policy, canonical relative paths, no ambient current directory |
| URL/opener | Exact schemes, normalized hosts/ports, optional path prefixes, denial of credentials and dangerous custom schemes by default |
| Process/sidecar | Predeclared executable identity, fixed or schema-validated arguments, working directory/environment policy, no arbitrary shell string |
| Clipboard | Explicit text/HTML/image/file formats and read/write distinction |
| Notifications | App identity, action categories, persistence/urgency policy, bounded payload |
| Dialogs | Allowed dialog kinds, initial-location tokens rather than arbitrary paths where possible, filter constraints |
| Network | Allowed schemes/hosts/ports/methods, redirect policy, headers, body/response limits |
| Persistence | Whether a browser/native permission or grant can be remembered and for which identity/duration |

Scopes do not replace application-domain authorization. A filesystem scope can permit a root while the service still checks which project/user may access a file.

## 6. Capability matching

For an accepted invocation, the runtime SHALL:

1. obtain the trusted immutable view label, document session, platform, origin proof (possibly absent), and whole-view-trust flag;
2. allow registered application RPC when the host and operation do not require an explicit capability;
3. otherwise select capability records whose view selector and platform match;
4. if a capability declares origins, require authenticated origin equality after normalized URI comparison; unknown origin does not match;
5. union only explicitly granted permissions/scopes according to documented merge rules;
6. reject the restricted command when no matching permission exists;
7. validate command arguments against all relevant scopes using deny-on-error semantics;
8. attach the resulting authorization decision to `NeoRpcContext` for application checks;
9. execute only after the decision is final.

Origin matching MUST be exact origin (`scheme`, host, effective port) unless a permission's reviewed schema explicitly supports narrower/broader forms. Paths, query strings, fragments, redirects, and the top-level view URI are not origin authentication.

### 6.1 Merge rules

The initial implementation SHOULD avoid runtime deny rules. Multiple grants for the same view/permission may union scopes only when the permission definition declares union safe. Otherwise duplicate grants are a build error. If deny rules are introduced, deny MUST override allow and all precedence MUST be resolved into a generated manifest before runtime.

## 7. Linux policy

WebKitGTK 6.0 does not provide trustworthy script-message source origin. Therefore:

- `origins` constraints never match a Linux bridge message;
- the runtime MUST NOT fill origin from top-level navigation, referrer, custom-scheme request metadata, or JavaScript arguments;
- a Linux bridge-enabled view requires explicit whole-view trust;
- production templates MUST load controlled local content, deny remote navigation/subframes, and use restrictive CSP;
- remote or mutable content MUST use a bridge-disabled view or system browser;
- diagnostics MUST identify `wholeViewTrust: true` and `originAuthenticated: false`;
- tests MUST prove that spoofed origin fields in payloads have no effect.

## 8. Security profiles

Tooling SHALL provide named defaults without obscuring resolved settings.

### 8.1 Production local-app profile

- controlled application-scheme assets only;
- bridge/RPC enabled only for controlled application content; registered application RPC trusted by default;
- top-level navigation restricted to the application origin;
- unexpected popup/new-window denied;
- external HTTP(S) links denied by default and handled by a scoped system-browser opener only after explicit exact-origin opt-in and a user action;
- no remote script by default and restrictive CSP/security headers;
- DevTools and detailed RPC errors disabled;
- frame/invocation/event/channel/resource limits enforced;
- all session state revoked on navigation/disposal;
- capability manifest embedded and hashed for diagnostics.

### 8.2 Development profile

May allow loopback dev origins, DevTools, source maps, and detailed diagnostics. It MUST:

- bind dev servers to loopback by default;
- distinguish configured exact dev origin from arbitrary localhost ports;
- display/log that development authority is active;
- never flow into Release output accidentally;
- preserve command grants/scopes unless a separately marked development grant is present;
- reject remote-network dev URLs unless explicitly approved.

### 8.3 Remote-content profile

Defaults to bridge-disabled, no RPC capability, denied popup, and system-browser handling for external navigation. Enabling any renderer authority for remote content requires explicit per-origin grants on origin-authenticating backends and is unsupported on the WebKitGTK configuration described above.

## 9. Runtime limits

Security options MUST include safe defaults and hard maxima for:

- frame bytes and JSON depth;
- invocations per view and application;
- command-specific concurrency and timeout;
- request rate/burst;
- subscriptions and buffered event count/bytes;
- channels, channel item size, and buffered items/bytes;
- resources per document/view/application and total resource bytes;
- diagnostic detail and retention;
- capability/scope document size and complexity.

Limit errors use stable codes. Repeated abuse MAY close the document session, but one view MUST NOT crash or starve the application. Limits and current usage SHOULD be present in redacted diagnostic snapshots.

## 10. Plugin integration

A plugin registers permissions and scope schemas statically. Registration alone grants nothing. Build tooling emits a resolved catalog showing:

- plugin ID/version and minimum NeoAstra version;
- all commands and permission sets;
- scope schemas and risk classifications;
- platform support;
- application grants and affected view labels;
- conflicts, deprecated permissions, and unresolved entries.

High-risk plugins MUST require explicit individual permissions/scopes rather than a broad default set. Backend-only plugin use remains possible with no renderer commands registered.

## 11. Diagnostics and privacy

Authorization log events include timestamp, stable decision code, view label, permission/command, authenticated-origin status (not a fabricated origin), platform, document session suffix/hash, and correlation ID. They SHOULD NOT include complete user paths, URLs with query/fragment, clipboard data, command body, secrets, or credentials.

Required decision codes include `allowed`, `no_matching_capability`, `permission_missing`, `origin_unavailable`, `origin_mismatch`, `platform_mismatch`, `scope_invalid`, `scope_denied`, `session_stale`, and `limit_exceeded`.

The diagnostic snapshot lists resolved permission IDs and scope summaries suitable for support use, with sensitive exact values redacted by permission-defined policy.

## 12. Threat model requirements

The implementation and security review MUST cover:

- XSS in controlled local content;
- compromised remote content/subframes;
- payload spoofing of origin/view/session/permission;
- stale frames after navigation;
- capability broadening through overlapping files/permission sets;
- path traversal, symlink/reparse and TOCTOU;
- unsafe URL schemes/redirects/credentials;
- arbitrary process/shell argument injection;
- resource exhaustion and cancellation races;
- plugin package update adding commands to existing grants;
- development configuration leaking into release;
- diagnostic/error leakage;
- updater/installer authority separately in Step 7.

No capability design can protect privileged commands from XSS executing in the same fully trusted document. Documentation MUST state this and emphasize CSP, immutable assets, dependency hygiene, navigation denial, and least privilege.

## 13. Implementation order

- [x] Define permission IDs, declarations, capability schema, selectors, and build-time resolved manifest.
- [x] Implement immutable view labels/document sessions in all creation paths.
- [x] Add permission registration and fail-closed command dispatch hook.
- [x] Implement origin/platform/view matching and Linux-specific denial behavior.
- [x] Implement scope schema generation, parsing, canonicalization helpers, and permission-specific validators.
- [x] Add security profiles and resolved-policy diagnostics.
- [x] Integrate plugin permission catalogs without implicit grants.
- [x] Add rate/concurrency/resource limits and abuse handling.
- [x] Complete threat model, security review, and migration docs.

## 14. Verification

Automated tests MUST prove denial for wrong view, missing permission, wrong authenticated origin, unknown origin, platform mismatch, malformed scope, out-of-scope path/URL/process arguments, expired document session, post-navigation invocation, disposed view, oversized/deep payload, concurrency/rate/resource exhaustion, unknown permission/schema fields, capability overlap, dev-only grants in release, and plugin commands with no grant.

Positive tests cover exact origin matching, least-privilege scopes, two views with different authority, backend-only plugin use, capability manifest reproducibility, and application-level authorization after framework authorization.

Native/browser security tests MUST prove source metadata cannot be supplied by JavaScript. Linux tests MUST prove origin remains absent and payload/top-level URL spoofing is ineffective.

## 15. Exit criteria

Registered application commands are trusted in controlled local views by default; plugin commands and explicitly restricted operations are default-denied. Build-time schemas autocomplete and validate grants. Runtime authorization is bound to trusted view/session metadata and validated scopes. Security tests pass across all backends, including explicit Linux provenance behavior, and release diagnostics reveal policy posture without exposing sensitive data.
