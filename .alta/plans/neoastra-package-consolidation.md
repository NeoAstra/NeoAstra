# NeoAstra package consolidation and HelloWorld refactor

**Status:** Implemented and verified on Windows<br>
**Prepared:** 2026-07-28  
**Baseline:** `706ff5e0471cd3e8ce921e2972b7d73afee9ae4f`

## Goal

Reduce the public runtime product to two NuGet packages and two runtime assemblies:

1. `NeoAstra.Core` / `NeoAstra.Core.dll` — the low-level cross-platform application, window, WebView, browser-policy, profile, transport, custom-scheme, ABI, and embedding layer.
2. `NeoAstra` / `NeoAstra.dll` — the default full experience, depending on `NeoAstra.Core` and containing RPC, capability security, desktop services, Generic Host integration, and a simple one-window local-web-app facade.

Keep tooling and templates as separate installation artifacts because .NET tools/templates have different installation semantics, but require only one direct runtime `PackageReference` in ordinary applications. NeoAstra is unreleased, so old package IDs, directories, and assembly compatibility shims will not be retained.

## Accepted decisions

- Rename the current project/package/assembly to `NeoAstra.Core`, while preserving its public types in namespace `NeoAstra`.
- Fold `NeoAstra.Rpc`, `NeoAstra.Desktop`, and `NeoAstra.Hosting` source into the new `NeoAstra.dll`, retaining their useful namespaces (`NeoAstra.Rpc`, `NeoAstra.Desktop`, and `NeoAstra.Hosting`) to keep the API organized.
- Accept `Microsoft.Extensions.Hosting.Abstractions` as a dependency of the full `NeoAstra` package.
- Keep only two runtime library packages. Tool and template artifacts do not count as runtime packages.
- Embed the incremental RPC generator in `NeoAstra.nupkg`; users never reference or install a generator package explicitly.
- Preserve default-deny security. A simple local one-window app may use a code-first capability manifest, but must explicitly grant generated permissions to `main`; registering an RPC service alone never grants it.
- Remove old package IDs (`NeoAstra.Rpc`, `NeoAstra.Desktop`, `NeoAstra.Hosting`, `NeoAstra.Rpc.Generator`, and `NeoAstra.Sdk`) without transitional packages.
- Remove V2 from all shipping sample/product names. V2 specification documents may remain as historical design/audit documents.

## Target repository and package topology

```text
src/
  NeoAstra.Core/
    NeoAstra.Core.csproj
    ...current low-level core source...
    runtimes/<RID>/native/...

  NeoAstra/
    NeoAstra.csproj
    Application/              # new high-level facade and conventions
    Rpc/                      # moved NeoAstra.Rpc source
    Desktop/                  # moved NeoAstra.Desktop source/resources/schemas
    Hosting/                  # moved NeoAstra.Hosting source
    Build/                    # packaged props/targets and schemas/docs

  NeoAstra.Generator/
    NeoAstra.Generator.csproj # internal build project, not a separate NuGet
    NeoRpcGenerator.cs

  NeoAstra.Tool/
    ...CLI plus moved Tooling and Capabilities.Tool behavior...

  NeoAstra.Templates/         # separate Template package
  NeoAstra.CodeGen/           # internal native ABI generator
  NeoAstra.Tests/
  NeoAstra.Conformance/
  NeoAstra.Benchmarks/
  NeoAstra.NativeAotFixture/  # one full-experience fixture replacing RPC/Desktop fixtures
  NeoAstra.SingleInstanceHelper/

samples/
  NeoAstra.Core.Sample/       # renamed current low-level sample
  NeoAstra.Sample/            # minimal full-experience typed HelloWorld
  NeoAstra.Sample.Advanced/   # renamed current V2 feature tour
```

### `NeoAstra.Core.nupkg`

Contains only:

- `lib/net10.0/NeoAstra.Core.dll` and XML documentation;
- RID-specific `neoastra_native` assets;
- public native headers;
- core docs/readme/license/notices and symbols.

It has no RPC, desktop, hosting, frontend build, analyzer, or tool dependency.

### `NeoAstra.nupkg`

Contains:

- `lib/net10.0/NeoAstra.dll` and XML documentation;
- dependency on the exact matching `NeoAstra.Core` package version;
- dependency on `Microsoft.Extensions.Hosting.Abstractions`;
- RPC generator under `analyzers/dotnet/cs/`;
- compiler-visible-property/build targets required by generated TypeScript output;
- desktop plugin descriptors and scope schemas;
- frontend/project/capability/delivery schemas and relevant docs;
- build assets/tool payload needed by automatic frontend prepare/publish, if the direct-pack prototype proves cycle-free.

The analyzer remains a separate internal project so Roslyn can load a `netstandard2.0` assembly, but it is `IsPackable=false` and is shipped only inside `NeoAstra.nupkg`, following the XenoAtom.Terminal.UI precedent.

### Tooling artifacts

- Consolidate `NeoAstra.Capabilities.Tool` into `NeoAstra.Tool` as `neoastra capabilities resolve ...`.
- Fold `NeoAstra.Tooling` source into the tool project unless tests show a compelling internal assembly boundary; it is not a public runtime library.
- Fold `NeoAstra.Sdk` props/targets into the main package rather than publishing `NeoAstra.Sdk`.
- Keep `NeoAstra.Tool` as an optional separately installable `dotnet tool` so `dotnet neoastra dev/init/doctor/inspect/bundle` remains available.
- Keep `NeoAstra.Templates` as a separate template artifact.
- Ensure a template application directly references only `NeoAstra`; the optional tool may be installed globally/locally for orchestration but is not a runtime library reference.

If packing the tool payload inside `NeoAstra.nupkg` creates an unavoidable `NeoAstra -> Tool -> NeoAstra` MSBuild cycle, keep the build payload in one non-runtime `NeoAstra.Tool` package and make it a build-only dependency of `NeoAstra`; do not restore the old `NeoAstra.Sdk` package or add another runtime assembly. This fallback must still preserve a single direct application `PackageReference`.

## Simplified application experience

### One direct project reference

The generated React/vanilla/Vue projects should reduce to the equivalent of:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NeoAstra" Version="..." />
  </ItemGroup>
</Project>
```

The package supplies the analyzer and frontend build integration. No explicit references to RPC, Desktop, Generator, or SDK packages remain.

### High-level local application facade

Add a focused high-level API (provisionally `NeoApp`, `NeoAppBuilder`, and `NeoAppOptions`) above the existing low-level primitives. The simple path must:

- create one `main` window and one fill-parent view;
- use a safe default title/size and allow concise overrides;
- use `NEOASTRA_DEV_URL` for a validated loopback development origin;
- otherwise serve manifest-backed local `assets/` from a secure application scheme;
- select exact trusted-origin mode where supported and controlled whole-view trust where required on Linux;
- configure generated RPC contract metadata, authorization, binding, navigation, and deterministic teardown;
- retain access to the underlying application/window/view through a context or advanced hooks;
- avoid enabling single instance, desktop renderer commands, remote navigation, DevTools in release, or other authority implicitly.

Target HelloWorld shape:

```csharp
using NeoAstra;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(AppJsonContext))]

return NeoApp.Run(args, app =>
{
    app.UseRpc(rpc => rpc.AddGreetingService(new GreetingService()));
    app.GrantMainView("greeting:read");
});
```

Exact names may be refined while compiling, but the acceptance criterion is this level of ceremony or less—not a manually authored environment/window/view/manifest/RPC/capability lifecycle sequence.

### Generated RPC metadata and code-first grants

Extend the generator/runtime contract so generated registration records the contract hash and application permission declarations in the `NeoRpcBuilder`. The high-level app builder can then construct a bounded immutable code-first capability manifest for the exact `main` view.

Rules:

- service registration remains authority-neutral;
- `GrantMainView(...)` is required before a generated command can run;
- unknown/unregistered permission IDs fail startup;
- plugin/native permissions that require typed scopes cannot be granted by an unscoped convenience overload;
- advanced/multi-view/scoped applications continue to support reviewed JSON capability manifests;
- code-first and JSON configuration resolve through the same canonical validation and authorization model;
- generated output remains deterministic and NativeAOT-safe.

### Convention-based frontend defaults

Make `neoastra.json` optional for HelloWorld/development while retaining it for overrides, advanced security policy, and delivery metadata. With one unambiguous frontend lockfile, defaults are:

- root: `frontend`;
- generated contract: `frontend/src/generated/neoastra.ts`;
- dev URL: `http://127.0.0.1:5173`;
- dev/build commands: the detected locked package manager's normal `run dev` / `run build` commands;
- dist: `frontend/dist`;
- SPA fallback: `index.html`;
- production origin: `app://neoastra`;
- existing restrictive CSP, referrer, cache, file-count, and byte limits;
- no package installation, network fetch, source-map inclusion, remote dev server, or renderer permission grant is inferred.

Ambiguous package managers, missing production output, unsafe assets, or shipping metadata operations without an explicit stable application identity must fail with an actionable diagnostic. `neoastra.json` remains available for nonstandard commands/paths, bundle identity, associations, update policy, and advanced capability files.

## Implementation checklist

- [x] **1. Establish the two-runtime-project skeleton and rename the core.** Move current `src/NeoAstra` to `src/NeoAstra.Core`, rename its project/package/assembly, preserve namespace `NeoAstra`, update native staging/codegen paths, friend assemblies, solution references, basic sample/conformance/benchmark references, loader/package identity tests, and docs. Add a new `src/NeoAstra/NeoAstra.csproj` referencing Core. Verify Core build, native asset copy, package contents, and the renamed `NeoAstra.Core.Sample`. Commit as one core-topology change.

- [x] **2. Fold RPC, Desktop, and Hosting into `NeoAstra.dll`.** Move sources/resources/schemas under `src/NeoAstra/{Rpc,Desktop,Hosting}`, remove obsolete project boundaries/IVTs, keep organized namespaces, add Hosting abstractions, merge package metadata/docs/resources, update all references/usings and combine the two NativeAOT fixtures. Delete old runtime projects/package outputs. Verify managed tests and a full-experience NativeAOT publish. Commit as one runtime-consolidation change.

- [x] **3. Embed the RPC generator in the main package.** Rename the internal analyzer project to `NeoAstra.Generator`, mark it non-packable, reference it as an analyzer for source builds, include its DLL/PDB and compiler-visible-property targets in `NeoAstra.nupkg`, remove explicit generator references from consumers, and add package-content plus clean local-feed consumer tests proving generated C#/TypeScript works from only `PackageReference Include="NeoAstra"`. Commit with generator/package tests.

- [x] **4. Consolidate build tooling without adding runtime packages.** Merge capability resolution into `NeoAstra.Tool`; fold the internal tooling library where practical; move SDK props/targets/schema payload into the main package; remove `NeoAstra.Capabilities.Tool`, `NeoAstra.Tooling`, and `NeoAstra.Sdk` package outputs/projects as applicable. Validate direct `dotnet pack`, optional `dotnet tool` pack/install, build-tool dependency resolution, no native runtime duplication in tool payload, and one-reference application restore. Use the documented build-only-tool fallback only if the direct payload creates a proven MSBuild cycle. Commit as one tooling/distribution change.

- [x] **5. Add the simple high-level app and secure code-first grant path.** Implement the high-level facade, generated contract/permission registration, code-first canonical capability builder, safe local frontend conventions, and deterministic ownership/teardown. Add unit tests for defaults, explicit-grant requirement, unknown/scoped permission denial, Linux trust policy, development-origin validation, startup failure cleanup, navigation/session teardown, and low-level escape hatches. Preserve existing advanced APIs. Commit as one developer-experience/runtime change.

- [x] **6. Simplify frontend configuration, targets, and templates.** Make `neoastra.json` optional under strict conventions, default generator outputs/build commands/asset policy, retain explicit overrides, and regenerate vanilla/React/Vue templates with one runtime PackageReference and no capability/config files for the basic greeting. Test npm/pnpm/yarn/bun lockfile detection or deterministic rejection, offline/no-install behavior, HMR orchestration, secure publish assets, stale contracts, and template drift. Commit as one tooling/template change.

- [x] **7. Replace V2 sample naming and create a clear sample ladder.** Rename the low-level sample to `NeoAstra.Core.Sample`; add/convert `NeoAstra.Sample` to the minimal typed HelloWorld matching the template; rename `NeoAstra.V2.Reference` and all application IDs/titles/docs to `NeoAstra.Sample.Advanced` with no shipping `V2`/`v2-reference` terminology. Keep the advanced feature tour comprehensive but do not use it as onboarding. Update solution, CI, delivery, scripts, manifests, assets, and README links. Commit as one sample/product-naming change.

- [x] **8. Update specifications, documentation, and release packaging.** Revise package-boundary tables and guides to distinguish Core/default/tool/template artifacts, remove instructions to install old packages, document the one-reference HelloWorld and advanced capability path, add unreleased breaking-change notes, update `dotnet-releaser`/CI pack verification, and assert that only `NeoAstra` and `NeoAstra.Core` are runtime nupkgs/assemblies. Keep the V2 audit/spec filenames only as historical implementation records and mark their old deliverable names superseded. Commit with documentation/release checks.

- [x] **9. Run the complete verification and self-review.** Run Release build/tests, frontend checks, deterministic generator/template checks, Core and full package packing, package-content/API checks, clean local-feed restore/build/run-validation, Core and full NativeAOT publishes, reference validations, native build/load smoke appropriate to the host, and `git diff --check`. Inspect the final diff for stale paths/package IDs/V2 sample names, accidental compatibility shims, duplicate shipped assemblies, and unnecessary configuration. Record target-host tests not runnable on Windows as explicit residual verification rather than passing claims.

## Verification matrix

Minimum local commands/scenarios:

```text
cd src
dotnet build -c Release
dotnet test -c Release

dotnet pack NeoAstra.Core/NeoAstra.Core.csproj -c Release --no-build
dotnet pack NeoAstra/NeoAstra.csproj -c Release --no-build
dotnet pack NeoAstra.Tool/NeoAstra.Tool.csproj -c Release --no-build
dotnet pack NeoAstra.Templates/NeoAstra.Templates.csproj -c Release --no-build

cd ../frontend
npm run check
```

Additional acceptance checks:

- A clean Core consumer references only `NeoAstra.Core`, opens packaged local HTML, and can NativeAOT publish.
- A clean HelloWorld consumer references only `NeoAstra`, receives the embedded analyzer/build assets, generates the TypeScript client, explicitly grants one permission in code, validates/publishes secure local assets, and can NativeAOT publish.
- Removing the explicit grant causes deterministic permission denial, not startup auto-grant.
- Adding `NeoAstra` does not require explicit `NeoAstra.Rpc`, `NeoAstra.Desktop`, `NeoAstra.Hosting`, generator, or SDK references.
- The main package contains `NeoAstra.dll` but not separate RPC/Desktop/Hosting runtime DLLs; Core contains `NeoAstra.Core.dll` and native assets.
- Old package IDs are absent from pack output, templates, CI, current docs, and project references.
- Tool and template packages contain no runtime library advertised as a third application framework package.
- Windows interactive advanced sample and noninteractive Core/HelloWorld/advanced validation pass locally; macOS/Linux and ARM64 execution remain target-host CI responsibilities.

## Risks and controls

- **Assembly split internals:** Main requires selected Core internals. Replace old RPC/Desktop/Hosting friend names with one `InternalsVisibleTo("NeoAstra")`; do not broaden public Core API merely to make the merge compile unless the operation is genuinely a supported extension point.
- **Analyzer packaging:** Validate both project-reference and packed-package consumption, design-time builds, generated file paths, and NativeAOT; a successful repository build alone is insufficient.
- **Tool packaging cycle:** Prototype pack order before broad moves. The allowed fallback is one build-only Tool artifact, never restoring multiple runtime feature packages.
- **Hosting dependency:** Keep Hosting abstractions usage isolated under `NeoAstra.Hosting`; verify trimming/AOT and document the dependency.
- **Convenience versus security:** The high-level API may remove files and boilerplate but must not infer renderer authority, remote trust, filesystem roots, shell/process access, or unsafe development origins.
- **Configuration defaults:** Defaults must be deterministic conventions, not heuristic network/install behavior. Ambiguity fails with a diagnostic.
- **Large move reviewability:** Use logical commits and verify after each topology/tooling/DX/sample step; do not mix Step 8/9 feature work from the V2 audit into this refactor.
