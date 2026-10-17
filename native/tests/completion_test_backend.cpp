// A deterministic, GUI-free backend for accepted-operation completion tests.
#include "../src/common/native_internal.hpp"

namespace completion_test_backend {
neo_platform_created_callback_t environment_callback{};
void* environment_context{};
neo_platform_created_callback_t view_callback{};
void* view_context{};
uint32_t environments_destroyed{};
uint32_t views_destroyed{};
}

bool neo_platform_initialize(neoastra_app_t*, neoastra_error_t**) noexcept { return true; }
void neo_platform_shutdown(neoastra_app_t*) noexcept { }
bool neo_platform_schedule_app_destruction(neoastra_app_t*) noexcept { return false; }
int32_t neo_platform_run(neoastra_app_t* app) noexcept { neo_drain_dispatch(app); return 0; }
void neo_platform_quit(neoastra_app_t*) noexcept { }
void neo_platform_wake(neoastra_app_t*) noexcept { }
bool neo_platform_schedule_decision_timeout(neoastra_app_t*, neoastra_decision_t*) noexcept { return false; }
bool neo_platform_window_create(neoastra_window_t*, const neoastra_window_options_t*, neoastra_error_t**) noexcept { return true; }
void neo_platform_window_destroy(neoastra_window_t*) noexcept { }
neoastra_result_t neo_platform_window_show(neoastra_window_t*, bool) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_activate(neoastra_window_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_begin_drag(neoastra_window_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_begin_resize(neoastra_window_t*, neoastra_window_resize_edge_t) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_force_close(neoastra_window_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_set_title(neoastra_window_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_get_frame(neoastra_window_t*, neoastra_rect_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_set_state(neoastra_window_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_set_attribute(neoastra_window_t*, neoastra_window_attribute_t, bool) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_set_title_bar(neoastra_window_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_set_background_color(neoastra_window_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_get_title_bar(neoastra_window_t*, neoastra_title_bar_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_window_get_handle(neoastra_window_t*, neoastra_native_handle_kind_t, neoastra_native_handle_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }

bool neo_platform_environment_create_async(neoastra_environment_t*, const neoastra_environment_options_t*, neo_platform_created_callback_t callback, void* context, neoastra_error_t**) noexcept {
    completion_test_backend::environment_callback = callback;
    completion_test_backend::environment_context = context;
    return true;
}
void neo_platform_environment_destroy(neoastra_environment_t*) noexcept { ++completion_test_backend::environments_destroyed; }
bool neo_platform_profile_create(neoastra_profile_t*, neoastra_error_t**) noexcept { return true; }
void neo_platform_profile_destroy(neoastra_profile_t*) noexcept { }
neoastra_result_t neo_platform_profile_get_cookies(neoastra_profile_t*, const std::string&, neoastra_buffer_callback_t, void*, neoastra_operation_t*, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_profile_set_cookie(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_profile_delete_cookie(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_profile_clear_data(neoastra_profile_t*, neoastra_data_kind_t, int64_t, int64_t, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
bool neo_platform_view_create_async(neoastra_view_t*, const neoastra_view_options_t*, neo_platform_created_callback_t callback, void* context, neoastra_error_t**) noexcept {
    completion_test_backend::view_callback = callback;
    completion_test_backend::view_context = context;
    return true;
}
void neo_platform_view_destroy(neoastra_view_t*) noexcept { ++completion_test_backend::views_destroyed; }
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t*) noexcept { return NEOASTRA_OK; }
neoastra_result_t neo_platform_view_navigate(neoastra_view_t*, const std::string&, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t*, const std::string&, const std::string&, const std::string&, const uint8_t*, uint64_t, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_load_html(neoastra_view_t*, const std::string&, const std::string&, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_command(neoastra_view_t*, uint32_t) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t*, const std::string&, neoastra_string_callback_t, void*, neoastra_operation_t*, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_add_script(neoastra_view_t*, const std::string&, const neoastra_script_options_t*, neoastra_string_callback_t, void*, neoastra_operation_t*, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t*, const std::string&) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_post_message(neoastra_view_t*, const std::string&, bool, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t*, double*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t*, double) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_set_setting(neoastra_view_t*, neoastra_view_setting_t, bool) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_open_devtools(neoastra_view_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_move_pointer(neoastra_view_t*, double, double) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
bool neo_platform_open_external(const std::string&, int64_t*) noexcept { return false; }
neoastra_result_t neo_platform_view_capture(neoastra_view_t*, const neoastra_capture_options_t&, neoastra_buffer_callback_t, void*, neoastra_operation_t*, neoastra_error_t**) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t*, neoastra_native_handle_kind_t, neoastra_native_handle_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
