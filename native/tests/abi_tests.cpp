#include "neowebview.h"

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <type_traits>

namespace {

struct log_capture {
    uint32_t calls{};
    bool valid{true};
};

void NEO_WEBVIEW_CALL capture_log(void* context, neo_webview_log_level_t level,
                                  neo_webview_string_view_t category, neo_webview_string_view_t message,
                                  uint64_t thread_id, uint64_t timestamp_ns, int64_t native_code, uint64_t object_id) {
    auto* capture = static_cast<log_capture*>(context);
    ++capture->calls;
    capture->valid = capture->valid && level == NEO_WEBVIEW_LOG_INFORMATION &&
        category.data != nullptr && category.length != 0 && message.data != nullptr && message.length != 0 &&
        thread_id != 0 && timestamp_ns != 0 && native_code == 0 && object_id == 0;
}

} // namespace

static_assert(std::is_same_v<std::underlying_type_t<neo_webview_result_t>, int32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_support_level_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_app_shutdown_mode_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_native_parent_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_native_handle_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_window_state_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_option_state_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_script_injection_time_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_decision_action_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_decision_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_permission_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_process_failure_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_event_type_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_capability_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_log_level_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_resource_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neo_webview_resource_body_kind_t>, uint32_t>);
static_assert(NEO_WEBVIEW_ERROR_SECURITY == -14);
static_assert(NEO_WEBVIEW_SUPPORT_LIMITED == 3);
static_assert(NEO_WEBVIEW_APP_SHUTDOWN_ON_MAIN_WINDOW_CLOSED == 2);
static_assert(NEO_WEBVIEW_NATIVE_PARENT_GTK_WIDGET == 3);
static_assert(NEO_WEBVIEW_NATIVE_HANDLE_WEBKITGTK_WEBVIEW == 9);
static_assert(NEO_WEBVIEW_WINDOW_FULLSCREEN == 3);
static_assert(NEO_WEBVIEW_OPTION_DISABLED == 2);
static_assert(NEO_WEBVIEW_SCRIPT_DOCUMENT_END == 1);
static_assert(NEO_WEBVIEW_DECISION_DOWNLOAD == 5);
static_assert(NEO_WEBVIEW_DECISION_HANDLED_EXTERNAL == 6);
static_assert(NEO_WEBVIEW_DECISION_CLIENT_CERTIFICATE == 10);
static_assert(NEO_WEBVIEW_PERMISSION_PERSISTENT_STORAGE == 12);
static_assert(NEO_WEBVIEW_PROCESS_FAILURE_PROCESS_UNRESPONSIVE == 3);
static_assert(NEO_WEBVIEW_EVENT_CLIENT_CERTIFICATE_REQUESTED == 33);
static_assert(NEO_WEBVIEW_CAPABILITY_FULLSCREEN_DECISIONS == 32);
static_assert(NEO_WEBVIEW_LOG_CRITICAL == 5);
static_assert((NEO_WEBVIEW_PROCESS_FAILURE_KIND_MASK & NEO_WEBVIEW_PROCESS_FAILURE_CRASHED) == 0);
static_assert(NEO_WEBVIEW_RESOURCE_MANIFEST == 12);
static_assert(NEO_WEBVIEW_RESOURCE_BODY_FILE == 2);
static_assert(sizeof(void*) == 8, "ABI 1.7 targets the current 64-bit primary platforms");
static_assert(sizeof(neo_webview_struct_header_t) == 8);
static_assert(sizeof(neo_webview_string_view_t) == 16);
static_assert(sizeof(neo_webview_point_t) == 8);
static_assert(sizeof(neo_webview_size_t) == 8);
static_assert(sizeof(neo_webview_rect_t) == 16);
static_assert(sizeof(neo_webview_color_t) == 4);
static_assert(sizeof(neo_webview_native_parent_t) == 24 && offsetof(neo_webview_native_parent_t, handle) == 16);
static_assert(sizeof(neo_webview_native_handle_t) == 24 && offsetof(neo_webview_native_handle_t, value) == 16);
static_assert(sizeof(neo_webview_event_header_t) == 32 && offsetof(neo_webview_event_header_t, sequence) == 16);
static_assert(sizeof(neo_webview_event_t) == 160 && offsetof(neo_webview_event_t, download) == 152);
static_assert(sizeof(neo_webview_capability_info_t) == 40 && offsetof(neo_webview_capability_info_t, details) == 24);
static_assert(sizeof(neo_webview_app_options_t) == 56 && offsetof(neo_webview_app_options_t, log_callback) == 40);
static_assert(sizeof(neo_webview_environment_options_t) == 96 && offsetof(neo_webview_environment_options_t, custom_scheme_stride) == 88);
static_assert(sizeof(neo_webview_profile_options_t) == 32 && offsetof(neo_webview_profile_options_t, ephemeral) == 24);
static_assert(sizeof(neo_webview_window_options_t) == 80 && offsetof(neo_webview_window_options_t, background_color) == 72);
static_assert(sizeof(neo_webview_view_options_t) == 104 && offsetof(neo_webview_view_options_t, bridge_origins) == 96);
static_assert(sizeof(neo_webview_script_options_t) == 40 && offsetof(neo_webview_script_options_t, world_name) == 24);
static_assert(sizeof(neo_webview_decision_response_t) == 80 && offsetof(neo_webview_decision_response_t, target_view) == 64);
static_assert(sizeof(neo_webview_download_info_t) == 88 && offsetof(neo_webview_download_info_t, failure_reason) == 72);
static_assert(sizeof(neo_webview_runtime_info_t) == 104 && offsetof(neo_webview_runtime_info_t, build_features) == 88);
static_assert(sizeof(neo_webview_cookie_t) == 88 && offsetof(neo_webview_cookie_t, expires_unix_ms) == 72);
static_assert(sizeof(neo_webview_resource_request_t) == 96 && offsetof(neo_webview_resource_request_t, body_length) == 88);
static_assert(sizeof(neo_webview_resource_response_t) == 120 && offsetof(neo_webview_resource_response_t, release) == 112);
static_assert(sizeof(neo_webview_custom_scheme_t) == 64 && offsetof(neo_webview_custom_scheme_t, resource_provider) == 40);

#define NEO_ASSERT_STANDARD_LAYOUT(type) static_assert(std::is_standard_layout_v<type>)
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_string_view_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_struct_header_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_native_parent_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_native_handle_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_event_header_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_event_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_capability_info_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_app_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_environment_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_profile_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_window_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_view_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_script_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_decision_response_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_download_info_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_runtime_info_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_cookie_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_resource_request_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_resource_response_t);
NEO_ASSERT_STANDARD_LAYOUT(neo_webview_custom_scheme_t);
#undef NEO_ASSERT_STANDARD_LAYOUT

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
    log_capture logs{};
    options.log_callback = capture_log;
    options.log_context = &logs;
    neo_webview_app_t* app = nullptr;
    assert(neo_webview_app_create(&options, &app, &error) == NEO_WEBVIEW_OK);
    assert(app != nullptr && error == nullptr);
    assert(logs.calls == 1 && logs.valid);

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
