# Window management and chromeless interaction

## Evaluation

The existing window layer already has a strong application-owned lifetime model, deterministic identifiers and ownership, cancelable asynchronous closing, show/hide/activate/close operations, mutable bounds and constraints, startup decoration/resizing/topmost/taskbar settings, a unified native-driven state model, typed native handles, and aggregate bounds/focus/state events.

The main experience gaps are ergonomic rather than architectural:

- callers must decode aggregate focus, bounds, and state events for common transitions;
- common state changes require assigning an enum instead of discoverable command methods;
- creation-time window behavior such as decorations, resizing, topmost status, and taskbar presence cannot be changed later;
- custom title bars have no portable native drag/resize entry points;
- Linux does not currently route native move/focus notifications through the event model;
- the public guide describes capabilities but does not provide a focused window-management example or platform caveats.

This increment will preserve the existing APIs and native-driven source of truth. It will add convenience APIs and events rather than replace the aggregate forms, use one typed native attribute ABI to avoid repetitive exports, and return an explicit unsupported error where a platform cannot implement a native interaction safely.

## Scope and design

- Add `Activated`/`Deactivated`, `PositionChanged`/`ClientSizeChanged`, and state-transition convenience events while retaining `FocusChanged`, `BoundsChanged`, and `StateChanged`.
- Add `Maximize()`, `Minimize()`, `Restore()`, `EnterFullScreen()`, `ExitFullScreen()`, and `BringToFront()` commands over the existing state/activation primitives.
- Add mutable `HasDecorations`, `IsResizable`, `IsAlwaysOnTop`, and `ShowInTaskbar` properties backed by native get/set attribute functions.
- Add `BeginDrag()` and `BeginResize(NeoWindowResizeEdge)` for chromeless pointer interactions. Support only native, user-initiated interactions; do not synthesize global mouse input or expose these operations to renderer code automatically.
- Improve Linux move/focus routing and document compositor limitations. On any unsupported interaction, surface `NotSupportedException` through the existing native error mapping.
- Keep ABI additions append-only and update generated interop/export checks.

## Execution checklist

- [x] Add regression-oriented managed tests for event fan-out, command validation/routing helpers, and option/edge validation.
- [x] Extend the native ABI and all three backends with mutable attributes and chromeless drag/resize operations, including ABI/export tests.
- [x] Regenerate interop and expose documented managed properties, commands, resize-edge types, and convenience events.
- [x] Route missing Linux move/focus notifications and verify duplicate transition suppression remains intact.
- [x] Update the main guide and platform limitations with window-management and custom-title-bar guidance.
- [x] Run native build/tests, managed build/tests, inspect the final diff, and commit the focused change.

## Validation

- Windows x64 native build and ABI/common/stress tests pass with the pre-release ABI kept at `1.0`.
- The full managed test project passes both with the checked runtime mismatch path and with the newly built native library selected explicitly.
- macOS and Linux backend compilation and interactive compositor behavior remain CI/target-host validation; their implementations were reviewed locally but cannot be executed on this Windows host.
