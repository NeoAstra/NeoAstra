// iOS backend: UIKit and WKWebView.
//
// UIKit owns the application and its run loop, so an iOS application attaches to the loop that is already running
// (neoastra_app_attach) instead of running one. This first step implements the application level: creation, dispatch to
// the main queue, decision timeouts, and teardown. Windows, environments, profiles, and views answer
// NEOASTRA_ERROR_NOT_SUPPORTED until their implementation follows.
#include "../common/native_internal.hpp"

#import <Foundation/Foundation.h>

#include <algorithm>
#include <chrono>
#include <new>
#include <string>

namespace {
struct uikit_app {};

constexpr const char* uikit_domain = "uikit";

neoastra_result_t unsupported(neoastra_error_t** error, const char* what) noexcept {
    return neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, std::string(what) + " is not implemented by the iOS backend yet", 0, uikit_domain);
}
}  // namespace

bool neo_platform_initialize(neoastra_app_t* app, neoastra_error_t** error) noexcept {
    auto* state = new (std::nothrow) uikit_app{};
    if (!state) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "the iOS application state could not be allocated", 0, uikit_domain); return false; }
    app->platform = state;
    return true;
}
void neo_platform_shutdown(neoastra_app_t* app) noexcept {
    delete static_cast<uikit_app*>(app->platform);
    app->platform = nullptr;
}
bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept {
    dispatch_async(dispatch_get_main_queue(), ^{ neo_destroy_app_on_ui(app); });
    return true;
}
// UIApplicationMain owns the run loop of an iOS process; an application attaches to it instead.
int32_t neo_platform_run(neoastra_app_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
// An iOS application cannot end its own process, so a quit request only leaves the application state.
void neo_platform_quit(neoastra_app_t*) noexcept {}
void neo_platform_wake(neoastra_app_t* app) noexcept {
    app->retain();
    dispatch_async(dispatch_get_main_queue(), ^{ neo_drain_dispatch(app); app->release(); });
}
bool neo_platform_schedule_decision_timeout(neoastra_app_t*, neoastra_decision_t* decision) noexcept {
    const auto remaining = std::chrono::duration_cast<std::chrono::nanoseconds>(decision->deadline - std::chrono::steady_clock::now()).count();
    decision->retain();
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, std::max<int64_t>(remaining, 1)), dispatch_get_main_queue(), ^{ decision->expire(); decision->release(); });
    return true;
}

bool neo_platform_window_create(neoastra_window_t*, const neoastra_window_options_t*, neoastra_error_t** error) noexcept { unsupported(error, "A window"); return false; }
void neo_platform_window_destroy(neoastra_window_t*) noexcept {}
neoastra_result_t neo_platform_window_show(neoastra_window_t*, bool) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_activate(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_force_close(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_title(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_get_frame(neoastra_window_t*, neoastra_rect_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_state(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_attribute(neoastra_window_t*, neoastra_window_attribute_t, bool) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_begin_drag(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_begin_resize(neoastra_window_t*, neoastra_window_resize_edge_t) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_title_bar(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_background_color(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_get_title_bar(neoastra_window_t*, neoastra_title_bar_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_get_handle(neoastra_window_t*, neoastra_native_handle_kind_t, neoastra_native_handle_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }

bool neo_platform_environment_create_async(neoastra_environment_t*, const neoastra_environment_options_t*, neo_platform_created_callback_t, void*, neoastra_error_t** error) noexcept { unsupported(error, "A browser environment"); return false; }
void neo_platform_environment_destroy(neoastra_environment_t*) noexcept {}
bool neo_platform_profile_create(neoastra_profile_t*, neoastra_error_t** error) noexcept { unsupported(error, "A profile"); return false; }
void neo_platform_profile_destroy(neoastra_profile_t*) noexcept {}
neoastra_result_t neo_platform_profile_get_cookies(neoastra_profile_t*, const std::string&, neoastra_buffer_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Cookie access"); }
neoastra_result_t neo_platform_profile_set_cookie(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Cookie access"); }
neoastra_result_t neo_platform_profile_delete_cookie(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Cookie access"); }
neoastra_result_t neo_platform_profile_clear_data(neoastra_profile_t*, neoastra_data_kind_t, int64_t, int64_t, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Clearing browser data"); }

bool neo_platform_view_create_async(neoastra_view_t*, const neoastra_view_options_t*, neo_platform_created_callback_t, void*, neoastra_error_t** error) noexcept { unsupported(error, "A view"); return false; }
void neo_platform_view_destroy(neoastra_view_t*) noexcept {}
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_navigate(neoastra_view_t*, const std::string&, neoastra_error_t** error) noexcept { return unsupported(error, "Navigation"); }
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t*, const std::string&, const std::string&, const std::string&, const uint8_t*, uint64_t, neoastra_error_t** error) noexcept { return unsupported(error, "Navigation"); }
neoastra_result_t neo_platform_view_load_html(neoastra_view_t*, const std::string&, const std::string&, neoastra_error_t** error) noexcept { return unsupported(error, "Loading HTML"); }
neoastra_result_t neo_platform_view_command(neoastra_view_t*, uint32_t) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t*, const std::string&, neoastra_string_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Script evaluation"); }
neoastra_result_t neo_platform_view_add_script(neoastra_view_t*, const std::string&, const neoastra_script_options_t*, neoastra_string_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Document scripts"); }
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t*, const std::string&) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_post_message(neoastra_view_t*, const std::string&, bool, neoastra_error_t** error) noexcept { return unsupported(error, "Messaging"); }
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t*, double*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t*, double) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_set_setting(neoastra_view_t*, neoastra_view_setting_t, bool enabled) noexcept { return enabled ? NEOASTRA_ERROR_NOT_SUPPORTED : NEOASTRA_OK; }
neoastra_result_t neo_platform_view_open_devtools(neoastra_view_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_move_pointer(neoastra_view_t*, double, double) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
bool neo_platform_open_external(const std::string&, int64_t* native_code) noexcept { if (native_code) *native_code = 0; return false; }
neoastra_result_t neo_platform_view_capture(neoastra_view_t*, const neoastra_capture_options_t&, neoastra_buffer_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Capture"); }
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t*, neoastra_native_handle_kind_t, neoastra_native_handle_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
