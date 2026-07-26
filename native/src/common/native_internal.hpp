#pragma once

#include "neoastra.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cctype>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

struct neo_callback_activation {
    const void* slot;
    const neo_callback_activation* previous;
};

inline thread_local const neo_callback_activation* neo_active_callbacks = nullptr;

inline uint32_t neo_active_callback_count(const void* slot) noexcept {
    uint32_t count{};
    for (auto* active = neo_active_callbacks; active; active = active->previous) {
        if (active->slot == slot) ++count;
    }
    return count;
}

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
        const auto current_thread_active = neo_active_callback_count(this);
        _quiesced.wait(lock, [this, current_thread_active] { return _active <= current_thread_active; });
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

        const neo_callback_activation activation{this, neo_active_callbacks};
        neo_active_callbacks = &activation;
        try {
            invoker(callback, context);
        } catch (...) {
            // No exception may cross an ABI callback boundary.
        }
        neo_active_callbacks = activation.previous;

        {
            std::lock_guard lock(_mutex);
            --_active;
            _quiesced.notify_all();
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
            if (count == 1) on_zero_references();
            return true;
        }
    }

protected:
    virtual void on_zero_references() noexcept { delete this; }
};

struct neoastra_error final : neo_ref_counted {
    const neoastra_result_t code;
    const int64_t native_code;
    const std::string domain;
    const std::string message;

    neoastra_error(neoastra_result_t value_code, int64_t value_native_code, std::string value_domain, std::string value_message)
        : code(value_code), native_code(value_native_code), domain(std::move(value_domain)), message(std::move(value_message)) { }
};

struct neoastra_buffer final : neo_ref_counted {
    const std::vector<uint8_t> bytes;
    explicit neoastra_buffer(std::vector<uint8_t> value = {}) : bytes(std::move(value)) { }
};

struct neoastra_stream final : neo_ref_counted { };

enum class neo_operation_state : uint32_t { pending, cancel_requested, completed };

struct neoastra_operation final : neo_ref_counted {
    std::atomic<neo_operation_state> state{neo_operation_state::pending};

    void cancel() noexcept {
        auto expected = neo_operation_state::pending;
        state.compare_exchange_strong(expected, neo_operation_state::cancel_requested, std::memory_order_acq_rel);
    }

    bool try_complete(neoastra_result_t requested, neoastra_result_t& actual) noexcept {
        auto current = state.load(std::memory_order_acquire);
        for (;;) {
            if (current == neo_operation_state::completed) return false;
            actual = current == neo_operation_state::cancel_requested ? NEOASTRA_ERROR_CANCELED : requested;
            if (state.compare_exchange_weak(current, neo_operation_state::completed, std::memory_order_acq_rel, std::memory_order_acquire)) return true;
        }
    }
};

struct neo_custom_scheme_registration final {
    std::string name;
    uint32_t flags{};
    std::vector<std::string> allowed_origins;
    neoastra_resource_provider_callback_t provider{};
    void* provider_context{};
    neoastra_context_release_callback_t release_provider_context{};

    neo_custom_scheme_registration() = default;
    neo_custom_scheme_registration(const neo_custom_scheme_registration&) = delete;
    neo_custom_scheme_registration& operator=(const neo_custom_scheme_registration&) = delete;
    neo_custom_scheme_registration(neo_custom_scheme_registration&& other) noexcept
        : name(std::move(other.name)), flags(other.flags), allowed_origins(std::move(other.allowed_origins)),
          provider(other.provider), provider_context(other.provider_context),
          release_provider_context(other.release_provider_context) {
        other.provider = nullptr;
        other.provider_context = nullptr;
        other.release_provider_context = nullptr;
    }
    neo_custom_scheme_registration& operator=(neo_custom_scheme_registration&&) = delete;
    ~neo_custom_scheme_registration() {
        if (release_provider_context && provider_context) {
            try { release_provider_context(provider_context); } catch (...) { }
        }
    }
};

inline bool neo_bridge_access_allowed_for(const std::vector<neo_custom_scheme_registration>& custom_schemes,
                                          const std::vector<std::string>& bridge_origins,
                                          neoastra_bridge_policy_t policy, std::string_view uri) noexcept {
    (void)custom_schemes;
    if (policy == NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW) return true;
    if (policy != NEOASTRA_BRIDGE_TRUSTED_ORIGINS) return false;
    for (const auto& origin : bridge_origins) {
        auto normalized_origin = std::string_view(origin);
        while (normalized_origin.size() > 1 && normalized_origin.back() == '/') normalized_origin.remove_suffix(1);
        const auto prefix_matches = uri.size() >= normalized_origin.size() && std::equal(normalized_origin.begin(), normalized_origin.end(), uri.begin(),
            [](unsigned char left, unsigned char right) { return std::tolower(left) == std::tolower(right); });
        if (prefix_matches &&
            (uri.size() == normalized_origin.size() || uri[normalized_origin.size()] == '/' || uri[normalized_origin.size()] == '?' || uri[normalized_origin.size()] == '#')) return true;
    }
    return false;
}

inline bool neo_bridge_message_allowed_for(const std::vector<neo_custom_scheme_registration>& custom_schemes,
                                           const std::vector<std::string>& bridge_origins,
                                           neoastra_bridge_policy_t policy, uint32_t maximum_message_size, bool destroyed,
                                           std::string_view message, std::string_view uri) noexcept {
    return !destroyed && message.size() <= maximum_message_size &&
        neo_bridge_access_allowed_for(custom_schemes, bridge_origins, policy, uri);
}

enum class neo_decision_state : uint32_t { pending, deferred, completed, timed_out, abandoned };

struct neoastra_decision final : neo_ref_counted {
    std::atomic<neo_decision_state> state{neo_decision_state::pending};
    neoastra_decision_kind_t kind{NEOASTRA_DECISION_UNKNOWN};
    neoastra_decision_action_t default_action{NEOASTRA_DECISION_DENY};
    std::atomic<neoastra_decision_action_t> resolved_action{NEOASTRA_DECISION_DEFAULT};
    std::atomic_bool popup_creation_started{};
    neoastra_view_t* resolved_target{};
    std::chrono::steady_clock::time_point deadline{std::chrono::steady_clock::now() + std::chrono::seconds(30)};
    void (*completion)(void* context, const neoastra_decision_response_t* response) noexcept{};
    void* completion_context{};
    neoastra_view_t* owner{};
    neoastra_decision_t* owner_previous{};
    neoastra_decision_t* owner_next{};
    void* popup_context{};
    void (*popup_context_release)(void*) noexcept{};

    void detach_owner() noexcept;
    void resolve(const neoastra_decision_response_t& response) noexcept;
    void resolve(neoastra_decision_action_t action) noexcept;
    void abandon() noexcept;
    bool expire() noexcept;
    ~neoastra_decision() override;
};

struct neo_dispatch_item {
    neoastra_dispatch_callback_t callback{};
    void* context{};
};

enum class neo_app_state : uint32_t { created, running, stopping, stopped };

struct neo_ui_ref_counted;

struct neoastra_app final : neo_ref_counted {
    bool embedded{};
    std::atomic<bool> quit_requested{false};
    std::atomic<bool> stopping{false}; // Used by the platform loop after common shutdown has drained.
    std::atomic<bool> stopped{false};
    std::atomic<neo_app_state> state{neo_app_state::created};
    std::atomic<int32_t> exit_code{0};
    std::thread::id ui_thread;
    neoastra_app_shutdown_mode_t shutdown_mode{NEOASTRA_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED};
    uint32_t dispatch_limit{65536};
    std::mutex dispatch_mutex;
    std::deque<neo_dispatch_item> dispatches;
    std::mutex platform_mutex;
    std::mutex ui_lifetime_mutex;
    neo_ui_ref_counted* ui_objects{};
    neo_ui_ref_counted* pending_ui_destructions{};
    bool ui_shutdown_started{};
    bool ui_shutdown_complete{};
    void (*wake_ui)(neoastra_app_t*) noexcept{};
    neo_callback_slot<neoastra_event_callback_t> events;
    neo_callback_slot<neoastra_log_callback_t> logs;
    std::atomic<uint64_t> next_id{1};
    std::atomic<uint64_t> next_sequence{1};
    std::mutex windows_mutex;
    std::unordered_map<uint64_t, neoastra_window_t*> windows;
    uint64_t main_window_id{};
    void* platform{};
    ~neoastra_app() override;

protected:
    void on_zero_references() noexcept override;
};

inline void neo_wake_app(neoastra_app_t* app) noexcept {
    if (!app) return;
    std::lock_guard lock(app->platform_mutex);
    if (app->platform && app->wake_ui && !app->stopped.load(std::memory_order_acquire)) app->wake_ui(app);
}

struct neo_ui_ref_counted : neo_ref_counted {
    neoastra_app_t* const destruction_app;
    const uint32_t ui_destruction_phase;
    neo_ui_ref_counted* ui_previous{};
    neo_ui_ref_counted* ui_next{};
    neo_ui_ref_counted* pending_ui_next{};
    bool ui_registered{};
    std::atomic<bool> ui_destroyed{false};

    explicit neo_ui_ref_counted(neoastra_app_t* app, uint32_t destruction_phase = 0);
    ~neo_ui_ref_counted() override;

    void destroy_ui_once() noexcept {
        if (!ui_destroyed.exchange(true, std::memory_order_acq_rel)) destroy_ui();
    }

    virtual void destroy_ui() noexcept = 0;

protected:
    void on_zero_references() noexcept override;
};

inline neo_ui_ref_counted::neo_ui_ref_counted(neoastra_app_t* app, uint32_t destruction_phase)
    : destruction_app(app), ui_destruction_phase(destruction_phase) {
    if (!app || !app->retain()) throw std::bad_alloc();
    try {
        std::lock_guard lock(app->ui_lifetime_mutex);
        ui_next = app->ui_objects;
        if (ui_next) ui_next->ui_previous = this;
        app->ui_objects = this;
        ui_registered = true;
    } catch (...) {
        app->release();
        throw;
    }
}

inline neo_ui_ref_counted::~neo_ui_ref_counted() {
    {
        std::lock_guard lock(destruction_app->ui_lifetime_mutex);
        if (ui_registered) {
            if (ui_previous) ui_previous->ui_next = ui_next;
            else destruction_app->ui_objects = ui_next;
            if (ui_next) ui_next->ui_previous = ui_previous;
            ui_registered = false;
        }
    }
    destruction_app->release();
}

inline void neo_ui_ref_counted::on_zero_references() noexcept {
    auto* app = destruction_app;
    if (app->ui_thread == std::this_thread::get_id()) {
        delete this;
        return;
    }

    bool queued{};
    void (*wake)(neoastra_app_t*) noexcept{};
    {
        std::lock_guard lock(app->ui_lifetime_mutex);
        if (!app->ui_shutdown_complete) {
            if (ui_registered) {
                if (ui_previous) ui_previous->ui_next = ui_next;
                else app->ui_objects = ui_next;
                if (ui_next) ui_next->ui_previous = ui_previous;
                ui_previous = nullptr;
                ui_next = nullptr;
                ui_registered = false;
            }
            pending_ui_next = app->pending_ui_destructions;
            app->pending_ui_destructions = this;
            queued = true;
            if (!app->ui_shutdown_started) wake = app->wake_ui;
        } else if (ui_registered) {
            if (ui_previous) ui_previous->ui_next = ui_next;
            else app->ui_objects = ui_next;
            if (ui_next) ui_next->ui_previous = ui_previous;
            ui_registered = false;
        }
    }

    if (queued) {
        if (wake) neo_wake_app(app);
    } else delete this;
}

struct neoastra_environment final : neo_ui_ref_counted {
    neoastra_app_t* app{};
    std::vector<neo_custom_scheme_registration> custom_schemes;
    void* platform{};
    explicit neoastra_environment(neoastra_app_t* value) : neo_ui_ref_counted(value, 3), app(value) { }
    ~neoastra_environment() override;
    void destroy_ui() noexcept override;
};

struct neoastra_profile final : neo_ui_ref_counted {
    neoastra_environment_t* environment{};
    bool ephemeral{};
    std::string name;
    void* platform{};
    explicit neoastra_profile(neoastra_environment_t* value) : neo_ui_ref_counted(value->app, 2), environment(value) { environment->retain(); }
    ~neoastra_profile() override;
    void destroy_ui() noexcept override;
};

struct neoastra_window final : neo_ui_ref_counted {
    neoastra_app_t* app{};
    neoastra_window_t* owner{};
    uint64_t id{};
    std::mutex state_mutex;
    std::string title;
    neoastra_rect_t bounds{};
    neoastra_size_t minimum_size{};
    neoastra_size_t maximum_size{};
    neoastra_window_state_t state{NEOASTRA_WINDOW_NORMAL};
    std::atomic<bool> closed{false};
    std::vector<neoastra_view_t*> views; // UI-thread-only weak references.
    void* platform{};
    explicit neoastra_window(neoastra_app_t* value) : neo_ui_ref_counted(value, 1), app(value) { }
    ~neoastra_window() override;
    void destroy_ui() noexcept override;
};

struct neoastra_view final : neo_ui_ref_counted {
    neoastra_environment_t* environment{};
    neoastra_profile_t* profile{};
    neoastra_window_t* window{};
    neoastra_native_parent_t parent{};
    neoastra_rect_t bounds{};
    bool fill_parent{true};
    std::chrono::milliseconds decision_timeout{std::chrono::seconds(30)};
    std::mutex decisions_mutex;
    neoastra_decision_t* decisions{};
    std::vector<neoastra_download_t*> downloads; // UI-thread-only weak references.
    bool destroying{};
    std::string source;
    std::string title;
    uint32_t maximum_message_size{1024u * 1024u};
    neoastra_bridge_policy_t bridge_policy{NEOASTRA_BRIDGE_DISABLED};
    std::vector<std::string> bridge_origins;
    neo_callback_slot<neoastra_event_callback_t> events;
    void* platform{};
    explicit neoastra_view(neoastra_environment_t* value) : neo_ui_ref_counted(value->app), environment(value) { environment->retain(); }
    ~neoastra_view() override;
    void destroy_ui() noexcept override;
};

void neo_download_emit(neoastra_download_t* download, neoastra_event_type_t type) noexcept;
bool neo_bridge_access_allowed(const neoastra_view_t* view, std::string_view uri) noexcept;
bool neo_emit_bridge_message(neoastra_view_t* view, const std::string& message, const std::string& uri, bool main_frame) noexcept;

struct neoastra_download final : neo_ui_ref_counted {
    neoastra_view_t* view{};
    uint64_t id{};
    std::atomic<neoastra_download_state_t> state{NEOASTRA_DOWNLOAD_REQUESTED};
    std::atomic<uint64_t> bytes_received{};
    std::atomic<uint64_t> total_bytes{UINT64_MAX};
    std::atomic<bool> lifecycle_released{};
    std::string source_uri;
    std::string destination_path;
    std::string failure_reason;
    bool can_pause{};
    bool event_published{};
    bool destructing{};
    void* platform{};
    neoastra_result_t (*command)(neoastra_download_t*, uint32_t) noexcept{};
    void (*platform_destroy)(neoastra_download_t*) noexcept{};

    explicit neoastra_download(neoastra_view_t* owner)
        : neo_ui_ref_counted(owner->environment->app), view(owner), id(owner->environment->app->next_id.fetch_add(1)) {
        owner->downloads.push_back(this);
    }
    ~neoastra_download() override { destructing = true; destroy_ui_once(); }
    void destroy_ui() noexcept override {
        const auto current = state.load(std::memory_order_acquire);
        if (current == NEOASTRA_DOWNLOAD_REQUESTED || current == NEOASTRA_DOWNLOAD_IN_PROGRESS) {
            state.store(NEOASTRA_DOWNLOAD_CANCELED, std::memory_order_release);
            if (command) command(this, 0);
            neo_download_emit(this, NEOASTRA_EVENT_DOWNLOAD_COMPLETED);
        }
        if (platform_destroy) platform_destroy(this);
        platform = nullptr;
        if (view) {
            auto& tracked = view->downloads;
            tracked.erase(std::remove(tracked.begin(), tracked.end(), this), tracked.end());
            view = nullptr;
        }
        if (destructing) lifecycle_released.store(true, std::memory_order_release);
        else release_lifecycle();
    }

    void release_lifecycle() noexcept {
        if (!lifecycle_released.exchange(true, std::memory_order_acq_rel)) {
            destroy_ui_once();
            release();
        }
    }
};

struct neo_event_details {
    const std::string* text2{};
    const std::string* text3{};
    uint64_t value2{};
    neoastra_rect_t bounds{};
    neoastra_download_t* download{};
};

inline void neo_configure_decision(neoastra_decision_t* decision, const neoastra_view_t* view,
                                   neoastra_decision_kind_t kind, neoastra_decision_action_t default_action) noexcept {
    decision->kind = kind;
    decision->default_action = default_action;
    decision->deadline = std::chrono::steady_clock::now() + view->decision_timeout;
    auto* mutable_view = const_cast<neoastra_view_t*>(view);
    std::lock_guard lock(mutable_view->decisions_mutex);
    if (!mutable_view->destroying) {
        decision->owner = mutable_view;
        decision->owner_next = mutable_view->decisions;
        if (decision->owner_next) decision->owner_next->owner_previous = decision;
        mutable_view->decisions = decision;
        decision->retain();
    }
}

inline void neoastra_decision::detach_owner() noexcept {
    auto* view = owner;
    if (!view) return;
    bool detached{};
    {
        std::lock_guard lock(view->decisions_mutex);
        if (owner == view) {
            if (owner_previous) owner_previous->owner_next = owner_next;
            else view->decisions = owner_next;
            if (owner_next) owner_next->owner_previous = owner_previous;
            owner = nullptr;
            owner_previous = nullptr;
            owner_next = nullptr;
            detached = true;
        }
    }
    if (detached) release();
}

inline void neoastra_decision::resolve(const neoastra_decision_response_t& response) noexcept {
    resolved_action.store(response.action, std::memory_order_release);
    if (response.target_view && response.target_view->retain()) resolved_target = response.target_view;
    const auto callback = completion;
    const auto context = completion_context;
    completion = nullptr;
    completion_context = nullptr;
    if (callback) callback(context, &response);
    detach_owner();
}

inline void neoastra_decision::resolve(neoastra_decision_action_t action) noexcept {
    neoastra_decision_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    response.action = action;
    resolve(response);
}

inline void neoastra_decision::abandon() noexcept {
    auto current = state.load(std::memory_order_acquire);
    while (current == neo_decision_state::pending || current == neo_decision_state::deferred) {
        const auto was_deferred = current == neo_decision_state::deferred;
        if (state.compare_exchange_weak(current, neo_decision_state::abandoned, std::memory_order_acq_rel, std::memory_order_acquire)) {
            resolve(default_action);
            if (was_deferred) release();
            return;
        }
    }
}

inline bool neoastra_decision::expire() noexcept {
    if (std::chrono::steady_clock::now() < deadline) return false;
    auto current = state.load(std::memory_order_acquire);
    while (current == neo_decision_state::pending || current == neo_decision_state::deferred) {
        const auto was_deferred = current == neo_decision_state::deferred;
        if (state.compare_exchange_weak(current, neo_decision_state::timed_out, std::memory_order_acq_rel, std::memory_order_acquire)) {
            resolve(default_action);
            if (was_deferred) release();
            return true;
        }
    }
    return current == neo_decision_state::timed_out;
}

inline neoastra_decision::~neoastra_decision() {
    abandon();
    if (resolved_target) resolved_target->release();
    if (popup_context_release && popup_context) popup_context_release(popup_context);
}

inline neoastra_string_view_t neo_string_view(const std::string& text) noexcept {
    return {reinterpret_cast<const uint8_t*>(text.data()), static_cast<uint64_t>(text.size())};
}

inline uint64_t neo_timestamp_ns() noexcept {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}

bool neo_valid_utf8(neoastra_string_view_t text) noexcept;
std::string neo_string(neoastra_string_view_t text);

constexpr uint64_t neo_maximum_buffered_resource_body_size = 64ull * 1024ull * 1024ull;
constexpr uint64_t neo_maximum_resource_header_size = 1024ull * 1024ull;
constexpr uint64_t neo_maximum_resource_metadata_size = 32ull * 1024ull;

inline bool neo_resource_request_within_limits(std::string_view uri, std::string_view method,
                                               std::string_view headers, std::string_view initiating_origin = {}) noexcept {
    return uri.size() <= neo_maximum_resource_metadata_size &&
        method.size() <= neo_maximum_resource_metadata_size &&
        headers.size() <= neo_maximum_resource_header_size &&
        initiating_origin.size() <= neo_maximum_resource_metadata_size;
}

inline bool neo_absolute_resource_path(neoastra_string_view_t path) noexcept {
    if (path.length == 0 || !path.data) return false;
#if defined(_WIN32)
    if (path.length >= 3 && std::isalpha(path.data[0]) && path.data[1] == ':' && (path.data[2] == '/' || path.data[2] == '\\')) return true;
    return path.length >= 2 && (path.data[0] == '/' || path.data[0] == '\\') && (path.data[1] == '/' || path.data[1] == '\\');
#else
    return path.data[0] == '/';
#endif
}

inline bool neo_valid_resource_response_shape(const neoastra_resource_response_t& response) noexcept {
    return response.size >= sizeof(response) && response.version == 1 &&
        response.status_code >= 100 && response.status_code <= 599 &&
        response.body_kind <= NEOASTRA_RESOURCE_BODY_FILE &&
        response.reason_phrase.length <= neo_maximum_resource_metadata_size &&
        response.headers.length <= neo_maximum_resource_header_size &&
        response.mime_type.length <= neo_maximum_resource_metadata_size &&
        response.file_path.length <= neo_maximum_resource_metadata_size &&
        !(response.body_kind == NEOASTRA_RESOURCE_BODY_BYTES && response.byte_length && !response.bytes) &&
        !(response.body_kind == NEOASTRA_RESOURCE_BODY_EMPTY && (response.bytes || response.byte_length || response.file_path.length)) &&
        !(response.body_kind == NEOASTRA_RESOURCE_BODY_BYTES && response.file_path.length) &&
        !(response.body_kind == NEOASTRA_RESOURCE_BODY_FILE && (response.bytes || response.byte_length || !response.file_path.length)) &&
        (response.body_kind != NEOASTRA_RESOURCE_BODY_BYTES ||
            (response.byte_length <= neo_maximum_buffered_resource_body_size &&
             (response.content_length == UINT64_MAX || response.content_length == response.byte_length))) &&
        (response.body_kind != NEOASTRA_RESOURCE_BODY_FILE || neo_absolute_resource_path(response.file_path)) &&
        ((response.release_context != nullptr) == (response.release != nullptr));
}

inline bool neo_valid_response_header_name(std::string_view name) noexcept {
    if (name.empty()) return false;
    for (const auto character : name) {
        const auto byte = static_cast<unsigned char>(character);
        const auto alphanumeric = (byte >= 'a' && byte <= 'z') || (byte >= 'A' && byte <= 'Z') || (byte >= '0' && byte <= '9');
        if (!alphanumeric && std::string_view("!#$%&'*+-.^_`|~").find(character) == std::string_view::npos) return false;
    }
    return true;
}

inline bool neo_valid_response_headers(std::string_view headers) noexcept {
    for (size_t position = 0; position < headers.size();) {
        const auto end = headers.find('\n', position);
        auto line = headers.substr(position, end == std::string_view::npos ? headers.size() - position : end - position);
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        if (line.empty()) return false;
        const auto separator = line.find(':');
        if (separator == std::string_view::npos || !neo_valid_response_header_name(line.substr(0, separator))) return false;
        for (const auto character : line.substr(separator + 1)) {
            const auto byte = static_cast<unsigned char>(character);
            if (byte == 0 || byte == '\r' || (byte < 0x20 && byte != '\t') || byte == 0x7f) return false;
        }
        if (end == std::string_view::npos) break;
        position = end + 1;
    }
    return true;
}

inline bool neo_valid_single_line_text(std::string_view text) noexcept {
    return std::none_of(text.begin(), text.end(), [](char character) {
        const auto byte = static_cast<unsigned char>(character);
        return byte == 0 || byte == '\r' || byte == '\n' || (byte < 0x20 && byte != '\t') || byte == 0x7f;
    });
}

inline bool neo_valid_resource_response(const neoastra_resource_response_t& response) noexcept {
    if (!neo_valid_resource_response_shape(response) ||
        !neo_valid_utf8(response.reason_phrase) || !neo_valid_utf8(response.headers) ||
        !neo_valid_utf8(response.mime_type) || !neo_valid_utf8(response.file_path)) return false;
    const auto as_text = [](neoastra_string_view_t value) noexcept {
        return value.length == 0 ? std::string_view{} :
            std::string_view(reinterpret_cast<const char*>(value.data), static_cast<size_t>(value.length));
    };
    const auto reason = as_text(response.reason_phrase);
    const auto headers = as_text(response.headers);
    const auto mime = as_text(response.mime_type);
    return neo_valid_single_line_text(reason) && neo_valid_response_headers(headers) && neo_valid_single_line_text(mime);
}

struct neo_resource_response_release_guard final {
    neoastra_resource_response_t& response;

    void release_once() noexcept {
        const auto release = response.release;
        auto* context = response.release_context;
        response.release = nullptr;
        response.release_context = nullptr;
        if (release && context) {
            try { release(context); } catch (...) { }
        }
    }

    ~neo_resource_response_release_guard() { release_once(); }
};

neoastra_result_t neo_fail(neoastra_error_t** error, neoastra_result_t code, std::string message, int64_t native_code = 0, std::string domain = "neoastra") noexcept;
void neo_log(neoastra_app_t* app, neoastra_log_level_t level, std::string_view category, std::string_view message,
             int64_t native_code = 0, uint64_t object_id = 0) noexcept;
void neo_emit_app(neoastra_app_t* app, neoastra_event_type_t type, uint64_t object_id = 0, const std::string* text = nullptr, const std::string* uri = nullptr, uint64_t value = 0, int64_t native_code = 0, neoastra_decision_t* decision = nullptr) noexcept;
void neo_emit_view(neoastra_view_t* view, neoastra_event_type_t type, uint64_t object_id = 0, const std::string* text = nullptr, const std::string* uri = nullptr, uint64_t value = 0, int64_t native_code = 0, neoastra_decision_t* decision = nullptr) noexcept;
void neo_emit_view_detailed(neoastra_view_t* view, neoastra_event_type_t type, uint64_t object_id, const std::string* text, const std::string* uri, uint64_t value, int64_t native_code, neoastra_decision_t* decision, const neo_event_details& details) noexcept;
void neo_download_emit(neoastra_download_t* download, neoastra_event_type_t type) noexcept;
void neo_finish_decision_event(neoastra_view_t* view, neoastra_decision_t* decision) noexcept;
void neo_drain_dispatch(neoastra_app_t* app) noexcept;
void neo_complete_ui_shutdown(neoastra_app_t* app) noexcept;
void neo_complete_app_shutdown(neoastra_app_t* app) noexcept;
void neo_destroy_app_on_ui(neoastra_app_t* app) noexcept;
void neo_window_closed(neoastra_window_t* window) noexcept;

bool neo_platform_initialize(neoastra_app_t* app, neoastra_error_t** error) noexcept;
void neo_platform_shutdown(neoastra_app_t* app) noexcept;
bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept;
int32_t neo_platform_run(neoastra_app_t* app) noexcept;
void neo_platform_quit(neoastra_app_t* app) noexcept;
void neo_platform_wake(neoastra_app_t* app) noexcept;
bool neo_platform_schedule_decision_timeout(neoastra_view_t* view, neoastra_decision_t* decision) noexcept;
bool neo_platform_window_create(neoastra_window_t* window, const neoastra_window_options_t* options, neoastra_error_t** error) noexcept;
void neo_platform_window_destroy(neoastra_window_t* window) noexcept;
neoastra_result_t neo_platform_window_show(neoastra_window_t* window, bool visible) noexcept;
neoastra_result_t neo_platform_window_activate(neoastra_window_t* window) noexcept;
neoastra_result_t neo_platform_window_close(neoastra_window_t* window) noexcept;
neoastra_result_t neo_platform_window_set_title(neoastra_window_t* window) noexcept;
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t* window) noexcept;
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t* window) noexcept;
neoastra_result_t neo_platform_window_set_state(neoastra_window_t* window) noexcept;
neoastra_result_t neo_platform_window_get_handle(neoastra_window_t* window, neoastra_native_handle_kind_t kind, neoastra_native_handle_t* handle) noexcept;

using neo_platform_created_callback_t = void (*)(void* context, neoastra_error_t* error) noexcept;
bool neo_platform_environment_create_async(neoastra_environment_t* environment, const neoastra_environment_options_t* options, neo_platform_created_callback_t callback, void* context, neoastra_error_t** error) noexcept;
void neo_platform_environment_destroy(neoastra_environment_t* environment) noexcept;
bool neo_platform_profile_create(neoastra_profile_t* profile, neoastra_error_t** error) noexcept;
void neo_platform_profile_destroy(neoastra_profile_t* profile) noexcept;
neoastra_result_t neo_platform_profile_get_cookies(neoastra_profile_t* profile, const std::string& uri, neoastra_buffer_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept;
neoastra_result_t neo_platform_profile_set_cookie(neoastra_profile_t* profile, const neoastra_cookie_t* cookie, neoastra_completion_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept;
neoastra_result_t neo_platform_profile_delete_cookie(neoastra_profile_t* profile, const neoastra_cookie_t* cookie, neoastra_completion_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept;
neoastra_result_t neo_platform_profile_clear_data(neoastra_profile_t* profile, neoastra_data_kind_t kinds, int64_t start_unix_ms, int64_t end_unix_ms, neoastra_completion_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept;
bool neo_platform_view_create_async(neoastra_view_t* view, const neoastra_view_options_t* options, neo_platform_created_callback_t callback, void* context, neoastra_error_t** error) noexcept;
void neo_platform_view_destroy(neoastra_view_t* view) noexcept;
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t* view) noexcept;
neoastra_result_t neo_platform_view_navigate(neoastra_view_t* view, const std::string& uri, neoastra_error_t** error) noexcept;
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t* view, const std::string& uri, const std::string& method, const std::string& headers, const uint8_t* body, uint64_t body_length, neoastra_error_t** error) noexcept;
neoastra_result_t neo_platform_view_load_html(neoastra_view_t* view, const std::string& html, const std::string& base_uri, neoastra_error_t** error) noexcept;
neoastra_result_t neo_platform_view_command(neoastra_view_t* view, uint32_t command) noexcept;
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t* view, const std::string& script, neoastra_string_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept;
neoastra_result_t neo_platform_view_add_script(neoastra_view_t* view, const std::string& script, const neoastra_script_options_t* options, neoastra_string_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept;
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t* view, const std::string& identifier) noexcept;
neoastra_result_t neo_platform_view_post_message(neoastra_view_t* view, const std::string& message, bool json, neoastra_error_t** error) noexcept;
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t* view, double* factor) noexcept;
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t* view, double factor) noexcept;
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t* view, neoastra_native_handle_kind_t kind, neoastra_native_handle_t* handle) noexcept;
