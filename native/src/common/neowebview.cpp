#include "native_internal.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <new>
#include <stdexcept>

neo_webview_app::~neo_webview_app() {
    events.clear();
    logs.clear();
    neo_platform_shutdown(this);
}
neo_webview_environment::~neo_webview_environment() { neo_platform_environment_destroy(this); app->release(); }
neo_webview_profile::~neo_webview_profile() { neo_platform_profile_destroy(this); environment->release(); }
neo_webview_window::~neo_webview_window() {
    neo_platform_window_destroy(this);
    if (owner) owner->release();
    app->release();
}
neo_webview_view::~neo_webview_view() {
    events.clear();
    neo_platform_view_destroy(this);
    if (window) {
        auto& views = window->views;
        views.erase(std::remove(views.begin(), views.end(), this), views.end());
    }
    if (profile) profile->release();
    if (window) window->release();
    environment->release();
}

bool neo_valid_utf8(neo_webview_string_view_t text) noexcept {
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

std::string neo_string(neo_webview_string_view_t text) {
    if (!neo_valid_utf8(text)) throw std::invalid_argument("The string is not valid UTF-8");
    if (text.length == 0) return {};
    return {reinterpret_cast<const char*>(text.data), static_cast<size_t>(text.length)};
}

neo_webview_result_t neo_fail(neo_webview_error_t** output, neo_webview_result_t code, std::string message, int64_t native_code, std::string domain) noexcept {
    if (output) {
        *output = nullptr;
        try { *output = new neo_webview_error(code, native_code, std::move(domain), std::move(message)); }
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

bool valid_shutdown_mode(neo_webview_app_shutdown_mode_t value) noexcept {
    return value >= NEO_WEBVIEW_APP_SHUTDOWN_EXPLICIT && value <= NEO_WEBVIEW_APP_SHUTDOWN_ON_MAIN_WINDOW_CLOSED;
}

bool valid_decision_action(neo_webview_decision_action_t value) noexcept {
    return value >= NEO_WEBVIEW_DECISION_DEFAULT && value <= NEO_WEBVIEW_DECISION_DOWNLOAD;
}

bool check_ui(const neo_webview_app_t* app) noexcept {
    return app && app->ui_thread == std::this_thread::get_id();
}

void initialize_event(neo_webview_app_t* app, neo_webview_event_t& event, neo_webview_event_type_t type, uint64_t object_id,
                      const std::string* text, const std::string* uri, uint64_t value, int64_t native_code,
                      neo_webview_decision_t* decision) noexcept {
    event.header = {sizeof(event), 1, type, app ? app->next_sequence.fetch_add(1, std::memory_order_relaxed) : 0, neo_timestamp_ns()};
    event.object_id = object_id;
    if (text) event.text = neo_string_view(*text);
    if (uri) event.uri = neo_string_view(*uri);
    event.value = value;
    event.native_code = native_code;
    event.decision = decision;
}

struct environment_completion {
    neo_webview_environment_created_callback_t callback{}; void* context{}; neo_webview_operation_t* operation{};
    neo_webview_environment_t* value{}; neo_webview_error_t* error{};
};
void NEO_WEBVIEW_CALL complete_environment(void* pointer) {
    auto* state = static_cast<environment_completion*>(pointer);
    neo_webview_result_t result{};
    const auto requested = state->error ? state->error->code : NEO_WEBVIEW_OK;
    if (state->operation->try_complete(requested, result)) {
        state->callback(state->context, result, result == NEO_WEBVIEW_OK ? state->value : nullptr, state->error);
        if (result != NEO_WEBVIEW_OK && state->value) state->value->release();
    } else if (state->value) state->value->release();
    if (state->error) state->error->release();
    state->operation->release();
    delete state;
}
void platform_environment_created(void* pointer, neo_webview_error_t* error) noexcept {
    auto* state = static_cast<environment_completion*>(pointer);
    state->error = error;
    if (neo_webview_app_dispatch(state->value->app, complete_environment, state) != NEO_WEBVIEW_OK) {
        neo_webview_result_t ignored{};
        state->operation->try_complete(NEO_WEBVIEW_ERROR_CANCELED, ignored);
        state->value->release();
        if (state->error) state->error->release();
        state->operation->release();
        delete state;
    }
}

struct profile_completion {
    neo_webview_profile_created_callback_t callback{}; void* context{}; neo_webview_operation_t* operation{}; neo_webview_profile_t* value{};
};
void NEO_WEBVIEW_CALL complete_profile(void* pointer) {
    auto* state = static_cast<profile_completion*>(pointer);
    neo_webview_result_t result{};
    if (state->operation->try_complete(NEO_WEBVIEW_OK, result)) {
        state->callback(state->context, result, result == NEO_WEBVIEW_OK ? state->value : nullptr, nullptr);
        if (result != NEO_WEBVIEW_OK) state->value->release();
    } else state->value->release();
    state->operation->release();
    delete state;
}

struct view_completion {
    neo_webview_view_created_callback_t callback{}; void* context{}; neo_webview_operation_t* operation{};
    neo_webview_view_t* value{}; neo_webview_error_t* error{};
};
void NEO_WEBVIEW_CALL complete_view(void* pointer) {
    auto* state = static_cast<view_completion*>(pointer);
    neo_webview_result_t result{};
    const auto requested = state->error ? state->error->code : NEO_WEBVIEW_OK;
    if (state->operation->try_complete(requested, result)) {
        state->callback(state->context, result, result == NEO_WEBVIEW_OK ? state->value : nullptr, state->error);
        if (result != NEO_WEBVIEW_OK && state->value) state->value->release();
    } else if (state->value) state->value->release();
    if (state->error) state->error->release();
    state->operation->release();
    delete state;
}
void platform_view_created(void* pointer, neo_webview_error_t* error) noexcept {
    auto* state = static_cast<view_completion*>(pointer);
    state->error = error;
    if (neo_webview_app_dispatch(state->value->environment->app, complete_view, state) != NEO_WEBVIEW_OK) {
        neo_webview_result_t ignored{};
        state->operation->try_complete(NEO_WEBVIEW_ERROR_CANCELED, ignored);
        state->value->release();
        if (state->error) state->error->release();
        state->operation->release();
        delete state;
    }
}

neo_webview_operation_t* make_operation(neo_webview_operation_t** output) {
    auto* operation = new neo_webview_operation;
    if (output) { operation->retain(); *output = operation; }
    return operation;
}

neo_webview_result_t schedule(neo_webview_app_t* app, neo_webview_dispatch_callback_t callback, void* context,
                              neo_webview_error_t** error, const char* message) noexcept {
    const auto result = neo_webview_app_dispatch(app, callback, context);
    return result == NEO_WEBVIEW_OK ? result : neo_fail(error, result, message);
}

template<class TStarter>
neo_webview_result_t start_profile_operation(neo_webview_profile_t* profile, neo_webview_completion_callback_t callback,
                                             neo_webview_operation_t** output, neo_webview_error_t** error,
                                             TStarter&& starter) {
    if (output) *output = nullptr;
    if (!profile || !callback) return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, "invalid profile operation");
    if (!check_ui(profile->environment->app)) return neo_fail(error, NEO_WEBVIEW_ERROR_WRONG_THREAD, "profile operations must begin on the UI thread");
    neo_webview_operation_t* operation{};
    try {
        operation = make_operation(output);
        const auto result = starter(operation);
        if (result != NEO_WEBVIEW_OK) {
            operation->release();
            if (output && *output) { (*output)->release(); *output = nullptr; }
        }
        return result;
    } catch (const std::exception& exception) {
        if (operation) operation->release();
        if (output && *output) { (*output)->release(); *output = nullptr; }
        return neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, exception.what());
    } catch (...) {
        if (operation) operation->release();
        if (output && *output) { (*output)->release(); *output = nullptr; }
        return neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "profile operation failed");
    }
}

} // namespace

void neo_emit_app(neo_webview_app_t* app, neo_webview_event_type_t type, uint64_t object_id, const std::string* text,
                  const std::string* uri, uint64_t value, int64_t native_code, neo_webview_decision_t* decision) noexcept {
    if (!app) return;
    neo_webview_event_t event{};
    initialize_event(app, event, type, object_id, text, uri, value, native_code, decision);
    app->events.invoke([&](auto callback, void* context) { callback(context, &event); });
}

void neo_emit_view(neo_webview_view_t* view, neo_webview_event_type_t type, uint64_t object_id, const std::string* text,
                   const std::string* uri, uint64_t value, int64_t native_code, neo_webview_decision_t* decision) noexcept {
    if (!view) return;
    neo_webview_event_t event{};
    initialize_event(view->environment->app, event, type, object_id, text, uri, value, native_code, decision);
    view->events.invoke([&](auto callback, void* context) { callback(context, &event); });
}

void neo_drain_dispatch(neo_webview_app_t* app) noexcept {
    if (!app || !check_ui(app)) return;
    for (;;) {
        neo_dispatch_item item{};
        {
            std::lock_guard lock(app->dispatch_mutex);
            if (app->dispatches.empty()) break;
            item = app->dispatches.front();
            app->dispatches.pop_front();
        }
        try { item.callback(item.context); } catch (...) { }
    }
    if (app->quit_requested.load(std::memory_order_acquire)) {
        app->stopping.store(true, std::memory_order_release);
        app->state.store(neo_app_state::stopping, std::memory_order_release);
    }
}

void neo_window_closed(neo_webview_window_t* window) noexcept {
    if (!window || window->closed.exchange(true, std::memory_order_acq_rel)) return;
    auto* app = window->app;
    bool should_quit{};
    {
        std::lock_guard lock(app->windows_mutex);
        auto found = app->windows.find(window->id);
        if (found != app->windows.end()) {
            app->windows.erase(found);
            should_quit = (app->shutdown_mode == NEO_WEBVIEW_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED && app->windows.empty()) ||
                          (app->shutdown_mode == NEO_WEBVIEW_APP_SHUTDOWN_ON_MAIN_WINDOW_CLOSED && app->main_window_id == window->id);
        }
    }
    neo_emit_app(app, NEO_WEBVIEW_EVENT_WINDOW_CLOSED, window->id);
    if (should_quit) neo_webview_app_quit(app, 0);
}

extern "C" {

uint32_t NEO_WEBVIEW_CALL neo_webview_get_abi_version_major() { return NEO_WEBVIEW_ABI_VERSION_MAJOR; }
uint32_t NEO_WEBVIEW_CALL neo_webview_get_abi_version_minor() { return NEO_WEBVIEW_ABI_VERSION_MINOR; }
neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_get_version() { static const std::string value = "0.1.0"; return neo_string_view(value); }

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_get_runtime_info(neo_webview_runtime_info_t* info, neo_webview_error_t** error) {
    if (!valid_struct(info, info ? info->size : 0, sizeof(neo_webview_runtime_info_t))) return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, "runtime info has an invalid size or version");
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
    return NEO_WEBVIEW_OK;
}

#define NEO_LIFETIME(name) void NEO_WEBVIEW_CALL neo_webview_##name##_retain(neo_webview_##name##_t* value){retain(value);} void NEO_WEBVIEW_CALL neo_webview_##name##_release(neo_webview_##name##_t* value){release(value);}
NEO_LIFETIME(app) NEO_LIFETIME(environment) NEO_LIFETIME(profile) NEO_LIFETIME(window) NEO_LIFETIME(view)
NEO_LIFETIME(operation) NEO_LIFETIME(decision) NEO_LIFETIME(error) NEO_LIFETIME(buffer) NEO_LIFETIME(stream)
#undef NEO_LIFETIME

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_error_get_code(const neo_webview_error_t* value) { return value ? value->code : NEO_WEBVIEW_ERROR_INVALID_ARGUMENT; }
int64_t NEO_WEBVIEW_CALL neo_webview_error_get_native_code(const neo_webview_error_t* value) { return value ? value->native_code : 0; }
neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_error_get_domain(const neo_webview_error_t* value) { return value ? neo_string_view(value->domain) : neo_webview_string_view_t{}; }
neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_error_get_message(const neo_webview_error_t* value) { return value ? neo_string_view(value->message) : neo_webview_string_view_t{}; }
const uint8_t* NEO_WEBVIEW_CALL neo_webview_buffer_get_data(const neo_webview_buffer_t* value) { return value && !value->bytes.empty() ? value->bytes.data() : nullptr; }
uint64_t NEO_WEBVIEW_CALL neo_webview_buffer_get_length(const neo_webview_buffer_t* value) { return value ? static_cast<uint64_t>(value->bytes.size()) : 0; }
void NEO_WEBVIEW_CALL neo_webview_operation_cancel(neo_webview_operation_t* value) { if (value) value->cancel(); }

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_decision_defer(neo_webview_decision_t* value) {
    if (!value) return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;
    if (value->expire()) return NEO_WEBVIEW_ERROR_TIMED_OUT;
    auto expected=neo_decision_state::pending;
    return value->state.compare_exchange_strong(expected,neo_decision_state::deferred,std::memory_order_acq_rel) ? NEO_WEBVIEW_OK : NEO_WEBVIEW_ERROR_INVALID_STATE;
}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_decision_complete(neo_webview_decision_t* value, const neo_webview_decision_response_t* response, neo_webview_error_t** error) {
    if (!value || !valid_struct(response, response ? response->size : 0, sizeof(*response)) || !valid_decision_action(response->action)) return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, "invalid decision response");
    if (!neo_valid_utf8(response->text) || (response->path_count && !response->paths)) return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, "decision response contains invalid strings");
    for (uint32_t index=0; index<response->path_count; ++index) if (!neo_valid_utf8(response->paths[index])) return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, "decision response contains invalid paths");
    if (value->expire()) return neo_fail(error, NEO_WEBVIEW_ERROR_TIMED_OUT, "decision expired");
    auto current=value->state.load(std::memory_order_acquire);
    while(current==neo_decision_state::pending || current==neo_decision_state::deferred) {
        if(value->state.compare_exchange_weak(current,neo_decision_state::completed,std::memory_order_acq_rel,std::memory_order_acquire)) {
            auto effective=*response;
            if(effective.action==NEO_WEBVIEW_DECISION_DEFAULT)effective.action=value->default_action;
            value->resolve(effective);
            return NEO_WEBVIEW_OK;
        }
    }
    return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_STATE, "decision is already complete");
}
neo_webview_decision_kind_t NEO_WEBVIEW_CALL neo_webview_decision_get_kind(const neo_webview_decision_t* value) { return value ? value->kind : NEO_WEBVIEW_DECISION_UNKNOWN; }
neo_webview_decision_action_t NEO_WEBVIEW_CALL neo_webview_decision_get_default_action(const neo_webview_decision_t* value) { return value ? value->default_action : NEO_WEBVIEW_DECISION_DENY; }
uint64_t NEO_WEBVIEW_CALL neo_webview_decision_get_deadline_ns(const neo_webview_decision_t* value) { return value ? static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(value->deadline.time_since_epoch()).count()) : 0; }

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_create(const neo_webview_app_options_t* options, neo_webview_app_t** output, neo_webview_error_t** error) {
    if (output) *output=nullptr;
    if (!output || !valid_struct(options, options ? options->size : 0, sizeof(*options)) || !valid_shutdown_mode(options->shutdown_mode) || !neo_valid_utf8(options->application_name)) return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, "invalid application options");
    try {
        auto* app=new neo_webview_app;
        app->ui_thread=std::this_thread::get_id(); app->shutdown_mode=options->shutdown_mode;
        if(options->maximum_pending_dispatches) app->dispatch_limit=options->maximum_pending_dispatches;
        app->logs.set(options->log_callback,options->log_context);
        if(!neo_platform_initialize(app,error)){app->release();return error&&*error?(*error)->code:NEO_WEBVIEW_ERROR_BACKEND_UNAVAILABLE;}
        *output=app; return NEO_WEBVIEW_OK;
    } catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}
}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_attach(const neo_webview_app_options_t* options, neo_webview_app_t** output, neo_webview_error_t** error) { auto result=neo_webview_app_create(options,output,error); if(result==NEO_WEBVIEW_OK)(*output)->embedded=true; return result; }
int32_t NEO_WEBVIEW_CALL neo_webview_app_run(neo_webview_app_t* app) {
    if(!app || app->embedded) return NEO_WEBVIEW_ERROR_INVALID_STATE;
    if(!check_ui(app)) return NEO_WEBVIEW_ERROR_WRONG_THREAD;
    auto expected=neo_app_state::created;
    if(!app->state.compare_exchange_strong(expected,neo_app_state::running)) return NEO_WEBVIEW_ERROR_INVALID_STATE;
    const auto result=neo_platform_run(app);
    app->stopped.store(true,std::memory_order_release); app->state.store(neo_app_state::stopped,std::memory_order_release);
    app->events.clear(); app->logs.clear();
    return result;
}
void NEO_WEBVIEW_CALL neo_webview_app_quit(neo_webview_app_t* app, int32_t code) { if(!app)return; app->exit_code.store(code); app->quit_requested.store(true,std::memory_order_release); neo_platform_quit(app); }
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_dispatch(neo_webview_app_t* app, neo_webview_dispatch_callback_t callback, void* context) {
    if(!app||!callback)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;
    { std::lock_guard lock(app->dispatch_mutex); if(app->stopped.load()||app->state.load()==neo_app_state::stopping)return NEO_WEBVIEW_ERROR_DISPOSED; if(app->dispatches.size()>=app->dispatch_limit)return NEO_WEBVIEW_ERROR_INVALID_STATE; app->dispatches.push_back({callback,context}); }
    neo_platform_wake(app); return NEO_WEBVIEW_OK;
}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_set_event_callback(neo_webview_app_t* app, neo_webview_event_callback_t callback, void* context){if(!app)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;app->events.set(callback,context);return NEO_WEBVIEW_OK;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_create_window(neo_webview_app_t* app,const neo_webview_window_options_t* options,neo_webview_window_t** output,neo_webview_error_t** error){
    if(output)*output=nullptr;
    if(!app||!output||!valid_struct(options,options?options->size:0,sizeof(*options))||!neo_valid_utf8(options->title)||options->bounds.width<=0||options->bounds.height<=0||options->minimum_size.width<0||options->minimum_size.height<0||options->maximum_size.width<0||options->maximum_size.height<0||options->state>NEO_WEBVIEW_WINDOW_FULLSCREEN||(options->maximum_size.width>0&&options->minimum_size.width>options->maximum_size.width)||(options->maximum_size.height>0&&options->minimum_size.height>options->maximum_size.height))return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid window arguments");
    if(!check_ui(app))return neo_fail(error,NEO_WEBVIEW_ERROR_WRONG_THREAD,"window creation must run on the UI thread");
    try{
        auto* window=new neo_webview_window(app);window->id=app->next_id.fetch_add(1);window->title=neo_string(options->title);window->bounds=options->bounds;window->minimum_size=options->minimum_size;window->maximum_size=options->maximum_size;window->state=options->state;
        if(options->owner){window->owner=options->owner;window->owner->retain();}
        if(!neo_platform_window_create(window,options,error)){window->release();return error&&*error?(*error)->code:NEO_WEBVIEW_ERROR_NATIVE_FAILURE;}
        {std::lock_guard lock(app->windows_mutex);app->windows.emplace(window->id,window);if(!app->main_window_id)app->main_window_id=window->id;}
        *output=window;return NEO_WEBVIEW_OK;
    }catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}
}
uint64_t NEO_WEBVIEW_CALL neo_webview_app_get_window_count(const neo_webview_app_t* app){if(!app)return 0;std::lock_guard lock(const_cast<neo_webview_app_t*>(app)->windows_mutex);return app->windows.size();}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_get_window(neo_webview_app_t* app,uint64_t id,neo_webview_window_t** output){if(output)*output=nullptr;if(!app||!output)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;std::lock_guard lock(app->windows_mutex);auto found=app->windows.find(id);if(found==app->windows.end())return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;found->second->retain();*output=found->second;return NEO_WEBVIEW_OK;}

uint64_t NEO_WEBVIEW_CALL neo_webview_window_get_id(const neo_webview_window_t* w){return w?w->id:0;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_bounds(const neo_webview_window_t* w,neo_webview_rect_t* value){if(!w||!value)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;std::lock_guard lock(const_cast<neo_webview_window_t*>(w)->state_mutex);*value=w->bounds;return NEO_WEBVIEW_OK;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_bounds(neo_webview_window_t* w,neo_webview_rect_t value){if(!w)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEO_WEBVIEW_ERROR_WRONG_THREAD;if(value.width<=0||value.height<=0)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;{std::lock_guard lock(w->state_mutex);w->bounds=value;}return neo_platform_window_set_bounds(w);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_minimum_size(const neo_webview_window_t* w,neo_webview_size_t* value){if(!w||!value)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;std::lock_guard lock(const_cast<neo_webview_window_t*>(w)->state_mutex);*value=w->minimum_size;return NEO_WEBVIEW_OK;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_minimum_size(neo_webview_window_t* w,neo_webview_size_t value){if(!w)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEO_WEBVIEW_ERROR_WRONG_THREAD;if(value.width<0||value.height<0)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;neo_webview_size_t previous{};{std::lock_guard lock(w->state_mutex);if((w->maximum_size.width>0&&value.width>w->maximum_size.width)||(w->maximum_size.height>0&&value.height>w->maximum_size.height))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;previous=w->minimum_size;w->minimum_size=value;}const auto result=neo_platform_window_set_size_constraints(w);if(result!=NEO_WEBVIEW_OK){std::lock_guard lock(w->state_mutex);w->minimum_size=previous;}return result;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_maximum_size(const neo_webview_window_t* w,neo_webview_size_t* value){if(!w||!value)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;std::lock_guard lock(const_cast<neo_webview_window_t*>(w)->state_mutex);*value=w->maximum_size;return NEO_WEBVIEW_OK;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_maximum_size(neo_webview_window_t* w,neo_webview_size_t value){if(!w)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEO_WEBVIEW_ERROR_WRONG_THREAD;if(value.width<0||value.height<0)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;neo_webview_size_t previous{};{std::lock_guard lock(w->state_mutex);if((value.width>0&&w->minimum_size.width>value.width)||(value.height>0&&w->minimum_size.height>value.height))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;previous=w->maximum_size;w->maximum_size=value;}const auto result=neo_platform_window_set_size_constraints(w);if(result!=NEO_WEBVIEW_OK){std::lock_guard lock(w->state_mutex);w->maximum_size=previous;}return result;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_state(const neo_webview_window_t* w,neo_webview_window_state_t* value){if(!w||!value)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;std::lock_guard lock(const_cast<neo_webview_window_t*>(w)->state_mutex);*value=w->state;return NEO_WEBVIEW_OK;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_state(neo_webview_window_t* w,neo_webview_window_state_t value){if(!w||value>NEO_WEBVIEW_WINDOW_FULLSCREEN)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEO_WEBVIEW_ERROR_WRONG_THREAD;neo_webview_window_state_t previous{};{std::lock_guard lock(w->state_mutex);previous=w->state;w->state=value;}const auto result=neo_platform_window_set_state(w);if(result!=NEO_WEBVIEW_OK){std::lock_guard lock(w->state_mutex);w->state=previous;}return result;}
neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_window_get_title(const neo_webview_window_t* w){if(!w)return {};std::lock_guard lock(const_cast<neo_webview_window_t*>(w)->state_mutex);return neo_string_view(w->title);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_title(neo_webview_window_t* w,neo_webview_string_view_t value){if(!w||!neo_valid_utf8(value))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;if(!check_ui(w->app))return NEO_WEBVIEW_ERROR_WRONG_THREAD;try{{std::lock_guard lock(w->state_mutex);w->title=neo_string(value);}return neo_platform_window_set_title(w);}catch(...){return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_show(neo_webview_window_t* w){return !w?NEO_WEBVIEW_ERROR_INVALID_ARGUMENT:!check_ui(w->app)?NEO_WEBVIEW_ERROR_WRONG_THREAD:neo_platform_window_show(w,true);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_hide(neo_webview_window_t* w){return !w?NEO_WEBVIEW_ERROR_INVALID_ARGUMENT:!check_ui(w->app)?NEO_WEBVIEW_ERROR_WRONG_THREAD:neo_platform_window_show(w,false);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_activate(neo_webview_window_t* w){return !w?NEO_WEBVIEW_ERROR_INVALID_ARGUMENT:!check_ui(w->app)?NEO_WEBVIEW_ERROR_WRONG_THREAD:neo_platform_window_activate(w);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_close(neo_webview_window_t* w){return !w?NEO_WEBVIEW_ERROR_INVALID_ARGUMENT:neo_platform_window_close(w);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_native_handle(neo_webview_window_t* w,neo_webview_native_handle_kind_t kind,neo_webview_native_handle_t* h){if(!w||!valid_struct(h,h?h->size:0,sizeof(*h)))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;return neo_platform_window_get_handle(w,kind,h);}

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_create_async(neo_webview_app_t* app,const neo_webview_environment_options_t* options,neo_webview_environment_created_callback_t callback,void* context,neo_webview_operation_t** outop,neo_webview_error_t** error){
    if(outop)*outop=nullptr;if(!app||!callback||!valid_struct(options,options?options->size:0,sizeof(*options))||!neo_valid_utf8(options->user_data_root)||!neo_valid_utf8(options->browser_runtime_path)||!neo_valid_utf8(options->browser_arguments)||!neo_valid_utf8(options->preferred_languages))return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid environment arguments");
    if(!check_ui(app))return neo_fail(error,NEO_WEBVIEW_ERROR_WRONG_THREAD,"environment creation must begin on the UI thread");
    try{auto* op=make_operation(outop);auto* value=new neo_webview_environment(app);auto* state=new environment_completion{callback,context,op,value,nullptr};neo_webview_error_t* start_error=nullptr;if(!neo_platform_environment_create_async(value,options,platform_environment_created,state,&start_error))platform_environment_created(state,start_error);return NEO_WEBVIEW_OK;}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_create_profile_async(neo_webview_environment_t* env,const neo_webview_profile_options_t* options,neo_webview_profile_created_callback_t callback,void* context,neo_webview_operation_t** outop,neo_webview_error_t** error){
    if(outop)*outop=nullptr;if(!env||!callback||!valid_struct(options,options?options->size:0,sizeof(*options))||!neo_valid_utf8(options->name))return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid profile arguments");if(!check_ui(env->app))return neo_fail(error,NEO_WEBVIEW_ERROR_WRONG_THREAD,"profile creation must begin on the UI thread");
    try{auto* op=make_operation(outop);auto* value=new neo_webview_profile(env);value->name=neo_string(options->name);value->ephemeral=options->ephemeral!=0;if(!neo_platform_profile_create(value,error)){value->release();op->release();if(outop&&*outop){(*outop)->release();*outop=nullptr;}return error&&*error?(*error)->code:NEO_WEBVIEW_ERROR_NATIVE_FAILURE;}auto* state=new profile_completion{callback,context,op,value};auto r=schedule(env->app,complete_profile,state,error,"could not schedule profile completion");if(r!=NEO_WEBVIEW_OK){value->release();op->release();delete state;}return r;}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_create_view_async(neo_webview_environment_t* env,const neo_webview_view_options_t* options,neo_webview_view_created_callback_t callback,void* context,neo_webview_operation_t** outop,neo_webview_error_t** error){
    if(outop)*outop=nullptr;if(!env||!callback||!valid_struct(options,options?options->size:0,sizeof(*options))||options->decision_timeout_ms>600000)return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid view arguments");if(!check_ui(env->app))return neo_fail(error,NEO_WEBVIEW_ERROR_WRONG_THREAD,"view creation must begin on the UI thread");
    try{auto* op=make_operation(outop);auto* value=new neo_webview_view(env);value->profile=options->profile;if(value->profile)value->profile->retain();value->window=options->window;if(value->window){value->window->retain();value->window->views.push_back(value);}value->parent=options->parent;value->bounds=options->bounds;value->fill_parent=options->fill_parent!=0;if(options->decision_timeout_ms)value->decision_timeout=std::chrono::milliseconds(options->decision_timeout_ms);auto* state=new view_completion{callback,context,op,value,nullptr};neo_webview_error_t* start_error=nullptr;if(!neo_platform_view_create_async(value,options,platform_view_created,state,&start_error))platform_view_created(state,start_error);return NEO_WEBVIEW_OK;}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_get_capability(const neo_webview_environment_t* env,neo_webview_capability_t capability,neo_webview_capability_info_t* info){
    if(!env||capability<NEO_WEBVIEW_CAPABILITY_CUSTOM_SCHEME||capability>NEO_WEBVIEW_CAPABILITY_ZOOM||!valid_struct(info,info?info->size:0,sizeof(*info)))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;
    static const std::string available="Implemented by the active NeoWebView backend";
    static const std::string unavailable="Not exposed by the current portable implementation";
    info->support=NEO_WEBVIEW_SUPPORT_NONE;info->capability_version=1;info->flags=0;
    switch(capability){
        case NEO_WEBVIEW_CAPABILITY_SCRIPT_DOCUMENT_START:
        case NEO_WEBVIEW_CAPABILITY_SCRIPT_ALL_FRAMES:
        case NEO_WEBVIEW_CAPABILITY_MESSAGE_ORIGIN:
        case NEO_WEBVIEW_CAPABILITY_COOKIES:
        case NEO_WEBVIEW_CAPABILITY_PROFILE_EPHEMERAL:
        case NEO_WEBVIEW_CAPABILITY_ZOOM:
            info->support=NEO_WEBVIEW_SUPPORT_NATIVE;break;
#if defined(_WIN32)
        case NEO_WEBVIEW_CAPABILITY_PERMISSIONS:
        case NEO_WEBVIEW_CAPABILITY_PERMISSION_PERSISTENCE:
            info->support=NEO_WEBVIEW_SUPPORT_NATIVE;break;
#else
        case NEO_WEBVIEW_CAPABILITY_PERMISSIONS:
            info->support=NEO_WEBVIEW_SUPPORT_LIMITED;break;
#endif
#if !defined(_WIN32)
        case NEO_WEBVIEW_CAPABILITY_SCRIPT_DOCUMENT_END:
            info->support=NEO_WEBVIEW_SUPPORT_NATIVE;break;
#endif
#if defined(_WIN32)
        case NEO_WEBVIEW_CAPABILITY_PROFILE_NAMED:
        case NEO_WEBVIEW_CAPABILITY_CLEAR_DATA_BY_TIME:
            info->support=NEO_WEBVIEW_SUPPORT_NATIVE;break;
#else
        case NEO_WEBVIEW_CAPABILITY_CLEAR_DATA_BY_TIME:
            info->support=NEO_WEBVIEW_SUPPORT_LIMITED;break;
#endif
        default:break;
    }
    info->details=neo_string_view(info->support==NEO_WEBVIEW_SUPPORT_NONE?unavailable:available);return NEO_WEBVIEW_OK;
}

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_get_cookies_async(neo_webview_profile_t* p,neo_webview_string_view_t uri,neo_webview_buffer_callback_t cb,void* ctx,neo_webview_operation_t** outop,neo_webview_error_t** error){if(outop)*outop=nullptr;if(!p||!cb||!neo_valid_utf8(uri))return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid cookie arguments");if(!check_ui(p->environment->app))return neo_fail(error,NEO_WEBVIEW_ERROR_WRONG_THREAD,"cookie operations must begin on the UI thread");try{auto native_uri=neo_string(uri);auto* op=make_operation(outop);auto result=neo_platform_profile_get_cookies(p,native_uri,cb,ctx,op,error);if(result!=NEO_WEBVIEW_OK){op->release();if(outop&&*outop){(*outop)->release();*outop=nullptr;}}return result;}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,ex.what());}}
static bool valid_cookie(const neo_webview_cookie_t* cookie) noexcept {return valid_struct(cookie,cookie?cookie->size:0,sizeof(*cookie))&&neo_valid_utf8(cookie->name)&&neo_valid_utf8(cookie->value)&&neo_valid_utf8(cookie->domain)&&neo_valid_utf8(cookie->path)&&cookie->name.length>0&&cookie->domain.length>0&&(cookie->flags&~7u)==0&&cookie->same_site<=3;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_set_cookie_async(neo_webview_profile_t* p,const neo_webview_cookie_t* cookie,neo_webview_completion_callback_t cb,void* ctx,neo_webview_operation_t** op,neo_webview_error_t** e){if(!valid_cookie(cookie))return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid cookie");return start_profile_operation(p,cb,op,e,[&](auto* operation){return neo_platform_profile_set_cookie(p,cookie,cb,ctx,operation,e);});}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_delete_cookie_async(neo_webview_profile_t* p,const neo_webview_cookie_t* cookie,neo_webview_completion_callback_t cb,void* ctx,neo_webview_operation_t** op,neo_webview_error_t** e){if(!valid_cookie(cookie))return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid cookie");return start_profile_operation(p,cb,op,e,[&](auto* operation){return neo_platform_profile_delete_cookie(p,cookie,cb,ctx,operation,e);});}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_clear_data_async(neo_webview_profile_t* p,neo_webview_data_kind_t kinds,int64_t start,int64_t end,neo_webview_completion_callback_t cb,void* ctx,neo_webview_operation_t** op,neo_webview_error_t** e){constexpr auto known=NEO_WEBVIEW_DATA_COOKIES|NEO_WEBVIEW_DATA_CACHE|NEO_WEBVIEW_DATA_LOCAL_STORAGE|NEO_WEBVIEW_DATA_INDEXED_DB|NEO_WEBVIEW_DATA_SERVICE_WORKERS|NEO_WEBVIEW_DATA_PERMISSIONS|NEO_WEBVIEW_DATA_DOWNLOAD_HISTORY;if(kinds==0||(kinds!=NEO_WEBVIEW_DATA_ALL&&(kinds&~known)!=0)||start>end)return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid data range");return start_profile_operation(p,cb,op,e,[&](auto* operation){return neo_platform_profile_clear_data(p,kinds,start,end,cb,ctx,operation,e);});}

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_set_event_callback(neo_webview_view_t* v,neo_webview_event_callback_t cb,void* ctx){if(!v)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;v->events.set(cb,ctx);return NEO_WEBVIEW_OK;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_set_bounds(neo_webview_view_t* v,neo_webview_rect_t bounds,uint32_t fill){if(!v)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;if(!check_ui(v->environment->app))return NEO_WEBVIEW_ERROR_WRONG_THREAD;v->bounds=bounds;v->fill_parent=fill!=0;return neo_platform_view_set_bounds(v);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_navigate(neo_webview_view_t* v,neo_webview_string_view_t uri,neo_webview_error_t** e){if(!v||!neo_valid_utf8(uri))return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid view or URI");if(!check_ui(v->environment->app))return neo_fail(e,NEO_WEBVIEW_ERROR_WRONG_THREAD,"navigation must run on the UI thread");try{v->source=neo_string(uri);return neo_platform_view_navigate(v,v->source,e);}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid URI");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_navigate_request(neo_webview_view_t* v,neo_webview_string_view_t uri,neo_webview_string_view_t method,neo_webview_string_view_t headers,const uint8_t* body,uint64_t length,neo_webview_error_t** e){if(!v||!neo_valid_utf8(uri)||!neo_valid_utf8(method)||!neo_valid_utf8(headers)||(length&&!body))return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid navigation request");if(!check_ui(v->environment->app))return neo_fail(e,NEO_WEBVIEW_ERROR_WRONG_THREAD,"navigation must run on the UI thread");try{v->source=neo_string(uri);return neo_platform_view_navigate_request(v,v->source,neo_string(method),neo_string(headers),body,length,e);}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid navigation request");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_load_html(neo_webview_view_t* v,neo_webview_string_view_t html,neo_webview_string_view_t base,neo_webview_error_t** e){if(!v||!neo_valid_utf8(html)||!neo_valid_utf8(base))return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid HTML arguments");if(!check_ui(v->environment->app))return neo_fail(e,NEO_WEBVIEW_ERROR_WRONG_THREAD,"HTML loading must run on the UI thread");try{return neo_platform_view_load_html(v,neo_string(html),neo_string(base),e);}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid HTML");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_stop(neo_webview_view_t* v){return !v?NEO_WEBVIEW_ERROR_INVALID_ARGUMENT:!check_ui(v->environment->app)?NEO_WEBVIEW_ERROR_WRONG_THREAD:neo_platform_view_command(v,0);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_reload(neo_webview_view_t* v,uint32_t ignore){return !v?NEO_WEBVIEW_ERROR_INVALID_ARGUMENT:!check_ui(v->environment->app)?NEO_WEBVIEW_ERROR_WRONG_THREAD:neo_platform_view_command(v,ignore?2:1);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_go_back(neo_webview_view_t* v){return !v?NEO_WEBVIEW_ERROR_INVALID_ARGUMENT:!check_ui(v->environment->app)?NEO_WEBVIEW_ERROR_WRONG_THREAD:neo_platform_view_command(v,3);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_go_forward(neo_webview_view_t* v){return !v?NEO_WEBVIEW_ERROR_INVALID_ARGUMENT:!check_ui(v->environment->app)?NEO_WEBVIEW_ERROR_WRONG_THREAD:neo_platform_view_command(v,4);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_evaluate_script_async(neo_webview_view_t* v,neo_webview_string_view_t script,neo_webview_string_callback_t cb,void* ctx,neo_webview_operation_t** outop,neo_webview_error_t** e){if(outop)*outop=nullptr;if(!v||!cb||!neo_valid_utf8(script))return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid script arguments");if(!check_ui(v->environment->app))return neo_fail(e,NEO_WEBVIEW_ERROR_WRONG_THREAD,"script evaluation must begin on the UI thread");try{auto* op=make_operation(outop);auto r=neo_platform_view_evaluate(v,neo_string(script),cb,ctx,op,e);if(r!=NEO_WEBVIEW_OK)op->release();return r;}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid script");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_add_script_async(neo_webview_view_t* v,neo_webview_string_view_t script,const neo_webview_script_options_t* options,neo_webview_string_callback_t cb,void* ctx,neo_webview_operation_t** outop,neo_webview_error_t** e){if(outop)*outop=nullptr;if(!v||!cb||!neo_valid_utf8(script)||!valid_struct(options,options?options->size:0,sizeof(*options))||!neo_valid_utf8(options->world_name))return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid persistent script arguments");if(!check_ui(v->environment->app))return neo_fail(e,NEO_WEBVIEW_ERROR_WRONG_THREAD,"script injection must begin on the UI thread");try{auto* op=make_operation(outop);auto r=neo_platform_view_add_script(v,neo_string(script),options,cb,ctx,op,e);if(r!=NEO_WEBVIEW_OK)op->release();return r;}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid persistent script");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_remove_script(neo_webview_view_t* v,neo_webview_string_view_t identifier){if(!v||!neo_valid_utf8(identifier))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;if(!check_ui(v->environment->app))return NEO_WEBVIEW_ERROR_WRONG_THREAD;try{return neo_platform_view_remove_script(v,neo_string(identifier));}catch(...){return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_post_message(neo_webview_view_t* v,neo_webview_string_view_t msg,uint32_t json,neo_webview_error_t** e){if(!v||!neo_valid_utf8(msg))return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid message");if(!check_ui(v->environment->app))return neo_fail(e,NEO_WEBVIEW_ERROR_WRONG_THREAD,"message posting must run on the UI thread");try{return neo_platform_view_post_message(v,neo_string(msg),json!=0,e);}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid message");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_get_zoom_factor(const neo_webview_view_t* v,double* factor){if(!v||!factor)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;if(!check_ui(v->environment->app))return NEO_WEBVIEW_ERROR_WRONG_THREAD;return neo_platform_view_get_zoom_factor(v,factor);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_set_zoom_factor(neo_webview_view_t* v,double factor){if(!v||!std::isfinite(factor)||factor<0.25||factor>5.0)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;if(!check_ui(v->environment->app))return NEO_WEBVIEW_ERROR_WRONG_THREAD;return neo_platform_view_set_zoom_factor(v,factor);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_get_native_handle(neo_webview_view_t* v,neo_webview_native_handle_kind_t kind,neo_webview_native_handle_t* h){if(!v||!valid_struct(h,h?h->size:0,sizeof(*h)))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;return neo_platform_view_get_handle(v,kind,h);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_query_extension(const void*,neo_webview_string_view_t name,uint32_t,const void** table){if(table)*table=nullptr;if(!table||!neo_valid_utf8(name))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;return NEO_WEBVIEW_ERROR_NOT_SUPPORTED;}

} // extern C
