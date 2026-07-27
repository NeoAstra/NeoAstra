# Step 7 — Bundling, Signing, Distribution, and Updates

**Parent:** [`neoastra_v2_specs.md`](../neoastra_v2_specs.md)
**Depends on:** [Step 4](04-frontend-tooling-and-assets.md), [Step 5](05-application-lifecycle-and-hosting.md), [Step 6](06-plugins-and-desktop-services.md)
**Outcome:** NeoAstra applications produce inspectable native bundles/installers and can update only through authenticated, rollback-aware workflows.

## 1. Scope

NeoAstra.Bundle is a thin orchestrator over established platform tools. It validates metadata, stages deterministic app payloads, generates inspectable platform inputs/commands, invokes signing/notarization when explicitly requested, and emits provenance/checksum/update metadata. It MUST NOT hide credentials, silently download toolchains, or claim cross-platform artifacts not built/tested on their host OS.

Updater support is security-sensitive and SHALL remain experimental/unavailable until its threat model and negative tests pass.

## 2. Bundle configuration

Project metadata extends the Step 4 configuration:

```json
{
  "bundle": {
    "identifier": "com.acme.notes",
    "displayName": "Acme Notes",
    "version": "1.2.3",
    "publisher": "Acme, Inc.",
    "copyright": "...",
    "icons": ["assets/icon.svg"],
    "fileAssociations": [{ "extension": ".acmenote", "role": "editor" }],
    "urlSchemes": ["acmenotes"],
    "targets": ["portable", "installer"]
  }
}
```

Required metadata includes stable identifier, display/product name, semantic/display version plus platform-mapped numeric versions, publisher, executable identity, icons, supported RIDs, minimum OS/runtime dependencies, licenses/notices, and selected artifact formats. Optional metadata includes URL schemes, file/MIME associations, autostart declarations, notification identity, requested entitlements/capabilities, store metadata, and update channel/feed.

Configuration is schema-validated. Identifiers and versions are normalized once and checked against each platform's restrictions. Changes affecting app identity, update compatibility, credentials, data directories, or single-instance identity produce prominent diagnostics.

## 3. Build pipeline

The bundle command SHALL execute stages with explicit inputs/outputs:

1. validate configuration and host/tool prerequisites;
2. run `dotnet publish` for one RID/configuration, normally self-contained/NativeAOT according to project policy;
3. verify managed/native ABI pairing and package/native asset identity;
4. verify frontend asset manifest/hash and release security profile;
5. collect only declared publish files, plugin assets, notices, symbols policy, and runtime dependencies;
6. scan for forbidden development files/settings and case/path collisions;
7. create deterministic staging manifest with hashes, mode/metadata, and component versions;
8. generate platform bundle/installer source/configuration;
9. invoke platform tools using argument arrays and recorded versions;
10. sign/notarize only when explicitly configured;
11. verify final signature/package structure by platform-native verification tools;
12. emit checksums, SBOM/provenance, support metadata, and optional signed update manifest;
13. run install/launch/uninstall or portable smoke tests on appropriate CI hosts.

Every stage logs an inspectable command with secrets redacted. `--dry-run` emits the plan/generated configuration without signing or artifact mutation. Temporary directories use restrictive permissions and are cleaned safely.

## 4. Artifact support

Initial official formats SHOULD be deliberately narrow:

| Platform | Initial | Later/conditional |
| --- | --- | --- |
| Windows | portable zip; one reviewed installer path (MSIX, MSI/WiX, or NSIS selected by prototype) | additional installer/store formats |
| macOS | `.app` plus zip/DMG | PKG/Mac App Store profiles |
| Linux | tar archive plus desktop/MIME metadata; one of AppImage or deb after runtime tests | rpm and additional distro formats |

The tool MAY generate configurations for other formats before they are release-qualified, but documentation MUST distinguish generated/experimental from install-tested support.

Cross-compilation may produce a raw binary where supported; signing, notarization, packaging, and qualification run on the target host OS. CI uses a per-host matrix.

## 5. Windows requirements

- correct PE architecture and `neoastra_native.dll` pairing;
- app identifier, version, icon, publisher, protocol/file associations, and notification identity mapped to selected format;
- Authenticode signing and timestamping with signature verification;
- WebView2 runtime policy: Evergreen detection by default, documented bootstrap/install behavior, optional fixed runtime only when explicitly packaged/licensed;
- installer per-user/per-machine elevation behavior explicit;
- upgrade/downgrade, install path, data path, shortcuts, repair/uninstall behavior tested;
- no unquoted executable paths or unsafe protocol command templates;
- Windows Defender/SmartScreen reputation limitations documented without bypass advice.

## 6. macOS requirements

- correct `.app` hierarchy, executable/RID architecture, `Info.plist`, icons, bundle identifier/version, URL/document types;
- universal binaries built from separately verified x64/ARM64 inputs when supported;
- explicit entitlements and hardened runtime; no broad entitlement added implicitly;
- nested binaries/frameworks signed inside-out with identity verification;
- notarization and stapling through Apple tools when requested;
- Gatekeeper assessment and launch smoke test on a clean host;
- DMG/PKG metadata and update behavior preserve bundle identity and quarantine expectations;
- Keychain/accessibility/notification features document signing identity requirements.

## 7. Linux requirements

- supported distro/runtime dependency policy for GTK/WebKitGTK and clear startup diagnostics;
- desktop entry, icons, MIME associations, URL schemes, categories, executable quoting, and install locations validated;
- package dependencies generated conservatively and inspectably, not guessed across all distributions;
- Wayland/X11 and secret-service/notification/tray dependencies documented by feature;
- package maintainer scripts minimized and reviewed;
- install/uninstall leaves user data according to documented policy;
- AppImage or other portable format does not claim to bundle/standardize unsupported WebKitGTK combinations without tests.

## 8. Icons, resources, and generated metadata

Tooling MAY convert a high-quality source icon into platform formats. It MUST preserve source, list generated sizes/formats, avoid network dependencies, and produce deterministic output where encoders allow. Invalid/missing required icon sizes fail before packaging.

Generated plist/manifests/desktop entries/installer scripts are retained in an artifact or inspect directory for review. User override fragments are schema/structure validated; raw script hooks are opt-in high-risk extensions and are not executed during normal build without explicit configuration.

## 9. Signing credentials

NeoAstra MUST NOT store private keys, certificate passwords, notarization credentials, or tokens in project configuration/artifacts/logs. It accepts references to OS key stores, files, hardware providers, or CI secret environment variables according to platform tools.

Requirements:

- secrets passed through the safest supported channel and redacted;
- no secret values in command echo, response files, diagnostics, crash reports, or update manifest;
- signing identity and certificate/public-key fingerprint recorded;
- timestamp/notarization responses retained as non-secret provenance where useful;
- unsigned output is labeled unsigned and cannot accidentally publish to a “signed” channel.

## 10. SBOM, provenance, symbols, and support data

Every release SHALL emit:

- SHA-256 checksum manifest for artifacts;
- component/version list for NeoAstra managed/native, plugins, .NET runtime mode, frontend asset hash, and platform WebView policy;
- SPDX or CycloneDX SBOM when tooling is available and validated;
- source commit/build identity and reproducibility metadata;
- third-party notices/license files;
- symbols/source mapping according to release policy, separately packaged when appropriate;
- redacted diagnostic/support metadata usable by the application.

Checksums MUST not hash themselves incorrectly and manifests are sorted/canonical. Published provenance signs or binds artifact digests, not mutable filenames alone.

## 11. Update architecture

### 11.1 Threat model

The updater MUST defend against malicious feed/server, TLS interception, artifact substitution, compromised old signing key, replay/downgrade, wrong app/channel/architecture, partial download, disk exhaustion, path traversal, symlink/reparse attacks, process races, interrupted replacement, rollback loops, and untrusted renderer-triggered updates.

TLS is REQUIRED but not sufficient. Every manifest and/or artifact is authenticated by an application-pinned update signing key independent from transport. Key rotation and revocation require an explicit signed transition policy.

### 11.2 Signed manifest

A canonical versioned manifest contains:

- schema version, application identifier, channel, version/build, release timestamp;
- minimum updater/app version and allowed upgrade range;
- per-platform/RID artifact URL, byte length, SHA-256, format, and signature;
- rollout/staging metadata that cannot bypass version/signature checks;
- optional release notes URL/text with safe rendering policy;
- signing key ID and signature over canonical bytes.

Unknown critical fields/schema versions fail closed. URL redirects follow strict HTTPS/host policy. Manifest parsing is bounded.

### 11.3 Client flow

1. application or trusted policy checks configured feed;
2. validate TLS/URL policy, download bounded manifest;
3. verify signature/canonical schema before using fields;
4. validate app ID/channel/platform/RID/version and downgrade rules;
5. report availability to backend UI; renderer receives only granted safe DTOs;
6. download to an app-owned temporary location with length/hash/signature verification;
7. validate package signature/identity where applicable;
8. coordinate normal quit through Step 5 and persist handoff state;
9. install atomically using a minimal platform helper where necessary;
10. verify new launch/health marker;
11. clean old/temp data or rollback according to policy;
12. prevent repeated failed-update loops and retain safe diagnostics.

Download/install operations are cancelable only at documented safe stages. No replacement occurs while bytes remain unauthenticated.

### 11.4 Rollback and downgrade

Rollback is allowed only to a previously authenticated artifact selected by trusted policy after failed health confirmation. It cannot be requested with arbitrary renderer paths/versions. Normal downgrade is denied unless explicitly configured for a development/test channel and compatible data migration policy.

Store-managed installations disable or adapt self-update according to store rules. Capability discovery reports update mode.

## 12. Renderer access

Frontend may query safe update status, request an allowed check/download, and display progress only through explicit permissions. Install/restart requires a high-risk permission and normally a trusted backend/user-confirmed flow. Feed URL, key, artifact path, command, or helper executable are never renderer-supplied under normal permissions.

Deep-link/file-association/autostart metadata integrates with Step 5 launch routing and Step 6 plugins; packaging declarations and runtime handlers MUST agree, with `doctor` diagnostics for mismatches.

## 13. Implementation order

- [x] Freeze bundle metadata schema and deterministic staging manifest.
- [x] Implement host/tool prerequisite checks, dry-run, inspectable commands, and redaction.
- [x] Implement raw portable bundles and package-content verification per OS.
- [x] Select and implement one installer path per claimed platform, with generated source retained.
- [x] Add signing/notarization and post-sign verification adapters.
- [x] Add icons/associations/entitlements/dependency metadata and `doctor` checks.
- [x] Emit checksums, SBOM/notices/provenance/symbol/support artifacts.
- [x] Build installer smoke-test CI on clean platform hosts.
- [x] Complete updater threat model and canonical signed manifest prototype.
- [ ] Implement download/verify/handoff/atomic install/health/rollback separately per OS. (The bounded portable installer/state machine and native-package deferral are implemented and unit-tested; signed target-host interruption/rollback qualification remains required before this item can be checked.)
- [x] Add channel/staging/key-rotation/store restrictions only after core negative tests pass.

> Qualification remains intentionally separate from implementation: unsigned portable/package smoke runs on each
> target host, while signed upgrade/downgrade, notarization, association delivery, updater interruption, and
> artifact-specific rollback evidence remain unclaimed until those CI scenarios execute successfully.

## 14. Verification

Repository bundle tests currently cover strict identity and declared-file policy, forbidden/colliding paths,
wrong native ABI, deterministic staging/archive hashes, retained platform association/protocol input, metadata
emission, and redaction boundaries. The target-host workflow is responsible for the unsigned NativeAOT
portable/package build, structure inspection, install, upgrade, repair-equivalent reinstall, launch, and
uninstall exercises; those jobs are not considered passed until they actually execute.

Updater unit tests currently cover canonical signature failure, unknown/revoked/rotated keys,
replay/downgrade/wrong-app/channel/RID, URL and manifest-size policy, deterministic rollout, store disablement,
portable traversal and package-identity mismatch, atomic promotion, interrupted-switch recovery, health
timeout, rollback, rollback-loop prevention, and renderer scope denial. Hostile-server redirects/truncation,
disk-full/locked-file/app-refusing-quit, signed native-package interruption, restart health, and artifact-specific
rollback remain required target-host qualification evidence; updater mode therefore remains unavailable or
experimental rather than `available`.

## 15. Exit criteria

Each claimed artifact is built and smoke-tested on its host OS, has inspectable generated packaging inputs, validated signature when configured, checksums/SBOM/notices/provenance, and accurate runtime dependencies. Updater support is advertised only after authenticated manifest/artifact, atomic replacement, restart, interruption, downgrade, health, and rollback tests pass for that artifact type.
