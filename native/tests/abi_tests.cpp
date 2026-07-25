#include "neowebview.h"

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <type_traits>

static_assert(std::is_same_v<std::underlying_type_t<neo_webview_result_t>, int32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_support_level_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_process_failure_kind_t>, uint32_t>);
static_assert((NEO_WEBVIEW_PROCESS_FAILURE_KIND_MASK & NEO_WEBVIEW_PROCESS_FAILURE_CRASHED) == 0);
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

    neo_webview_window_options_t window_options{};
    window_options.size = sizeof(window_options);
    window_options.version = 1;
    window_options.bounds = {100, 100, 800, 600};
    window_options.flags = 3;
    neo_webview_window_t* window = nullptr;
    assert(neo_webview_app_create_window(app, &window_options, &window, &error) == NEO_WEBVIEW_OK);
    assert(window != nullptr && error == nullptr);
    assert(neo_webview_window_set_maximum_size(window, {1200, 900}) == NEO_WEBVIEW_OK);
    assert(neo_webview_window_set_minimum_size(window, {320, 200}) == NEO_WEBVIEW_OK);
    neo_webview_size_t size{};
    assert(neo_webview_window_get_minimum_size(window, &size) == NEO_WEBVIEW_OK);
    assert(size.width == 320 && size.height == 200);
    assert(neo_webview_window_get_maximum_size(window, &size) == NEO_WEBVIEW_OK);
    assert(size.width == 1200 && size.height == 900);
    assert(neo_webview_window_set_minimum_size(window, {1300, 200}) == NEO_WEBVIEW_ERROR_INVALID_ARGUMENT);
    assert(neo_webview_window_set_state(window, NEO_WEBVIEW_WINDOW_NORMAL) == NEO_WEBVIEW_OK);
    neo_webview_window_state_t state{};
    assert(neo_webview_window_get_state(window, &state) == NEO_WEBVIEW_OK);
    assert(state == NEO_WEBVIEW_WINDOW_NORMAL);
    double zoom{};
    assert(neo_webview_view_get_zoom_factor(nullptr, &zoom) == NEO_WEBVIEW_ERROR_INVALID_ARGUMENT);
    assert(neo_webview_view_set_zoom_factor(nullptr, 1.0) == NEO_WEBVIEW_ERROR_INVALID_ARGUMENT);
    neo_webview_app_quit(app, 7);
    neo_webview_app_release(app);
    neo_webview_window_release(window);
    return 0;
}
