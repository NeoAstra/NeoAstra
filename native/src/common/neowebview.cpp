#include "native_internal.hpp"

#include <algorithm>
#include <cstring>
#include <new>

neo_webview_app::~neo_webview_app() { neo_platform_shutdown(this); }
neo_webview_environment::~neo_webview_environment() { neo_platform_environment_destroy(this); app->release(); }
neo_webview_profile::~neo_webview_profile() { environment->release(); }
neo_webview_window::~neo_webview_window() { neo_platform_window_destroy(this); if (owner) owner->release(); app->release(); }
neo_webview_view::~neo_webview_view() { neo_platform_view_destroy(this); if (profile) profile->release(); if (window) window->release(); environment->release(); }

neo_webview_result_t neo_fail(neo_webview_error_t** output, neo_webview_result_t code, std::string message, int64_t native_code, std::string domain) noexcept {
    if (output) {
        try { auto* value = new neo_webview_error; value->code = code; value->native_code = native_code; value->domain = std::move(domain); value->message = std::move(message); *output = value; }
        catch (...) { *output = nullptr; }
    }
    return code;
}

void neo_emit(neo_webview_app_t*, neo_webview_event_callback_t callback, void* context, neo_webview_event_type_t type, uint64_t object_id, const std::string* text, const std::string* uri, uint64_t value, int64_t native_code, neo_webview_decision_t* decision) noexcept {
    if (!callback) return;
    neo_webview_event_t event{};
    event.header = {sizeof(event), 1, type, value, neo_timestamp_ns()};
    event.object_id = object_id;
    if (text) event.text = neo_string_view(*text);
    if (uri) event.uri = neo_string_view(*uri);
    event.value = value;
    event.native_code = native_code;
    event.decision = decision;
    try { callback(context, &event); } catch (...) { }
}

void neo_drain_dispatch(neo_webview_app_t* app) noexcept {
    for (;;) {
        neo_dispatch_item item{};
        {
            std::lock_guard lock(app->dispatch_mutex);
            if (app->dispatches.empty() || app->stopped.load(std::memory_order_acquire)) return;
            item = app->dispatches.front();
            app->dispatches.pop_front();
        }
        try { item.callback(item.context); } catch (...) { }
    }
}

namespace {
template<class T> void retain(T* value) noexcept { if (value) value->retain(); }
template<class T> void release(T* value) noexcept { if (value) value->release(); }

struct environment_completion {
    neo_webview_environment_created_callback_t callback; void* context; neo_webview_operation_t* operation; neo_webview_environment_t* value; neo_webview_error_t* error;
};
void NEO_WEBVIEW_CALL complete_environment(void* pointer) {
    auto* state = static_cast<environment_completion*>(pointer);
    auto result = state->operation->canceled.load() ? NEO_WEBVIEW_ERROR_CANCELED : (state->error ? state->error->code : NEO_WEBVIEW_OK);
    state->callback(state->context, result, result == NEO_WEBVIEW_OK ? state->value : nullptr, state->error);
    if (result != NEO_WEBVIEW_OK && state->value) state->value->release();
    if (state->error) state->error->release();
    state->operation->release();
    delete state;
}
struct profile_completion { neo_webview_profile_created_callback_t callback; void* context; neo_webview_operation_t* operation; neo_webview_profile_t* value; };
void NEO_WEBVIEW_CALL complete_profile(void* pointer) {
    auto* state = static_cast<profile_completion*>(pointer);
    auto result = state->operation->canceled.load() ? NEO_WEBVIEW_ERROR_CANCELED : NEO_WEBVIEW_OK;
    state->callback(state->context, result, result == NEO_WEBVIEW_OK ? state->value : nullptr, nullptr);
    if (result != NEO_WEBVIEW_OK) state->value->release();
    state->operation->release(); delete state;
}
struct view_completion { neo_webview_view_created_callback_t callback; void* context; neo_webview_operation_t* operation; neo_webview_view_t* value; neo_webview_error_t* error; };
void NEO_WEBVIEW_CALL complete_view(void* pointer) {
    auto* state = static_cast<view_completion*>(pointer);
    auto result = state->operation->canceled.load() ? NEO_WEBVIEW_ERROR_CANCELED : (state->error ? state->error->code : NEO_WEBVIEW_OK);
    state->callback(state->context, result, result == NEO_WEBVIEW_OK ? state->value : nullptr, state->error);
    if (result != NEO_WEBVIEW_OK && state->value) state->value->release();
    if (state->error) state->error->release();
    state->operation->release(); delete state;
}
struct simple_completion { neo_webview_completion_callback_t callback; void* context; neo_webview_operation_t* operation; neo_webview_result_t result; };
void NEO_WEBVIEW_CALL complete_simple(void* pointer) {
    auto* state = static_cast<simple_completion*>(pointer);
    auto result = state->operation->canceled.load() ? NEO_WEBVIEW_ERROR_CANCELED : state->result;
    state->callback(state->context, result, nullptr);
    state->operation->release(); delete state;
}
struct buffer_completion { neo_webview_buffer_callback_t callback; void* context; neo_webview_operation_t* operation; neo_webview_buffer_t* buffer; neo_webview_result_t result; };
void NEO_WEBVIEW_CALL complete_buffer(void* pointer) {
    auto* state = static_cast<buffer_completion*>(pointer);
    auto result = state->operation->canceled.load() ? NEO_WEBVIEW_ERROR_CANCELED : state->result;
    state->callback(state->context, result, result == NEO_WEBVIEW_OK ? state->buffer : nullptr, nullptr);
    if (result != NEO_WEBVIEW_OK) state->buffer->release();
    state->operation->release(); delete state;
}

neo_webview_operation_t* make_operation(neo_webview_operation_t** output) {
    auto* operation = new neo_webview_operation;
    if (output) { operation->retain(); *output = operation; }
    return operation;
}
bool valid_size(const void* value, uint32_t supplied, size_t required) { return value && supplied >= required; }
}

extern "C" {
uint32_t NEO_WEBVIEW_CALL neo_webview_get_abi_version_major() { return NEO_WEBVIEW_ABI_VERSION_MAJOR; }
uint32_t NEO_WEBVIEW_CALL neo_webview_get_abi_version_minor() { return NEO_WEBVIEW_ABI_VERSION_MINOR; }
neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_get_version() { static const std::string value = "0.1.0"; return neo_string_view(value); }

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_get_runtime_info(neo_webview_runtime_info_t* info, neo_webview_error_t** error) {
    if (!info || info->size < sizeof(neo_webview_runtime_info_t)) return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, "runtime info has an invalid size");
#if defined(_WIN32)
    static const std::string backend="webview2", os="windows";
#elif defined(__APPLE__)
    static const std::string backend="wkwebview", os="macos";
#else
    static const std::string backend="webkitgtk", os="linux";
#endif
#if defined(_M_ARM64) || defined(__aarch64__)
    static const std::string architecture="arm64";
#else
    static const std::string architecture="x64";
#endif
    static const std::string version="system";
    info->backend_name=neo_string_view(backend); info->backend_version=neo_string_view(version); info->browser_version=neo_string_view(version); info->operating_system=neo_string_view(os); info->architecture=neo_string_view(architecture);
#ifdef NDEBUG
    info->debug_build=0;
#else
    info->debug_build=1;
#endif
    return NEO_WEBVIEW_OK;
}

#define NEO_LIFETIME(name) void NEO_WEBVIEW_CALL neo_webview_##name##_retain(neo_webview_##name##_t* value){retain(value);} void NEO_WEBVIEW_CALL neo_webview_##name##_release(neo_webview_##name##_t* value){release(value);}
NEO_LIFETIME(app) NEO_LIFETIME(environment) NEO_LIFETIME(profile) NEO_LIFETIME(window) NEO_LIFETIME(view) NEO_LIFETIME(operation) NEO_LIFETIME(decision) NEO_LIFETIME(error) NEO_LIFETIME(buffer)
#undef NEO_LIFETIME

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_error_get_code(const neo_webview_error_t* value) { return value ? value->code : NEO_WEBVIEW_ERROR_INVALID_ARGUMENT; }
int64_t NEO_WEBVIEW_CALL neo_webview_error_get_native_code(const neo_webview_error_t* value) { return value ? value->native_code : 0; }
neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_error_get_domain(const neo_webview_error_t* value) { return value ? neo_string_view(value->domain) : neo_webview_string_view_t{}; }
neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_error_get_message(const neo_webview_error_t* value) { return value ? neo_string_view(value->message) : neo_webview_string_view_t{}; }
const uint8_t* NEO_WEBVIEW_CALL neo_webview_buffer_get_data(const neo_webview_buffer_t* value) { return value && !value->bytes.empty() ? value->bytes.data() : nullptr; }
uint64_t NEO_WEBVIEW_CALL neo_webview_buffer_get_length(const neo_webview_buffer_t* value) { return value ? value->bytes.size() : 0; }
void NEO_WEBVIEW_CALL neo_webview_operation_cancel(neo_webview_operation_t* value) { if (value) value->canceled.store(true, std::memory_order_release); }
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_decision_defer(neo_webview_decision_t* value) { if (!value) return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT; uint32_t expected=0; return value->state.compare_exchange_strong(expected,1) ? NEO_WEBVIEW_OK : NEO_WEBVIEW_ERROR_INVALID_STATE; }
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_decision_complete(neo_webview_decision_t* value, const neo_webview_decision_response_t* response, neo_webview_error_t** error) { if (!value || !response || response->size < sizeof(*response)) return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, "invalid decision response"); auto old=value->state.exchange(2); return old < 2 && std::chrono::steady_clock::now() <= value->deadline ? NEO_WEBVIEW_OK : neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_STATE, "decision is already completed or expired"); }

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_create(const neo_webview_app_options_t* options, neo_webview_app_t** output, neo_webview_error_t** error) {
    if (!output || !valid_size(options, options ? options->size : 0, sizeof(*options))) return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, "invalid application options");
    try { auto* app=new neo_webview_app; app->ui_thread=std::this_thread::get_id(); app->shutdown_mode=options->shutdown_mode; if(options->maximum_pending_dispatches) app->dispatch_limit=options->maximum_pending_dispatches; app->log_callback=options->log_callback; app->log_context=options->log_context; if(!neo_platform_initialize(app,error)){app->release();return error&&*error?(*error)->code:NEO_WEBVIEW_ERROR_BACKEND_UNAVAILABLE;} *output=app; return NEO_WEBVIEW_OK; } catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}
}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_attach(const neo_webview_app_options_t* options, neo_webview_app_t** output, neo_webview_error_t** error) { auto result=neo_webview_app_create(options,output,error); if(result==NEO_WEBVIEW_OK)(*output)->embedded=true; return result; }
int32_t NEO_WEBVIEW_CALL neo_webview_app_run(neo_webview_app_t* app) { if(!app || app->embedded || app->ui_thread!=std::this_thread::get_id()) return NEO_WEBVIEW_ERROR_INVALID_STATE; return neo_platform_run(app); }
void NEO_WEBVIEW_CALL neo_webview_app_quit(neo_webview_app_t* app, int32_t code) { if(!app)return; app->exit_code.store(code); app->stopping.store(true); neo_platform_quit(app); }
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_dispatch(neo_webview_app_t* app, neo_webview_dispatch_callback_t callback, void* context) { if(!app||!callback)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT; {std::lock_guard lock(app->dispatch_mutex); if(app->stopping||app->stopped)return NEO_WEBVIEW_ERROR_DISPOSED; if(app->dispatches.size()>=app->dispatch_limit)return NEO_WEBVIEW_ERROR_INVALID_STATE; app->dispatches.push_back({callback,context});} neo_platform_wake(app); return NEO_WEBVIEW_OK; }
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_set_event_callback(neo_webview_app_t* app, neo_webview_event_callback_t callback, void* context){if(!app)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;app->event_callback=callback;app->event_context=context;return NEO_WEBVIEW_OK;}

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_create_window(neo_webview_app_t* app,const neo_webview_window_options_t* options,neo_webview_window_t** output,neo_webview_error_t** error){if(!app||!output||!valid_size(options,options?options->size:0,sizeof(*options)))return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid window arguments");try{auto* window=new neo_webview_window(app);window->id=app->next_id.fetch_add(1);window->title=neo_string(options->title);window->bounds=options->bounds;if(options->owner){window->owner=options->owner;window->owner->retain();}if(!neo_platform_window_create(window,options,error)){window->release();return error&&*error?(*error)->code:NEO_WEBVIEW_ERROR_NATIVE_FAILURE;}app->window_count.fetch_add(1);*output=window;return NEO_WEBVIEW_OK;}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}}
uint64_t NEO_WEBVIEW_CALL neo_webview_window_get_id(const neo_webview_window_t* w){return w?w->id:0;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_bounds(const neo_webview_window_t* w,neo_webview_rect_t* value){if(!w||!value)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;*value=w->bounds;return NEO_WEBVIEW_OK;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_bounds(neo_webview_window_t* w,neo_webview_rect_t value){if(!w)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;w->bounds=value;return neo_platform_window_set_bounds(w);}
neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_window_get_title(const neo_webview_window_t* w){return w?neo_string_view(w->title):neo_webview_string_view_t{};}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_title(neo_webview_window_t* w,neo_webview_string_view_t value){if(!w)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;try{w->title=neo_string(value);return neo_platform_window_set_title(w);}catch(...){return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_show(neo_webview_window_t* w){return w?neo_platform_window_show(w,true):NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_hide(neo_webview_window_t* w){return w?neo_platform_window_show(w,false):NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_activate(neo_webview_window_t* w){return w?neo_platform_window_activate(w):NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_close(neo_webview_window_t* w){return w?neo_platform_window_close(w):NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_native_handle(neo_webview_window_t* w,neo_webview_native_handle_kind_t kind,neo_webview_native_handle_t* h){if(!w||!h||h->size<sizeof(*h))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;return neo_platform_window_get_handle(w,kind,h);}

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_create_async(neo_webview_app_t* app,const neo_webview_environment_options_t* options,neo_webview_environment_created_callback_t callback,void* context,neo_webview_operation_t** outop,neo_webview_error_t** error){if(!app||!callback||!valid_size(options,options?options->size:0,sizeof(*options)))return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid environment arguments");try{auto* op=make_operation(outop);auto* value=new neo_webview_environment(app);neo_webview_error_t* async_error=nullptr;neo_platform_environment_create(value,options,&async_error);auto* state=new environment_completion{callback,context,op,value,async_error};auto r=neo_webview_app_dispatch(app,complete_environment,state);if(r!=NEO_WEBVIEW_OK){value->release();if(async_error)async_error->release();op->release();delete state;return neo_fail(error,r,"could not schedule environment completion");}return NEO_WEBVIEW_OK;}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_create_profile_async(neo_webview_environment_t* env,const neo_webview_profile_options_t* options,neo_webview_profile_created_callback_t callback,void* context,neo_webview_operation_t** outop,neo_webview_error_t** error){if(!env||!callback||!valid_size(options,options?options->size:0,sizeof(*options)))return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid profile arguments");try{auto* op=make_operation(outop);auto* value=new neo_webview_profile(env);value->name=neo_string(options->name);value->ephemeral=options->ephemeral!=0;auto* state=new profile_completion{callback,context,op,value};auto r=neo_webview_app_dispatch(env->app,complete_profile,state);if(r!=NEO_WEBVIEW_OK){value->release();op->release();delete state;return r;}return NEO_WEBVIEW_OK;}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_create_view_async(neo_webview_environment_t* env,const neo_webview_view_options_t* options,neo_webview_view_created_callback_t callback,void* context,neo_webview_operation_t** outop,neo_webview_error_t** error){if(!env||!callback||!valid_size(options,options?options->size:0,sizeof(*options)))return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid view arguments");try{auto* op=make_operation(outop);auto* value=new neo_webview_view(env);value->profile=options->profile;if(value->profile)value->profile->retain();value->window=options->window;if(value->window)value->window->retain();value->parent=options->parent;value->bounds=options->bounds;value->fill_parent=options->fill_parent!=0;neo_webview_error_t* async_error=nullptr;neo_platform_view_create(value,options,&async_error);auto* state=new view_completion{callback,context,op,value,async_error};auto r=neo_webview_app_dispatch(env->app,complete_view,state);if(r!=NEO_WEBVIEW_OK){value->release();if(async_error)async_error->release();op->release();delete state;return r;}return NEO_WEBVIEW_OK;}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_get_capability(const neo_webview_environment_t* env,neo_webview_capability_t capability,neo_webview_capability_info_t* info){if(!env||!info||info->size<sizeof(*info))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;info->support=NEO_WEBVIEW_SUPPORT_NONE;switch(capability){case NEO_WEBVIEW_CAPABILITY_SCRIPT_DOCUMENT_START:case NEO_WEBVIEW_CAPABILITY_SCRIPT_DOCUMENT_END:case NEO_WEBVIEW_CAPABILITY_MESSAGE_ORIGIN:case NEO_WEBVIEW_CAPABILITY_COOKIES:case NEO_WEBVIEW_CAPABILITY_PROFILE_EPHEMERAL:case NEO_WEBVIEW_CAPABILITY_DEVTOOLS:info->support=NEO_WEBVIEW_SUPPORT_NATIVE;break;default:break;}info->capability_version=1;return NEO_WEBVIEW_OK;}

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_get_cookies_async(neo_webview_profile_t* p,neo_webview_string_view_t,neo_webview_buffer_callback_t cb,void* ctx,neo_webview_operation_t** outop,neo_webview_error_t** error){if(!p||!cb)return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid cookie arguments");auto* op=make_operation(outop);auto* buffer=new neo_webview_buffer;auto* state=new buffer_completion{cb,ctx,op,buffer,NEO_WEBVIEW_ERROR_NOT_SUPPORTED};auto r=neo_webview_app_dispatch(p->environment->app,complete_buffer,state);if(r!=NEO_WEBVIEW_OK){buffer->release();op->release();delete state;}return r;}
static neo_webview_result_t profile_simple(neo_webview_profile_t* p,neo_webview_completion_callback_t cb,void* ctx,neo_webview_operation_t** outop,neo_webview_error_t** error){if(!p||!cb)return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid profile operation");auto* op=make_operation(outop);auto* state=new simple_completion{cb,ctx,op,NEO_WEBVIEW_ERROR_NOT_SUPPORTED};auto r=neo_webview_app_dispatch(p->environment->app,complete_simple,state);if(r!=NEO_WEBVIEW_OK){op->release();delete state;}return r;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_set_cookie_async(neo_webview_profile_t* p,const neo_webview_cookie_t*,neo_webview_completion_callback_t cb,void* ctx,neo_webview_operation_t** op,neo_webview_error_t** e){return profile_simple(p,cb,ctx,op,e);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_delete_cookie_async(neo_webview_profile_t* p,const neo_webview_cookie_t*,neo_webview_completion_callback_t cb,void* ctx,neo_webview_operation_t** op,neo_webview_error_t** e){return profile_simple(p,cb,ctx,op,e);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_clear_data_async(neo_webview_profile_t* p,neo_webview_data_kind_t,int64_t,int64_t,neo_webview_completion_callback_t cb,void* ctx,neo_webview_operation_t** op,neo_webview_error_t** e){return profile_simple(p,cb,ctx,op,e);}

neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_set_event_callback(neo_webview_view_t* v,neo_webview_event_callback_t cb,void* ctx){if(!v)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;v->event_callback=cb;v->event_context=ctx;return NEO_WEBVIEW_OK;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_set_bounds(neo_webview_view_t* v,neo_webview_rect_t bounds,uint32_t fill){if(!v)return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;v->bounds=bounds;v->fill_parent=fill!=0;return neo_platform_view_set_bounds(v);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_navigate(neo_webview_view_t* v,neo_webview_string_view_t uri,neo_webview_error_t** e){if(!v)return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"view is null");try{v->source=neo_string(uri);return neo_platform_view_navigate(v,v->source,e);}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid URI");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_navigate_request(neo_webview_view_t* v,neo_webview_string_view_t uri,neo_webview_string_view_t method,neo_webview_string_view_t,const uint8_t*,uint64_t,neo_webview_error_t** e){try{if(neo_string(method)!="GET")return neo_fail(e,NEO_WEBVIEW_ERROR_NOT_SUPPORTED,"custom-method navigation is unavailable on this backend");}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid navigation request");}return neo_webview_view_navigate(v,uri,e);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_load_html(neo_webview_view_t* v,neo_webview_string_view_t html,neo_webview_string_view_t base,neo_webview_error_t** e){if(!v)return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"view is null");try{return neo_platform_view_load_html(v,neo_string(html),neo_string(base),e);}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid HTML");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_stop(neo_webview_view_t* v){return v?neo_platform_view_command(v,0):NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_reload(neo_webview_view_t* v,uint32_t ignore){return v?neo_platform_view_command(v,ignore?2:1):NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_go_back(neo_webview_view_t* v){return v?neo_platform_view_command(v,3):NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_go_forward(neo_webview_view_t* v){return v?neo_platform_view_command(v,4):NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_evaluate_script_async(neo_webview_view_t* v,neo_webview_string_view_t script,neo_webview_string_callback_t cb,void* ctx,neo_webview_operation_t** outop,neo_webview_error_t** e){if(!v||!cb)return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid script arguments");try{auto* op=make_operation(outop);auto r=neo_platform_view_evaluate(v,neo_string(script),cb,ctx,op,e);if(r!=NEO_WEBVIEW_OK)op->release();return r;}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid script");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_add_script_async(neo_webview_view_t*,neo_webview_string_view_t,const neo_webview_script_options_t*,neo_webview_string_callback_t,void*,neo_webview_operation_t**,neo_webview_error_t** e){return neo_fail(e,NEO_WEBVIEW_ERROR_NOT_SUPPORTED,"persistent script injection is unavailable");}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_remove_script(neo_webview_view_t*,neo_webview_string_view_t){return NEO_WEBVIEW_ERROR_NOT_SUPPORTED;}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_post_message(neo_webview_view_t* v,neo_webview_string_view_t msg,uint32_t json,neo_webview_error_t** e){if(!v)return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"view is null");try{return neo_platform_view_post_message(v,neo_string(msg),json!=0,e);}catch(...){return neo_fail(e,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"invalid message");}}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_get_native_handle(neo_webview_view_t* v,neo_webview_native_handle_kind_t kind,neo_webview_native_handle_t* h){if(!v||!h||h->size<sizeof(*h))return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;return neo_platform_view_get_handle(v,kind,h);}
neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_query_extension(const void*,neo_webview_string_view_t,uint32_t,const void** table){if(table)*table=nullptr;return NEO_WEBVIEW_ERROR_NOT_SUPPORTED;}
}
