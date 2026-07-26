#include "native_internal.hpp"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstring>
#include <new>
#include <stdexcept>

namespace {

constexpr uint32_t known_custom_scheme_flags = NEOASTRA_CUSTOM_SCHEME_HAS_AUTHORITY |
    NEOASTRA_CUSTOM_SCHEME_SECURE | NEOASTRA_CUSTOM_SCHEME_CORS_ENABLED |
    NEOASTRA_CUSTOM_SCHEME_APPLICATION | NEOASTRA_CUSTOM_SCHEME_SERVICE_WORKERS;

bool valid_scheme_name(std::string_view name) noexcept {
    if (name.empty() || !std::isalpha(static_cast<unsigned char>(name.front()))) return false;
    for (const auto character : name) {
        const auto byte = static_cast<unsigned char>(character);
        if (!std::isalnum(byte) && character != '+' && character != '-' && character != '.') return false;
    }
    return true;
}

bool built_in_scheme_name(std::string_view name) noexcept {
    constexpr std::string_view built_in_schemes[]{
        "about", "blob", "data", "file", "ftp", "http", "https", "javascript", "ws", "wss"
    };
    for (const auto built_in : built_in_schemes) if (name == built_in) return true;
    return false;
}

bool valid_port(std::string_view port) noexcept {
    if (port.empty()) return false;
    uint32_t value{};
    for (const auto character : port) {
        if (!std::isdigit(static_cast<unsigned char>(character))) return false;
        value = value * 10u + static_cast<uint32_t>(character - '0');
        if (value > 65535u) return false;
    }
    return true;
}

bool valid_origin(std::string_view origin) noexcept {
    while (origin.size() > 1 && origin.back() == '/') origin.remove_suffix(1);
    const auto separator = origin.find("://");
    if (separator == std::string_view::npos || !valid_scheme_name(origin.substr(0, separator)) ||
        separator + 3 >= origin.size() || origin.find_first_of("/?#", separator + 3) != std::string_view::npos) return false;
    const auto authority = origin.substr(separator + 3);
    if (authority.find('@') != std::string_view::npos || authority.find('\\') != std::string_view::npos) return false;
    for (const auto character : authority) {
        const auto byte = static_cast<unsigned char>(character);
        if (byte <= 0x20 || byte == 0x7f) return false;
    }
    if (authority.front() == '[') {
        const auto close = authority.find(']');
        if (close == std::string_view::npos || close == 1) return false;
        if (close + 1 == authority.size()) return true;
        return authority[close + 1] == ':' && valid_port(authority.substr(close + 2));
    }
    const auto port = authority.find(':');
    if (port == 0 || (port != std::string_view::npos &&
        (authority.find(':', port + 1) != std::string_view::npos || !valid_port(authority.substr(port + 1))))) return false;
    return true;
}

const neoastra_custom_scheme_t& custom_scheme_at(const neoastra_environment_options_t* options, uint32_t index) noexcept {
    const auto* bytes = reinterpret_cast<const uint8_t*>(options->custom_schemes);
    return *reinterpret_cast<const neoastra_custom_scheme_t*>(bytes + static_cast<size_t>(index) * options->custom_scheme_stride);
}

bool valid_custom_schemes(const neoastra_environment_options_t* options) noexcept {
    if (options->custom_scheme_count == 0) return options->custom_schemes == nullptr && options->custom_scheme_stride == 0;
    if (!options->custom_schemes || reinterpret_cast<uintptr_t>(options->custom_schemes) % alignof(neoastra_custom_scheme_t) != 0 ||
        options->custom_scheme_stride < sizeof(neoastra_custom_scheme_t) ||
        options->custom_scheme_stride % alignof(neoastra_custom_scheme_t) != 0 ||
        options->custom_scheme_count > SIZE_MAX / options->custom_scheme_stride) return false;
    std::vector<std::string> names;
    try {
        names.reserve(options->custom_scheme_count);
        for (uint32_t index = 0; index < options->custom_scheme_count; ++index) {
            const auto& scheme = custom_scheme_at(options, index);
            if (scheme.size < sizeof(scheme) || scheme.size > options->custom_scheme_stride || scheme.version != 1 || !neo_valid_utf8(scheme.name) ||
                !valid_scheme_name(neo_string(scheme.name)) || !scheme.resource_provider ||
                (scheme.flags & ~known_custom_scheme_flags) != 0 ||
                ((scheme.allowed_origin_count != 0) != (scheme.allowed_origins != nullptr))) return false;
            auto name = neo_string(scheme.name);
            std::transform(name.begin(), name.end(), name.begin(), [](unsigned char value) { return static_cast<char>(std::tolower(value)); });
            if (built_in_scheme_name(name) || std::find(names.begin(), names.end(), name) != names.end()) return false;
            names.push_back(std::move(name));
            for (uint32_t origin = 0; origin < scheme.allowed_origin_count; ++origin) {
                if (!neo_valid_utf8(scheme.allowed_origins[origin]) || !valid_origin(neo_string(scheme.allowed_origins[origin]))) return false;
            }
        }
        return true;
    } catch (...) { return false; }
}

void copy_custom_schemes(neoastra_environment_t* environment, const neoastra_environment_options_t* options) {
    environment->custom_schemes.reserve(options->custom_scheme_count);
    for (uint32_t index = 0; index < options->custom_scheme_count; ++index) {
        const auto& source = custom_scheme_at(options, index);
        neo_custom_scheme_registration target;
        target.name = neo_string(source.name);
        std::transform(target.name.begin(), target.name.end(), target.name.begin(), [](unsigned char value) { return static_cast<char>(std::tolower(value)); });
        target.flags = source.flags;
        target.provider = source.resource_provider;
        target.provider_context = source.resource_provider_context;
        target.allowed_origins.reserve(source.allowed_origin_count);
        for (uint32_t origin = 0; origin < source.allowed_origin_count; ++origin) target.allowed_origins.push_back(neo_string(source.allowed_origins[origin]));
        environment->custom_schemes.push_back(std::move(target));
    }
}

void drain_ui_destructions(neoastra_app_t* app) noexcept {
    for (;;) {
        neo_ui_ref_counted* pending{};
        {
            std::lock_guard lock(app->ui_lifetime_mutex);
            pending = app->pending_ui_destructions;
            app->pending_ui_destructions = nullptr;
        }
        if (!pending) return;
        while (pending) {
            auto* next = pending->pending_ui_next;
            pending->pending_ui_next = nullptr;
            delete pending;
            pending = next;
        }
    }
}

} // namespace

bool neo_bridge_access_allowed(const neoastra_view_t* view, std::string_view uri) noexcept {
    if (!view) return false;
    return neo_bridge_access_allowed_for(view->environment->custom_schemes, view->bridge_origins, view->bridge_policy, uri);
}

bool neo_emit_bridge_message(neoastra_view_t* view, const std::string& message, const std::string& uri, bool main_frame) noexcept {
    if (!view) return false;
    const auto destroyed = view->ui_destroyed.load(std::memory_order_acquire) || view->destroying;
    if (neo_bridge_message_allowed_for(view->environment->custom_schemes, view->bridge_origins,
                                       view->bridge_policy, view->maximum_message_size, destroyed, message, uri)) {
        neo_emit_view(view, NEOASTRA_EVENT_MESSAGE_RECEIVED, 0, &message, &uri, main_frame ? 1u : 0u);
        return true;
    }
    if (destroyed) return false;
    if (!neo_bridge_access_allowed(view, uri)) {
        neo_log(view->environment->app, NEOASTRA_LOG_WARNING, "bridge", "Blocked a web message by the configured bridge policy");
        return false;
    }
    if (message.size() > view->maximum_message_size) {
        neo_log(view->environment->app, NEOASTRA_LOG_WARNING, "bridge", "Blocked a web message that exceeded the configured size limit");
        return false;
    }
    return false;
}

neoastra_app::~neoastra_app() {
    events.clear();
    logs.clear();
}

void neoastra_app::on_zero_references() noexcept {
    if (ui_thread == std::this_thread::get_id()) {
        if (stopped.load(std::memory_order_acquire) || !platform) {
            delete this;
            return;
        }
        neo_complete_app_shutdown(this);
        delete this;
        return;
    }

    bool delete_now{};
    {
        std::lock_guard lock(platform_mutex);
        delete_now = stopped.load(std::memory_order_acquire) || !platform;
        if (!delete_now) {
            // Failure means that the owning loop can no longer accept work. Keep this
            // zero-reference object pending rather than performing platform teardown here.
            (void)neo_platform_schedule_app_destruction(this);
        }
    }
    if (delete_now) delete this;
}
neoastra_environment::~neoastra_environment() { destroy_ui_once(); }
void neoastra_environment::destroy_ui() noexcept { neo_platform_environment_destroy(this); }
neoastra_profile::~neoastra_profile() { destroy_ui_once(); environment->release(); }
void neoastra_profile::destroy_ui() noexcept { neo_platform_profile_destroy(this); }
neoastra_window::~neoastra_window() {
    destroy_ui_once();
    if (owner) owner->release();
}
void neoastra_window::destroy_ui() noexcept { neo_platform_window_destroy(this); }
neoastra_view::~neoastra_view() {
    destroy_ui_once();
    if (profile) profile->release();
    if (window) window->release();
    environment->release();
}
void neoastra_view::destroy_ui() noexcept {
    for (;;) {
        neoastra_decision_t* decision{};
        {
            std::lock_guard lock(decisions_mutex);
            destroying = true;
            decision = decisions;
            if (!decision) break;
            decisions = decision->owner_next;
            if (decisions) decisions->owner_previous = nullptr;
            decision->owner = nullptr;
            decision->owner_previous = nullptr;
            decision->owner_next = nullptr;
        }
        decision->abandon();
        decision->release();
    }
    while (!downloads.empty()) {
        auto* download = downloads.back();
        if (!download->retain()) {
            downloads.pop_back();
            continue;
        }
        download->destroy_ui_once();
        download->release();
    }
    events.clear();
    neo_platform_view_destroy(this);
    if (window) {
        auto& views = window->views;
        views.erase(std::remove(views.begin(), views.end(), this), views.end());
    }
}

bool neo_valid_utf8(neoastra_string_view_t text) noexcept {
    if (text.length == 0) return true;
    if (!text.data || text.length > static_cast<uint64_t>(SIZE_MAX)) return false;
    const auto* current = text.data;
    const auto* end = current + static_cast<size_t>(text.length);
    while (current < end) {
        const uint8_t lead = *current++;
        if (lead <= 0x7f) continue;
        uint32_t codepoint{};
        uint32_t continuation{};
        if (lead >= 0xc2 && lead <= 0xdf) { codepoint = lead & 0x1f; continuation = 1; }
        else if (lead >= 0xe0 && lead <= 0xef) { codepoint = lead & 0x0f; continuation = 2; }
        else if (lead >= 0xf0 && lead <= 0xf4) { codepoint = lead & 0x07; continuation = 3; }
        else return false;
        if (static_cast<size_t>(end - current) < continuation) return false;
        for (uint32_t index = 0; index < continuation; ++index) {
            const uint8_t value = *current++;
            if ((value & 0xc0) != 0x80) return false;
            codepoint = (codepoint << 6) | (value & 0x3f);
        }
        if ((continuation == 2 && codepoint < 0x800) ||
            (continuation == 3 && codepoint < 0x10000) ||
            codepoint > 0x10ffff || (codepoint >= 0xd800 && codepoint <= 0xdfff)) return false;
    }
    return true;
}

std::string neo_string(neoastra_string_view_t text) {
    if (!neo_valid_utf8(text)) throw std::invalid_argument("The string is not valid UTF-8");
    if (text.length == 0) return {};
    return {reinterpret_cast<const char*>(text.data), static_cast<size_t>(text.length)};
}

neoastra_result_t neo_fail(neoastra_error_t** output, neoastra_result_t code, std::string message, int64_t native_code, std::string domain) noexcept {
    if (output) {
        *output = nullptr;
        try { *output = new neoastra_error(code, native_code, std::move(domain), std::move(message)); }
        catch (...) { }
    }
    return code;
}

namespace {

template<class T> void retain(T* value) noexcept { if (value) value->retain(); }
template<class T> void release(T* value) noexcept { if (value) value->release(); }

bool valid_struct(const void* value, uint32_t supplied, size_t required, uint32_t version = 1) noexcept {
    if (!value || supplied < required) return false;
    uint32_t actual_version{};
    std::memcpy(&actual_version, static_cast<const uint8_t*>(value) + sizeof(uint32_t), sizeof(actual_version));
    return actual_version >= 1 && actual_version <= version;
}

bool valid_native_parent(const neoastra_native_parent_t& parent) noexcept {
    const auto is_default = parent.size == 0 && parent.version == 0 &&
        parent.kind == NEOASTRA_NATIVE_PARENT_NONE && parent.handle == nullptr;
    return is_default || valid_struct(&parent, parent.size, sizeof(parent));
}

bool valid_shutdown_mode(neoastra_app_shutdown_mode_t value) noexcept {
    return value >= NEOASTRA_APP_SHUTDOWN_EXPLICIT && value <= NEOASTRA_APP_SHUTDOWN_ON_MAIN_WINDOW_CLOSED;
}

bool valid_decision_action(neoastra_decision_action_t value) noexcept {
    return value >= NEOASTRA_DECISION_DEFAULT && value <= NEOASTRA_DECISION_HANDLED_EXTERNAL;
}

bool check_ui(const neoastra_app_t* app) noexcept {
    return app && app->ui_thread == std::this_thread::get_id();
}

bool accepts_ui_objects(const neoastra_app_t* app) noexcept {
    if (!app) return false;
    const auto state = app->state.load(std::memory_order_acquire);
    return state == neo_app_state::created || state == neo_app_state::running;
}

void initialize_event(neoastra_app_t* app, neoastra_event_t& event, neoastra_event_type_t type, uint64_t object_id,
                      const std::string* text, const std::string* uri, uint64_t value, int64_t native_code,
                      neoastra_decision_t* decision, const neo_event_details* details = nullptr) noexcept {
    event.header = {sizeof(event), 1, type, app ? app->next_sequence.fetch_add(1, std::memory_order_relaxed) : 0, neo_timestamp_ns()};
    event.object_id = object_id;
    if (text) event.text = neo_string_view(*text);
    if (uri) event.uri = neo_string_view(*uri);
    event.value = value;
    event.native_code = native_code;
    event.decision = decision;
    if (details) {
        if (details->text2) event.text2 = neo_string_view(*details->text2);
        if (details->text3) event.text3 = neo_string_view(*details->text3);
        event.value2 = details->value2;
        event.bounds = details->bounds;
        event.download = details->download;
    }
}

struct environment_completion {
    neoastra_environment_created_callback_t callback{}; void* context{}; neoastra_operation_t* operation{};
    neoastra_environment_t* value{}; neoastra_error_t* error{};
};
void NEOASTRA_CALL complete_environment(void* pointer) {
    auto* state = static_cast<environment_completion*>(pointer);
    neoastra_result_t result{};
    const auto requested = state->error ? state->error->code : NEOASTRA_OK;
    if (state->operation->try_complete(requested, result)) {
        state->callback(state->context, result, result == NEOASTRA_OK ? state->value : nullptr, state->error);
        if (result != NEOASTRA_OK && state->value) state->value->release();
    } else if (state->value) state->value->release();
    if (state->error) state->error->release();
    state->operation->release();
    delete state;
}
void platform_environment_created(void* pointer, neoastra_error_t* error) noexcept {
    auto* state = static_cast<environment_completion*>(pointer);
    state->error = error;
    if (neoastra_app_dispatch(state->value->app, complete_environment, state) != NEOASTRA_OK) {
        neoastra_result_t ignored{};
        state->operation->try_complete(NEOASTRA_ERROR_CANCELED, ignored);
        state->value->release();
        if (state->error) state->error->release();
        state->operation->release();
        delete state;
    }
}

struct profile_completion {
    neoastra_profile_created_callback_t callback{}; void* context{}; neoastra_operation_t* operation{}; neoastra_profile_t* value{};
};
void NEOASTRA_CALL complete_profile(void* pointer) {
    auto* state = static_cast<profile_completion*>(pointer);
    neoastra_result_t result{};
    if (state->operation->try_complete(NEOASTRA_OK, result)) {
        state->callback(state->context, result, result == NEOASTRA_OK ? state->value : nullptr, nullptr);
        if (result != NEOASTRA_OK) state->value->release();
    } else state->value->release();
    state->operation->release();
    delete state;
}

struct view_completion {
    neoastra_view_created_callback_t callback{}; void* context{}; neoastra_operation_t* operation{};
    neoastra_view_t* value{}; neoastra_error_t* error{}; bool popup{};
};
void NEOASTRA_CALL complete_view(void* pointer) {
    auto* state = static_cast<view_completion*>(pointer);
    neoastra_result_t result{};
    const auto requested = state->error ? state->error->code : NEOASTRA_OK;
    if (state->operation->try_complete(requested, result)) {
        state->callback(state->context, result, result == NEOASTRA_OK ? state->value : nullptr, state->error);
        if (result != NEOASTRA_OK && state->value) state->value->release();
    } else if (state->value) state->value->release();
    if (state->error) state->error->release();
    state->operation->release();
    delete state;
}
void platform_view_created(void* pointer, neoastra_error_t* error) noexcept {
    auto* state = static_cast<view_completion*>(pointer);
    state->error = error;
    if (state->popup && check_ui(state->value->environment->app)) {
        complete_view(state);
        return;
    }
    if (neoastra_app_dispatch(state->value->environment->app, complete_view, state) != NEOASTRA_OK) {
        neoastra_result_t ignored{};
        state->operation->try_complete(NEOASTRA_ERROR_CANCELED, ignored);
        state->value->release();
        if (state->error) state->error->release();
        state->operation->release();
        delete state;
    }
}

neoastra_operation_t* make_operation(neoastra_operation_t** output) {
    auto* operation = new neoastra_operation;
    if (output) { operation->retain(); *output = operation; }
    return operation;
}

neoastra_result_t schedule(neoastra_app_t* app, neoastra_dispatch_callback_t callback, void* context,
                              neoastra_error_t** error, const char* message) noexcept {
    const auto result = neoastra_app_dispatch(app, callback, context);
    return result == NEOASTRA_OK ? result : neo_fail(error, result, message);
}

template<class TStarter>
neoastra_result_t start_profile_operation(neoastra_profile_t* profile, neoastra_completion_callback_t callback,
                                             neoastra_operation_t** output, neoastra_error_t** error,
                                             TStarter&& starter) {
    if (output) *output = nullptr;
    if (!profile || !callback) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "invalid profile operation");
    if (!check_ui(profile->environment->app)) return neo_fail(error, NEOASTRA_ERROR_WRONG_THREAD, "profile operations must begin on the UI thread");
    neoastra_operation_t* operation{};
    try {
        operation = make_operation(output);
        const auto result = starter(operation);
        if (result != NEOASTRA_OK) {
            operation->release();
            if (output && *output) { (*output)->release(); *output = nullptr; }
        }
        return result;
    } catch (const std::exception& exception) {
        if (operation) operation->release();
        if (output && *output) { (*output)->release(); *output = nullptr; }
        return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, exception.what());
    } catch (...) {
        if (operation) operation->release();
        if (output && *output) { (*output)->release(); *output = nullptr; }
        return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "profile operation failed");
    }
}

} // namespace

void neo_emit_app(neoastra_app_t* app, neoastra_event_type_t type, uint64_t object_id, const std::string* text,
                  const std::string* uri, uint64_t value, int64_t native_code, neoastra_decision_t* decision) noexcept {
    if (!app) return;
    neoastra_event_t event{};
    initialize_event(app, event, type, object_id, text, uri, value, native_code, decision);
    app->events.invoke([&](auto callback, void* context) { callback(context, &event); });
}

void neo_log(neoastra_app_t* app, neoastra_log_level_t level, std::string_view category, std::string_view message,
             int64_t native_code, uint64_t object_id) noexcept {
    if (!app || neo_active_callback_count(&app->logs) != 0 || !app->retain()) return;
    try {
        const neoastra_string_view_t category_view{
            reinterpret_cast<const uint8_t*>(category.data()), static_cast<uint64_t>(category.size())};
        const neoastra_string_view_t message_view{
            reinterpret_cast<const uint8_t*>(message.data()), static_cast<uint64_t>(message.size())};
        const auto hashed_thread_id = static_cast<uint64_t>(std::hash<std::thread::id>{}(std::this_thread::get_id()));
        const auto thread_id = hashed_thread_id == 0 ? UINT64_C(1) : hashed_thread_id;
        const auto timestamp = neo_timestamp_ns();
        app->logs.invoke([&](auto callback, void* context) {
            callback(context, level, category_view, message_view, thread_id, timestamp, native_code, object_id);
        });
    } catch (...) {
        // Logging is diagnostic and must never affect the operation that emitted it.
    }
    app->release();
}

void neo_emit_view(neoastra_view_t* view, neoastra_event_type_t type, uint64_t object_id, const std::string* text,
                   const std::string* uri, uint64_t value, int64_t native_code, neoastra_decision_t* decision) noexcept {
    if (!view || view->ui_destroyed.load(std::memory_order_acquire)) return;
    neoastra_event_t event{};
    initialize_event(view->environment->app, event, type, object_id, text, uri, value, native_code, decision);
    view->events.invoke([&](auto callback, void* context) { callback(context, &event); });
}

void neo_emit_view_detailed(neoastra_view_t* view, neoastra_event_type_t type, uint64_t object_id, const std::string* text,
                            const std::string* uri, uint64_t value, int64_t native_code, neoastra_decision_t* decision,
                            const neo_event_details& details) noexcept {
    if (!view || view->ui_destroyed.load(std::memory_order_acquire)) return;
    neoastra_event_t event{};
    initialize_event(view->environment->app, event, type, object_id, text, uri, value, native_code, decision, &details);
    view->events.invoke([&](auto callback, void* context) { callback(context, &event); });
}

void neo_download_emit(neoastra_download_t* download, neoastra_event_type_t type) noexcept {
    if (!download || !download->view || !download->event_published) return;
    neo_event_details details{};
    details.value2 = download->total_bytes.load(std::memory_order_acquire);
    details.download = download;
    neo_emit_view_detailed(download->view, type, download->id, &download->destination_path, &download->source_uri,
                           download->bytes_received.load(std::memory_order_acquire), 0, nullptr, details);
}

void neo_drain_dispatch(neoastra_app_t* app) noexcept {
    if (!app || !check_ui(app) || !app->retain()) return;
    drain_ui_destructions(app);
    for (;;) {
        neo_dispatch_item item{};
        {
            std::lock_guard lock(app->dispatch_mutex);
            if (app->dispatches.empty()) break;
            item = app->dispatches.front();
            app->dispatches.pop_front();
        }
        try { item.callback(item.context); } catch (...) { }
        app->release();
        drain_ui_destructions(app);
    }
    drain_ui_destructions(app);
    if (app->quit_requested.load(std::memory_order_acquire)) {
        {
            std::lock_guard lock(app->dispatch_mutex);
            app->stopping.store(true, std::memory_order_release);
            app->state.store(neo_app_state::stopping, std::memory_order_release);
        }
        neo_complete_ui_shutdown(app);
    }
    app->release();
}

void neo_complete_ui_shutdown(neoastra_app_t* app) noexcept {
    if (!app || !check_ui(app)) return;
    {
        std::lock_guard lock(app->ui_lifetime_mutex);
        if (app->ui_shutdown_complete) return;
        app->ui_shutdown_started = true;
    }
    app->events.clear();
    app->logs.clear();

    for (;;) {
        drain_ui_destructions(app);
        neo_ui_ref_counted* candidate{};
        bool candidate_retained{};
        {
            std::lock_guard lock(app->ui_lifetime_mutex);
            for (auto* value = app->ui_objects; value; value = value->ui_next) {
                if (!value->ui_destroyed.load(std::memory_order_acquire) &&
                    (!candidate || value->ui_destruction_phase < candidate->ui_destruction_phase)) {
                    candidate = value;
                }
            }
            if (candidate) candidate_retained = candidate->retain();
            if (!candidate && !app->pending_ui_destructions) {
                app->ui_shutdown_complete = true;
                return;
            }
        }
        if (candidate) {
            candidate->destroy_ui_once();
            if (candidate_retained) candidate->release();
        }
    }
}

void neo_complete_app_shutdown(neoastra_app_t* app) noexcept {
    if (!app || !check_ui(app) || app->stopped.load(std::memory_order_acquire)) return;
    {
        std::lock_guard lock(app->dispatch_mutex);
        if (app->stopped.load(std::memory_order_relaxed)) return;
        app->stopping.store(true, std::memory_order_release);
        app->state.store(neo_app_state::stopping, std::memory_order_release);
    }

    neo_log(app, NEOASTRA_LOG_INFORMATION, "application", "Native application shutdown started");
    neo_drain_dispatch(app);
    neo_complete_ui_shutdown(app);
    app->events.clear();
    app->logs.clear();
    {
        std::lock_guard lock(app->platform_mutex);
        if (app->platform) neo_platform_shutdown(app);
    }
    app->stopped.store(true, std::memory_order_release);
    app->state.store(neo_app_state::stopped, std::memory_order_release);
}

void neo_destroy_app_on_ui(neoastra_app_t* app) noexcept {
    if (!app || !check_ui(app) || app->references.load(std::memory_order_acquire) != 0) return;
    neo_complete_app_shutdown(app);
    delete app;
}

void neo_window_closed(neoastra_window_t* window) noexcept {
    if (!window || window->closed.exchange(true, std::memory_order_acq_rel)) return;
    auto* app = window->app;
    bool should_quit{};
    {
        std::lock_guard lock(app->windows_mutex);
        auto found = app->windows.find(window->id);
        if (found != app->windows.end()) {
            app->windows.erase(found);
            should_quit = (app->shutdown_mode == NEOASTRA_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED && app->windows.empty()) ||
                          (app->shutdown_mode == NEOASTRA_APP_SHUTDOWN_ON_MAIN_WINDOW_CLOSED && app->main_window_id == window->id);
        }
    }
    neo_emit_app(app, NEOASTRA_EVENT_WINDOW_CLOSED, window->id);
    if (should_quit) neoastra_app_quit(app, 0);
}

void neo_finish_decision_event(neoastra_view_t* view, neoastra_decision_t* decision) noexcept {
    const auto state=decision->state.load(std::memory_order_acquire);
    if(state==neo_decision_state::pending){
        neoastra_decision_response_t response{};response.size=sizeof(response);response.version=1;response.action=decision->default_action;
        neoastra_decision_complete(decision,&response,nullptr);
    }else if(state==neo_decision_state::deferred&&!neo_platform_schedule_decision_timeout(view,decision)){
        decision->abandon();
    }
}

extern "C" {

uint32_t NEOASTRA_CALL neoastra_get_abi_version_major() { return NEOASTRA_ABI_VERSION_MAJOR; }
uint32_t NEOASTRA_CALL neoastra_get_abi_version_minor() { return NEOASTRA_ABI_VERSION_MINOR; }
neoastra_string_view_t NEOASTRA_CALL neoastra_get_version() { static const std::string value = "0.1.0"; return neo_string_view(value); }

neoastra_result_t NEOASTRA_CALL neoastra_get_runtime_info(neoastra_runtime_info_t* info, neoastra_error_t** error) {
    if (!valid_struct(info, info ? info->size : 0, sizeof(neoastra_runtime_info_t))) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "runtime info has an invalid size or version");
#if defined(_WIN32)
    static const std::string backend="webview2", os="windows";
#elif defined(__APPLE__)
    static const std::string backend="wkwebview", os="macos";
#else
    static const std::string backend="webkitgtk", os="linux";
#endif
#if defined(_M_ARM64) || defined(__aarch64__)
    static const std::string architecture="arm64";
#elif defined(_M_IX86) || defined(__i386__)
    static const std::string architecture="x86";
#else
    static const std::string architecture="x64";
#endif
    static const std::string version="system";
    info->backend_name=neo_string_view(backend); info->backend_version=neo_string_view(version);
    info->browser_version=neo_string_view(version); info->operating_system=neo_string_view(os);
    info->architecture=neo_string_view(architecture); info->build_features=0;
#ifdef NDEBUG
    info->debug_build=0;
#else
    info->debug_build=1;
#endif
    return NEOASTRA_OK;
}

#define NEO_LIFETIME(name) void NEOASTRA_CALL neoastra_##name##_retain(neoastra_##name##_t* value){retain(value);} void NEOASTRA_CALL neoastra_##name##_release(neoastra_##name##_t* value){release(value);}
NEO_LIFETIME(app) NEO_LIFETIME(environment) NEO_LIFETIME(profile) NEO_LIFETIME(window) NEO_LIFETIME(view)
NEO_LIFETIME(operation) NEO_LIFETIME(decision) NEO_LIFETIME(download) NEO_LIFETIME(error) NEO_LIFETIME(buffer) NEO_LIFETIME(stream)
#undef NEO_LIFETIME

neoastra_result_t NEOASTRA_CALL neoastra_error_get_code(const neoastra_error_t* value) { return value ? value->code : NEOASTRA_ERROR_INVALID_ARGUMENT; }
int64_t NEOASTRA_CALL neoastra_error_get_native_code(const neoastra_error_t* value) { return value ? value->native_code : 0; }
neoastra_string_view_t NEOASTRA_CALL neoastra_error_get_domain(const neoastra_error_t* value) { return value ? neo_string_view(value->domain) : neoastra_string_view_t{}; }
neoastra_string_view_t NEOASTRA_CALL neoastra_error_get_message(const neoastra_error_t* value) { return value ? neo_string_view(value->message) : neoastra_string_view_t{}; }
const uint8_t* NEOASTRA_CALL neoastra_buffer_get_data(const neoastra_buffer_t* value) { return value && !value->bytes.empty() ? value->bytes.data() : nullptr; }
uint64_t NEOASTRA_CALL neoastra_buffer_get_length(const neoastra_buffer_t* value) { return value ? static_cast<uint64_t>(value->bytes.size()) : 0; }
void NEOASTRA_CALL neoastra_operation_cancel(neoastra_operation_t* value) { if (value) value->cancel(); }

neoastra_result_t NEOASTRA_CALL neoastra_decision_defer(neoastra_decision_t* value) {
    if (!value) return NEOASTRA_ERROR_INVALID_ARGUMENT;
    if (value->expire()) return NEOASTRA_ERROR_TIMED_OUT;
    auto expected=neo_decision_state::pending;
    value->retain();
    if (value->state.compare_exchange_strong(expected,neo_decision_state::deferred,std::memory_order_acq_rel)) return NEOASTRA_OK;
    value->release();
    return NEOASTRA_ERROR_INVALID_STATE;
}
neoastra_result_t NEOASTRA_CALL neoastra_decision_complete(neoastra_decision_t* value, const neoastra_decision_response_t* response, neoastra_error_t** error) {
    if (!value || !valid_struct(response, response ? response->size : 0, sizeof(*response)) || !valid_decision_action(response->action)) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "invalid decision response");
    if (value->owner && !check_ui(value->owner->environment->app)) return neo_fail(error, NEOASTRA_ERROR_WRONG_THREAD, "decision completion must run on the UI thread");
    if (!neo_valid_utf8(response->text) || !neo_valid_utf8(response->secondary_text) || (response->path_count && !response->paths)) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "decision response contains invalid strings");
    if (response->target_view && (value->kind != NEOASTRA_DECISION_NEW_WINDOW || !value->owner || response->target_view->environment != value->owner->environment || response->target_view->profile != value->owner->profile)) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "popup target is not opener-compatible");
    for (uint32_t index=0; index<response->path_count; ++index) if (!neo_valid_utf8(response->paths[index])) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "decision response contains invalid paths");
    if (value->expire()) return neo_fail(error, NEOASTRA_ERROR_TIMED_OUT, "decision expired");
    auto current=value->state.load(std::memory_order_acquire);
    while(current==neo_decision_state::pending || current==neo_decision_state::deferred) {
        if(value->state.compare_exchange_weak(current,neo_decision_state::completed,std::memory_order_acq_rel,std::memory_order_acquire)) {
            const auto was_deferred=current==neo_decision_state::deferred;
            value->resolve(*response);
            if(was_deferred)value->release();
            return NEOASTRA_OK;
        }
    }
    return neo_fail(error, NEOASTRA_ERROR_INVALID_STATE, "decision is already complete");
}

neoastra_decision_kind_t NEOASTRA_CALL neoastra_decision_get_kind(const neoastra_decision_t* value) { return value ? value->kind : NEOASTRA_DECISION_UNKNOWN; }
neoastra_decision_action_t NEOASTRA_CALL neoastra_decision_get_default_action(const neoastra_decision_t* value) { return value ? value->default_action : NEOASTRA_DECISION_DENY; }
uint64_t NEOASTRA_CALL neoastra_decision_get_deadline_ns(const neoastra_decision_t* value) { return value ? static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(value->deadline.time_since_epoch()).count()) : 0; }
neoastra_result_t NEOASTRA_CALL neoastra_download_get_info(const neoastra_download_t* value, neoastra_download_info_t* info) {
    if (!value || !valid_struct(info, info ? info->size : 0, sizeof(*info))) return NEOASTRA_ERROR_INVALID_ARGUMENT;
    if (!check_ui(value->destruction_app)) return NEOASTRA_ERROR_WRONG_THREAD;
    info->id=value->id;info->state=value->state.load(std::memory_order_acquire);info->can_pause=value->can_pause?1u:0u;
    info->source_uri=neo_string_view(value->source_uri);info->destination_path=neo_string_view(value->destination_path);
    info->bytes_received=value->bytes_received.load(std::memory_order_acquire);info->total_bytes=value->total_bytes.load(std::memory_order_acquire);
    info->failure_reason=neo_string_view(value->failure_reason);return NEOASTRA_OK;
}
static neoastra_result_t download_command(neoastra_download_t* value,uint32_t command) {
    if(!value)return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(value->destruction_app))return NEOASTRA_ERROR_WRONG_THREAD;
    const auto state=value->state.load(std::memory_order_acquire);if(state==NEOASTRA_DOWNLOAD_COMPLETED||state==NEOASTRA_DOWNLOAD_CANCELED||state==NEOASTRA_DOWNLOAD_FAILED)return NEOASTRA_ERROR_INVALID_STATE;
    if(!value->command)return NEOASTRA_ERROR_NOT_SUPPORTED;return value->command(value,command);
}
neoastra_result_t NEOASTRA_CALL neoastra_download_cancel(neoastra_download_t* value){return download_command(value,0);}
neoastra_result_t NEOASTRA_CALL neoastra_download_pause(neoastra_download_t* value){return download_command(value,1);}
neoastra_result_t NEOASTRA_CALL neoastra_download_resume(neoastra_download_t* value){return download_command(value,2);}

neoastra_result_t NEOASTRA_CALL neoastra_app_create(const neoastra_app_options_t* options, neoastra_app_t** output, neoastra_error_t** error) {
    if (output) *output=nullptr;
    if (!output || !valid_struct(options, options ? options->size : 0, sizeof(*options)) || !valid_shutdown_mode(options->shutdown_mode) || !neo_valid_utf8(options->application_name)) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "invalid application options");
    try {
        auto* app=new neoastra_app;
        app->ui_thread=std::this_thread::get_id(); app->shutdown_mode=options->shutdown_mode;
        if(options->maximum_pending_dispatches) app->dispatch_limit=options->maximum_pending_dispatches;
        app->logs.set(options->log_callback,options->log_context);
        if(!neo_platform_initialize(app,error)){app->release();return error&&*error?(*error)->code:NEOASTRA_ERROR_BACKEND_UNAVAILABLE;}
        app->wake_ui=neo_platform_wake;
        neo_log(app, NEOASTRA_LOG_INFORMATION, "application", "Native application initialized");
        *output=app;
        return NEOASTRA_OK;
    } catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,ex.what());}
}
neoastra_result_t NEOASTRA_CALL neoastra_app_attach(const neoastra_app_options_t* options, neoastra_app_t** output, neoastra_error_t** error) {
    const auto result = neoastra_app_create(options, output, error);
    if (result != NEOASTRA_OK) return result;
    if (!output || !*output) return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "application creation returned no instance");
    (*output)->embedded = true;
    return NEOASTRA_OK;
}
neoastra_result_t NEOASTRA_CALL neoastra_app_detach(neoastra_app_t* app, neoastra_error_t** error) {
    if (error) *error = nullptr;
    if (!app) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "application is null");
    if (!app->retain()) return neo_fail(error, NEOASTRA_ERROR_DISPOSED, "application is disposed");
    if (!check_ui(app)) {
        app->release();
        return neo_fail(error, NEOASTRA_ERROR_WRONG_THREAD, "application detach must run on the owning UI thread");
    }
    const auto state = app->state.load(std::memory_order_acquire);
    if (!app->embedded && state == neo_app_state::running) {
        app->release();
        return neo_fail(error, NEOASTRA_ERROR_INVALID_STATE, "a running standalone application is shut down by its run loop");
    }
    neo_complete_app_shutdown(app);
    app->release();
    return NEOASTRA_OK;
}
int32_t NEOASTRA_CALL neoastra_app_run(neoastra_app_t* app) {
    if(!app || app->embedded) return NEOASTRA_ERROR_INVALID_STATE;
    if(!check_ui(app)) return NEOASTRA_ERROR_WRONG_THREAD;
    if(!app->retain()) return NEOASTRA_ERROR_DISPOSED;
    auto expected=neo_app_state::created;
    if(!app->state.compare_exchange_strong(expected,neo_app_state::running)){app->release();return NEOASTRA_ERROR_INVALID_STATE;}
    const auto result=neo_platform_run(app);
    neo_complete_app_shutdown(app);
    app->release();
    return result;
}
void NEOASTRA_CALL neoastra_app_quit(neoastra_app_t* app, int32_t code) { if(!app)return; app->exit_code.store(code); app->quit_requested.store(true,std::memory_order_release); std::lock_guard lock(app->platform_mutex); if(app->platform&&!app->stopped.load(std::memory_order_acquire)){neo_platform_wake(app);neo_platform_quit(app);} }
neoastra_result_t NEOASTRA_CALL neoastra_app_dispatch(neoastra_app_t* app, neoastra_dispatch_callback_t callback, void* context) {
    if(!app||!callback)return NEOASTRA_ERROR_INVALID_ARGUMENT;
    if(!app->retain())return NEOASTRA_ERROR_DISPOSED;
    neoastra_result_t result=NEOASTRA_OK;
    try { std::lock_guard lock(app->dispatch_mutex); if(app->stopped.load()||app->state.load()==neo_app_state::stopping)result=NEOASTRA_ERROR_DISPOSED;else if(app->dispatches.size()>=app->dispatch_limit)result=NEOASTRA_ERROR_INVALID_STATE;else app->dispatches.push_back({callback,context}); }
    catch (...) { result=NEOASTRA_ERROR_NATIVE_FAILURE; }
    if(result!=NEOASTRA_OK){
        if(result==NEOASTRA_ERROR_INVALID_STATE)neo_log(app,NEOASTRA_LOG_WARNING,"dispatcher","Native dispatcher queue limit reached");
        app->release();return result;
    }
    neo_wake_app(app); return NEOASTRA_OK;
}
neoastra_result_t NEOASTRA_CALL neoastra_app_set_event_callback(neoastra_app_t* app, neoastra_event_callback_t callback, void* context){if(!app)return NEOASTRA_ERROR_INVALID_ARGUMENT;app->events.set(callback,context);return NEOASTRA_OK;}
neoastra_result_t NEOASTRA_CALL neoastra_app_create_window(neoastra_app_t* app,const neoastra_window_options_t* options,neoastra_window_t** output,neoastra_error_t** error){
    if(output)*output=nullptr;
    if(!app||!output||!valid_struct(options,options?options->size:0,sizeof(*options))||!neo_valid_utf8(options->title)||options->bounds.width<=0||options->bounds.height<=0||options->minimum_size.width<0||options->minimum_size.height<0||options->maximum_size.width<0||options->maximum_size.height<0||options->state>NEOASTRA_WINDOW_FULLSCREEN||(options->maximum_size.width>0&&options->minimum_size.width>options->maximum_size.width)||(options->maximum_size.height>0&&options->minimum_size.height>options->maximum_size.height))return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid window arguments");
    if(!check_ui(app))return neo_fail(error,NEOASTRA_ERROR_WRONG_THREAD,"window creation must run on the UI thread");
    if(!accepts_ui_objects(app))return neo_fail(error,NEOASTRA_ERROR_DISPOSED,"application shutdown has begun");
    try{
        auto* window=new neoastra_window(app);window->id=app->next_id.fetch_add(1);window->title=neo_string(options->title);window->bounds=options->bounds;window->minimum_size=options->minimum_size;window->maximum_size=options->maximum_size;window->state=options->state;
        if(options->owner){window->owner=options->owner;window->owner->retain();}
        if(!neo_platform_window_create(window,options,error)){window->release();return error&&*error?(*error)->code:NEOASTRA_ERROR_NATIVE_FAILURE;}
        {std::lock_guard lock(app->windows_mutex);app->windows.emplace(window->id,window);if(!app->main_window_id)app->main_window_id=window->id;}
        *output=window;return NEOASTRA_OK;
    }catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,ex.what());}
}
uint64_t NEOASTRA_CALL neoastra_app_get_window_count(const neoastra_app_t* app){if(!app)return 0;std::lock_guard lock(const_cast<neoastra_app_t*>(app)->windows_mutex);return app->windows.size();}
neoastra_result_t NEOASTRA_CALL neoastra_app_get_window(neoastra_app_t* app,uint64_t id,neoastra_window_t** output){if(output)*output=nullptr;if(!app||!output)return NEOASTRA_ERROR_INVALID_ARGUMENT;std::lock_guard lock(app->windows_mutex);auto found=app->windows.find(id);if(found==app->windows.end())return NEOASTRA_ERROR_INVALID_ARGUMENT;found->second->retain();*output=found->second;return NEOASTRA_OK;}

uint64_t NEOASTRA_CALL neoastra_window_get_id(const neoastra_window_t* w){return w?w->id:0;}
neoastra_result_t NEOASTRA_CALL neoastra_window_get_bounds(const neoastra_window_t* w,neoastra_rect_t* value){if(!w||!value)return NEOASTRA_ERROR_INVALID_ARGUMENT;std::lock_guard lock(const_cast<neoastra_window_t*>(w)->state_mutex);*value=w->bounds;return NEOASTRA_OK;}
neoastra_result_t NEOASTRA_CALL neoastra_window_set_bounds(neoastra_window_t* w,neoastra_rect_t value){if(!w)return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEOASTRA_ERROR_WRONG_THREAD;if(value.width<=0||value.height<=0)return NEOASTRA_ERROR_INVALID_ARGUMENT;{std::lock_guard lock(w->state_mutex);w->bounds=value;}return neo_platform_window_set_bounds(w);}
neoastra_result_t NEOASTRA_CALL neoastra_window_get_minimum_size(const neoastra_window_t* w,neoastra_size_t* value){if(!w||!value)return NEOASTRA_ERROR_INVALID_ARGUMENT;std::lock_guard lock(const_cast<neoastra_window_t*>(w)->state_mutex);*value=w->minimum_size;return NEOASTRA_OK;}
neoastra_result_t NEOASTRA_CALL neoastra_window_set_minimum_size(neoastra_window_t* w,neoastra_size_t value){if(!w)return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEOASTRA_ERROR_WRONG_THREAD;if(value.width<0||value.height<0)return NEOASTRA_ERROR_INVALID_ARGUMENT;neoastra_size_t previous{};{std::lock_guard lock(w->state_mutex);if((w->maximum_size.width>0&&value.width>w->maximum_size.width)||(w->maximum_size.height>0&&value.height>w->maximum_size.height))return NEOASTRA_ERROR_INVALID_ARGUMENT;previous=w->minimum_size;w->minimum_size=value;}const auto result=neo_platform_window_set_size_constraints(w);if(result!=NEOASTRA_OK){std::lock_guard lock(w->state_mutex);w->minimum_size=previous;}return result;}
neoastra_result_t NEOASTRA_CALL neoastra_window_get_maximum_size(const neoastra_window_t* w,neoastra_size_t* value){if(!w||!value)return NEOASTRA_ERROR_INVALID_ARGUMENT;std::lock_guard lock(const_cast<neoastra_window_t*>(w)->state_mutex);*value=w->maximum_size;return NEOASTRA_OK;}
neoastra_result_t NEOASTRA_CALL neoastra_window_set_maximum_size(neoastra_window_t* w,neoastra_size_t value){if(!w)return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEOASTRA_ERROR_WRONG_THREAD;if(value.width<0||value.height<0)return NEOASTRA_ERROR_INVALID_ARGUMENT;neoastra_size_t previous{};{std::lock_guard lock(w->state_mutex);if((value.width>0&&w->minimum_size.width>value.width)||(value.height>0&&w->minimum_size.height>value.height))return NEOASTRA_ERROR_INVALID_ARGUMENT;previous=w->maximum_size;w->maximum_size=value;}const auto result=neo_platform_window_set_size_constraints(w);if(result!=NEOASTRA_OK){std::lock_guard lock(w->state_mutex);w->maximum_size=previous;}return result;}
neoastra_result_t NEOASTRA_CALL neoastra_window_get_state(const neoastra_window_t* w,neoastra_window_state_t* value){if(!w||!value)return NEOASTRA_ERROR_INVALID_ARGUMENT;std::lock_guard lock(const_cast<neoastra_window_t*>(w)->state_mutex);*value=w->state;return NEOASTRA_OK;}
neoastra_result_t NEOASTRA_CALL neoastra_window_set_state(neoastra_window_t* w,neoastra_window_state_t value){if(!w||value>NEOASTRA_WINDOW_FULLSCREEN)return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEOASTRA_ERROR_WRONG_THREAD;neoastra_window_state_t previous{};{std::lock_guard lock(w->state_mutex);previous=w->state;w->state=value;}const auto result=neo_platform_window_set_state(w);if(result!=NEOASTRA_OK){std::lock_guard lock(w->state_mutex);w->state=previous;}return result;}
neoastra_string_view_t NEOASTRA_CALL neoastra_window_get_title(const neoastra_window_t* w){if(!w)return {};std::lock_guard lock(const_cast<neoastra_window_t*>(w)->state_mutex);return neo_string_view(w->title);}
neoastra_result_t NEOASTRA_CALL neoastra_window_set_title(neoastra_window_t* w,neoastra_string_view_t value){if(!w||!neo_valid_utf8(value))return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEOASTRA_ERROR_WRONG_THREAD;try{{std::lock_guard lock(w->state_mutex);w->title=neo_string(value);}return neo_platform_window_set_title(w);}catch(...){return NEOASTRA_ERROR_INVALID_ARGUMENT;}}
neoastra_result_t NEOASTRA_CALL neoastra_window_show(neoastra_window_t* w){return !w?NEOASTRA_ERROR_INVALID_ARGUMENT:!check_ui(w->app)?NEOASTRA_ERROR_WRONG_THREAD:neo_platform_window_show(w,true);}
neoastra_result_t NEOASTRA_CALL neoastra_window_hide(neoastra_window_t* w){return !w?NEOASTRA_ERROR_INVALID_ARGUMENT:!check_ui(w->app)?NEOASTRA_ERROR_WRONG_THREAD:neo_platform_window_show(w,false);}
neoastra_result_t NEOASTRA_CALL neoastra_window_activate(neoastra_window_t* w){return !w?NEOASTRA_ERROR_INVALID_ARGUMENT:!check_ui(w->app)?NEOASTRA_ERROR_WRONG_THREAD:neo_platform_window_activate(w);}
neoastra_result_t NEOASTRA_CALL neoastra_window_close(neoastra_window_t* w){return !w?NEOASTRA_ERROR_INVALID_ARGUMENT:neo_platform_window_close(w);}
neoastra_result_t NEOASTRA_CALL neoastra_window_get_native_handle(neoastra_window_t* w,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* h){if(!w||!valid_struct(h,h?h->size:0,sizeof(*h)))return NEOASTRA_ERROR_INVALID_ARGUMENT;return neo_platform_window_get_handle(w,kind,h);}

neoastra_result_t NEOASTRA_CALL neoastra_environment_create_async(neoastra_app_t* app,const neoastra_environment_options_t* options,neoastra_environment_created_callback_t callback,void* context,neoastra_operation_t** outop,neoastra_error_t** error){
    if(outop)*outop=nullptr;if(!app||!callback||!valid_struct(options,options?options->size:0,sizeof(*options))||!neo_valid_utf8(options->user_data_root)||!neo_valid_utf8(options->browser_runtime_path)||!neo_valid_utf8(options->browser_arguments)||!neo_valid_utf8(options->preferred_languages)||!valid_custom_schemes(options))return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid environment arguments");
    if(!check_ui(app))return neo_fail(error,NEOASTRA_ERROR_WRONG_THREAD,"environment creation must begin on the UI thread");
    if(!accepts_ui_objects(app))return neo_fail(error,NEOASTRA_ERROR_DISPOSED,"application shutdown has begun");
    neoastra_operation_t* op{};
    neoastra_environment_t* value{};
    try {
        op=make_operation(outop);
        value=new neoastra_environment(app);
        copy_custom_schemes(value,options);
        auto* state=new environment_completion{callback,context,op,value,nullptr};
        // Provider ownership transfers only once every potentially throwing allocation has succeeded.
        for(uint32_t index=0;index<options->custom_scheme_count;++index)value->custom_schemes[index].release_provider_context=custom_scheme_at(options,index).release_resource_provider_context;
        neoastra_error_t* start_error=nullptr;
        if(!neo_platform_environment_create_async(value,options,platform_environment_created,state,&start_error))platform_environment_created(state,start_error);
        return NEOASTRA_OK;
    } catch(const std::exception& ex) {
        if(value)value->release();
        if(op)op->release();
        if(outop&&*outop){(*outop)->release();*outop=nullptr;}
        return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,ex.what());
    }
}
neoastra_result_t NEOASTRA_CALL neoastra_environment_create_profile_async(neoastra_environment_t* env,const neoastra_profile_options_t* options,neoastra_profile_created_callback_t callback,void* context,neoastra_operation_t** outop,neoastra_error_t** error){
    if(outop)*outop=nullptr;if(!env||!callback||!valid_struct(options,options?options->size:0,sizeof(*options))||!neo_valid_utf8(options->name))return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid profile arguments");if(!check_ui(env->app))return neo_fail(error,NEOASTRA_ERROR_WRONG_THREAD,"profile creation must begin on the UI thread");if(!accepts_ui_objects(env->app))return neo_fail(error,NEOASTRA_ERROR_DISPOSED,"application shutdown has begun");
    try{auto* op=make_operation(outop);auto* value=new neoastra_profile(env);value->name=neo_string(options->name);value->ephemeral=options->ephemeral!=0;if(!neo_platform_profile_create(value,error)){value->release();op->release();if(outop&&*outop){(*outop)->release();*outop=nullptr;}return error&&*error?(*error)->code:NEOASTRA_ERROR_NATIVE_FAILURE;}auto* state=new profile_completion{callback,context,op,value};auto r=schedule(env->app,complete_profile,state,error,"could not schedule profile completion");if(r!=NEOASTRA_OK){value->release();op->release();delete state;}return r;}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,ex.what());}}
neoastra_result_t NEOASTRA_CALL neoastra_environment_create_view_async(neoastra_environment_t* env,const neoastra_view_options_t* options,neoastra_view_created_callback_t callback,void* context,neoastra_operation_t** outop,neoastra_error_t** error){
    if(outop)*outop=nullptr;if(!env||!callback||!valid_struct(options,options?options->size:0,sizeof(*options))||!valid_native_parent(options->parent)||options->decision_timeout_ms>600000||options->bridge_policy>NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW||((options->bridge_origin_count!=0)!=(options->bridge_origins!=nullptr))||(options->bridge_policy==NEOASTRA_BRIDGE_DISABLED&&options->bridge_origin_count!=0)||(options->bridge_policy==NEOASTRA_BRIDGE_TRUSTED_ORIGINS&&options->bridge_origin_count==0)||(options->bridge_policy==NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW&&options->bridge_origin_count!=0))return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid view arguments");for(uint32_t index=0;index<options->bridge_origin_count;++index)if(!neo_valid_utf8(options->bridge_origins[index])||!valid_origin(neo_string(options->bridge_origins[index])))return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid bridge origin");if(!check_ui(env->app))return neo_fail(error,NEOASTRA_ERROR_WRONG_THREAD,"view creation must begin on the UI thread");if(!accepts_ui_objects(env->app))return neo_fail(error,NEOASTRA_ERROR_DISPOSED,"application shutdown has begun");
    if(options->profile&&options->profile->environment!=env)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"view profile belongs to a different environment");
    if(options->popup_request){auto* request=options->popup_request;if(request->kind!=NEOASTRA_DECISION_NEW_WINDOW||request->owner==nullptr||request->owner->environment!=env||options->profile!=request->owner->profile||request->state.load(std::memory_order_acquire)>neo_decision_state::deferred)return neo_fail(error,NEOASTRA_ERROR_INVALID_STATE,"popup request is no longer valid or opener-compatible");if(request->popup_creation_started.exchange(true,std::memory_order_acq_rel))return neo_fail(error,NEOASTRA_ERROR_INVALID_STATE,"popup target creation has already started");}
    try{auto* op=make_operation(outop);auto* value=new neoastra_view(env);value->profile=options->profile;if(value->profile)value->profile->retain();value->window=options->window;if(value->window){value->window->retain();value->window->views.push_back(value);}value->parent=options->parent;value->bounds=options->bounds;value->fill_parent=options->fill_parent!=0;if(options->maximum_message_size)value->maximum_message_size=options->maximum_message_size;value->bridge_policy=options->bridge_policy;if(options->decision_timeout_ms)value->decision_timeout=std::chrono::milliseconds(options->decision_timeout_ms);value->bridge_origins.reserve(options->bridge_origin_count);for(uint32_t index=0;index<options->bridge_origin_count;++index)value->bridge_origins.push_back(neo_string(options->bridge_origins[index]));auto* state=new view_completion{callback,context,op,value,nullptr,options->popup_request!=nullptr};neoastra_error_t* start_error=nullptr;if(!neo_platform_view_create_async(value,options,platform_view_created,state,&start_error))platform_view_created(state,start_error);return NEOASTRA_OK;}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,ex.what());}}
neoastra_result_t NEOASTRA_CALL neoastra_environment_get_capability(const neoastra_environment_t* env,neoastra_capability_t capability,neoastra_capability_info_t* info){
    if(!env||capability<NEOASTRA_CAPABILITY_CUSTOM_SCHEME||capability>NEOASTRA_CAPABILITY_FULLSCREEN_DECISIONS||!valid_struct(info,info?info->size:0,sizeof(*info)))return NEOASTRA_ERROR_INVALID_ARGUMENT;
    static const std::string available="Implemented by the active NeoAstra backend";
    static const std::string unavailable="Not exposed by the current portable implementation";
#if defined(__linux__)
    static const std::string linux_custom_scheme="WebKitGTK supports synchronous byte/file responses, secure and CORS scheme flags, and request bodies up to 64 MiB; authority, allowed-origin, service-worker, initiator, frame and resource-kind semantics and application-scheme bridge trust are unavailable";
    static const std::string linux_message_origin="WebKitGTK 4.1 script messages do not expose trustworthy source-origin data";
#endif
    info->support=NEOASTRA_SUPPORT_NONE;info->capability_version=1;info->flags=0;
    switch(capability){
        case NEOASTRA_CAPABILITY_SCRIPT_DOCUMENT_START:
        case NEOASTRA_CAPABILITY_SCRIPT_ALL_FRAMES:
        case NEOASTRA_CAPABILITY_COOKIES:
        case NEOASTRA_CAPABILITY_PROFILE_EPHEMERAL:
        case NEOASTRA_CAPABILITY_ZOOM:
        case NEOASTRA_CAPABILITY_DOWNLOADS:
        case NEOASTRA_CAPABILITY_TRACKED_POPUPS:
        case NEOASTRA_CAPABILITY_SCRIPT_DIALOGS:
        case NEOASTRA_CAPABILITY_HTTP_AUTHENTICATION:
            info->support=NEOASTRA_SUPPORT_NATIVE;break;
#if defined(_WIN32)
        case NEOASTRA_CAPABILITY_CUSTOM_SCHEME:
        case NEOASTRA_CAPABILITY_MESSAGE_ORIGIN:
        case NEOASTRA_CAPABILITY_PERMISSIONS:
        case NEOASTRA_CAPABILITY_PERMISSION_PERSISTENCE:
        case NEOASTRA_CAPABILITY_DOWNLOAD_PAUSE:
            info->support=NEOASTRA_SUPPORT_NATIVE;break;
#elif defined(__APPLE__)
        case NEOASTRA_CAPABILITY_CUSTOM_SCHEME:
            info->support=NEOASTRA_SUPPORT_LIMITED;break;
        case NEOASTRA_CAPABILITY_MESSAGE_ORIGIN:
            info->support=NEOASTRA_SUPPORT_NATIVE;break;
        case NEOASTRA_CAPABILITY_PERMISSIONS:
            info->support=NEOASTRA_SUPPORT_LIMITED;break;
#else
        case NEOASTRA_CAPABILITY_CUSTOM_SCHEME:
        case NEOASTRA_CAPABILITY_PERMISSIONS:
            info->support=NEOASTRA_SUPPORT_LIMITED;break;
#endif
#if !defined(_WIN32)
        case NEOASTRA_CAPABILITY_SCRIPT_DOCUMENT_END:
        case NEOASTRA_CAPABILITY_FILE_CHOOSER:
            info->support=NEOASTRA_SUPPORT_NATIVE;break;
#endif
#if defined(_WIN32)
        case NEOASTRA_CAPABILITY_CLIENT_CERTIFICATES:
        case NEOASTRA_CAPABILITY_TLS_ERROR_DECISIONS:
            info->support=NEOASTRA_SUPPORT_NATIVE;break;
#elif defined(__APPLE__)
        case NEOASTRA_CAPABILITY_TLS_ERROR_DECISIONS:
            info->support=NEOASTRA_SUPPORT_NATIVE;break;
#endif
#if defined(_WIN32) || defined(__linux__)
        case NEOASTRA_CAPABILITY_FULLSCREEN_DECISIONS:
            info->support=NEOASTRA_SUPPORT_NATIVE;break;
#endif
#if defined(_WIN32)
        case NEOASTRA_CAPABILITY_PROFILE_NAMED:
        case NEOASTRA_CAPABILITY_CLEAR_DATA_BY_TIME:
            info->support=NEOASTRA_SUPPORT_NATIVE;break;
#else
        case NEOASTRA_CAPABILITY_CLEAR_DATA_BY_TIME:
            info->support=NEOASTRA_SUPPORT_LIMITED;break;
#endif
        default:break;
    }
#if defined(__linux__)
    if(capability==NEOASTRA_CAPABILITY_CUSTOM_SCHEME)info->details=neo_string_view(linux_custom_scheme);
    else if(capability==NEOASTRA_CAPABILITY_MESSAGE_ORIGIN)info->details=neo_string_view(linux_message_origin);
    else info->details=neo_string_view(info->support==NEOASTRA_SUPPORT_NONE?unavailable:available);
#else
    info->details=neo_string_view(info->support==NEOASTRA_SUPPORT_NONE?unavailable:available);
#endif
    return NEOASTRA_OK;
}

neoastra_result_t NEOASTRA_CALL neoastra_profile_get_cookies_async(neoastra_profile_t* p,neoastra_string_view_t uri,neoastra_buffer_callback_t cb,void* ctx,neoastra_operation_t** outop,neoastra_error_t** error){if(outop)*outop=nullptr;if(!p||!cb||!neo_valid_utf8(uri))return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid cookie arguments");if(!check_ui(p->environment->app))return neo_fail(error,NEOASTRA_ERROR_WRONG_THREAD,"cookie operations must begin on the UI thread");try{auto native_uri=neo_string(uri);auto* op=make_operation(outop);auto result=neo_platform_profile_get_cookies(p,native_uri,cb,ctx,op,error);if(result!=NEOASTRA_OK){op->release();if(outop&&*outop){(*outop)->release();*outop=nullptr;}}return result;}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
static bool valid_cookie(const neoastra_cookie_t* cookie) noexcept {return valid_struct(cookie,cookie?cookie->size:0,sizeof(*cookie))&&neo_valid_utf8(cookie->name)&&neo_valid_utf8(cookie->value)&&neo_valid_utf8(cookie->domain)&&neo_valid_utf8(cookie->path)&&cookie->name.length>0&&cookie->domain.length>0&&(cookie->flags&~7u)==0&&cookie->same_site<=3;}
neoastra_result_t NEOASTRA_CALL neoastra_profile_set_cookie_async(neoastra_profile_t* p,const neoastra_cookie_t* cookie,neoastra_completion_callback_t cb,void* ctx,neoastra_operation_t** op,neoastra_error_t** e){if(!valid_cookie(cookie))return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid cookie");return start_profile_operation(p,cb,op,e,[&](auto* operation){return neo_platform_profile_set_cookie(p,cookie,cb,ctx,operation,e);});}
neoastra_result_t NEOASTRA_CALL neoastra_profile_delete_cookie_async(neoastra_profile_t* p,const neoastra_cookie_t* cookie,neoastra_completion_callback_t cb,void* ctx,neoastra_operation_t** op,neoastra_error_t** e){if(!valid_cookie(cookie))return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid cookie");return start_profile_operation(p,cb,op,e,[&](auto* operation){return neo_platform_profile_delete_cookie(p,cookie,cb,ctx,operation,e);});}
neoastra_result_t NEOASTRA_CALL neoastra_profile_clear_data_async(neoastra_profile_t* p,neoastra_data_kind_t kinds,int64_t start,int64_t end,neoastra_completion_callback_t cb,void* ctx,neoastra_operation_t** op,neoastra_error_t** e){constexpr auto known=NEOASTRA_DATA_COOKIES|NEOASTRA_DATA_CACHE|NEOASTRA_DATA_LOCAL_STORAGE|NEOASTRA_DATA_INDEXED_DB|NEOASTRA_DATA_SERVICE_WORKERS|NEOASTRA_DATA_PERMISSIONS|NEOASTRA_DATA_DOWNLOAD_HISTORY;if(kinds==0||(kinds!=NEOASTRA_DATA_ALL&&(kinds&~known)!=0)||start>end)return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid data range");return start_profile_operation(p,cb,op,e,[&](auto* operation){return neo_platform_profile_clear_data(p,kinds,start,end,cb,ctx,operation,e);});}

neoastra_result_t NEOASTRA_CALL neoastra_view_set_event_callback(neoastra_view_t* v,neoastra_event_callback_t cb,void* ctx){if(!v)return NEOASTRA_ERROR_INVALID_ARGUMENT;v->events.set(cb,ctx);return NEOASTRA_OK;}
neoastra_result_t NEOASTRA_CALL neoastra_view_set_bounds(neoastra_view_t* v,neoastra_rect_t bounds,uint32_t fill){if(!v)return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(v->environment->app))return NEOASTRA_ERROR_WRONG_THREAD;v->bounds=bounds;v->fill_parent=fill!=0;return neo_platform_view_set_bounds(v);}
neoastra_result_t NEOASTRA_CALL neoastra_view_navigate(neoastra_view_t* v,neoastra_string_view_t uri,neoastra_error_t** e){if(!v||!neo_valid_utf8(uri))return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid view or URI");if(!check_ui(v->environment->app))return neo_fail(e,NEOASTRA_ERROR_WRONG_THREAD,"navigation must run on the UI thread");try{v->source=neo_string(uri);return neo_platform_view_navigate(v,v->source,e);}catch(...){return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid URI");}}
neoastra_result_t NEOASTRA_CALL neoastra_view_navigate_request(neoastra_view_t* v,neoastra_string_view_t uri,neoastra_string_view_t method,neoastra_string_view_t headers,const uint8_t* body,uint64_t length,neoastra_error_t** e){if(!v||!neo_valid_utf8(uri)||!neo_valid_utf8(method)||!neo_valid_utf8(headers)||(length&&!body))return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid navigation request");if(!check_ui(v->environment->app))return neo_fail(e,NEOASTRA_ERROR_WRONG_THREAD,"navigation must run on the UI thread");try{v->source=neo_string(uri);return neo_platform_view_navigate_request(v,v->source,neo_string(method),neo_string(headers),body,length,e);}catch(...){return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid navigation request");}}
neoastra_result_t NEOASTRA_CALL neoastra_view_load_html(neoastra_view_t* v,neoastra_string_view_t html,neoastra_string_view_t base,neoastra_error_t** e){if(!v||!neo_valid_utf8(html)||!neo_valid_utf8(base))return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid HTML arguments");if(!check_ui(v->environment->app))return neo_fail(e,NEOASTRA_ERROR_WRONG_THREAD,"HTML loading must run on the UI thread");try{return neo_platform_view_load_html(v,neo_string(html),neo_string(base),e);}catch(...){return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid HTML");}}
neoastra_result_t NEOASTRA_CALL neoastra_view_stop(neoastra_view_t* v){return !v?NEOASTRA_ERROR_INVALID_ARGUMENT:!check_ui(v->environment->app)?NEOASTRA_ERROR_WRONG_THREAD:neo_platform_view_command(v,0);}
neoastra_result_t NEOASTRA_CALL neoastra_view_reload(neoastra_view_t* v,uint32_t ignore){return !v?NEOASTRA_ERROR_INVALID_ARGUMENT:!check_ui(v->environment->app)?NEOASTRA_ERROR_WRONG_THREAD:neo_platform_view_command(v,ignore?2:1);}
neoastra_result_t NEOASTRA_CALL neoastra_view_go_back(neoastra_view_t* v){return !v?NEOASTRA_ERROR_INVALID_ARGUMENT:!check_ui(v->environment->app)?NEOASTRA_ERROR_WRONG_THREAD:neo_platform_view_command(v,3);}
neoastra_result_t NEOASTRA_CALL neoastra_view_go_forward(neoastra_view_t* v){return !v?NEOASTRA_ERROR_INVALID_ARGUMENT:!check_ui(v->environment->app)?NEOASTRA_ERROR_WRONG_THREAD:neo_platform_view_command(v,4);}
neoastra_result_t NEOASTRA_CALL neoastra_view_evaluate_script_async(neoastra_view_t* v,neoastra_string_view_t script,neoastra_string_callback_t cb,void* ctx,neoastra_operation_t** outop,neoastra_error_t** e){if(outop)*outop=nullptr;if(!v||!cb||!neo_valid_utf8(script))return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid script arguments");if(!check_ui(v->environment->app))return neo_fail(e,NEOASTRA_ERROR_WRONG_THREAD,"script evaluation must begin on the UI thread");try{auto* op=make_operation(outop);auto r=neo_platform_view_evaluate(v,neo_string(script),cb,ctx,op,e);if(r!=NEOASTRA_OK)op->release();return r;}catch(...){return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid script");}}
neoastra_result_t NEOASTRA_CALL neoastra_view_add_script_async(neoastra_view_t* v,neoastra_string_view_t script,const neoastra_script_options_t* options,neoastra_string_callback_t cb,void* ctx,neoastra_operation_t** outop,neoastra_error_t** e){if(outop)*outop=nullptr;if(!v||!cb||!neo_valid_utf8(script)||!valid_struct(options,options?options->size:0,sizeof(*options))||!neo_valid_utf8(options->world_name))return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid persistent script arguments");if(!check_ui(v->environment->app))return neo_fail(e,NEOASTRA_ERROR_WRONG_THREAD,"script injection must begin on the UI thread");try{auto* op=make_operation(outop);auto r=neo_platform_view_add_script(v,neo_string(script),options,cb,ctx,op,e);if(r!=NEOASTRA_OK)op->release();return r;}catch(...){return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid persistent script");}}
neoastra_result_t NEOASTRA_CALL neoastra_view_remove_script(neoastra_view_t* v,neoastra_string_view_t identifier){if(!v||!neo_valid_utf8(identifier))return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(v->environment->app))return NEOASTRA_ERROR_WRONG_THREAD;try{return neo_platform_view_remove_script(v,neo_string(identifier));}catch(...){return NEOASTRA_ERROR_INVALID_ARGUMENT;}}
neoastra_result_t NEOASTRA_CALL neoastra_view_post_message(neoastra_view_t* v,neoastra_string_view_t msg,uint32_t json,neoastra_error_t** e){if(!v||!neo_valid_utf8(msg))return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid message");if(!check_ui(v->environment->app))return neo_fail(e,NEOASTRA_ERROR_WRONG_THREAD,"message posting must run on the UI thread");if(!neo_bridge_access_allowed(v,v->source))return neo_fail(e,NEOASTRA_ERROR_SECURITY,"Web messaging is blocked by the configured bridge policy",0,"bridge");try{return neo_platform_view_post_message(v,neo_string(msg),json!=0,e);}catch(...){return neo_fail(e,NEOASTRA_ERROR_INVALID_ARGUMENT,"invalid message");}}
neoastra_result_t NEOASTRA_CALL neoastra_view_get_zoom_factor(const neoastra_view_t* v,double* factor){if(!v||!factor)return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(v->environment->app))return NEOASTRA_ERROR_WRONG_THREAD;return neo_platform_view_get_zoom_factor(v,factor);}
neoastra_result_t NEOASTRA_CALL neoastra_view_set_zoom_factor(neoastra_view_t* v,double factor){if(!v||!std::isfinite(factor)||factor<0.25||factor>5.0)return NEOASTRA_ERROR_INVALID_ARGUMENT;if(!check_ui(v->environment->app))return NEOASTRA_ERROR_WRONG_THREAD;return neo_platform_view_set_zoom_factor(v,factor);}
neoastra_result_t NEOASTRA_CALL neoastra_view_get_native_handle(neoastra_view_t* v,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* h){if(!v||!valid_struct(h,h?h->size:0,sizeof(*h)))return NEOASTRA_ERROR_INVALID_ARGUMENT;return neo_platform_view_get_handle(v,kind,h);}
neoastra_result_t NEOASTRA_CALL neoastra_query_extension(const void*,neoastra_string_view_t name,uint32_t,const void** table){if(table)*table=nullptr;if(!table||!neo_valid_utf8(name))return NEOASTRA_ERROR_INVALID_ARGUMENT;return NEOASTRA_ERROR_NOT_SUPPORTED;}

} // extern C
