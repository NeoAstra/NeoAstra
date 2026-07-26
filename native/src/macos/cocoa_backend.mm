#include "../common/native_internal.hpp"

#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>
#import <dispatch/dispatch.h>
#import <objc/runtime.h>
#import <Security/Security.h>

#include <algorithm>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

namespace {
struct cocoa_app { };
struct cocoa_window;
struct cocoa_environment { WKProcessPool* process_pool; WKWebsiteDataStore* data_store; };
struct cocoa_profile { WKWebsiteDataStore* data_store; };
struct cocoa_view;

NSString* ns_string(const std::string& value) { return [[NSString alloc] initWithBytes:value.data() length:value.size() encoding:NSUTF8StringEncoding]; }
std::string utf8(NSString* value) { if(!value)return {};const char* text=value.UTF8String;return text?text:""; }
neoastra_error_t* make_error(neoastra_result_t code,const char* message,int64_t native_code=0,const char* domain="wkwebview") noexcept {neoastra_error_t* error{};neo_fail(&error,code,message,native_code,domain);return error;}
}

@interface NeoWindowDelegate : NSObject<NSWindowDelegate>
@property(nonatomic,assign) neoastra_window_t* nativeWindow;
@end
@interface NeoURLSchemeHandler : NSObject<WKURLSchemeHandler>
@property(nonatomic,assign) neoastra_environment_t* nativeEnvironment;
@property(nonatomic,assign) const neo_custom_scheme_registration* registration;
@end
@interface NeoAstraDelegate : NSObject<WKNavigationDelegate,WKScriptMessageHandler,WKUIDelegate,WKDownloadDelegate>
@property(nonatomic,assign) neoastra_view_t* nativeView;
@end

namespace {
struct cocoa_window { NSWindow* window; NeoWindowDelegate* delegate; neoastra_window_state_t reported_state{NEOASTRA_WINDOW_NORMAL}; };
struct cocoa_view {
    WKWebView* webview;
    NeoAstraDelegate* delegate;
    NSMutableArray<NeoURLSchemeHandler*>* scheme_handlers;
    std::unordered_map<std::string,WKUserScript*> scripts;
    ~cocoa_view() { if (delegate) delegate.nativeView=nullptr; }
};

std::string request_headers(NSURLRequest* request) {
    std::string result;
    for (NSString* name in request.allHTTPHeaderFields) {
        result += utf8(name);
        result += ": ";
        result += utf8(request.allHTTPHeaderFields[name]);
        result += "\r\n";
    }
    return result;
}

std::string url_origin(NSURL* url) {
    if (!url) return {};
    NSString* scheme=url.scheme.lowercaseString,*host=url.host.lowercaseString;
    if (!scheme.length || !host.length) return utf8(url.absoluteString);
    NSNumber* port=url.port;
    const bool default_port=port&&(([scheme isEqualToString:@"http"]&&port.integerValue==80)||([scheme isEqualToString:@"https"]&&port.integerValue==443));
    if ([host containsString:@":"] && ![host hasPrefix:@"["]) host=[NSString stringWithFormat:@"[%@]",host];
    NSString* value=port&&!default_port?[NSString stringWithFormat:@"%@://%@:%@",scheme,host,port]:[NSString stringWithFormat:@"%@://%@",scheme,host];
    return utf8(value);
}

NSMutableDictionary<NSString*,NSString*>* response_headers(const neoastra_resource_response_t& response) {
    NSMutableDictionary<NSString*,NSString*>* result=[NSMutableDictionary dictionary];
    NSString* raw=ns_string(neo_string(response.headers));
    for (NSString* line in [raw componentsSeparatedByCharactersInSet:[NSCharacterSet newlineCharacterSet]]) {
        NSRange separator=[line rangeOfString:@":"];
        if (separator.location==NSNotFound || separator.location==0) continue;
        NSString* name=[[line substringToIndex:separator.location] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
        NSString* value=[[line substringFromIndex:separator.location+1] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
        if (name.length) result[name]=value;
    }
    return result;
}

bool contains_header(NSDictionary<NSString*,NSString*>* headers,NSString* name) {
    for (NSString* candidate in headers) if ([candidate caseInsensitiveCompare:name]==NSOrderedSame) return true;
    return false;
}

struct resource_response_guard {
    neoastra_resource_response_t& response;
    void release() noexcept {
        if (response.release&&response.release_context) {
            try { response.release(response.release_context); } catch (...) { }
            response.release=nullptr;
            response.release_context=nullptr;
        }
    }
    ~resource_response_guard() { release(); }
};

void fail_scheme_task(id<WKURLSchemeTask> task,NSInteger code,NSString* message) noexcept {
    @try {
        NSError* error=[NSError errorWithDomain:@"NeoAstra.WKURLSchemeHandler" code:code userInfo:@{NSLocalizedDescriptionKey:message?:@"Custom-scheme request failed."}];
        [task didFailWithError:error];
    } @catch (NSException*) { }
}

struct navigation_context { void (^handler)(WKNavigationActionPolicy); };
void navigation_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<navigation_context> context(static_cast<navigation_context*>(pointer));context->handler(response->action==NEOASTRA_DECISION_ALLOW||response->action==NEOASTRA_DECISION_DEFAULT?WKNavigationActionPolicyAllow:WKNavigationActionPolicyCancel);}
struct permission_context { void (^handler)(WKPermissionDecision); };
void permission_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<permission_context> context(static_cast<permission_context*>(pointer));const auto decision=response->action==NEOASTRA_DECISION_ALLOW?WKPermissionDecisionGrant:response->action==NEOASTRA_DECISION_DEFAULT?WKPermissionDecisionPrompt:WKPermissionDecisionDeny;context->handler(decision);}
struct new_window_context { neoastra_view_t* view{}; std::string uri; };
void new_window_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {@autoreleasepool{std::unique_ptr<new_window_context> context(static_cast<new_window_context*>(pointer));NSURL* url=[NSURL URLWithString:ns_string(context->uri)];if(response->action==NEOASTRA_DECISION_ALLOW&&!response->target_view){auto* state=static_cast<cocoa_view*>(context->view->platform);if(state&&state->webview&&url)[state->webview loadRequest:[NSURLRequest requestWithURL:url]];}else if(response->action==NEOASTRA_DECISION_OPEN_EXTERNAL&&url){[[NSWorkspace sharedWorkspace]openURL:url];}}}
void finish_synchronous_decision(neoastra_decision_t* decision){const auto state=decision->state.load(std::memory_order_acquire);if(state==neo_decision_state::pending||state==neo_decision_state::deferred){neoastra_decision_response_t response{};response.size=sizeof(response);response.version=1;response.action=decision->default_action;neoastra_decision_complete(decision,&response,nullptr);}}
void release_popup_configuration(void* pointer) noexcept {if(pointer)CFRelease(pointer);}
struct dialog_context { void (^completion)(BOOL,NSString*); };
void dialog_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {@autoreleasepool{std::unique_ptr<dialog_context> context(static_cast<dialog_context*>(pointer));try{NSString* text=response->text.length?ns_string(neo_string(response->text)):nil;context->completion(response->action==NEOASTRA_DECISION_ALLOW,text);}catch(...){context->completion(NO,nil);}}}
struct chooser_context { void (^completion)(NSArray<NSURL*>*); };
void chooser_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {@autoreleasepool{std::unique_ptr<chooser_context> context(static_cast<chooser_context*>(pointer));if(response->action!=NEOASTRA_DECISION_ALLOW){context->completion(nil);return;}try{NSMutableArray<NSURL*>* urls=[NSMutableArray arrayWithCapacity:response->path_count];for(uint32_t i=0;i<response->path_count;++i)[urls addObject:[NSURL fileURLWithPath:ns_string(neo_string(response->paths[i]))]];context->completion(urls);}catch(...){context->completion(nil);}}}
struct auth_context { NSURLAuthenticationChallenge* challenge; void (^completion)(NSURLSessionAuthChallengeDisposition,NSURLCredential*); bool server_trust{}; };
void auth_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {@autoreleasepool{std::unique_ptr<auth_context> context(static_cast<auth_context*>(pointer));if(response->action==NEOASTRA_DECISION_CANCEL||response->action==NEOASTRA_DECISION_DENY){context->completion(NSURLSessionAuthChallengeCancelAuthenticationChallenge,nil);return;}if(response->action==NEOASTRA_DECISION_DEFAULT){context->completion(NSURLSessionAuthChallengePerformDefaultHandling,nil);return;}try{NSURLCredential* credential=nil;if(context->server_trust&&context->challenge.protectionSpace.serverTrust)credential=[NSURLCredential credentialForTrust:context->challenge.protectionSpace.serverTrust];else credential=[NSURLCredential credentialWithUser:ns_string(neo_string(response->text)) password:ns_string(neo_string(response->secondary_text)) persistence:NSURLCredentialPersistenceNone];context->completion(credential?NSURLSessionAuthChallengeUseCredential:NSURLSessionAuthChallengeCancelAuthenticationChallenge,credential);}catch(...){context->completion(NSURLSessionAuthChallengeCancelAuthenticationChallenge,nil);}}}
struct cocoa_download { WKDownload* value; NSProgress* progress; NSObject* observer; bool observes_completed{}; bool observes_total{}; };
neoastra_result_t cocoa_download_command(neoastra_download_t* download,uint32_t command) noexcept {auto* state=static_cast<cocoa_download*>(download->platform);if(!state||!state->value)return NEOASTRA_ERROR_DISPOSED;if(command!=0)return NEOASTRA_ERROR_NOT_SUPPORTED;[state->value cancel:nil];return NEOASTRA_OK;}
void destroy_cocoa_download(neoastra_download_t* download) noexcept {auto* state=static_cast<cocoa_download*>(download->platform);if(state){if(state->progress&&state->observer&&state->observes_completed){@try{[state->progress removeObserver:state->observer forKeyPath:@"completedUnitCount"];}@catch(NSException*){}}if(state->progress&&state->observer&&state->observes_total){@try{[state->progress removeObserver:state->observer forKeyPath:@"totalUnitCount"];}@catch(NSException*){}}if(state->progress)objc_setAssociatedObject(state->progress,@selector(downloadDidFinish:),nil,OBJC_ASSOCIATION_ASSIGN);if(state->value)objc_setAssociatedObject(state->value,@selector(downloadDidFinish:),nil,OBJC_ASSOCIATION_ASSIGN);delete state;}}
struct download_destination_context { neoastra_download_t* download{}; NSURL* default_destination; void (^completion)(NSURL*); };
void download_destination_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {@autoreleasepool{std::unique_ptr<download_destination_context> context(static_cast<download_destination_context*>(pointer));try{if(response->action==NEOASTRA_DECISION_DOWNLOAD){context->download->destination_path=neo_string(response->text);context->completion([NSURL fileURLWithPath:ns_string(context->download->destination_path)]);}else if(response->action==NEOASTRA_DECISION_ALLOW||response->action==NEOASTRA_DECISION_DEFAULT){context->download->destination_path=utf8(context->default_destination.path);context->completion(context->default_destination);}else context->completion(nil);}catch(...){context->completion(nil);}}}
void report_window_state(neoastra_window_t* value,neoastra_window_state_t state){auto* native=static_cast<cocoa_window*>(value->platform);if(!native||native->reported_state==state)return;native->reported_state=state;{std::lock_guard lock(value->state_mutex);value->state=state;}neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_STATE_CHANGED,value->id,nullptr,nullptr,state);}
}

@implementation NeoURLSchemeHandler
- (void)webView:(WKWebView*)webView startURLSchemeTask:(id<WKURLSchemeTask>)task {
    (void)webView;
    auto* environment=self.nativeEnvironment;
    const auto* scheme=self.registration;
    if (!environment||!scheme||!scheme->provider) { fail_scheme_task(task,NEOASTRA_ERROR_DISPOSED,@"The custom-scheme provider is unavailable.");return; }
    neoastra_resource_response_t response{};
    response.size=sizeof(response);
    response.version=1;
    resource_response_guard response_guard{response};
    @try {
        try {
            NSURLRequest* native_request=task.request;
            const auto uri=utf8(native_request.URL.absoluteString);
            const auto method=utf8(native_request.HTTPMethod.length?native_request.HTTPMethod:@"GET");
            const auto headers=request_headers(native_request);
            NSData* request_body=native_request.HTTPBody;
            if (request_body.length>neo_maximum_buffered_resource_body_size) {
                neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme request body exceeded the 64 MiB limit");
                fail_scheme_task(task,NEOASTRA_ERROR_INVALID_ARGUMENT,@"The custom-scheme request body is too large.");
                return;
            }
            const bool main_frame=!native_request.mainDocumentURL||[native_request.URL isEqual:native_request.mainDocumentURL];
            const auto initiating_origin=main_frame?std::string{}:url_origin(native_request.mainDocumentURL);
            if (!neo_resource_request_within_limits(uri,method,headers,initiating_origin)) {
                neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme request metadata exceeded its size limit");
                fail_scheme_task(task,NEOASTRA_ERROR_INVALID_ARGUMENT,@"The custom-scheme request metadata is too large.");
                return;
            }
            neoastra_resource_request_t request{};
            request.size=sizeof(request);
            request.version=1;
            request.uri=neo_string_view(uri);
            request.method=neo_string_view(method);
            request.headers=neo_string_view(headers);
            request.initiating_origin=neo_string_view(initiating_origin);
            request.resource_kind=main_frame?NEOASTRA_RESOURCE_DOCUMENT:NEOASTRA_RESOURCE_OTHER;
            request.main_frame=main_frame?1u:0u;
            request.body=static_cast<const uint8_t*>(request_body.bytes);
            request.body_length=request_body.length;
            neoastra_result_t provider_result=NEOASTRA_ERROR_NATIVE_FAILURE;
            try { provider_result=scheme->provider(scheme->provider_context,&request,&response); }
            catch (...) { provider_result=NEOASTRA_ERROR_NATIVE_FAILURE; }
            if (provider_result!=NEOASTRA_OK) {
                neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme resource provider failed",provider_result);
                response_guard.release();
                response={};response.size=sizeof(response);response.version=1;response.status_code=500;
            }
            if (!neo_valid_resource_response(response)||response.byte_length>NSUIntegerMax) {
                neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme resource provider returned an invalid response");
                fail_scheme_task(task,NEOASTRA_ERROR_INVALID_ARGUMENT,@"The custom-scheme provider returned an invalid response.");
                return;
            }
            NSMutableDictionary<NSString*,NSString*>* native_headers=response_headers(response);
            if (response.mime_type.length&&!contains_header(native_headers,@"Content-Type")) native_headers[@"Content-Type"]=ns_string(neo_string(response.mime_type));
            if (response.content_length!=UINT64_MAX&&!contains_header(native_headers,@"Content-Length")) native_headers[@"Content-Length"]=[NSString stringWithFormat:@"%llu",static_cast<unsigned long long>(response.content_length)];
            NSHTTPURLResponse* native_response=[[NSHTTPURLResponse alloc]initWithURL:native_request.URL statusCode:response.status_code HTTPVersion:@"HTTP/1.1" headerFields:native_headers];
            if (!native_response) { fail_scheme_task(task,NEOASTRA_ERROR_NATIVE_FAILURE,@"WKWebView could not create the custom-scheme response.");return; }
            NSData* data=nil;
            if (response.body_kind==NEOASTRA_RESOURCE_BODY_BYTES) data=[NSData dataWithBytes:response.bytes length:(NSUInteger)response.byte_length];
            else if (response.body_kind==NEOASTRA_RESOURCE_BODY_FILE) {
                NSError* file_error=nil;
                data=[NSData dataWithContentsOfFile:ns_string(neo_string(response.file_path)) options:NSDataReadingMappedIfSafe error:&file_error];
                if (!data) {
                    neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Could not map a custom-scheme file response",file_error.code);
                    [task didFailWithError:file_error?:[NSError errorWithDomain:@"NeoAstra.WKURLSchemeHandler" code:NEOASTRA_ERROR_NATIVE_FAILURE userInfo:nil]];
                    return;
                }
            }
            [task didReceiveResponse:native_response];
            if (data.length) [task didReceiveData:data];
            [task didFinish];
        } catch (...) {
            neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","Custom-scheme request handling failed");
            fail_scheme_task(task,NEOASTRA_ERROR_NATIVE_FAILURE,@"Custom-scheme request handling failed.");
        }
    } @catch (NSException* exception) {
        neo_log(environment->app,NEOASTRA_LOG_ERROR,"resource","WKWebView custom-scheme request handling raised an exception");
        fail_scheme_task(task,NEOASTRA_ERROR_NATIVE_FAILURE,exception.reason?:@"Custom-scheme request handling failed.");
    }
}
- (void)webView:(WKWebView*)webView stopURLSchemeTask:(id<WKURLSchemeTask>)task { (void)webView;(void)task; }
@end

@implementation NeoWindowDelegate
- (BOOL)windowShouldClose:(NSWindow*)sender { (void)sender;auto* value=self.nativeWindow;if(value)neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_CLOSE_REQUESTED,value->id);return YES; }
- (void)windowWillClose:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;if(value){auto* state=static_cast<cocoa_window*>(value->platform);if(state)state->window=nil;neo_window_closed(value);} }
- (void)windowDidResize:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;if(!value)return;NSWindow* window=static_cast<cocoa_window*>(value->platform)->window;NSRect frame=[window contentRectForFrameRect:window.frame];{std::lock_guard lock(value->state_mutex);value->bounds.width=(int32_t)frame.size.width;value->bounds.height=(int32_t)frame.size.height;}neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_RESIZED,value->id);if((window.styleMask&NSWindowStyleMaskFullScreen)!=0)report_window_state(value,NEOASTRA_WINDOW_FULLSCREEN);else if(window.miniaturized)report_window_state(value,NEOASTRA_WINDOW_MINIMIZED);else report_window_state(value,window.zoomed?NEOASTRA_WINDOW_MAXIMIZED:NEOASTRA_WINDOW_NORMAL); }
- (void)windowDidMove:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;if(value)neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_MOVED,value->id); }
- (void)windowDidBecomeKey:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;if(value)neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_FOCUS_CHANGED,value->id,nullptr,nullptr,1); }
- (void)windowDidResignKey:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;if(value)neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_FOCUS_CHANGED,value->id,nullptr,nullptr,0); }
- (void)windowDidMiniaturize:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;if(value)report_window_state(value,NEOASTRA_WINDOW_MINIMIZED); }
- (void)windowDidDeminiaturize:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;if(value)report_window_state(value,NEOASTRA_WINDOW_NORMAL); }
- (void)windowDidEnterFullScreen:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;if(value)report_window_state(value,NEOASTRA_WINDOW_FULLSCREEN); }
- (void)windowDidExitFullScreen:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;if(value)report_window_state(value,NEOASTRA_WINDOW_NORMAL); }
@end

@implementation NeoAstraDelegate
- (void)webView:(WKWebView*)webView decidePolicyForNavigationAction:(WKNavigationAction*)action decisionHandler:(void (^)(WKNavigationActionPolicy))handler {
    auto* view=self.nativeView;if(!view){handler(WKNavigationActionPolicyCancel);return;}std::string uri=utf8(action.request.URL.absoluteString);
    auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_NAVIGATION,NEOASTRA_DECISION_ALLOW);
    decision->completion=navigation_decided;decision->completion_context=new navigation_context{[handler copy]};
    uint64_t flags=1u|(action.navigationType==WKNavigationTypeLinkActivated?2u:0u);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_REQUESTED,0,nullptr,&uri,flags,0,decision);
    neo_finish_decision_event(view,decision);decision->release();
}
- (void)webView:(WKWebView*)webView didStartProvisionalNavigation:(WKNavigation*)navigation { (void)navigation;auto* view=self.nativeView;if(!view)return;std::string uri=utf8(webView.URL.absoluteString);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_STARTED,0,nullptr,&uri,1); }
- (void)webView:(WKWebView*)webView didFinishNavigation:(WKNavigation*)navigation { (void)navigation;auto* view=self.nativeView;if(!view)return;std::string uri=utf8(webView.URL.absoluteString);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_COMPLETED,0,nullptr,&uri); }
- (void)webView:(WKWebView*)webView didFailNavigation:(WKNavigation*)navigation withError:(NSError*)error { (void)navigation;auto* view=self.nativeView;if(!view)return;std::string uri=utf8(webView.URL.absoluteString);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_FAILED,0,nullptr,&uri,NEOASTRA_ERROR_NATIVE_FAILURE,error.code); }
- (void)webView:(WKWebView*)webView didFailProvisionalNavigation:(WKNavigation*)navigation withError:(NSError*)error { [self webView:webView didFailNavigation:navigation withError:error]; }
- (void)webViewWebContentProcessDidTerminate:(WKWebView*)webView { (void)webView;auto* view=self.nativeView;if(view)neo_emit_view(view,NEOASTRA_EVENT_WEB_PROCESS_TERMINATED,0,nullptr,nullptr,NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED|NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW); }
- (void)userContentController:(WKUserContentController*)controller didReceiveScriptMessage:(WKScriptMessage*)message { (void)controller;auto* view=self.nativeView;if(!view)return;@try{try{NSError* error=nil;NSData* data=[NSJSONSerialization dataWithJSONObject:message.body options:NSJSONWritingFragmentsAllowed error:&error];std::string text;if(data&&!error)text.assign((const char*)data.bytes,data.length);else text=utf8([message.body description]);std::string uri=url_origin(message.frameInfo.request.URL);neo_emit_bridge_message(view,text,uri,message.frameInfo.mainFrame);}catch(...){neo_log(view->environment->app,NEOASTRA_LOG_ERROR,"bridge","WKWebView web-message handling failed");}}@catch(NSException*){neo_log(view->environment->app,NEOASTRA_LOG_ERROR,"bridge","WKWebView web-message handling raised an exception");} }
- (void)webView:(WKWebView*)webView requestMediaCapturePermissionForOrigin:(WKSecurityOrigin*)origin initiatedByFrame:(WKFrameInfo*)frame type:(WKMediaCaptureType)type decisionHandler:(void (^)(WKPermissionDecision))handler API_AVAILABLE(macos(12.0)) { (void)webView;(void)frame;auto* view=self.nativeView;if(!view){handler(WKPermissionDecisionDeny);return;}NSString* raw=[NSString stringWithFormat:@"%@://%@:%ld",origin.protocol,origin.host,(long)origin.port];std::string uri=utf8(raw);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_PERMISSION,NEOASTRA_DECISION_DENY);decision->completion=permission_decided;decision->completion_context=new permission_context{[handler copy]};const auto kind=type==WKMediaCaptureTypeMicrophone?NEOASTRA_PERMISSION_MICROPHONE:type==WKMediaCaptureTypeCamera?NEOASTRA_PERMISSION_CAMERA:NEOASTRA_PERMISSION_UNKNOWN;neo_emit_view(view,NEOASTRA_EVENT_PERMISSION_REQUESTED,0,nullptr,&uri,kind,0,decision);neo_finish_decision_event(view,decision);decision->release(); }
- (WKWebView*)webView:(WKWebView*)webView createWebViewWithConfiguration:(WKWebViewConfiguration*)configuration forNavigationAction:(WKNavigationAction*)action windowFeatures:(WKWindowFeatures*)features { (void)webView;(void)features;auto* view=self.nativeView;if(!view)return nil;std::string uri=utf8(action.request.URL.absoluteString);std::string name;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_NEW_WINDOW,NEOASTRA_DECISION_CANCEL);decision->completion=new_window_decided;decision->completion_context=new new_window_context{view,uri};decision->popup_context=(void*)CFRetain((__bridge CFTypeRef)configuration);decision->popup_context_release=release_popup_configuration;neo_emit_view(view,NEOASTRA_EVENT_NEW_WINDOW_REQUESTED,0,&name,&uri,action.navigationType==WKNavigationTypeLinkActivated?1u:0u,0,decision);finish_synchronous_decision(decision);WKWebView* target=nil;if(decision->resolved_target){auto* state=static_cast<cocoa_view*>(decision->resolved_target->platform);if(state)target=state->webview;}decision->release();return target;}
- (void)webView:(WKWebView*)webView runJavaScriptAlertPanelWithMessage:(NSString*)message initiatedByFrame:(WKFrameInfo*)frame completionHandler:(void (^)(void))completionHandler { (void)webView;auto* view=self.nativeView;if(!view){completionHandler();return;}std::string text=utf8(message),origin=utf8(frame.request.URL.absoluteString);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_SCRIPT_DIALOG,NEOASTRA_DECISION_ALLOW);decision->completion=dialog_decided;decision->completion_context=new dialog_context{[^(BOOL,NSString*){completionHandler();} copy]};neo_emit_view(view,NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED,0,&text,&origin,NEOASTRA_SCRIPT_DIALOG_ALERT,0,decision);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView runJavaScriptConfirmPanelWithMessage:(NSString*)message initiatedByFrame:(WKFrameInfo*)frame completionHandler:(void (^)(BOOL))completionHandler { (void)webView;auto* view=self.nativeView;if(!view){completionHandler(NO);return;}std::string text=utf8(message),origin=utf8(frame.request.URL.absoluteString);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_SCRIPT_DIALOG,NEOASTRA_DECISION_CANCEL);decision->completion=dialog_decided;decision->completion_context=new dialog_context{[^(BOOL accepted,NSString*){completionHandler(accepted);} copy]};neo_emit_view(view,NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED,0,&text,&origin,NEOASTRA_SCRIPT_DIALOG_CONFIRM,0,decision);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView runJavaScriptTextInputPanelWithPrompt:(NSString*)prompt defaultText:(NSString*)defaultText initiatedByFrame:(WKFrameInfo*)frame completionHandler:(void (^)(NSString*))completionHandler { (void)webView;auto* view=self.nativeView;if(!view){completionHandler(nil);return;}std::string text=utf8(prompt),initial=utf8(defaultText),origin=utf8(frame.request.URL.absoluteString);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_SCRIPT_DIALOG,NEOASTRA_DECISION_CANCEL);decision->completion=dialog_decided;decision->completion_context=new dialog_context{[^(BOOL accepted,NSString* value){completionHandler(accepted?value:nil);} copy]};neo_event_details details{};details.text2=&initial;neo_emit_view_detailed(view,NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED,0,&text,&origin,NEOASTRA_SCRIPT_DIALOG_PROMPT,0,decision,details);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView runOpenPanelWithParameters:(WKOpenPanelParameters*)parameters initiatedByFrame:(WKFrameInfo*)frame completionHandler:(void (^)(NSArray<NSURL*>*))completionHandler { (void)webView;(void)frame;auto* view=self.nativeView;if(!view){completionHandler(nil);return;}std::string accepted;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_FILE_CHOOSER,NEOASTRA_DECISION_CANCEL);decision->completion=chooser_decided;decision->completion_context=new chooser_context{[completionHandler copy]};neo_emit_view(view,NEOASTRA_EVENT_FILE_CHOOSER_REQUESTED,0,&accepted,nullptr,parameters.allowsMultipleSelection?1u:0u,0,decision);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView didReceiveAuthenticationChallenge:(NSURLAuthenticationChallenge*)challenge completionHandler:(void (^)(NSURLSessionAuthChallengeDisposition,NSURLCredential*))completionHandler { (void)webView;auto* view=self.nativeView;if(!view){completionHandler(NSURLSessionAuthChallengeCancelAuthenticationChallenge,nil);return;}NSString* method=challenge.protectionSpace.authenticationMethod;const bool tls=[method isEqualToString:NSURLAuthenticationMethodServerTrust];const bool client=[method isEqualToString:NSURLAuthenticationMethodClientCertificate];if(client){completionHandler(NSURLSessionAuthChallengePerformDefaultHandling,nil);return;}if(tls&&challenge.protectionSpace.serverTrust&&SecTrustEvaluateWithError(challenge.protectionSpace.serverTrust,nullptr)){completionHandler(NSURLSessionAuthChallengePerformDefaultHandling,nil);return;}const auto kind=tls?NEOASTRA_DECISION_CERTIFICATE_ERROR:NEOASTRA_DECISION_AUTHENTICATION;const auto event=tls?NEOASTRA_EVENT_CERTIFICATE_ERROR:NEOASTRA_EVENT_AUTHENTICATION_REQUESTED;std::string host=utf8(challenge.protectionSpace.host),realm=utf8(challenge.protectionSpace.realm),scheme=utf8(method);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,kind,tls?NEOASTRA_DECISION_DENY:NEOASTRA_DECISION_DEFAULT);decision->completion=auth_decided;decision->completion_context=new auth_context{challenge,[completionHandler copy],tls};neo_event_details details{};details.text2=tls?nullptr:&realm;details.text3=&scheme;neo_emit_view_detailed(view,event,0,&host,nullptr,0,tls?0:challenge.protectionSpace.port,decision,details);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView navigationAction:(WKNavigationAction*)navigationAction didBecomeDownload:(WKDownload*)download API_AVAILABLE(macos(11.3)) { (void)webView;[self beginDownload:download request:navigationAction.request]; }
- (void)webView:(WKWebView*)webView navigationResponse:(WKNavigationResponse*)navigationResponse didBecomeDownload:(WKDownload*)download API_AVAILABLE(macos(11.3)) { (void)webView;[self beginDownload:download request:navigationResponse.response.URL? [NSURLRequest requestWithURL:navigationResponse.response.URL] : nil]; }
- (void)beginDownload:(WKDownload*)download request:(NSURLRequest*)request API_AVAILABLE(macos(11.3)) { auto* view=self.nativeView;if(!view){[download cancel:nil];return;}std::unique_ptr<neoastra_download> native;@try{try{native=std::make_unique<neoastra_download>(view);native->source_uri=utf8(request.URL.absoluteString);native->platform=new cocoa_download{download,download.progress,self};native->command=cocoa_download_command;native->platform_destroy=destroy_cocoa_download;auto* state=static_cast<cocoa_download*>(native->platform);objc_setAssociatedObject(download,@selector(downloadDidFinish:),[NSValue valueWithPointer:native.get()],OBJC_ASSOCIATION_RETAIN_NONATOMIC);objc_setAssociatedObject(download.progress,@selector(downloadDidFinish:),[NSValue valueWithPointer:native.get()],OBJC_ASSOCIATION_RETAIN_NONATOMIC);[download.progress addObserver:self forKeyPath:@"completedUnitCount" options:NSKeyValueObservingOptionNew context:nullptr];state->observes_completed=true;[download.progress addObserver:self forKeyPath:@"totalUnitCount" options:NSKeyValueObservingOptionNew context:nullptr];state->observes_total=true;download.delegate=self;native.release();}catch(...){native.reset();[download cancel:nil];}}@catch(NSException*){native.reset();[download cancel:nil];} }
- (void)download:(WKDownload*)download decideDestinationUsingResponse:(NSURLResponse*)response suggestedFilename:(NSString*)suggestedFilename completionHandler:(void (^)(NSURL*))completionHandler API_AVAILABLE(macos(11.3)) { auto* native=(neoastra_download_t*)[objc_getAssociatedObject(download,@selector(downloadDidFinish:)) pointerValue];if(!native){completionHandler(nil);return;}std::string filename=utf8(suggestedFilename),mime=utf8(response.MIMEType);native->total_bytes=response.expectedContentLength<0?UINT64_MAX:(uint64_t)response.expectedContentLength;NSURL* downloads=[[[NSFileManager defaultManager] URLsForDirectory:NSDownloadsDirectory inDomains:NSUserDomainMask] firstObject];NSURL* default_destination=[downloads URLByAppendingPathComponent:suggestedFilename];for(NSUInteger index=1;[[NSFileManager defaultManager]fileExistsAtPath:default_destination.path];++index){NSString* stem=[suggestedFilename stringByDeletingPathExtension],*extension=[suggestedFilename pathExtension];NSString* candidate=[NSString stringWithFormat:@"%@ (%lu)%@%@",stem,(unsigned long)index,extension.length?@".":@"",extension];default_destination=[downloads URLByAppendingPathComponent:candidate];}auto* decision=new neoastra_decision;neo_configure_decision(decision,native->view,NEOASTRA_DECISION_DOWNLOAD_REQUEST,NEOASTRA_DECISION_CANCEL);decision->completion=download_destination_decided;decision->completion_context=new download_destination_context{native,default_destination,[completionHandler copy]};neo_event_details details{};details.text2=&mime;details.value2=1;details.download=native;native->event_published=true;neo_emit_view_detailed(native->view,NEOASTRA_EVENT_DOWNLOAD_REQUESTED,native->id,&filename,&native->source_uri,native->total_bytes,0,decision,details);neo_finish_decision_event(native->view,decision);if(decision->resolved_action.load()==NEOASTRA_DECISION_DEFAULT||decision->resolved_action.load()==NEOASTRA_DECISION_ALLOW||decision->resolved_action.load()==NEOASTRA_DECISION_DOWNLOAD){native->state.store(NEOASTRA_DOWNLOAD_IN_PROGRESS);neo_download_emit(native,NEOASTRA_EVENT_DOWNLOAD_STARTED);}decision->release(); }
- (void)downloadDidFinish:(WKDownload*)download API_AVAILABLE(macos(11.3)) { auto* native=(neoastra_download_t*)[objc_getAssociatedObject(download,@selector(downloadDidFinish:)) pointerValue];if(!native)return;auto expected=NEOASTRA_DOWNLOAD_IN_PROGRESS;if(native->state.compare_exchange_strong(expected,NEOASTRA_DOWNLOAD_COMPLETED)){native->bytes_received.store(native->total_bytes.load());neo_download_emit(native,NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED);neo_download_emit(native,NEOASTRA_EVENT_DOWNLOAD_COMPLETED);native->release_lifecycle();} }
- (void)download:(WKDownload*)download didFailWithError:(NSError*)error resumeData:(NSData*)resumeData API_AVAILABLE(macos(11.3)) { (void)resumeData;auto* native=(neoastra_download_t*)[objc_getAssociatedObject(download,@selector(downloadDidFinish:)) pointerValue];if(!native)return;const auto terminal=error.code==NSURLErrorCancelled?NEOASTRA_DOWNLOAD_CANCELED:NEOASTRA_DOWNLOAD_FAILED;auto expected=NEOASTRA_DOWNLOAD_IN_PROGRESS;if(!native->state.compare_exchange_strong(expected,terminal)){expected=NEOASTRA_DOWNLOAD_REQUESTED;if(!native->state.compare_exchange_strong(expected,terminal))return;}try{native->failure_reason=utf8(error.localizedDescription);}catch(...){ }neo_download_emit(native,NEOASTRA_EVENT_DOWNLOAD_COMPLETED);native->release_lifecycle(); }
- (void)observeValueForKeyPath:(NSString*)keyPath ofObject:(id)object change:(NSDictionary<NSKeyValueChangeKey,id>*)change context:(void*)context { (void)change;(void)context;if([object isKindOfClass:[NSProgress class]]){auto* native=(neoastra_download_t*)[objc_getAssociatedObject(object,@selector(downloadDidFinish:)) pointerValue];if(native&&native->state.load()==NEOASTRA_DOWNLOAD_IN_PROGRESS){NSProgress* progress=(NSProgress*)object;native->bytes_received.store(progress.completedUnitCount<0?0:(uint64_t)progress.completedUnitCount);native->total_bytes.store(progress.totalUnitCount<0?UINT64_MAX:(uint64_t)progress.totalUnitCount);neo_download_emit(native,NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED);}return;}auto* view=self.nativeView;if(!view)return;WKWebView* webview=(WKWebView*)object;if([keyPath isEqualToString:@"title"]){view->title=utf8(webview.title);neo_emit_view(view,NEOASTRA_EVENT_TITLE_CHANGED,0,&view->title);}else if([keyPath isEqualToString:@"URL"]){view->source=utf8(webview.URL.absoluteString);neo_emit_view(view,NEOASTRA_EVENT_SOURCE_CHANGED,0,nullptr,&view->source);} }
@end

namespace {
NSView* view_parent(neoastra_view_t* view) {if(view->window){auto* state=static_cast<cocoa_window*>(view->window->platform);return state?state->window.contentView:nil;}return view->parent.kind==NEOASTRA_NATIVE_PARENT_COCOA_NSVIEW?(__bridge NSView*)view->parent.handle:nil;}
NSRect view_frame(neoastra_view_t* view,NSView* parent){if(view->fill_parent)return parent.bounds;return NSMakeRect(view->bounds.x,view->bounds.y,std::max(view->bounds.width,1),std::max(view->bounds.height,1));}
}

bool neo_platform_initialize(neoastra_app_t* app,neoastra_error_t**) noexcept {@autoreleasepool{[NSApplication sharedApplication];[NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];app->platform=new cocoa_app;return true;}}
void neo_platform_shutdown(neoastra_app_t* app) noexcept {delete static_cast<cocoa_app*>(app->platform);app->platform=nullptr;}
bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept {dispatch_async(dispatch_get_main_queue(),^{neo_destroy_app_on_ui(app);});return true;}
int32_t neo_platform_run(neoastra_app_t* app) noexcept {@autoreleasepool{[NSApp run];return app->exit_code.load();}}
void neo_platform_quit(neoastra_app_t*) noexcept {dispatch_async(dispatch_get_main_queue(),^{[NSApp stop:nil];NSEvent* event=[NSEvent otherEventWithType:NSEventTypeApplicationDefined location:NSZeroPoint modifierFlags:0 timestamp:0 windowNumber:0 context:nil subtype:0 data1:0 data2:0];[NSApp postEvent:event atStart:NO];});}
void neo_platform_wake(neoastra_app_t* app) noexcept {app->retain();dispatch_async(dispatch_get_main_queue(),^{neo_drain_dispatch(app);app->release();});}
bool neo_platform_schedule_decision_timeout(neoastra_view_t*,neoastra_decision_t* decision) noexcept {const auto remaining=std::chrono::duration_cast<std::chrono::nanoseconds>(decision->deadline-std::chrono::steady_clock::now()).count();decision->retain();dispatch_after(dispatch_time(DISPATCH_TIME_NOW,std::max<int64_t>(remaining,1)),dispatch_get_main_queue(),^{decision->expire();decision->release();});return true;}

bool neo_platform_window_create(neoastra_window_t* window,const neoastra_window_options_t* options,neoastra_error_t**) noexcept {@autoreleasepool{auto* state=new cocoa_window{};window->platform=state;NSWindowStyleMask style=NSWindowStyleMaskClosable|NSWindowStyleMaskMiniaturizable;if(options->flags&2u)style|=NSWindowStyleMaskTitled;if(options->flags&1u)style|=NSWindowStyleMaskResizable;state->window=[[NSWindow alloc]initWithContentRect:NSMakeRect(window->bounds.x,window->bounds.y,window->bounds.width,window->bounds.height) styleMask:style backing:NSBackingStoreBuffered defer:NO];state->delegate=[NeoWindowDelegate new];state->delegate.nativeWindow=window;state->window.delegate=state->delegate;state->window.title=ns_string(window->title);state->window.releasedWhenClosed=NO;if(options->minimum_size.width>0||options->minimum_size.height>0)state->window.contentMinSize=NSMakeSize(std::max(options->minimum_size.width,0),std::max(options->minimum_size.height,0));if(options->maximum_size.width>0||options->maximum_size.height>0)state->window.contentMaxSize=NSMakeSize(options->maximum_size.width>0?options->maximum_size.width:CGFLOAT_MAX,options->maximum_size.height>0?options->maximum_size.height:CGFLOAT_MAX);if(options->flags&8u)state->window.level=NSFloatingWindowLevel;if(options->flags&4u){[state->window makeKeyAndOrderFront:nil];if(options->state==NEOASTRA_WINDOW_MINIMIZED)[state->window miniaturize:nil];else if(options->state==NEOASTRA_WINDOW_MAXIMIZED)[state->window zoom:nil];else if(options->state==NEOASTRA_WINDOW_FULLSCREEN)[state->window toggleFullScreen:nil];}[NSApp activateIgnoringOtherApps:YES];return true;}}
void neo_platform_window_destroy(neoastra_window_t* window) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_window*>(window->platform);if(!state)return;state->delegate.nativeWindow=nullptr;state->window.delegate=nil;if(state->window)[state->window close];delete state;window->platform=nullptr;}}
neoastra_result_t neo_platform_window_show(neoastra_window_t* window,bool visible) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;if(!visible){[state->window orderOut:nil];return NEOASTRA_OK;}neoastra_window_state_t desired{};{std::lock_guard lock(window->state_mutex);desired=window->state;}[state->window orderFront:nil];{std::lock_guard lock(window->state_mutex);window->state=desired;}return neo_platform_window_set_state(window);}
neoastra_result_t neo_platform_window_activate(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;[state->window makeKeyAndOrderFront:nil];[NSApp activateIgnoringOtherApps:YES];return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_close(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;[state->window performClose:nil];return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_title(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;state->window.title=ns_string(window->title);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;[state->window setFrame:NSMakeRect(window->bounds.x,window->bounds.y,window->bounds.width,window->bounds.height) display:YES];return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;state->window.contentMinSize=NSMakeSize(std::max(window->minimum_size.width,0),std::max(window->minimum_size.height,0));state->window.contentMaxSize=NSMakeSize(window->maximum_size.width>0?window->maximum_size.width:CGFLOAT_MAX,window->maximum_size.height>0?window->maximum_size.height:CGFLOAT_MAX);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_state(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;if(!state->window.visible&&(state->window.styleMask&NSWindowStyleMaskFullScreen)==0)return NEOASTRA_OK;switch(window->state){case NEOASTRA_WINDOW_NORMAL:if(state->window.miniaturized)[state->window deminiaturize:nil];if(state->window.zoomed)[state->window zoom:nil];if((state->window.styleMask&NSWindowStyleMaskFullScreen)!=0)[state->window toggleFullScreen:nil];break;case NEOASTRA_WINDOW_MINIMIZED:[state->window miniaturize:nil];break;case NEOASTRA_WINDOW_MAXIMIZED:if(!state->window.zoomed)[state->window zoom:nil];break;case NEOASTRA_WINDOW_FULLSCREEN:if((state->window.styleMask&NSWindowStyleMaskFullScreen)==0)[state->window toggleFullScreen:nil];break;default:return NEOASTRA_ERROR_INVALID_ARGUMENT;}return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_get_handle(neoastra_window_t* window,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* handle) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;if(kind==NEOASTRA_NATIVE_HANDLE_COCOA_NSWINDOW)handle->value=(__bridge void*)state->window;else if(kind==NEOASTRA_NATIVE_HANDLE_COCOA_NSVIEW)handle->value=(__bridge void*)state->window.contentView;else return NEOASTRA_ERROR_NOT_SUPPORTED;handle->kind=kind;return NEOASTRA_OK;}

bool neo_platform_environment_create_async(neoastra_environment_t* environment,const neoastra_environment_options_t* options,neo_platform_created_callback_t callback,void* context,neoastra_error_t** error) noexcept {
    @autoreleasepool {
        @try {
            try {
                for (const auto& scheme : environment->custom_schemes) {
                    NSString* name=ns_string(scheme.name);
                    if ((scheme.flags&NEOASTRA_CUSTOM_SCHEME_SERVICE_WORKERS)!=0) {
                        neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WKWebView custom schemes do not support service workers",0,"wkwebview");
                        return false;
                    }
                    if (!name||[WKWebView handlesURLScheme:name]) {
                        neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WKWebView cannot replace a built-in URL scheme",0,"wkwebview");
                        return false;
                    }
                }
                auto state=std::make_unique<cocoa_environment>();
                state->process_pool=[WKProcessPool new];
                state->data_store=options->private_mode?[WKWebsiteDataStore nonPersistentDataStore]:[WKWebsiteDataStore defaultDataStore];
                environment->platform=state.release();
                callback(context,nullptr);
                return true;
            } catch (const std::exception& exception) {
                neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,exception.what(),0,"wkwebview");
                return false;
            }
        } @catch (NSException* exception) {
            neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,utf8(exception.reason).c_str(),0,"wkwebview");
            return false;
        }
    }
}
void neo_platform_environment_destroy(neoastra_environment_t* environment) noexcept {delete static_cast<cocoa_environment*>(environment->platform);environment->platform=nullptr;}
bool neo_platform_profile_create(neoastra_profile_t* profile,neoastra_error_t** error) noexcept {@autoreleasepool{try{auto* state=new cocoa_profile{};state->data_store=profile->ephemeral?[WKWebsiteDataStore nonPersistentDataStore]:[WKWebsiteDataStore defaultDataStore];profile->platform=state;return true;}catch(...){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Could not allocate WKWebView profile state");return false;}}}
void neo_platform_profile_destroy(neoastra_profile_t* profile) noexcept {delete static_cast<cocoa_profile*>(profile->platform);profile->platform=nullptr;}
neoastra_result_t neo_platform_profile_get_cookies(neoastra_profile_t* profile,const std::string& uri,neoastra_buffer_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_profile*>(profile->platform);NSURL* url=[NSURL URLWithString:ns_string(uri)];if(!state||!state->data_store||!url)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"Invalid WKWebView cookie URI");[state->data_store.httpCookieStore getAllCookies:^(NSArray<NSHTTPCookie*>* values){@autoreleasepool{NSMutableArray* output=[NSMutableArray array];NSString* host=url.host.lowercaseString;NSString* request_path=url.path.length?url.path:@"/";for(NSHTTPCookie* cookie in values){NSString* domain=cookie.domain.lowercaseString;BOOL domain_match=[host isEqualToString:domain]||([domain hasPrefix:@"."]&&[host hasSuffix:domain]);BOOL path_match=[request_path hasPrefix:cookie.path.length?cookie.path:@"/"];if(!domain_match||!path_match||(cookie.secure&&![url.scheme.lowercaseString isEqualToString:@"https"]))continue;NSMutableDictionary* item=[@{@"name":cookie.name,@"value":cookie.value,@"domain":cookie.domain,@"path":cookie.path.length?cookie.path:@"/",@"secure":@(cookie.secure),@"httpOnly":@(cookie.HTTPOnly),@"sameSite":@0} mutableCopy];if(cookie.expiresDate)item[@"expiresUnixMs"]=@((int64_t)(cookie.expiresDate.timeIntervalSince1970*1000.0));[output addObject:item];}NSError* serialization_error=nil;NSData* data=[NSJSONSerialization dataWithJSONObject:output options:0 error:&serialization_error];neoastra_result_t requested=serialization_error?NEOASTRA_ERROR_NATIVE_FAILURE:NEOASTRA_OK,actual{};neoastra_error_t* native_error=serialization_error?make_error(requested,utf8(serialization_error.localizedDescription).c_str(),serialization_error.code):nullptr;auto* buffer=data?new neoastra_buffer(std::vector<uint8_t>((const uint8_t*)data.bytes,(const uint8_t*)data.bytes+data.length)):nullptr;if(operation->try_complete(requested,actual)){callback(context,actual,actual==NEOASTRA_OK?buffer:nullptr,actual==requested?native_error:nullptr);if(actual!=NEOASTRA_OK&&buffer)buffer->release();}else if(buffer)buffer->release();if(native_error)native_error->release();operation->release();}}];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_profile_set_cookie(neoastra_profile_t* profile,const neoastra_cookie_t* cookie,neoastra_completion_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_profile*>(profile->platform);if(!state||!state->data_store)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WKWebView profile is not initialized");NSMutableDictionary* properties=[@{NSHTTPCookieName:ns_string(neo_string(cookie->name)),NSHTTPCookieValue:ns_string(neo_string(cookie->value)),NSHTTPCookieDomain:ns_string(neo_string(cookie->domain)),NSHTTPCookiePath:ns_string(neo_string(cookie->path))} mutableCopy];if(cookie->flags&1u)properties[NSHTTPCookieSecure]=@"TRUE";if(cookie->flags&2u)properties[@"HttpOnly"]=@"TRUE";if((cookie->flags&4u)==0&&cookie->expires_unix_ms>0)properties[NSHTTPCookieExpires]=[NSDate dateWithTimeIntervalSince1970:cookie->expires_unix_ms/1000.0];NSHTTPCookie* value=[NSHTTPCookie cookieWithProperties:properties];if(!value)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"Invalid WKWebView cookie");[state->data_store.httpCookieStore setCookie:value completionHandler:^{neoastra_result_t actual{};if(operation->try_complete(NEOASTRA_OK,actual))callback(context,actual,nullptr);operation->release();}];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_profile_delete_cookie(neoastra_profile_t* profile,const neoastra_cookie_t* cookie,neoastra_completion_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_profile*>(profile->platform);if(!state||!state->data_store)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WKWebView profile is not initialized");NSDictionary* properties=@{NSHTTPCookieName:ns_string(neo_string(cookie->name)),NSHTTPCookieValue:ns_string(neo_string(cookie->value)),NSHTTPCookieDomain:ns_string(neo_string(cookie->domain)),NSHTTPCookiePath:ns_string(neo_string(cookie->path))};NSHTTPCookie* value=[NSHTTPCookie cookieWithProperties:properties];if(!value)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"Invalid WKWebView cookie");[state->data_store.httpCookieStore deleteCookie:value completionHandler:^{neoastra_result_t actual{};if(operation->try_complete(NEOASTRA_OK,actual))callback(context,actual,nullptr);operation->release();}];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_profile_clear_data(neoastra_profile_t* profile,neoastra_data_kind_t kinds,int64_t start,int64_t end,neoastra_completion_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_profile*>(profile->platform);if(!state||!state->data_store)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WKWebView profile is not initialized");if(end!=INT64_MAX||(kinds!=NEOASTRA_DATA_ALL&&(kinds&(NEOASTRA_DATA_SERVICE_WORKERS|NEOASTRA_DATA_PERMISSIONS|NEOASTRA_DATA_DOWNLOAD_HISTORY))))return neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WKWebView cannot clear the selected data kinds or bounded end time");NSMutableSet<NSString*>* types=[NSMutableSet set];if(kinds==NEOASTRA_DATA_ALL)[types unionSet:[WKWebsiteDataStore allWebsiteDataTypes]];else{if(kinds&NEOASTRA_DATA_COOKIES)[types addObject:WKWebsiteDataTypeCookies];if(kinds&NEOASTRA_DATA_CACHE){[types addObject:WKWebsiteDataTypeDiskCache];[types addObject:WKWebsiteDataTypeMemoryCache];}if(kinds&NEOASTRA_DATA_LOCAL_STORAGE)[types addObject:WKWebsiteDataTypeLocalStorage];if(kinds&NEOASTRA_DATA_INDEXED_DB)[types addObject:WKWebsiteDataTypeIndexedDBDatabases];}NSDate* since=start==INT64_MIN?[NSDate distantPast]:[NSDate dateWithTimeIntervalSince1970:start/1000.0];[state->data_store removeDataOfTypes:types modifiedSince:since completionHandler:^{neoastra_result_t actual{};if(operation->try_complete(NEOASTRA_OK,actual))callback(context,actual,nullptr);operation->release();}];return NEOASTRA_OK;}}
bool neo_platform_view_create_async(neoastra_view_t* view,const neoastra_view_options_t* options,neo_platform_created_callback_t callback,void* context,neoastra_error_t** error) noexcept {
    @autoreleasepool {
        @try {
            try {
                auto* environment=static_cast<cocoa_environment*>(view->environment->platform);
                NSView* parent=view_parent(view);
                if (!environment||!parent) {
                    neo_fail(error,NEOASTRA_ERROR_INVALID_STATE,"WKWebView environment or parent is unavailable");
                    return false;
                }
                auto state=std::make_unique<cocoa_view>();
                WKWebViewConfiguration* configuration=options->popup_request&&options->popup_request->popup_context?
                    (__bridge WKWebViewConfiguration*)options->popup_request->popup_context:[WKWebViewConfiguration new];
                if (!configuration) {
                    neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WKWebView could not allocate a view configuration",0,"wkwebview");
                    return false;
                }
                if (!options->popup_request) {
                    configuration.processPool=environment->process_pool;
                    auto* profile=view->profile?static_cast<cocoa_profile*>(view->profile->platform):nullptr;
                    configuration.websiteDataStore=profile?profile->data_store:environment->data_store;
                    state->scheme_handlers=[NSMutableArray arrayWithCapacity:view->environment->custom_schemes.size()];
                    for (const auto& scheme : view->environment->custom_schemes) {
                        NeoURLSchemeHandler* handler=[NeoURLSchemeHandler new];
                        if (!handler) throw std::bad_alloc();
                        handler.nativeEnvironment=view->environment;
                        handler.registration=&scheme;
                        [configuration setURLSchemeHandler:handler forURLScheme:ns_string(scheme.name)];
                        [state->scheme_handlers addObject:handler];
                    }
                }
                state->delegate=[NeoAstraDelegate new];
                if (!state->delegate) throw std::bad_alloc();
                state->delegate.nativeView=view;
                if (options->popup_request) {
                    WKUserContentController* content=[WKUserContentController new];
                    if (!content) throw std::bad_alloc();
                    for (WKUserScript* script in configuration.userContentController.userScripts) [content addUserScript:script];
                    configuration.userContentController=content;
                }
                if (view->bridge_policy != NEOASTRA_BRIDGE_DISABLED)
                    [configuration.userContentController addScriptMessageHandler:state->delegate name:@"_neoastra_transport_v1"];
                state->webview=[[WKWebView alloc]initWithFrame:view_frame(view,parent) configuration:configuration];
                if (!state->webview) {
                    neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WKWebView could not create the browser view",0,"wkwebview");
                    return false;
                }
                state->webview.navigationDelegate=state->delegate;
                state->webview.UIDelegate=state->delegate;
                state->webview.allowsMagnification=YES;
                if (view->fill_parent) state->webview.autoresizingMask=NSViewWidthSizable|NSViewHeightSizable;
                [state->webview addObserver:state->delegate forKeyPath:@"title" options:NSKeyValueObservingOptionNew context:nullptr];
                [state->webview addObserver:state->delegate forKeyPath:@"URL" options:NSKeyValueObservingOptionNew context:nullptr];
                [parent addSubview:state->webview];
                view->platform=state.release();
                callback(context,nullptr);
                return true;
            } catch (const std::exception& exception) {
                neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,exception.what(),0,"wkwebview");
                return false;
            }
        } @catch (NSException* exception) {
            neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,utf8(exception.reason).c_str(),0,"wkwebview");
            return false;
        }
    }
}
void neo_platform_view_destroy(neoastra_view_t* view) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state)return;@try{[state->webview removeObserver:state->delegate forKeyPath:@"title"];[state->webview removeObserver:state->delegate forKeyPath:@"URL"];}@catch(NSException*){}state->delegate.nativeView=nullptr;state->webview.navigationDelegate=nil;state->webview.UIDelegate=nil;if(view->bridge_policy!=NEOASTRA_BRIDGE_DISABLED)[state->webview.configuration.userContentController removeScriptMessageHandlerForName:@"_neoastra_transport_v1"];[state->webview removeFromSuperview];delete state;view->platform=nullptr;}}
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t* view) noexcept {auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;NSView* parent=view_parent(view);state->webview.frame=view_frame(view,parent);state->webview.autoresizingMask=view->fill_parent?(NSViewWidthSizable|NSViewHeightSizable):NSViewNotSizable;return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_navigate(neoastra_view_t* view,const std::string& uri,neoastra_error_t** error) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);NSURL* url=[NSURL URLWithString:ns_string(uri)];if(!state||!state->webview||!url)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"Invalid WKWebView navigation URI");[state->webview loadRequest:[NSURLRequest requestWithURL:url]];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t* view,const std::string& uri,const std::string& method,const std::string& headers,const uint8_t* body,uint64_t body_length,neoastra_error_t** error) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);NSURL* url=[NSURL URLWithString:ns_string(uri)];if(!state||!state->webview||!url||method.empty()||body_length>NSUIntegerMax)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"Invalid WKWebView navigation request");NSMutableURLRequest* request=[NSMutableURLRequest requestWithURL:url];request.HTTPMethod=ns_string(method);if(body_length)request.HTTPBody=[NSData dataWithBytes:body length:(NSUInteger)body_length];for(NSString* line in [ns_string(headers) componentsSeparatedByCharactersInSet:[NSCharacterSet newlineCharacterSet]]){NSRange separator=[line rangeOfString:@":"];if(separator.location==NSNotFound)continue;NSString* name=[[line substringToIndex:separator.location] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];NSString* value=[[line substringFromIndex:separator.location+1] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];if(name.length)[request setValue:value forHTTPHeaderField:name];}[state->webview loadRequest:request];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_load_html(neoastra_view_t* view,const std::string& html,const std::string& base,neoastra_error_t**) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;NSURL* url=base.empty()?nil:[NSURL URLWithString:ns_string(base)];[state->webview loadHTMLString:ns_string(html) baseURL:url];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_command(neoastra_view_t* view,uint32_t command) noexcept {auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;switch(command){case 0:[state->webview stopLoading];break;case 1:case 2:[state->webview reload];break;case 3:[state->webview goBack];break;case 4:[state->webview goForward];break;default:return NEOASTRA_ERROR_INVALID_ARGUMENT;}return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t* view,const std::string& script,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t**) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;[state->webview evaluateJavaScript:ns_string(script) completionHandler:^(id value,NSError* error){neoastra_result_t requested=error?NEOASTRA_ERROR_NATIVE_FAILURE:NEOASTRA_OK,actual{};neoastra_error_t* native_error=error?make_error(requested,utf8(error.localizedDescription).c_str(),error.code):nullptr;std::string output="null";if(value){NSData* data=[NSJSONSerialization dataWithJSONObject:value options:NSJSONWritingFragmentsAllowed error:nil];if(data)output.assign((const char*)data.bytes,data.length);}if(operation->try_complete(requested,actual))callback(context,actual,actual==NEOASTRA_OK?neo_string_view(output):neoastra_string_view_t{},native_error);if(native_error)native_error->release();operation->release();}];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_add_script(neoastra_view_t* view,const std::string& script,const neoastra_script_options_t* options,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t**) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;if(options->isolated_world)return NEOASTRA_ERROR_NOT_SUPPORTED;auto time=options->injection_time==NEOASTRA_SCRIPT_DOCUMENT_END?WKUserScriptInjectionTimeAtDocumentEnd:WKUserScriptInjectionTimeAtDocumentStart;WKUserScript* user_script=[[WKUserScript alloc]initWithSource:ns_string(script) injectionTime:time forMainFrameOnly:options->main_frame_only!=0];std::string identifier=utf8([NSUUID UUID].UUIDString);state->scripts.emplace(identifier,user_script);[state->webview.configuration.userContentController addUserScript:user_script];dispatch_async(dispatch_get_main_queue(),^{neoastra_result_t actual{};if(operation->try_complete(NEOASTRA_OK,actual))callback(context,actual,actual==NEOASTRA_OK?neo_string_view(identifier):neoastra_string_view_t{},nullptr);operation->release();});return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t* view,const std::string& identifier) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;auto found=state->scripts.find(identifier);if(found==state->scripts.end())return NEOASTRA_ERROR_INVALID_ARGUMENT;state->scripts.erase(found);auto* manager=state->webview.configuration.userContentController;[manager removeAllUserScripts];for(const auto& entry:state->scripts)[manager addUserScript:entry.second];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_post_message(neoastra_view_t* view,const std::string& message,bool json,neoastra_error_t**) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;std::string script="window.dispatchEvent(new CustomEvent('neoastramessage',{detail:"+(json?message:"null")+"}));";[state->webview evaluateJavaScript:ns_string(script) completionHandler:nil];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t* view,double* factor) noexcept {auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;*factor=state->webview.magnification;return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t* view,double factor) noexcept {auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;state->webview.magnification=factor;return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t* view,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* handle) noexcept {if(kind!=NEOASTRA_NATIVE_HANDLE_WKWEBVIEW&&kind!=NEOASTRA_NATIVE_HANDLE_COCOA_NSVIEW)return NEOASTRA_ERROR_NOT_SUPPORTED;auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;handle->kind=kind;handle->value=(__bridge void*)state->webview;return NEOASTRA_OK;}
