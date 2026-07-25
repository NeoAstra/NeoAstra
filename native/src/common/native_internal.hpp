#pragma once

#include "neowebview.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

struct neo_ref_counted {
    std::atomic<uint32_t> references{1};
    virtual ~neo_ref_counted() = default;
    void retain() noexcept { references.fetch_add(1, std::memory_order_relaxed); }
    void release() noexcept {
        if (references.fetch_sub(1, std::memory_order_acq_rel) == 1) delete this;
    }
};

struct neo_webview_error final : neo_ref_counted {
    neo_webview_result_t code;
    int64_t native_code;
    std::string domain;
    std::string message;
};

struct neo_webview_buffer final : neo_ref_counted { std::vector<uint8_t> bytes; };
struct neo_webview_operation final : neo_ref_counted { std::atomic<bool> canceled{false}; };
struct neo_webview_decision final : neo_ref_counted {
    std::atomic<uint32_t> state{0}; // 0 pending, 1 deferred, 2 complete
    std::chrono::steady_clock::time_point deadline;
};

struct neo_dispatch_item { neo_webview_dispatch_callback_t callback; void* context; };

struct neo_webview_app final : neo_ref_counted {
    bool embedded{};
    std::atomic<bool> stopping{false};
    std::atomic<bool> stopped{false};
    std::atomic<int32_t> exit_code{0};
    std::thread::id ui_thread;
    neo_webview_app_shutdown_mode_t shutdown_mode{NEO_WEBVIEW_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED};
    uint32_t dispatch_limit{65536};
    std::mutex dispatch_mutex;
    std::deque<neo_dispatch_item> dispatches;
    neo_webview_event_callback_t event_callback{};
    void* event_context{};
    neo_webview_log_callback_t log_callback{};
    void* log_context{};
    std::atomic<uint64_t> next_id{1};
    std::atomic<uint32_t> window_count{0};
    void* platform{};
    ~neo_webview_app() override;
};

struct neo_webview_environment final : neo_ref_counted {
    neo_webview_app_t* app{};
    void* platform{};
    explicit neo_webview_environment(neo_webview_app_t* value) : app(value) { app->retain(); }
    ~neo_webview_environment() override;
};

struct neo_webview_profile final : neo_ref_counted {
    neo_webview_environment_t* environment{};
    bool ephemeral{};
    std::string name;
    void* platform{};
    explicit neo_webview_profile(neo_webview_environment_t* value) : environment(value) { environment->retain(); }
    ~neo_webview_profile() override;
};

struct neo_webview_window final : neo_ref_counted {
    neo_webview_app_t* app{};
    neo_webview_window_t* owner{};
    uint64_t id{};
    std::string title;
    neo_webview_rect_t bounds{};
    bool closed{};
    void* platform{};
    explicit neo_webview_window(neo_webview_app_t* value) : app(value) { app->retain(); }
    ~neo_webview_window() override;
};

struct neo_webview_view final : neo_ref_counted {
    neo_webview_environment_t* environment{};
    neo_webview_profile_t* profile{};
    neo_webview_window_t* window{};
    neo_webview_native_parent_t parent{};
    neo_webview_rect_t bounds{};
    bool fill_parent{true};
    std::string source;
    std::string title;
    neo_webview_event_callback_t event_callback{};
    void* event_context{};
    void* platform{};
    explicit neo_webview_view(neo_webview_environment_t* value) : environment(value) { environment->retain(); }
    ~neo_webview_view() override;
};

inline neo_webview_string_view_t neo_string_view(const std::string& text) noexcept {
    return {reinterpret_cast<const uint8_t*>(text.data()), static_cast<uint64_t>(text.size())};
}
inline std::string neo_string(neo_webview_string_view_t text) {
    if (text.length == 0) return {};
    if (!text.data) throw std::invalid_argument("A non-empty string has a null data pointer");
    return {reinterpret_cast<const char*>(text.data), static_cast<size_t>(text.length)};
}
inline uint64_t neo_timestamp_ns() noexcept {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}

neo_webview_result_t neo_fail(neo_webview_error_t** error, neo_webview_result_t code, std::string message, int64_t native_code = 0, std::string domain = "neowebview") noexcept;
void neo_emit(neo_webview_app_t* app, neo_webview_event_callback_t callback, void* context, neo_webview_event_type_t type, uint64_t object_id = 0, const std::string* text = nullptr, const std::string* uri = nullptr, uint64_t value = 0, int64_t native_code = 0, neo_webview_decision_t* decision = nullptr) noexcept;
void neo_drain_dispatch(neo_webview_app_t* app) noexcept;

bool neo_platform_initialize(neo_webview_app_t* app, neo_webview_error_t** error) noexcept;
void neo_platform_shutdown(neo_webview_app_t* app) noexcept;
int32_t neo_platform_run(neo_webview_app_t* app) noexcept;
void neo_platform_quit(neo_webview_app_t* app) noexcept;
void neo_platform_wake(neo_webview_app_t* app) noexcept;
bool neo_platform_window_create(neo_webview_window_t* window, const neo_webview_window_options_t* options, neo_webview_error_t** error) noexcept;
void neo_platform_window_destroy(neo_webview_window_t* window) noexcept;
neo_webview_result_t neo_platform_window_show(neo_webview_window_t* window, bool visible) noexcept;
neo_webview_result_t neo_platform_window_activate(neo_webview_window_t* window) noexcept;
neo_webview_result_t neo_platform_window_close(neo_webview_window_t* window) noexcept;
neo_webview_result_t neo_platform_window_set_title(neo_webview_window_t* window) noexcept;
neo_webview_result_t neo_platform_window_set_bounds(neo_webview_window_t* window) noexcept;
neo_webview_result_t neo_platform_window_get_handle(neo_webview_window_t* window, neo_webview_native_handle_kind_t kind, neo_webview_native_handle_t* handle) noexcept;

bool neo_platform_environment_create(neo_webview_environment_t* environment, const neo_webview_environment_options_t* options, neo_webview_error_t** error) noexcept;
void neo_platform_environment_destroy(neo_webview_environment_t* environment) noexcept;
bool neo_platform_view_create(neo_webview_view_t* view, const neo_webview_view_options_t* options, neo_webview_error_t** error) noexcept;
void neo_platform_view_destroy(neo_webview_view_t* view) noexcept;
neo_webview_result_t neo_platform_view_set_bounds(neo_webview_view_t* view) noexcept;
neo_webview_result_t neo_platform_view_navigate(neo_webview_view_t* view, const std::string& uri, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_load_html(neo_webview_view_t* view, const std::string& html, const std::string& base_uri, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_command(neo_webview_view_t* view, uint32_t command) noexcept;
neo_webview_result_t neo_platform_view_evaluate(neo_webview_view_t* view, const std::string& script, neo_webview_string_callback_t callback, void* context, neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_post_message(neo_webview_view_t* view, const std::string& message, bool json, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_get_handle(neo_webview_view_t* view, neo_webview_native_handle_kind_t kind, neo_webview_native_handle_t* handle) noexcept;
