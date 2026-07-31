#include "../common/native_internal.hpp"

#include <gtk/gtk.h>
#include <webkit2/webkit2.h>

#include <algorithm>
#include <memory>
#include <new>
#include <string>
#include <unordered_map>
#include <vector>

namespace {
struct gtk_app { GMainContext* context{}; GSource* wake_source{}; };
struct gtk_window { GtkWidget* widget{}; neoastra_window_state_t reported_state{NEOASTRA_WINDOW_NORMAL}; };
struct gtk_environment { WebKitWebContext* context{}; gulong download_started{}; };
struct gtk_profile { WebKitWebContext* context{}; gulong download_started{}; };
struct gtk_view { GtkWidget* widget{}; WebKitUserContentManager* content{}; gulong load_changed{}; gulong load_failed{}; gulong title_changed{}; gulong uri_changed{}; gulong message_received{}; gulong process_terminated{}; gulong drop_received{}; uint64_t next_script{1}; std::unordered_map<std::string,WebKitUserScript*> scripts; };

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

void drag_data_received(GtkWidget*,GdkDragContext* context,gint x,gint y,GtkSelectionData* selection,guint info,guint time,void* data) noexcept {
    auto* view=static_cast<neoastra_view_t*>(data);
    try {
        neo_event_details details{};details.bounds={static_cast<int32_t>(x),static_cast<int32_t>(y),0,0};
        if(info==1){const auto source_length=gtk_selection_data_get_length(selection);if(source_length<=0||source_length>32768){gtk_drag_finish(context,FALSE,FALSE,time);return;}guchar* value=gtk_selection_data_get_text(selection);if(value){std::string text((const char*)value);g_free(value);if(!text.empty()&&text.size()<=32768){neo_emit_view_detailed(view,NEOASTRA_EVENT_MESSAGE_RECEIVED,0,&text,nullptr,(UINT64_C(1)<<63)|1,0,nullptr,details);gtk_drag_finish(context,TRUE,FALSE,time);return;}}gtk_drag_finish(context,FALSE,FALSE,time);return;}
        gchar** uris=gtk_selection_data_get_uris(selection);
        if(!uris){gtk_drag_finish(context,FALSE,FALSE,time);return;}
        std::string paths,links;uint32_t count=0,file_count=0,link_count=0;bool valid=true;
        for(gchar** current=uris;*current;++current){
            if(++count>256){valid=false;break;}
            GError* error=nullptr;gchar* path=g_filename_from_uri(*current,nullptr,&error);
            if(error){g_error_free(error);error=nullptr;}
            if(!path){const size_t length=strlen(*current);if(length==0||length>32768||links.size()+length+1>1024*1024){valid=false;break;}if(!links.empty())links.push_back('\0');links.append(*current,length);++link_count;continue;}
            gchar* canonical=g_canonicalize_filename(path,nullptr);g_free(path);
            const size_t length=canonical?strlen(canonical):0;
            if(!canonical||length==0||length>32768||paths.size()+length+1>1024*1024){if(canonical)g_free(canonical);valid=false;break;}
            if(!paths.empty())paths.push_back('\0');paths.append(canonical,length);g_free(canonical);++file_count;
        }
        g_strfreev(uris);
        if(valid&&(!paths.empty()||!links.empty())){
            if(!paths.empty())neo_emit_view_detailed(view,NEOASTRA_EVENT_MESSAGE_RECEIVED,0,&paths,nullptr,(UINT64_C(1)<<63)|(UINT64_C(2)<<56)|file_count,0,nullptr,details);
            if(!links.empty())neo_emit_view_detailed(view,NEOASTRA_EVENT_MESSAGE_RECEIVED,0,&links,nullptr,(UINT64_C(1)<<63)|(UINT64_C(1)<<56)|link_count,0,nullptr,details);
            gtk_drag_finish(context,TRUE,FALSE,time);return;
        }
    }catch(...){neo_log(view->environment->app,NEOASTRA_LOG_WARNING,"drag-drop","WebKitGTK native drop decoding failed");}
    gtk_drag_finish(context,FALSE,FALSE,time);
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
    auto* environment=static_cast<neoastra_environment_t*>(data);
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

        // WebKitGTK 4.1 does not expose initiator, frame, or resource-kind metadata
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
        webkit_web_context_register_uri_scheme(context,scheme.name.c_str(),uri_scheme_requested,environment,nullptr);
        if((scheme.flags&NEOASTRA_CUSTOM_SCHEME_SECURE)!=0)webkit_security_manager_register_uri_scheme_as_secure(security,scheme.name.c_str());
        if((scheme.flags&NEOASTRA_CUSTOM_SCHEME_CORS_ENABLED)!=0)webkit_security_manager_register_uri_scheme_as_cors_enabled(security,scheme.name.c_str());
    }
    return true;
}

GtkWidget* view_parent(neoastra_view_t* view) noexcept {
    if (view->window) { auto* state=static_cast<gtk_window*>(view->window->platform); return state?state->widget:nullptr; }
    return view->parent.kind==NEOASTRA_NATIVE_PARENT_GTK_WIDGET?static_cast<GtkWidget*>(view->parent.handle):nullptr;
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
    neo_drain_dispatch(app);
    return G_SOURCE_REMOVE;
}
gboolean destroy_app_on_main(void* data) { neo_destroy_app_on_ui(static_cast<neoastra_app_t*>(data)); return G_SOURCE_REMOVE; }
gboolean decision_timed_out(void* data){static_cast<neoastra_decision_t*>(data)->expire();return G_SOURCE_REMOVE;}
void release_timed_decision(void* data){static_cast<neoastra_decision_t*>(data)->release();}

void window_destroyed(GtkWidget*, void* data) { auto* window=static_cast<neoastra_window_t*>(data); auto* state=static_cast<gtk_window*>(window->platform); if(state)state->widget=nullptr; neo_window_closed(window); }
gboolean window_delete(GtkWidget*, GdkEvent*, void* data) { auto* window=static_cast<neoastra_window_t*>(data); if(window->force_closing){window->force_closing=false;return FALSE;}neo_window_request_close(window,NEOASTRA_WINDOW_CLOSE_USER,true);return TRUE; }
void window_size(GtkWidget*, GdkRectangle* allocation, void* data) { auto* window=static_cast<neoastra_window_t*>(data); {std::lock_guard lock(window->state_mutex);window->bounds.width=allocation->width;window->bounds.height=allocation->height;} neo_emit_app(window->app,NEOASTRA_EVENT_WINDOW_RESIZED,window->id); }
gboolean window_state_changed(GtkWidget*,GdkEventWindowState* event,void* data){auto* window=static_cast<neoastra_window_t*>(data);const auto state=(event->new_window_state&GDK_WINDOW_STATE_FULLSCREEN)?NEOASTRA_WINDOW_FULLSCREEN:(event->new_window_state&GDK_WINDOW_STATE_ICONIFIED)?NEOASTRA_WINDOW_MINIMIZED:(event->new_window_state&GDK_WINDOW_STATE_MAXIMIZED)?NEOASTRA_WINDOW_MAXIMIZED:NEOASTRA_WINDOW_NORMAL;auto* native=static_cast<gtk_window*>(window->platform);const auto changed=native&&native->reported_state!=state;if(native)native->reported_state=state;{std::lock_guard lock(window->state_mutex);window->state=state;}if(changed)neo_emit_app(window->app,NEOASTRA_EVENT_WINDOW_STATE_CHANGED,window->id,nullptr,nullptr,state);return FALSE;}

struct navigation_context { WebKitPolicyDecision* policy{}; };
void navigation_decided(void* pointer, const neoastra_decision_response_t* response) noexcept { std::unique_ptr<navigation_context> context(static_cast<navigation_context*>(pointer)); if(response->action==NEOASTRA_DECISION_ALLOW||response->action==NEOASTRA_DECISION_DEFAULT)webkit_policy_decision_use(context->policy);else webkit_policy_decision_ignore(context->policy);g_object_unref(context->policy); }
struct permission_context { WebKitPermissionRequest* request{}; };
void permission_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<permission_context> context(static_cast<permission_context*>(pointer));if(response->action==NEOASTRA_DECISION_ALLOW)webkit_permission_request_allow(context->request);else webkit_permission_request_deny(context->request);g_object_unref(context->request);}
struct new_window_context { neoastra_view_t* view{}; std::string uri; };
void new_window_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<new_window_context> context(static_cast<new_window_context*>(pointer));auto* state=static_cast<gtk_view*>(context->view->platform);if(response->action==NEOASTRA_DECISION_ALLOW&&!response->target_view&&state&&state->widget)webkit_web_view_load_uri(WEBKIT_WEB_VIEW(state->widget),context->uri.c_str());else if(response->action==NEOASTRA_DECISION_OPEN_EXTERNAL&&state&&state->widget){auto* top=gtk_widget_get_toplevel(state->widget);GError* error{};gtk_show_uri_on_window(GTK_IS_WINDOW(top)?GTK_WINDOW(top):nullptr,context->uri.c_str(),GDK_CURRENT_TIME,&error);if(error)g_error_free(error);}}
void finish_synchronous_decision(neoastra_decision_t* decision);
gboolean permission_requested(WebKitWebView*,WebKitPermissionRequest* request,void* data){auto* view=static_cast<neoastra_view_t*>(data);neoastra_permission_kind_t kind=NEOASTRA_PERMISSION_UNKNOWN;if(WEBKIT_IS_GEOLOCATION_PERMISSION_REQUEST(request))kind=NEOASTRA_PERMISSION_GEOLOCATION;else if(WEBKIT_IS_NOTIFICATION_PERMISSION_REQUEST(request))kind=NEOASTRA_PERMISSION_NOTIFICATIONS;else if(WEBKIT_IS_USER_MEDIA_PERMISSION_REQUEST(request)){auto* media=WEBKIT_USER_MEDIA_PERMISSION_REQUEST(request);kind=webkit_user_media_permission_is_for_video_device(media)?NEOASTRA_PERMISSION_CAMERA:NEOASTRA_PERMISSION_MICROPHONE;}auto* decision=new(std::nothrow) neoastra_decision;auto* context=new(std::nothrow) permission_context{WEBKIT_PERMISSION_REQUEST(g_object_ref(request))};if(!decision||!context){delete decision;if(context){g_object_unref(context->request);delete context;}webkit_permission_request_deny(request);return TRUE;}neo_configure_decision(decision,view,NEOASTRA_DECISION_PERMISSION,NEOASTRA_DECISION_DENY);decision->completion=permission_decided;decision->completion_context=context;neo_emit_view(view,NEOASTRA_EVENT_PERMISSION_REQUESTED,0,nullptr,nullptr,kind,0,decision);neo_finish_decision_event(view,decision);decision->release();return TRUE;}
WebKitWebView* create_web_view(WebKitWebView*,WebKitNavigationAction* action,void* data){auto* view=static_cast<neoastra_view_t*>(data);auto* request=webkit_navigation_action_get_request(action);std::string uri=request&&webkit_uri_request_get_uri(request)?webkit_uri_request_get_uri(request):"";const char* raw_name=webkit_navigation_action_get_frame_name(action);std::string name=raw_name?raw_name:"";auto* decision=new(std::nothrow) neoastra_decision;auto* context=new(std::nothrow) new_window_context{view,uri};if(!decision||!context){delete decision;delete context;return nullptr;}neo_configure_decision(decision,view,NEOASTRA_DECISION_NEW_WINDOW,NEOASTRA_DECISION_CANCEL);decision->completion=new_window_decided;decision->completion_context=context;neo_emit_view(view,NEOASTRA_EVENT_NEW_WINDOW_REQUESTED,0,&name,&uri,webkit_navigation_action_is_user_gesture(action)?1u:0u,0,decision);finish_synchronous_decision(decision);WebKitWebView* target{};if(decision->resolved_target){auto* target_state=static_cast<gtk_view*>(decision->resolved_target->platform);if(target_state)target=WEBKIT_WEB_VIEW(target_state->widget);}decision->release();return target;}
gboolean decide_policy(WebKitWebView*, WebKitPolicyDecision* policy, WebKitPolicyDecisionType type, void* data) {
    if(type!=WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION)return FALSE;
    auto* view=static_cast<neoastra_view_t*>(data);
    auto* navigation=WEBKIT_NAVIGATION_POLICY_DECISION(policy);
    auto* action=webkit_navigation_policy_decision_get_navigation_action(navigation);
    auto* request=webkit_navigation_action_get_request(action);
    std::string uri=webkit_uri_request_get_uri(request)?webkit_uri_request_get_uri(request):"";
    auto* decision=new neoastra_decision;
    neo_configure_decision(decision,view,NEOASTRA_DECISION_NAVIGATION,NEOASTRA_DECISION_ALLOW);
    auto* context=new navigation_context{WEBKIT_POLICY_DECISION(g_object_ref(policy))};decision->completion=navigation_decided;decision->completion_context=context;
    const auto navigation_type=webkit_navigation_action_get_navigation_type(action);
    const uint64_t flags=1u|((navigation_type==WEBKIT_NAVIGATION_TYPE_LINK_CLICKED)?2u:0u);
    neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_REQUESTED,0,nullptr,&uri,flags,0,decision);
    neo_finish_decision_event(view,decision);
    decision->release();return TRUE;
}
void finish_synchronous_decision(neoastra_decision_t* decision) {const auto state=decision->state.load(std::memory_order_acquire);if(state==neo_decision_state::pending||state==neo_decision_state::deferred){neoastra_decision_response_t response{};response.size=sizeof(response);response.version=1;response.action=decision->default_action;neoastra_decision_complete(decision,&response,nullptr);}}
struct script_dialog_context { WebKitScriptDialog* dialog{}; };
void script_dialog_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<script_dialog_context> context(static_cast<script_dialog_context*>(pointer));if(response->action!=NEOASTRA_DECISION_ALLOW)return;const auto type=webkit_script_dialog_get_dialog_type(context->dialog);if(type==WEBKIT_SCRIPT_DIALOG_CONFIRM)webkit_script_dialog_confirm_set_confirmed(context->dialog,TRUE);else if(type==WEBKIT_SCRIPT_DIALOG_PROMPT){try{auto text=neo_string(response->text);webkit_script_dialog_prompt_set_text(context->dialog,text.c_str());}catch(...){}}}
gboolean script_dialog(WebKitWebView*,WebKitScriptDialog* dialog,void* data){auto* view=static_cast<neoastra_view_t*>(data);const auto native=webkit_script_dialog_get_dialog_type(dialog);const auto kind=native==WEBKIT_SCRIPT_DIALOG_ALERT?NEOASTRA_SCRIPT_DIALOG_ALERT:native==WEBKIT_SCRIPT_DIALOG_CONFIRM?NEOASTRA_SCRIPT_DIALOG_CONFIRM:native==WEBKIT_SCRIPT_DIALOG_PROMPT?NEOASTRA_SCRIPT_DIALOG_PROMPT:NEOASTRA_SCRIPT_DIALOG_BEFORE_UNLOAD;std::string message=webkit_script_dialog_get_message(dialog)?webkit_script_dialog_get_message(dialog):"";std::string default_text=webkit_script_dialog_prompt_get_default_text(dialog)?webkit_script_dialog_prompt_get_default_text(dialog):"";auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_SCRIPT_DIALOG,kind==NEOASTRA_SCRIPT_DIALOG_ALERT?NEOASTRA_DECISION_ALLOW:NEOASTRA_DECISION_CANCEL);decision->completion=script_dialog_decided;decision->completion_context=new script_dialog_context{dialog};neo_event_details details{};details.text2=&default_text;neo_emit_view_detailed(view,NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED,0,&message,&view->source,kind,0,decision,details);finish_synchronous_decision(decision);const auto handled=decision->resolved_action.load()==NEOASTRA_DECISION_ALLOW;decision->release();return handled||kind!=NEOASTRA_SCRIPT_DIALOG_ALERT;}
struct chooser_context { WebKitFileChooserRequest* request{}; };
void chooser_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<chooser_context> context(static_cast<chooser_context*>(pointer));if(response->action!=NEOASTRA_DECISION_ALLOW){webkit_file_chooser_request_cancel(context->request);return;}try{std::vector<std::string> storage;std::vector<const char*> paths;storage.reserve(response->path_count);paths.reserve(static_cast<size_t>(response->path_count)+1);for(uint32_t i=0;i<response->path_count;++i)storage.push_back(neo_string(response->paths[i]));for(const auto& path:storage)paths.push_back(path.c_str());paths.push_back(nullptr);webkit_file_chooser_request_select_files(context->request,paths.data());}catch(...){webkit_file_chooser_request_cancel(context->request);}}
gboolean run_file_chooser(WebKitWebView*,WebKitFileChooserRequest* request,void* data){auto* view=static_cast<neoastra_view_t*>(data);std::string accepted;for(auto* current=webkit_file_chooser_request_get_mime_types(request);current&&*current;++current){if(!accepted.empty())accepted.push_back(';');accepted+=*current;}auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_FILE_CHOOSER,NEOASTRA_DECISION_CANCEL);decision->completion=chooser_decided;decision->completion_context=new chooser_context{request};neo_emit_view(view,NEOASTRA_EVENT_FILE_CHOOSER_REQUESTED,0,&accepted,nullptr,webkit_file_chooser_request_get_select_multiple(request)?1u:0u,0,decision);finish_synchronous_decision(decision);decision->release();return TRUE;}
struct auth_context { WebKitAuthenticationRequest* request{}; };
void auth_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<auth_context> context(static_cast<auth_context*>(pointer));if(response->action==NEOASTRA_DECISION_ALLOW){try{auto user=neo_string(response->text),password=neo_string(response->secondary_text);auto* credential=webkit_credential_new(user.c_str(),password.c_str(),WEBKIT_CREDENTIAL_PERSISTENCE_NONE);if(credential){webkit_authentication_request_authenticate(context->request,credential);webkit_credential_free(credential);return;}}catch(...){ }webkit_authentication_request_cancel(context->request);}else if(response->action==NEOASTRA_DECISION_CANCEL||response->action==NEOASTRA_DECISION_DENY)webkit_authentication_request_cancel(context->request);}
gboolean authenticate(WebKitWebView*,WebKitAuthenticationRequest* request,void* data){auto* view=static_cast<neoastra_view_t*>(data);std::string host=webkit_authentication_request_get_host(request)?webkit_authentication_request_get_host(request):"";std::string realm=webkit_authentication_request_get_realm(request)?webkit_authentication_request_get_realm(request):"";auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_AUTHENTICATION,NEOASTRA_DECISION_DEFAULT);decision->completion=auth_decided;decision->completion_context=new auth_context{request};neo_event_details details{};details.text2=&realm;neo_emit_view_detailed(view,NEOASTRA_EVENT_AUTHENTICATION_REQUESTED,0,&host,nullptr,0,webkit_authentication_request_get_port(request),decision,details);finish_synchronous_decision(decision);const auto handled=decision->resolved_action.load()!=NEOASTRA_DECISION_DEFAULT;decision->release();return handled;}
struct fullscreen_context { WebKitWebView* webview{}; };
void fullscreen_decided(void* pointer,const neoastra_decision_response_t*) noexcept {delete static_cast<fullscreen_context*>(pointer);}
gboolean enter_fullscreen(WebKitWebView* webview,void* data){auto* view=static_cast<neoastra_view_t*>(data);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_FULLSCREEN,NEOASTRA_DECISION_DENY);decision->completion=fullscreen_decided;decision->completion_context=new fullscreen_context{webview};neo_emit_view(view,NEOASTRA_EVENT_FULLSCREEN_REQUESTED,0,nullptr,&view->source,1,0,decision);finish_synchronous_decision(decision);const auto allow=decision->resolved_action.load()==NEOASTRA_DECISION_ALLOW;decision->release();return allow?FALSE:TRUE;}
struct gtk_download { WebKitDownload* value{};gulong destination{},received{},finished{},failed{}; };
neoastra_result_t gtk_download_command(neoastra_download_t* download,uint32_t command) noexcept {auto* state=static_cast<gtk_download*>(download->platform);if(!state||!state->value)return NEOASTRA_ERROR_DISPOSED;if(command!=0)return NEOASTRA_ERROR_NOT_SUPPORTED;webkit_download_cancel(state->value);return NEOASTRA_OK;}
void destroy_gtk_download(neoastra_download_t* download) noexcept {auto* state=static_cast<gtk_download*>(download->platform);if(!state)return;if(state->value){if(state->destination)g_signal_handler_disconnect(state->value,state->destination);if(state->received)g_signal_handler_disconnect(state->value,state->received);if(state->finished)g_signal_handler_disconnect(state->value,state->finished);if(state->failed)g_signal_handler_disconnect(state->value,state->failed);g_object_unref(state->value);}delete state;}
struct download_destination_context { neoastra_download_t* download{}; };
void download_destination_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<download_destination_context> context(static_cast<download_destination_context*>(pointer));auto* state=static_cast<gtk_download*>(context->download->platform);if(!state)return;if(response->action==NEOASTRA_DECISION_DOWNLOAD){try{context->download->destination_path=neo_string(response->text);GError* error{};auto* uri=g_filename_to_uri(context->download->destination_path.c_str(),nullptr,&error);if(uri){webkit_download_set_destination(state->value,uri);g_free(uri);}else{webkit_download_cancel(state->value);if(error){context->download->failure_reason=error->message;g_error_free(error);}}}catch(...){webkit_download_cancel(state->value);}}else if(response->action!=NEOASTRA_DECISION_ALLOW&&response->action!=NEOASTRA_DECISION_DEFAULT)webkit_download_cancel(state->value);}
gboolean download_decide_destination(WebKitDownload* native,const gchar* suggested,void* data) noexcept {auto* download=static_cast<neoastra_download_t*>(data);try{std::string filename=suggested?suggested:"";auto decision=std::make_unique<neoastra_decision>();neo_configure_decision(decision.get(),download->view,NEOASTRA_DECISION_DOWNLOAD_REQUEST,NEOASTRA_DECISION_CANCEL);decision->completion=download_destination_decided;decision->completion_context=new download_destination_context{download};neo_event_details details{};details.value2=1;details.download=download;download->event_published=true;neo_emit_view_detailed(download->view,NEOASTRA_EVENT_DOWNLOAD_REQUESTED,download->id,&filename,&download->source_uri,UINT64_MAX,0,decision.get(),details);finish_synchronous_decision(decision.get());const auto action=decision->resolved_action.load();const auto accepted=action==NEOASTRA_DECISION_DEFAULT||action==NEOASTRA_DECISION_ALLOW||action==NEOASTRA_DECISION_DOWNLOAD;if(accepted){download->state.store(NEOASTRA_DOWNLOAD_IN_PROGRESS);neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_STARTED);}return action==NEOASTRA_DECISION_ALLOW||action==NEOASTRA_DECISION_DEFAULT?FALSE:TRUE;}catch(...){webkit_download_cancel(native);return TRUE;}}
void download_received(WebKitDownload* native,guint64 length,void* data) noexcept {auto* download=static_cast<neoastra_download_t*>(data);try{if(download->destination_path.empty()){const auto* destination=webkit_download_get_destination(native);if(destination){GError* error{};auto* path=g_filename_from_uri(destination,nullptr,&error);if(path){download->destination_path=path;g_free(path);}if(error)g_error_free(error);}}}catch(...){ }download->bytes_received.fetch_add(length);neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED);}
void finish_gtk_download(neoastra_download_t* download,neoastra_download_state_t terminal,const GError* error=nullptr) noexcept {auto expected=NEOASTRA_DOWNLOAD_IN_PROGRESS;if(!download->state.compare_exchange_strong(expected,terminal)){expected=NEOASTRA_DOWNLOAD_REQUESTED;if(!download->state.compare_exchange_strong(expected,terminal))return;}if(error){try{download->failure_reason=error->message?error->message:"";}catch(...){}}neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_COMPLETED);download->release_lifecycle();}
void download_finished(WebKitDownload*,void* data){finish_gtk_download(static_cast<neoastra_download_t*>(data),NEOASTRA_DOWNLOAD_COMPLETED);}
void download_failed(WebKitDownload*,GError* error,void* data){finish_gtk_download(static_cast<neoastra_download_t*>(data),error&&g_error_matches(error,G_IO_ERROR,G_IO_ERROR_CANCELLED)?NEOASTRA_DOWNLOAD_CANCELED:NEOASTRA_DOWNLOAD_FAILED,error);}
void download_started(WebKitWebContext*,WebKitDownload* native,void*) noexcept {auto* webview=webkit_download_get_web_view(native);auto* view=webview?static_cast<neoastra_view_t*>(g_object_get_data(G_OBJECT(webview),"neoastra.native-view")):nullptr;if(!view){webkit_download_cancel(native);return;}try{auto download=std::make_unique<neoastra_download>(view);auto state=std::make_unique<gtk_download>();download->platform=state.release();download->command=gtk_download_command;download->platform_destroy=destroy_gtk_download;auto* platform=static_cast<gtk_download*>(download->platform);platform->value=WEBKIT_DOWNLOAD(g_object_ref(native));auto* request=webkit_download_get_request(native);download->source_uri=request&&webkit_uri_request_get_uri(request)?webkit_uri_request_get_uri(request):"";platform->destination=g_signal_connect(native,"decide-destination",G_CALLBACK(download_decide_destination),download.get());platform->received=g_signal_connect(native,"received-data",G_CALLBACK(download_received),download.get());platform->finished=g_signal_connect(native,"finished",G_CALLBACK(download_finished),download.get());platform->failed=g_signal_connect(native,"failed",G_CALLBACK(download_failed),download.get());if(!platform->destination||!platform->received||!platform->finished||!platform->failed){download.reset();webkit_download_cancel(native);return;}download.release();}catch(...){webkit_download_cancel(native);}}
void load_changed(WebKitWebView* webview, WebKitLoadEvent event, void* data) { auto* view=static_cast<neoastra_view_t*>(data);const char* raw=webkit_web_view_get_uri(webview);std::string uri=raw?raw:"";if(event==WEBKIT_LOAD_STARTED)neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_STARTED,0,nullptr,&uri,1);else if(event==WEBKIT_LOAD_FINISHED)neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_COMPLETED,0,nullptr,&uri); }
gboolean load_failed(WebKitWebView*, WebKitLoadEvent, const char* uri, GError* error, void* data) { auto* view=static_cast<neoastra_view_t*>(data);std::string value=uri?uri:"";neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_FAILED,0,nullptr,&value,NEOASTRA_ERROR_NATIVE_FAILURE,error?error->code:0);return FALSE; }
void title_changed(GObject* object,GParamSpec*,void* data){auto* view=static_cast<neoastra_view_t*>(data);const auto* value=webkit_web_view_get_title(WEBKIT_WEB_VIEW(object));view->title=value?value:"";neo_emit_view(view,NEOASTRA_EVENT_TITLE_CHANGED,0,&view->title);}
void uri_changed(GObject* object,GParamSpec*,void* data){auto* view=static_cast<neoastra_view_t*>(data);const auto* value=webkit_web_view_get_uri(WEBKIT_WEB_VIEW(object));view->source=value?value:"";neo_emit_view(view,NEOASTRA_EVENT_SOURCE_CHANGED,0,nullptr,&view->source);}
void message_received(WebKitUserContentManager*,WebKitJavascriptResult* result,void* data) noexcept {auto* view=static_cast<neoastra_view_t*>(data);char* json{};try{auto* value=webkit_javascript_result_get_js_value(result);json=jsc_value_to_json(value,0);std::string message=json?json:"null";g_free(json);json=nullptr;const std::string origin;neo_emit_bridge_message(view,message,origin,false);}catch(...){g_free(json);if(view)neo_log(view->environment->app,NEOASTRA_LOG_ERROR,"bridge","WebKitGTK web-message handling failed");}}
void web_process_terminated(WebKitWebView*,WebKitWebProcessTerminationReason reason,void* data){auto* view=static_cast<neoastra_view_t*>(data);uint64_t value=NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED|NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW;if(reason==WEBKIT_WEB_PROCESS_CRASHED||reason==WEBKIT_WEB_PROCESS_EXCEEDED_MEMORY_LIMIT)value|=NEOASTRA_PROCESS_FAILURE_CRASHED;neo_emit_view(view,NEOASTRA_EVENT_WEB_PROCESS_TERMINATED,0,nullptr,nullptr,value,static_cast<int64_t>(reason));}

struct script_context { neoastra_view_t* view{};neoastra_string_callback_t callback{};void* context{};neoastra_operation_t* operation{}; };
void script_finished(GObject* object,GAsyncResult* result,void* data){std::unique_ptr<script_context> context(static_cast<script_context*>(data));GError* error{};auto* value=webkit_web_view_evaluate_javascript_finish(WEBKIT_WEB_VIEW(object),result,&error);std::string output;neoastra_error_t* native_error{};auto requested=NEOASTRA_OK;if(error){requested=NEOASTRA_ERROR_NATIVE_FAILURE;native_error=make_error(requested,error->message,error->code);g_error_free(error);}else if(value){char* json=jsc_value_to_json(value,0);output=json?json:"null";g_free(json);g_object_unref(value);}neoastra_result_t actual{};if(context->operation->try_complete(requested,actual))context->callback(context->context,actual,actual==NEOASTRA_OK?neo_string_view(output):neoastra_string_view_t{},native_error);if(native_error)native_error->release();context->operation->release();}
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

bool neo_platform_initialize(neoastra_app_t* app,neoastra_error_t** error) noexcept {if(!gtk_init_check(nullptr,nullptr)){neo_fail(error,NEOASTRA_ERROR_BACKEND_UNAVAILABLE,"GTK could not connect to a display",0,"gtk");return false;}auto* state=new(std::nothrow) gtk_app;if(!state){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"GTK backend allocation failed");return false;}state->context=g_main_context_ref_thread_default();app->platform=state;return true;}
void neo_platform_shutdown(neoastra_app_t* app) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(!state)return;if(state->wake_source){g_source_destroy(state->wake_source);g_source_unref(state->wake_source);state->wake_source=nullptr;}if(state->context)g_main_context_unref(state->context);delete state;app->platform=nullptr;}
bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(!state||!state->context)return false;auto* source=g_idle_source_new();if(!source)return false;g_source_set_callback(source,destroy_app_on_main,app,nullptr);const auto id=g_source_attach(source,state->context);g_source_unref(source);return id!=0;}
int32_t neo_platform_run(neoastra_app_t* app) noexcept {gtk_main();return app->exit_code.load();}
void neo_platform_quit(neoastra_app_t*) noexcept {auto* source=g_idle_source_new();g_source_set_callback(source,[](void*)->gboolean{gtk_main_quit();return G_SOURCE_REMOVE;},nullptr,nullptr);g_source_attach(source,nullptr);g_source_unref(source);}
void neo_platform_wake(neoastra_app_t* app) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(!state||!state->context||state->wake_source)return;auto* source=g_idle_source_new();if(!source)return;app->retain();g_source_set_callback(source,dispatch_on_main,app,release_dispatch_app);if(g_source_attach(source,state->context)==0){g_source_unref(source);return;}state->wake_source=source;}
bool neo_platform_schedule_decision_timeout(neoastra_app_t* app,neoastra_decision_t* decision) noexcept {auto* state=static_cast<gtk_app*>(app->platform);if(!state||!state->context)return false;const auto remaining=std::chrono::duration_cast<std::chrono::milliseconds>(decision->deadline-std::chrono::steady_clock::now()).count();decision->retain();auto* source=g_timeout_source_new(static_cast<guint>(std::clamp<int64_t>(remaining+1,1,G_MAXUINT)));if(!source){decision->release();return false;}g_source_set_callback(source,decision_timed_out,decision,release_timed_decision);const auto id=g_source_attach(source,state->context);g_source_unref(source);return id!=0;}

bool neo_platform_window_create(neoastra_window_t* window,const neoastra_window_options_t* options,neoastra_error_t**) noexcept {auto* state=new(std::nothrow) gtk_window;if(!state)return false;state->widget=gtk_window_new(GTK_WINDOW_TOPLEVEL);window->platform=state;gtk_window_set_title(GTK_WINDOW(state->widget),window->title.c_str());if((options->flags&(32u|64u))==0)gtk_window_move(GTK_WINDOW(state->widget),window->bounds.x,window->bounds.y);gtk_window_set_default_size(GTK_WINDOW(state->widget),window->bounds.width,window->bounds.height);neo_platform_window_set_size_constraints(window);gtk_window_set_resizable(GTK_WINDOW(state->widget),(options->flags&1u)!=0);gtk_window_set_decorated(GTK_WINDOW(state->widget),(options->flags&2u)!=0);gtk_window_set_keep_above(GTK_WINDOW(state->widget),(options->flags&8u)!=0);gtk_window_set_skip_taskbar_hint(GTK_WINDOW(state->widget),(options->flags&16u)==0);if(window->owner){auto* owner=static_cast<gtk_window*>(window->owner->platform);if(owner&&owner->widget)gtk_window_set_transient_for(GTK_WINDOW(state->widget),GTK_WINDOW(owner->widget));}if(options->flags&64u)gtk_window_set_position(GTK_WINDOW(state->widget),window->owner?GTK_WIN_POS_CENTER_ON_PARENT:GTK_WIN_POS_CENTER);else if(options->flags&32u)gtk_window_set_position(GTK_WINDOW(state->widget),GTK_WIN_POS_NONE);gtk_window_set_modal(GTK_WINDOW(state->widget),(options->flags&128u)!=0);g_signal_connect(state->widget,"delete-event",G_CALLBACK(window_delete),window);g_signal_connect(state->widget,"destroy",G_CALLBACK(window_destroyed),window);g_signal_connect(state->widget,"size-allocate",G_CALLBACK(window_size),window);g_signal_connect(state->widget,"window-state-event",G_CALLBACK(window_state_changed),window);if(options->flags&4u){gtk_widget_show_all(state->widget);neo_platform_window_set_state(window);}return true;}
void neo_platform_window_destroy(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state)return;if(state->widget)gtk_widget_destroy(state->widget);delete state;window->platform=nullptr;}
neoastra_result_t neo_platform_window_show(neoastra_window_t* window,bool visible) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;if(!visible){gtk_widget_hide(state->widget);return NEOASTRA_OK;}neoastra_window_state_t desired{};{std::lock_guard lock(window->state_mutex);desired=window->state;}gtk_widget_show_all(state->widget);{std::lock_guard lock(window->state_mutex);window->state=desired;}return neo_platform_window_set_state(window);}
neoastra_result_t neo_platform_window_activate(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;gtk_window_present(GTK_WINDOW(state->widget));return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_force_close(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;gtk_widget_destroy(state->widget);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_title(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;gtk_window_set_title(GTK_WINDOW(state->widget),window->title.c_str());return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;gtk_window_move(GTK_WINDOW(state->widget),window->bounds.x,window->bounds.y);gtk_window_resize(GTK_WINDOW(state->widget),window->bounds.width,window->bounds.height);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;GdkGeometry geometry{};GdkWindowHints hints=static_cast<GdkWindowHints>(0);if(window->minimum_size.width>0||window->minimum_size.height>0){geometry.min_width=std::max(window->minimum_size.width,1);geometry.min_height=std::max(window->minimum_size.height,1);hints=static_cast<GdkWindowHints>(hints|GDK_HINT_MIN_SIZE);}if(window->maximum_size.width>0||window->maximum_size.height>0){geometry.max_width=window->maximum_size.width>0?window->maximum_size.width:G_MAXINT;geometry.max_height=window->maximum_size.height>0?window->maximum_size.height:G_MAXINT;hints=static_cast<GdkWindowHints>(hints|GDK_HINT_MAX_SIZE);}gtk_window_set_geometry_hints(GTK_WINDOW(state->widget),nullptr,&geometry,hints);return NEOASTRA_OK;} // NOLINT(clang-analyzer-optin.core.EnumCastOutOfRange) GLib hints are bit flags.
neoastra_result_t neo_platform_window_set_state(neoastra_window_t* window) noexcept {auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;if(!gtk_widget_get_visible(state->widget))return NEOASTRA_OK;auto* value=GTK_WINDOW(state->widget);switch(window->state){case NEOASTRA_WINDOW_NORMAL:gtk_window_deiconify(value);gtk_window_unmaximize(value);gtk_window_unfullscreen(value);break;case NEOASTRA_WINDOW_MINIMIZED:gtk_window_iconify(value);break;case NEOASTRA_WINDOW_MAXIMIZED:gtk_window_maximize(value);break;case NEOASTRA_WINDOW_FULLSCREEN:gtk_window_fullscreen(value);break;default:return NEOASTRA_ERROR_INVALID_ARGUMENT;}return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_get_handle(neoastra_window_t* window,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* handle) noexcept {if(kind!=NEOASTRA_NATIVE_HANDLE_GTK_WINDOW&&kind!=NEOASTRA_NATIVE_HANDLE_GTK_WIDGET)return NEOASTRA_ERROR_NOT_SUPPORTED;auto* state=static_cast<gtk_window*>(window->platform);if(!state||!state->widget)return NEOASTRA_ERROR_DISPOSED;handle->kind=kind;handle->value=state->widget;return NEOASTRA_OK;}

bool neo_platform_environment_create_async(neoastra_environment_t* environment,const neoastra_environment_options_t* options,neo_platform_created_callback_t callback,void* context,neoastra_error_t** error) noexcept {auto* state=new(std::nothrow) gtk_environment;if(!state)return false;state->context=options->private_mode?webkit_web_context_new_ephemeral():webkit_web_context_new();environment->platform=state;if(!state->context){delete state;environment->platform=nullptr;return false;}if(!register_custom_schemes(environment,state->context,error)){g_object_unref(state->context);delete state;environment->platform=nullptr;return false;}state->download_started=g_signal_connect(state->context,"download-started",G_CALLBACK(download_started),nullptr);callback(context,nullptr);return true;}
void neo_platform_environment_destroy(neoastra_environment_t* environment) noexcept {auto* state=static_cast<gtk_environment*>(environment->platform);if(!state)return;if(state->context){if(state->download_started)g_signal_handler_disconnect(state->context,state->download_started);g_object_unref(state->context);}delete state;environment->platform=nullptr;}
bool neo_platform_profile_create(neoastra_profile_t* profile,neoastra_error_t** error) noexcept {auto* state=new(std::nothrow) gtk_profile;if(!state){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK profile allocation failed");return false;}auto* environment=static_cast<gtk_environment*>(profile->environment->platform);state->context=profile->ephemeral?webkit_web_context_new_ephemeral():environment&&environment->context?WEBKIT_WEB_CONTEXT(g_object_ref(environment->context)):nullptr;if(!state->context){delete state;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK profile context creation failed");return false;}if(profile->ephemeral&&!register_custom_schemes(profile->environment,state->context,error)){g_object_unref(state->context);delete state;return false;}if(profile->ephemeral)state->download_started=g_signal_connect(state->context,"download-started",G_CALLBACK(download_started),nullptr);profile->platform=state;return true;}
void neo_platform_profile_destroy(neoastra_profile_t* profile) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state)return;if(state->context){if(state->download_started)g_signal_handler_disconnect(state->context,state->download_started);g_object_unref(state->context);}delete state;profile->platform=nullptr;}
neoastra_result_t neo_platform_profile_get_cookies(neoastra_profile_t* profile,const std::string& uri,neoastra_buffer_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state||!state->context)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebKitGTK profile is not initialized");auto* completion=new(std::nothrow) cookie_context{callback,context,operation};if(!completion)return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK cookie operation allocation failed");webkit_cookie_manager_get_cookies(webkit_web_context_get_cookie_manager(state->context),uri.c_str(),nullptr,cookies_finished,completion);return NEOASTRA_OK;}
neoastra_result_t neo_platform_profile_set_cookie(neoastra_profile_t* profile,const neoastra_cookie_t* value,neoastra_completion_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state||!state->context)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebKitGTK profile is not initialized");auto* cookie=make_cookie(value);auto* completion=new(std::nothrow) cookie_change_context{callback,context,operation,false};if(!cookie||!completion){if(cookie)soup_cookie_free(cookie);delete completion;return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK cookie allocation failed");}webkit_cookie_manager_add_cookie(webkit_web_context_get_cookie_manager(state->context),cookie,nullptr,cookie_changed,completion);soup_cookie_free(cookie);return NEOASTRA_OK;}
neoastra_result_t neo_platform_profile_delete_cookie(neoastra_profile_t* profile,const neoastra_cookie_t* value,neoastra_completion_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state||!state->context)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebKitGTK profile is not initialized");auto* cookie=make_cookie(value);auto* completion=new(std::nothrow) cookie_change_context{callback,context,operation,true};if(!cookie||!completion){if(cookie)soup_cookie_free(cookie);delete completion;return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK cookie allocation failed");}webkit_cookie_manager_delete_cookie(webkit_web_context_get_cookie_manager(state->context),cookie,nullptr,cookie_changed,completion);soup_cookie_free(cookie);return NEOASTRA_OK;}
neoastra_result_t neo_platform_profile_clear_data(neoastra_profile_t* profile,neoastra_data_kind_t kinds,int64_t start,int64_t end,neoastra_completion_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {auto* state=static_cast<gtk_profile*>(profile->platform);if(!state||!state->context)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebKitGTK profile is not initialized");if(end!=INT64_MAX||(kinds!=NEOASTRA_DATA_ALL&&(kinds&(NEOASTRA_DATA_PERMISSIONS|NEOASTRA_DATA_DOWNLOAD_HISTORY))))return neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WebKitGTK cannot clear the selected data kinds or bounded end time");WebKitWebsiteDataTypes types=static_cast<WebKitWebsiteDataTypes>(0);if(kinds==NEOASTRA_DATA_ALL)types=WEBKIT_WEBSITE_DATA_ALL;else{if(kinds&NEOASTRA_DATA_COOKIES)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_COOKIES);if(kinds&NEOASTRA_DATA_CACHE)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_MEMORY_CACHE|WEBKIT_WEBSITE_DATA_DISK_CACHE|WEBKIT_WEBSITE_DATA_DOM_CACHE);if(kinds&NEOASTRA_DATA_LOCAL_STORAGE)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_LOCAL_STORAGE);if(kinds&NEOASTRA_DATA_INDEXED_DB)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_INDEXEDDB_DATABASES);if(kinds&NEOASTRA_DATA_SERVICE_WORKERS)types=static_cast<WebKitWebsiteDataTypes>(types|WEBKIT_WEBSITE_DATA_SERVICE_WORKER_REGISTRATIONS);}const auto now=g_get_real_time()/1000;const auto timespan=start==INT64_MIN?0:std::max<int64_t>(0,now-start)*1000;auto* completion=new(std::nothrow) clear_context{callback,context,operation};if(!completion)return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK clear-data allocation failed");auto* manager=webkit_web_context_get_website_data_manager(state->context);webkit_website_data_manager_clear(manager,types,timespan,nullptr,clear_finished,completion);return NEOASTRA_OK;} // NOLINT(clang-analyzer-optin.core.EnumCastOutOfRange) WebKit data types are bit flags.
bool neo_platform_view_create_async(neoastra_view_t* view,const neoastra_view_options_t* options,neo_platform_created_callback_t callback,void* context,neoastra_error_t** error) noexcept {if(view->bridge_policy==NEOASTRA_BRIDGE_TRUSTED_ORIGINS){neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WebKitGTK 4.1 does not expose trustworthy web-message sender origins",0,"webkitgtk");return false;}auto* environment=static_cast<gtk_environment*>(view->environment->platform);auto* profile=view->profile?static_cast<gtk_profile*>(view->profile->platform):nullptr;auto* web_context=profile?profile->context:environment?environment->context:nullptr;auto* parent=view_parent(view);if(!web_context||!parent){neo_fail(error,NEOASTRA_ERROR_INVALID_STATE,"WebKitGTK context or parent is unavailable");return false;}auto* state=new(std::nothrow) gtk_view;if(!state)return false;state->content=webkit_user_content_manager_new();if(!state->content){delete state;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK could not allocate its user-content manager",0,"webkitgtk");return false;}if(view->bridge_policy!=NEOASTRA_BRIDGE_DISABLED&&!webkit_user_content_manager_register_script_message_handler(state->content,"_neoastra_transport_v1")){g_object_unref(state->content);delete state;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebKitGTK could not register the private transport handler",0,"webkitgtk");return false;}WebKitWebView* related{};if(options->popup_request&&options->popup_request->owner){auto* opener=static_cast<gtk_view*>(options->popup_request->owner->platform);if(opener)related=WEBKIT_WEB_VIEW(opener->widget);}state->widget=related?GTK_WIDGET(g_object_new(WEBKIT_TYPE_WEB_VIEW,"related-view",related,"user-content-manager",state->content,nullptr)):GTK_WIDGET(g_object_new(WEBKIT_TYPE_WEB_VIEW,"web-context",web_context,"user-content-manager",state->content,nullptr));view->platform=state;g_object_set_data(G_OBJECT(state->widget),"neoastra.native-view",view);state->load_changed=g_signal_connect(state->widget,"load-changed",G_CALLBACK(load_changed),view);state->load_failed=g_signal_connect(state->widget,"load-failed",G_CALLBACK(load_failed),view);g_signal_connect(state->widget,"decide-policy",G_CALLBACK(decide_policy),view);g_signal_connect(state->widget,"permission-request",G_CALLBACK(permission_requested),view);g_signal_connect(state->widget,"create",G_CALLBACK(create_web_view),view);g_signal_connect(state->widget,"script-dialog",G_CALLBACK(script_dialog),view);g_signal_connect(state->widget,"run-file-chooser",G_CALLBACK(run_file_chooser),view);g_signal_connect(state->widget,"authenticate",G_CALLBACK(authenticate),view);g_signal_connect(state->widget,"enter-fullscreen",G_CALLBACK(enter_fullscreen),view);state->title_changed=g_signal_connect(state->widget,"notify::title",G_CALLBACK(title_changed),view);state->uri_changed=g_signal_connect(state->widget,"notify::uri",G_CALLBACK(uri_changed),view);if(view->bridge_policy!=NEOASTRA_BRIDGE_DISABLED)state->message_received=g_signal_connect(state->content,"script-message-received::_neoastra_transport_v1",G_CALLBACK(message_received),view);state->process_terminated=g_signal_connect(state->widget,"web-process-terminated",G_CALLBACK(web_process_terminated),view);GtkTargetEntry drop_targets[]={{const_cast<gchar*>("text/uri-list"),0,0},{const_cast<gchar*>("text/plain;charset=utf-8"),0,1}};gtk_drag_dest_set(state->widget,GTK_DEST_DEFAULT_ALL,drop_targets,2,GDK_ACTION_COPY);state->drop_received=g_signal_connect(state->widget,"drag-data-received",G_CALLBACK(drag_data_received),view);if(GTK_IS_CONTAINER(parent))gtk_container_add(GTK_CONTAINER(parent),state->widget);gtk_widget_show(state->widget);callback(context,nullptr);return true;}
void neo_platform_view_destroy(neoastra_view_t* view) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state)return;for(auto& entry:state->scripts)webkit_user_script_unref(entry.second);if(state->widget)gtk_widget_destroy(state->widget);if(state->content){if(state->message_received)g_signal_handler_disconnect(state->content,state->message_received);if(view->bridge_policy!=NEOASTRA_BRIDGE_DISABLED)webkit_user_content_manager_unregister_script_message_handler(state->content,"_neoastra_transport_v1");g_object_unref(state->content);}delete state;view->platform=nullptr;}
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t* view) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;gtk_widget_set_size_request(state->widget,view->fill_parent?-1:view->bounds.width,view->fill_parent?-1:view->bounds.height);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_navigate(neoastra_view_t* view,const std::string& uri,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;webkit_web_view_load_uri(WEBKIT_WEB_VIEW(state->widget),uri.c_str());return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t* view,const std::string& uri,const std::string& method,const std::string& headers,const uint8_t*,uint64_t body_length,neoastra_error_t** error) noexcept {if(method=="GET"&&headers.empty()&&body_length==0)return neo_platform_view_navigate(view,uri,error);return neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WebKitGTK 4.1 does not expose arbitrary-method top-level navigation");}
neoastra_result_t neo_platform_view_load_html(neoastra_view_t* view,const std::string& html,const std::string& base,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;webkit_web_view_load_html(WEBKIT_WEB_VIEW(state->widget),html.c_str(),base.empty()?nullptr:base.c_str());return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_command(neoastra_view_t* view,uint32_t command) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;auto* web=WEBKIT_WEB_VIEW(state->widget);switch(command){case 0:webkit_web_view_stop_loading(web);break;case 1:webkit_web_view_reload(web);break;case 2:webkit_web_view_reload_bypass_cache(web);break;case 3:webkit_web_view_go_back(web);break;case 4:webkit_web_view_go_forward(web);break;default:return NEOASTRA_ERROR_INVALID_ARGUMENT;}return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t* view,const std::string& script,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;auto* completion=new(std::nothrow) script_context{view,callback,context,operation};if(!completion)return NEOASTRA_ERROR_NATIVE_FAILURE;webkit_web_view_evaluate_javascript(WEBKIT_WEB_VIEW(state->widget),script.data(),script.size(),nullptr,nullptr,nullptr,script_finished,completion);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_add_script(neoastra_view_t* view,const std::string& script,const neoastra_script_options_t* options,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->content)return NEOASTRA_ERROR_NOT_INITIALIZED;if(options->isolated_world)return NEOASTRA_ERROR_NOT_SUPPORTED;const auto frames=options->main_frame_only?WEBKIT_USER_CONTENT_INJECT_TOP_FRAME:WEBKIT_USER_CONTENT_INJECT_ALL_FRAMES;const auto time=options->injection_time==NEOASTRA_SCRIPT_DOCUMENT_END?WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_END:WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START;auto* user_script=webkit_user_script_new(script.c_str(),frames,time,nullptr,nullptr);if(!user_script)return NEOASTRA_ERROR_NATIVE_FAILURE;const auto identifier=std::to_string(state->next_script++);state->scripts.emplace(identifier,user_script);webkit_user_content_manager_add_script(state->content,user_script);g_idle_add(script_added,new add_script_context{callback,context,operation,identifier});return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t* view,const std::string& identifier) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->content)return NEOASTRA_ERROR_NOT_INITIALIZED;auto found=state->scripts.find(identifier);if(found==state->scripts.end())return NEOASTRA_ERROR_INVALID_ARGUMENT;webkit_user_script_unref(found->second);state->scripts.erase(found);webkit_user_content_manager_remove_all_scripts(state->content);for(const auto& entry:state->scripts)webkit_user_content_manager_add_script(state->content,entry.second);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_post_message(neoastra_view_t* view,const std::string& message,bool json,neoastra_error_t**) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;std::string script="window.dispatchEvent(new CustomEvent('neoastramessage',{detail:"+(json?message:("JSON.parse("+message+")"))+"}));";webkit_web_view_evaluate_javascript(WEBKIT_WEB_VIEW(state->widget),script.data(),script.size(),nullptr,nullptr,nullptr,nullptr,nullptr);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t* view,double* factor) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;*factor=webkit_web_view_get_zoom_level(WEBKIT_WEB_VIEW(state->widget));return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t* view,double factor) noexcept {auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;webkit_web_view_set_zoom_level(WEBKIT_WEB_VIEW(state->widget),factor);return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t* view,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* handle) noexcept {if(kind!=NEOASTRA_NATIVE_HANDLE_WEBKITGTK_WEBVIEW&&kind!=NEOASTRA_NATIVE_HANDLE_GTK_WIDGET)return NEOASTRA_ERROR_NOT_SUPPORTED;auto* state=static_cast<gtk_view*>(view->platform);if(!state||!state->widget)return NEOASTRA_ERROR_NOT_INITIALIZED;handle->kind=kind;handle->value=state->widget;return NEOASTRA_OK;}
