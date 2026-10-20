#include "../common/native_internal.hpp"

#include <gtk/gtk.h>
#include <webkit/webkit.h>

#include <algorithm>
#include <cerrno>
#include <filesystem>
#include <memory>
#include <new>
#include <string>
#include <system_error>
#include <unordered_map>
#include <utility>
#include <vector>

// Of the X11 backend of GDK, which GTK may be built without: null where it is absent.
extern "C" __attribute__((weak)) void gdk_x11_monitor_get_workarea(GdkMonitor* monitor, GdkRectangle* workarea);

namespace {
// `monitors` is the list of the monitors of the display, and `watched` the monitors whose changes are listened to, each held.
// `display_source` reads the displays once GTK is done telling of a change.
struct gtk_app { GMainContext* context{}; GMainLoop* loop{}; GSource* wake_source{}; GListModel* monitors{}; gulong monitors_changed{}; std::vector<GdkMonitor*> watched; GSource* display_source{}; };
struct gtk_window { GtkWidget* widget{}; neoastra_window_state_t reported_state{NEOASTRA_WINDOW_NORMAL}; GdkSurface* surface{}; gulong laid_out{}; gulong entered_monitor{}; gulong left_monitor{}; };
struct gtk_scheme_context { neoastra_environment_t* environment{}; };
struct gtk_environment { WebKitWebContext* context{}; WebKitNetworkSession* session{}; gulong download_started{}; std::vector<gtk_scheme_context*> schemes; };
struct gtk_profile { WebKitNetworkSession* session{}; gulong download_started{}; };
struct gtk_view { GtkWidget* widget{}; WebKitUserContentManager* content{}; WebKitBackForwardList* history_list{}; GtkDropTarget* drop_target{}; GtkEventController* keys{}; gulong load_changed{}; gulong load_failed{}; gulong title_changed{}; gulong uri_changed{}; gulong message_received{}; gulong process_terminated{}; gulong drop_received{}; gulong history_changed{}; uint64_t history{}; bool context_menus_disabled{}; bool default_script_dialogs{}; uint64_t next_script{1}; std::unordered_map<std::string,WebKitUserScript*> scripts; };

neoastra_error_t* make_error(neoastra_result_t code, const char* message, int64_t native_code = 0, const char* domain = "webkitgtk") noexcept { neoastra_error_t* error{}; neo_fail(&error, code, message, native_code, domain); return error; }

struct g_object_deleter { void operator()(void* value) const noexcept { if(value)g_object_unref(value); } };
template<typename T> using g_object_ptr=std::unique_ptr<T,g_object_deleter>;
struct g_bytes_deleter { void operator()(GBytes* value) const noexcept { if(value)g_bytes_unref(value); } };
using g_bytes_ptr=std::unique_ptr<GBytes,g_bytes_deleter>;
struct soup_headers_deleter { void operator()(SoupMessageHeaders* value) const noexcept { if(value)soup_message_headers_unref(value); } };
using soup_headers_ptr=std::unique_ptr<SoupMessageHeaders,soup_headers_deleter>;

const neo_custom_scheme_registration* find_custom_scheme(const neoastra_environment_t* environment, const char* name) noexcept {
    if (!environment || !name) return nullptr;
    const auto found=std::find_if(environment->custom_schemes.begin(),environment->custom_schemes.end(),
        [name](const auto& scheme){return g_ascii_strcasecmp(scheme.name.c_str(),name)==0;});
    return found==environment->custom_schemes.end()?nullptr:&*found;
}

struct header_accumulator { std::string value; bool failed{}; };
void append_request_header(const char* name,const char* value,void* data) noexcept {
    auto* output=static_cast<header_accumulator*>(data);
    if(output->failed)return;
    try{output->value+=name?name:"";output->value+=": ";output->value+=value?value:"";output->value+="\r\n";}
    catch(...){output->failed=true;}
}

void fail_uri_scheme_request(WebKitURISchemeRequest* request,const char* message) noexcept {
    auto* error=g_error_new_literal(G_IO_ERROR,G_IO_ERROR_FAILED,message);
    webkit_uri_scheme_request_finish_error(request,error);
    g_error_free(error);
}

// The browser of a view in a window shows the window color until its page paints.
void apply_view_background(neoastra_view_t* view) noexcept {
    auto* state=static_cast<gtk_view*>(view->platform);
    if(!view->window||!state||!state->widget)return;
    const auto color=view->window->background_color;
    const GdkRGBA value{color.red/255.0f,color.green/255.0f,color.blue/255.0f,color.alpha/255.0f};
    webkit_web_view_set_background_color(WEBKIT_WEB_VIEW(state->widget),&value);
}

// A drag that started in this application (an in-page HTML5 drag) is refused here, so that WebKit's own drop target
// takes it and the page gets its drop. Other drags get GtkDropTarget's own check, which the first handler replaces.
gboolean drop_accept(GtkDropTarget* target,GdkDrop* drop,void*) noexcept {
    if(gdk_drop_get_drag(drop))return FALSE;
    if((gdk_drop_get_actions(drop)&gtk_drop_target_get_actions(target))==0)return FALSE;
    auto* formats=gtk_drop_target_get_formats(target);
    return formats&&gdk_content_formats_match(formats,gdk_drop_get_formats(drop));
}

gboolean drop_received(GtkDropTarget*,const GValue* value,double x,double y,void* data) noexcept {
    auto* view=static_cast<neoastra_view_t*>(data);
    neo_retain_guard owner(view);if(!owner)return FALSE;
    try {
        neo_event_details details{};details.bounds={static_cast<int32_t>(x),static_cast<int32_t>(y),0,0};
        if(G_VALUE_HOLDS_STRING(value)){
            const auto* raw=g_value_get_string(value);
            if(!raw)return FALSE;
            std::string input(raw);
            if(input.empty()||input.size()>32768)return FALSE;
            neo_emit_view_detailed(view,NEOASTRA_EVENT_MESSAGE_RECEIVED,0,&input,nullptr,(UINT64_C(1)<<63)|1,0,nullptr,details);
            return TRUE;
        }
        if(!G_VALUE_HOLDS(value,GDK_TYPE_FILE_LIST))return FALSE;
        std::string paths,links;uint32_t count=0,file_count=0,link_count=0;bool valid=true;
        std::unique_ptr<GSList,decltype(&g_slist_free)> files(gdk_file_list_get_files(static_cast<GdkFileList*>(g_value_get_boxed(value))),g_slist_free);
        for(auto* current=files.get();current;current=current->next){
            if(++count>256){valid=false;break;}
            auto* file=G_FILE(current->data);
            std::unique_ptr<gchar,decltype(&g_free)> path(g_file_get_path(file),g_free);
            if(!path){std::unique_ptr<gchar,decltype(&g_free)> uri(g_file_get_uri(file),g_free);const size_t length=uri?strlen(uri.get()):0;if(length==0||length>32768||links.size()+length+1>1024*1024){valid=false;break;}if(!links.empty())links.push_back('\0');links.append(uri.get(),length);++link_count;continue;}
            gchar* canonical=g_canonicalize_filename(path.get(),nullptr);
            const size_t length=canonical?strlen(canonical):0;
            if(!canonical||length==0||length>32768||paths.size()+length+1>1024*1024){if(canonical)g_free(canonical);valid=false;break;}
            if(!paths.empty())paths.push_back('\0');paths.append(canonical,length);g_free(canonical);++file_count;
        }
        if(valid&&(!paths.empty()||!links.empty())){
            if(!paths.empty())neo_emit_view_detailed(view,NEOASTRA_EVENT_MESSAGE_RECEIVED,0,&paths,nullptr,(UINT64_C(1)<<63)|(UINT64_C(2)<<56)|file_count,0,nullptr,details);
            if(!links.empty())neo_emit_view_detailed(view,NEOASTRA_EVENT_MESSAGE_RECEIVED,0,&links,nullptr,(UINT64_C(1)<<63)|(UINT64_C(1)<<56)|link_count,0,nullptr,details);
            return TRUE;
        }
    }catch(...){neo_log(view->environment->app,NEOASTRA_LOG_WARNING,"drag-drop","WebKitGTK native drop decoding failed");}
    return FALSE;
}

soup_headers_ptr make_response_headers(const neoastra_resource_response_t& response) {
    soup_headers_ptr result(soup_message_headers_new(SOUP_MESSAGE_HEADERS_RESPONSE));
    const auto raw=neo_string(response.headers);
    for(size_t position=0;position<raw.size();){
        const auto end=raw.find('\n',position);
        auto line=std::string_view(raw).substr(position,end==std::string::npos?raw.size()-position:end-position);
        while(!line.empty()&&line.back()=='\r')line.remove_suffix(1);
        const auto separator=line.find(':');
        if(separator!=std::string_view::npos&&separator!=0){
            auto name=std::string(line.substr(0,separator));
            auto value=std::string(line.substr(separator+1));
            const auto first=value.find_first_not_of(" \t");
            if(first==std::string::npos)value.clear();else value.erase(0,first);
            soup_message_headers_append(result.get(),name.c_str(),value.c_str());
        }
        if(end==std::string::npos)break;
        position=end+1;
    }
    const auto mime=neo_string(response.mime_type);
    if(!mime.empty()&&!soup_message_headers_get_one(result.get(),"Content-Type"))soup_message_headers_append(result.get(),"Content-Type",mime.c_str());
    if(response.content_length!=UINT64_MAX&&!soup_message_headers_get_one(result.get(),"Content-Length")){
        const auto length=std::to_string(response.content_length);
        soup_message_headers_append(result.get(),"Content-Length",length.c_str());
    }
    return result;
}

void uri_scheme_requested(WebKitURISchemeRequest* native_request,void* data) noexcept {
    auto* environment=static_cast<gtk_scheme_context*>(data)->environment;
    neo_retain_guard owner(environment);
    g_object_ptr<WebKitURISchemeRequest> request_owner(WEBKIT_URI_SCHEME_REQUEST(g_object_ref(native_request)));
    if(!owner){fail_uri_scheme_request(native_request,"The custom-scheme environment is unavailable.");return;}
    const auto* scheme=find_custom_scheme(environment,webkit_uri_scheme_request_get_scheme(native_request));
    if(!scheme||!scheme->provider){fail_uri_scheme_request(native_request,"The custom-scheme provider is unavailable.");return;}

    neoastra_resource_response_t response{};
    response.size=sizeof(response);
    response.version=1;
    neo_resource_response_release_guard response_guard{response};
    try{
        const char* raw_uri=webkit_uri_scheme_request_get_uri(native_request);
        const char* raw_method=webkit_uri_scheme_request_get_http_method(native_request);
        const std::string uri=raw_uri?raw_uri:"";
        const std::string method=raw_method?raw_method:"GET";
        header_accumulator headers;
        if(auto* native_headers=webkit_uri_scheme_request_get_http_headers(native_request))soup_message_headers_foreach(native_headers,append_request_header,&headers);
        if(headers.failed)throw std::bad_alloc();
        if(!neo_resource_request_within_limits(uri,method,headers.value)){
            neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme request metadata exceeded its size limit");
            fail_uri_scheme_request(native_request,"The custom-scheme request metadata is too large.");
            return;
        }

        std::vector<uint8_t> body;
        if(auto* input=webkit_uri_scheme_request_get_http_body(native_request)){
            uint8_t buffer[8192];
            for(;;){
                GError* read_error{};
                const auto count=g_input_stream_read(input,buffer,sizeof(buffer),nullptr,&read_error);
                if(count<0){
                    neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Could not read a custom-scheme request body",read_error?read_error->code:0);
                    if(read_error)g_error_free(read_error);
                    fail_uri_scheme_request(native_request,"Could not read the custom-scheme request body.");
                    return;
                }
                if(count==0)break;
                const auto amount=static_cast<size_t>(count);
                if(amount>neo_maximum_buffered_resource_body_size-body.size()){
                    neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme request body exceeded the 64 MiB limit");
                    fail_uri_scheme_request(native_request,"The custom-scheme request body is too large.");
                    return;
                }
                body.insert(body.end(),buffer,buffer+count);
            }
        }

        // WebKitGTK 6.0 does not expose initiator, frame, or resource-kind metadata
        // on WebKitURISchemeRequest, so these fields remain explicitly unknown.
        neoastra_resource_request_t request{};
        request.size=sizeof(request);
        request.version=1;
        request.uri=neo_string_view(uri);
        request.method=neo_string_view(method);
        request.headers=neo_string_view(headers.value);
        request.resource_kind=NEOASTRA_RESOURCE_OTHER;
        request.main_frame=0;
        request.body=body.empty()?nullptr:body.data();
        request.body_length=body.size();
        neoastra_result_t provider_result=NEOASTRA_ERROR_NATIVE_FAILURE;
        try{provider_result=scheme->provider(scheme->provider_context,&request,&response);}catch(...){provider_result=NEOASTRA_ERROR_NATIVE_FAILURE;}
        if(provider_result!=NEOASTRA_OK){
            neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme resource provider failed",provider_result);
            response_guard.release_once();
            response={};response.size=sizeof(response);response.version=1;response.status_code=500;
        }
        if(!neo_valid_resource_response(response)||response.byte_length>G_MAXSIZE||response.byte_length>G_MAXINT64||
            (response.content_length!=UINT64_MAX&&response.content_length>G_MAXINT64)){
            neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme resource provider returned an invalid response");
            fail_uri_scheme_request(native_request,"The custom-scheme provider returned an invalid response.");
            return;
        }

        const auto reason=neo_string(response.reason_phrase);
        const auto mime=neo_string(response.mime_type);
        auto native_headers=make_response_headers(response);
        g_object_ptr<GInputStream> stream;
        gint64 stream_length{};
        if(response.body_kind==NEOASTRA_RESOURCE_BODY_BYTES){
            g_bytes_ptr bytes(g_bytes_new(response.bytes,static_cast<gsize>(response.byte_length)));
            stream.reset(g_memory_input_stream_new_from_bytes(bytes.get()));
            stream_length=static_cast<gint64>(response.byte_length);
        }else if(response.body_kind==NEOASTRA_RESOURCE_BODY_FILE){
            const auto path=neo_string(response.file_path);
            g_object_ptr<GFile> file(g_file_new_for_path(path.c_str()));
            GError* file_error{};
            stream.reset(G_INPUT_STREAM(g_file_read(file.get(),nullptr,&file_error)));
            if(!stream){
                neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Could not open a custom-scheme file response",file_error?file_error->code:0);
                if(file_error){webkit_uri_scheme_request_finish_error(native_request,file_error);g_error_free(file_error);}
                else fail_uri_scheme_request(native_request,"Could not open the custom-scheme file response.");
                return;
            }
            stream_length=response.content_length==UINT64_MAX?-1:static_cast<gint64>(response.content_length);
        }else{
            stream.reset(g_memory_input_stream_new());
            stream_length=0;
        }

        g_object_ptr<WebKitURISchemeResponse> native_response(webkit_uri_scheme_response_new(stream.get(),stream_length));
        if(!native_response){fail_uri_scheme_request(native_request,"WebKitGTK could not create the custom-scheme response.");return;}
        webkit_uri_scheme_response_set_status(native_response.get(),response.status_code,reason.empty()?nullptr:reason.c_str());
        if(!mime.empty())webkit_uri_scheme_response_set_content_type(native_response.get(),mime.c_str());
        // WebKitURISchemeResponse takes ownership of the SoupMessageHeaders.
        webkit_uri_scheme_response_set_http_headers(native_response.get(),native_headers.release());
        webkit_uri_scheme_request_finish_with_response(native_request,native_response.get());
    }catch(...){
        neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme request handling failed");
        fail_uri_scheme_request(native_request,"Custom-scheme request handling failed.");
    }
}

bool register_custom_schemes(neoastra_environment_t* environment,WebKitWebContext* context,neoastra_error_t** error) noexcept {
    if(!environment||!context)return false;
    for(const auto& scheme:environment->custom_schemes){
        if((scheme.flags&NEOASTRA_CUSTOM_SCHEME_SERVICE_WORKERS)!=0){
            neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WebKitGTK custom schemes do not support service workers",0,"webkitgtk");
            return false;
        }
    }
    auto* security=webkit_web_context_get_security_manager(context);
    for(const auto& scheme:environment->custom_schemes){
        // A host can retain the WebKit context after disposing NeoAstra's environment. WebKit owns this token; the backend
        // invalidates its weak environment pointer on teardown instead of leaving a callback with freed userdata.
        auto registration=std::unique_ptr<gtk_scheme_context>(new(std::nothrow) gtk_scheme_context{environment});
        if(!registration){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK scheme callback allocation failed");return false;}
        try{static_cast<gtk_environment*>(environment->platform)->schemes.push_back(registration.get());}
        catch(...){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK scheme callback allocation failed");return false;}
        webkit_web_context_register_uri_scheme(context,scheme.name.c_str(),uri_scheme_requested,registration.release(),[](void* value){delete static_cast<gtk_scheme_context*>(value);});
        if((scheme.flags&NEOASTRA_CUSTOM_SCHEME_SECURE)!=0)webkit_security_manager_register_uri_scheme_as_secure(security,scheme.name.c_str());
        if((scheme.flags&NEOASTRA_CUSTOM_SCHEME_CORS_ENABLED)!=0)webkit_security_manager_register_uri_scheme_as_cors_enabled(security,scheme.name.c_str());
    }
    return true;
}

GtkWidget* view_parent(neoastra_view_t* view) noexcept {
    if (view->window) { auto* state=static_cast<gtk_window*>(view->window->platform); return state?state->widget:nullptr; }
    return view->parent.kind==NEOASTRA_NATIVE_PARENT_GTK_WIDGET?static_cast<GtkWidget*>(view->parent.handle):nullptr;
}

// A GTK window has one child, and a window can host several views. They share a GtkOverlay, the view stack, that the
// window keeps for as long as it lives: every view fills it, and a newer view is drawn over an older one, as on the
// other platforms. The stack is told apart from other widgets by the data it carries under this key.
constexpr const char* view_stack_key="neoastra.view-stack";
// The menu presenter of the managed side (LinuxMenus.cs) puts a box between a window and its content, for a menu bar
// above the content, and marks that box with this key. The stack is then looked for, and created, inside the box.
constexpr const char* menu_host_key="neoastra.menu-host";

bool is_view_stack(GtkWidget* widget) noexcept { return widget&&GTK_IS_OVERLAY(widget)&&g_object_get_data(G_OBJECT(widget),view_stack_key); }

// Returns the view stack of a window, which it creates for the first view of the window.
GtkWidget* window_view_stack(GtkWindow* window) noexcept {
    auto* child=gtk_window_get_child(window);
    if(is_view_stack(child))return child;
    const bool menu_host=child&&GTK_IS_BOX(child)&&g_object_get_data(G_OBJECT(child),menu_host_key);
    if(menu_host){for(auto* item=gtk_widget_get_first_child(child);item;item=gtk_widget_get_next_sibling(item))if(is_view_stack(item))return item;}
    auto* stack=gtk_overlay_new();
    g_object_set_data(G_OBJECT(stack),view_stack_key,stack);
    gtk_widget_set_hexpand(stack,TRUE);
    gtk_widget_set_vexpand(stack,TRUE);
    // Anything else that the window holds is replaced, as it was when a view became the child of the window itself.
    if(menu_host)gtk_box_append(GTK_BOX(child),stack);else gtk_window_set_child(window,stack);
    return stack;
}

void release_dispatch_app(void* data) { static_cast<neoastra_app_t*>(data)->release(); }
gboolean dispatch_on_main(void* data) {
    auto* app=static_cast<neoastra_app_t*>(data);
    GSource* source{};
    {
        std::lock_guard lock(app->platform_mutex);
        auto* state=static_cast<gtk_app*>(app->platform);
        if(state){source=state->wake_source;state->wake_source=nullptr;}
    }
    if(source)g_source_unref(source);
    // The rest of a busy queue waits for a new idle source, which runs after the input and drawing of the loop.
    if(neo_drain_dispatch_slice(app))neo_wake_app(app);
    return G_SOURCE_REMOVE;
}
gboolean destroy_app_on_main(void* data) { neo_destroy_app_on_ui(static_cast<neoastra_app_t*>(data)); return G_SOURCE_REMOVE; }
gboolean decision_timed_out(void* data){static_cast<neoastra_decision_t*>(data)->expire();return G_SOURCE_REMOVE;}
void release_timed_decision(void* data){static_cast<neoastra_decision_t*>(data)->release();}

// A window has the size it was asked for until it is laid out on its surface. From then on it has the size of its widget, which
// is the frame without the shadow around it: a screen, a size limit, or the user may have given it another size than it was asked for.
void window_laid_out(GdkSurface*,int,int,void* data){
    auto* window=static_cast<neoastra_window_t*>(data);neo_retain_guard owner(window);if(!owner)return;auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return;
    const auto width=gtk_widget_get_width(state->widget),height=gtk_widget_get_height(state->widget);if(width<=0||height<=0)return;
    bool changed{};{std::lock_guard lock(window->state_mutex);changed=window->bounds.width!=width||window->bounds.height!=height;window->bounds.width=width;window->bounds.height=height;}
    if(changed)neo_emit_app(window->app,NEOASTRA_EVENT_WINDOW_RESIZED,window->id);
}
void unwatch_window_size(gtk_window* state) noexcept {if(!state->surface)return;g_signal_handler_disconnect(state->surface,state->laid_out);if(state->entered_monitor)g_signal_handler_disconnect(state->surface,state->entered_monitor);if(state->left_monitor)g_signal_handler_disconnect(state->surface,state->left_monitor);g_object_unref(state->surface);state->surface=nullptr;state->laid_out=0;state->entered_monitor=0;state->left_monitor=0;}
// The surface of a window enters and leaves the monitors it is shown on.
void window_monitor_changed(GdkSurface*,GdkMonitor*,void* data){neo_window_display_changed(static_cast<neoastra_window_t*>(data));}
// The surface says when a size is settled, after the window gave its widgets their place. A window gets its surface when it is first
// shown and keeps it while hidden, and the surface is held so that the handler can be removed whatever became of the window.
void window_realized(GtkWidget* widget,void* data){
    auto* window=static_cast<neoastra_window_t*>(data);neo_retain_guard owner(window);if(!owner)return;auto* state=static_cast<gtk_window*>(window->platform);if(!state)return;
    auto* surface=gtk_native_get_surface(GTK_NATIVE(widget));if(surface==state->surface)return;
    unwatch_window_size(state);if(!surface)return;
    state->surface=GDK_SURFACE(g_object_ref(surface));state->laid_out=g_signal_connect_after(surface,"layout",G_CALLBACK(window_laid_out),window);
    state->entered_monitor=g_signal_connect(surface,"enter-monitor",G_CALLBACK(window_monitor_changed),window);state->left_monitor=g_signal_connect(surface,"leave-monitor",G_CALLBACK(window_monitor_changed),window);
}
// GTK counts a display in whole pixels for each logical unit.
void window_scale_changed(GObject* object,GParamSpec*,void* data){auto* window=static_cast<neoastra_window_t*>(data);neo_retain_guard owner(window);if(!owner)return;neo_window_set_scale_factor(window,gtk_widget_get_scale_factor(GTK_WIDGET(object)));}
gboolean window_close_requested(GtkWindow* native,void* data){auto* window=static_cast<neoastra_window_t*>(data);neo_retain_guard owner(window);if(!owner)return FALSE;if(window->force_closing){window->force_closing=false;auto* state=static_cast<gtk_window*>(window->platform);if(state){unwatch_window_size(state);if(state->widget)g_object_remove_weak_pointer(G_OBJECT(state->widget),reinterpret_cast<void**>(&state->widget));state->widget=nullptr;}g_signal_handlers_disconnect_by_data(native,window);neo_window_closed(window);return FALSE;}neo_window_request_close(window,NEOASTRA_WINDOW_CLOSE_USER,true);return TRUE;}
void window_focus_changed(GObject* object,GParamSpec*,void* data){auto* window=static_cast<neoastra_window_t*>(data);neo_retain_guard owner(window);if(!owner)return;neo_emit_app(window->app,NEOASTRA_EVENT_WINDOW_FOCUS_CHANGED,window->id,nullptr,nullptr,gtk_window_is_active(GTK_WINDOW(object))?1u:0u);}
void window_state_changed(GObject* object,GParamSpec*,void* data){auto* window=static_cast<neoastra_window_t*>(data);neo_retain_guard owner(window);if(!owner)return;auto* value=GTK_WINDOW(object);const auto state=gtk_window_is_fullscreen(value)?NEOASTRA_WINDOW_FULLSCREEN:gtk_window_is_maximized(value)?NEOASTRA_WINDOW_MAXIMIZED:gtk_window_is_suspended(value)?NEOASTRA_WINDOW_MINIMIZED:NEOASTRA_WINDOW_NORMAL;auto* native=static_cast<gtk_window*>(window->platform);const auto changed=native&&native->reported_state!=state;if(native)native->reported_state=state;{std::lock_guard lock(window->state_mutex);window->state=state;}if(changed)neo_emit_app(window->app,NEOASTRA_EVENT_WINDOW_STATE_CHANGED,window->id,nullptr,nullptr,state);}

// WebKitGTK tells the user gesture a request was made in, which a script keeps while it handles a click, and the redirect of
// a request apart from the request.
uint64_t link_activation(WebKitNavigationAction* action,uint64_t user_initiated) {
    const bool activated=webkit_navigation_action_get_navigation_type(action)==WEBKIT_NAVIGATION_TYPE_LINK_CLICKED;
    const bool by_user=webkit_navigation_action_is_user_gesture(action)&&!webkit_navigation_action_is_redirect(action);
    return NEOASTRA_REQUEST_LINK_ACTIVATION_REPORTED|(activated?NEOASTRA_REQUEST_LINK_ACTIVATED:0)|(by_user?user_initiated:0);
}
// WebKitGTK names the reload and the history navigation of a submitted form with one type, which does not tell the kind.
uint64_t navigation_kind(WebKitNavigationAction* action) {
    switch(webkit_navigation_action_get_navigation_type(action)){
        case WEBKIT_NAVIGATION_TYPE_BACK_FORWARD:return NEOASTRA_NAVIGATION_REQUEST_KIND_BACK_FORWARD;
        case WEBKIT_NAVIGATION_TYPE_RELOAD:return NEOASTRA_NAVIGATION_REQUEST_KIND_RELOAD;
        case WEBKIT_NAVIGATION_TYPE_FORM_RESUBMITTED:return NEOASTRA_NAVIGATION_REQUEST_KIND_UNKNOWN;
        default:return NEOASTRA_NAVIGATION_REQUEST_KIND_NEW_DOCUMENT;
    }
}
struct navigation_context { WebKitPolicyDecision* policy{}; std::string uri; };
void navigation_decided(void* pointer, const neoastra_decision_response_t* response) noexcept { std::unique_ptr<navigation_context> context(static_cast<navigation_context*>(pointer)); if(response->action==NEOASTRA_DECISION_ALLOW||response->action==NEOASTRA_DECISION_DEFAULT)webkit_policy_decision_use(context->policy);else webkit_policy_decision_ignore(context->policy);g_object_unref(context->policy); }
// A page that asks for a microphone and a camera together makes one request, which WebKitGTK answers once. The host is asked
// about each device, as WebView2 asks, and the page gets both when the host allows both.
struct permission_context { WebKitPermissionRequest* request{}; int pending{1}; bool allowed{true}; };
void permission_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {
    auto* context=static_cast<permission_context*>(pointer);
    if(response->action!=NEOASTRA_DECISION_ALLOW)context->allowed=false;
    if(--context->pending>0)return;
    if(context->allowed)webkit_permission_request_allow(context->request);else webkit_permission_request_deny(context->request);
    g_object_unref(context->request);delete context;
}
struct new_window_context { neoastra_view_t* view{}; std::string uri; };
void new_window_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<new_window_context> context(static_cast<new_window_context*>(pointer));auto* state=static_cast<gtk_view*>(context->view->platform);if(response->action==NEOASTRA_DECISION_ALLOW&&!response->target_view&&state&&state->widget)webkit_web_view_load_uri(WEBKIT_WEB_VIEW(state->widget),context->uri.c_str());}
void finish_synchronous_decision(neoastra_decision_t* decision);
// The origin of the page that a view shows, or nothing for a page without one.
std::string page_origin(WebKitWebView* web_view){
    const char* raw=webkit_web_view_get_uri(web_view);GUri* uri=raw?g_uri_parse(raw,G_URI_FLAGS_NONE,nullptr):nullptr;if(!uri)return {};
    std::string origin;const char* scheme=g_uri_get_scheme(uri);const char* host=g_uri_get_host(uri);
    if(scheme&&host&&*host){
        const std::string name=host;const int port=g_uri_get_port(uri);
        origin=std::string(scheme)+"://"+(name.find(':')!=std::string::npos?"["+name+"]":name);
        if(port>0&&!((origin.rfind("http://",0)==0&&port==80)||(origin.rfind("https://",0)==0&&port==443)))origin+=":"+std::to_string(port);
    }
    g_uri_unref(uri);return origin;
}
// WebKitGTK does not tell which origin asks for a capture. It asks only while the view still shows the page of the top document
// that wants it, so that the origin of that page is given, as WKWebView gives the origin of the top document. The other kinds of
// request come without that guarantee, and without an origin. A request for the screen is told apart from one for a microphone.
gboolean permission_requested(WebKitWebView* web_view,WebKitPermissionRequest* request,void* data){
    auto* view=static_cast<neoastra_view_t*>(data);
    neo_retain_guard owner(view);if(!owner){webkit_permission_request_deny(request);return TRUE;}
    neoastra_permission_kind_t kinds[2]={NEOASTRA_PERMISSION_UNKNOWN,NEOASTRA_PERMISSION_UNKNOWN};int count=1;std::string origin;
    if(WEBKIT_IS_GEOLOCATION_PERMISSION_REQUEST(request))kinds[0]=NEOASTRA_PERMISSION_GEOLOCATION;
    else if(WEBKIT_IS_NOTIFICATION_PERMISSION_REQUEST(request))kinds[0]=NEOASTRA_PERMISSION_NOTIFICATIONS;
    else if(WEBKIT_IS_USER_MEDIA_PERMISSION_REQUEST(request)){
        auto* media=WEBKIT_USER_MEDIA_PERMISSION_REQUEST(request);
        const bool video=webkit_user_media_permission_is_for_video_device(media);
        if(webkit_user_media_permission_is_for_display_device(media))kinds[0]=NEOASTRA_PERMISSION_SCREEN_CAPTURE;
        else if(video&&webkit_user_media_permission_is_for_audio_device(media)){kinds[0]=NEOASTRA_PERMISSION_MICROPHONE;kinds[1]=NEOASTRA_PERMISSION_CAMERA;count=2;}
        else kinds[0]=video?NEOASTRA_PERMISSION_CAMERA:NEOASTRA_PERMISSION_MICROPHONE;
        origin=page_origin(web_view);
    }
    neoastra_decision* decisions[2]={};
    auto* context=new(std::nothrow) permission_context{WEBKIT_PERMISSION_REQUEST(g_object_ref(request)),count};
    bool allocated=context!=nullptr;
    for(int index=0;index<count;++index){decisions[index]=new(std::nothrow) neoastra_decision;allocated=allocated&&decisions[index];}
    if(!allocated){for(auto* decision:decisions)delete decision;if(context){g_object_unref(context->request);delete context;}webkit_permission_request_deny(request);return TRUE;}
    // Every decision is answered once, by the host or by its default, and the last answer settles the request.
    for(int index=0;index<count;++index){
        auto* decision=decisions[index];
        neo_configure_decision(decision,view,NEOASTRA_DECISION_PERMISSION,NEOASTRA_DECISION_DENY);decision->completion=permission_decided;decision->completion_context=context;
        neo_emit_view(view,NEOASTRA_EVENT_PERMISSION_REQUESTED,0,nullptr,origin.empty()?nullptr:&origin,kinds[index],0,decision);
        neo_finish_decision_event(view,decision);decision->release();
    }
    return TRUE;
}
// WebKit keeps the returned view as a plain pointer and never unreferences it: return the target that its host owns.
WebKitWebView* create_web_view(WebKitWebView*,WebKitNavigationAction* action,void* data){auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return nullptr;auto* request=webkit_navigation_action_get_request(action);std::string uri=request&&webkit_uri_request_get_uri(request)?webkit_uri_request_get_uri(request):"";const char* raw_name=webkit_navigation_action_get_frame_name(action);std::string name=raw_name?raw_name:"";auto* decision=new(std::nothrow) neoastra_decision;auto* context=new(std::nothrow) new_window_context{view,uri};if(!decision||!context){delete decision;delete context;return nullptr;}neo_configure_decision(decision,view,NEOASTRA_DECISION_NEW_WINDOW,NEOASTRA_DECISION_CANCEL);decision->completion=new_window_decided;decision->completion_context=context;decision->external_uri=uri;neo_emit_view(view,NEOASTRA_EVENT_NEW_WINDOW_REQUESTED,0,&name,&uri,link_activation(action,NEOASTRA_NEW_WINDOW_REQUEST_USER_INITIATED),0,decision);finish_synchronous_decision(decision);WebKitWebView* target{};if(decision->resolved_target){auto* target_state=static_cast<gtk_view*>(decision->resolved_target->platform);if(target_state&&target_state->widget)target=WEBKIT_WEB_VIEW(target_state->widget);}decision->release();return target;}
gboolean decide_policy(WebKitWebView*, WebKitPolicyDecision* policy, WebKitPolicyDecisionType type, void* data) {
    if(type!=WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION)return FALSE;
    auto* view=static_cast<neoastra_view_t*>(data);
    neo_retain_guard owner(view);if(!owner){webkit_policy_decision_ignore(policy);return TRUE;}
    auto* navigation=WEBKIT_NAVIGATION_POLICY_DECISION(policy);
    auto* action=webkit_navigation_policy_decision_get_navigation_action(navigation);
    const uint64_t kind=navigation_kind(action);
    if(neo_refuses_navigation(view,kind)){webkit_policy_decision_ignore(policy);return TRUE;}
    auto* request=webkit_navigation_action_get_request(action);
    std::string uri=webkit_uri_request_get_uri(request)?webkit_uri_request_get_uri(request):"";
    auto* decision=new neoastra_decision;
    neo_configure_decision(decision,view,NEOASTRA_DECISION_NAVIGATION,NEOASTRA_DECISION_ALLOW);
    auto* context=new navigation_context{WEBKIT_POLICY_DECISION(g_object_ref(policy)),uri};decision->completion=navigation_decided;decision->completion_context=context;decision->external_uri=uri;
    const uint64_t flags=NEOASTRA_NAVIGATION_REQUEST_MAIN_FRAME|link_activation(action,NEOASTRA_NAVIGATION_REQUEST_USER_INITIATED)|kind;
    neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_REQUESTED,0,nullptr,&uri,flags,0,decision);
    neo_finish_decision_event(view,decision);
    decision->release();return TRUE;
}
// These signals cannot wait. Completing the fallback consumes the native deferral reference, never the host's independent
// retained reference: a late managed response must still be able to observe InvalidState and release its own handle safely.
void finish_synchronous_decision(neoastra_decision_t* decision) {const auto state=decision->state.load(std::memory_order_acquire);if(state==neo_decision_state::pending||state==neo_decision_state::deferred){neoastra_decision_response_t response{};response.size=sizeof(response);response.version=1;response.action=decision->default_action;neoastra_decision_complete(decision,&response,nullptr);}}
// The context holds a reference on its dialog. WebKitGTK closes a dialog decided inside the signal itself; one decided
// later stays open, with the page waiting, until it is closed here.
struct script_dialog_context { WebKitScriptDialog* dialog{}; bool deferred{}; };
void script_dialog_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<script_dialog_context> context(static_cast<script_dialog_context*>(pointer));if(response->action==NEOASTRA_DECISION_ALLOW){const auto type=webkit_script_dialog_get_dialog_type(context->dialog);if(type==WEBKIT_SCRIPT_DIALOG_CONFIRM||type==WEBKIT_SCRIPT_DIALOG_BEFORE_UNLOAD_CONFIRM)webkit_script_dialog_confirm_set_confirmed(context->dialog,TRUE);else if(type==WEBKIT_SCRIPT_DIALOG_PROMPT){try{auto text=neo_string(response->text);webkit_script_dialog_prompt_set_text(context->dialog,text.c_str());}catch(...){}}}if(context->deferred)webkit_script_dialog_close(context->dialog);webkit_script_dialog_unref(context->dialog);}
gboolean script_dialog(WebKitWebView*,WebKitScriptDialog* dialog,void* data){auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return TRUE;if(auto* state=static_cast<gtk_view*>(view->platform);state&&state->default_script_dialogs)return FALSE;const auto native=webkit_script_dialog_get_dialog_type(dialog);const auto kind=native==WEBKIT_SCRIPT_DIALOG_ALERT?NEOASTRA_SCRIPT_DIALOG_ALERT:native==WEBKIT_SCRIPT_DIALOG_CONFIRM?NEOASTRA_SCRIPT_DIALOG_CONFIRM:native==WEBKIT_SCRIPT_DIALOG_PROMPT?NEOASTRA_SCRIPT_DIALOG_PROMPT:NEOASTRA_SCRIPT_DIALOG_BEFORE_UNLOAD;std::string message=webkit_script_dialog_get_message(dialog)?webkit_script_dialog_get_message(dialog):"";std::string default_text;if(native==WEBKIT_SCRIPT_DIALOG_PROMPT){const char* text=webkit_script_dialog_prompt_get_default_text(dialog);if(text)default_text=text;}auto* decision=new neoastra_decision;auto* context=new script_dialog_context{webkit_script_dialog_ref(dialog)};neo_configure_decision(decision,view,NEOASTRA_DECISION_SCRIPT_DIALOG,kind==NEOASTRA_SCRIPT_DIALOG_ALERT?NEOASTRA_DECISION_ALLOW:NEOASTRA_DECISION_CANCEL);decision->completion=script_dialog_decided;decision->completion_context=context;neo_event_details details{};details.text2=&default_text;neo_emit_view_detailed(view,NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED,0,&message,&view->source,kind,0,decision,details);neo_finish_decision_event(view,decision);
    // A decision that is still deferred has not run its completion, so its context is alive and is told to close the dialog.
    if(decision->state.load(std::memory_order_acquire)==neo_decision_state::deferred)context->deferred=true;decision->release();return TRUE;}
struct chooser_context { WebKitFileChooserRequest* request{}; };
void chooser_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<chooser_context> context(static_cast<chooser_context*>(pointer));if(response->action!=NEOASTRA_DECISION_ALLOW){webkit_file_chooser_request_cancel(context->request);return;}try{std::vector<std::string> storage;std::vector<const char*> paths;storage.reserve(response->path_count);paths.reserve(static_cast<size_t>(response->path_count)+1);for(uint32_t i=0;i<response->path_count;++i)storage.push_back(neo_string(response->paths[i]));for(const auto& path:storage)paths.push_back(path.c_str());paths.push_back(nullptr);webkit_file_chooser_request_select_files(context->request,paths.data());}catch(...){webkit_file_chooser_request_cancel(context->request);}}
gboolean run_file_chooser(WebKitWebView*,WebKitFileChooserRequest* request,void* data){auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner){webkit_file_chooser_request_cancel(request);return TRUE;}std::string accepted;for(auto* current=webkit_file_chooser_request_get_mime_types(request);current&&*current;++current){if(!accepted.empty())accepted.push_back(';');accepted+=*current;}auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_FILE_CHOOSER,NEOASTRA_DECISION_CANCEL);decision->completion=chooser_decided;decision->completion_context=new chooser_context{request};neo_emit_view(view,NEOASTRA_EVENT_FILE_CHOOSER_REQUESTED,0,&accepted,nullptr,webkit_file_chooser_request_get_select_multiple(request)?1u:0u,0,decision);finish_synchronous_decision(decision);decision->release();return TRUE;}
struct auth_context { WebKitAuthenticationRequest* request{}; };
void auth_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<auth_context> context(static_cast<auth_context*>(pointer));if(response->action==NEOASTRA_DECISION_ALLOW){try{auto user=neo_string(response->text),password=neo_string(response->secondary_text);auto* credential=webkit_credential_new(user.c_str(),password.c_str(),WEBKIT_CREDENTIAL_PERSISTENCE_NONE);if(credential){webkit_authentication_request_authenticate(context->request,credential);webkit_credential_free(credential);return;}}catch(...){ }webkit_authentication_request_cancel(context->request);}else if(response->action==NEOASTRA_DECISION_CANCEL||response->action==NEOASTRA_DECISION_DENY)webkit_authentication_request_cancel(context->request);}
gboolean authenticate(WebKitWebView*,WebKitAuthenticationRequest* request,void* data){auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner){webkit_authentication_request_cancel(request);return TRUE;}std::string host=webkit_authentication_request_get_host(request)?webkit_authentication_request_get_host(request):"";std::string realm=webkit_authentication_request_get_realm(request)?webkit_authentication_request_get_realm(request):"";auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_AUTHENTICATION,NEOASTRA_DECISION_DEFAULT);decision->completion=auth_decided;decision->completion_context=new auth_context{request};neo_event_details details{};details.text2=&realm;neo_emit_view_detailed(view,NEOASTRA_EVENT_AUTHENTICATION_REQUESTED,0,&host,nullptr,0,webkit_authentication_request_get_port(request),decision,details);finish_synchronous_decision(decision);const auto handled=decision->resolved_action.load()!=NEOASTRA_DECISION_DEFAULT;decision->release();return handled;}
struct fullscreen_context { WebKitWebView* webview{}; };
void fullscreen_decided(void* pointer,const neoastra_decision_response_t*) noexcept {delete static_cast<fullscreen_context*>(pointer);}
gboolean enter_fullscreen(WebKitWebView* webview,void* data){auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return TRUE;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_FULLSCREEN,NEOASTRA_DECISION_DENY);decision->completion=fullscreen_decided;decision->completion_context=new fullscreen_context{webview};neo_emit_view(view,NEOASTRA_EVENT_FULLSCREEN_REQUESTED,0,nullptr,&view->source,1,0,decision);finish_synchronous_decision(decision);const auto allow=decision->resolved_action.load()==NEOASTRA_DECISION_ALLOW;decision->release();return allow?FALSE:TRUE;}
struct gtk_download {
    WebKitDownload* value{};
    gulong destination{},received{},finished{},failed{};
    neoastra_decision_t* pending_destination{}; // Retained until resolution or download teardown.
    bool cancel_requested{};
};
struct decision_deleter { void operator()(neoastra_decision_t* value) const noexcept { if(value)value->release(); } };
void abandon_download_destination(gtk_download* state) noexcept {
    if(auto* decision=std::exchange(state->pending_destination,nullptr)){decision->abandon();decision->release();}
}
neoastra_result_t gtk_download_command(neoastra_download_t* download,uint32_t command) noexcept {
    // Destruction also calls this command at zero references: it must still cancel the engine, without resurrecting the owner.
    neo_retain_guard owner(download);
    neo_retain_guard view_owner(download->view);
    auto* state=static_cast<gtk_download*>(download->platform);
    if(!state||!state->value)return NEOASTRA_ERROR_DISPOSED;
    if(command!=0)return NEOASTRA_ERROR_NOT_SUPPORTED;
    if(state->cancel_requested)return NEOASTRA_OK;
    g_object_ptr<WebKitDownload> native(WEBKIT_DOWNLOAD(g_object_ref(state->value)));
    state->cancel_requested=true;
    abandon_download_destination(state);
    webkit_download_cancel(native.get());
    return NEOASTRA_OK;
}
void destroy_gtk_download(neoastra_download_t* download) noexcept {
    auto* state=static_cast<gtk_download*>(download->platform);
    if(!state)return;
    download->platform=nullptr;
    abandon_download_destination(state);
    if(state->value){
        if(state->destination)g_signal_handler_disconnect(state->value,state->destination);
        if(state->received)g_signal_handler_disconnect(state->value,state->received);
        if(state->finished)g_signal_handler_disconnect(state->value,state->finished);
        if(state->failed)g_signal_handler_disconnect(state->value,state->failed);
        g_object_unref(state->value);
    }
    delete state;
}
struct download_destination_context {
    neoastra_download_t* download;
    std::string suggested_filename;
    download_destination_context(neoastra_download_t* value,const std::string& suggested) : download(value),suggested_filename(suggested) { download->retain(); }
    ~download_destination_context() { download->release(); }
};
void download_destination_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {
    std::unique_ptr<download_destination_context> context(static_cast<download_destination_context*>(pointer));
    auto* download=context->download; // The context owns this reference, including after cancellation destroys the platform.
    neo_retain_guard view_owner(download->view);
    auto* state=static_cast<gtk_download*>(download->platform);
    if(!state)return;
    if(auto* decision=std::exchange(state->pending_destination,nullptr))decision->release();
    if(state->cancel_requested||download->state.load()!=NEOASTRA_DOWNLOAD_REQUESTED)return;
    g_object_ptr<WebKitDownload> native(WEBKIT_DOWNLOAD(g_object_ref(state->value)));
    if(!view_owner||(response->action!=NEOASTRA_DECISION_DOWNLOAD&&response->action!=NEOASTRA_DECISION_ALLOW&&response->action!=NEOASTRA_DECISION_DEFAULT)){
        state->cancel_requested=true;
        webkit_download_cancel(native.get());
        return;
    }
    try{
        std::string destination;
        if(response->action==NEOASTRA_DECISION_DOWNLOAD)destination=neo_string(response->text);
        else if(const auto* existing=webkit_download_get_destination(native.get());existing&&*existing)destination=existing;
        else{
            // Match WebKit's default even when the response arrives after the signal: Downloads (or HOME), with directory
            // separators in the suggested filename replaced. Returning FALSE later cannot invoke the signal's default handler.
            auto filename=context->suggested_filename;
            std::replace(filename.begin(),filename.end(),G_DIR_SEPARATOR,'_');
            const auto* directory=g_get_user_special_dir(G_USER_DIRECTORY_DOWNLOAD);
            if(!directory)directory=g_get_home_dir();
            std::unique_ptr<gchar,decltype(&g_free)> path(g_build_filename(directory,filename.c_str(),nullptr),g_free);
            if(path)destination=path.get();
        }
        // API 6.0 takes an absolute filesystem path, not the file URI used by the older WebKitGTK APIs. Invalid paths must
        // cancel rather than leave WebKit waiting forever after returning TRUE from decide-destination.
        if(destination.empty()||destination.find('\0')!=std::string::npos||!g_path_is_absolute(destination.c_str())){
            state->cancel_requested=true;
            webkit_download_cancel(native.get());
            return;
        }
        download->destination_path=std::move(destination);
        download->state.store(NEOASTRA_DOWNLOAD_IN_PROGRESS);
        webkit_download_set_destination(native.get(),download->destination_path.c_str());
        if(view_owner&&download->state.load()==NEOASTRA_DOWNLOAD_IN_PROGRESS)neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_STARTED);
    }catch(...){webkit_download_cancel(native.get());}
}
gboolean download_decide_destination(WebKitDownload* native,const gchar* suggested,void* data) noexcept {
    auto* download=static_cast<neoastra_download_t*>(data);
    neo_retain_guard owner(download);
    if(!owner)return TRUE;
    neo_retain_guard view_owner(download->view);
    g_object_ptr<WebKitDownload> native_owner(WEBKIT_DOWNLOAD(g_object_ref(native)));
    if(!view_owner||download->state.load()!=NEOASTRA_DOWNLOAD_REQUESTED){webkit_download_cancel(native);return TRUE;}
    try{
        const std::string filename=suggested?suggested:"";
        std::unique_ptr<neoastra_decision,decision_deleter> decision(new neoastra_decision);
        auto context=std::make_unique<download_destination_context>(download,filename);
        neo_configure_decision(decision.get(),download->view,NEOASTRA_DECISION_DOWNLOAD_REQUEST,NEOASTRA_DECISION_CANCEL);
        decision->completion=download_destination_decided;
        decision->completion_context=context.release();
        auto* state=static_cast<gtk_download*>(download->platform);
        decision->retain();state->pending_destination=decision.get();
        neo_event_details details{};details.value2=1;details.download=download;
        download->event_published=true;
        neo_emit_view_detailed(download->view,NEOASTRA_EVENT_DOWNLOAD_REQUESTED,download->id,&filename,&download->source_uri,UINT64_MAX,0,decision.get(),details);
        neo_finish_decision_event(download->destruction_app,decision.get());
        // WebKitGTK 6.0 (since 2.40) waits after TRUE until set_destination or cancel, including when the host defers.
        return TRUE;
    }catch(...){webkit_download_cancel(native);return TRUE;}
}
void download_received(WebKitDownload* native,guint64 length,void* data) noexcept {
    auto* download=static_cast<neoastra_download_t*>(data);
    neo_retain_guard owner(download);if(!owner)return;
    neo_retain_guard view_owner(download->view);
    try{if(download->destination_path.empty()){if(const auto* destination=webkit_download_get_destination(native))download->destination_path=destination;}}catch(...){ }
    download->bytes_received.fetch_add(length);
    if(view_owner)neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED);
}
void finish_gtk_download(neoastra_download_t* download,neoastra_download_state_t terminal,const GError* error=nullptr) noexcept {
    neo_retain_guard owner(download);if(!owner)return;
    neo_retain_guard view_owner(download->view);
    auto expected=NEOASTRA_DOWNLOAD_IN_PROGRESS;
    if(!download->state.compare_exchange_strong(expected,terminal)){expected=NEOASTRA_DOWNLOAD_REQUESTED;if(!download->state.compare_exchange_strong(expected,terminal))return;}
    if(auto* state=static_cast<gtk_download*>(download->platform))abandon_download_destination(state);
    if(error){try{download->failure_reason=error->message?error->message:"";}catch(...){}}
    if(view_owner)neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_COMPLETED);
    download->release_lifecycle();
}
void download_finished(WebKitDownload*,void* data){finish_gtk_download(static_cast<neoastra_download_t*>(data),NEOASTRA_DOWNLOAD_COMPLETED);}
void download_failed(WebKitDownload*,GError* error,void* data){finish_gtk_download(static_cast<neoastra_download_t*>(data),error&&g_error_matches(error,WEBKIT_DOWNLOAD_ERROR,WEBKIT_DOWNLOAD_ERROR_CANCELLED_BY_USER)?NEOASTRA_DOWNLOAD_CANCELED:NEOASTRA_DOWNLOAD_FAILED,error);}
void download_started(WebKitNetworkSession*,WebKitDownload* native,void*) noexcept {
    // Several environments can share one session, each with a download-started handler. There is still only one policy
    // and one tracked lifecycle for a native download. This boolean tag owns no NeoAstra pointer and cannot become stale.
    constexpr const char* tracked_key="neoastra.download-tracked";
    if(g_object_get_data(G_OBJECT(native),tracked_key))return;
    g_object_set_data(G_OBJECT(native),tracked_key,GINT_TO_POINTER(1));
    g_object_ptr<WebKitDownload> native_owner(WEBKIT_DOWNLOAD(g_object_ref(native)));
    auto* webview=webkit_download_get_web_view(native);
    auto* view=webview?static_cast<neoastra_view_t*>(g_object_get_data(G_OBJECT(webview),"neoastra.native-view")):nullptr;
    neo_retain_guard view_owner(view);
    if(!view_owner){webkit_download_cancel(native);return;}
    try{
        auto download=std::make_unique<neoastra_download>(view);
        auto state=std::make_unique<gtk_download>();
        download->platform=state.release();download->command=gtk_download_command;download->platform_destroy=destroy_gtk_download;
        auto* platform=static_cast<gtk_download*>(download->platform);
        platform->value=WEBKIT_DOWNLOAD(g_object_ref(native));
        auto* request=webkit_download_get_request(native);
        download->source_uri=request&&webkit_uri_request_get_uri(request)?webkit_uri_request_get_uri(request):"";
        platform->destination=g_signal_connect(native,"decide-destination",G_CALLBACK(download_decide_destination),download.get());
        platform->received=g_signal_connect(native,"received-data",G_CALLBACK(download_received),download.get());
        platform->finished=g_signal_connect(native,"finished",G_CALLBACK(download_finished),download.get());
        platform->failed=g_signal_connect(native,"failed",G_CALLBACK(download_failed),download.get());
        if(!platform->destination||!platform->received||!platform->finished||!platform->failed){download.reset();webkit_download_cancel(native);return;}
        download.release();
    }catch(...){webkit_download_cancel(native);}
}
// Reports which directions of the history have an entry to go to, one bit for each, when that has changed.
void report_history(neoastra_view_t* view) noexcept {neo_retain_guard owner(view);if(!owner)return;auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return;auto* web=WEBKIT_WEB_VIEW(state->widget);const uint64_t history=neo_history_directions(view,webkit_web_view_can_go_back(web),webkit_web_view_can_go_forward(web));if(state->history==history)return;state->history=history;neo_emit_view(view,NEOASTRA_EVENT_HISTORY_CHANGED,0,nullptr,nullptr,history);}
void history_changed(WebKitBackForwardList*,WebKitBackForwardListItem*,gpointer,void* data) {report_history(static_cast<neoastra_view_t*>(data));}
// The history is also read as a document commits and before its load is reported as finished, so that a handler of the completed navigation sees the directions it can go in.
void load_changed(WebKitWebView* webview, WebKitLoadEvent event, void* data) { auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return;const char* raw=webkit_web_view_get_uri(webview);std::string uri=raw?raw:"";if(event==WEBKIT_LOAD_STARTED)neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_STARTED,0,nullptr,&uri,1);else if(event==WEBKIT_LOAD_COMMITTED){report_history(view);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_COMMITTED,0,nullptr,&uri);}else if(event==WEBKIT_LOAD_FINISHED){report_history(view);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_COMPLETED,0,nullptr,&uri);} }
gboolean load_failed(WebKitWebView*, WebKitLoadEvent, const char* uri, GError* error, void* data) { auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return FALSE;std::string value=uri?uri:"";neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_FAILED,0,nullptr,&value,NEOASTRA_ERROR_NATIVE_FAILURE,error?error->code:0);return FALSE; }
void title_changed(GObject* object,GParamSpec*,void* data){auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return;const auto* value=webkit_web_view_get_title(WEBKIT_WEB_VIEW(object));view->title=value?value:"";neo_emit_view(view,NEOASTRA_EVENT_TITLE_CHANGED,0,&view->title);}
void uri_changed(GObject* object,GParamSpec*,void* data){auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return;const auto* value=webkit_web_view_get_uri(WEBKIT_WEB_VIEW(object));view->source=value?value:"";neo_emit_view(view,NEOASTRA_EVENT_SOURCE_CHANGED,0,nullptr,&view->source);}
void message_received(WebKitUserContentManager*,JSCValue* value,void* data) noexcept {auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return;char* json{};try{json=jsc_value_to_json(value,0);std::string message=json?json:"null";g_free(json);json=nullptr;const std::string origin;neo_emit_bridge_message(view,message,origin,false);}catch(...){g_free(json);neo_log(view->environment->app,NEOASTRA_LOG_ERROR,"bridge","WebKitGTK web-message handling failed");}}
void web_process_terminated(WebKitWebView*,WebKitWebProcessTerminationReason reason,void* data){auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return;uint64_t value=NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED|NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW;if(reason==WEBKIT_WEB_PROCESS_CRASHED||reason==WEBKIT_WEB_PROCESS_EXCEEDED_MEMORY_LIMIT)value|=NEOASTRA_PROCESS_FAILURE_CRASHED;neo_emit_view(view,NEOASTRA_EVENT_WEB_PROCESS_TERMINATED,0,nullptr,nullptr,value,static_cast<int64_t>(reason));}

struct script_context { neoastra_view_t* view{};neoastra_string_callback_t callback{};void* context{};neoastra_operation_t* operation{}; };
void script_finished(GObject* object,GAsyncResult* result,void* data){std::unique_ptr<script_context> context(static_cast<script_context*>(data));GError* error{};auto* value=webkit_web_view_evaluate_javascript_finish(WEBKIT_WEB_VIEW(object),result,&error);std::string output;neoastra_error_t* native_error{};auto requested=NEOASTRA_OK;if(error){requested=NEOASTRA_ERROR_NATIVE_FAILURE;native_error=make_error(requested,error->message,error->code);g_error_free(error);}else if(value){char* json=jsc_value_to_json(value,0);output=json?json:"null";g_free(json);g_object_unref(value);}neoastra_result_t actual{};if(context->operation->try_complete(requested,actual))context->callback(context->context,actual,actual==NEOASTRA_OK?neo_string_view(output):neoastra_string_view_t{},native_error);if(native_error)native_error->release();context->operation->release();}
struct capture_context { neoastra_capture_options_t options{};neoastra_buffer_callback_t callback{};void* context{};neoastra_operation_t* operation{};double zoom{1};int view_width{};int view_height{}; };
// Crops a snapshot, which is in device pixels, to the requested region and encodes it. Returns null and a reason on failure.
neoastra_buffer_t* encode_capture(GdkTexture* texture,const capture_context& capture,neoastra_result_t& failure,const char*& reason){
    const int width=gdk_texture_get_width(texture),height=gdk_texture_get_height(texture);
    if(width<=0||height<=0){reason="WebKitGTK returned an empty capture";return nullptr;}
    int left=0,top=0,right=width,bottom=height;
    if(!capture.options.full_page&&capture.options.region.width>0&&capture.options.region.height>0){
        // CSS pixels become view units through the zoom level, and view units become texture pixels through the device scale.
        const double scale_x=capture.zoom*(capture.view_width>0?static_cast<double>(width)/capture.view_width:1.0);
        const double scale_y=capture.zoom*(capture.view_height>0?static_cast<double>(height)/capture.view_height:1.0);
        const auto& region=capture.options.region;
        left=std::clamp(static_cast<int>(region.x*scale_x),0,width);top=std::clamp(static_cast<int>(region.y*scale_y),0,height);
        right=std::clamp(static_cast<int>((static_cast<double>(region.x)+region.width)*scale_x+0.5),0,width);
        bottom=std::clamp(static_cast<int>((static_cast<double>(region.y)+region.height)*scale_y+0.5),0,height);
        if(right<=left||bottom<=top){failure=NEOASTRA_ERROR_INVALID_ARGUMENT;reason="The capture region is outside the visible viewport";return nullptr;}
    }
    const int crop_width=right-left,crop_height=bottom-top;
    const bool cropped=crop_width!=width||crop_height!=height;
    if(capture.options.format==NEOASTRA_CAPTURE_FORMAT_PNG&&!cropped){
        g_bytes_ptr png(gdk_texture_save_to_png_bytes(texture));gsize size{};const auto* data=png?static_cast<const uint8_t*>(g_bytes_get_data(png.get(),&size)):nullptr;
        if(!data||!size){reason="The WebKitGTK capture could not be encoded";return nullptr;}
        return new neoastra_buffer(std::vector<uint8_t>(data,data+size));
    }
    // The download is in the default memory format: premultiplied 32-bit pixels in native byte order, as Cairo has them.
    const size_t stride=static_cast<size_t>(width)*4;
    std::vector<guchar> pixels(stride*static_cast<size_t>(height));
    gdk_texture_download(texture,pixels.data(),stride);
    if(capture.options.format==NEOASTRA_CAPTURE_FORMAT_PNG){
        const size_t crop_stride=static_cast<size_t>(crop_width)*4;
        std::vector<guchar> rows(crop_stride*static_cast<size_t>(crop_height));
        for(int row=0;row<crop_height;++row)std::copy_n(pixels.data()+static_cast<size_t>(top+row)*stride+static_cast<size_t>(left)*4,crop_stride,rows.data()+static_cast<size_t>(row)*crop_stride);
        g_bytes_ptr bytes(g_bytes_new(rows.data(),rows.size()));
        g_object_ptr<GdkTexture> part(gdk_memory_texture_new(crop_width,crop_height,GDK_MEMORY_DEFAULT,bytes.get(),crop_stride));
        g_bytes_ptr png(part?gdk_texture_save_to_png_bytes(part.get()):nullptr);gsize size{};const auto* data=png?static_cast<const uint8_t*>(g_bytes_get_data(png.get(),&size)):nullptr;
        if(!data||!size){reason="The WebKitGTK capture could not be encoded";return nullptr;}
        return new neoastra_buffer(std::vector<uint8_t>(data,data+size));
    }
    // JPEG has no alpha: the pixels are laid over white, which undoes the premultiplication for opaque content.
    const size_t rgb_stride=static_cast<size_t>(crop_width)*3;
    std::vector<guchar> rgb(rgb_stride*static_cast<size_t>(crop_height));
    for(int row=0;row<crop_height;++row){
        const auto* input=pixels.data()+static_cast<size_t>(top+row)*stride+static_cast<size_t>(left)*4;auto* output=rgb.data()+static_cast<size_t>(row)*rgb_stride;
        for(int column=0;column<crop_width;++column,input+=4,output+=3){
#if G_BYTE_ORDER == G_LITTLE_ENDIAN
            const unsigned blue=input[0],green=input[1],red=input[2],alpha=input[3];
#else
            const unsigned alpha=input[0],red=input[1],green=input[2],blue=input[3];
#endif
            const unsigned white=255u-alpha;
            output[0]=static_cast<guchar>(std::min(red+white,255u));output[1]=static_cast<guchar>(std::min(green+white,255u));output[2]=static_cast<guchar>(std::min(blue+white,255u));
        }
    }
    g_object_ptr<GdkPixbuf> pixbuf(gdk_pixbuf_new_from_data(rgb.data(),GDK_COLORSPACE_RGB,FALSE,8,crop_width,crop_height,static_cast<int>(rgb_stride),nullptr,nullptr));
    const auto quality=std::to_string(capture.options.quality?capture.options.quality:80u);
    gchar* encoded{};gsize size{};
    if(!pixbuf||!gdk_pixbuf_save_to_buffer(pixbuf.get(),&encoded,&size,"jpeg",nullptr,"quality",quality.c_str(),nullptr)||!encoded||!size){g_free(encoded);reason="The WebKitGTK capture could not be encoded";return nullptr;}
    std::unique_ptr<gchar,decltype(&g_free)> owned(encoded,g_free);
    return new neoastra_buffer(std::vector<uint8_t>(reinterpret_cast<const uint8_t*>(encoded),reinterpret_cast<const uint8_t*>(encoded)+size));
}
void capture_finished(GObject* object,GAsyncResult* result,void* data){
    std::unique_ptr<capture_context> completion(static_cast<capture_context*>(data));GError* error{};
    g_object_ptr<GdkTexture> texture(webkit_web_view_get_snapshot_finish(WEBKIT_WEB_VIEW(object),result,&error));
    neoastra_result_t requested=NEOASTRA_OK,actual{};neoastra_error_t* native_error{};neoastra_buffer_t* buffer{};
    if(!texture){requested=NEOASTRA_ERROR_NATIVE_FAILURE;native_error=make_error(requested,error?error->message:"WebKitGTK returned no capture",error?error->code:0);}
    else{
        const char* reason="The WebKitGTK capture could not be encoded";auto failure=NEOASTRA_ERROR_NATIVE_FAILURE;
        try{buffer=encode_capture(texture.get(),*completion,failure,reason);}catch(...){buffer=nullptr;}
        if(!buffer){requested=failure;native_error=make_error(requested,reason);}
    }
    if(error)g_error_free(error);
    if(completion->operation->try_complete(requested,actual)){completion->callback(completion->context,actual,actual==NEOASTRA_OK?buffer:nullptr,actual==requested?native_error:nullptr);if(actual!=NEOASTRA_OK&&buffer)buffer->release();}else if(buffer)buffer->release();
    if(native_error)native_error->release();completion->operation->release();
}
struct add_script_context { neoastra_string_callback_t callback{};void* context{};neoastra_operation_t* operation{};std::string identifier; };
gboolean script_added(void* data){std::unique_ptr<add_script_context> completion(static_cast<add_script_context*>(data));neoastra_result_t actual{};if(completion->operation->try_complete(NEOASTRA_OK,actual))completion->callback(completion->context,actual,actual==NEOASTRA_OK?neo_string_view(completion->identifier):neoastra_string_view_t{},nullptr);completion->operation->release();return G_SOURCE_REMOVE;}

void append_json_string(std::string& output,const char* value){output.push_back('"');for(const auto* current=reinterpret_cast<const unsigned char*>(value?value:"");*current;++current){switch(*current){case '"':output+="\\\"";break;case '\\':output+="\\\\";break;case '\b':output+="\\b";break;case '\f':output+="\\f";break;case '\n':output+="\\n";break;case '\r':output+="\\r";break;case '\t':output+="\\t";break;default:if(*current<0x20){char escape[7]{};g_snprintf(escape,sizeof(escape),"\\u%04x",*current);output+=escape;}else output.push_back(static_cast<char>(*current));break;}}output.push_back('"');}
struct cookie_context { neoastra_buffer_callback_t callback{};void* context{};neoastra_operation_t* operation{}; };
void cookies_finished(GObject* object,GAsyncResult* result,void* data){std::unique_ptr<cookie_context> completion(static_cast<cookie_context*>(data));GError* error{};GList* cookies=webkit_cookie_manager_get_cookies_finish(WEBKIT_COOKIE_MANAGER(object),result,&error);neoastra_result_t requested=error?NEOASTRA_ERROR_NATIVE_FAILURE:NEOASTRA_OK,actual{};neoastra_error_t* native_error=error?make_error(requested,error->message,error->code):nullptr;std::string json="[";uint32_t index{};if(!error){for(auto* item=cookies;item;item=item->next){auto* cookie=static_cast<SoupCookie*>(item->data);if(index++)json.push_back(',');json+="{\"name\":";append_json_string(json,soup_cookie_get_name(cookie));json+=",\"value\":";append_json_string(json,soup_cookie_get_value(cookie));json+=",\"domain\":";append_json_string(json,soup_cookie_get_domain(cookie));json+=",\"path\":";append_json_string(json,soup_cookie_get_path(cookie));json+=",\"secure\":";json+=soup_cookie_get_secure(cookie)?"true":"false";json+=",\"httpOnly\":";json+=soup_cookie_get_http_only(cookie)?"true":"false";json+=",\"sameSite\":"+std::to_string(static_cast<uint32_t>(soup_cookie_get_same_site_policy(cookie))+1u);if(auto* expires=soup_cookie_get_expires(cookie))json+=",\"expiresUnixMs\":"+std::to_string(g_date_time_to_unix(expires)*1000);json.push_back('}');}json.push_back(']');}auto* buffer=error?nullptr:new neoastra_buffer(std::vector<uint8_t>(json.begin(),json.end()));if(completion->operation->try_complete(requested,actual)){completion->callback(completion->context,actual,actual==NEOASTRA_OK?buffer:nullptr,actual==requested?native_error:nullptr);if(actual!=NEOASTRA_OK&&buffer)buffer->release();}else if(buffer)buffer->release();if(cookies)g_list_free_full(cookies,reinterpret_cast<GDestroyNotify>(soup_cookie_free));if(error)g_error_free(error);if(native_error)native_error->release();completion->operation->release();}
struct cookie_change_context { neoastra_completion_callback_t callback{};void* context{};neoastra_operation_t* operation{};bool deleting{}; };
void cookie_changed(GObject* object,GAsyncResult* result,void* data){std::unique_ptr<cookie_change_context> completion(static_cast<cookie_change_context*>(data));GError* error{};gboolean success=completion->deleting?webkit_cookie_manager_delete_cookie_finish(WEBKIT_COOKIE_MANAGER(object),result,&error):webkit_cookie_manager_add_cookie_finish(WEBKIT_COOKIE_MANAGER(object),result,&error);auto requested=success?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;neoastra_error_t* native_error=error?make_error(requested,error->message,error->code):nullptr;neoastra_result_t actual{};if(completion->operation->try_complete(requested,actual))completion->callback(completion->context,actual,actual==requested?native_error:nullptr);if(error)g_error_free(error);if(native_error)native_error->release();completion->operation->release();}
SoupCookie* make_cookie(const neoastra_cookie_t* value){const auto name=neo_string(value->name),text=neo_string(value->value),domain=neo_string(value->domain),path=neo_string(value->path);auto* cookie=soup_cookie_new(name.c_str(),text.c_str(),domain.c_str(),path.c_str(),-1);if(!cookie)return nullptr;soup_cookie_set_secure(cookie,(value->flags&1u)!=0);soup_cookie_set_http_only(cookie,(value->flags&2u)!=0);if(value->same_site>0)soup_cookie_set_same_site_policy(cookie,static_cast<SoupSameSitePolicy>(value->same_site-1u));if((value->flags&4u)==0&&value->expires_unix_ms>0){auto* expires=g_date_time_new_from_unix_utc(value->expires_unix_ms/1000);soup_cookie_set_expires(cookie,expires);g_date_time_unref(expires);}return cookie;}
struct clear_context { neoastra_completion_callback_t callback{};void* context{};neoastra_operation_t* operation{}; };
void clear_finished(GObject* object,GAsyncResult* result,void* data){std::unique_ptr<clear_context> completion(static_cast<clear_context*>(data));GError* error{};const auto success=webkit_website_data_manager_clear_finish(WEBKIT_WEBSITE_DATA_MANAGER(object),result,&error);const auto requested=success?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;neoastra_error_t* native_error=error?make_error(requested,error->message,error->code):nullptr;neoastra_result_t actual{};if(completion->operation->try_complete(requested,actual))completion->callback(completion->context,actual,actual==requested?native_error:nullptr);if(error)g_error_free(error);if(native_error)native_error->release();completion->operation->release();}
}

namespace {
void unwatch_monitors(gtk_app* state,neoastra_app_t* app) noexcept {for(auto* monitor:state->watched){g_signal_handlers_disconnect_by_data(monitor,app);g_object_unref(monitor);}state->watched.clear();}
void watch_monitors(neoastra_app_t* app) noexcept;
gboolean read_changed_displays(void* data){
    auto* app=static_cast<neoastra_app_t*>(data);
    if(auto* state=static_cast<gtk_app*>(app->platform);state&&state->display_source){g_source_unref(state->display_source);state->display_source=nullptr;}
    watch_monitors(app);neo_app_displays_changed(app);return G_SOURCE_REMOVE;
}
void displays_changed_soon(neoastra_app_t* app) noexcept {
    auto* state=static_cast<gtk_app*>(app->platform);if(!state||state->display_source)return;
    state->display_source=g_idle_source_new();g_source_set_callback(state->display_source,read_changed_displays,app,nullptr);g_source_attach(state->display_source,state->context);
}
void monitor_changed(GObject*,GParamSpec*,void* data){displays_changed_soon(static_cast<neoastra_app_t*>(data));}
void monitors_changed(GListModel*,guint,guint,guint,void* data){displays_changed_soon(static_cast<neoastra_app_t*>(data));}
// The monitors that are in the list now are listened to, and those that left it are let go.
void watch_monitors(neoastra_app_t* app) noexcept {
    auto* state=static_cast<gtk_app*>(app->platform);if(!state||!state->monitors)return;
    unwatch_monitors(state,app);
    const auto count=g_list_model_get_n_items(state->monitors);
    for(guint index=0;index<count;++index){
        auto* monitor=GDK_MONITOR(g_list_model_get_item(state->monitors,index));if(!monitor)continue;
        try{state->watched.push_back(monitor);}catch(...){g_object_unref(monitor);continue;}
        g_signal_connect(monitor,"notify",G_CALLBACK(monitor_changed),app);
    }
}
}

bool neo_platform_initialize(neoastra_app_t* app,neoastra_error_t** error) noexcept {if(!gtk_init_check()){neo_fail(error,NEOASTRA_ERROR_BACKEND_UNAVAILABLE,"GTK could not connect to a display",0,"gtk");return false;}auto* state=new(std::nothrow) gtk_app;if(!state){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"GTK backend allocation failed");return false;}state->context=g_main_context_ref_thread_default();state->loop=g_main_loop_new(state->context,FALSE);if(!state->loop){g_main_context_unref(state->context);delete state;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"GTK main-loop allocation failed");return false;}app->platform=state;
    // A monitor that comes or goes changes the list, and one that changes its geometry, scale, or work area notifies.
    if(auto* display=gdk_display_get_default()){state->monitors=G_LIST_MODEL(g_object_ref(gdk_display_get_monitors(display)));state->monitors_changed=g_signal_connect(state->monitors,"items-changed",G_CALLBACK(monitors_changed),app);}
    watch_monitors(app);return true;}
void neo_platform_shutdown(neoastra_app_t* app) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(!state)return;unwatch_monitors(state,app);if(state->monitors){g_signal_handler_disconnect(state->monitors,state->monitors_changed);g_object_unref(state->monitors);state->monitors=nullptr;}if(state->display_source){g_source_destroy(state->display_source);g_source_unref(state->display_source);state->display_source=nullptr;}if(state->wake_source){g_source_destroy(state->wake_source);g_source_unref(state->wake_source);state->wake_source=nullptr;}if(state->loop)g_main_loop_unref(state->loop);if(state->context)g_main_context_unref(state->context);delete state;app->platform=nullptr;}
bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(!state||!state->context)return false;auto* source=g_idle_source_new();if(!source)return false;g_source_set_callback(source,destroy_app_on_main,app,nullptr);const auto id=g_source_attach(source,state->context);g_source_unref(source);return id!=0;}
int32_t neo_platform_run(neoastra_app_t* app) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(state&&state->loop)g_main_loop_run(state->loop);return app->exit_code.load();}
void neo_platform_quit(neoastra_app_t* app) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(state&&state->loop)g_main_loop_quit(state->loop);}
void neo_platform_wake(neoastra_app_t* app) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(!state||!state->context||state->wake_source)return;auto* source=g_idle_source_new();if(!source)return;app->retain();g_source_set_callback(source,dispatch_on_main,app,release_dispatch_app);if(g_source_attach(source,state->context)==0){g_source_unref(source);return;}state->wake_source=source;}
bool neo_platform_schedule_decision_timeout(neoastra_app_t* app,neoastra_decision_t* decision) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(!state||!state->context)return false;const auto remaining=std::chrono::duration_cast<std::chrono::milliseconds>(decision->deadline-std::chrono::steady_clock::now()).count();decision->retain();auto* source=g_timeout_source_new(static_cast<guint>(std::clamp<int64_t>(remaining+1,1,G_MAXUINT)));if(!source){decision->release();return false;}g_source_set_callback(source,decision_timed_out,decision,release_timed_decision);const auto id=g_source_attach(source,state->context);g_source_unref(source);return id!=0;}

namespace {
// GDK starts interactive moves from the pointer device and position of a press that is still held down.
bool held_pointer_press(gtk_window* state,GdkToplevel** toplevel,GdkDevice** device,double* x,double* y) noexcept {
    auto* surface=gtk_native_get_surface(GTK_NATIVE(state->widget));
    if(!surface||!GDK_IS_TOPLEVEL(surface))return false;
    auto* seat=gdk_display_get_default_seat(gdk_surface_get_display(surface));
    auto* pointer=seat?gdk_seat_get_pointer(seat):nullptr;
    GdkModifierType mask{};
    if(!pointer||!gdk_surface_get_device_position(surface,pointer,x,y,&mask)||(mask&GDK_BUTTON1_MASK)==0)return false;
    *toplevel=GDK_TOPLEVEL(surface);*device=pointer;return true;
}
// A hidden custom title bar keeps client-side shadows and resize borders while the content covers the whole window.
// GTK draws no window controls over the content, so both extended styles leave them to the application.
void apply_title_bar(neoastra_window_t* window,gtk_window* state) noexcept {
    auto* value=GTK_WINDOW(state->widget);
    if(window->title_bar.style==NEOASTRA_TITLE_BAR_DEFAULT){if(gtk_window_get_titlebar(value))gtk_window_set_titlebar(value,nullptr);return;}
    if(gtk_window_get_titlebar(value))return;
    auto* bar=gtk_box_new(GTK_ORIENTATION_HORIZONTAL,0);
    gtk_widget_set_visible(bar,FALSE);
    gtk_window_set_titlebar(value,bar);
}
}
bool neo_platform_window_create(neoastra_window_t* window,const neoastra_window_options_t* options,neoastra_error_t**) noexcept {auto* state=new(std::nothrow) gtk_window;if(!state)return false;state->widget=gtk_window_new();g_object_add_weak_pointer(G_OBJECT(state->widget),reinterpret_cast<void**>(&state->widget));window->platform=state;auto* value=GTK_WINDOW(state->widget);gtk_window_set_title(value,window->title.c_str());gtk_window_set_default_size(value,window->bounds.width,window->bounds.height);neo_platform_window_set_size_constraints(window);gtk_window_set_resizable(value,(options->flags&1u)!=0);gtk_window_set_decorated(value,(options->flags&2u)!=0);apply_title_bar(window,state);if(window->owner){auto* owner=static_cast<gtk_window*>(window->owner->platform);if(owner&&owner->widget)gtk_window_set_transient_for(value,GTK_WINDOW(owner->widget));}gtk_window_set_modal(value,(options->flags&128u)!=0);g_signal_connect(state->widget,"close-request",G_CALLBACK(window_close_requested),window);g_signal_connect(state->widget,"realize",G_CALLBACK(window_realized),window);g_signal_connect(state->widget,"notify::scale-factor",G_CALLBACK(window_scale_changed),window);neo_window_set_scale_factor(window,gtk_widget_get_scale_factor(state->widget),false);g_signal_connect(state->widget,"notify::is-active",G_CALLBACK(window_focus_changed),window);g_signal_connect(state->widget,"notify::maximized",G_CALLBACK(window_state_changed),window);g_signal_connect(state->widget,"notify::fullscreened",G_CALLBACK(window_state_changed),window);g_signal_connect(state->widget,"notify::suspended",G_CALLBACK(window_state_changed),window);if(options->flags&4u){gtk_window_present(value);neo_platform_window_set_state(window);}return true;}
void neo_platform_window_destroy(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state)return;unwatch_window_size(state);if(state->widget){auto* widget=state->widget;g_signal_handlers_disconnect_by_data(widget,window);g_object_remove_weak_pointer(G_OBJECT(widget),reinterpret_cast<void**>(&state->widget));state->widget=nullptr;gtk_window_destroy(GTK_WINDOW(widget));}delete state;window->platform=nullptr;}
neoastra_result_t neo_platform_window_show(neoastra_window_t* window,bool visible) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;if(!visible){gtk_widget_set_visible(state->widget,FALSE);return NEOASTRA_OK;}neoastra_window_state_t desired{};{std::lock_guard lock(window->state_mutex);desired=window->state;}gtk_window_present(GTK_WINDOW(state->widget));{std::lock_guard lock(window->state_mutex);window->state=desired;}return neo_platform_window_set_state(window);}
neoastra_result_t neo_platform_window_activate(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;gtk_window_present(GTK_WINDOW(state->widget));return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_force_close(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;auto* widget=state->widget;unwatch_window_size(state);g_object_remove_weak_pointer(G_OBJECT(widget),reinterpret_cast<void**>(&state->widget));state->widget=nullptr;g_signal_handlers_disconnect_by_data(widget,window);gtk_window_destroy(GTK_WINDOW(widget));neo_window_closed(window);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_title(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;gtk_window_set_title(GTK_WINDOW(state->widget),window->title.c_str());return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;gtk_window_set_default_size(GTK_WINDOW(state->widget),window->bounds.width,window->bounds.height);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;gtk_widget_set_size_request(state->widget,window->minimum_size.width>0?window->minimum_size.width:-1,window->minimum_size.height>0?window->minimum_size.height:-1);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_state(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;if(!gtk_widget_get_visible(state->widget))return NEOASTRA_OK;auto* value=GTK_WINDOW(state->widget);switch(window->state){
    // The order matters: a window that is told to leave the fullscreen state after the maximized one keeps the size of its screen.
    case NEOASTRA_WINDOW_NORMAL:gtk_window_unfullscreen(value);gtk_window_unmaximize(value);gtk_window_unminimize(value);break;case NEOASTRA_WINDOW_MINIMIZED:gtk_window_minimize(value);break;case NEOASTRA_WINDOW_MAXIMIZED:gtk_window_maximize(value);break;case NEOASTRA_WINDOW_FULLSCREEN:gtk_window_fullscreen(value);break;default:return NEOASTRA_ERROR_INVALID_ARGUMENT;}return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_attribute(neoastra_window_t* window,neoastra_window_attribute_t attribute,bool enabled) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;auto* value=GTK_WINDOW(state->widget);switch(attribute){case NEOASTRA_WINDOW_RESIZABLE:gtk_window_set_resizable(value,enabled);break;case NEOASTRA_WINDOW_DECORATED:gtk_window_set_decorated(value,enabled);break;case NEOASTRA_WINDOW_ALWAYS_ON_TOP:case NEOASTRA_WINDOW_SHOW_IN_TASKBAR:case NEOASTRA_WINDOW_ALL_VIRTUAL_DESKTOPS:return NEOASTRA_ERROR_NOT_SUPPORTED;default:return NEOASTRA_ERROR_INVALID_ARGUMENT;}return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_begin_drag(neoastra_window_t* window) noexcept {
    auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;
    GdkToplevel* toplevel{};GdkDevice* device{};double x{},y{};
    if(!held_pointer_press(state,&toplevel,&device,&x,&y))return NEOASTRA_ERROR_INVALID_STATE;
    gdk_toplevel_begin_move(toplevel,device,GDK_BUTTON_PRIMARY,x,y,GDK_CURRENT_TIME);return NEOASTRA_OK;
}
neoastra_result_t neo_platform_window_begin_resize(neoastra_window_t* window,neoastra_window_resize_edge_t edge) noexcept {
    auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;
    static constexpr GdkSurfaceEdge edges[]={GDK_SURFACE_EDGE_WEST,GDK_SURFACE_EDGE_NORTH,GDK_SURFACE_EDGE_EAST,GDK_SURFACE_EDGE_SOUTH,GDK_SURFACE_EDGE_NORTH_WEST,GDK_SURFACE_EDGE_NORTH_EAST,GDK_SURFACE_EDGE_SOUTH_WEST,GDK_SURFACE_EDGE_SOUTH_EAST};
    GdkToplevel* toplevel{};GdkDevice* device{};double x{},y{};
    if(!held_pointer_press(state,&toplevel,&device,&x,&y))return NEOASTRA_ERROR_INVALID_STATE;
    gdk_toplevel_begin_resize(toplevel,edges[edge],device,GDK_BUTTON_PRIMARY,x,y,GDK_CURRENT_TIME);return NEOASTRA_OK;
}
neoastra_result_t neo_platform_window_set_title_bar(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;apply_title_bar(window,state);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_background_color(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;for(auto* view:window->views)if(view)apply_view_background(view);return NEOASTRA_OK;}
// The size of a GTK window includes the title bar that it draws, and nothing is known to be around it: the frame is the bounds.
neoastra_result_t neo_platform_window_get_frame(neoastra_window_t* window,neoastra_rect_t*) noexcept {auto* state=static_cast<gtk_window*>(window->platform);return state&&state->widget?NEOASTRA_OK:NEOASTRA_ERROR_DISPOSED;}
neoastra_result_t neo_platform_window_get_title_bar(neoastra_window_t* window,neoastra_title_bar_t* value) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;value->height=window->title_bar.style==NEOASTRA_TITLE_BAR_DEFAULT?0:window->title_bar.height>0?window->title_bar.height:32;return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_get_handle(neoastra_window_t* window,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* handle) noexcept {if(kind!=NEOASTRA_NATIVE_HANDLE_GTK_WINDOW&&kind!=NEOASTRA_NATIVE_HANDLE_GTK_WIDGET)return NEOASTRA_ERROR_NOT_SUPPORTED;auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;handle->kind=kind;handle->value=state->widget;return NEOASTRA_OK;}

namespace {
// One process must not open a data directory through two network sessions, so environments on the same root share one.
// The registry is used on the UI thread only, does not keep a session alive, and is never destroyed: a session can outlive it.
std::unordered_map<std::string,WebKitNetworkSession*>& root_sessions() {static auto* sessions=new std::unordered_map<std::string,WebKitNetworkSession*>;return *sessions;}
void root_session_finalized(void* data,GObject*) noexcept {std::unique_ptr<std::string> root(static_cast<std::string*>(data));root_sessions().erase(*root);}
// Without a root the environment keeps the default session, which is also where data stored before roots were honored remains.
WebKitNetworkSession* persistent_session(neoastra_string_view_t user_data_root,neoastra_error_t** error) noexcept {
    if(user_data_root.length==0)return WEBKIT_NETWORK_SESSION(g_object_ref(webkit_network_session_get_default()));
    try{
        // WebKitGTK fills the two directories below. Creating them first reports a root that cannot be used, and lets
        // one directory be one session however it is spelled.
        std::error_code failure;
        auto root=std::filesystem::absolute(std::filesystem::path(neo_string(user_data_root)),failure);
        if(!failure&&(g_mkdir_with_parents((root/"data").c_str(),0700)!=0||g_mkdir_with_parents((root/"cache").c_str(),0700)!=0))failure.assign(errno,std::generic_category());
        if(!failure)root=std::filesystem::canonical(root,failure);
        if(failure){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK could not create the user-data root",failure.value(),"webkitgtk");return nullptr;}
        auto& sessions=root_sessions();
        if(const auto found=sessions.find(root.string());found!=sessions.end())return WEBKIT_NETWORK_SESSION(g_object_ref(found->second));
        auto key=std::make_unique<std::string>(root.string());
        g_object_ptr<WebKitNetworkSession> session(webkit_network_session_new((root/"data").c_str(),(root/"cache").c_str()));
        if(!session)return nullptr;
        // WebKitGTK keeps cookies in memory unless it is given a file for them.
        webkit_cookie_manager_set_persistent_storage(webkit_network_session_get_cookie_manager(session.get()),(root/"data"/"cookies.sqlite").c_str(),WEBKIT_COOKIE_PERSISTENT_STORAGE_SQLITE);
        sessions.emplace(*key,session.get());
        g_object_weak_ref(G_OBJECT(session.get()),root_session_finalized,key.release());
        return session.release();
    }catch(...){return nullptr;}
}
}

bool neo_platform_environment_create_async(neoastra_environment_t* environment,const neoastra_environment_options_t* options,neo_platform_created_callback_t callback,void* context,neoastra_error_t** error) noexcept {auto* state=new(std::nothrow) gtk_environment;if(!state)return false;state->context=webkit_web_context_new();state->session=options->private_mode?webkit_network_session_new_ephemeral():persistent_session(options->user_data_root,error);environment->platform=state;if(!state->context||!state->session){if(state->context)g_object_unref(state->context);if(state->session)g_object_unref(state->session);delete state;environment->platform=nullptr;if(error&&!*error)neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK could not create its web context or network session",0,"webkitgtk");return false;}if(!register_custom_schemes(environment,state->context,error)){neo_platform_environment_destroy(environment);return false;}state->download_started=g_signal_connect(state->session,"download-started",G_CALLBACK(download_started),nullptr);callback(context,nullptr);return true;}
void neo_platform_environment_destroy(neoastra_environment_t* environment) noexcept {auto* state=static_cast<gtk_environment*>(environment->platform);if(!state)return;for(auto* registration:state->schemes)registration->environment=nullptr;if(state->session){if(state->download_started)g_signal_handler_disconnect(state->session,state->download_started);g_object_unref(state->session);}if(state->context)g_object_unref(state->context);delete state;environment->platform=nullptr;}
bool neo_platform_profile_create(neoastra_profile_t* profile,neoastra_error_t** error) noexcept {auto* state=new(std::nothrow) gtk_profile;if(!state){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK profile allocation failed");return false;}auto* environment=static_cast<gtk_environment*>(profile->environment->platform);state->session=profile->ephemeral?webkit_network_session_new_ephemeral():environment&&environment->session?WEBKIT_NETWORK_SESSION(g_object_ref(environment->session)):nullptr;if(!state->session){delete state;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK profile network-session creation failed");return false;}if(profile->ephemeral)state->download_started=g_signal_connect(state->session,"download-started",G_CALLBACK(download_started),nullptr);profile->platform=state;return true;}
void neo_platform_profile_destroy(neoastra_profile_t* profile) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state)return;if(state->session){if(state->download_started)g_signal_handler_disconnect(state->session,state->download_started);g_object_unref(state->session);}delete state;profile->platform=nullptr;}
neoastra_result_t neo_platform_profile_get_cookies(neoastra_profile_t* profile,const std::string& uri,neoastra_buffer_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state||!state->session)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebKitGTK profile is not initialized");auto* completion=new(std::nothrow) cookie_context{callback,context,operation};if(!completion)return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK cookie operation allocation failed");webkit_cookie_manager_get_cookies(webkit_network_session_get_cookie_manager(state->session),uri.c_str(),nullptr,cookies_finished,completion);return NEOASTRA_OK;}
neoastra_result_t neo_platform_profile_set_cookie(neoastra_profile_t* profile,const neoastra_cookie_t* value,neoastra_completion_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state||!state->session)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebKitGTK profile is not initialized");auto* cookie=make_cookie(value);auto* completion=new(std::nothrow) cookie_change_context{callback,context,operation,false};if(!cookie||!completion){if(cookie)soup_cookie_free(cookie);delete completion;return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK cookie allocation failed");}webkit_cookie_manager_add_cookie(webkit_network_session_get_cookie_manager(state->session),cookie,nullptr,cookie_changed,completion);soup_cookie_free(cookie);return NEOASTRA_OK;}
neoastra_result_t neo_platform_profile_delete_cookie(neoastra_profile_t* profile,const neoastra_cookie_t* value,neoastra_completion_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state||!state->session)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebKitGTK profile is not initialized");auto* cookie=make_cookie(value);auto* completion=new(std::nothrow) cookie_change_context{callback,context,operation,true};if(!cookie||!completion){if(cookie)soup_cookie_free(cookie);delete completion;return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK cookie allocation failed");}webkit_cookie_manager_delete_cookie(webkit_network_session_get_cookie_manager(state->session),cookie,nullptr,cookie_changed,completion);soup_cookie_free(cookie);return NEOASTRA_OK;}
neoastra_result_t neo_platform_profile_clear_data(neoastra_profile_t* profile,neoastra_data_kind_t kinds,int64_t start,int64_t end,neoastra_completion_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state||!state->session)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebKitGTK profile is not initialized");if(end!=INT64_MAX||(kinds!=NEOASTRA_DATA_ALL&&(kinds&(NEOASTRA_DATA_PERMISSIONS|NEOASTRA_DATA_DOWNLOAD_HISTORY))))return neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WebKitGTK cannot clear the selected data kinds or bounded end time");WebKitWebsiteDataTypes types=static_cast<WebKitWebsiteDataTypes>(0);if(kinds==NEOASTRA_DATA_ALL)types=WEBKIT_WEBSITE_DATA_ALL;else{if(kinds&NEOASTRA_DATA_COOKIES)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_COOKIES);if(kinds&NEOASTRA_DATA_CACHE)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_MEMORY_CACHE|WEBKIT_WEBSITE_DATA_DISK_CACHE|WEBKIT_WEBSITE_DATA_DOM_CACHE);if(kinds&NEOASTRA_DATA_LOCAL_STORAGE)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_LOCAL_STORAGE);if(kinds&NEOASTRA_DATA_INDEXED_DB)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_INDEXEDDB_DATABASES);if(kinds&NEOASTRA_DATA_SERVICE_WORKERS)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_SERVICE_WORKER_REGISTRATIONS);}const auto now=g_get_real_time()/1000;const auto timespan=start==INT64_MIN?0:std::max<int64_t>(0,now-start)*1000;auto* completion=new(std::nothrow) clear_context{callback,context,operation};if(!completion)return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK clear-data allocation failed");auto* manager=webkit_network_session_get_website_data_manager(state->session);webkit_website_data_manager_clear(manager,types,timespan,nullptr,clear_finished,completion);return NEOASTRA_OK;} // NOLINT(clang-analyzer-optin.core.EnumCastOutOfRange) WebKit data types are bit flags.
namespace {
// Suppresses WebKit's default menu when the host turned it off; page-level contextmenu handlers have already run.
gboolean context_menu(WebKitWebView*,WebKitContextMenu*,WebKitHitTestResult*,void* data){auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return TRUE;auto* state=static_cast<gtk_view*>(view->platform);return state&&state->context_menus_disabled?TRUE:FALSE;}
// WebKitGTK has no DevTools shortcut of its own, so F12 opens the inspector whenever developer extras are enabled.
gboolean devtools_key_pressed(GtkEventControllerKey*,guint keyval,guint,GdkModifierType modifiers,void* data){
    auto* view=static_cast<neoastra_view_t*>(data);neo_retain_guard owner(view);if(!owner)return FALSE;auto* state=static_cast<gtk_view*>(view->platform);
    if(!state||!state->widget||keyval!=GDK_KEY_F12||(modifiers&(GDK_CONTROL_MASK|GDK_SHIFT_MASK|GDK_ALT_MASK))!=0)return FALSE;
    auto* web_view=WEBKIT_WEB_VIEW(state->widget);
    if(!webkit_settings_get_enable_developer_extras(webkit_web_view_get_settings(web_view)))return FALSE;
    webkit_web_inspector_show(webkit_web_view_get_inspector(web_view));return TRUE;
}
}
bool neo_platform_view_create_async(neoastra_view_t* view,const neoastra_view_options_t* options,neo_platform_created_callback_t callback,void* context,neoastra_error_t** error) noexcept {if(view->bridge_policy==NEOASTRA_BRIDGE_TRUSTED_ORIGINS){neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WebKitGTK 6.0 does not expose trustworthy web-message sender origins",0,"webkitgtk");return false;}auto* environment=static_cast<gtk_environment*>(view->environment->platform);auto* profile=view->profile?static_cast<gtk_profile*>(view->profile->platform):nullptr;auto* web_context=environment?environment->context:nullptr;auto* session=profile?profile->session:environment?environment->session:nullptr;auto* parent=view_parent(view);if(!web_context||!session||!parent){neo_fail(error,NEOASTRA_ERROR_INVALID_STATE,"WebKitGTK context, network session, or parent is unavailable");return false;}auto* state=new(std::nothrow) gtk_view;if(!state)return false;state->content=webkit_user_content_manager_new();if(!state->content){delete state;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK could not allocate its user-content manager",0,"webkitgtk");return false;}if(view->bridge_policy!=NEOASTRA_BRIDGE_DISABLED&&!webkit_user_content_manager_register_script_message_handler(state->content,"_neoastra_transport_v1",nullptr)){g_object_unref(state->content);delete state;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK could not register the private transport handler",0,"webkitgtk");return false;}WebKitWebView* related{};if(options->popup_request&&options->popup_request->owner){auto* opener=static_cast<gtk_view*>(options->popup_request->owner->platform);if(opener&&opener->widget)related=WEBKIT_WEB_VIEW(opener->widget);}state->widget=related?GTK_WIDGET(g_object_new(WEBKIT_TYPE_WEB_VIEW,"related-view",related,"user-content-manager",state->content,nullptr)):GTK_WIDGET(g_object_new(WEBKIT_TYPE_WEB_VIEW,"web-context",web_context,"network-session",session,"user-content-manager",state->content,nullptr));view->platform=state;
    // WebKitGTK before 2.46 gives a page no microphone or camera to ask for unless the setting is on. A page asks as it does in
    // the other backends whatever the version, and its request is refused unless the host allows it.
    webkit_settings_set_enable_media_stream(webkit_web_view_get_settings(WEBKIT_WEB_VIEW(state->widget)),TRUE);
    state->history_list=WEBKIT_BACK_FORWARD_LIST(g_object_ref(webkit_web_view_get_back_forward_list(WEBKIT_WEB_VIEW(state->widget))));
    g_object_set_data(G_OBJECT(state->widget),"neoastra.native-view",view);g_object_add_weak_pointer(G_OBJECT(state->widget),reinterpret_cast<void**>(&state->widget));state->load_changed=g_signal_connect(state->widget,"load-changed",G_CALLBACK(load_changed),view);state->load_failed=g_signal_connect(state->widget,"load-failed",G_CALLBACK(load_failed),view);g_signal_connect(state->widget,"decide-policy",G_CALLBACK(decide_policy),view);g_signal_connect(state->widget,"permission-request",G_CALLBACK(permission_requested),view);g_signal_connect(state->widget,"create",G_CALLBACK(create_web_view),view);g_signal_connect(state->widget,"script-dialog",G_CALLBACK(script_dialog),view);g_signal_connect(state->widget,"run-file-chooser",G_CALLBACK(run_file_chooser),view);g_signal_connect(state->widget,"authenticate",G_CALLBACK(authenticate),view);g_signal_connect(state->widget,"enter-fullscreen",G_CALLBACK(enter_fullscreen),view);state->title_changed=g_signal_connect(state->widget,"notify::title",G_CALLBACK(title_changed),view);state->uri_changed=g_signal_connect(state->widget,"notify::uri",G_CALLBACK(uri_changed),view);state->history_changed=g_signal_connect(webkit_web_view_get_back_forward_list(WEBKIT_WEB_VIEW(state->widget)),"changed",G_CALLBACK(history_changed),view);if(view->bridge_policy!=NEOASTRA_BRIDGE_DISABLED)state->message_received=g_signal_connect(state->content,"script-message-received::_neoastra_transport_v1",G_CALLBACK(message_received),view);state->process_terminated=g_signal_connect(state->widget,"web-process-terminated",G_CALLBACK(web_process_terminated),view);g_signal_connect(state->widget,"context-menu",G_CALLBACK(context_menu),view);
    // Keep our own controller references so that even a host that removes and retains a controller cannot leave stale userdata.
    state->keys=gtk_event_controller_key_new();gtk_event_controller_set_propagation_phase(state->keys,GTK_PHASE_CAPTURE);g_signal_connect(state->keys,"key-pressed",G_CALLBACK(devtools_key_pressed),view);gtk_widget_add_controller(state->widget,GTK_EVENT_CONTROLLER(g_object_ref(state->keys)));
    state->drop_target=GTK_DROP_TARGET(gtk_drop_target_new(G_TYPE_INVALID,GDK_ACTION_COPY));GType drop_types[]={G_TYPE_STRING,GDK_TYPE_FILE_LIST};gtk_drop_target_set_gtypes(state->drop_target,drop_types,2);g_signal_connect(state->drop_target,"accept",G_CALLBACK(drop_accept),nullptr);state->drop_received=g_signal_connect(state->drop_target,"drop",G_CALLBACK(drop_received),view);gtk_widget_add_controller(state->widget,GTK_EVENT_CONTROLLER(g_object_ref(state->drop_target)));apply_view_background(view);if(GTK_IS_WINDOW(parent)){auto* stack=window_view_stack(GTK_WINDOW(parent));gtk_overlay_add_overlay(GTK_OVERLAY(stack),state->widget);gtk_overlay_set_measure_overlay(GTK_OVERLAY(stack),state->widget,TRUE);}else if(GTK_IS_BOX(parent))gtk_box_append(GTK_BOX(parent),state->widget);else gtk_widget_set_parent(state->widget,parent);gtk_widget_set_visible(state->widget,TRUE);callback(context,nullptr);return true;}
// A window keeps its own pointer to its child, which only gtk_window_set_child clears: unparenting a hosted view directly would leave the window a freed child.
void neo_platform_view_destroy(neoastra_view_t* view) noexcept {
    auto* state=static_cast<gtk_view*>(view->platform);if(!state)return;
    view->platform=nullptr;
    for(auto& entry:state->scripts)webkit_user_script_unref(entry.second);
    if(state->history_list)g_signal_handlers_disconnect_by_data(state->history_list,view);
    if(state->drop_target)g_signal_handlers_disconnect_by_data(state->drop_target,view);
    if(state->keys)g_signal_handlers_disconnect_by_data(state->keys,view);
    if(state->content)g_signal_handlers_disconnect_by_data(state->content,view);
    if(state->widget){
        g_object_ptr<GtkWidget> widget(GTK_WIDGET(g_object_ref(state->widget)));
        g_object_set_data(G_OBJECT(widget.get()),"neoastra.native-view",nullptr);
        g_signal_handlers_disconnect_by_data(widget.get(),view);
        if(state->drop_target&&gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(state->drop_target))==widget.get())gtk_widget_remove_controller(widget.get(),GTK_EVENT_CONTROLLER(state->drop_target));
        if(state->keys&&gtk_event_controller_get_widget(state->keys)==widget.get())gtk_widget_remove_controller(widget.get(),state->keys);
        g_object_remove_weak_pointer(G_OBJECT(widget.get()),reinterpret_cast<void**>(&state->widget));
        auto* parent=gtk_widget_get_parent(widget.get());
        if(is_view_stack(parent))gtk_overlay_remove_overlay(GTK_OVERLAY(parent),widget.get());
        else if(GTK_IS_WINDOW(parent)&&gtk_window_get_child(GTK_WINDOW(parent))==widget.get())gtk_window_set_child(GTK_WINDOW(parent),nullptr);
        else if(parent)gtk_widget_unparent(widget.get());
        state->widget=nullptr;
    }
    if(state->history_list)g_object_unref(state->history_list);
    if(state->drop_target)g_object_unref(state->drop_target);
    if(state->keys)g_object_unref(state->keys);
    if(state->content){
        if(view->bridge_policy!=NEOASTRA_BRIDGE_DISABLED)webkit_user_content_manager_unregister_script_message_handler(state->content,"_neoastra_transport_v1",nullptr);
        g_object_unref(state->content);
    }
    delete state;
}
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t* view) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;gtk_widget_set_size_request(state->widget,view->fill_parent?-1:view->bounds.width,view->fill_parent?-1:view->bounds.height);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_navigate(neoastra_view_t* view,const std::string& uri,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;webkit_web_view_load_uri(WEBKIT_WEB_VIEW(state->widget),uri.c_str());return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t* view,const std::string& uri,const std::string& method,const std::string& headers,const uint8_t*,uint64_t body_length,neoastra_error_t** error) noexcept {if(method=="GET"&&headers.empty()&&body_length==0)return neo_platform_view_navigate(view,uri,error);return neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WebKitGTK 6.0 does not expose arbitrary-method top-level navigation");}
neoastra_result_t neo_platform_view_load_html(neoastra_view_t* view,const std::string& html,const std::string& base,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;webkit_web_view_load_html(WEBKIT_WEB_VIEW(state->widget),html.c_str(),base.empty()?nullptr:base.c_str());return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_command(neoastra_view_t* view,uint32_t command) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;auto* web=WEBKIT_WEB_VIEW(state->widget);switch(command){case 0:webkit_web_view_stop_loading(web);break;case 1:webkit_web_view_reload(web);break;case 2:webkit_web_view_reload_bypass_cache(web);break;case 3:webkit_web_view_go_back(web);break;case 4:webkit_web_view_go_forward(web);break;default:return NEOASTRA_ERROR_INVALID_ARGUMENT;}return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t* view,const std::string& script,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;auto* completion=new(std::nothrow) script_context{view,callback,context,operation};if(!completion)return NEOASTRA_ERROR_NATIVE_FAILURE;webkit_web_view_evaluate_javascript(WEBKIT_WEB_VIEW(state->widget),script.data(),script.size(),nullptr,nullptr,nullptr,script_finished,completion);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_add_script(neoastra_view_t* view,const std::string& script,const neoastra_script_options_t* options,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->content)return NEOASTRA_ERROR_NOT_INITIALIZED;if(options->isolated_world)return NEOASTRA_ERROR_NOT_SUPPORTED;const auto frames=options->main_frame_only?WEBKIT_USER_CONTENT_INJECT_TOP_FRAME:WEBKIT_USER_CONTENT_INJECT_ALL_FRAMES;const auto time=options->injection_time==NEOASTRA_SCRIPT_DOCUMENT_END?WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_END:WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START;auto* user_script=webkit_user_script_new(script.c_str(),frames,time,nullptr,nullptr);if(!user_script)return NEOASTRA_ERROR_NATIVE_FAILURE;const auto identifier=std::to_string(state->next_script++);state->scripts.emplace(identifier,user_script);webkit_user_content_manager_add_script(state->content,user_script);g_idle_add(script_added,new add_script_context{callback,context,operation,identifier});return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t* view,const std::string& identifier) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->content)return NEOASTRA_ERROR_NOT_INITIALIZED;auto found=state->scripts.find(identifier);if(found==state->scripts.end())return NEOASTRA_ERROR_INVALID_ARGUMENT;webkit_user_script_unref(found->second);state->scripts.erase(found);webkit_user_content_manager_remove_all_scripts(state->content);for(const auto& entry:state->scripts)webkit_user_content_manager_add_script(state->content,entry.second);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_post_message(neoastra_view_t* view,const std::string& message,bool json,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;std::string script="window.dispatchEvent(new CustomEvent('neoastramessage',{detail:"+(json?message:("JSON.parse("+message+")"))+"}));";webkit_web_view_evaluate_javascript(WEBKIT_WEB_VIEW(state->widget),script.data(),script.size(),nullptr,nullptr,nullptr,nullptr,nullptr);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t* view,double* factor) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;*factor=webkit_web_view_get_zoom_level(WEBKIT_WEB_VIEW(state->widget));return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t* view,double factor) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;webkit_web_view_set_zoom_level(WEBKIT_WEB_VIEW(state->widget),factor);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_set_setting(neoastra_view_t* view,neoastra_view_setting_t setting,bool enabled) noexcept {
    auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;
    switch(setting){
        // WebKitGTK has no browser shortcuts, status bar, or zoom controls of its own, so there is nothing to turn off or on.
        case NEOASTRA_VIEW_SETTING_BROWSER_ACCELERATOR_KEYS:case NEOASTRA_VIEW_SETTING_STATUS_BAR:case NEOASTRA_VIEW_SETTING_ZOOM_CONTROLS:return enabled?NEOASTRA_ERROR_NOT_SUPPORTED:NEOASTRA_OK;
        case NEOASTRA_VIEW_SETTING_DEFAULT_CONTEXT_MENUS:state->context_menus_disabled=!enabled;return NEOASTRA_OK;
        case NEOASTRA_VIEW_SETTING_DEVTOOLS:webkit_settings_set_enable_developer_extras(webkit_web_view_get_settings(WEBKIT_WEB_VIEW(state->widget)),enabled);return NEOASTRA_OK;
        // Script dialogs go to the host unless WebKitGTK is asked to show its own.
        case NEOASTRA_VIEW_SETTING_DEFAULT_SCRIPT_DIALOGS:state->default_script_dialogs=enabled;return NEOASTRA_OK;
        case NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS:webkit_settings_set_enable_tabs_to_links(webkit_web_view_get_settings(WEBKIT_WEB_VIEW(state->widget)),enabled);return NEOASTRA_OK;
        // The requests that walk the history are refused as they come. The swipe of WebKitGTK is off unless a host turns it on
        // through the native view; it is turned off here so that it does not draw the page it would go to, and never on.
        case NEOASTRA_VIEW_SETTING_HISTORY_NAVIGATION:if(!enabled)webkit_settings_set_enable_back_forward_navigation_gestures(webkit_web_view_get_settings(WEBKIT_WEB_VIEW(state->widget)),FALSE);report_history(view);return NEOASTRA_OK;
        default:return NEOASTRA_ERROR_INVALID_ARGUMENT;
    }
}
bool neo_platform_open_external(const std::string& uri,int64_t* native_code) noexcept {GError* error{};const bool opened=g_app_info_launch_default_for_uri(uri.c_str(),nullptr,&error);if(error){*native_code=error->code;g_error_free(error);}return opened;}
// GTK 4 has no public way to hand a widget a synthesized pointer event.
neoastra_result_t neo_platform_view_move_pointer(neoastra_view_t* view,double,double) noexcept {auto* state=static_cast<gtk_view*>(view->platform);return !state||!state->widget?NEOASTRA_ERROR_NOT_INITIALIZED:NEOASTRA_ERROR_NOT_SUPPORTED;}
neoastra_result_t neo_platform_view_open_devtools(neoastra_view_t* view) noexcept {
    auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;
    auto* web_view=WEBKIT_WEB_VIEW(state->widget);
    if(!webkit_settings_get_enable_developer_extras(webkit_web_view_get_settings(web_view)))return NEOASTRA_ERROR_INVALID_STATE;
    webkit_web_inspector_show(webkit_web_view_get_inspector(web_view));return NEOASTRA_OK;
}
neoastra_result_t neo_platform_view_capture(neoastra_view_t* view,const neoastra_capture_options_t& options,neoastra_buffer_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {
    auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebKitGTK view is not initialized",0,"webkitgtk");
    auto* web_view=WEBKIT_WEB_VIEW(state->widget);
    auto* completion=new(std::nothrow) capture_context{options,callback,context,operation,webkit_web_view_get_zoom_level(web_view),gtk_widget_get_width(state->widget),gtk_widget_get_height(state->widget)};
    if(!completion)return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK capture could not be started",0,"webkitgtk");
    webkit_web_view_get_snapshot(web_view,options.full_page?WEBKIT_SNAPSHOT_REGION_FULL_DOCUMENT:WEBKIT_SNAPSHOT_REGION_VISIBLE,WEBKIT_SNAPSHOT_OPTIONS_NONE,nullptr,capture_finished,completion);
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t* view,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* handle) noexcept {if(kind!=NEOASTRA_NATIVE_HANDLE_WEBKITGTK_WEBVIEW&&kind!=NEOASTRA_NATIVE_HANDLE_GTK_WIDGET)return NEOASTRA_ERROR_NOT_SUPPORTED;auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;handle->kind=kind;handle->value=state->widget;return NEOASTRA_OK;}

namespace {
struct monitor_reference { void operator()(GdkMonitor* monitor) const noexcept { g_object_unref(monitor); } };
// The X11 backend of GDK tells the work area of a monitor; the X11 headers are not included, whose macros would clash with this file.
bool x11_monitor(GdkMonitor* monitor) noexcept { const auto type=g_type_from_name("GdkX11Monitor");return type&&G_TYPE_CHECK_INSTANCE_TYPE(monitor,type)&&gdk_x11_monitor_get_workarea; }
bool x11_display() noexcept { auto* display=gdk_display_get_default();const auto type=g_type_from_name("GdkX11Display");return display&&type&&G_TYPE_CHECK_INSTANCE_TYPE(display,type); }
}

// GDK counts monitors in logical units, measured from the origin of the layout of the desktop, and tells no primary one: the one
// at that origin is taken for it.
void neo_platform_get_displays(neoastra_app_t*,std::vector<neo_display>& displays) noexcept {
    try{
        displays.clear();
        auto* display=gdk_display_get_default();if(!display)return;
        auto* monitors=gdk_display_get_monitors(display);
        const auto count=g_list_model_get_n_items(monitors);
        for(guint index=0;index<count;++index){
            std::unique_ptr<GdkMonitor,monitor_reference> monitor(GDK_MONITOR(g_list_model_get_item(monitors,index)));
            if(!monitor||!gdk_monitor_is_valid(monitor.get()))continue;
            neo_display value;
            value.handle=static_cast<uint64_t>(reinterpret_cast<uintptr_t>(monitor.get()));
            GdkRectangle geometry{};gdk_monitor_get_geometry(monitor.get(),&geometry);
            value.bounds={geometry.x,geometry.y,geometry.width,geometry.height};
            value.work_area=value.bounds;
            // Wayland has no work area for a client to read.
            if(x11_monitor(monitor.get())){GdkRectangle area{};gdk_x11_monitor_get_workarea(monitor.get(),&area);value.work_area={area.x,area.y,area.width,area.height};value.flags|=NEOASTRA_DISPLAY_WORK_AREA_REPORTED;}
            // The scale of a widget is the whole number of the monitor, which is what a window reports.
            value.scale_factor=gdk_monitor_get_scale_factor(monitor.get());
            if(const auto rate=gdk_monitor_get_refresh_rate(monitor.get());rate>0)value.refresh_rate=rate/1000.0;
            const char* connector=gdk_monitor_get_connector(monitor.get());
            const char* manufacturer=gdk_monitor_get_manufacturer(monitor.get());
            const char* model=gdk_monitor_get_model(monitor.get());
            // The connector names the connection of the monitor after a restart, and the manufacturer and the model the monitor.
            if(connector||manufacturer||model){
                value.id=std::string("gtk:")+(manufacturer?manufacturer:"")+":"+(model?model:"")+":"+(connector?connector:"");
                if(connector)value.flags|=NEOASTRA_DISPLAY_ID_PERSISTENT;
            }
#if GTK_CHECK_VERSION(4,10,0)
            if(const char* description=gdk_monitor_get_description(monitor.get()))value.name=description;
#endif
            if(value.name.empty())value.name=model?model:connector?connector:"";
            displays.push_back(std::move(value));
        }
    }catch(...){displays.clear();}
}

// A surface is on the monitors that the compositor or the X server reports, and a window has no surface before it is shown.
uint64_t neo_platform_window_display_handle(neoastra_window_t* window) noexcept {
    auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return 0;
    auto* surface=gtk_native_get_surface(GTK_NATIVE(state->widget));if(!surface)return 0;
    auto* monitor=gdk_display_get_monitor_at_surface(gdk_surface_get_display(surface),surface);
    return monitor?static_cast<uint64_t>(reinterpret_cast<uintptr_t>(monitor)):0;
}

void neo_platform_window_capability(neoastra_app_t*,neoastra_window_capability_t capability,neoastra_capability_info_t* info) noexcept {
    static const std::string x11_displays="GdkMonitor geometry and work areas in logical units, and the connector, manufacturer, and model as identifier; GTK tells no primary display, so the one at the origin is, and no rotation";
    static const std::string wayland_displays="GdkMonitor geometry in logical units, and the connector, manufacturer, and model as identifier; Wayland tells no work area, so it is the bounds, GTK tells no primary display, so the one at the origin is, and no rotation";
    static const std::string placement="GTK 4 cannot place a window and does not tell where it is: a position is kept but not applied, and the compositor places windows on Wayland";
    static const std::string desktops="GTK 4 tells nothing of virtual desktops (workspaces)";
    switch(capability){
        case NEOASTRA_WINDOW_CAPABILITY_DISPLAYS:info->support=NEOASTRA_SUPPORT_LIMITED;info->details=neo_string_view(x11_display()?x11_displays:wayland_displays);break;
        case NEOASTRA_WINDOW_CAPABILITY_PLACEMENT:info->details=neo_string_view(placement);break;
        case NEOASTRA_WINDOW_CAPABILITY_VIRTUAL_DESKTOPS:
        case NEOASTRA_WINDOW_CAPABILITY_VIRTUAL_DESKTOP_MOVE:
        case NEOASTRA_WINDOW_CAPABILITY_ALL_VIRTUAL_DESKTOPS:info->details=neo_string_view(desktops);break;
        default:break;
    }
}

neoastra_result_t neo_platform_window_get_virtual_desktop(neoastra_window_t* window,uint32_t*,std::string*) noexcept {auto* state=static_cast<gtk_window*>(window->platform);return !state||!state->widget?NEOASTRA_ERROR_DISPOSED:NEOASTRA_ERROR_NOT_SUPPORTED;}
neoastra_result_t neo_platform_window_move_to_virtual_desktop(neoastra_window_t* window,const std::string&) noexcept {auto* state=static_cast<gtk_window*>(window->platform);return !state||!state->widget?NEOASTRA_ERROR_DISPOSED:NEOASTRA_ERROR_NOT_SUPPORTED;}
// GTK 4 tells neither where a window is nor the order of windows on screen.
neoastra_result_t neo_platform_window_at(neoastra_app_t*,neoastra_point_t,const neoastra_window_t*,uint64_t*) noexcept {return NEOASTRA_ERROR_NOT_SUPPORTED;}
neoastra_result_t neo_platform_view_convert_point(neoastra_view_t* view,bool,double,double,double*,double*) noexcept {auto* state=static_cast<gtk_view*>(view->platform);return !state||!state->widget?NEOASTRA_ERROR_NOT_INITIALIZED:NEOASTRA_ERROR_NOT_SUPPORTED;}
