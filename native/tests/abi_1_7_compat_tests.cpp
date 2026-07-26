// This fixture deliberately does not include neoastra.h. It models an ABI 1.7
// consumer so later header edits cannot silently update the compatibility test.
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <filesystem>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#define NEO_ABI_1_7_CALL __cdecl
#else
#include <dlfcn.h>
#define NEO_ABI_1_7_CALL
#endif

namespace abi_1_7 {

struct app;
struct error;

struct string_view {
    const uint8_t* data;
    uint64_t length;
};

using log_callback = void (NEO_ABI_1_7_CALL *)(void*, uint32_t, string_view, string_view,
                                               uint64_t, uint64_t, int64_t, uint64_t);

struct app_options {
    uint32_t size;
    uint32_t version;
    string_view application_name;
    uint32_t shutdown_mode;
    uint32_t maximum_pending_dispatches;
    uint32_t reserved;
    log_callback log;
    void* log_context;
};

struct runtime_info {
    uint32_t size;
    uint32_t version;
    string_view backend_name;
    string_view backend_version;
    string_view browser_version;
    string_view operating_system;
    string_view architecture;
    uint64_t build_features;
    uint32_t debug_build;
    uint32_t reserved;
};

static_assert(sizeof(void*) == 8, "ABI 1.7 supports the current 64-bit primary targets");
static_assert(sizeof(string_view) == 16 && offsetof(string_view, length) == 8);
static_assert(sizeof(app_options) == 56 && offsetof(app_options, log) == 40 &&
              offsetof(app_options, log_context) == 48);
static_assert(sizeof(runtime_info) == 104 && offsetof(runtime_info, backend_name) == 8 &&
              offsetof(runtime_info, build_features) == 88 && offsetof(runtime_info, reserved) == 100);

class shared_library final {
public:
    explicit shared_library(const std::filesystem::path& path) {
#if defined(_WIN32)
        handle_ = LoadLibraryW(path.c_str());
#else
        handle_ = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
#endif
        assert(handle_ != nullptr);
    }

    ~shared_library() {
#if defined(_WIN32)
        if (handle_) FreeLibrary(handle_);
#else
        if (handle_) dlclose(handle_);
#endif
    }

    shared_library(const shared_library&) = delete;
    shared_library& operator=(const shared_library&) = delete;

    [[nodiscard]] bool has_symbol(const char* name) const noexcept {
#if defined(_WIN32)
        return GetProcAddress(handle_, name) != nullptr;
#else
        return dlsym(handle_, name) != nullptr;
#endif
    }

    template<typename T>
    [[nodiscard]] T load(const char* name) const noexcept {
#if defined(_WIN32)
        return reinterpret_cast<T>(GetProcAddress(handle_, name));
#else
        return reinterpret_cast<T>(dlsym(handle_, name));
#endif
    }

private:
#if defined(_WIN32)
    HMODULE handle_{};
#else
    void* handle_{};
#endif
};

} // namespace abi_1_7

int main(int argc, char** argv) {
    assert(argc == 2);
    const abi_1_7::shared_library library{std::filesystem::path{argv[1]}};

#define NEO_ABI_1_7_EXPORT(name) assert(library.has_symbol(#name));
#include "abi_1_7_exports.inc"
#undef NEO_ABI_1_7_EXPORT

    using get_version_number = uint32_t (NEO_ABI_1_7_CALL *)();
    using get_version = abi_1_7::string_view (NEO_ABI_1_7_CALL *)();
    using get_runtime_info = int32_t (NEO_ABI_1_7_CALL *)(abi_1_7::runtime_info*, abi_1_7::error**);
    using app_attach = int32_t (NEO_ABI_1_7_CALL *)(const abi_1_7::app_options*, abi_1_7::app**, abi_1_7::error**);
    using app_detach = int32_t (NEO_ABI_1_7_CALL *)(abi_1_7::app*, abi_1_7::error**);
    using app_release = void (NEO_ABI_1_7_CALL *)(abi_1_7::app*);

    const auto major = library.load<get_version_number>("neoastra_get_abi_version_major");
    const auto minor = library.load<get_version_number>("neoastra_get_abi_version_minor");
    const auto version = library.load<get_version>("neoastra_get_version");
    const auto runtime = library.load<get_runtime_info>("neoastra_get_runtime_info");
    const auto attach = library.load<app_attach>("neoastra_app_attach");
    const auto detach = library.load<app_detach>("neoastra_app_detach");
    const auto release = library.load<app_release>("neoastra_app_release");
    assert(major && minor && version && runtime && attach && detach && release);

    assert(major() == 1);
    assert(minor() >= 7);
    const auto semantic_version = version();
    assert(semantic_version.data != nullptr && semantic_version.length != 0);

    abi_1_7::runtime_info info{};
    info.size = sizeof(info);
    info.version = 1;
    abi_1_7::error* error{};
    assert(runtime(&info, &error) == 0);
    assert(error == nullptr && info.backend_name.data != nullptr && info.backend_name.length != 0);

    abi_1_7::app_options options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = 0; // NEOASTRA_APP_SHUTDOWN_EXPLICIT
    abi_1_7::app* application{};
    assert(attach(&options, &application, &error) == 0);
    assert(application != nullptr && error == nullptr);
    assert(detach(application, &error) == 0);
    assert(error == nullptr);
    release(application);
    return 0;
}

#undef NEO_ABI_1_7_CALL
