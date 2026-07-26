#include "neoastra.h"

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

void NEOASTRA_CALL capture_log(void* context, neoastra_log_level_t level,
                                  neoastra_string_view_t category, neoastra_string_view_t message,
                                  uint64_t thread_id, uint64_t timestamp_ns, int64_t native_code, uint64_t object_id) {
    auto* capture = static_cast<log_capture*>(context);
    ++capture->calls;
    capture->valid = capture->valid && level == NEOASTRA_LOG_INFORMATION &&
        category.data != nullptr && category.length != 0 && message.data != nullptr && message.length != 0 &&
        thread_id != 0 && timestamp_ns != 0 && native_code == 0 && object_id == 0;
}

} // namespace

static_assert(std::is_same_v<std::underlying_type_t<neoastra_result_t>, int32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_support_level_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_app_shutdown_mode_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_native_parent_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_native_handle_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_window_state_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_option_state_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_script_injection_time_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_decision_action_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_decision_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_permission_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_process_failure_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_event_type_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_capability_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_log_level_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_resource_kind_t>, uint32_t>);
static_assert(std::is_same_v<std::underlying_type_t<neoastra_resource_body_kind_t>, uint32_t>);
static_assert(NEOASTRA_ERROR_SECURITY == -14);
static_assert(NEOASTRA_SUPPORT_LIMITED == 3);
static_assert(NEOASTRA_APP_SHUTDOWN_ON_MAIN_WINDOW_CLOSED == 2);
static_assert(NEOASTRA_NATIVE_PARENT_GTK_WIDGET == 3);
static_assert(NEOASTRA_NATIVE_HANDLE_WEBKITGTK_WEBVIEW == 9);
static_assert(NEOASTRA_WINDOW_FULLSCREEN == 3);
static_assert(NEOASTRA_OPTION_DISABLED == 2);
static_assert(NEOASTRA_SCRIPT_DOCUMENT_END == 1);
static_assert(NEOASTRA_DECISION_DOWNLOAD == 5);
static_assert(NEOASTRA_DECISION_HANDLED_EXTERNAL == 6);
static_assert(NEOASTRA_DECISION_CLIENT_CERTIFICATE == 10);
static_assert(NEOASTRA_PERMISSION_PERSISTENT_STORAGE == 12);
static_assert(NEOASTRA_PROCESS_FAILURE_PROCESS_UNRESPONSIVE == 3);
static_assert(NEOASTRA_EVENT_CLIENT_CERTIFICATE_REQUESTED == 33);
static_assert(NEOASTRA_CAPABILITY_FULLSCREEN_DECISIONS == 32);
static_assert(NEOASTRA_LOG_CRITICAL == 5);
static_assert((NEOASTRA_PROCESS_FAILURE_KIND_MASK & NEOASTRA_PROCESS_FAILURE_CRASHED) == 0);
static_assert(NEOASTRA_RESOURCE_MANIFEST == 12);
static_assert(NEOASTRA_RESOURCE_BODY_FILE == 2);
static_assert(NEOASTRA_BRIDGE_DISABLED == 0 && NEOASTRA_BRIDGE_TRUSTED_ORIGINS == 1 && NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW == 2);
static_assert(sizeof(void*) == 8, "ABI 1.8 targets the current 64-bit primary platforms");
static_assert(sizeof(neoastra_struct_header_t) == 8);
static_assert(sizeof(neoastra_string_view_t) == 16);
static_assert(sizeof(neoastra_point_t) == 8);
static_assert(sizeof(neoastra_size_t) == 8);
static_assert(sizeof(neoastra_rect_t) == 16);
static_assert(sizeof(neoastra_color_t) == 4);
static_assert(sizeof(neoastra_native_parent_t) == 24 && offsetof(neoastra_native_parent_t, handle) == 16);
static_assert(sizeof(neoastra_native_handle_t) == 24 && offsetof(neoastra_native_handle_t, value) == 16);
static_assert(sizeof(neoastra_event_header_t) == 32 && offsetof(neoastra_event_header_t, sequence) == 16);
static_assert(sizeof(neoastra_event_t) == 160 && offsetof(neoastra_event_t, download) == 152);
static_assert(sizeof(neoastra_capability_info_t) == 40 && offsetof(neoastra_capability_info_t, details) == 24);
static_assert(sizeof(neoastra_app_options_t) == 56 && offsetof(neoastra_app_options_t, log_callback) == 40);
static_assert(sizeof(neoastra_environment_options_t) == 96 && offsetof(neoastra_environment_options_t, custom_scheme_stride) == 88);
static_assert(sizeof(neoastra_profile_options_t) == 32 && offsetof(neoastra_profile_options_t, ephemeral) == 24);
static_assert(sizeof(neoastra_window_options_t) == 80 && offsetof(neoastra_window_options_t, background_color) == 72);
static_assert(sizeof(neoastra_view_options_t) == 104 && offsetof(neoastra_view_options_t, bridge_policy) == 92 && offsetof(neoastra_view_options_t, bridge_origins) == 96);
static_assert(sizeof(neoastra_script_options_t) == 40 && offsetof(neoastra_script_options_t, world_name) == 24);
static_assert(sizeof(neoastra_decision_response_t) == 80 && offsetof(neoastra_decision_response_t, target_view) == 64);
static_assert(sizeof(neoastra_download_info_t) == 88 && offsetof(neoastra_download_info_t, failure_reason) == 72);
static_assert(sizeof(neoastra_runtime_info_t) == 104 && offsetof(neoastra_runtime_info_t, build_features) == 88);
static_assert(sizeof(neoastra_cookie_t) == 88 && offsetof(neoastra_cookie_t, expires_unix_ms) == 72);
static_assert(sizeof(neoastra_resource_request_t) == 96 && offsetof(neoastra_resource_request_t, body_length) == 88);
static_assert(sizeof(neoastra_resource_response_t) == 120 && offsetof(neoastra_resource_response_t, release) == 112);
static_assert(sizeof(neoastra_custom_scheme_t) == 64 && offsetof(neoastra_custom_scheme_t, resource_provider) == 40);

#define NEO_ASSERT_STANDARD_LAYOUT(type) static_assert(std::is_standard_layout_v<type>)
NEO_ASSERT_STANDARD_LAYOUT(neoastra_string_view_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_struct_header_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_native_parent_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_native_handle_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_event_header_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_event_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_capability_info_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_app_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_environment_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_profile_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_window_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_view_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_script_options_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_decision_response_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_download_info_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_runtime_info_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_cookie_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_resource_request_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_resource_response_t);
NEO_ASSERT_STANDARD_LAYOUT(neoastra_custom_scheme_t);
#undef NEO_ASSERT_STANDARD_LAYOUT

int main() {
    assert(neoastra_get_abi_version_major() == NEOASTRA_ABI_VERSION_MAJOR);
    assert(neoastra_get_abi_version_minor() == NEOASTRA_ABI_VERSION_MINOR);
    auto version = neoastra_get_version();
    assert(version.data != nullptr && version.length != 0);

    neoastra_runtime_info_t info{};
    info.size = sizeof(info);
    info.version = 1;
    neoastra_error_t* error = nullptr;
    assert(neoastra_get_runtime_info(&info, &error) == NEOASTRA_OK);
    assert(error == nullptr && info.backend_name.length != 0);

    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED;
    log_capture logs{};
    options.log_callback = capture_log;
    options.log_context = &logs;
    neoastra_app_t* app = nullptr;
    assert(neoastra_app_create(&options, &app, &error) == NEOASTRA_OK);
    assert(app != nullptr && error == nullptr);
    assert(logs.calls == 1 && logs.valid);

    neoastra_window_options_t window_options{};
    window_options.size = sizeof(window_options);
    window_options.version = 1;
    window_options.bounds = {100, 100, 800, 600};
    window_options.flags = 3;
    neoastra_window_t* window = nullptr;
    assert(neoastra_app_create_window(app, &window_options, &window, &error) == NEOASTRA_OK);
    assert(window != nullptr && error == nullptr);
    assert(neoastra_window_set_maximum_size(window, {1200, 900}) == NEOASTRA_OK);
    assert(neoastra_window_set_minimum_size(window, {320, 200}) == NEOASTRA_OK);
    neoastra_size_t size{};
    assert(neoastra_window_get_minimum_size(window, &size) == NEOASTRA_OK);
    assert(size.width == 320 && size.height == 200);
    assert(neoastra_window_get_maximum_size(window, &size) == NEOASTRA_OK);
    assert(size.width == 1200 && size.height == 900);
    assert(neoastra_window_set_minimum_size(window, {1300, 200}) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(neoastra_window_set_state(window, NEOASTRA_WINDOW_NORMAL) == NEOASTRA_OK);
    neoastra_window_state_t state{};
    assert(neoastra_window_get_state(window, &state) == NEOASTRA_OK);
    assert(state == NEOASTRA_WINDOW_NORMAL);
    double zoom{};
    assert(neoastra_view_get_zoom_factor(nullptr, &zoom) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(neoastra_view_set_zoom_factor(nullptr, 1.0) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    neoastra_app_quit(app, 7);
    neoastra_app_release(app);
    neoastra_window_release(window);
    return 0;
}
