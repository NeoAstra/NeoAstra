# Frontend tooling, production assets, and templates

NeoAstra's version 1 project configuration, SDK, `dotnet neoastra` tool, and production manifest host keep frontend framework choices ordinary. NeoAstra never runs a package install, never enables telemetry, and never requires Node.js at application runtime. The normative design is [v2 Step 4](neoastra_v2/04-frontend-tooling-and-assets.md); the checked schema is [`schemas/neoastra-project-v1.schema.json`](../schemas/neoastra-project-v1.schema.json).

## Configuration and inspection

Place `neoastra.json` beside the backend project. All paths resolve from that file, not the caller's working directory. Commands are JSON arrays and are passed through `ProcessStartInfo.ArgumentList` without a shell. Unknown fields, duplicate JSON properties, unknown versions, unsafe production origins, permissive CSP values, unbounded values, and malformed paths fail closed. `frontend.environment` lists explicit additions; names in `secretEnvironment` are redacted from inspect output and child logs. The inherited process environment is otherwise retained so normal toolchains can locate their runtimes; do not put secrets in command arguments.

```sh
dotnet neoastra inspect --config neoastra.json
dotnet neoastra doctor --config neoastra.json
dotnet neoastra doctor --config neoastra.json --json
```

`inspect` emits resolved absolute paths and redacts configured secrets. `doctor` checks configured executables, lockfile/output status, exact loopback policy, CSP indicators, and the custom-scheme service-worker limitation. Neither command installs or repairs software.

## Development

Restore frontend dependencies explicitly with the committed project's package manager. For a freshly generated npm template, run `npm install` from `ClientApp` to create `package-lock.json`, review and commit that lockfile, and only then run development or publish commands; NeoAstra never performs the install. Then run:

```sh
dotnet neoastra dev --config neoastra.json
```

The tool first runs the configured `contractCommand` (default `dotnet build --no-restore`) so generated bindings are current, then starts the configured frontend command in `frontend.root`, labels/redacts output, probes the exact `devUrl` without following redirects, and starts the configured backend command only after readiness. Generated defaults use `127.0.0.1`; `localhost` is rejected. `::1` is also accepted. Other IP literals require `allowRemoteDevServer: true` and produce a prominent warning. The exact configured origin alone is trusted. Ctrl+C, readiness timeout, or either unexpected child exit tears down both process trees within a bounded interval and returns nonzero for unexpected failures. Frontend HMR remains Vite's responsibility; C# restart remains `dotnet watch`'s responsibility.

## Publish and MSBuild properties

Reference `NeoAstra.Sdk` as a private build dependency. Its publish target runs after C# compilation so the RPC generator finishes first, compares the generated TypeScript header with the SHA-256 of the generated backend manifest, runs the configured production build, generates `neoastra-assets.json`, re-verifies every hash while copying, and adds exactly the staging directory to publish output.

| Property | Meaning |
| --- | --- |
| `NeoAstraProjectConfig` | Configuration path; defaults to `$(MSBuildProjectDirectory)/neoastra.json` |
| `NeoAstraFrontendEnabled` | Explicitly disables SDK frontend work when `false` |
| `NeoAstraAssetManifest` / `NeoAstraAssetOutput` | Intermediate manifest/staging paths; CI may relocate them under `obj` |
| `NeoAstraPrebuiltAssets` | Selects explicit prebuilt mode when `true` |
| `NeoAstraPrebuiltAssetDirectory` | Required prebuilt directory; it must equal the resolved configured `dist` path |
| `NeoAstraToolPath` | Tool assembly override used by source builds/controlled CI |
| `NeoAstraDevUrl` | CI/development URL override; it remains subject to the same exact loopback policy and does not affect production manifest output |
| `NeoAstraAllowDevelopmentSettingsInRelease` | Prominent explicit override for a reviewed remote-dev setting; does not change production origin/CSP/capabilities |

Normal publish (dependencies already restored):

```sh
dotnet publish -c Release -r win-x64 --self-contained
```

Prebuilt/offline publish:

```sh
dotnet publish -c Release -r linux-x64 --self-contained \
  -p:NeoAstraPrebuiltAssets=true \
  -p:NeoAstraPrebuiltAssetDirectory=/absolute/reviewed/project/ClientApp/dist
```

Prebuilt mode does not skip validation. It requires the explicit configured directory, rebuilds the deterministic manifest, rejects links/reparse points, source maps unless opted in, VCS/source/dependency/secret paths, case collisions, reserved routes, excessive files/sizes, and files that change between manifest and copy. Production builds with a configured package manager require its explicit lockfile; no restore/install is inferred. To work fully offline, populate NuGet and frontend caches beforehand, restore with the ecosystem's offline/frozen-lockfile option, and use prebuilt mode. Do not point prebuilt mode at downloaded or mutable content without reviewing it.

For deterministic CI, generate the manifest twice from clean equivalent frontend outputs and byte-compare it, run `dotnet neoastra contract check`, then fail on generated-tree drift:

```sh
dotnet build -c Release
dotnet neoastra contract check --typescript ClientApp/src/generated/neoastra.ts --manifest generated/neoastra.manifest.json
git diff --exit-code -- ClientApp/src/generated generated
dotnet publish -c Release -r "$RID" --self-contained
```

## Runtime hosting and security

Load `neoastra-assets.json` with `NeoAssetManifest.Load`, then construct `NeoManifestResourceProvider`. It serves only listed regular files using `GET`/`HEAD`, decodes strict UTF-8 exactly once, ignores query/fragment for lookup, rejects encoded separators/dot traversal/NUL/device forms, linked asset-root ancestors, and escaping links, and applies manifest MIME/cache metadata plus `nosniff`, CSP, and referrer policy. Missing recognized assets remain 404. SPA fallback is limited by Accept, static extensions, optional route prefixes, and excluded `/api` and `/_neoastra` prefixes. Entry HTML and mutable manifests revalidate; content-hashed files may be immutable. There is no permissive CORS default and no development fallback header in production.

Vite must use `base: "./"` so modules, dynamic imports, worker URLs, fonts, and other assets remain custom-scheme relative. Service workers are intentionally absent: WebKitGTK custom schemes do not support them and backend behavior is not portable enough to advertise. Windows WebView2, macOS WKWebView, and Linux WebKitGTK implementations compile through the existing custom-scheme ABI; renderer behavior still requires their platform CI/browser suites. Linux bridge-enabled production views remain controlled-local-content-only because source-origin provenance is unavailable.

## Initialization and templates

`dotnet neoastra init` requires explicit frontend root, command arguments, URL, output, identity, and package manager. Run `--dry-run` first. The generated configuration is validated relative to its destination before any destination mutation, then created or replaced atomically through that directory. Existing `neoastra.json` is a conflict; no write occurs unless `--force`, which creates a conflict-safe, non-overwriting `.bak` rather than deleting the old file. The tool prints the exact opt-in client dependency command and never executes it. Separate sibling frontend/backend layouts work because paths resolve from the configuration file.

`NeoAstra.Templates` contains vanilla TypeScript (`neoastra`), Vite React (`neoastra-react`), and Vite Vue (`neoastra-vue`) templates. They use ordinary upstream projects, generated typed calls, a mock-transport test, exact loopback Vite defaults, relative production base, accessible language/viewport/focus/reduced-motion foundations, and deny external opening until a reviewed backend command is granted. Shared host/security fragments are checked by `frontend/tools/generate-templates.mjs --check`; generated output remains plain source. Template package restore and frontend dependency installation are explicit network/cache operations, never part of a normal NeoAstra build.

The Step 4 reference application is `samples/NeoAstra.V2.Reference`. It carries React source with generated typed RPC use, an explicit `main` grant and an omitted/default-denied `preview` grant, and a checked prebuilt relative module graph containing the entry module, dynamic chunk, module worker, bundled CSS, project-owned fixture font, and SVG asset. `npm run check:fixtures` rebuilds that graph with locked repository tooling and byte-compares every output path and file, so drift fails offline checks. Its NativeAOT validation resolves release capabilities, confirms main-granted/preview-denied selectors, verifies every asset hash/MIME response, and checks graph references. CI publishes it on each OS/RID matrix and runs this non-renderer validation. Actual interactive module/dynamic-import/worker/font and HMR behavior remains platform renderer testing; this Windows implementation pass verified only non-renderer tooling/publish behavior and did not execute interactive WebView2, WKWebView, or WebKitGTK/HMR automation.
