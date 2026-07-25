#include "neowebview.h"

#include <cassert>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <type_traits>

static_assert(std::is_same_v<std::underlying_type_t<neo_webview_result_t>, int32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_support_level_t>, uint32_t>);
static_assert(sizeof(neo_webview_struct_header_t) == 8);
static_assert(sizeof(neo_webview_string_view_t) == sizeof(void*) + sizeof(uint64_t));
static_assert(offsetof(neo_webview_event_header_t, type) == 8);
static_assert(offsetof(neo_webview_event_header_t, sequence) % alignof(uint64_t) == 0);

int main() {
    assert(neo_webview_get_abi_version_major() == NEO_WEBVIEW_ABI_VERSION_MAJOR);
    assert(neo_webview_get_abi_version_minor() == NEO_WEBVIEW_ABI_VERSION_MINOR);
    auto version = neo_webview_get_version();
    assert(version.data != nullptr && version.length != 0);

    neo_webview_runtime_info_t info{};
    info.size = sizeof(info);
    info.version = 1;
    neo_webview_error_t* error = nullptr;
    assert(neo_webview_get_runtime_info(&info, &error) == NEO_WEBVIEW_OK);
    assert(error == nullptr && info.backend_name.length != 0);

    neo_webview_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEO_WEBVIEW_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED;
    neo_webview_app_t* app = nullptr;
    assert(neo_webview_app_create(&options, &app, &error) == NEO_WEBVIEW_OK);
    assert(app != nullptr && error == nullptr);
    neo_webview_app_quit(app, 7);
    neo_webview_app_release(app);
    return 0;
}
