# Frontend tooling, production assets, and templates

The frontend targets included in the `NeoAstra` package, the `dotnet neoastra` tool, and the production manifest host keep frontend framework choices ordinary. For npm projects with an explicitly configured committed `package-lock.json`, NeoAstra runs an incremental locked `npm ci` before the configured frontend build. It never installs Node.js or npm themselves, never enables telemetry, and never requires Node.js at application runtime. The checked optional configuration schema is [`schemas/neoastra-project-v1.schema.json`](../schemas/neoastra-project-v1.schema.json).

## Configuration and inspection

No configuration file is needed for the common layouts. A `frontend/package.json` project uses exactly one supported lockfile, standard `dev` and `build` scripts, `frontend/dist`, and a loopback Vite server at `127.0.0.1:5173`; NeoAstra derives the remaining secure defaults. A plain static project needs only `frontend/index.html`: no package manifest, Node.js, package manager, build command, or copied NeoAstra runtime is required. Place an optional `neoastra.json` beside the backend project only to override the package-project conventions. All configured paths resolve from that file, not the caller's working directory. Commands are JSON arrays and are passed through `ProcessStartInfo.ArgumentList` without a shell. Unknown fields, duplicate JSON properties, unknown versions, unsafe production origins, permissive CSP values, unbounded values, and malformed paths fail closed. `frontend.environment` lists explicit additions; names in `secretEnvironment` are redacted from inspect output and child logs. The inherited process environment is otherwise retained so normal toolchains can locate their runtimes; do not put secrets in command arguments.

NeoAstra invokes a directly available package manager first. If `npm` is not directly available, it detects the common executable Node managers `fnm`, Volta, mise, and asdf, in that order, and uses their non-shell execution form. For `fnm`, NeoAstra honors a `.nvmrc` or `.node-version` in the frontend root; otherwise it uses the configured `default` fnm alias. On Windows it invokes `npm.cmd` through fnm because fnm cannot launch the extensionless npm shim. NeoAstra does not activate a shell function, mutate the selected Node version, or install Node/npm. Detection applies consistently to locked restore and configured npm `devCommand`/`buildCommand` entries. Set `frontend.packageManagerCommand` to an explicit command-prefix array such as `["C:/tools/fnm.exe", "exec", "--using=22", "npm.cmd"]` to override detection; `inspect` shows the effective prefix and `doctor` probes it. An explicit absolute npm path in a dev/build command remains untouched. nvm is not auto-activated because its common Unix installation is a shell function; initialize it in the calling environment or use the explicit command-prefix override.

```sh
dotnet neoastra inspect
dotnet neoastra doctor
dotnet neoastra doctor --json
```

`inspect` emits resolved absolute paths and redacts configured secrets. `doctor` checks configured executables, lockfile/output status, exact loopback policy, CSP indicators, and the custom-scheme service-worker limitation. Neither command installs or repairs software.

## Development

For a freshly generated npm project, run `npm install` (or the equivalent manager-prefixed invocation) from `frontend` once to create `package-lock.json`, review it, and commit it. After that, `dotnet build` and `dotnet neoastra dev` run `npm ci --no-audit --no-fund` automatically only when the package manifest or lockfile changed, `node_modules` was removed, or the successful-restore marker is absent. A missing lockfile fails with an actionable diagnostic instead of performing an unlocked install. The committed lockfile is a supply-chain trust boundary: `npm ci` may download packages and execute their lifecycle scripts, so review dependency and lockfile changes like build code. Then run:

```sh
dotnet neoastra dev
```

The tool first runs the configured `contractCommand` (default `dotnet build --no-restore`) so generated bindings are current, then starts the configured frontend command in `frontend.root`, labels/redacts output, probes the exact `devUrl` without following redirects, and starts the configured backend command only after an HTTP 2xx response. HTTP error pages are not readiness; connection failures and individual two-second request timeouts are retried within the overall 60-second deadline. User cancellation remains cancellation, while an exhausted deadline reports `readiness_timeout`. An early frontend exit cancels and observes the outstanding probe, and a known exit takes precedence over simultaneous readiness. Generated defaults use `127.0.0.1`; `localhost` is rejected. `::1` is also accepted. Other IP literals require `allowRemoteDevServer: true` and produce a prominent warning. The exact configured origin alone is trusted. Ctrl+C, readiness timeout, or either unexpected child exit tears down both process trees within a bounded interval and returns nonzero for unexpected failures. Frontend HMR remains Vite's responsibility; C# restart remains `dotnet watch`'s responsibility.

A successful loopback response proves reachability, not process identity. Keep Vite's strict-port
setting enabled, reserve the configured development port, and run only a trusted development server
on an origin given bridge authority. Readiness does not authenticate a different process already
listening on that port.

## Build, run, publish, and MSBuild properties

Reference only the `NeoAstra` package. The package carries the compiled, framework-neutral `@neoastra/client` runtime under its SDK tools; repository `ProjectReference` builds compile the same runtime as part of the `NeoAstra` project. For package-based `frontend` projects, the transitive targets stage that package under `obj/neoastra/client` before locked npm restore, so `"@neoastra/client": "file:../obj/neoastra/client"` remains offline and version-aligned with the .NET package without compiling NeoAstra's TypeScript source in each application. For static `frontend/index.html` projects, the targets copy the browser runtime modules and an optional generated JavaScript RPC binding directly into the materialized frontend under `obj`; SDK files never appear in the project tree. When frontend work is configured, normal `dotnet build` runs preparation after C# compilation, generates `neoastra-assets.json`, and re-verifies every hash while copying an exact staging directory under `obj`. The same prepared assets are copied to `bin/.../assets`, so ordinary `dotnet run` consumes regular build output. Publish reuses that preparation instead of requiring a publish-only frontend build.

For configured package projects, the production command is framework-neutral: it is exactly the `frontend.buildCommand` argument array. It may invoke any locally available build tool and does not imply Vite, React, TypeScript, Node.js, or a package manager. Static convention projects perform no frontend command: the SDK fingerprints `frontend`, assembles project and SDK files under `obj`, validates the result, and synchronizes exact build/publish assets. `packageManager: "none"` never invokes Node. Automatic dependency restore is currently limited to explicitly configured npm projects whose `frontend.lockfile` is `frontend.root/package-lock.json`; other package managers remain explicit.

The targets fingerprint the effective configuration, configuration name, frontend tree, lockfile, generated RPC contract outputs, declared extra inputs, and NeoAstra tool version. The configured `dist` directory, `node_modules`, and VCS metadata are excluded from normal-build inputs. Content changes, additions, and deletions therefore rerun preparation, while an unchanged build skips the production command. Preparation and output copies synchronize exact directories so removed assets do not survive a rerun; `dotnet clean` removes tracked preparation/output files and causes the next build to prepare again.

| Property | Meaning |
| --- | --- |
| `NeoAstraProjectConfig` | Optional configuration path; defaults to `$(MSBuildProjectDirectory)/neoastra.json`, with conventions used when absent |
| `NeoAstraFrontendEnabled` | Explicitly disables frontend work when `false` |
| `NeoAstraStaticFrontend` | Selects no-build-tool materialization; defaults to `true` for `frontend/index.html` without `frontend/package.json` |
| `NeoAstraStaticFrontendSourceDirectory` | Project-owned static source; defaults to `frontend` |
| `NeoAstraBuildFrontend` | Skips the configured production command and preparation when `false`; explicit prebuilt mode still validates and stages its reviewed directory |
| `NeoAstraRestoreFrontendDependencies` | Runs incremental locked npm restore when `true` (default); set `false` only when dependencies are provisioned separately |
| `NeoAstraStageFrontendClient` | Stages `@neoastra/client` before frontend restore/materialization; defaults to `true` for package and static frontend conventions |
| `NeoAstraFrontendClientStageDirectory` | Generated local npm package directory; defaults to `obj/neoastra/client` |
| `NeoAstraAssetManifest` / `NeoAstraAssetOutput` | Intermediate manifest/staging paths; CI may relocate them under `obj` |
| `NeoAstraPrebuiltAssets` | Selects explicit prebuilt mode when `true` |
| `NeoAstraPrebuiltAssetDirectory` | Required prebuilt directory; it must equal the resolved configured `dist` path |
| `NeoAstraToolPath` | Tool assembly override used by source builds/controlled CI |
| `NeoAstraDevUrl` | CI/development URL override; it remains subject to the same exact loopback policy and does not affect production manifest output |
| `NeoAstraAllowDevelopmentSettingsInRelease` | Prominent explicit override for a reviewed remote-dev setting; does not change production origin/CSP/capabilities |

Additional files or directories that affect a custom frontend build can be declared with `NeoAstraFrontendInput` items. The generated TypeScript and manifest outputs are included automatically when present:

```xml
<ItemGroup>
  <NeoAstraFrontendInput Include="frontend-build.config.json" />
</ItemGroup>
```

`dotnet neoastra dev` sets `NeoAstraBuildFrontend=false` for its backend child so `dotnet watch` does not launch a competing production build while the configured development/HMR process is active. Design-time builds also skip production preparation.

Normal build/run and publish (dependencies already restored):

```sh
dotnet build -c Release
dotnet run -c Release --no-build
dotnet publish -c Release -r win-x64 --self-contained
```

Prebuilt/offline publish:

```sh
dotnet publish -c Release -r linux-x64 --self-contained \
  -p:NeoAstraPrebuiltAssets=true \
  -p:NeoAstraPrebuiltAssetDirectory=/absolute/reviewed/project/frontend/dist
```

Prebuilt mode does not skip validation. It requires the explicit configured directory, rebuilds the deterministic manifest, rejects links/reparse points, source maps unless opted in, VCS/source/dependency/secret paths, case collisions, reserved routes, excessive files/sizes, and files that change between manifest and copy. It requires the configured lockfile but skips npm restore and the frontend command. To work fully offline, populate NuGet and frontend caches beforehand or use reviewed prebuilt mode. Do not point prebuilt mode at downloaded or mutable content without reviewing it.

For deterministic CI, generate the manifest twice from clean equivalent frontend outputs and byte-compare it, then verify the intermediate RPC contract before publishing:

```sh
dotnet build -c Release
dotnet neoastra contract check --typescript obj/neoastra/neoastra.ts --manifest obj/neoastra/neoastra.manifest.json
dotnet publish -c Release -r "$RID" --self-contained
```

## Runtime hosting and security

Load `neoastra-assets.json` with `NeoAssetManifest.Load`, then construct `NeoManifestResourceProvider`. It serves only listed regular files using `GET`/`HEAD`, decodes strict UTF-8 exactly once, ignores query/fragment for lookup, rejects encoded separators/dot traversal/NUL/device forms, linked asset-root ancestors, and escaping links, and applies manifest MIME/cache metadata plus `nosniff`, CSP, and referrer policy. Missing recognized assets remain 404. SPA fallback is limited by Accept, static extensions, optional route prefixes, and excluded `/api` and `/_neoastra` prefixes. Entry HTML and mutable manifests revalidate; content-hashed files may be immutable. There is no permissive CORS default and no development fallback header in production.

Vite must use `base: "./"` so modules, dynamic imports, worker URLs, fonts, and other assets remain custom-scheme relative. Service workers are intentionally absent: WebKitGTK custom schemes do not support them and backend behavior is not portable enough to advertise. Windows WebView2, macOS WKWebView, and Linux WebKitGTK implementations compile through the existing custom-scheme ABI; renderer behavior still requires their platform CI/browser suites. Linux bridge-enabled production views remain controlled-local-content-only because source-origin provenance is unavailable.

## Initialization and templates

`dotnet neoastra init` requires explicit frontend root, command arguments, URL, output, identity, and package manager. Run `--dry-run` first. The generated configuration is validated relative to its destination before any destination mutation, then created or replaced atomically through that directory. Existing `neoastra.json` is a conflict; no write occurs unless `--force`, which creates a conflict-safe, non-overwriting `.bak` rather than deleting the old file. Initialization prints the client dependency command but does not execute it; after a reviewed lockfile is committed, normal builds may perform the locked incremental npm restore described above. Separate sibling frontend/backend layouts work because paths resolve from the configuration file.

`NeoAstra.Templates` contains vanilla TypeScript (`neoastra`), Vite React (`neoastra-react`), and Vite Vue (`neoastra-vue`) templates. They use ordinary upstream projects, generated typed calls, a mock-transport test, exact loopback Vite defaults, relative production base, accessible language/viewport/focus/reduced-motion foundations, and deny external opening until a reviewed backend command is granted. Shared host/security fragments are checked by `frontend/tools/generate-templates.mjs --check`; generated output remains plain source. Each template includes a reviewed npm lockfile whose local NeoAstra client dependency is materialized from the referenced `NeoAstra` package by the SDK targets, so a generated application can restore and build without a public client registry. Normal NeoAstra builds then restore that locked graph incrementally.

The advanced application-platform sample is `samples/NeoAstra.Sample.Advanced`. It carries React source with generated typed application RPC that needs no permission declarations, plus one explicit capability record restricting the `preview` view's native desktop authority. It also includes a committed npm lockfile and a relative module graph containing the entry module, dynamic chunk, module worker, bundled CSS, project-owned fixture font, and SVG asset. A clean normal `dotnet build` performs the locked npm restore, runs the configured Vite production command, validates/stages exact assets, and writes regular runnable output. Generated `dist` directories are not committed. The temporary-project MSBuild integration tests separately exercise explicit reviewed prebuilt mode without running a frontend command. The sample's NativeAOT validation resolves the restricted preview capability, verifies every asset hash/MIME response, and checks graph references. Actual interactive module/dynamic-import/worker/font and HMR behavior remains platform renderer testing.
