# Integrate frontend work into dotnet build

- Status: Complete
- Plan file: `.alta/plans/2026-07-30-integrated-frontend-build.md`
- Created: 2026-07-30
- Task: Make configured frontend builds incremental, framework-neutral parts of `dotnet build`/`dotnet run`, while preserving no-Node plain-JavaScript applications.
- Git: Not ignored; commit this plan with the related implementation.

## Objective
- Make ordinary `dotnet build` prepare and copy current production frontend assets so `dotnet run` works without a separate manual frontend build.
- Keep frontend frameworks, TypeScript, Node, and package managers optional; execute only the configured command and preserve the plain-JavaScript sample's no-Node path.
- Reuse prepared assets during publish instead of maintaining a separate duplicate build path.

## Context and evidence
- `src/NeoAstra/Build/Sdk/NeoAstra.Build.targets` currently verifies contracts after compile but prepares assets only before `ComputeFilesToPublish`; targets have no `Inputs`/`Outputs`.
- `src/NeoAstra.Tool/Program.cs` already executes an arbitrary configured `buildCommand`, validates assets, creates the manifest, and stages exact output; this is framework-neutral.
- `samples/NeoAstra.Sample` deliberately has plain browser assets and must remain runnable without Node. `samples/NeoAstra.Sample.Advanced` currently opts into checked prebuilt assets and does not demonstrate automatic frontend rebuilding.
- The user prefers a packaged MSBuild task where that is the cleanest way to resolve inputs/fingerprints and incremental state.

## Assumptions and open decisions
- Resolved: frontend building is enabled by default only when frontend integration is detected/configured; a narrow property must disable command execution without disabling generated RPC contracts.
- Resolved: dependency installation remains explicit and is not run implicitly by `dotnet build`.
- Resolved: no frontend technology is hard-coded; conventions may recognize package projects, while explicit configuration can run any bounded command or use prebuilt/plain assets.
- Resolved: no browser/window-opening tests.

## Design notes
- Split preparation from copying: compile/generate contracts, resolve/fingerprint frontend inputs, build and stage only when stale, then copy exact staged assets to normal output; publish consumes the same staging.
- Prefer a packaged MSBuild task for configuration resolution/fingerprinting if it avoids fragile JSON parsing and deletion-blind timestamp globs. The fingerprint must include config, command/environment, generated contract, frontend source/static/config files, package metadata/lockfile, and tool version while excluding dependencies/output/intermediates; allow additional MSBuild inputs.
- Guard design-time, cross-targeting, and development-server/watch scenarios. `dotnet neoastra dev` must disable production frontend building for its contract/backend builds.
- Preserve existing prebuilt validation mode. Keep the no-Node sample outside frontend command detection; do not require TypeScript or remove its optional generated JavaScript binding in this task unless evidence supports a narrower cleanup.
- Ensure rebuilding replaces exact staged/output assets so stale hashed bundles are not retained.

## Risks and challenges
- MSBuild target ordering and package import behavior must work for build, run, publish, clean, source-tree samples, and packaged consumers.
- Input deletion, external/additional inputs, dynamic output names, configuration changes, and parallel target/RID builds require deterministic fingerprint/stamp handling.
- Do not run frontend commands during IDE design-time builds or duplicate production builds under `dotnet neoastra dev`.
- Preserve the user's uncommitted `samples/NeoAstra.Sample/Program.cs` change and exclude it from the commit.

## Implementation checklist
- [x] Inspect package layout, target import order, tool/config APIs, clean/output conventions, and existing test infrastructure; choose the smallest reliable MSBuild-task packaging design.
- [x] Add framework-neutral properties/items for default build integration, narrow opt-out, additional inputs, intermediate fingerprint/staging, and development/design-time guards.
- [x] Implement deterministic frontend configuration/input fingerprinting with reliable addition/change/deletion detection and content-stable state writes.
- [x] Refactor targets so normal Build prepares current frontend assets after contract generation and copies exact assets to `$(OutDir)`, while Publish reuses the same staged output.
- [x] Keep prebuilt and no-frontend/plain-JavaScript paths working without Node or package installation.
- [x] Update `dotnet neoastra dev` defaults/environment so HMR/watch builds skip production frontend commands.
- [x] Add browser-free package/MSBuild integration tests proving first-build execution, unchanged skip, source/config/contract/addition/deletion reruns, opt-out, clean/rebuild, normal output assets, publish reuse, prebuilt behavior, and no-frontend behavior.
- [x] Update primary/advanced samples only as needed to demonstrate normal integrated behavior without making any framework mandatory.
- [x] Update `readme.md`, `doc/frontend-tooling-and-assets.md`, RPC/build property docs, and templates/config schema if behavior or public properties change.
- [x] Audit generated/debug/experimental files, preserve but do not commit the user's `Program.cs` logging change, and create one focused autolabel-compliant commit.

## Verification checklist
- [x] Run focused MSBuild/task/tooling tests without opening a browser.
- [x] Run representative clean `dotnet build` twice and prove the configured frontend command runs once, then reruns for each relevant invalidation case.
- [x] Verify a plain-JavaScript/no-Node project builds without invoking Node.
- [x] Run `dotnet build -c Release` and `dotnet test -c Release --no-build` from `src`.
- [x] Run relevant frontend fixture checks only when existing dependencies are already available; do not install dependencies.
- [x] Run `git diff --check`, inspect staged diff/status, commit, and report the full hash plus any preserved uncommitted user change.

## Handoff notes
- The user explicitly approved implementation and requested a new commit.
- Start from commit `4e15985f1984ff71bdca4b9314e8ca9e628d6dc0`; the only expected pre-existing dirty file is the user's `samples/NeoAstra.Sample/Program.cs` Console.WriteLine, which must remain uncommitted.
- Do not optimize only for Vite/React/TypeScript/npm. The configured command and asset directory are the abstraction; no command means no frontend toolchain requirement.

## Execution notes
- Used a small `dotnet neoastra frontend fingerprint` command rather than adding Microsoft.Build assembly dependencies to the packaged tool. This keeps strict configuration resolution and file validation in the existing framework-neutral tool while native MSBuild `Inputs`/`Outputs` provide target skipping.
- Browser-free integration coverage uses `dotnet msbuild` as the configured frontend command with `packageManager: none`, proving the configured path does not require Node.
- Release build passed twice (the second skipped asset preparation), and all 162 tests passed. Dependency-backed npm fixtures were not run because `node_modules` was absent; no dependencies were installed.
