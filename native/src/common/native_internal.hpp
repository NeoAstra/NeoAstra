#pragma once

#include "neowebview.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <limits>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

inline thread_local const void* neo_active_callback_slot = nullptr;

template<typename TCallback>
class neo_callback_slot final {
public:
    neo_callback_slot() = default;
    neo_callback_slot(const neo_callback_slot&) = delete;
    neo_callback_slot& operator=(const neo_callback_slot&) = delete;

    void set(TCallback callback, void* context) noexcept {
        std::unique_lock lock(_mutex);
        _callback = nullptr;
        _context = nullptr;
        if (neo_active_callback_slot != this) {
            _quiesced.wait(lock, [this] { return _active == 0; });
        }
        _callback = callback;
        _context = callback ? context : nullptr;
    }

    void clear() noexcept { set(nullptr, nullptr); }

    template<typename TInvoker>
    bool invoke(TInvoker&& invoker) noexcept {
        TCallback callback{};
        void* context{};
        {
            std::lock_guard lock(_mutex);
            callback = _callback;
            if (!callback) return false;
            context = _context;
            ++_active;
        }

        const auto* previous = neo_active_callback_slot;
        neo_active_callback_slot = this;
        try {
            invoker(callback, context);
        } catch (...) {
            // No exception may cross an ABI callback boundary.
        }
        neo_active_callback_slot = previous;

        {
            std::lock_guard lock(_mutex);
            if (--_active == 0) _quiesced.notify_all();
        }
        return true;
    }

private:
    std::mutex _mutex;
    std::condition_variable _quiesced;
    TCallback _callback{};
    void* _context{};
    uint32_t _active{};
};

struct neo_ref_counted {
    std::atomic<uint32_t> references{1};
    virtual ~neo_ref_counted() = default;

    bool retain() noexcept {
        auto count = references.load(std::memory_order_relaxed);
        for (;;) {
            if (count == 0 || count == std::numeric_limits<uint32_t>::max()) return false;
            if (references.compare_exchange_weak(count, count + 1, std::memory_order_relaxed, std::memory_order_relaxed)) return true;
        }
    }

    bool release() noexcept {
        auto count = references.load(std::memory_order_acquire);
        for (;;) {
            if (count == 0) return false;
            if (!references.compare_exchange_weak(count, count - 1, std::memory_order_acq_rel, std::memory_order_acquire)) continue;
            if (count == 1) delete this;
            return true;
        }
    }
};

struct neo_webview_error final : neo_ref_counted {
    const neo_webview_result_t code;
    const int64_t native_code;
    const std::string domain;
    const std::string message;

    neo_webview_error(neo_webview_result_t value_code, int64_t value_native_code, std::string value_domain, std::string value_message)
        : code(value_code), native_code(value_native_code), domain(std::move(value_domain)), message(std::move(value_message)) { }
};

struct neo_webview_buffer final : neo_ref_counted {
    const std::vector<uint8_t> bytes;
    explicit neo_webview_buffer(std::vector<uint8_t> value = {}) : bytes(std::move(value)) { }
};

struct neo_webview_stream final : neo_ref_counted { };

enum class neo_operation_state : uint32_t { pending, cancel_requested, completed };

struct neo_webview_operation final : neo_ref_counted {
    std::atomic<neo_operation_state> state{neo_operation_state::pending};

    void cancel() noexcept {
        auto expected = neo_operation_state::pending;
        state.compare_exchange_strong(expected, neo_operation_state::cancel_requested, std::memory_order_acq_rel);
    }

    bool try_complete(neo_webview_result_t requested, neo_webview_result_t& actual) noexcept {
        auto current = state.load(std::memory_order_acquire);
        for (;;) {
            if (current == neo_operation_state::completed) return false;
            actual = current == neo_operation_state::cancel_requested ? NEO_WEBVIEW_ERROR_CANCELED : requested;
            if (state.compare_exchange_weak(current, neo_operation_state::completed, std::memory_order_acq_rel, std::memory_order_acquire)) return true;
        }
    }
};

enum class neo_decision_state : uint32_t { pending, deferred, completed, timed_out, abandoned };

struct neo_webview_decision final : neo_ref_counted {
    std::atomic<neo_decision_state> state{neo_decision_state::pending};
    neo_webview_decision_kind_t kind{NEO_WEBVIEW_DECISION_UNKNOWN};
    neo_webview_decision_action_t default_action{NEO_WEBVIEW_DECISION_DENY};
    std::atomic<neo_webview_decision_action_t> resolved_action{NEO_WEBVIEW_DECISION_DEFAULT};
    std::chrono::steady_clock::time_point deadline{std::chrono::steady_clock::now() + std::chrono::seconds(30)};
    void (*completion)(void* context, neo_webview_decision_action_t action) noexcept{};
    void* completion_context{};

    void resolve(neo_webview_decision_action_t action) noexcept {
        resolved_action.store(action, std::memory_order_release);
        if (completion) completion(completion_context, action);
        completion = nullptr;
        completion_context = nullptr;
    }

    ~neo_webview_decision() override {
        auto current = state.load(std::memory_order_acquire);
        while (current == neo_decision_state::pending || current == neo_decision_state::deferred) {
            if (state.compare_exchange_weak(current, neo_decision_state::abandoned, std::memory_order_acq_rel, std::memory_order_acquire)) {
                resolve(default_action);
                break;
            }
        }
    }

    bool expire() noexcept {
        if (std::chrono::steady_clock::now() < deadline) return false;
        auto current = state.load(std::memory_order_acquire);
        while (current == neo_decision_state::pending || current == neo_decision_state::deferred) {
            if (state.compare_exchange_weak(current, neo_decision_state::timed_out, std::memory_order_acq_rel, std::memory_order_acquire)) {
                resolve(default_action);
                return true;
            }
        }
        return current == neo_decision_state::timed_out;
    }
};

struct neo_dispatch_item {
    neo_webview_dispatch_callback_t callback{};
    void* context{};
};

enum class neo_app_state : uint32_t { created, running, stopping, stopped };

struct neo_webview_app final : neo_ref_counted {
    bool embedded{};
    std::atomic<bool> quit_requested{false};
    std::atomic<bool> stopping{false}; // Used by the platform loop after common shutdown has drained.
    std::atomic<bool> stopped{false};
    std::atomic<neo_app_state> state{neo_app_state::created};
    std::atomic<int32_t> exit_code{0};
    std::thread::id ui_thread;
    neo_webview_app_shutdown_mode_t shutdown_mode{NEO_WEBVIEW_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED};
    uint32_t dispatch_limit{65536};
    std::mutex dispatch_mutex;
    std::deque<neo_dispatch_item> dispatches;
    neo_callback_slot<neo_webview_event_callback_t> events;
    neo_callback_slot<neo_webview_log_callback_t> logs;
    std::atomic<uint64_t> next_id{1};
    std::atomic<uint64_t> next_sequence{1};
    std::mutex windows_mutex;
    std::unordered_map<uint64_t, neo_webview_window_t*> windows;
    uint64_t main_window_id{};
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
    std::mutex state_mutex;
    std::string title;
    neo_webview_rect_t bounds{};
    neo_webview_size_t minimum_size{};
    neo_webview_size_t maximum_size{};
    neo_webview_window_state_t state{NEO_WEBVIEW_WINDOW_NORMAL};
    std::atomic<bool> closed{false};
    std::vector<neo_webview_view_t*> views; // UI-thread-only weak references.
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
    neo_callback_slot<neo_webview_event_callback_t> events;
    void* platform{};
    explicit neo_webview_view(neo_webview_environment_t* value) : environment(value) { environment->retain(); }
    ~neo_webview_view() override;
};

inline neo_webview_string_view_t neo_string_view(const std::string& text) noexcept {
    return {reinterpret_cast<const uint8_t*>(text.data()), static_cast<uint64_t>(text.size())};
}

inline uint64_t neo_timestamp_ns() noexcept {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}

bool neo_valid_utf8(neo_webview_string_view_t text) noexcept;
std::string neo_string(neo_webview_string_view_t text);
neo_webview_result_t neo_fail(neo_webview_error_t** error, neo_webview_result_t code, std::string message, int64_t native_code = 0, std::string domain = "neowebview") noexcept;
void neo_emit_app(neo_webview_app_t* app, neo_webview_event_type_t type, uint64_t object_id = 0, const std::string* text = nullptr, const std::string* uri = nullptr, uint64_t value = 0, int64_t native_code = 0, neo_webview_decision_t* decision = nullptr) noexcept;
void neo_emit_view(neo_webview_view_t* view, neo_webview_event_type_t type, uint64_t object_id = 0, const std::string* text = nullptr, const std::string* uri = nullptr, uint64_t value = 0, int64_t native_code = 0, neo_webview_decision_t* decision = nullptr) noexcept;
void neo_drain_dispatch(neo_webview_app_t* app) noexcept;
void neo_window_closed(neo_webview_window_t* window) noexcept;

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
neo_webview_result_t neo_platform_window_set_size_constraints(neo_webview_window_t* window) noexcept;
neo_webview_result_t neo_platform_window_set_state(neo_webview_window_t* window) noexcept;
neo_webview_result_t neo_platform_window_get_handle(neo_webview_window_t* window, neo_webview_native_handle_kind_t kind, neo_webview_native_handle_t* handle) noexcept;

using neo_platform_created_callback_t = void (*)(void* context, neo_webview_error_t* error) noexcept;
bool neo_platform_environment_create_async(neo_webview_environment_t* environment, const neo_webview_environment_options_t* options, neo_platform_created_callback_t callback, void* context, neo_webview_error_t** error) noexcept;
void neo_platform_environment_destroy(neo_webview_environment_t* environment) noexcept;
bool neo_platform_profile_create(neo_webview_profile_t* profile, neo_webview_error_t** error) noexcept;
void neo_platform_profile_destroy(neo_webview_profile_t* profile) noexcept;
neo_webview_result_t neo_platform_profile_get_cookies(neo_webview_profile_t* profile, const std::string& uri, neo_webview_buffer_callback_t callback, void* context, neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_profile_set_cookie(neo_webview_profile_t* profile, const neo_webview_cookie_t* cookie, neo_webview_completion_callback_t callback, void* context, neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_profile_delete_cookie(neo_webview_profile_t* profile, const neo_webview_cookie_t* cookie, neo_webview_completion_callback_t callback, void* context, neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_profile_clear_data(neo_webview_profile_t* profile, neo_webview_data_kind_t kinds, int64_t start_unix_ms, int64_t end_unix_ms, neo_webview_completion_callback_t callback, void* context, neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept;
bool neo_platform_view_create_async(neo_webview_view_t* view, const neo_webview_view_options_t* options, neo_platform_created_callback_t callback, void* context, neo_webview_error_t** error) noexcept;
void neo_platform_view_destroy(neo_webview_view_t* view) noexcept;
neo_webview_result_t neo_platform_view_set_bounds(neo_webview_view_t* view) noexcept;
neo_webview_result_t neo_platform_view_navigate(neo_webview_view_t* view, const std::string& uri, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_navigate_request(neo_webview_view_t* view, const std::string& uri, const std::string& method, const std::string& headers, const uint8_t* body, uint64_t body_length, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_load_html(neo_webview_view_t* view, const std::string& html, const std::string& base_uri, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_command(neo_webview_view_t* view, uint32_t command) noexcept;
neo_webview_result_t neo_platform_view_evaluate(neo_webview_view_t* view, const std::string& script, neo_webview_string_callback_t callback, void* context, neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_add_script(neo_webview_view_t* view, const std::string& script, const neo_webview_script_options_t* options, neo_webview_string_callback_t callback, void* context, neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_remove_script(neo_webview_view_t* view, const std::string& identifier) noexcept;
neo_webview_result_t neo_platform_view_post_message(neo_webview_view_t* view, const std::string& message, bool json, neo_webview_error_t** error) noexcept;
neo_webview_result_t neo_platform_view_get_handle(neo_webview_view_t* view, neo_webview_native_handle_kind_t kind, neo_webview_native_handle_t* handle) noexcept;
