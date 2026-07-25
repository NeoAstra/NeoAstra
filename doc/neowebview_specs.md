# NeoWebView Implementation Specification

**Status:** Draft 0.2
**Date:** July 25, 2026
**Intended audience:** NeoWebView implementers and contributors

---

# 1. Purpose

NeoWebView is a lightweight, cross-platform framework for building desktop applications whose user interface is implemented with web technologies and rendered using the operating system’s installed web engine.

NeoWebView shall:

* Use WebView2 on Windows.
* Use WKWebView/WebKit on macOS.
* Use WebKitGTK on Linux.
* Avoid bundling Chromium.
* Avoid requiring Node.js at runtime.
* Support applications written in modern .NET, including NativeAOT.
* Expose a stable native ABI with C linkage and the C calling convention.
* Generate the low-level C# interop layer from the authoritative C++ header using CppAst.CodeGen.
* Provide an ergonomic, high-performance managed API above the generated interop.
* Provide a first-class standalone windowing model for web-first applications, including multiple windows and controlled popup creation.
* Embed into existing native windows through explicit platform handle types such as Win32 `HWND`.

The default windowing layer is a core product feature, not only a sample helper. It MUST be sufficient for a complex web-first desktop application to manage its application loop, top-level windows, owned windows, popups, focus, placement, state and shutdown without a third-party managed windowing framework. It is not intended to become a general native widget toolkit; application controls remain implemented primarily with web technologies.

The project is not intended to reproduce every WebView2, WKWebView, or WebKitGTK API. WebView2 alone contains hundreds of APIs across browser integration, messaging, permissions, downloads, profiles, printing, networking and diagnostics. NeoWebView shall instead expose stable cross-platform concepts, capability discovery, and optional native extensions.

# 2. Normative terminology

The terms **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT**, and **MAY** describe implementation requirements.

* **Portable API:** Functionality with defined semantics on all supported platforms.
* **Capability:** Functionality that may be unavailable, limited, or emulated on a particular backend.
* **Backend:** The platform-specific implementation using WebView2, WKWebView, or WebKitGTK.
* **UI thread:** The thread owning the operating system GUI event loop and native WebView objects.
* **Environment:** A browser-process and configuration scope.
* **Profile:** A persistent or ephemeral browsing-data scope.
* **View:** A single embedded browser instance.
* **Application:** The native UI event-loop owner.
* **Window:** A NeoWebView-owned top-level native window.
* **Popup:** A new window and view requested by web content and explicitly accepted by the application.
* **Embedded host:** A caller-owned native container into which NeoWebView attaches a view.

---

# 3. Goals

## 3.1 Primary goals

NeoWebView MUST provide:

1. A stable C ABI consumable by any language.
2. A generated low-level C# interop layer.
3. A manually designed high-level C# API.
4. NativeAOT compatibility.
5. UTF-8 throughout the native API.
6. Asynchronous operations that map cleanly to `Task`, `Task<T>`, `ValueTask`, and cancellation.
7. First-class native windowing for multi-window web-first applications.
8. Embedding into an existing native parent.
9. Navigation and navigation-policy handling.
10. JavaScript execution.
11. Document-start script injection.
12. Bidirectional web/native messaging.
13. Secure loading of packaged application assets.
14. Browser profiles and private sessions.
15. Downloads, permissions, dialogs, and file-selection requests.
16. Explicit capability discovery.
17. Predictable threading and lifetime semantics.
18. Actionable runtime diagnostics.
19. Cross-platform integration tests.
20. A small deployment footprint excluding the system web engine.
21. Explicitly typed integration with existing native hosts such as Win32 `HWND`, Cocoa `NSView`, and GTK `GtkWidget`.

## 3.2 Secondary goals

NeoWebView SHOULD:

* Support multiple views in the same application.
* Support multiple profiles.
* Support applications written without ASP.NET Core.
* Support plain HTML, CSS and JavaScript without a frontend build tool.
* Allow optional use with Blazor, React, Vue, Svelte, or other frontend technologies.
* Allow a managed asset provider to serve embedded or generated content.
* Provide a TypeScript/JavaScript client library for the normalized messaging API.
* Permit advanced users to access backend-native handles and extension APIs.

---

# 4. Non-goals

NeoWebView v1 MUST NOT attempt to provide:

* A bundled Chromium runtime.
* A complete web browser.
* Pixel-identical rendering across platforms.
* Exact feature parity among WebView2, WKWebView, and WebKitGTK.
* Mobile platform support.
* Offscreen rendering.
* Browser-extension support.
* A DOM API exposed directly to C#.
* A mandatory frontend framework.
* A mandatory local HTTP server.
* A mandatory Node.js toolchain.
* Automatic application updates.
* A general-purpose native desktop widget framework. The default top-level windowing and popup layer remains in scope.
* Portable arbitrary HTTP/HTTPS subresource replacement.
* Portable Chrome DevTools Protocol support.
* WebView2 composition-controller support in the portable API.
* Guaranteed Alpine/musl support in the initial release.

Native menus, tray icons, notifications, drag-and-drop helpers and installer generation are also outside the initial core scope.

---

# 5. Supported platforms

## 5.1 Initial support matrix

| Platform      | Architecture | Backend       | Initial status |
| ------------- | -----------: | ------------- | -------------- |
| Windows 10/11 |          x64 | WebView2      | Required       |
| Windows 10/11 |        ARM64 | WebView2      | Required       |
| macOS         |          x64 | WKWebView     | Required       |
| macOS         |        ARM64 | WKWebView     | Required       |
| Ubuntu 22.04+ |          x64 | WebKitGTK 4.1 | Required       |
| Ubuntu 22.04+ |        ARM64 | WebKitGTK 4.1 | Required       |
| Linux musl    |    x64/ARM64 | WebKitGTK     | Future target  |

Thirty-two-bit architectures are not supported in v1.

## 5.2 Linux backend selection

The initial Linux backend MUST use:

* WebKitGTK API 4.1.
* GTK 3.
* libsoup 3.
* The GLib main loop.

WebKitGTK 4.1 is available on Ubuntu 22.04 and later. WebKitGTK 6.0 uses GTK 4 and officially supersedes the older GTK 3 API variants, but requiring it initially would reduce compatibility with existing distributions. The Linux implementation MUST therefore isolate all GTK/WebKitGTK code behind a backend boundary so that a WebKitGTK 6.0 implementation can be added later. It MUST NOT expose GTK-version-specific structures.

## 5.3 Musl constraints

The common native implementation MUST avoid unnecessary glibc-specific APIs.

Musl support shall be considered during initial architecture, but it is not accepted as supported until:

* `linux-musl-x64` and `linux-musl-arm64` native libraries build successfully.
* Compatible GTK and WebKitGTK packages are available on the target distribution.
* NativeAOT applications pass the integration suite.
* Runtime dependencies and deployment instructions are documented.
* CI executes against an actual musl environment.

Musl support MUST be represented as unsupported rather than experimental in released packages until those conditions are met.

---

# 6. Product architecture

NeoWebView consists of four logical layers:

```text
Application code
    ↓
NeoWebView managed API
    ↓
Generated C# interop
    ↓
Stable NeoWebView C ABI
    ↓
Platform backend
    ├── WebView2 / Win32
    ├── WKWebView / Cocoa
    └── WebKitGTK / GTK
```

## 6.1 Native library

The native library shall be named:

| Platform | Filename                      |
| -------- | ----------------------------- |
| Windows  | `neowebview_native.dll`      |
| macOS    | `libneowebview_native.dylib` |
| Linux    | `libneowebview_native.so`    |

The basename `neowebview_native` MUST be used consistently by CMake, generated interop, development builds, tests and packages. In particular, the Windows native library MUST NOT be named `NeoWebView.dll` or `neowebview.dll`, because Windows filenames are case-insensitive and that name conflicts with the managed `NeoWebView.dll` assembly.

The library MUST expose only symbols with C linkage.

The implementation MAY use:

* C++ and COM on Windows.
* Objective-C++ and Cocoa on macOS.
* C or C++ with GTK/GObject on Linux.

No C++ class, reference, template, Objective-C, COM, GTK, GLib, or platform SDK type may appear in exported portable function signatures or structures. Fixed-underlying-type C++ enums SHOULD be used for all ABI enumerations because CppAst.CodeGen preserves their exact storage type.

## 6.2 Managed assembly and tooling

NeoWebView MUST ship one managed runtime assembly: `NeoWebView.dll`. Generated interop and manually maintained interop helpers MUST compile into that assembly and MUST NOT be published as separate `NeoWebView.Interop` or `NeoWebView.Interop.Generated` assemblies.

The initial managed solution SHOULD contain these projects:

```text
NeoWebView                    shipped runtime assembly
NeoWebView.CodeGen            development-time console tool
NeoWebView.Tests              managed unit tests
NeoWebView.IntegrationTests   managed/native integration tests
NeoWebView.Sample.Basic       sample executable
NeoWebView.Sample.NativeAot   sample executable
NeoWebView.Sample.Embedded    sample executable
```

Test, sample and code-generation executables do not add runtime assemblies to an application consuming the package. `CppAst.CodeGen` MUST be referenced only by `NeoWebView.CodeGen`; it MUST NOT become a dependency of `NeoWebView.dll` or consuming applications.

Suggested responsibilities:

### Generated interop

The generated declarations are compiled into `NeoWebView.dll` under the `NeoWebView.Interop.Generated` namespace. All generated types and members MUST be `internal`; no generated declaration is a supported public API.

It contains:

* Native structures.
* Enums.
* Constants.
* Opaque pointer types.
* Native function declarations.
* Native callback function-pointer declarations.

It MUST NOT contain user-facing abstractions.

### Manually maintained interop

Manually maintained low-level helpers are compiled into `NeoWebView.dll`, normally under the `NeoWebView.Interop` namespace:

* Native library resolution.
* Safe-handle implementations.
* UTF-8 helpers.
* Callback routing.
* ABI validation.
* Error translation.
* Operation completion infrastructure.

These helpers MUST remain `internal` except for intentionally designed native-handle escape hatches in the public API. Public signatures MUST NOT expose generated types.

### `NeoWebView`

Contains the public managed API:

* `NeoApplication`
* `NeoDispatcher`
* `NeoEnvironment`
* `NeoProfile`
* `NeoWindow`
* `NeoWebView`
* Options and event arguments
* Resource providers
* Exceptions
* Capability APIs

### `NeoWebView.CodeGen`

Contains the deterministic CppAst.CodeGen console application. It reads the authoritative headers under `native/include/`, writes checked-in C# files directly into the `NeoWebView` project, and is never referenced by the runtime assembly.

---

# 7. Proposed repository structure

```text
/
├── CMakeLists.txt
├── CMakePresets.json
├── license.txt
├── readme.md
├── doc/
│   └── neowebview_specs.md
├── THIRD-PARTY-NOTICES.md
│
├── native/
│   ├── include/
│   │   ├── neowebview.h
│   │   └── neowebview_version.h
│   │
│   ├── src/
│   │   ├── common/
│   │   │   ├── app_base.*
│   │   │   ├── environment_base.*
│   │   │   ├── profile_base.*
│   │   │   ├── window_base.*
│   │   │   ├── view_base.*
│   │   │   ├── operation.*
│   │   │   ├── decision.*
│   │   │   ├── error.*
│   │   │   ├── logging.*
│   │   │   └── ref_counted.*
│   │   │
│   │   ├── windows/
│   │   │   ├── windows_app.cpp
│   │   │   ├── windows_window.cpp
│   │   │   ├── webview2_environment.cpp
│   │   │   ├── webview2_profile.cpp
│   │   │   └── webview2_view.cpp
│   │   │
│   │   ├── macos/
│   │   │   ├── cocoa_app.mm
│   │   │   ├── cocoa_window.mm
│   │   │   ├── wk_environment.mm
│   │   │   ├── wk_profile.mm
│   │   │   └── wk_view.mm
│   │   │
│   │   └── linux/
│   │       ├── gtk_app.cpp
│   │       ├── gtk_window.cpp
│   │       ├── webkitgtk_environment.cpp
│   │       ├── webkitgtk_profile.cpp
│   │       └── webkitgtk_view.cpp
│   │
│   └── tests/
│
├── src/
│   ├── NeoWebView.slnx
│   ├── NeoWebView/
│   │   ├── Interop/
│   │   └── Generated/
│   │       └── Interop/
│   ├── NeoWebView.CodeGen/
│   ├── NeoWebView.Tests/
│   ├── NeoWebView.IntegrationTests/
│   ├── NeoWebView.Sample.Basic/
│   ├── NeoWebView.Sample.NativeAot/
│   └── NeoWebView.Sample.Embedded/
│
├── javascript/
│   ├── src/
│   ├── tests/
│   └── package.json
│
├── eng/
│   ├── native/
│   ├── packaging/
│   └── scripts/
│
└── tests/
    ├── pages/
    ├── assets/
    ├── stress/
    └── conformance/
```

All native source, public native headers and native tests MUST remain under `native/`. All managed runtime source, managed tooling, managed tests and managed samples MUST remain under the existing `src/` tree. Generated interop files MUST live inside `src/NeoWebView/` so the normal SDK compile glob includes them in the single managed assembly.

The JavaScript package MUST remain optional. The basic framework and basic samples MUST work without npm.

---

# 8. Build system

## 8.1 CMake requirements

The native project MUST use CMake.

The minimum CMake version SHOULD initially be 3.28 or later.

CMake presets MUST be provided for supported configurations.

Cross-compilation settings MUST be expressed through CMake toolchain files where appropriate. CMake reads the toolchain file before normal project configuration and uses it to locate compilers, SDKs and related tools.

The shared-library target SHOULD be named `neowebview_native` and MUST emit the filenames defined in section 6.1 on every platform. Platform-specific defaults MUST NOT silently change that basename.

## 8.2 Compiler requirements

Clang MUST be used on every platform:

| Platform | Compiler            |
| -------- | ------------------- |
| Windows  | `clang-cl`          |
| macOS    | Apple Clang         |
| Linux    | `clang` / `clang++` |

The preferred linker is:

| Platform | Linker                             |
| -------- | ---------------------------------- |
| Windows  | `lld-link`                         |
| macOS    | Apple system linker                |
| Linux    | `lld`, with system linker fallback |

The authoritative native header MUST parse successfully as C++20 with the supported native toolchains and CppAst.CodeGen. Compatibility with a C compiler is not required. Exported functions MUST retain C linkage and the C calling convention so the binary ABI remains consumable by non-C++ languages.

Platform implementations SHOULD use C++20 where C++ is required.

Objective-C++ source files MUST use `.mm`.

## 8.3 Required build presets

At minimum:

```text
windows-x64-debug
windows-x64-release
windows-arm64-release
macos-x64-debug
macos-x64-release
macos-arm64-debug
macos-arm64-release
macos-universal-release
linux-x64-debug
linux-x64-release
linux-arm64-release
```

Future:

```text
linux-musl-x64-release
linux-musl-arm64-release
```

## 8.4 CMake options

Suggested options:

```cmake
NEOWEBVIEW_BUILD_SHARED
NEOWEBVIEW_BUILD_TESTS
NEOWEBVIEW_BUILD_SAMPLES
NEOWEBVIEW_ENABLE_ASAN
NEOWEBVIEW_ENABLE_UBSAN
NEOWEBVIEW_ENABLE_TSAN
NEOWEBVIEW_ENABLE_LTO
NEOWEBVIEW_WARNINGS_AS_ERRORS
NEOWEBVIEW_WEBVIEW2_SDK_VERSION
NEOWEBVIEW_LINUX_WEBKIT_API
NEOWEBVIEW_BUILD_UNIVERSAL_MACOS
```

Defaults:

```text
BUILD_SHARED              ON
BUILD_TESTS               OFF for package builds
BUILD_SAMPLES             OFF for package builds
ENABLE_LTO                ON for release
WARNINGS_AS_ERRORS        ON in CI
LINUX_WEBKIT_API          4.1
```

## 8.5 Windows build

The Windows backend MUST:

* Compile with `clang-cl`.
* Target the MSVC ABI.
* Use the Windows SDK.
* Use COM directly.
* Link the WebView2 loader statically where practical.
* Avoid requiring WinUI, WPF, WinForms, or Windows App SDK.
* Create a regular child `HWND` for the view controller.
* Initialize the UI thread as a COM STA.

The WebView2 SDK version MUST be pinned and reproducibly restored.

The installed WebView2 runtime version MUST be discovered at runtime.

NeoWebView SHOULD use the Evergreen WebView2 Runtime by default. Microsoft recommends Evergreen for most applications because it is shared and automatically updated; fixed-version mode may remain available for applications that need strict runtime control.

## 8.6 macOS build

The macOS backend MUST:

* Compile Objective-C++ with ARC.
* Link Cocoa, WebKit and required system frameworks.
* Support separate x64 and ARM64 builds.
* Produce an optional universal binary.
* Set dylib compatibility and current versions.
* Avoid private WebKit APIs.
* Avoid embedding a separate WebKit build.

## 8.7 Linux build

The Linux backend MUST:

* Use `pkg-config`.
* Link against `webkit2gtk-4.1`.
* Use GTK 3 and libsoup 3.
* Work under both X11 and Wayland through GTK.
* Avoid direct X11 assumptions in the portable layer.
* Dynamically link the distro-provided WebKitGTK.
* Provide a useful diagnostic when runtime dependencies are unavailable.

The package itself MUST NOT redistribute WebKitGTK in v1.

---

# 9. ABI design

## 9.1 General requirements

The C ABI MUST:

* Use C linkage.
* Use the C calling convention.
* Use fixed-width integer types.
* Use opaque object handles.
* Use UTF-8 strings.
* Avoid `bool`, `long`, C++ references, C++ classes and templates.
* Declare every enumeration as a C++ typed enum with an explicit fixed-width underlying integer type.
* Avoid compiler-dependent bitfields.
* Avoid packed public structures.
* Avoid variadic functions.
* Avoid callbacks whose lifetime is unclear.
* Never allow exceptions to cross the ABI.
* Never expose ownership ambiguously.
* Remain compatible with NativeAOT.
* Be suitable for automatic processing by CppAst.CodeGen.

All exported symbols MUST use the `neo_webview_` prefix.

## 9.2 Enumeration representation

All public ABI enumerations MUST use C++ typed-enum syntax and an explicit fixed-width underlying type, for example:

```cpp
typedef enum neo_webview_support_level : uint32_t {
    NEO_WEBVIEW_SUPPORT_NONE = 0,
    NEO_WEBVIEW_SUPPORT_NATIVE = 1,
    NEO_WEBVIEW_SUPPORT_EMULATED = 2,
    NEO_WEBVIEW_SUPPORT_LIMITED = 3
} neo_webview_support_level_t;
```

CppAst.CodeGen MUST derive the corresponding managed enum and its storage type directly from this declaration. The generator MUST NOT need a mapping rule to infer or override an enum's underlying type.

Portable enums SHOULD use `uint32_t` unless negative values or another width are semantically required. Result-code enums MUST use `int32_t`. Bitmask types MAY remain fixed-width integer typedefs with constants when arbitrary combinations are valid. An enum's underlying type is part of the ABI and MUST NOT change without an ABI-major-version change.

## 9.3 Symbol export

```cpp
#if defined(_WIN32)
    #if defined(NEOWEBVIEW_BUILD)
        #define NEO_WEBVIEW_API __declspec(dllexport)
    #else
        #define NEO_WEBVIEW_API __declspec(dllimport)
    #endif
#else
    #define NEO_WEBVIEW_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif
```

All other native symbols MUST use hidden visibility.

## 9.4 ABI version

```cpp
#define NEO_WEBVIEW_ABI_VERSION_MAJOR 1
#define NEO_WEBVIEW_ABI_VERSION_MINOR 0

NEO_WEBVIEW_API uint32_t
neo_webview_get_abi_version_major(void);

NEO_WEBVIEW_API uint32_t
neo_webview_get_abi_version_minor(void);
```

The managed library MUST validate ABI compatibility before creating any object.

ABI rules:

* Adding a new exported function is compatible.
* Adding an enum value is compatible when it fits the enum's existing fixed-width underlying type.
* Appending a field to a size-versioned structure is compatible.
* Reordering or removing structure fields is incompatible.
* Changing parameter or return types is incompatible.
* Changing ownership or threading semantics is incompatible.
* Incompatible changes require a new ABI major version.

Linux MUST use an appropriate SONAME such as:

```text
libneowebview_native.so.1
```

## 9.5 Public structure versioning

Every extensible input or output structure MUST start with:

```cpp
typedef struct neo_webview_struct_header {
    uint32_t size;
    uint32_t version;
} neo_webview_struct_header_t;
```

Example:

```cpp
typedef struct neo_webview_window_options {
    uint32_t size;
    uint32_t version;

    neo_webview_string_view_t title;
    int32_t width;
    int32_t height;
    uint32_t flags;
} neo_webview_window_options_t;
```

Callers MUST initialize `size`.

The native library MUST:

* Read only fields covered by the supplied size.
* Ignore unknown trailing fields.
* Initialize only output fields covered by the supplied size.

## 9.6 String representation

```cpp
typedef struct neo_webview_string_view {
    const uint8_t* data;
    uint64_t length;
} neo_webview_string_view_t;
```

Rules:

* Strings are UTF-8.
* Strings are not required to be null-terminated.
* A zero-length string may use a null data pointer.
* Input string memory must remain valid for the duration of the call.
* Borrowed output strings remain valid only for the documented callback or accessor lifetime.
* Long-lived or large outputs SHOULD use an immutable buffer object.

## 9.7 Buffer representation

```cpp
typedef struct neo_webview_buffer neo_webview_buffer_t;

NEO_WEBVIEW_API void
neo_webview_buffer_retain(neo_webview_buffer_t* buffer);

NEO_WEBVIEW_API void
neo_webview_buffer_release(neo_webview_buffer_t* buffer);

NEO_WEBVIEW_API const uint8_t*
neo_webview_buffer_get_data(const neo_webview_buffer_t* buffer);

NEO_WEBVIEW_API uint64_t
neo_webview_buffer_get_length(const neo_webview_buffer_t* buffer);
```

Buffers MUST be immutable once exposed.

The managed API MAY copy small buffers and MAY expose advanced zero-copy access through an unmanaged memory manager.

## 9.8 Opaque handles

```cpp
typedef struct neo_webview_app neo_webview_app_t;
typedef struct neo_webview_environment neo_webview_environment_t;
typedef struct neo_webview_profile neo_webview_profile_t;
typedef struct neo_webview_window neo_webview_window_t;
typedef struct neo_webview_view neo_webview_view_t;
typedef struct neo_webview_operation neo_webview_operation_t;
typedef struct neo_webview_decision neo_webview_decision_t;
typedef struct neo_webview_error neo_webview_error_t;
typedef struct neo_webview_buffer neo_webview_buffer_t;
typedef struct neo_webview_stream neo_webview_stream_t;
```

All long-lived objects MUST be reference-counted.

Retain and release operations MUST be safe to call from any thread.

Final native destruction of UI objects MUST be marshalled to the owning UI thread.

This requirement allows managed safe handles to release objects from a finalizer thread without violating native UI-thread restrictions.

---

# 10. Error model

## 10.1 Result codes

```cpp
typedef enum neo_webview_result : int32_t {
    NEO_WEBVIEW_OK = 0,

    NEO_WEBVIEW_ERROR_UNKNOWN = -1,
    NEO_WEBVIEW_ERROR_INVALID_ARGUMENT = -2,
    NEO_WEBVIEW_ERROR_INVALID_STATE = -3,
    NEO_WEBVIEW_ERROR_NOT_SUPPORTED = -4,
    NEO_WEBVIEW_ERROR_NOT_INITIALIZED = -5,
    NEO_WEBVIEW_ERROR_ALREADY_INITIALIZED = -6,
    NEO_WEBVIEW_ERROR_WRONG_THREAD = -7,
    NEO_WEBVIEW_ERROR_CANCELED = -8,
    NEO_WEBVIEW_ERROR_TIMED_OUT = -9,
    NEO_WEBVIEW_ERROR_BACKEND_UNAVAILABLE = -10,
    NEO_WEBVIEW_ERROR_RUNTIME_UNAVAILABLE = -11,
    NEO_WEBVIEW_ERROR_NATIVE_FAILURE = -12,
    NEO_WEBVIEW_ERROR_DISPOSED = -13,
    NEO_WEBVIEW_ERROR_SECURITY = -14
} neo_webview_result_t;
```

## 10.2 Detailed errors

Detailed errors MUST use an opaque, reference-counted object.

```cpp
NEO_WEBVIEW_API neo_webview_result_t
neo_webview_error_get_code(const neo_webview_error_t* error);

NEO_WEBVIEW_API int64_t
neo_webview_error_get_native_code(const neo_webview_error_t* error);

NEO_WEBVIEW_API neo_webview_string_view_t
neo_webview_error_get_domain(const neo_webview_error_t* error);

NEO_WEBVIEW_API neo_webview_string_view_t
neo_webview_error_get_message(const neo_webview_error_t* error);
```

Error domains may include:

```text
neowebview
win32
com
webview2
cocoa
webkit
gtk
glib
posix
```

Errors are expected to be rare, so allocating an error object on failure is acceptable.

The high-level C# layer MUST translate errors to typed managed exceptions while preserving:

* Portable result code.
* Native result code.
* Backend domain.
* Native message.
* Operation context.

---

# 11. Threading model

## 11.1 Main-thread ownership

The UI event loop MUST run on:

* The application UI thread on Windows.
* The process main thread on macOS.
* The GTK/GLib owning thread on Linux.

On macOS, standalone mode MUST be started from the process main thread.

## 11.2 Callable-from-any-thread operations

The following MUST be safe from any thread:

* Retain.
* Release.
* Operation cancellation.
* Application dispatch.
* Application quit request.
* Immutable capability queries.
* Immutable runtime-information queries.

Other C functions MAY require the UI thread unless documented otherwise.

The managed API SHOULD hide most thread restrictions by dispatching asynchronous operations to the UI thread.

## 11.3 Dispatcher

```cpp
typedef void (*neo_webview_dispatch_callback_t)(void* context);

NEO_WEBVIEW_API neo_webview_result_t
neo_webview_app_dispatch(
    neo_webview_app_t* app,
    neo_webview_dispatch_callback_t callback,
    void* context);
```

Dispatch requirements:

* It MUST be callable from any thread.
* It MUST preserve FIFO ordering for calls from a single producer.
* It MUST not invoke the callback before `neo_webview_app_dispatch` returns.
* It MUST not invoke callbacks after application shutdown has completed.
* It SHOULD minimize allocations.
* It MUST catch and contain native implementation exceptions.

## 11.4 Callback safety

The native library MUST NOT:

* Invoke managed callbacks while holding internal mutexes.
* Invoke asynchronous completion callbacks inline before the initiating function returns.
* Invoke callbacks after the associated managed callback registration has been removed and quiesced.
* Invoke callbacks after the source object’s final destruction.

The implementation MUST tolerate C API calls made from inside event callbacks unless a specific call is documented as invalid.

---

# 12. Application and event loop

## 12.1 Standalone mode

Standalone mode provides the default application and windowing layer. It is the recommended mode for applications that do not already own a native UI event loop.

```cpp
neo_webview_result_t
neo_webview_app_create(
    const neo_webview_app_options_t* options,
    neo_webview_app_t** app,
    neo_webview_error_t** error);

int32_t
neo_webview_app_run(neo_webview_app_t* app);

void
neo_webview_app_quit(
    neo_webview_app_t* app,
    int32_t exit_code);
```

`neo_webview_app_run` MUST block the calling thread until shutdown.

It MUST run:

* A Win32 message loop.
* `NSApplication` on macOS.
* The GTK/GLib event loop on Linux.

The application MUST support a startup callback invoked after the event loop becomes dispatchable.

Standalone mode MUST:

* Create and manage any number of top-level `NeoWindow` instances on the UI thread.
* Keep an application-wide registry of open windows with stable identifiers.
* Support owned/transient windows suitable for dialogs and web-requested popups.
* Route activation, focus, move, resize, scale-factor, state-change, close-request and closed events.
* Allow views to fill a window automatically or use explicit client-area bounds.
* Provide configurable shutdown behavior when the last top-level window closes.
* Continue running after the startup callback returns.
* Avoid nested event loops for ordinary asynchronous operations.

The initial shutdown modes SHOULD include:

```cpp
typedef enum neo_webview_app_shutdown_mode : uint32_t {
    NEO_WEBVIEW_APP_SHUTDOWN_EXPLICIT = 0,
    NEO_WEBVIEW_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED = 1,
    NEO_WEBVIEW_APP_SHUTDOWN_ON_MAIN_WINDOW_CLOSED = 2
} neo_webview_app_shutdown_mode_t;
```

`ON_LAST_WINDOW_CLOSED` SHOULD be the standalone default. Closing an owned popup while another top-level window remains open MUST NOT terminate the application.

## 12.2 Embedded mode

Embedded mode allows NeoWebView to be used inside another native UI framework.

The host is responsible for:

* Initializing the platform GUI framework.
* Running its event loop.
* Calling NeoWebView on the correct UI thread.
* Providing a supported native parent handle.
* Coordinating application shutdown.

Embedded mode MUST NOT create a second event loop, change process-wide GUI configuration unexpectedly, or take ownership of the supplied parent. NeoWebView MUST provide an explicit embedded-application initialization path that binds its dispatcher to the host UI thread.

Supported parent kinds:

```cpp
typedef enum neo_webview_native_parent_kind : uint32_t {
    NEO_WEBVIEW_NATIVE_PARENT_NONE = 0,
    NEO_WEBVIEW_NATIVE_PARENT_WIN32_HWND = 1,
    NEO_WEBVIEW_NATIVE_PARENT_COCOA_NSVIEW = 2,
    NEO_WEBVIEW_NATIVE_PARENT_GTK_WIDGET = 3
} neo_webview_native_parent_kind_t;

typedef struct neo_webview_native_parent {
    uint32_t size;
    uint32_t version;
    neo_webview_native_parent_kind_t kind;
    void* handle;
} neo_webview_native_parent_t;
```

A parent handle is borrowed and MUST remain valid until every attached view is detached or destroyed. The native layer MUST validate that the kind is supported by the active backend and that the handle is non-null before creating a view.

On Windows, `handle` is the value of an `HWND` cast to `void*`. On macOS it is an `NSView*`. On Linux it is a `GtkWidget*`, not an X11 window ID or Wayland surface. The managed API MUST expose named factories for these forms rather than requiring normal users to construct an untyped `nint` descriptor.

---

# 13. Object model

## 13.1 Application

Owns:

* The UI event loop in standalone mode.
* The UI dispatcher.
* Application-level lifetime.
* Native window registration.
* Shutdown coordination.
* Global logging configuration.

Only one standalone application object MAY run in a process in v1.

The application MUST expose its open-window collection and deterministic window lookup by identifier. Creating and closing one window MUST NOT invalidate unrelated windows or views. Shutdown MUST close or detach all windows, resolve pending close and popup decisions, and complete pending operations according to the shutdown contract.

## 13.2 Environment

Represents browser configuration and browser-process scope.

Environment options SHOULD include:

* User-data root.
* Preferred language list.
* Browser runtime path.
* Browser arguments.
* Proxy configuration where supported.
* Custom-scheme registrations.
* Logging preferences.
* Browser feature flags.
* Private/default mode.
* Backend preference.
* Runtime channel preference on Windows.

WebView2 explicitly models an environment and creates views asynchronously from that environment. Views created from an environment share its browser-process configuration. Environment creation MUST be asynchronous on the portable API even when a backend can create it synchronously.

## 13.3 Profile

Represents browsing data and session identity.

A profile MUST support:

* Persistent mode.
* Ephemeral/private mode.
* Cookie storage.
* Cache and website data.
* Permission decisions where supported.
* Data clearing.
* Shared use by multiple views.

Named profiles are capability-gated.

Mapping:

| NeoWebView  | Windows                        | macOS                          | Linux                      |
| ----------- | ------------------------------ | ------------------------------ | -------------------------- |
| Environment | WebView2 environment           | WK configuration/process scope | WebKitWebContext           |
| Profile     | CoreWebView2 profile/user data | WKWebsiteDataStore             | WebsiteDataManager/context |
| View        | Controller + CoreWebView2      | WKWebView                      | WebKitWebView              |

A backend that cannot implement fully independent named profiles MUST report limited or unsupported capability rather than silently sharing data.

## 13.4 Window

A NeoWebView-owned native top-level window.

Required portable properties:

* Stable application-local identifier.
* Title.
* Position.
* Client size.
* Normal/restored bounds.
* Minimum size.
* Maximum size.
* Visibility.
* Resizable state.
* Decorations state.
* Maximized state.
* Minimized state.
* Fullscreen state.
* Always-on-top state.
* Taskbar/dock visibility where supported.
* Background color.
* Focus and activation state.
* Effective scale factor.
* Owner/transient-parent relationship.
* Close request.
* Closed notification.
* Typed native handles.

A window MUST NOT depend on a specific managed UI framework.

Window coordinates exposed by the portable API MUST be logical units. The backend MUST translate to platform pixels and report scale-factor changes. Position requests MUST account for the selected monitor work area where possible, and platform constraints or compositor-controlled positioning MUST be documented through capabilities.

An owned window MUST remain associated with its owner for z-order, minimize and close behavior using the nearest platform semantics. NeoWebView MUST surface owner closure to managed code and MUST NOT leave an inaccessible modal or popup window alive. Modal behavior, if provided, SHOULD be asynchronous and MUST NOT require user code to start a nested event loop.

The default layer MUST support ordinary decorated application windows without platform-specific code. Native menus, tray icons and toolbars are not part of v1; web-based application chrome remains supported inside the view.

## 13.5 View

A view represents one browser instance.

It may be attached to:

* A NeoWebView window.
* A caller-provided native parent.
* A caller-controlled rectangle inside the parent.

A view MUST support explicit bounds and automatic fill-parent mode.

Multiple views MAY share one window when explicit bounds are used. A typical standalone window SHOULD use one fill-parent view. Resizing one view MUST NOT change the bounds of sibling views.

## 13.6 Window lifetime and popup integration

`NeoApplication` owns the native window registry, while each managed `NeoWindow` owns a safe handle to its native window. Window closure and managed disposal MUST be idempotent and MUST produce one terminal `Closed` notification.

Web-requested windows MUST enter the same registry and use the same `NeoWindow` and `NeoWebView` abstractions as application-created windows. The popup creation path MUST preserve the originating view relationship required by the active backend, while still allowing the application to select the window owner, requested bounds, profile-compatible options and initial visibility.

An application MUST be able to create, show, hide, activate and close windows after the startup callback has returned. A popup MUST NOT be represented by an untracked backend-owned native window in the normal managed API.

---

# 14. Asynchronous native operations

## 14.1 Operation rules

Any operation that can be asynchronous on at least one backend MUST be asynchronous in the portable API.

This includes:

* Environment creation.
* View creation.
* JavaScript evaluation.
* Cookie operations.
* Website-data clearing.
* PDF generation.
* Screenshot capture.
* Runtime version lookup where required.
* Selected profile operations.

## 14.2 Completion contract

Each asynchronous function MUST:

1. Return an immediate scheduling result.
2. Optionally return an operation handle.
3. Invoke the completion callback exactly once after successful scheduling.
4. Never invoke completion before the initiating function returns.
5. Invoke completion on the UI thread unless documented otherwise.
6. Report cancellation through the completion callback.
7. Release all callback-associated native resources after completion.

Example shape:

```cpp
typedef void (*neo_webview_environment_created_callback_t)(
    void* context,
    neo_webview_result_t result,
    neo_webview_environment_t* environment,
    const neo_webview_error_t* error);

neo_webview_result_t
neo_webview_environment_create_async(
    neo_webview_app_t* app,
    const neo_webview_environment_options_t* options,
    neo_webview_environment_created_callback_t callback,
    void* context,
    neo_webview_operation_t** operation,
    neo_webview_error_t** error);
```

On successful completion, the returned environment reference is owned by the recipient.

## 14.3 Cancellation

```cpp
NEO_WEBVIEW_API void
neo_webview_operation_cancel(neo_webview_operation_t* operation);
```

Cancellation has three possible backend outcomes:

* The native operation is canceled.
* Only the managed wait is canceled while native work finishes.
* Cancellation is unsupported because the operation has already committed.

The managed API MUST document whether a particular cancellation token cancels the operation or only the wait.

Regardless of backend behavior, operation resources MUST eventually be completed and released.

---

# 15. Event model

## 15.1 Unified native event callback

To reduce callback registrations and managed delegate roots, each source object SHOULD use one event callback.

```cpp
typedef enum neo_webview_event_type : uint32_t {
    NEO_WEBVIEW_EVENT_NONE = 0,

    NEO_WEBVIEW_EVENT_WINDOW_CLOSE_REQUESTED,
    NEO_WEBVIEW_EVENT_WINDOW_CLOSED,
    NEO_WEBVIEW_EVENT_WINDOW_MOVED,
    NEO_WEBVIEW_EVENT_WINDOW_RESIZED,
    NEO_WEBVIEW_EVENT_WINDOW_FOCUS_CHANGED,
    NEO_WEBVIEW_EVENT_WINDOW_SCALE_FACTOR_CHANGED,
    NEO_WEBVIEW_EVENT_WINDOW_STATE_CHANGED,

    NEO_WEBVIEW_EVENT_NAVIGATION_REQUESTED,
    NEO_WEBVIEW_EVENT_NAVIGATION_STARTED,
    NEO_WEBVIEW_EVENT_NAVIGATION_REDIRECTED,
    NEO_WEBVIEW_EVENT_NAVIGATION_COMMITTED,
    NEO_WEBVIEW_EVENT_NAVIGATION_COMPLETED,
    NEO_WEBVIEW_EVENT_NAVIGATION_FAILED,

    NEO_WEBVIEW_EVENT_SOURCE_CHANGED,
    NEO_WEBVIEW_EVENT_TITLE_CHANGED,
    NEO_WEBVIEW_EVENT_HISTORY_CHANGED,
    NEO_WEBVIEW_EVENT_LOADING_PROGRESS_CHANGED,
    NEO_WEBVIEW_EVENT_FAVICON_CHANGED,

    NEO_WEBVIEW_EVENT_MESSAGE_RECEIVED,
    NEO_WEBVIEW_EVENT_CONSOLE_MESSAGE,

    NEO_WEBVIEW_EVENT_NEW_WINDOW_REQUESTED,
    NEO_WEBVIEW_EVENT_PERMISSION_REQUESTED,
    NEO_WEBVIEW_EVENT_DOWNLOAD_REQUESTED,
    NEO_WEBVIEW_EVENT_SCRIPT_DIALOG_REQUESTED,
    NEO_WEBVIEW_EVENT_FILE_CHOOSER_REQUESTED,
    NEO_WEBVIEW_EVENT_AUTHENTICATION_REQUESTED,
    NEO_WEBVIEW_EVENT_CERTIFICATE_ERROR,
    NEO_WEBVIEW_EVENT_FULLSCREEN_REQUESTED,

    NEO_WEBVIEW_EVENT_WEB_PROCESS_TERMINATED,
    NEO_WEBVIEW_EVENT_DOWNLOAD_STARTED,
    NEO_WEBVIEW_EVENT_DOWNLOAD_PROGRESS_CHANGED,
    NEO_WEBVIEW_EVENT_DOWNLOAD_COMPLETED,
    NEO_WEBVIEW_EVENT_CLIENT_CERTIFICATE_REQUESTED
} neo_webview_event_type_t;
```

Every event structure MUST begin with:

```cpp
typedef struct neo_webview_event_header {
    uint32_t size;
    uint32_t version;
    neo_webview_event_type_t type;
    uint64_t sequence;
    uint64_t timestamp_ns;
} neo_webview_event_header_t;
```

## 15.2 Notifications versus decisions

Events are divided into:

### Notification events

The host observes an event but does not control it.

Examples:

* Title changed.
* Navigation started.
* Navigation completed.
* History changed.
* Process terminated.

### Decision events

The browser waits for a host decision.

Examples:

* Navigation policy.
* New window.
* Permission.
* Download destination.
* JavaScript dialog.
* File chooser.
* Authentication.
* Certificate failure.
* Fullscreen.

---

# 16. Deferred decisions

## 16.1 Decision object

```cpp
typedef enum neo_webview_decision_action : uint32_t {
    NEO_WEBVIEW_DECISION_DEFAULT = 0,
    NEO_WEBVIEW_DECISION_ALLOW = 1,
    NEO_WEBVIEW_DECISION_DENY = 2,
    NEO_WEBVIEW_DECISION_CANCEL = 3,
    NEO_WEBVIEW_DECISION_OPEN_EXTERNAL = 4,
    NEO_WEBVIEW_DECISION_DOWNLOAD = 5,
    NEO_WEBVIEW_DECISION_HANDLED_EXTERNAL = 6
} neo_webview_decision_action_t;
```

A decision can be completed synchronously during an event callback or deferred.

```cpp
neo_webview_result_t
neo_webview_decision_defer(
    neo_webview_decision_t* decision);

neo_webview_result_t
neo_webview_decision_complete(
    neo_webview_decision_t* decision,
    const neo_webview_decision_response_t* response,
    neo_webview_error_t** error);
```

## 16.2 Required semantics

* A decision may be completed exactly once.
* `defer` keeps the underlying native request alive.
* If the callback returns without completion or deferral, the documented safe default is applied.
* Destroying the view resolves outstanding decisions safely.
* Deferred decisions MUST have a timeout.
* A late decision completion MUST return `NEO_WEBVIEW_ERROR_INVALID_STATE`.
* Timeout defaults MUST be configurable.
* No decision may wait indefinitely.

WebKitGTK supports asynchronous policy decisions by retaining the policy object and completing it later, while applying a default action when no explicit decision is made. This maps naturally to the portable decision abstraction.

## 16.3 Safe defaults

Recommended defaults:

| Request                           | Default                |
| --------------------------------- | ---------------------- |
| Main-frame same-origin navigation | Allow                  |
| Cross-origin navigation           | Allow browser handling |
| New window                        | Cancel                 |
| Sensitive permission              | Deny                   |
| Download                          | Cancel                 |
| JavaScript alert                  | Use engine default     |
| JavaScript confirm                | Cancel/false           |
| JavaScript prompt                 | Cancel                 |
| File chooser                      | Cancel                 |
| HTTP authentication               | Use engine default     |
| TLS certificate error             | Deny                   |
| Client certificate                | Use engine default     |
| Fullscreen                        | Deny unless enabled    |

---

# 17. Navigation

## 17.1 Navigation operations

The portable API MUST expose:

* Navigate to URI.
* Navigate using method, headers and optional body.
* Load HTML with a base URI.
* Stop.
* Reload.
* Reload ignoring cache where supported.
* Go back.
* Go forward.

## 17.2 Navigation state

The view MUST expose cached or event-maintained state for:

* Current source URI.
* Document title.
* Loading state.
* Loading progress.
* Can go back.
* Can go forward.
* Last navigation identifier.

State getters SHOULD avoid synchronous browser-process IPC.

## 17.3 Navigation phases

```cpp
typedef enum neo_webview_navigation_phase : uint32_t {
    NEO_WEBVIEW_NAVIGATION_REQUESTED = 0,
    NEO_WEBVIEW_NAVIGATION_STARTED,
    NEO_WEBVIEW_NAVIGATION_REDIRECTED,
    NEO_WEBVIEW_NAVIGATION_COMMITTED,
    NEO_WEBVIEW_NAVIGATION_COMPLETED,
    NEO_WEBVIEW_NAVIGATION_FAILED
} neo_webview_navigation_phase_t;
```

Navigation event information SHOULD include:

* Navigation identifier.
* URI.
* HTTP method if known.
* Main-frame status.
* User-initiated status.
* Redirect status.
* Navigation kind.
* HTTP status where available.
* Portable error code.
* Backend-native error code.

Subframe support MUST be capability-gated.

---

# 18. JavaScript execution and injection

## 18.1 Script injection

The view MUST support scripts injected at:

* Document start.
* Document end.

Options SHOULD include:

* Main frame only.
* All frames.
* Page world.
* Isolated world where supported.
* Allowed origins.
* Optional world name.

The Apple WebKit content controller supports user-script injection and native message handlers, and WebKitGTK exposes asynchronous script execution and named script worlds.

## 18.2 Script handles

Adding a persistent script MUST return a handle or identifier that can be removed.

Removing a script affects future documents. It does not need to undo code already executed in the current document.

## 18.3 JavaScript evaluation

```cpp
neo_webview_result_t
neo_webview_view_evaluate_script_async(
    neo_webview_view_t* view,
    neo_webview_string_view_t script,
    const neo_webview_script_evaluation_options_t* options,
    neo_webview_script_evaluated_callback_t callback,
    void* context,
    neo_webview_operation_t** operation,
    neo_webview_error_t** error);
```

The result MUST contain:

* Success state.
* JSON-encoded result.
* Exception name.
* Exception message.
* Stack trace where available.

The C# API SHOULD provide:

```csharp
Task<string?> EvaluateScriptAsync(
    string script,
    CancellationToken cancellationToken = default);

Task<T?> EvaluateScriptAsync<T>(
    string script,
    JsonTypeInfo<T> typeInfo,
    CancellationToken cancellationToken = default);
```

Generic deserialization MUST use supplied or generated `System.Text.Json` metadata and MUST NOT require runtime reflection under NativeAOT.

---

# 19. Web/native messaging

## 19.1 Transport

NeoWebView MUST provide a normalized message transport independent of backend-specific JavaScript APIs.

The JavaScript-facing API SHOULD resemble:

```javascript
window.neoWebView.postMessage(message);
window.neoWebView.addEventListener("message", handler);
window.neoWebView.removeEventListener("message", handler);
```

The exact global namespace remains configurable before v1 API freeze.

## 19.2 Native API

The portable native API MUST support:

* JSON messages.
* UTF-8 text messages.
* Origin information.
* Main-frame information.
* Optional frame identity.
* Native-to-web messages.
* Web-to-native messages.

Binary message support MAY be added later through buffer handles.

## 19.3 Bridge implementation

Suggested mappings:

| Platform | Mechanism                                         |
| -------- | ------------------------------------------------- |
| Windows  | WebView2 web messaging                            |
| macOS    | `WKScriptMessageHandler`                          |
| Linux    | `WebKitUserContentManager` script message handler |

WKScriptMessageHandler is specifically intended for receiving messages from JavaScript running inside a WKWebView.

## 19.4 Bridge security

The native bridge MUST be disabled for untrusted origins by default.

Default bridge access SHOULD be limited to:

* The configured application origin.
* Explicitly allowed custom-scheme origins.
* Additional origins explicitly allowlisted by the application.

The bridge configuration MUST distinguish:

* Main frame only.
* All frames.
* Exact origin.
* Origin wildcard, discouraged.
* Local application content.
* Remote HTTP/HTTPS content.

Each message event MUST report the source origin where available.

Navigating from trusted local content to arbitrary remote content MUST NOT silently preserve privileged bridge access.

## 19.5 RPC layer

The native library MUST expose transport, not application RPC semantics.

Typed RPC, generated TypeScript declarations and generated C# dispatch MAY be built above the transport as a separate component.

---

# 20. Local application content and custom schemes

## 20.1 Requirement

NeoWebView MUST allow applications to load packaged assets without starting a local HTTP server.

Example application URI:

```text
app://neowebview/index.html
```

## 20.2 Scheme registration

Custom schemes MUST be registered before environment creation.

Options SHOULD include:

* Scheme name.
* Whether the scheme has an authority component.
* Whether it is treated as secure.
* Whether CORS is enabled.
* Allowed origins.
* Whether service-worker behavior is expected.
* Whether the scheme is reserved for internal application content.

## 20.3 Resource request

Resource requests SHOULD expose:

* URI.
* Method.
* Headers.
* Request body.
* Resource kind.
* Initiating origin.
* Main-frame status.
* Frame information where supported.
* Cancellation state.

## 20.4 Resource response

Responses MUST support:

* Status code.
* Reason phrase.
* Headers.
* MIME type.
* Content length.
* In-memory bytes.
* File path.
* Streaming body.
* Empty body.
* Cancellation.

The file-path response SHOULD be optimized to avoid copying file contents through managed memory.

For the Phase 1 Windows slice, WebView2 asks for the response during its resource-request callback, so a byte/file resource provider is synchronous. The file response is consumed as a native stream and MUST NOT be copied through managed memory. The later streaming-body contract remains asynchronous because reads and cancellation are genuinely callback-based operations.

## 20.5 Streaming

A stream interface MUST support asynchronous reads and cancellation.

The implementation MUST apply backpressure and MUST NOT request the entire resource body in memory unless explicitly configured.

## 20.6 Backend mapping

* WebView2: custom-scheme registration and resource-request handling.
* WKWebView: `WKURLSchemeHandler`.
* WebKitGTK: `webkit_web_context_register_uri_scheme`.

WebKitGTK permits URI scheme requests to be retained and completed asynchronously.

## 20.7 Performance note

WebView2 documentation notes that handling each resource through browser-to-host request interception adds cross-process and UI-thread work. NeoWebView SHOULD therefore provide optimized file and virtual-host paths where a backend offers them, while preserving custom schemes as the portable model.

## 20.8 Directory-provider security

The default directory asset provider MUST:

* Normalize separators.
* Reject `..` traversal.
* Reject encoded traversal.
* Reject null bytes.
* Prevent escape from the configured root.
* Define symlink-following behavior.
* Generate correct MIME types.
* Support cache headers.
* Support range requests where practical.

The default provider MUST NOT follow symbolic links, junctions or other reparse points. Its portable baseline serves `GET` and `HEAD`; unsupported methods return `405`, missing resources return `404`, and malformed or encoded traversal returns `400`.

---

# 21. General network interception

Arbitrary HTTP/HTTPS request replacement MUST NOT be guaranteed by the portable v1 API.

The API MAY expose these capabilities:

```text
network.observe
network.intercept
network.intercept.main-frame
network.intercept.subresources
network.modify-request
network.modify-response
```

Possible support levels:

```cpp
typedef enum neo_webview_support_level : uint32_t {
    NEO_WEBVIEW_SUPPORT_NONE = 0,
    NEO_WEBVIEW_SUPPORT_NATIVE = 1,
    NEO_WEBVIEW_SUPPORT_EMULATED = 2,
    NEO_WEBVIEW_SUPPORT_LIMITED = 3
} neo_webview_support_level_t;
```

WebView2 can intercept broad web-resource requests, while public WKWebView APIs do not offer an equivalent general-purpose HTTP/HTTPS replacement mechanism. The common contract must not claim semantics that Cocoa cannot implement reliably.

---

# 22. Permissions

## 22.1 Portable permission kinds

```cpp
typedef enum neo_webview_permission_kind : uint32_t {
    NEO_WEBVIEW_PERMISSION_UNKNOWN = 0,
    NEO_WEBVIEW_PERMISSION_GEOLOCATION,
    NEO_WEBVIEW_PERMISSION_CAMERA,
    NEO_WEBVIEW_PERMISSION_MICROPHONE,
    NEO_WEBVIEW_PERMISSION_NOTIFICATIONS,
    NEO_WEBVIEW_PERMISSION_CLIPBOARD_READ,
    NEO_WEBVIEW_PERMISSION_CLIPBOARD_WRITE,
    NEO_WEBVIEW_PERMISSION_MIDI,
    NEO_WEBVIEW_PERMISSION_SCREEN_CAPTURE,
    NEO_WEBVIEW_PERMISSION_POINTER_LOCK,
    NEO_WEBVIEW_PERMISSION_LOCAL_FONTS,
    NEO_WEBVIEW_PERMISSION_FILE_SYSTEM,
    NEO_WEBVIEW_PERMISSION_PERSISTENT_STORAGE
} neo_webview_permission_kind_t;
```

The permission event MUST include:

* Portable kind.
* Backend-native kind string.
* Origin.
* User-initiated status.
* Main-frame status.
* Decision object.
* Whether persistence is supported.

The managed response SHOULD support:

* Default.
* Allow once.
* Allow and persist.
* Deny once.
* Deny and persist.

Unsupported persistence MUST be reported, not silently assumed.

---

# 23. Browser dialogs and host UX

## 23.1 JavaScript dialogs

The portable API MUST handle:

* `alert`
* `confirm`
* `prompt`
* `beforeunload`

The event MUST include:

* Dialog kind.
* Origin.
* Message.
* Default prompt text.

The response MUST allow:

* Accept.
* Cancel.
* Prompt result.

## 23.2 File chooser

The event SHOULD include:

* Accepted MIME types.
* Accepted extensions.
* Multiple-selection flag.
* Directory-selection flag.
* Capture preference.
* Suggested filename.

The response MUST allow selected paths or cancellation.

The native library MAY use the engine’s default chooser when requested.

## 23.3 New windows

A new-window request MUST report:

* Target URI, if known.
* Target frame name and navigation disposition, if known.
* Whether the request resulted from a user gesture.
* Requested logical position and client size from popup window features, if present.
* The opener view and its origin.
* An opaque, short-lived creation context containing any backend relationship required by the target view.

The request MUST allow:

* Cancel.
* Open externally.
* Navigate the current view.
* Create an owned `NeoWindow` and attach a new `NeoWebView` through a framework helper.
* Attach the request to a newly supplied profile-compatible `NeoWebView`.
* Use a backend default only through an explicit advanced opt-in and only when the resulting window can be tracked safely.

The popup creation context MUST ensure the new view uses the opener-compatible environment, WebKit configuration or related-view relationship required by the backend. Applications MUST NOT need to access backend-native objects to satisfy this requirement. The context MUST become invalid when the decision completes or times out.

Standalone mode SHOULD provide an application-level popup policy and a per-view asynchronous handler. A convenience policy MAY create an owned `NeoWindow`, apply safe requested bounds and attach a view. It MUST still enforce the configured origin policy and popup limits.

Unhandled new-window requests MUST be canceled. Automatic creation of unmanaged windows without application involvement MUST NOT be the portable default. A popup window MUST be registered with `NeoApplication`, participate in shutdown, and raise the normal window and view lifecycle events.

## 23.4 External URI launching

External URI launching MUST be an explicit managed action.

The native layer MAY provide a helper, but it MUST NOT automatically execute arbitrary schemes received from web content.

---

# 24. Downloads

## 24.1 Download request

The event SHOULD include:

* Source URI.
* Suggested filename.
* MIME type.
* Content disposition.
* Expected byte count.
* User-initiated status.
* Originating view.
* Decision object.

## 24.2 Download response

The host can:

* Cancel.
* Accept with destination path.
* Use backend default.
* Mark the download as handled externally.

## 24.3 Download object

A download object SHOULD expose:

* Identifier.
* State.
* Destination.
* Received byte count.
* Total byte count.
* Progress.
* Failure reason.
* Cancel.
* Pause and resume capabilities.

Pause and resume MUST be capability-gated.

---

# 25. Authentication and certificates

## 25.1 HTTP authentication

The authentication event SHOULD include:

* Host.
* Port.
* Realm.
* Authentication scheme.
* Previous failure count.
* Whether default credentials are available.

Responses:

* Use default handling.
* Supply username/password.
* Cancel.

Credentials MUST NOT be written to normal logs.

## 25.2 Client certificates

Client certificates SHOULD be represented by opaque candidate handles and readable metadata.

The host may:

* Select a candidate.
* Use default behavior.
* Cancel.

Private key material SHOULD remain in the native platform store.

## 25.3 TLS errors

The certificate error event SHOULD include:

* URI.
* Host.
* Portable error flags.
* Native error.
* Certificate metadata.
* Certificate chain metadata where available.

Default action MUST be deny.

“Trust permanently” MUST NOT be part of the initial portable contract.

---

# 26. Cookies and browsing data

## 26.1 Cookie operations

Profiles MUST support asynchronous:

* Get cookies for a URI.
* Enumerate matching cookies.
* Set cookie.
* Delete cookie.
* Delete all cookies.

Cookie data SHOULD include:

* Name.
* Value.
* Domain.
* Path.
* Expiration.
* Secure.
* HTTP-only.
* SameSite.
* Session status.

## 26.2 Data clearing

Portable data kinds SHOULD include:

```cpp
typedef uint64_t neo_webview_data_kind_t;

#define NEO_WEBVIEW_DATA_COOKIES          (1ull << 0)
#define NEO_WEBVIEW_DATA_CACHE            (1ull << 1)
#define NEO_WEBVIEW_DATA_LOCAL_STORAGE    (1ull << 2)
#define NEO_WEBVIEW_DATA_INDEXED_DB       (1ull << 3)
#define NEO_WEBVIEW_DATA_SERVICE_WORKERS  (1ull << 4)
#define NEO_WEBVIEW_DATA_PERMISSIONS      (1ull << 5)
#define NEO_WEBVIEW_DATA_DOWNLOAD_HISTORY (1ull << 6)
#define NEO_WEBVIEW_DATA_ALL              UINT64_MAX
```

The API SHOULD support an optional time range.

A backend that clears a broader category than requested MUST document and report this limitation.

---

# 27. View settings

Portable settings SHOULD use tri-state values:

```cpp
typedef enum neo_webview_option_state : uint32_t {
    NEO_WEBVIEW_OPTION_DEFAULT = 0,
    NEO_WEBVIEW_OPTION_ENABLED = 1,
    NEO_WEBVIEW_OPTION_DISABLED = 2
} neo_webview_option_state_t;
```

Initial settings:

* JavaScript.
* Web messaging.
* Default context menus.
* Default script dialogs.
* DevTools.
* Status text.
* Zoom controls.
* Swipe navigation.
* Clipboard access.
* Autoplay.
* Background throttling.
* Spellchecking.
* Autofill.
* Default error pages.
* User agent.
* Preferred color scheme.
* Transparent background.

Unsupported settings MUST be discoverable through capabilities.

---

# 28. Utility functionality

The following SHOULD be portable but capability-gated:

## 28.1 Find in page

* Start search.
* Next match.
* Previous match.
* Stop search.
* Match count.
* Active match.

## 28.2 Printing

* Show native print dialog.
* Print to PDF.

## 28.3 Capture

* Capture visible viewport.
* Capture full page where supported.
* PNG output.
* JPEG output where supported.

## 28.4 DevTools

Portable operations:

* Open DevTools.
* Close DevTools.
* Query whether DevTools are open.

Backend debugging protocols MUST remain backend-specific extensions.

## 28.5 Zoom

* Get zoom factor.
* Set zoom factor.
* Reset zoom.

## 28.6 Audio

* Query whether audio is playing.
* Mute/unmute where supported.

---

# 29. Capability discovery

## 29.1 Requirement

Capability discovery is mandatory.

Applications MUST be able to distinguish:

* Unsupported.
* Natively supported.
* Emulated.
* Limited.
* Supported only from a particular runtime version.

## 29.2 Capability identifiers

Known capabilities SHOULD use an enum for generated type safety.

Examples:

```text
custom_scheme
script_document_start
script_document_end
script_isolated_world
script_all_frames
message_origin
message_subframes
profile_named
profile_ephemeral
cookies
clear_data_by_time
downloads
download_pause
permissions
permission_persistence
network_observation
network_interception
print_dialog
print_pdf
capture_viewport
capture_full_page
devtools
find
transparent_background
composition
```

## 29.3 Capability information

```cpp
typedef struct neo_webview_capability_info {
    uint32_t size;
    uint32_t version;

    neo_webview_support_level_t support;
    uint32_t capability_version;
    uint64_t flags;
    neo_webview_string_view_t details;
} neo_webview_capability_info_t;
```

Flags MAY describe:

```text
CREATE_TIME_ONLY
MAIN_FRAME_ONLY
NO_SUBRESOURCES
NO_ASYNC_DECISION
NO_PERSISTENCE
EMULATED_WITH_SCRIPT
REQUIRES_RUNTIME_VERSION
REQUIRES_APPLICATION_ENTITLEMENT
```

Capabilities may depend on:

* NeoWebView backend version.
* Installed WebView2 Runtime.
* macOS version.
* WebKitGTK version.
* Distribution build options.

---

# 30. Native extension APIs

## 30.1 Extension query

Advanced backend APIs MUST be exposed through versioned extension tables, not by polluting the portable ABI.

```cpp
neo_webview_result_t
neo_webview_query_extension(
    const void* object,
    neo_webview_string_view_t extension_name,
    uint32_t minimum_version,
    const void** extension_table);
```

Possible names:

```text
neowebview.webview2.v1
neowebview.webview2.composition.v1
neowebview.webview2.devtools.v1
neowebview.webkit.cocoa.v1
neowebview.webkitgtk.v1
```

## 30.2 Native handles

```cpp
typedef enum neo_webview_native_handle_kind : uint32_t {
    NEO_WEBVIEW_NATIVE_HANDLE_NONE = 0,
    NEO_WEBVIEW_NATIVE_HANDLE_WIN32_HWND = 1,
    NEO_WEBVIEW_NATIVE_HANDLE_COCOA_NSWINDOW = 2,
    NEO_WEBVIEW_NATIVE_HANDLE_COCOA_NSVIEW = 3,
    NEO_WEBVIEW_NATIVE_HANDLE_GTK_WINDOW = 4,
    NEO_WEBVIEW_NATIVE_HANDLE_GTK_WIDGET = 5,
    NEO_WEBVIEW_NATIVE_HANDLE_WEBVIEW2_CONTROLLER = 6,
    NEO_WEBVIEW_NATIVE_HANDLE_WEBVIEW2_CORE = 7,
    NEO_WEBVIEW_NATIVE_HANDLE_WKWEBVIEW = 8,
    NEO_WEBVIEW_NATIVE_HANDLE_WEBKITGTK_WEBVIEW = 9
} neo_webview_native_handle_kind_t;

typedef struct neo_webview_native_handle {
    uint32_t size;
    uint32_t version;
    neo_webview_native_handle_kind_t kind;
    void* value;
} neo_webview_native_handle_t;
```

Portable accessors MUST accept a requested kind and return `NEO_WEBVIEW_ERROR_NOT_SUPPORTED` when the active object/backend cannot provide it. This avoids an ambiguous raw pointer whose meaning changes by platform. At minimum, a NeoWebView-owned window MUST expose its top-level window and content-host handles, and an embedded view MUST expose its browser view/controller handle where available.

The managed representation SHOULD be a small value type containing `NeoNativeHandleKind` and `nint Value`. It MUST provide named access or conversion helpers such as `GetWin32Hwnd()` so integrating with an existing Win32 API does not require generated interop types or a separate platform assembly.

Native handles MUST be documented as:

* Borrowed.
* Backend-specific.
* Valid only while the NeoWebView owner remains alive.
* Usable only on the appropriate UI thread.
* Not releasable by the caller unless explicitly retained through the backend API.

---

# 31. Managed interop implementation

## 31.1 Generated code contract

`NeoWebView.CodeGen` shall use CppAst.CodeGen to generate low-level declarations from the authoritative umbrella header `native/include/neowebview.h`. The generator MUST be a non-packable console project under `src/` and MUST reference the pinned CppAst.CodeGen package centrally used by the repository.

Generated files MUST be written under `src/NeoWebView/Generated/Interop/`, checked into source control, and compiled directly into `NeoWebView.dll`. The generated namespace MUST be `NeoWebView.Interop.Generated`. Generated classes, structures, enums, constants, imports and callback types MUST all be `internal`.

The generated output MUST preserve:

* Exact native field layout.
* Exact enum values.
* Pointer types.
* Calling convention.
* Callback signatures.
* Exported entry-point names.

The generator MUST follow these rules:

* Resolve input and output paths from the repository root rather than the caller's current directory.
* Validate expected repository markers, including `src/NeoWebView.slnx` and `native/include/neowebview.h`, before creating or deleting output files.
* Configure include folders explicitly and parse one authoritative umbrella header.
* Use CppAst.CodeGen mapping rules for pointer/ref direction, opaque handles and exceptional signatures instead of post-generation regular-expression edits; fixed enum underlying types MUST come directly from the typed-enum declarations.
* Set the default namespace, internal native-methods class, logical library-name expression and output paths explicitly.
* Dispatch output per include file when it keeps generated diffs reviewable, while preserving stable filenames.
* Enable generation compatible with disabled runtime marshalling.
* Print all parser/converter diagnostics and exit nonzero on any error before accepting output.
* Emit a standard copyright and `DO NOT EDIT` header and only narrowly scoped warning suppressions with comments.
* Remove stale generated files only inside the validated generated-output directory.

Native entry points SHOULD use source-generated `[LibraryImport]` declarations in an `internal static unsafe partial` class. A manually maintained partial declaration in the same namespace MUST define the shared logical library name `neowebview_native`; the string MUST NOT be repeated in every generated file.

The generation process MUST be deterministic and idempotent. Given identical headers, generator version and configuration, it MUST produce byte-for-byte identical relative paths and file contents. Generated output MUST NOT contain timestamps, absolute paths, machine-specific include paths or nondeterministic member ordering.

The normal regeneration command SHOULD be:

```text
dotnet run --project src/NeoWebView.CodeGen/NeoWebView.CodeGen.csproj --configuration Release
```

CI MUST run the generator and fail when regeneration changes tracked output. The generator project and generated source MUST compile in the normal solution build; consuming applications MUST never run the generator.

## 31.2 Runtime marshalling

The generated interop MUST use only blittable types and pointers.

The `NeoWebView` project SHOULD enable:

```xml
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
```

No generated function should require automatic string, delegate, array, object, COM, or structure marshalling. Generated code MUST use pointers, unmanaged function pointers and blittable structures; manually maintained internal helpers perform UTF-8 conversion and ownership-safe wrapping.

## 31.3 Native callbacks

Callbacks SHOULD be implemented with:

* Static managed methods.
* `UnmanagedCallersOnly`.
* Unmanaged function pointers.
* Explicit context pointers.

Microsoft’s current native-interop guidance recommends unmanaged function pointers and `UnmanagedCallersOnly` rather than delegate marshaling for unmanaged callbacks. Conceptual shape:

```csharp
[UnmanagedCallersOnly]
private static unsafe void OnEnvironmentCreated(
    void* context,
    NeoResult result,
    NeoEnvironmentHandle* environment,
    NeoErrorHandle* error)
{
    // Resolve operation from context and complete it.
}
```

No exception may escape an unmanaged callback.

## 31.4 Handle management

Managed native objects MUST be wrapped using `SafeHandle`-derived types.

Safe handles are preferred over custom finalizers for unmanaged resource ownership. Since a safe-handle finalizer may run on a non-UI thread, the native release function MUST safely queue final UI destruction.

The public managed object SHOULD own one safe handle and MUST NOT expose raw pointers by default.

## 31.5 Native library loading

Generated interop in `NeoWebView.dll` SHOULD refer to the native library using the logical name:

```text
neowebview_native
```

The .NET runtime resolves normal `.dll`, `.so`, `lib*.so`, `.dylib`, and `lib*.dylib` name variations. A resolver MAY be installed in `NeoWebView.dll` to provide better RID-aware diagnostics and development-build probing. Direct P/Invoke MAY be enabled for release applications that always package the required native library. Direct calls can reduce indirection, but the application then fails at startup if the native library or entry point is unavailable.

## 31.6 Task completion

Each managed asynchronous operation SHOULD use:

```csharp
TaskCompletionSource<T>(
    TaskCreationOptions.RunContinuationsAsynchronously)
```

Completion MUST use `TrySetResult`, `TrySetException`, or `TrySetCanceled`.

Every success, failure, cancellation, disposal and shutdown path MUST complete the task exactly once. Using asynchronous continuations prevents arbitrary user code from running on the native callback stack.

## 31.7 Cancellation

The managed operation state MUST retain:

* Task completion source.
* Cancellation registration.
* Native operation handle.
* Native callback context.
* Completion state.

The callback context MUST remain valid until native completion, even when the caller has canceled its wait.

Cancellation races MUST be resolved atomically.

## 31.8 Event dispatch

Notification events may use normal C# events:

```csharp
public event EventHandler<NavigationCompletedEventArgs>? NavigationCompleted;
```

Decision events SHOULD use a single asynchronous policy handler rather than multicast events:

```csharp
public Func<NavigationRequest, ValueTask<NavigationDecision>>?
    NavigationRequested { get; set; }
```

This avoids ambiguous conflicts between multiple subscribers.

For an asynchronous decision:

1. The managed callback immediately defers the native decision.
2. The native callback returns.
3. The handler is awaited.
4. The result is dispatched to the UI thread if necessary.
5. The native decision is completed.
6. Exceptions apply the documented safe default.

## 31.9 Synchronization context

Standalone mode MUST install a `NeoDispatcherSynchronizationContext` on the UI thread.

The context MUST:

* Post through `neo_webview_app_dispatch`.
* Preserve normal `await` continuations on the UI thread.
* Reject or safely ignore posts after shutdown.
* Avoid running continuations inline from native callbacks.

---

# 32. Proposed managed API

The exact naming may evolve before API freeze.

## 32.1 Application

```csharp
namespace NeoWebView;

public sealed class NeoApplication : IAsyncDisposable
{
    public static int Run(
        NeoApplicationOptions options,
        Func<NeoApplication, ValueTask> startup);

    public static NeoApplication AttachToCurrentThread(
        NeoApplicationOptions options);

    public NeoDispatcher Dispatcher { get; }
    public IReadOnlyCollection<NeoWindow> Windows { get; }
    public NeoWindow? MainWindow { get; set; }
    public NeoApplicationShutdownMode ShutdownMode { get; set; }

    public ValueTask<NeoEnvironment> CreateEnvironmentAsync(
        NeoEnvironmentOptions? options = null,
        CancellationToken cancellationToken = default);

    public NeoWindow CreateWindow(
        NeoWindowOptions? options = null);

    public void Shutdown(int exitCode = 0);
}
```

`Run` blocks the process main thread while the native event loop executes.

The startup delegate is initialization logic. Returning from it does not automatically shut down the application.

`AttachToCurrentThread` selects embedded mode. It MUST be called on the host UI thread after the host GUI framework is initialized, and it MUST NOT run or replace the host event loop. The exact naming may change, but standalone and embedded initialization MUST remain explicit and hard to confuse.

## 32.2 Environment

```csharp
public sealed class NeoEnvironment : IAsyncDisposable
{
    public NeoRuntimeInfo RuntimeInfo { get; }

    public ValueTask<NeoProfile> CreateProfileAsync(
        NeoProfileOptions? options = null,
        CancellationToken cancellationToken = default);

    public ValueTask<NeoWebView> CreateWebViewAsync(
        NeoWebViewHost host,
        NeoWebViewOptions? options = null,
        CancellationToken cancellationToken = default);

    public NeoCapabilityInfo GetCapability(
        NeoCapability capability);
}
```

## 32.3 Profile

```csharp
public sealed class NeoProfile : IAsyncDisposable
{
    public bool IsEphemeral { get; }

    public ValueTask<IReadOnlyList<NeoCookie>> GetCookiesAsync(
        Uri uri,
        CancellationToken cancellationToken = default);

    public ValueTask SetCookieAsync(
        NeoCookie cookie,
        CancellationToken cancellationToken = default);

    public ValueTask DeleteCookieAsync(
        NeoCookie cookie,
        CancellationToken cancellationToken = default);

    public ValueTask ClearDataAsync(
        NeoBrowsingDataKinds kinds,
        NeoTimeRange? timeRange = null,
        CancellationToken cancellationToken = default);
}
```

## 32.4 Window

```csharp
public sealed class NeoWindow : IAsyncDisposable
{
    public ulong Id { get; }
    public string Title { get; set; }
    public NeoPoint Position { get; set; }
    public NeoSize ClientSize { get; set; }
    public NeoSize MinimumClientSize { get; set; }
    public NeoSize MaximumClientSize { get; set; }
    public bool IsVisible { get; }
    public bool IsFocused { get; }
    public double ScaleFactor { get; }
    public NeoWindowState State { get; set; }
    public NeoWindow? Owner { get; }

    public event EventHandler<NeoWindowClosingEventArgs>? Closing;
    public event EventHandler? Closed;
    public event EventHandler<NeoWindowBoundsChangedEventArgs>? BoundsChanged;
    public event EventHandler<NeoWindowScaleFactorChangedEventArgs>? ScaleFactorChanged;
    public event EventHandler? FocusChanged;

    public void Show();
    public void Hide();
    public void Activate();
    public void Close();

    public NeoNativeHandle GetNativeHandle(
        NeoNativeHandleKind kind);
}
```

`NeoWindowOptions` SHOULD include owner, title, initial logical bounds, startup location, minimum/maximum size, decorations, resizability, visibility, initial state, always-on-top state, taskbar/dock visibility and background color. Unsupported or constrained options MUST be reported consistently rather than silently interpreted as a different option.

## 32.5 Native host integration

The public managed API SHOULD provide the following conceptual shapes in the same `NeoWebView.dll` assembly:

```csharp
public enum NeoNativeHandleKind
{
    Win32Hwnd,
    CocoaNSWindow,
    CocoaNSView,
    GtkWindow,
    GtkWidget,
    WebView2Controller,
    WebView2Core,
    WkWebView,
    WebKitGtkWebView
}

public readonly record struct NeoNativeHandle(
    NeoNativeHandleKind Kind,
    nint Value);

public sealed class NeoWebViewHost
{
    public static NeoWebViewHost FillWindow(NeoWindow window);
    public static NeoWebViewHost FromWin32Hwnd(nint hwnd);
    public static NeoWebViewHost FromCocoaNSView(nint nsView);
    public static NeoWebViewHost FromGtkWidget(nint gtkWidget);
    public static NeoWebViewHost FromNativeParent(NeoNativeHandle handle);
}
```

Named host factories MUST validate the current operating system, reject zero handles, document UI-thread and lifetime requirements, and keep the handle borrowed. `FromWin32Hwnd` MUST accept the numeric value returned by normal Win32 interop without requiring a generated NeoWebView type. Advanced code MAY use the generic typed descriptor, but the normal path MUST NOT rely on an unlabelled `nint`.

## 32.6 WebView

```csharp
public sealed class NeoWebView : IAsyncDisposable
{
    public Uri? Source { get; }
    public string Title { get; }
    public bool IsLoading { get; }
    public bool CanGoBack { get; }
    public bool CanGoForward { get; }

    public Func<NeoNavigationRequest, ValueTask<NeoNavigationDecision>>?
        NavigationRequested { get; set; }

    public Func<NeoPermissionRequest, ValueTask<NeoPermissionDecision>>?
        PermissionRequested { get; set; }

    public Func<NeoDownloadRequest, ValueTask<NeoDownloadDecision>>?
        DownloadRequested { get; set; }

    public Func<NeoNewWindowRequest, ValueTask<NeoNewWindowDecision>>?
        NewWindowRequested { get; set; }

    public event EventHandler<NeoNavigationCompletedEventArgs>?
        NavigationCompleted;

    public event EventHandler<NeoWebMessageReceivedEventArgs>?
        MessageReceived;

    public ValueTask NavigateAsync(
        Uri uri,
        CancellationToken cancellationToken = default);

    public ValueTask LoadHtmlAsync(
        string html,
        Uri? baseUri = null,
        CancellationToken cancellationToken = default);

    public ValueTask<string?> EvaluateScriptAsync(
        string script,
        CancellationToken cancellationToken = default);

    public ValueTask PostMessageAsync(
        string json,
        CancellationToken cancellationToken = default);

    public void Reload();
    public void Stop();
    public void GoBack();
    public void GoForward();
}
```

## 32.7 Application example

```csharp
using NeoWebView;

return NeoApplication.Run(
    new NeoApplicationOptions
    {
        ApplicationName = "NeoWebView Sample"
    },
    async app =>
    {
        var environment = await app.CreateEnvironmentAsync(
            new NeoEnvironmentOptions
            {
                CustomSchemes =
                [
                    NeoCustomScheme.Application("app")
                ]
            });

        var profile = await environment.CreateProfileAsync(
            new NeoProfileOptions
            {
                Name = "default",
                IsEphemeral = false
            });

        var window = app.CreateWindow(
            new NeoWindowOptions
            {
                Title = "NeoWebView",
                Width = 1200,
                Height = 800
            });

        var webView = await environment.CreateWebViewAsync(
            NeoWebViewHost.FillWindow(window),
            new NeoWebViewOptions
            {
                Profile = profile,
                BridgeOrigins = ["app://neowebview"]
            });

        webView.MessageReceived += (_, e) =>
        {
            Console.WriteLine(e.Json);
        };

        await webView.NavigateAsync(
            new Uri("app://neowebview/index.html"));

        window.Show();
    });
```

---

# 33. Platform backend requirements

## 33.1 Windows backend

The Windows implementation MUST use:

* Win32 for application windows and the message loop.
* WebView2 Win32 COM interfaces.
* A COM STA UI thread.
* Child-window or supported controller hosting.
* WebView2 environment/controller/core separation.
* WebView2 event registration tokens with deterministic removal.
* WebView2 web messaging.
* Web resource handling for custom content.
* WebView2 profiles where available.
* Per-monitor-DPI-aware window sizing and `WM_DPICHANGED` handling when standalone mode controls process startup.
* Win32 owner relationships for framework-created dialogs and popups.

It MUST:

* Detect a missing WebView2 Runtime.
* Return an actionable `RUNTIME_UNAVAILABLE` error.
* Report installed runtime version.
* Allow optional fixed-version path configuration.
* Avoid a dependency on WPF, WinForms and WinUI.
* Remove all COM event handlers before final release.
* Handle browser process failures.
* Ensure COM callbacks cannot outlive wrapper objects.
* Expose each framework-created window's `HWND` and each embedded/view controller handle through an explicitly typed accessor.
* Avoid changing process DPI awareness in embedded mode after the host has initialized it.

## 33.2 macOS backend

The macOS implementation MUST use:

* `NSApplication`.
* `NSWindow`.
* `NSView`.
* `WKWebView`.
* `WKWebViewConfiguration`.
* `WKWebsiteDataStore`.
* `WKUserContentController`.
* `WKURLSchemeHandler`.
* Navigation and UI delegates.
* Owned/child `NSWindow` relationships for framework-created dialogs and popups.

It MUST:

* Execute UI work on the main thread.
* Compile under ARC.
* Use weak delegate relationships where necessary to prevent cycles.
* Remove script handlers during destruction.
* Avoid private selectors and SPI.
* Handle web-content process termination.
* Report limitations of general network interception.
* Support both Intel and Apple Silicon.
* Expose `NSWindow*`, content `NSView*`, and `WKWebView*` only through explicitly typed borrowed-handle accessors.

## 33.3 Linux backend

The Linux implementation MUST use:

* GTK 3.
* WebKitGTK 4.1.
* GLib/GObject.
* `WebKitWebContext`.
* `WebKitWebView`.
* `WebKitUserContentManager`.
* `WebKitWebsiteDataManager`.
* Registered URI schemes.
* GTK’s normal X11/Wayland abstraction.
* GTK transient-window relationships for framework-created dialogs and popups.

`WebKitWebView` is itself a GTK widget responsible for content rendering and event forwarding. The Linux implementation MUST:

* Maintain GObject reference ownership correctly.
* Disconnect all signal handlers before final release.
* Integrate with the owning `GMainContext`.
* Avoid blocking the GLib main loop.
* Support asynchronous policy and scheme responses.
* Detect missing runtime dependencies where possible.
* Document required Ubuntu packages.
* Enable normal WebKit sandbox behavior.
* Avoid relying on deprecated libsoup 2 APIs.
* Expose `GtkWindow*`, host `GtkWidget*`, and `WebKitWebView*` only through explicitly typed borrowed-handle accessors.

---

# 34. Performance requirements

## 34.1 General principles

NeoWebView MUST:

* Avoid synchronous browser-process IPC in hot managed getters.
* Cache title, URI, navigation and history state from events.
* Avoid allocating an error object on successful calls.
* Avoid delegate marshaling.
* Avoid automatic string marshaling.
* Avoid copying file-backed application resources through C#.
* Avoid holding global locks while invoking callbacks.
* Avoid running user continuations on native callback stacks.
* Avoid unnecessary UI-thread dispatch when already on the correct thread.
* Coalesce high-frequency progress and resize notifications where appropriate.

## 34.2 Allocation expectations

The managed layer SHOULD allocate:

* No managed objects for retain/release.
* No managed objects for simple cached property getters.
* No delegate wrapper per native callback.
* One operation state for each asynchronous call.
* Event arguments only when an event has subscribers.
* Strings only when the application requests decoded text.

## 34.3 Resource limits

Configurable limits SHOULD include:

* Maximum incoming message size.
* Maximum outgoing message size.
* Maximum resource-header size.
* Maximum in-memory resource-response size.
* Decision timeout.
* Maximum pending asynchronous decisions.
* Maximum pending dispatch callbacks.

Suggested default message limit:

```text
16 MiB
```

Applications requiring larger payloads should use files, streams, or future shared-buffer APIs.

## 34.4 Benchmarks

The repository MUST include benchmarks for:

* Native dispatch latency.
* Managed-to-native call overhead.
* Native-to-managed callback overhead.
* Small-message throughput.
* Large-message throughput.
* JavaScript evaluation round trips.
* Local asset loading.
* File-backed custom-scheme responses.
* View creation time.
* Environment creation time.
* Idle CPU.
* Memory after repeated view creation and destruction.

Benchmarks MUST report results separately per engine and platform. They MUST NOT imply that engine startup performance is controlled entirely by NeoWebView.

Performance acceptance should primarily use regression thresholds against stored baselines rather than one absolute number across all machines.

---

# 35. Security requirements

## 35.1 Default posture

NeoWebView MUST be secure by default.

The default configuration MUST:

* Disable privileged messaging for arbitrary remote origins.
* Deny sensitive permissions without an application decision.
* Deny TLS error bypass.
* Cancel unhandled file choosers.
* Cancel unhandled downloads.
* Cancel unhandled new-window requests.
* Avoid automatically launching unknown URI schemes.
* Avoid exposing native handles to web content.
* Avoid exposing arbitrary managed objects to JavaScript.
* Avoid evaluating web-provided strings as native commands.

## 35.2 Origin model

Application content SHOULD have one explicit origin, such as:

```text
app://neowebview
```

The framework MUST avoid treating all custom-scheme hosts as the same origin when a backend distinguishes them.

The effective origin MUST be available through diagnostics.

## 35.3 Bridge isolation

Bridge configuration MUST be immutable after navigation begins unless the view is recreated or the implementation can safely update it.

Remote navigation MUST be evaluated against the origin allowlist.

Subframes MUST not receive bridge access by default.

## 35.4 Resource-provider security

Managed resource providers MUST treat all request data as untrusted.

The framework MUST not concatenate URI paths directly into filesystem paths.

## 35.5 Logging

Normal logs MUST NOT include:

* Cookie values.
* Authorization headers.
* Passwords.
* Client-certificate private data.
* Full message bodies by default.
* File contents.
* JavaScript source by default.

Verbose diagnostic logging requiring sensitive information must be explicit.

---

# 36. Diagnostics and logging

## 36.1 Runtime information

The native API MUST expose:

* NeoWebView version.
* ABI version.
* Backend name.
* Backend version.
* Browser engine version.
* WebView2 Runtime version and channel.
* macOS version.
* WebKitGTK version.
* GTK version.
* Architecture.
* Debug/release status.
* Enabled build features.

## 36.2 Logging callback

```cpp
typedef enum neo_webview_log_level : uint32_t {
    NEO_WEBVIEW_LOG_TRACE = 0,
    NEO_WEBVIEW_LOG_DEBUG,
    NEO_WEBVIEW_LOG_INFORMATION,
    NEO_WEBVIEW_LOG_WARNING,
    NEO_WEBVIEW_LOG_ERROR,
    NEO_WEBVIEW_LOG_CRITICAL
} neo_webview_log_level_t;
```

The callback SHOULD receive:

* Level.
* Category.
* UTF-8 message.
* Thread identifier.
* Monotonic timestamp.
* Optional native error code.
* Associated object identifier.

Logging MUST be safe from any native thread.

The managed logger adapter SHOULD integrate with `Microsoft.Extensions.Logging` through an optional package, not a core dependency.

## 36.3 Process failures

The view MUST report:

* Web process exited.
* Browser process exited.
* Render process unresponsive where detectable.
* Crash versus normal exit where detectable.
* Recommended recovery action.

Applications SHOULD be able to recreate a view after process failure.

---

# 37. Packaging

## 37.1 NuGet layout

Suggested package layout:

```text
lib/net8.0/NeoWebView.dll

runtimes/win-x64/native/neowebview_native.dll
runtimes/win-arm64/native/neowebview_native.dll
runtimes/osx-x64/native/libneowebview_native.dylib
runtimes/osx-arm64/native/libneowebview_native.dylib
runtimes/linux-x64/native/libneowebview_native.so
runtimes/linux-arm64/native/libneowebview_native.so
```

A single package containing one managed runtime assembly and the RID-specific native assets is simplest initially. The package MUST NOT contain separate managed interop assemblies or the `NeoWebView.CodeGen` tool.

Native assets MAY later be split into RID-specific packages if package size or release management requires it.

## 37.2 Managed target framework

The public managed library SHOULD initially target `net8.0` so that it can be consumed by newer .NET versions.

CI MUST explicitly test NativeAOT publishing with the current supported .NET LTS SDK.

No API may depend on JIT-only code generation.

## 37.3 Windows runtime deployment

NeoWebView MUST document:

* Evergreen detection.
* Bootstrapper deployment.
* Offline Evergreen deployment.
* Fixed-version configuration.
* Missing-runtime errors.

The WebView2 Runtime itself is not part of the NeoWebView NuGet package by default.

## 37.4 macOS deployment

NeoWebView MUST document:

* App bundle creation.
* NativeAOT app-bundle layout.
* `Info.plist`.
* Camera/microphone usage descriptions.
* Entitlements where required.
* Code signing.
* Notarization.
* Universal versus architecture-specific builds.

## 37.5 Linux deployment

NeoWebView MUST document required runtime packages for each supported Ubuntu version.

The NuGet package ships the NeoWebView wrapper but not GTK or WebKitGTK.

A startup diagnostic SHOULD identify missing libraries and provide their logical package names.

---

# 38. Testing strategy

## 38.1 Native unit tests

Native tests MUST cover:

* Reference counting.
* Thread-safe retain/release.
* Operation cancellation.
* Decision state machine.
* Decision timeout.
* UTF-8 validation.
* Structure-size handling.
* Error lifetime.
* Dispatch queue.
* Window registry and shutdown modes.
* Owned-window and popup lifetime transitions.
* Event subscription teardown.
* Capability lookup.
* Path normalization.
* Resource response streaming.

## 38.2 ABI tests

ABI tests MUST verify:

* Structure sizes.
* Field offsets.
* Enum values.
* Calling conventions.
* Exported symbol list.
* Fixed enum underlying types and values in native and generated managed code.
* Public-header compatibility with the supported C++20 compilers.
* Compatibility between generated C# layout and native layout.
* Older caller structure sizes against newer libraries.

A small independent C++ test application MUST use only the public header and the shared library's C ABI.

## 38.3 Managed tests

Managed tests MUST cover:

* Safe-handle lifetime.
* Callback context lifetime.
* Task completion on every path.
* Cancellation races.
* Disposal races.
* Event unsubscription.
* No callback after disposal.
* Exception containment.
* UTF-8 encoding and decoding.
* Native library resolution.
* NativeAOT compilation.
* Generated interop remains internal to `NeoWebView.dll`.
* No separate managed interop assembly is produced or packaged.
* Typed native-host validation and wrong-platform errors.
* Multi-window registry, close ordering and shutdown behavior.
* Popup creation-context lifetime and exactly-once completion.

## 38.4 Browser conformance pages

Test pages MUST exercise:

* Navigation.
* Redirects.
* History.
* JavaScript evaluation.
* Promise results.
* JavaScript exceptions.
* Document-start injection.
* Messaging.
* Large messages.
* Custom schemes.
* Resource streaming.
* Cookies.
* Local storage.
* IndexedDB.
* Permissions.
* Downloads.
* Dialogs.
* File input.
* New windows.
* Process termination recovery.
* Popup opener/environment relationship and requested window features.

## 38.5 Stress tests

Required stress scenarios:

* Repeated creation and destruction of views.
* Multiple concurrent views.
* Repeated creation, activation and closure of multiple top-level windows.
* Owned-window and popup closure while the owner or application is shutting down.
* Popup request storms, limits and decision timeouts.
* Repeated environment creation where supported.
* 100,000 small messages.
* Rapid navigation and cancellation.
* Closing a window during navigation.
* Closing a view with deferred decisions.
* Cancellation during JavaScript evaluation.
* Shutdown with pending operations.
* Resource-stream cancellation.
* Browser-process failure.

## 38.6 Sanitizers and analysis

CI SHOULD run:

* AddressSanitizer on Linux and macOS.
* UndefinedBehaviorSanitizer on Linux and macOS.
* ThreadSanitizer on selected common-code tests.
* Clang static analysis.
* Compiler warnings as errors.
* .NET analyzers.
* NativeAOT trimming analysis.

Windows SHOULD use Application Verifier or equivalent diagnostics where practical.

## 38.7 Linux display tests

Linux integration tests SHOULD execute under:

* Xvfb/X11.
* A Wayland-capable environment when available.

Tests that require a real GPU or interactive desktop should be separated from the normal unit-test job.

---

# 39. CI and release requirements

CI MUST build:

* Windows x64 debug/release.
* Windows ARM64 release.
* macOS x64 debug/release.
* macOS ARM64 debug/release.
* Linux x64 debug/release.
* Linux ARM64 release or cross-build plus native execution on ARM64 infrastructure.
* Managed JIT tests.
* Managed NativeAOT samples.
* C ABI test executable.
* Generated interop verification.

Release artifacts MUST include:

* NuGet package.
* Native binaries.
* Public headers.
* Debug symbols.
* Source-link information for the managed assembly.
* Checksums.
* Third-party notices.
* ABI report.
* Runtime dependency documentation.

Release builds SHOULD be reproducible where toolchains permit.

---

# 40. Implementation phases

## Phase 0 — Repository and ABI foundation

Deliverables:

* Repository structure.
* CMake presets.
* Clang builds on all three operating systems.
* Public header skeleton.
* Version and ABI APIs.
* Reference-counted object base.
* Error object.
* Logging.
* Dispatcher abstraction.
* Deterministic `NeoWebView.CodeGen` integration.
* C ABI conformance test.

Acceptance:

* Empty native library loads from JIT and NativeAOT applications.
* Generated layouts match native layouts.
* The generated declarations compile internally into the single `NeoWebView.dll` assembly.
* CI builds all primary targets.

## Phase 1 — Windows vertical slice

Deliverables:

* Win32 application loop.
* Multi-window Win32 windowing layer.
* WebView2 environment.
* Default profile.
* View creation.
* Navigation.
* Title/source events.
* JavaScript evaluation.
* Messaging.
* Application custom scheme.
* Typed `HWND` embedding and handle access.
* Basic managed API.
* NativeAOT sample.

Acceptance:

* A NativeAOT executable opens local HTML.
* JavaScript and C# exchange messages.
* The executable does not use WPF, WinForms, WinUI, ASP.NET Core, or Node.js.
* Missing WebView2 Runtime produces a clear error.
* One application can create, activate and independently close multiple windows.
* A view can be attached to a caller-owned `HWND` without a second event loop.

## Phase 2 — macOS vertical slice

Deliverables:

* Cocoa application loop.
* NSWindow hosting.
* WKWebView backend.
* WKWebsiteDataStore mapping.
* Script bridge.
* Custom scheme.
* Navigation policy.
* x64 and ARM64 output.
* Basic app bundle sample.

Acceptance:

* Same managed sample runs with no platform-specific application code.
* NativeAOT app opens packaged HTML and exchanges messages.

## Phase 3 — Linux vertical slice

Deliverables:

* GTK application loop.
* GTK window.
* WebKitGTK view.
* WebKitWebContext/profile mapping.
* Script bridge.
* Custom scheme.
* Ubuntu dependency diagnostics.
* x64 and ARM64 packages.

Acceptance:

* Same managed sample runs on Ubuntu 22.04 and 24.04.
* X11 and Wayland behavior is validated.
* Missing WebKitGTK dependency is documented clearly.

## Phase 4 — Portable browser functionality

Deliverables:

* Navigation decisions.
* Managed popup and new-window handling through tracked `NeoWindow` instances.
* Downloads.
* Permissions.
* JavaScript dialogs.
* File chooser.
* Cookies.
* Browsing-data clearing.
* Process-failure event.
* Capability system.
* Native-handle escape hatch.

The Phase 4 implementation uses paired native/managed ABI 1.6. Download requests carry a tracked
download handle and produce started, progress (when the engine reports it), and exactly one terminal
notification. `NeoDownloadDecision.Default` preserves engine destination handling, while an absent,
failed, or expired handler cancels safely. Windows supports pause/resume; the other current backends
report that capability as unavailable.

Popup handlers create one opener-compatible target with `NeoNewWindowRequest.CreateViewAsync` and
complete with `NeoNewWindowDecision.UseView`. Target creation may be started only once for a request;
the target uses the opener profile and is hosted by a normal tracked `NeoWindow` or borrowed parent. The short-lived creation context maps to WebView2's
new-window deferral, the supplied `WKWebViewConfiguration`, or WebKitGTK's related-view relationship.

Capability reporting reflects backend limits: Windows does not expose file-chooser interception;
Linux does not expose portable client-certificate or TLS-error decisions; macOS does not expose
portable client-certificate or fullscreen decisions. Popup and dialog decisions that the WebKit
callback requires synchronously use their documented safe default if a managed handler does not
complete inline. macOS remains source-validated only when development occurs on a non-macOS host.

## Phase 5 — Hardening

Deliverables:

* Stress suite.
* Sanitizer-clean native implementation.
* Disposal-race tests.
* Performance benchmarks.
* Security review.
* API documentation.
* ABI compatibility tests.
* Packaging documentation.

## Phase 6 — v1 release

Requirements:

* Public API review complete.
* ABI frozen at major version 1.
* Samples for all platforms.
* NativeAOT validation.
* Third-party notices.
* Release pipeline.
* Supported-platform documentation.
* Known limitations documented.

---

# 41. v1 acceptance criteria

NeoWebView v1 is complete when all the following are true:

1. One managed sample source runs on Windows, macOS, and Ubuntu.
2. The sample can be published with NativeAOT.
3. The sample opens a native window containing local HTML.
4. No Chromium runtime is bundled by NeoWebView.
5. No Node.js runtime is required.
6. JavaScript can call C# through normalized messaging.
7. C# can send messages and evaluate JavaScript.
8. Navigation policy is controllable.
9. Custom-scheme assets load without localhost.
10. Persistent and ephemeral profiles work.
11. Cookies can be read, written, deleted and cleared.
12. Downloads are surfaced to the host.
13. Sensitive permissions are surfaced to the host.
14. JavaScript dialogs and file choosers are surfaced.
15. View/process failure is reported.
16. Native objects survive and dispose correctly under stress.
17. No callbacks occur after final disposal.
18. All scheduled tasks complete on success, failure, cancellation or shutdown.
19. The C ABI passes layout and compatibility tests.
20. The native code is sanitizer-clean on supported sanitizer platforms.
21. Capability differences are reported accurately.
22. Missing platform runtime dependencies produce actionable errors.
23. Native and managed API documentation is available.
24. The basic application does not depend on ASP.NET Core, MAUI, WPF, WinForms, WinUI, Avalonia, GTK# or another managed desktop framework.
25. One application can create and independently manage multiple top-level and owned windows.
26. An accepted `window.open` request creates a tracked `NeoWindow`/`NeoWebView` pair with a correct backend opener relationship; an unhandled request is canceled.
27. A managed view can embed into a caller-owned `HWND`, `NSView*`, or `GtkWidget*` through an explicitly typed API.
28. The NuGet package exposes one managed runtime assembly, `NeoWebView.dll`, and loads the correctly named `neowebview_native` asset for each supported RID.

---

# 42. Explicitly deferred items

The following should be tracked after v1:

* WebKitGTK 6.0/GTK 4 backend.
* Alpine/musl validation.
* WPE WebKit backend.
* Shared binary buffers between JavaScript and native code.
* Portable request observation.
* Backend-specific HTTP interception.
* WebView2 composition controller.
* Offscreen rendering.
* DevTools protocol extensions.
* Browser automation.
* Accessibility helpers.
* Native context-menu model.
* Native drag-and-drop.
* Tray icons.
* Application menus.
* Updater.
* Installer tooling.
* Blazor-specific integration package.
* Typed JavaScript/C# RPC generation.
* Browser process pooling controls.
* Multiple independent standalone application loops.

---

# 43. Initial implementation decisions

The implementer should treat these decisions as fixed unless revised explicitly:

1. The native binary interface uses C linkage and the C calling convention; the authoritative header may use C++ syntax supported by CppAst.CodeGen.
2. Exported names use `neo_webview_`.
3. UTF-8 pointer-and-length strings are used.
4. Public structures are size-versioned.
5. Native objects are opaque and reference-counted.
6. Retain and release are callable from any thread.
7. UI destruction is marshalled to the UI thread.
8. Asynchronous callbacks are never invoked inline.
9. Every asynchronous operation completes exactly once.
10. A first-class multi-window standalone native windowing layer is included.
11. Embedding is also supported.
12. Linux v1 uses WebKitGTK 4.1 and GTK 3.
13. WebKitGTK 6.0 support is a separate backend implementation.
14. Windows uses Win32 and WebView2 COM directly.
15. macOS uses public Cocoa and WKWebView APIs.
16. The managed callback layer uses unmanaged function pointers.
17. The managed public API uses safe handles and async/await.
18. No automatic runtime marshalling is required.
19. Custom schemes are the primary portable application-content mechanism.
20. Arbitrary network interception is capability-gated.
21. The JavaScript bridge is origin-restricted by default.
22. Backend-native functionality is exposed through versioned extension tables.
23. NativeAOT is tested as a first-class deployment model.
24. Musl compatibility influences architecture but is not a v1 support commitment.
25. The native library basename is `neowebview_native` on every platform.
26. Native code remains under `native/`; managed code, tools, tests and samples remain under `src/`.
27. `NeoWebView.dll` is the only shipped managed runtime assembly.
28. Generated interop is internal under `NeoWebView.Interop.Generated` and is compiled into `NeoWebView.dll`.
29. CppAst.CodeGen runs only in the non-shipping `NeoWebView.CodeGen` tool and produces deterministic checked-in source.
30. Native parent and escape-hatch handles carry an explicit platform kind; normal users are not expected to pass ambiguous pointers.
31. Web-requested popups are canceled by default and, when accepted, use tracked NeoWebView windows and views.
