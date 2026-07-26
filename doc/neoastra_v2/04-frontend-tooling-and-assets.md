# Step 4 — Frontend Tooling, Asset Hosting, SDK, and Templates

**Parent:** [`neoastra_v2_specs.md`](../neoastra_v2_specs.md)
**Depends on:** [Steps 1–3](01-frontend-transport.md)
**Outcome:** Existing and new static frontends receive one-command development, generated contracts, secure asset hosting, and deterministic publish.

## 1. Scope

NeoAstra owns orchestration and static hosting, not the frontend framework. This step provides a small versioned project configuration, `NeoAstra.Sdk` MSBuild integration, a `dotnet neoastra` tool where MSBuild is insufficient, templates, production asset manifests, SPA fallback, and a reference application.

SSR and production localhost servers are explicit advanced configurations and are not part of the default path.

## 2. Project configuration

A project SHALL be representable by a versioned `neoastra.json` equivalent to:

```json
{
  "$schema": "neoastra-project-v1.schema.json",
  "version": 1,
  "app": {
    "identifier": "com.acme.notes",
    "displayName": "Acme Notes"
  },
  "frontend": {
    "root": "ClientApp",
    "devCommand": ["pnpm", "dev", "--host", "127.0.0.1"],
    "devUrl": "http://127.0.0.1:5173",
    "buildCommand": ["pnpm", "build"],
    "dist": "ClientApp/dist",
    "spaFallback": "index.html"
  },
  "assets": {
    "origin": "app://acme",
    "cacheHashedAssets": true,
    "csp": "default-src 'self'; ..."
  },
  "capabilities": ["capabilities/main.json"]
}
```

Normative configuration rules:

- paths resolve relative to the configuration/project directory, never ambient working directory;
- command arrays are preferred over shell strings to avoid quoting/injection ambiguity;
- environment additions are explicit and redactable; inherited environment policy is documented;
- unknown fields are errors for security/build sections unless the schema marks them forward-compatible;
- Release fails on development-only settings unless explicitly overridden with a prominent diagnostic;
- configuration supports MSBuild property overrides for CI without making output non-reproducible;
- resolved configuration can be printed as inspectable JSON with secrets redacted;
- package installation is never automatic during build or publish.

## 3. Tool responsibilities

### 3.1 `NeoAstra.Sdk`

The SDK SHOULD own deterministic build integration:

- validate project/capability configuration;
- run or verify generated RPC contracts;
- declare frontend build inputs/outputs;
- invoke configured production build during publish;
- validate and manifest production assets;
- copy/embed only the validated output;
- produce metadata consumed by Step 7;
- support `--no-frontend-build` only with an already validated explicit asset directory;
- avoid running package-manager restore/install unexpectedly.

### 3.2 `dotnet neoastra`

The tool SHOULD own interactive/process orchestration:

```text
dotnet neoastra init
dotnet neoastra dev
dotnet neoastra doctor
dotnet neoastra inspect
dotnet neoastra bundle
dotnet neoastra contract diff
```

`init` adds files/config to an existing frontend/.NET project without overwriting uncommitted files. `dev` coordinates processes. `doctor` reports .NET, Node/package manager when configured, WebView runtime, native dependencies, platform packaging tools, and actionable fixes. `inspect` prints resolved assets/capabilities/plugins/build metadata. Bundle delegates to Step 7.

The tool MUST support noninteractive CI, machine-readable output, useful exit codes, `--dry-run` where mutation occurs, and no telemetry by default.

## 4. Development orchestration

`dotnet run` through project targets or `dotnet neoastra dev` SHALL:

1. load and validate configuration;
2. verify the configured frontend command executable without installing it;
3. start the dev process in the configured root with explicit environment;
4. capture stdout/stderr with process labels while preserving useful colors where possible;
5. wait for the exact configured loopback URL using bounded readiness probes;
6. reject redirects to unexpected origins during readiness;
7. start/watch the .NET application with the development origin/profile;
8. keep frontend HMR owned by the frontend tool;
9. propagate Ctrl+C/process exit and terminate the process tree cleanly;
10. return nonzero when either required process fails unexpectedly.

### 4.1 Security

- default dev hosts are `127.0.0.1`/`::1`, not all interfaces;
- `localhost` DNS ambiguity SHOULD be avoided in generated defaults;
- arbitrary LAN/remote URLs require an explicit insecure-development opt-in and warning;
- only the configured exact origin is trusted;
- the dev server cannot alter release capabilities or production CSP;
- command arguments are passed without a shell unless the user explicitly selects shell execution;
- logs redact configured secret environment variables;
- child processes are never started by a normal library build that did not request dev/publish frontend work.

### 4.2 C# changes

NeoAstra does not implement renderer HMR. The documented path uses the frontend's HMR and `dotnet watch` application restart. Tooling MUST ensure old app instances/process trees are stopped before replacement and SHALL preserve stable frontend port only when safe. Contract regeneration SHOULD complete before a restarted frontend performs calls; mismatch errors remain clear during races.

## 5. Production frontend build

Publish SHALL:

1. validate lockfile/package-manager policy when configured;
2. execute `buildCommand` in `frontend.root` unless an explicit validated prebuilt mode is selected;
3. require successful exit and existing `dist` directory;
4. reject output outside allowed project/build roots;
5. enumerate regular files without following links/reparse points outside the root;
6. exclude `node_modules`, source directories, VCS data, environment files, secrets, and undeclared files by construction;
7. validate asset count, individual/total size, case-colliding paths, normalized relative paths, and reserved routes;
8. assign MIME types and security/cache metadata;
9. produce a sorted versioned asset manifest with SHA-256 hashes;
10. embed/copy exactly manifest-listed files into publish output.

Incremental builds MUST include configuration, lockfiles, generated contracts, and relevant frontend inputs or conservatively rerun. Correctness is preferred over fragile timestamp heuristics. CI SHOULD support a clean deterministic mode comparing manifests across two builds when the frontend itself is reproducible.

## 6. Static asset host

The production host extends the hardened v1 directory/custom-scheme provider.

### 6.1 Request mapping

- only `GET` and `HEAD` are supported for static assets;
- URL paths are percent-decoded exactly once using strict UTF-8;
- empty/root path maps to configured entry document;
- normalized `.`/`..`, encoded separators, NUL, drive/device forms, case ambiguity, and links escaping root are rejected;
- query/fragment do not participate in filesystem lookup;
- manifest lookup is preferred over ambient filesystem traversal in packaged apps;
- `HEAD` returns headers and length without body;
- absent assets return a real 404 unless SPA fallback rules apply.

### 6.2 SPA fallback

Fallback to `index.html` occurs only when:

- method is `GET`/`HEAD`;
- request accepts HTML or has no stronger asset expectation;
- path has no recognized static asset extension, or matches configured route patterns;
- no manifest asset exists;
- path is not under excluded API/internal prefixes.

A missing `.js`, `.css`, image, font, source map, manifest asset, or explicit file route MUST remain 404. Fallback adds a diagnostic header in development only.

### 6.3 Headers and caching

Minimum production headers:

- correct `Content-Type` and `X-Content-Type-Options: nosniff`;
- configured restrictive `Content-Security-Policy`;
- `Referrer-Policy` and other template security defaults;
- immutable long cache for content-hashed assets;
- no-cache/revalidation for entry HTML and mutable manifest files;
- no source maps unless explicitly enabled;
- no permissive CORS by default.

Custom-scheme base URLs, ES modules, dynamic imports, workers, fonts, and asset URLs MUST be tested on every backend. Service-worker behavior MUST be documented honestly and disabled/rejected where the custom-scheme backend cannot support it securely.

## 7. Templates

Initial templates:

1. vanilla TypeScript;
2. Vite React TypeScript;
3. Vite Vue TypeScript;
4. Vite Svelte TypeScript after the first three are stable.

Each template uses an ordinary upstream frontend project with minimal NeoAstra additions:

- `@neoastra/client` dependency;
- generated application contract import;
- one typed invocation/event example;
- secure navigation/external-link handling;
- accessible HTML language/viewport/focus-visible/reduced-motion foundation;
- development and production configuration;
- unit tests using mock transport;
- backend tests and NativeAOT publish profile;
- no NeoAstra UI components.

Templates MUST be generated/tested from shared minimal host fragments to prevent security drift, but checked output MUST remain understandable without the template generator.

## 8. Existing-project initialization

`dotnet neoastra init` SHALL accept explicit frontend root, dev command/URL, build command, output, app identity, and package manager. It MUST:

- detect conflicts and show a dry-run plan;
- preserve existing package scripts/configuration where possible;
- never delete or overwrite without explicit consent/backup policy;
- add client dependency using an opt-in command or print the exact package-manager command;
- validate the resulting configuration;
- support projects with frontend and backend in separate sibling directories;
- avoid framework-specific source rewriting beyond selected, reviewable adapter files.

## 9. Generated contracts in frontend builds

The RPC contract pipeline MUST finish before TypeScript typecheck/build. Generated output location is explicit, stable, and excluded/included in source control by application choice. Build diagnostics detect stale generated output using a contract hash. CI provides a mode equivalent to “generate then fail if the working tree differs.”

Frontend build does not infer renderer permissions from generated APIs. Capability manifests remain a separate reviewed input.

## 10. Asset diagnostics

The application diagnostic snapshot includes asset manifest version/hash, entry document, asset count/total bytes, production/development origin, CSP hash/summary, SPA fallback mode, and whether source maps are present. It MUST NOT expose asset contents or local source paths.

`doctor`/`inspect` SHOULD detect:

- missing output/entry point;
- wrong Vite `base`/custom-scheme asset URL behavior;
- insecure CSP or remote scripts;
- service-worker configuration on unsupported backends;
- dev server binding to non-loopback;
- development capability in release;
- case-colliding asset paths and excessive files/sizes;
- generated contract mismatch.

## 11. Reference application

This step starts the v2 reference app. It MUST use Generic Host integration when Step 5 lands, generated RPC, two view labels with distinct grants, React or Vue, local assets, secure opener policy, unit/integration tests, and NativeAOT. It is built by CI in dev-configuration validation and production publish modes.

## 12. Implementation order

- [x] Define project JSON Schema, resolution rules, MSBuild properties, and inspect output.
- [x] Implement process runner/readiness/teardown with fake-process tests.
- [x] Implement `dev`, exact loopback-origin policy, and frontend log routing.
- [x] Implement RPC contract generation ordering and stale-check target.
- [x] Implement production build target and strict asset manifest builder.
- [x] Extend asset host with manifest lookup, SPA fallback, MIME/cache/security headers.
- [x] Create vanilla, React, and Vue templates from shared secure host fragments.
- [x] Implement conflict-safe existing-project `init` and `doctor`.
- [x] Build the reference app and publish it under NativeAOT.
- [x] Document manual/CI/offline/prebuilt workflows.

## 13. Verification

Tests include configuration schema/version/errors; path resolution; command argument quoting on all OSes; loopback/remote URL validation; readiness timeout/redirect; Ctrl+C/process crash/orphan cleanup; no implicit package install; missing/failed build; malicious symlink/reparse/path/case output; asset MIME/cache/CSP; SPA route reload versus missing asset 404; module/dynamic import/worker/font behavior per backend; development and production origin separation; template create/restore/dev/build/test/publish from clean directories; lockfile variants; spaces/Unicode in paths; and deterministic manifest/package content.

## 14. Exit criteria

Vanilla TypeScript, React, and Vue templates support one-command development with frontend HMR and deterministic `dotnet publish`. An existing frontend can be initialized without destructive rewriting. Production assets are strictly manifested and securely hosted, SPA routes reload correctly, no raw bridge glue exists, and NativeAOT reference-app publish passes from a clean checkout.
