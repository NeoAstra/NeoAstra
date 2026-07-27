# Delivery and authenticated updates

`dotnet neoastra bundle` is a thin, inspectable release orchestrator. It never restores dependencies,
downloads toolchains, invents platform dependencies, reads a signing secret from project metadata, or
claims that a cross-host artifact passed target-host qualification. The project configuration is frozen
by [`neoastra-project-v1.schema.json`](../schemas/neoastra-project-v1.schema.json); deterministic staging
and signed feed formats are frozen separately by `neoastra-staging-manifest-v1.schema.json` and
`neoastra-update-manifest-v1.schema.json`.

## Bundle metadata and command

The `bundle` object repeats the stable `app.identifier` and `app.displayName` deliberately. Runtime
validation requires exact equality because an identity change affects single-instance routing, data and
safe-storage namespaces, notification delivery, package upgrades, and update compatibility. It also
requires semantic and platform numeric versions, publisher, executable, source icons, explicit RIDs,
artifact targets, an allowlist of publish-relative files, notices, minimum OS, and reviewed runtime
dependencies. Optional associations, protocols, notification identity, symbols, entitlements, and update
policy are bounded and reject unknown fields. Association and protocol declarations must match Step 5
`OpenFiles`/`OpenUrls` launch handlers; `doctor` emits a prominent review finding because tooling cannot
prove application event-handler behavior statically.

`eng/build_native.py` stages `neoastra-native.json` beside each native library. Include both files in the
publish allowlist: bundling verifies the sidecar RID, exact managed ABI, binary name, SHA-256, and machine
architecture, so a stale or substituted native runtime fails before packaging even during cross-host work.

Run after locked restore, release frontend asset generation, and NativeAOT publish:

```sh
dotnet neoastra bundle --config neoastra.json --rid win-x64 \
  --publish artifacts/publish --assets-manifest obj/Release/neoastra/assets/neoastra-assets.json \
  --output artifacts/bundle
```

`--dry-run` validates metadata, files, frontend manifest, ABI/RID pairing, icons, notices, release bounds,
forbidden development files, links/reparse points, path traversal, and portable case/Unicode collisions.
It retains generated platform inputs and a redacted argument-array command plan without creating a
portable archive, signing, or invoking an installer. Normal mode stages only declared files in a private
random directory, emits a sorted hash/mode/component manifest, creates a deterministic archive, verifies
every archived path and digest against that manifest, and removes only the owned temporary directory.

Outputs include `SHA256SUMS`, SPDX 2.3 and CycloneDX 1.6 SBOMs, copied notices, provenance bound to the
staging/artifact digest, redacted support metadata, and explicit symbols metadata when selected. The
checksum file never includes itself. `inspect/` retains the command plan, source icon, staging manifest,
and generated package inputs.

## Platform formats and qualification

The generator supports these deliberately narrow paths:

| Host | Portable | Reviewed installer input/tool | Runtime policy |
| --- | --- | --- | --- |
| Windows | deterministic zip | MSIX `AppxManifest.xml`; `makeappx` | Evergreen WebView2 detection; fixed runtime is never added implicitly |
| macOS | `.app` hierarchy in deterministic zip; DMG plan | PKG through `pkgbuild` | system WKWebView; explicit hardened-runtime entitlements only |
| Linux | deterministic `tar.gz`, desktop entry and shared MIME XML | deb control/root through `dpkg-deb` | declared GTK/WebKitGTK dependencies; no guessed universal distro policy |

macOS also retains DMG commands. Windows generated MSIX extensions use structured XML, never an unquoted
protocol command template. Linux desktop `Exec` contains only the fixed executable plus `%U`; maintainer
scripts are not generated. Source icons are retained; NeoAstra does not download converters, and a
platform-required conversion/size failure must be resolved with an installed reviewed offline tool before
packaging.

Generation is not qualification. Provenance always records `hostQualified: false`; qualification evidence
is a separate target-host CI artifact and cannot be inferred from the machine that generated a bundle.
Installer support remains **generated/experimental** until the target CI job builds,
installs, launches, exercises file/URL/notification identity, upgrades, repairs where applicable,
uninstalls, verifies user-data policy, and uploads retained inputs/evidence. Gatekeeper, quarantine,
SmartScreen/reputation, Wayland/X11, desktop notification, keychain/credential-service, and distro runtime
behavior cannot be inferred on another OS.

## Signing and notarization

Unsigned is the default and provenance labels it unsigned. Signing requires both `--sign` and
`--signing-identity-env NAME`; configuration accepts no credential value. The named environment value is
passed directly to a platform executable using an argument array and is redacted from retained plans.
Windows requires a reviewed HTTPS timestamp reference in `NEOASTRA_TIMESTAMP_URL` and plans Authenticode
SHA-256 plus `signtool verify`; macOS plans inside-out
`codesign`, explicit entitlements/hardened runtime, `notarytool`, stapling/Gatekeeper verification; Linux
plans a detached GPG signature and verification. Tooling never creates response files, echoes tool output
that may contain credential metadata, downloads a signer, or stores passwords/tokens. CI should use OS
key stores/hardware/secret variables and retain only public fingerprint/notarization evidence. Real
signing/notarization is intentionally absent from repository CI.

## Update threat model and state machine

Self-update is **unavailable by default** and can only be configured as `disabled`, `experimental`, or
`store`. There is intentionally no `available` configuration value. Store installations expose
store-managed mode and deny self-install. Experimental status must not be converted to release support
until every artifact's hostile-server, interruption, clean-host, health, and rollback matrix passes.

The canonical manifest uses sorted UTF-8 JSON properties, integers only, duplicate rejection, a 1 MiB and
4,096-node bound, schema/version fail-closed behavior, exact app/channel/RID/format matching, four-part
versions/build, allowed upgrade range, minimum updater version, UTC release time, rollout percentage,
artifact byte length/SHA-256/signature, and one pinned ECDSA P-256 signing key ID. A manifest signature is
over the canonical document without `signature`; the artifact signature is independently over its
SHA-256 digest. Rotation pins old and new public keys in reviewed application policy before use.
Revocation is local signed-release policy: a revoked or unknown key fails before artifact selection. Never
remove the last trusted non-revoked key without shipping a prior authenticated transition.
Staged rollout requires a bounded backend-owned stable installation identity (normally derived from the
Step 6 safe-storage boundary) and uses a deterministic SHA-256 cohort; renderer input cannot select it.

HTTPS is mandatory but not authentication. Feed, release-note, artifact, and at most three redirects must
use HTTPS, no credentials/fragments, and the exact IDN host/effective port pinned by the application.
Transport decompression and cookies are disabled. Downloads stream to a private backend-owned directory,
check advertised and signed lengths, enforce a 2 GiB policy bound, hash while writing, verify both digest
and signature, and only then rename the temporary file. Cancellation is safe during download; temporary
bytes are deleted. Renderer code cannot provide a URL, key, channel, RID, version, path, helper, command,
or rollback target.

Authenticated portable extraction accepts only zip or `tar.gz`, rechecks the artifact digest, rejects
links/special files/traversal/portable path collisions, caps file count and expanded bytes, verifies the
single packaged app/version/RID identity root, and writes only to an empty backend-owned root. Native installer formats are never parsed as portable archives.

The backend coordinates an ordinary Step 5 quit before handing a canonical state file to the minimal
per-OS helper. App refusal or timeout leaves the authenticated artifact staged and performs no
replacement. Installation accepts only backend-owned absolute non-root paths, refuses linked payload
roots, renames the current install to a fixed previous path, atomically promotes a separately verified
payload, and restores the previous directory on an interrupted promotion. Windows helpers must additionally
reject reparse points/locked executables; macOS helpers replace the complete same-identity `.app`; Linux
helpers replace the app-owned portable directory or defer deb updates to the package manager. Package
identity/signature verification with `signtool`/`codesign`/`dpkg-deb` is required before extraction or
promotion in host integration.

A new launch changes `installed` to `healthy` only after backend readiness and data migration confirmation.
A timeout requests rollback only to the fixed previously authenticated directory. Two pending failures stop
the loop. Healthy completion removes previous/temp state; rollback preserves a bounded diagnostic state.
Arbitrary downgrade remains denied except an explicitly configured non-stable development channel with a
reviewed data policy.

## Renderer surface

`createUpdateClient` exposes only status, check, download, changed progress, and install/restart commands.
Status is a safe DTO. Check/download require explicit capabilities; install/restart is high risk and also
requires trusted backend/user confirmation. The deny-by-default `createMockUpdates` frontend harness has
no implicit success result. Capability diagnostics should report `unavailable`, `experimental`, or
`store-managed` together with notification/safe-storage support, never infer updater availability from a
configured feed.

## Verification responsibility

Repository unit tests cover strict/deterministic staging, forbidden and collision handling, canonical
signature, key rotation/revocation, store and staged-rollout policy, wrong app/channel/RID,
replay/downgrade, URL/size policy, authenticated extraction traversal, atomic promotion, interrupted-switch
recovery, health timeout, rollback, and loop prevention. Frontend tests cover fixed argument-free commands and
deny-by-default mocks. CI matrix jobs must perform host-native portable/package smoke, install,
association/protocol launch routing, upgrade, repair/equivalent, rollback interruption, uninstall, and
launch. A job must not label a format qualified when signing credentials are absent, desktop services are
unavailable, or a target-host check is skipped.
