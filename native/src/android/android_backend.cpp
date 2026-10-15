// Android backend: the Android NDK and the Android System WebView.
//
// The Android framework owns the process and the run loop of its main thread, so an Android application attaches to the
// native looper of the thread that it calls from (neoastra_app_attach) instead of running a loop. This first step
// implements the application level: creation, dispatch to the looper, decision timeouts, and teardown. Windows,
// environments, profiles, and views answer NEOASTRA_ERROR_NOT_SUPPORTED until their implementation follows.
#include "../common/native_internal.hpp"

#include <android/looper.h>
#include <fcntl.h>
#include <sys/timerfd.h>
#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <mutex>
#include <new>
#include <string>
#include <vector>

namespace {
struct timeout_context {
    neoastra_decision_t* decision{};
    int descriptor{-1};
};

struct android_app {
    ALooper* looper{};
    int wake_read{-1};
    int wake_write{-1};
    std::atomic<bool> destroy_requested{false};
    std::mutex timers_mutex;
    std::vector<timeout_context*> timers;
};

constexpr const char* android_domain = "android";

neoastra_result_t unsupported(neoastra_error_t** error, const char* what) noexcept {
    return neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, std::string(what) + " is not implemented by the Android backend yet", 0, android_domain);
}

void signal_wake(android_app* state) noexcept {
    const char byte = 1;
    // A full pipe already holds a pending wake-up, so a short write can be ignored.
    while (write(state->wake_write, &byte, 1) < 0 && errno == EINTR) {}
}

// Runs on the looper thread: either the application asked for its destruction, or dispatches are pending.
int drain_wake(int descriptor, int, void* data) noexcept {
    auto* app = static_cast<neoastra_app_t*>(data);
    auto* state = static_cast<android_app*>(app->platform);
    char buffer[64];
    while (read(descriptor, buffer, sizeof buffer) > 0) {}
    if (state && state->destroy_requested.load(std::memory_order_acquire)) {
        neo_destroy_app_on_ui(app);
        return 0;
    }
    neo_drain_dispatch(app);
    return 1;
}

int expire_decision(int descriptor, int, void* data) noexcept {
    auto* app = static_cast<neoastra_app_t*>(data);
    auto* state = static_cast<android_app*>(app->platform);
    uint64_t expirations{};
    if (read(descriptor, &expirations, sizeof expirations) < 0) return 1;
    timeout_context* context{};
    if (state) {
        std::lock_guard lock(state->timers_mutex);
        const auto found = std::find_if(state->timers.begin(), state->timers.end(), [descriptor](const auto* timer) { return timer->descriptor == descriptor; });
        if (found != state->timers.end()) { context = *found; state->timers.erase(found); }
    }
    if (!context) return 0;
    ALooper_removeFd(state->looper, descriptor);
    close(descriptor);
    context->decision->expire();
    context->decision->release();
    delete context;
    return 0;
}
}  // namespace

bool neo_platform_initialize(neoastra_app_t* app, neoastra_error_t** error) noexcept {
    auto* state = new (std::nothrow) android_app{};
    if (!state) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "the Android application state could not be allocated", 0, android_domain); return false; }
    state->looper = ALooper_prepare(0);
    int pipes[2]{-1, -1};
    if (!state->looper || pipe2(pipes, O_NONBLOCK | O_CLOEXEC) != 0) {
        neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "the looper of the calling thread could not be prepared", errno, android_domain);
        delete state;
        return false;
    }
    state->wake_read = pipes[0];
    state->wake_write = pipes[1];
    if (ALooper_addFd(state->looper, state->wake_read, ALOOPER_POLL_CALLBACK, ALOOPER_EVENT_INPUT, drain_wake, app) != 1) {
        neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "the wake descriptor could not be added to the looper", 0, android_domain);
        close(state->wake_read);
        close(state->wake_write);
        delete state;
        return false;
    }
    app->platform = state;
    return true;
}
void neo_platform_shutdown(neoastra_app_t* app) noexcept {
    auto* state = static_cast<android_app*>(app->platform);
    if (!state) return;
    ALooper_removeFd(state->looper, state->wake_read);
    {
        std::lock_guard lock(state->timers_mutex);
        for (auto* timer : state->timers) {
            ALooper_removeFd(state->looper, timer->descriptor);
            close(timer->descriptor);
            timer->decision->release();
            delete timer;
        }
        state->timers.clear();
    }
    close(state->wake_read);
    close(state->wake_write);
    delete state;
    app->platform = nullptr;
}
bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept {
    auto* state = static_cast<android_app*>(app->platform);
    if (!state) return false;
    state->destroy_requested.store(true, std::memory_order_release);
    signal_wake(state);
    return true;
}
// The Android framework owns the run loop of the main thread; an application attaches to it instead.
int32_t neo_platform_run(neoastra_app_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
// An Android application cannot end its own process, so a quit request only leaves the application state.
void neo_platform_quit(neoastra_app_t*) noexcept {}
void neo_platform_wake(neoastra_app_t* app) noexcept {
    if (auto* state = static_cast<android_app*>(app->platform)) signal_wake(state);
}
bool neo_platform_schedule_decision_timeout(neoastra_app_t* app, neoastra_decision_t* decision) noexcept {
    auto* state = static_cast<android_app*>(app->platform);
    if (!state) return false;
    const auto remaining = std::max<int64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(decision->deadline - std::chrono::steady_clock::now()).count(), 1);
    const int descriptor = timerfd_create(CLOCK_MONOTONIC, TFD_NONBLOCK | TFD_CLOEXEC);
    if (descriptor < 0) return false;
    itimerspec spec{};
    spec.it_value.tv_sec = remaining / 1'000'000'000;
    spec.it_value.tv_nsec = remaining % 1'000'000'000;
    auto* context = new (std::nothrow) timeout_context{decision, descriptor};
    if (!context || timerfd_settime(descriptor, 0, &spec, nullptr) != 0) { close(descriptor); delete context; return false; }
    decision->retain();
    std::lock_guard lock(state->timers_mutex);
    if (ALooper_addFd(state->looper, descriptor, ALOOPER_POLL_CALLBACK, ALOOPER_EVENT_INPUT, expire_decision, app) != 1) {
        decision->release();
        close(descriptor);
        delete context;
        return false;
    }
    state->timers.push_back(context);
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
