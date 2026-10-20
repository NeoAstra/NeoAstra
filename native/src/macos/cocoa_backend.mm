#include "../common/native_internal.hpp"

#import <Cocoa/Cocoa.h>
#import <CommonCrypto/CommonDigest.h>
#import <WebKit/WebKit.h>
#import <dispatch/dispatch.h>
#import <objc/runtime.h>
#import <Security/Security.h>

#include <algorithm>
#include <cstdio>
#include <filesystem>
#include <memory>
#include <string>
#include <system_error>
#include <unordered_map>
#include <vector>

namespace {
// `wake_pending` is true, under the platform mutex, while a drain block waits on the main queue: that one block stands for all queued work.
struct cocoa_app { id previous_delegate; id delegate; neoastra_decision_t* termination_decision{}; bool wake_pending{}; id screen_observer; };
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
@interface NeoApplicationDelegate : NSObject<NSApplicationDelegate>
@property(nonatomic,assign) neoastra_app_t* nativeApplication;
@property(nonatomic,strong) id<NSApplicationDelegate> previousDelegate;
@end
@interface NeoURLSchemeHandler : NSObject<WKURLSchemeHandler>
@property(nonatomic,assign) neoastra_environment_t* nativeEnvironment;
@property(nonatomic,assign) const neo_custom_scheme_registration* registration;
@end
@interface NeoAstraDelegate : NSObject<WKNavigationDelegate,WKScriptMessageHandler,WKUIDelegate,WKDownloadDelegate>
@property(nonatomic,assign) neoastra_view_t* nativeView;
@end

@interface NeoAstraWebView : WKWebView
@property(nonatomic,assign) neoastra_view_t* nativeView;
@property(nonatomic,assign) BOOL contextMenusDisabled;
@end

@implementation NeoAstraWebView
// AppKit shows nothing for an empty menu; page-level contextmenu handlers have already run by now.
- (void)willOpenMenu:(NSMenu*)menu withEvent:(NSEvent*)event { if(self.contextMenusDisabled)[menu removeAllItems];else [super willOpenMenu:menu withEvent:event]; }
- (BOOL)performDragOperation:(id<NSDraggingInfo>)sender {
    auto* view=self.nativeView;
    neo_retain_guard owner(view);
    BOOL handled=[super performDragOperation:sender];
    if(!owner)return handled;
    // A drag that started in a web view of this application (an in-page HTML5 drag) is the page's alone: WebKit has
    // dispatched its drop. NeoAstra's own outbound drags have a source of their own and still reach the host.
    for(id source=sender.draggingSource;[source isKindOfClass:[NSView class]];source=((NSView*)source).superview)
        if([source isKindOfClass:[WKWebView class]])return handled;
    @try {
        NSPasteboard* pasteboard=sender.draggingPasteboard;
        NSArray<NSURL*>* urls=[pasteboard readObjectsForClasses:@[[NSURL class]] options:@{NSPasteboardURLReadingFileURLsOnlyKey:@YES}];
        if(urls.count>256)return handled;
        NSPoint point=[self convertPoint:sender.draggingLocation fromView:nil];if(!self.isFlipped)point.y=self.bounds.size.height-point.y;
        neo_event_details details{};details.bounds={static_cast<int32_t>(point.x),static_cast<int32_t>(point.y),0,0};
        std::string paths;uint64_t path_count=0;
        for(NSURL* url in urls){
            const char* value=url.standardizedURL.fileSystemRepresentation;
            if(!value||!*value)continue;
            const size_t length=strlen(value);
            if(length>32768||paths.size()+length+1>1024*1024)return handled;
            if(!paths.empty())paths.push_back('\0');
            paths.append(value,length);++path_count;
        }
        if(!paths.empty()){neo_emit_view_detailed(view,NEOASTRA_EVENT_MESSAGE_RECEIVED,0,&paths,nullptr,(UINT64_C(1)<<63)|(UINT64_C(2)<<56)|path_count,0,nullptr,details);handled=YES;}
        else {
            NSString* value=[pasteboard stringForType:NSPasteboardTypeURL];uint64_t kind=1;
            if(!value){value=[pasteboard stringForType:NSPasteboardTypeString];kind=0;}
            std::string text;if(value&&[value lengthOfBytesUsingEncoding:NSUTF8StringEncoding]<=32768)text=utf8(value);if(!text.empty()){neo_emit_view_detailed(view,NEOASTRA_EVENT_MESSAGE_RECEIVED,0,&text,nullptr,(UINT64_C(1)<<63)|(kind<<56)|1,0,nullptr,details);handled=YES;}
        }
    } @catch(NSException*) { neo_log(view->environment->app,NEOASTRA_LOG_WARNING,"drag-drop","WKWebView native drop decoding failed"); }
    return handled;
}
@end

namespace {
struct cocoa_window { NSWindow* window; NeoWindowDelegate* delegate; neoastra_window_state_t reported_state{NEOASTRA_WINDOW_NORMAL}; bool modal{}; bool modal_active{}; };
struct cocoa_view {
    WKWebView* webview;
    NeoAstraDelegate* delegate;
    NSMutableArray<NeoURLSchemeHandler*>* scheme_handlers;
    std::unordered_map<std::string,WKUserScript*> scripts;
    uint64_t history{};
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

// The public interface of WKNavigationAction has no user-gesture flag. A link that the user clicks, or activates with the
// Enter key, comes with the button of a trusted click, and a link that a script clicks comes without one.
uint64_t link_activation(WKNavigationAction* action,uint64_t user_initiated) {
    const bool activated=action.navigationType==WKNavigationTypeLinkActivated;
    return NEOASTRA_REQUEST_LINK_ACTIVATION_REPORTED|(activated?NEOASTRA_REQUEST_LINK_ACTIVATED:0)|(activated&&action.buttonNumber!=0?user_initiated:0);
}
// WKWebView names the reload and the history navigation of a submitted form with one type, which does not tell the kind.
uint64_t navigation_kind(WKNavigationAction* action) {
    switch(action.navigationType){
        case WKNavigationTypeBackForward:return NEOASTRA_NAVIGATION_REQUEST_KIND_BACK_FORWARD;
        case WKNavigationTypeReload:return NEOASTRA_NAVIGATION_REQUEST_KIND_RELOAD;
        case WKNavigationTypeFormResubmitted:return NEOASTRA_NAVIGATION_REQUEST_KIND_UNKNOWN;
        default:return NEOASTRA_NAVIGATION_REQUEST_KIND_NEW_DOCUMENT;
    }
}
struct navigation_context { void (^handler)(WKNavigationActionPolicy); std::string uri; };
void navigation_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {@autoreleasepool{std::unique_ptr<navigation_context> context(static_cast<navigation_context*>(pointer));const bool allowed=response->action==NEOASTRA_DECISION_ALLOW||response->action==NEOASTRA_DECISION_DEFAULT;context->handler(allowed?WKNavigationActionPolicyAllow:WKNavigationActionPolicyCancel);}}
// A page that asks for a microphone and a camera together makes one request, which WKWebView answers once. The host is asked
// about each device, as WebView2 asks: the page gets both when the host allows both and nothing when it refuses one, and WebKit
// asks the user itself when the host refuses neither and leaves one to it.
struct permission_context { void (^handler)(WKPermissionDecision); int pending{1}; WKPermissionDecision outcome{WKPermissionDecisionGrant}; };
void permission_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {
    auto* context=static_cast<permission_context*>(pointer);
    if(response->action==NEOASTRA_DECISION_DEFAULT){if(context->outcome==WKPermissionDecisionGrant)context->outcome=WKPermissionDecisionPrompt;}
    else if(response->action!=NEOASTRA_DECISION_ALLOW)context->outcome=WKPermissionDecisionDeny;
    if(--context->pending>0)return;
    context->handler(context->outcome);delete context;
}
struct new_window_context { neoastra_view_t* view{}; std::string uri; };
void new_window_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {@autoreleasepool{std::unique_ptr<new_window_context> context(static_cast<new_window_context*>(pointer));NSURL* url=[NSURL URLWithString:ns_string(context->uri)];if(response->action==NEOASTRA_DECISION_ALLOW&&!response->target_view){auto* state=static_cast<cocoa_view*>(context->view->platform);if(state&&state->webview&&url)[state->webview loadRequest:[NSURLRequest requestWithURL:url]];}}}
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
struct download_destination_context {
    neoastra_download_t* download;
    NSURL* default_destination;
    void (^completion)(NSURL*);

    download_destination_context(neoastra_download_t* value,NSURL* destination,void (^handler)(NSURL*))
        : download(value),default_destination(destination),completion([handler copy]) { download->retain(); }
    ~download_destination_context() { download->release(); }
};
void download_destination_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {
    @autoreleasepool {
        std::unique_ptr<download_destination_context> context(static_cast<download_destination_context*>(pointer));
        auto* download=context->download;
        neo_retain_guard owner(download);
        neo_retain_guard view_owner(download->view);
        if(!owner||!view_owner||download->ui_destroyed.load()||download->state.load()!=NEOASTRA_DOWNLOAD_REQUESTED){context->completion(nil);return;}
        NSURL* destination=nil;
        try {
            if(response->action==NEOASTRA_DECISION_DOWNLOAD){download->destination_path=neo_string(response->text);destination=[NSURL fileURLWithPath:ns_string(download->destination_path)];}
            else if(response->action==NEOASTRA_DECISION_ALLOW||response->action==NEOASTRA_DECISION_DEFAULT){download->destination_path=utf8(context->default_destination.path);destination=context->default_destination;}
        } catch (...) { }
        if(destination)download->state.store(NEOASTRA_DOWNLOAD_IN_PROGRESS);
        context->completion(destination);
        if(destination&&download->state.load()==NEOASTRA_DOWNLOAD_IN_PROGRESS)neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_STARTED);
    }
}
// WebKit changes canGoBack and canGoForward together and notifies once for each, so the repeated pair is not reported twice.
void report_history(neoastra_view_t* view,WKWebView* webview){auto* state=static_cast<cocoa_view*>(view->platform);if(!state)return;const uint64_t history=neo_history_directions(view,webview.canGoBack,webview.canGoForward);if(state->history==history)return;state->history=history;neo_emit_view(view,NEOASTRA_EVENT_HISTORY_CHANGED,0,nullptr,nullptr,history);}
void report_window_state(neoastra_window_t* value,neoastra_window_state_t state){auto* native=static_cast<cocoa_window*>(value->platform);if(!native||native->reported_state==state)return;native->reported_state=state;{std::lock_guard lock(value->state_mutex);value->state=state;}neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_STATE_CHANGED,value->id,nullptr,nullptr,state);}
// Portable bounds pair the window's top-left corner with the size of its content area. The corner is measured downward
// from the top of the primary display, as CoreGraphics display bounds are, while AppKit frames grow upward from its bottom.
CGFloat primary_display_height(){NSArray<NSScreen*>* screens=NSScreen.screens;return screens.count?NSHeight(screens[0].frame):0;}
NSRect frame_for_bounds(NSWindow* window,const neoastra_rect_t& bounds){const NSRect frame=[window frameRectForContentRect:NSMakeRect(0,0,std::max(bounds.width,1),std::max(bounds.height,1))];return NSMakeRect(bounds.x,primary_display_height()-bounds.y-NSHeight(frame),NSWidth(frame),NSHeight(frame));}
void sync_bounds(neoastra_window_t* value,NSWindow* window){if(!window)return;const NSRect frame=window.frame,content=[window contentRectForFrameRect:frame];std::lock_guard lock(value->state_mutex);value->bounds={(int32_t)lround(NSMinX(frame)),(int32_t)lround(primary_display_height()-NSMaxY(frame)),(int32_t)lround(NSWidth(content)),(int32_t)lround(NSHeight(content))};}
}

@implementation NeoURLSchemeHandler
@synthesize nativeEnvironment=_nativeEnvironment;
- (void)setNativeEnvironment:(neoastra_environment_t*)environment {
    if(environment==_nativeEnvironment)return;
    if(environment&&!environment->retain())environment=nullptr;
    auto* previous=_nativeEnvironment;
    _nativeEnvironment=environment;
    if(previous)previous->release();
}
- (void)dealloc { if(_nativeEnvironment)_nativeEnvironment->release(); }
- (void)webView:(WKWebView*)webView startURLSchemeTask:(id<WKURLSchemeTask>)task {
    (void)webView;
    auto* environment=self.nativeEnvironment;
    neo_retain_guard environment_owner(environment);
    neo_retain_guard view_owner([webView isKindOfClass:[NeoAstraWebView class]]?((NeoAstraWebView*)webView).nativeView:nullptr);
    const auto* scheme=self.registration;
    if (!environment_owner||!scheme||!scheme->provider) { fail_scheme_task(task,NEOASTRA_ERROR_DISPOSED,@"The custom-scheme provider is unavailable.");return; }
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
- (BOOL)windowShouldClose:(NSWindow*)sender { (void)sender;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(!owner)return YES;if(value->force_closing){value->force_closing=false;return YES;}neo_window_request_close(value,NEOASTRA_WINDOW_CLOSE_USER,true);return NO; }
- (void)windowWillClose:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(owner){auto* state=static_cast<cocoa_window*>(value->platform);if(state&&state->modal_active&&state->window.sheetParent){NSWindow* native=state->window;state->modal_active=false;[native.sheetParent endSheet:native];state=static_cast<cocoa_window*>(value->platform);}if(state)state->window=nil;neo_window_closed(value);} }
- (void)windowDidResize:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(!owner)return;auto* state=static_cast<cocoa_window*>(value->platform);if(!state)return;NSWindow* window=state->window;sync_bounds(value,window);neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_RESIZED,value->id);if((window.styleMask&NSWindowStyleMaskFullScreen)!=0)report_window_state(value,NEOASTRA_WINDOW_FULLSCREEN);else if(window.miniaturized)report_window_state(value,NEOASTRA_WINDOW_MINIMIZED);else report_window_state(value,window.zoomed?NEOASTRA_WINDOW_MAXIMIZED:NEOASTRA_WINDOW_NORMAL); }
- (void)windowDidChangeBackingProperties:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(!owner)return;auto* state=static_cast<cocoa_window*>(value->platform);if(state&&state->window)neo_window_set_scale_factor(value,state->window.backingScaleFactor); }
- (void)windowDidMove:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(!owner)return;auto* state=static_cast<cocoa_window*>(value->platform);if(state)sync_bounds(value,state->window);neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_MOVED,value->id);neo_window_display_changed(value); }
- (void)windowDidChangeScreen:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(owner)neo_window_display_changed(value); }
- (void)windowDidBecomeKey:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(owner)neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_FOCUS_CHANGED,value->id,nullptr,nullptr,1); }
- (void)windowDidResignKey:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(owner)neo_emit_app(value->app,NEOASTRA_EVENT_WINDOW_FOCUS_CHANGED,value->id,nullptr,nullptr,0); }
- (void)windowDidMiniaturize:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(owner)report_window_state(value,NEOASTRA_WINDOW_MINIMIZED); }
- (void)windowDidDeminiaturize:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(owner)report_window_state(value,NEOASTRA_WINDOW_NORMAL); }
- (void)windowDidEnterFullScreen:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(owner)report_window_state(value,NEOASTRA_WINDOW_FULLSCREEN); }
- (void)windowDidExitFullScreen:(NSNotification*)notification { (void)notification;auto* value=self.nativeWindow;neo_retain_guard owner(value);if(owner)report_window_state(value,NEOASTRA_WINDOW_NORMAL); }
@end

@implementation NeoApplicationDelegate
- (BOOL)respondsToSelector:(SEL)selector { return [super respondsToSelector:selector]||[self.previousDelegate respondsToSelector:selector]; }
- (id)forwardingTargetForSelector:(SEL)selector { return [self.previousDelegate respondsToSelector:selector]?self.previousDelegate:[super forwardingTargetForSelector:selector]; }
- (BOOL)applicationShouldHandleReopen:(NSApplication*)sender hasVisibleWindows:(BOOL)visible { auto* app=self.nativeApplication;neo_retain_guard owner(app);id<NSApplicationDelegate> previous=self.previousDelegate;if(owner)neo_emit_app(app,NEOASTRA_EVENT_APPLICATION_ACTIVATED);return previous&&[previous respondsToSelector:@selector(applicationShouldHandleReopen:hasVisibleWindows:)]?[previous applicationShouldHandleReopen:sender hasVisibleWindows:visible]:YES; }
- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication*)sender { auto* app=self.nativeApplication;neo_retain_guard owner(app);id<NSApplicationDelegate> previous=self.previousDelegate;const auto previousReply=previous&&[previous respondsToSelector:@selector(applicationShouldTerminate:)]?[previous applicationShouldTerminate:sender]:NSTerminateNow;if(previousReply!=NSTerminateNow)return previousReply;if(!owner)return NSTerminateNow;auto* state=static_cast<cocoa_app*>(app->platform);if(!state)return NSTerminateNow;if(state->termination_decision)return NSTerminateLater;auto* decision=new(std::nothrow) neoastra_decision_t;if(!decision){neo_emit_app(app,NEOASTRA_EVENT_APPLICATION_SESSION_END,0,nullptr,nullptr,2);return NSTerminateNow;}decision->kind=NEOASTRA_DECISION_APPLICATION_QUIT;decision->default_action=NEOASTRA_DECISION_CANCEL;decision->deadline=std::chrono::steady_clock::now()+std::chrono::seconds(30);decision->attach_app(app);decision->completion=[](void* context,const neoastra_decision_response_t* response) noexcept {@autoreleasepool{auto* nativeApp=static_cast<neoastra_app_t*>(context);auto* nativeState=nativeApp?static_cast<cocoa_app*>(nativeApp->platform):nullptr;if(nativeState&&nativeState->termination_decision){auto* retained=nativeState->termination_decision;nativeState->termination_decision=nullptr;retained->release();}[NSApp replyToApplicationShouldTerminate:response&&(response->action==NEOASTRA_DECISION_ALLOW||response->action==NEOASTRA_DECISION_DEFAULT)];}};decision->completion_context=app;decision->retain();state->termination_decision=decision;neo_emit_app(app,NEOASTRA_EVENT_APPLICATION_SESSION_END,0,nullptr,nullptr,0,1,decision);neo_finish_decision_event(app,decision);decision->release();return NSTerminateLater; }
- (void)application:(NSApplication*)sender openFiles:(NSArray<NSString*>*)filenames { auto* app=self.nativeApplication;neo_retain_guard owner(app);id<NSApplicationDelegate> previous=self.previousDelegate;if(owner)for(NSString* filename in filenames){auto value=utf8(filename);if(value.size()<=4096)neo_emit_app(app,NEOASTRA_EVENT_APPLICATION_OPEN_FILE,0,&value);}if(previous&&[previous respondsToSelector:@selector(application:openFiles:)])[previous application:sender openFiles:filenames];else[sender replyToOpenOrPrint:NSApplicationDelegateReplySuccess]; }
- (void)application:(NSApplication*)application openURLs:(NSArray<NSURL*>*)urls { auto* app=self.nativeApplication;neo_retain_guard owner(app);id<NSApplicationDelegate> previous=self.previousDelegate;if(owner)for(NSURL* url in urls){auto value=utf8(url.absoluteString);if(value.size()<=4096)neo_emit_app(app,NEOASTRA_EVENT_APPLICATION_OPEN_URL,0,nullptr,&value);}if(previous&&[previous respondsToSelector:@selector(application:openURLs:)])[previous application:application openURLs:urls]; }
@end

@implementation NeoAstraDelegate
- (void)webView:(WKWebView*)webView decidePolicyForNavigationAction:(WKNavigationAction*)action decisionHandler:(void (^)(WKNavigationActionPolicy))handler {
    auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner){handler(WKNavigationActionPolicyCancel);return;}
    // An action without a target frame asks for a new window: WebKit asks to create its view next, and that request decides.
    if(!action.targetFrame){handler(WKNavigationActionPolicyAllow);return;}
    const uint64_t kind=navigation_kind(action);
    if(neo_refuses_navigation(view,kind)){handler(WKNavigationActionPolicyCancel);return;}
    std::string uri=utf8(action.request.URL.absoluteString);
    auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_NAVIGATION,NEOASTRA_DECISION_ALLOW);
    decision->completion=navigation_decided;decision->completion_context=new navigation_context{[handler copy],uri};decision->external_uri=uri;
    uint64_t flags=(action.targetFrame.isMainFrame?NEOASTRA_NAVIGATION_REQUEST_MAIN_FRAME:0)|link_activation(action,NEOASTRA_NAVIGATION_REQUEST_USER_INITIATED)|kind;neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_REQUESTED,0,nullptr,&uri,flags,0,decision);
    neo_finish_decision_event(view,decision);decision->release();
}
- (void)webView:(WKWebView*)webView didStartProvisionalNavigation:(WKNavigation*)navigation { (void)navigation;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner)return;std::string uri=utf8(webView.URL.absoluteString);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_STARTED,0,nullptr,&uri,1); }
- (void)webView:(WKWebView*)webView didCommitNavigation:(WKNavigation*)navigation { (void)navigation;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner)return;std::string uri=utf8(webView.URL.absoluteString);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_COMMITTED,0,nullptr,&uri); }
- (void)webView:(WKWebView*)webView didFinishNavigation:(WKNavigation*)navigation { (void)navigation;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner)return;std::string uri=utf8(webView.URL.absoluteString);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_COMPLETED,0,nullptr,&uri); }
- (void)webView:(WKWebView*)webView didFailNavigation:(WKNavigation*)navigation withError:(NSError*)error { (void)navigation;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner)return;std::string uri=utf8(webView.URL.absoluteString);neo_emit_view(view,NEOASTRA_EVENT_NAVIGATION_FAILED,0,nullptr,&uri,NEOASTRA_ERROR_NATIVE_FAILURE,error.code); }
- (void)webView:(WKWebView*)webView didFailProvisionalNavigation:(WKNavigation*)navigation withError:(NSError*)error { [self webView:webView didFailNavigation:navigation withError:error]; }
- (void)webViewWebContentProcessDidTerminate:(WKWebView*)webView { (void)webView;auto* view=self.nativeView;neo_retain_guard owner(view);if(owner)neo_emit_view(view,NEOASTRA_EVENT_WEB_PROCESS_TERMINATED,0,nullptr,nullptr,NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED|NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW); }
- (void)userContentController:(WKUserContentController*)controller didReceiveScriptMessage:(WKScriptMessage*)message { (void)controller;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner)return;@try{try{NSError* error=nil;NSData* data=[NSJSONSerialization dataWithJSONObject:message.body options:NSJSONWritingFragmentsAllowed error:&error];std::string text;if(data&&!error)text.assign((const char*)data.bytes,data.length);else text=utf8([message.body description]);std::string uri=url_origin(message.frameInfo.request.URL);neo_emit_bridge_message(view,text,uri,message.frameInfo.mainFrame);}catch(...){neo_log(view->environment->app,NEOASTRA_LOG_ERROR,"bridge","WKWebView web-message handling failed");}}@catch(NSException*){neo_log(view->environment->app,NEOASTRA_LOG_ERROR,"bridge","WKWebView web-message handling raised an exception");} }
// WKWebView gives the origin of the top document, with a port of zero for the default one, which is left out.
- (void)webView:(WKWebView*)webView requestMediaCapturePermissionForOrigin:(WKSecurityOrigin*)origin initiatedByFrame:(WKFrameInfo*)frame type:(WKMediaCaptureType)type decisionHandler:(void (^)(WKPermissionDecision))handler API_AVAILABLE(macos(12.0)) { (void)webView;(void)frame;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner){handler(WKPermissionDecisionDeny);return;}NSString* raw=origin.port?[NSString stringWithFormat:@"%@://%@:%ld",origin.protocol,origin.host,(long)origin.port]:[NSString stringWithFormat:@"%@://%@",origin.protocol,origin.host];std::string uri=utf8(raw);
    const bool both=type==WKMediaCaptureTypeCameraAndMicrophone;const int count=both?2:1;
    const neoastra_permission_kind_t kinds[2]={both||type==WKMediaCaptureTypeMicrophone?NEOASTRA_PERMISSION_MICROPHONE:type==WKMediaCaptureTypeCamera?NEOASTRA_PERMISSION_CAMERA:NEOASTRA_PERMISSION_UNKNOWN,NEOASTRA_PERMISSION_CAMERA};
    // Every decision is answered once, by the host or by its default, and the last answer settles the request.
    auto* context=new permission_context{[handler copy],count};
    for(int index=0;index<count;++index){
        auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_PERMISSION,NEOASTRA_DECISION_DENY);decision->completion=permission_decided;decision->completion_context=context;
        neo_emit_view(view,NEOASTRA_EVENT_PERMISSION_REQUESTED,0,nullptr,&uri,kinds[index],0,decision);neo_finish_decision_event(view,decision);decision->release();
    }
}
- (WKWebView*)webView:(WKWebView*)webView createWebViewWithConfiguration:(WKWebViewConfiguration*)configuration forNavigationAction:(WKNavigationAction*)action windowFeatures:(WKWindowFeatures*)features { (void)webView;(void)features;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner)return nil;std::string uri=utf8(action.request.URL.absoluteString);std::string name;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_NEW_WINDOW,NEOASTRA_DECISION_CANCEL);decision->completion=new_window_decided;decision->completion_context=new new_window_context{view,uri};decision->external_uri=uri;decision->popup_context=(void*)CFRetain((__bridge CFTypeRef)configuration);decision->popup_context_release=release_popup_configuration;neo_emit_view(view,NEOASTRA_EVENT_NEW_WINDOW_REQUESTED,0,&name,&uri,link_activation(action,NEOASTRA_NEW_WINDOW_REQUEST_USER_INITIATED),0,decision);finish_synchronous_decision(decision);WKWebView* target=nil;if(decision->resolved_target){auto* state=static_cast<cocoa_view*>(decision->resolved_target->platform);if(state)target=state->webview;}decision->release();return target;}
- (void)webView:(WKWebView*)webView runJavaScriptAlertPanelWithMessage:(NSString*)message initiatedByFrame:(WKFrameInfo*)frame completionHandler:(void (^)(void))completionHandler { (void)webView;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner){completionHandler();return;}std::string text=utf8(message),origin=utf8(frame.request.URL.absoluteString);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_SCRIPT_DIALOG,NEOASTRA_DECISION_ALLOW);decision->completion=dialog_decided;decision->completion_context=new dialog_context{[^(BOOL,NSString*){completionHandler();} copy]};neo_emit_view(view,NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED,0,&text,&origin,NEOASTRA_SCRIPT_DIALOG_ALERT,0,decision);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView runJavaScriptConfirmPanelWithMessage:(NSString*)message initiatedByFrame:(WKFrameInfo*)frame completionHandler:(void (^)(BOOL))completionHandler { (void)webView;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner){completionHandler(NO);return;}std::string text=utf8(message),origin=utf8(frame.request.URL.absoluteString);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_SCRIPT_DIALOG,NEOASTRA_DECISION_CANCEL);decision->completion=dialog_decided;decision->completion_context=new dialog_context{[^(BOOL accepted,NSString*){completionHandler(accepted);} copy]};neo_emit_view(view,NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED,0,&text,&origin,NEOASTRA_SCRIPT_DIALOG_CONFIRM,0,decision);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView runJavaScriptTextInputPanelWithPrompt:(NSString*)prompt defaultText:(NSString*)defaultText initiatedByFrame:(WKFrameInfo*)frame completionHandler:(void (^)(NSString*))completionHandler { (void)webView;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner){completionHandler(nil);return;}std::string text=utf8(prompt),initial=utf8(defaultText),origin=utf8(frame.request.URL.absoluteString);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_SCRIPT_DIALOG,NEOASTRA_DECISION_CANCEL);decision->completion=dialog_decided;decision->completion_context=new dialog_context{[^(BOOL accepted,NSString* value){completionHandler(accepted?value:nil);} copy]};neo_event_details details{};details.text2=&initial;neo_emit_view_detailed(view,NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED,0,&text,&origin,NEOASTRA_SCRIPT_DIALOG_PROMPT,0,decision,details);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView runOpenPanelWithParameters:(WKOpenPanelParameters*)parameters initiatedByFrame:(WKFrameInfo*)frame completionHandler:(void (^)(NSArray<NSURL*>*))completionHandler { (void)webView;(void)frame;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner){completionHandler(nil);return;}std::string accepted;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_FILE_CHOOSER,NEOASTRA_DECISION_CANCEL);decision->completion=chooser_decided;decision->completion_context=new chooser_context{[completionHandler copy]};neo_emit_view(view,NEOASTRA_EVENT_FILE_CHOOSER_REQUESTED,0,&accepted,nullptr,parameters.allowsMultipleSelection?1u:0u,0,decision);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView didReceiveAuthenticationChallenge:(NSURLAuthenticationChallenge*)challenge completionHandler:(void (^)(NSURLSessionAuthChallengeDisposition,NSURLCredential*))completionHandler { (void)webView;auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner){completionHandler(NSURLSessionAuthChallengeCancelAuthenticationChallenge,nil);return;}NSString* method=challenge.protectionSpace.authenticationMethod;const bool tls=[method isEqualToString:NSURLAuthenticationMethodServerTrust];const bool client=[method isEqualToString:NSURLAuthenticationMethodClientCertificate];if(client){completionHandler(NSURLSessionAuthChallengePerformDefaultHandling,nil);return;}if(tls&&challenge.protectionSpace.serverTrust&&SecTrustEvaluateWithError(challenge.protectionSpace.serverTrust,nullptr)){completionHandler(NSURLSessionAuthChallengePerformDefaultHandling,nil);return;}const auto kind=tls?NEOASTRA_DECISION_CERTIFICATE_ERROR:NEOASTRA_DECISION_AUTHENTICATION;const auto event=tls?NEOASTRA_EVENT_CERTIFICATE_ERROR:NEOASTRA_EVENT_AUTHENTICATION_REQUESTED;std::string host=utf8(challenge.protectionSpace.host),realm=utf8(challenge.protectionSpace.realm),scheme=utf8(method);auto* decision=new neoastra_decision;neo_configure_decision(decision,view,kind,tls?NEOASTRA_DECISION_DENY:NEOASTRA_DECISION_DEFAULT);decision->completion=auth_decided;decision->completion_context=new auth_context{challenge,[completionHandler copy],tls};neo_event_details details{};details.text2=tls?nullptr:&realm;details.text3=&scheme;neo_emit_view_detailed(view,event,0,&host,nullptr,0,tls?0:challenge.protectionSpace.port,decision,details);neo_finish_decision_event(view,decision);decision->release();}
- (void)webView:(WKWebView*)webView navigationAction:(WKNavigationAction*)navigationAction didBecomeDownload:(WKDownload*)download API_AVAILABLE(macos(11.3)) { (void)webView;[self beginDownload:download request:navigationAction.request]; }
- (void)webView:(WKWebView*)webView navigationResponse:(WKNavigationResponse*)navigationResponse didBecomeDownload:(WKDownload*)download API_AVAILABLE(macos(11.3)) { (void)webView;[self beginDownload:download request:navigationResponse.response.URL? [NSURLRequest requestWithURL:navigationResponse.response.URL] : nil]; }
- (void)beginDownload:(WKDownload*)download request:(NSURLRequest*)request API_AVAILABLE(macos(11.3)) { auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner){[download cancel:nil];return;}std::unique_ptr<neoastra_download> native;@try{try{native=std::make_unique<neoastra_download>(view);native->source_uri=utf8(request.URL.absoluteString);native->platform=new cocoa_download{download,download.progress,self};native->command=cocoa_download_command;native->platform_destroy=destroy_cocoa_download;auto* state=static_cast<cocoa_download*>(native->platform);objc_setAssociatedObject(download,@selector(downloadDidFinish:),[NSValue valueWithPointer:native.get()],OBJC_ASSOCIATION_RETAIN_NONATOMIC);objc_setAssociatedObject(download.progress,@selector(downloadDidFinish:),[NSValue valueWithPointer:native.get()],OBJC_ASSOCIATION_RETAIN_NONATOMIC);[download.progress addObserver:self forKeyPath:@"completedUnitCount" options:NSKeyValueObservingOptionNew context:nullptr];state->observes_completed=true;[download.progress addObserver:self forKeyPath:@"totalUnitCount" options:NSKeyValueObservingOptionNew context:nullptr];state->observes_total=true;download.delegate=self;native.release();}catch(...){native.reset();[download cancel:nil];}}@catch(NSException*){native.reset();[download cancel:nil];} }
- (void)download:(WKDownload*)download decideDestinationUsingResponse:(NSURLResponse*)response suggestedFilename:(NSString*)suggestedFilename completionHandler:(void (^)(NSURL*))completionHandler API_AVAILABLE(macos(11.3)) {
    auto* native=(neoastra_download_t*)[objc_getAssociatedObject(download,@selector(downloadDidFinish:)) pointerValue];
    neo_retain_guard owner(native);
    auto* view=owner?native->view:nullptr;
    neo_retain_guard view_owner(view);
    if(!owner||!view_owner){completionHandler(nil);return;}
    std::string filename=utf8(suggestedFilename),mime=utf8(response.MIMEType);
    native->total_bytes=response.expectedContentLength<0?UINT64_MAX:(uint64_t)response.expectedContentLength;
    NSURL* downloads=[[[NSFileManager defaultManager] URLsForDirectory:NSDownloadsDirectory inDomains:NSUserDomainMask] firstObject];
    NSURL* default_destination=[downloads URLByAppendingPathComponent:suggestedFilename];
    for(NSUInteger index=1;[[NSFileManager defaultManager]fileExistsAtPath:default_destination.path];++index){
        NSString* stem=[suggestedFilename stringByDeletingPathExtension],*extension=[suggestedFilename pathExtension];
        NSString* candidate=[NSString stringWithFormat:@"%@ (%lu)%@%@",stem,(unsigned long)index,extension.length?@".":@"",extension];
        default_destination=[downloads URLByAppendingPathComponent:candidate];
    }
    auto* decision=new neoastra_decision;
    neo_configure_decision(decision,view,NEOASTRA_DECISION_DOWNLOAD_REQUEST,NEOASTRA_DECISION_CANCEL);
    decision->completion=download_destination_decided;
    decision->completion_context=new download_destination_context{native,default_destination,completionHandler};
    neo_event_details details{};details.text2=&mime;details.value2=1;details.download=native;
    native->event_published=true;
    neo_emit_view_detailed(view,NEOASTRA_EVENT_DOWNLOAD_REQUESTED,native->id,&filename,&native->source_uri,native->total_bytes,0,decision,details);
    // Detach may clear the download's weak view, but this operation owns the original.
    neo_finish_decision_event(view,decision);
    decision->release();
}
- (void)downloadDidFinish:(WKDownload*)download API_AVAILABLE(macos(11.3)) { auto* native=(neoastra_download_t*)[objc_getAssociatedObject(download,@selector(downloadDidFinish:)) pointerValue];neo_retain_guard owner(native);neo_retain_guard view_owner(owner?native->view:nullptr);if(!owner||!view_owner)return;auto expected=NEOASTRA_DOWNLOAD_IN_PROGRESS;if(native->state.compare_exchange_strong(expected,NEOASTRA_DOWNLOAD_COMPLETED)){native->bytes_received.store(native->total_bytes.load());neo_download_emit(native,NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED);neo_download_emit(native,NEOASTRA_EVENT_DOWNLOAD_COMPLETED);native->release_lifecycle();} }
- (void)download:(WKDownload*)download didFailWithError:(NSError*)error resumeData:(NSData*)resumeData API_AVAILABLE(macos(11.3)) { (void)resumeData;auto* native=(neoastra_download_t*)[objc_getAssociatedObject(download,@selector(downloadDidFinish:)) pointerValue];neo_retain_guard owner(native);neo_retain_guard view_owner(owner?native->view:nullptr);if(!owner||!view_owner)return;const auto terminal=error.code==NSURLErrorCancelled?NEOASTRA_DOWNLOAD_CANCELED:NEOASTRA_DOWNLOAD_FAILED;auto expected=NEOASTRA_DOWNLOAD_IN_PROGRESS;if(!native->state.compare_exchange_strong(expected,terminal)){expected=NEOASTRA_DOWNLOAD_REQUESTED;if(!native->state.compare_exchange_strong(expected,terminal))return;}try{native->failure_reason=utf8(error.localizedDescription);}catch(...){ }neo_download_emit(native,NEOASTRA_EVENT_DOWNLOAD_COMPLETED);native->release_lifecycle(); }
- (void)observeValueForKeyPath:(NSString*)keyPath ofObject:(id)object change:(NSDictionary<NSKeyValueChangeKey,id>*)change context:(void*)context { (void)change;(void)context;if([object isKindOfClass:[NSProgress class]]){auto* native=(neoastra_download_t*)[objc_getAssociatedObject(object,@selector(downloadDidFinish:)) pointerValue];neo_retain_guard owner(native);neo_retain_guard view_owner(owner?native->view:nullptr);if(owner&&view_owner&&native->state.load()==NEOASTRA_DOWNLOAD_IN_PROGRESS){NSProgress* progress=(NSProgress*)object;native->bytes_received.store(progress.completedUnitCount<0?0:(uint64_t)progress.completedUnitCount);native->total_bytes.store(progress.totalUnitCount<0?UINT64_MAX:(uint64_t)progress.totalUnitCount);neo_download_emit(native,NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED);}return;}auto* view=self.nativeView;neo_retain_guard owner(view);if(!owner)return;WKWebView* webview=(WKWebView*)object;if([keyPath isEqualToString:@"title"]){view->title=utf8(webview.title);neo_emit_view(view,NEOASTRA_EVENT_TITLE_CHANGED,0,&view->title);}else if([keyPath isEqualToString:@"URL"]){view->source=utf8(webview.URL.absoluteString);neo_emit_view(view,NEOASTRA_EVENT_SOURCE_CHANGED,0,nullptr,&view->source);}else if([keyPath isEqualToString:@"canGoBack"]||[keyPath isEqualToString:@"canGoForward"])report_history(view,webview); }
@end

namespace {
NSView* view_parent(neoastra_view_t* view) {if(view->window){auto* state=static_cast<cocoa_window*>(view->window->platform);return state?state->window.contentView:nil;}return view->parent.kind==NEOASTRA_NATIVE_PARENT_COCOA_NSVIEW?(__bridge NSView*)view->parent.handle:nil;}
NSRect view_frame(neoastra_view_t* view,NSView* parent){if(view->fill_parent)return parent.bounds;return NSMakeRect(view->bounds.x,view->bounds.y,std::max(view->bounds.width,1),std::max(view->bounds.height,1));}
// Extends the content view beneath a transparent title bar. The traffic lights stay in place for the overlay style.
void apply_title_bar(neoastra_window_t* window,cocoa_window* state) {
    NSWindow* native=state->window;
    const BOOL extended=window->title_bar.style!=NEOASTRA_TITLE_BAR_DEFAULT;
    // Only the full-size-content bit changes; AppKit rejects clearing bits it owns, such as fullscreen.
    NSWindowStyleMask mask=native.styleMask;
    if(extended)mask|=NSWindowStyleMaskFullSizeContentView;else mask&=~NSWindowStyleMaskFullSizeContentView;
    if(mask!=native.styleMask)native.styleMask=mask;
    if(window->platform!=state)return;
    native.titlebarAppearsTransparent=extended;
    native.titleVisibility=extended?NSWindowTitleHidden:NSWindowTitleVisible;
    const NSWindowButton buttons[]={NSWindowCloseButton,NSWindowMiniaturizeButton,NSWindowZoomButton};
    for(const auto button:buttons)[native standardWindowButton:button].hidden=window->title_bar.style==NEOASTRA_TITLE_BAR_HIDDEN;
}
// WebKit selects a persistent store by identifier and keeps it in its own container, not in a directory the application chooses.
// The identifier is the SHA-256 name-based UUID (RFC 9562, version 8) of the root. Deriving it any other way would detach
// every root from the data already stored for it.
NSUUID* data_store_identifier(const std::string& user_data_root) {
    // One directory is one store however it is spelled: symbolic links and letter case are resolved for the part that exists.
    std::error_code failure;
    auto path=std::filesystem::weakly_canonical(std::filesystem::path(user_data_root),failure);
    if(failure)path=std::filesystem::path(user_data_root).lexically_normal();
    if(path.has_relative_path()&&!path.has_filename())path=path.parent_path();
    const auto name=path.string();
    static constexpr char scope[]="neoastra:user-data-root:";
    unsigned char digest[CC_SHA256_DIGEST_LENGTH];
    CC_SHA256_CTX hash;
    CC_SHA256_Init(&hash);
    CC_SHA256_Update(&hash,scope,sizeof(scope)-1);
    CC_SHA256_Update(&hash,name.data(),static_cast<CC_LONG>(name.size()));
    CC_SHA256_Final(digest,&hash);
    digest[6]=(digest[6]&0x0f)|0x80;
    digest[8]=(digest[8]&0x3f)|0x80;
    return [[NSUUID alloc]initWithUUIDBytes:digest];
}
// Without a root the environment keeps the default store, which is also where data stored before roots were honored remains.
WKWebsiteDataStore* persistent_data_store(neoastra_app_t* app,const std::string& user_data_root) {
    if(user_data_root.empty())return [WKWebsiteDataStore defaultDataStore];
    if(@available(macOS 14.0,*))return [WKWebsiteDataStore dataStoreForIdentifier:data_store_identifier(user_data_root)];
    neo_log(app,NEOASTRA_LOG_WARNING,"environment","WKWebView cannot give a user-data root its own store before macOS 14; the default store is shared");
    return [WKWebsiteDataStore defaultDataStore];
}
}

bool neo_platform_initialize(neoastra_app_t* app,neoastra_error_t**) noexcept {@autoreleasepool{[NSApplication sharedApplication];[NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];auto* state=new cocoa_app{};state->previous_delegate=NSApp.delegate;NeoApplicationDelegate* delegate=[NeoApplicationDelegate new];delegate.nativeApplication=app;delegate.previousDelegate=state->previous_delegate;state->delegate=delegate;NSApp.delegate=delegate;app->platform=state;
    // Posted on the main thread when a display comes, goes, or changes its frame, its visible frame, or its scale; without a queue the block runs there at once.
    state->screen_observer=[NSNotificationCenter.defaultCenter addObserverForName:NSApplicationDidChangeScreenParametersNotification object:nil queue:nil usingBlock:^(NSNotification*){neo_app_displays_changed(app);}];
    return true;}}
void neo_platform_shutdown(neoastra_app_t* app) noexcept {auto* state=static_cast<cocoa_app*>(app->platform);if(state){if(state->termination_decision){auto* decision=state->termination_decision;state->termination_decision=nullptr;decision->completion=nullptr;decision->completion_context=nullptr;decision->abandon();decision->release();}if(state->screen_observer){[NSNotificationCenter.defaultCenter removeObserver:state->screen_observer];state->screen_observer=nil;}((NeoApplicationDelegate*)state->delegate).nativeApplication=nullptr;if(NSApp.delegate==state->delegate)NSApp.delegate=state->previous_delegate;delete state;}app->platform=nullptr;}
bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept {dispatch_async(dispatch_get_main_queue(),^{neo_destroy_app_on_ui(app);});return true;}
int32_t neo_platform_run(neoastra_app_t* app) noexcept {@autoreleasepool{[NSApp run];return app->exit_code.load();}}
void neo_platform_quit(neoastra_app_t*) noexcept {dispatch_async(dispatch_get_main_queue(),^{[NSApp stop:nil];NSEvent* event=[NSEvent otherEventWithType:NSEventTypeApplicationDefined location:NSZeroPoint modifierFlags:0 timestamp:0 windowNumber:0 context:nil subtype:0 data1:0 data2:0];[NSApp postEvent:event atStart:NO];});}
// Called under the platform mutex. A drain runs for one slice; the rest of a busy queue waits for a new block, after the events of the run loop.
void neo_platform_wake(neoastra_app_t* app) noexcept {auto* state=static_cast<cocoa_app*>(app->platform);if(!state||state->wake_pending)return;state->wake_pending=true;app->retain();dispatch_async(dispatch_get_main_queue(),^{{std::lock_guard lock(app->platform_mutex);if(auto* current=static_cast<cocoa_app*>(app->platform))current->wake_pending=false;}if(neo_drain_dispatch_slice(app))neo_wake_app(app);app->release();});}
bool neo_platform_schedule_decision_timeout(neoastra_app_t*,neoastra_decision_t* decision) noexcept {const auto remaining=std::chrono::duration_cast<std::chrono::nanoseconds>(decision->deadline-std::chrono::steady_clock::now()).count();decision->retain();dispatch_after(dispatch_time(DISPATCH_TIME_NOW,std::max<int64_t>(remaining,1)),dispatch_get_main_queue(),^{decision->expire();decision->release();});return true;}

// The color the window shows before and behind its content.
static void apply_background_color(neoastra_window_t* window,cocoa_window* state) noexcept {
    const auto color=window->background_color;
    state->window.backgroundColor=[NSColor colorWithSRGBRed:color.red/255.0 green:color.green/255.0 blue:color.blue/255.0 alpha:color.alpha/255.0];
}
neoastra_result_t neo_platform_window_set_background_color(neoastra_window_t* window) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;apply_background_color(window,state);return NEOASTRA_OK;}}
bool neo_platform_window_create(neoastra_window_t* window,const neoastra_window_options_t* options,neoastra_error_t** error) noexcept {@autoreleasepool{auto* state=new cocoa_window{};window->platform=state;NSWindowStyleMask style=NSWindowStyleMaskClosable|NSWindowStyleMaskMiniaturizable;if(options->flags&2u)style|=NSWindowStyleMaskTitled;if(options->flags&1u)style|=NSWindowStyleMaskResizable;const auto requested=window->bounds;state->window=[[NSWindow alloc]initWithContentRect:NSMakeRect(0,0,requested.width,requested.height) styleMask:style backing:NSBackingStoreBuffered defer:NO];state->delegate=[NeoWindowDelegate new];state->delegate.nativeWindow=window;state->window.title=ns_string(window->title);state->window.releasedWhenClosed=NO;apply_background_color(window,state);if(window->title_bar.style!=NEOASTRA_TITLE_BAR_DEFAULT)apply_title_bar(window,state);
    // The frame depends on the title-bar style, so the requested bounds are applied once that style is in place.
    [state->window setFrame:frame_for_bounds(state->window,requested) display:NO];
    if(options->minimum_size.width>0||options->minimum_size.height>0)state->window.contentMinSize=NSMakeSize(std::max(options->minimum_size.width,0),std::max(options->minimum_size.height,0));if(options->maximum_size.width>0||options->maximum_size.height>0)state->window.contentMaxSize=NSMakeSize(options->maximum_size.width>0?options->maximum_size.width:CGFLOAT_MAX,options->maximum_size.height>0?options->maximum_size.height:CGFLOAT_MAX);if(options->flags&8u)state->window.level=NSFloatingWindowLevel;if(options->flags&64u){if(window->owner){auto* owner=static_cast<cocoa_window*>(window->owner->platform);if(owner&&owner->window){const auto owner_frame=owner->window.frame,child_frame=state->window.frame;[state->window setFrameOrigin:NSMakePoint(NSMidX(owner_frame)-child_frame.size.width/2,NSMidY(owner_frame)-child_frame.size.height/2)];}else [state->window center];}else [state->window center];}else if(options->flags&32u)[state->window cascadeTopLeftFromPoint:NSMakePoint(20,20)];
    // A centered, cascaded, or system-constrained placement is read back so the position is known.
    sync_bounds(window,state->window);
    // A window counts in points, and its display draws each of them with this many pixels.
    neo_window_set_scale_factor(window,state->window.backingScaleFactor,false);
    // Initial geometry is configured before delivery starts. Showing the window
    // can then reenter the host, including a complete application detach.
    state->modal=(options->flags&128u)!=0;
    NSWindow* native=state->window;
    native.delegate=state->delegate;
    if(options->flags&4u){
        if(state->modal&&window->owner){auto* owner=static_cast<cocoa_window*>(window->owner->platform);if(!owner||!owner->window||owner->window.attachedSheet)return false;state->modal_active=true;[owner->window beginSheet:native completionHandler:nil];}
        else [native makeKeyAndOrderFront:nil];
        if(window->platform!=state){neo_fail(error,NEOASTRA_ERROR_DISPOSED,"Application detached while showing its window");return false;}
        if(options->state==NEOASTRA_WINDOW_MINIMIZED)[native miniaturize:nil];else if(options->state==NEOASTRA_WINDOW_MAXIMIZED)[native zoom:nil];else if(options->state==NEOASTRA_WINDOW_FULLSCREEN)[native toggleFullScreen:nil];
    }
    [NSApp activateIgnoringOtherApps:YES];
    if(window->platform!=state){neo_fail(error,NEOASTRA_ERROR_DISPOSED,"Application detached while creating its window");return false;}
    return true;}}
void neo_platform_window_destroy(neoastra_window_t* window) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_window*>(window->platform);if(!state)return;window->platform=nullptr;NSWindow* native=state->window;state->delegate.nativeWindow=nullptr;native.delegate=nil;if(state->modal_active&&window->owner){auto* owner=static_cast<cocoa_window*>(window->owner->platform);if(owner&&owner->window)[owner->window endSheet:native];}if(native)[native close];delete state;}}
neoastra_result_t neo_platform_window_show(neoastra_window_t* window,bool visible) noexcept {
    auto* state=static_cast<cocoa_window*>(window->platform);
    if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;
    NSWindow* native=state->window;
    if(!visible){
        if(state->modal_active&&window->owner){auto* owner=static_cast<cocoa_window*>(window->owner->platform);state->modal_active=false;if(owner&&owner->window)[owner->window endSheet:native];}
        if(window->platform!=state)return NEOASTRA_ERROR_DISPOSED;
        [native orderOut:nil];return NEOASTRA_OK;
    }
    if(state->modal&&window->owner&&!state->modal_active){
        auto* owner=static_cast<cocoa_window*>(window->owner->platform);
        if(!owner||!owner->window||owner->window.attachedSheet)return NEOASTRA_ERROR_INVALID_STATE;
        state->modal_active=true;[owner->window beginSheet:native completionHandler:nil];
    }else if(!state->modal)[native orderFront:nil];
    if(window->platform!=state)return NEOASTRA_ERROR_DISPOSED;
    return neo_platform_window_set_state(window);
}
neoastra_result_t neo_platform_window_activate(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;[state->window makeKeyAndOrderFront:nil];[NSApp activateIgnoringOtherApps:YES];return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_force_close(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;[state->window close];return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_title(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;state->window.title=ns_string(window->title);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;[state->window setFrame:frame_for_bounds(state->window,window->bounds) display:YES];
    // AppKit may constrain the frame, and a request that leaves the frame unchanged posts no notification.
    if(window->platform!=state)return NEOASTRA_ERROR_DISPOSED;
    sync_bounds(window,state->window);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t* window) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;state->window.contentMinSize=NSMakeSize(std::max(window->minimum_size.width,0),std::max(window->minimum_size.height,0));if(window->platform!=state)return NEOASTRA_ERROR_DISPOSED;state->window.contentMaxSize=NSMakeSize(window->maximum_size.width>0?window->maximum_size.width:CGFLOAT_MAX,window->maximum_size.height>0?window->maximum_size.height:CGFLOAT_MAX);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_state(neoastra_window_t* window) noexcept {
    auto* state=static_cast<cocoa_window*>(window->platform);
    if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;
    NSWindow* native=state->window;
    if(!native.visible&&(native.styleMask&NSWindowStyleMaskFullScreen)==0)return NEOASTRA_OK;
    switch(window->state){
        case NEOASTRA_WINDOW_NORMAL:
            if(native.miniaturized)[native deminiaturize:nil];
            if(window->platform!=state)return NEOASTRA_ERROR_DISPOSED;
            if(native.zoomed)[native zoom:nil];
            if(window->platform!=state)return NEOASTRA_ERROR_DISPOSED;
            if((native.styleMask&NSWindowStyleMaskFullScreen)!=0)[native toggleFullScreen:nil];break;
        case NEOASTRA_WINDOW_MINIMIZED:[native miniaturize:nil];break;
        case NEOASTRA_WINDOW_MAXIMIZED:if(!native.zoomed)[native zoom:nil];break;
        case NEOASTRA_WINDOW_FULLSCREEN:if((native.styleMask&NSWindowStyleMaskFullScreen)==0)[native toggleFullScreen:nil];break;
        default:return NEOASTRA_ERROR_INVALID_ARGUMENT;
    }
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_window_set_attribute(neoastra_window_t* window,neoastra_window_attribute_t attribute,bool enabled) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;switch(attribute){case NEOASTRA_WINDOW_RESIZABLE:{auto style=state->window.styleMask;if(enabled)style|=NSWindowStyleMaskResizable;else style&=~NSWindowStyleMaskResizable;state->window.styleMask=style;break;}case NEOASTRA_WINDOW_DECORATED:{auto style=state->window.styleMask;if(enabled)style|=NSWindowStyleMaskTitled;else style&=~NSWindowStyleMaskTitled;state->window.styleMask=style;if(window->platform!=state)return NEOASTRA_ERROR_DISPOSED;if(enabled&&window->title_bar.style!=NEOASTRA_TITLE_BAR_DEFAULT)apply_title_bar(window,state);if(window->platform!=state)return NEOASTRA_ERROR_DISPOSED;sync_bounds(window,state->window);break;}case NEOASTRA_WINDOW_ALWAYS_ON_TOP:state->window.level=enabled?NSFloatingWindowLevel:NSNormalWindowLevel;break;case NEOASTRA_WINDOW_SHOW_IN_TASKBAR:return NEOASTRA_ERROR_NOT_SUPPORTED;case NEOASTRA_WINDOW_ALL_VIRTUAL_DESKTOPS:{
        // A window that joins every Space cannot also move to the active one, which AppKit refuses with an exception.
        auto behavior=state->window.collectionBehavior;if(enabled){behavior|=NSWindowCollectionBehaviorCanJoinAllSpaces;behavior&=~NSWindowCollectionBehaviorMoveToActiveSpace;}else behavior&=~NSWindowCollectionBehaviorCanJoinAllSpaces;
        @try{state->window.collectionBehavior=behavior;}@catch(NSException*){return NEOASTRA_ERROR_NATIVE_FAILURE;}break;}default:return NEOASTRA_ERROR_INVALID_ARGUMENT;}return NEOASTRA_OK;}}
neoastra_result_t neo_platform_window_begin_drag(neoastra_window_t* window) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;NSEvent* event=NSApp.currentEvent;
    // A renderer request arrives after the press; the drag is only valid while that press is still held.
    if(!event||(event.type!=NSEventTypeLeftMouseDown&&event.type!=NSEventTypeLeftMouseDragged)||(NSEvent.pressedMouseButtons&1u)==0)return NEOASTRA_ERROR_INVALID_STATE;
    [state->window performWindowDragWithEvent:event];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_window_begin_resize(neoastra_window_t*,neoastra_window_resize_edge_t) noexcept {return NEOASTRA_ERROR_NOT_SUPPORTED;}
neoastra_result_t neo_platform_window_set_title_bar(neoastra_window_t* window) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;apply_title_bar(window,state);
    // Extending the content keeps the frame and resizes the content area without a resize notification.
    if(window->platform!=state)return NEOASTRA_ERROR_DISPOSED;
    sync_bounds(window,state->window);return NEOASTRA_OK;}}
// The frame of an AppKit window is what it shows: its content with the title bar above it, measured as the bounds are.
neoastra_result_t neo_platform_window_get_frame(neoastra_window_t* window,neoastra_rect_t* value) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;const NSRect frame=state->window.frame;*value={(int32_t)lround(NSMinX(frame)),(int32_t)lround(primary_display_height()-NSMaxY(frame)),(int32_t)lround(NSWidth(frame)),(int32_t)lround(NSHeight(frame))};return NEOASTRA_OK;}}
neoastra_result_t neo_platform_window_get_title_bar(neoastra_window_t* window,neoastra_title_bar_t* value) noexcept {@autoreleasepool{
    auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;
    if(window->title_bar.style==NEOASTRA_TITLE_BAR_DEFAULT){value->height=0;return NEOASTRA_OK;}
    // AppKit owns the title-bar height, so the requested height is not applied on this backend.
    const NSRect content=NSMakeRect(0,0,100,100);
    value->height=(int32_t)ceil(NSHeight([NSWindow frameRectForContentRect:content styleMask:NSWindowStyleMaskTitled])-NSHeight(content));
    const NSWindowStyleMask mask=state->window.styleMask;
    // Fullscreen and untitled windows show no traffic lights for the content to avoid.
    if(window->title_bar.style!=NEOASTRA_TITLE_BAR_OVERLAY||(mask&NSWindowStyleMaskTitled)==0||(mask&NSWindowStyleMaskFullScreen)!=0)return NEOASTRA_OK;
    CGFloat left=CGFLOAT_MAX,right=0;
    const NSWindowButton buttons[]={NSWindowCloseButton,NSWindowMiniaturizeButton,NSWindowZoomButton};
    for(const auto kind:buttons){NSButton* button=[state->window standardWindowButton:kind];if(!button||button.hidden)continue;const NSRect frame=[button convertRect:button.bounds toView:nil];left=std::min(left,NSMinX(frame));right=std::max(right,NSMaxX(frame));}
    if(right<=0)return NEOASTRA_OK;
    // The controls sit on the leading edge, which is the right side in right-to-left layouts. Mirror the system margin on their inner side.
    const CGFloat width=NSWidth(state->window.frame);
    if(left<=width-right)value->left_inset=(int32_t)ceil(right+left);else value->right_inset=(int32_t)ceil((width-left)+(width-right));
    return NEOASTRA_OK;}}
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
                state->data_store=options->private_mode?[WKWebsiteDataStore nonPersistentDataStore]:persistent_data_store(environment->app,neo_string(options->user_data_root));
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
// A profile that is not ephemeral is the store of its environment, so it follows the environment's user-data root and private mode.
bool neo_platform_profile_create(neoastra_profile_t* profile,neoastra_error_t** error) noexcept {@autoreleasepool{try{auto* environment=static_cast<cocoa_environment*>(profile->environment->platform);if(!environment||!environment->data_store){neo_fail(error,NEOASTRA_ERROR_INVALID_STATE,"WKWebView environment is unavailable");return false;}auto* state=new cocoa_profile{};state->data_store=profile->ephemeral?[WKWebsiteDataStore nonPersistentDataStore]:environment->data_store;profile->platform=state;return true;}catch(...){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Could not allocate WKWebView profile state");return false;}}}
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
                state->webview=[[NeoAstraWebView alloc]initWithFrame:view_frame(view,parent) configuration:configuration];
                if (!state->webview) {
                    neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WKWebView could not create the browser view",0,"wkwebview");
                    return false;
                }
                state->webview.navigationDelegate=state->delegate;
                ((NeoAstraWebView*)state->webview).nativeView=view;
                state->webview.UIDelegate=state->delegate;
                state->webview.allowsMagnification=YES;
                if (view->fill_parent) state->webview.autoresizingMask=NSViewWidthSizable|NSViewHeightSizable;
                [state->webview addObserver:state->delegate forKeyPath:@"title" options:NSKeyValueObservingOptionNew context:nullptr];
                [state->webview addObserver:state->delegate forKeyPath:@"URL" options:NSKeyValueObservingOptionNew context:nullptr];
                [state->webview addObserver:state->delegate forKeyPath:@"canGoBack" options:NSKeyValueObservingOptionNew context:nullptr];
                [state->webview addObserver:state->delegate forKeyPath:@"canGoForward" options:NSKeyValueObservingOptionNew context:nullptr];
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
void neo_platform_view_destroy(neoastra_view_t* view) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state)return;@try{[state->webview removeObserver:state->delegate forKeyPath:@"title"];[state->webview removeObserver:state->delegate forKeyPath:@"URL"];[state->webview removeObserver:state->delegate forKeyPath:@"canGoBack"];[state->webview removeObserver:state->delegate forKeyPath:@"canGoForward"];}@catch(NSException*){}((NeoAstraWebView*)state->webview).nativeView=nullptr;state->delegate.nativeView=nullptr;state->webview.navigationDelegate=nil;state->webview.UIDelegate=nil;if(view->bridge_policy!=NEOASTRA_BRIDGE_DISABLED)[state->webview.configuration.userContentController removeScriptMessageHandlerForName:@"_neoastra_transport_v1"];[state->webview removeFromSuperview];delete state;view->platform=nullptr;}}
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t* view) noexcept {auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;NSView* parent=view_parent(view);state->webview.frame=view_frame(view,parent);state->webview.autoresizingMask=view->fill_parent?(NSViewWidthSizable|NSViewHeightSizable):NSViewNotSizable;return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_navigate(neoastra_view_t* view,const std::string& uri,neoastra_error_t** error) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);NSURL* url=[NSURL URLWithString:ns_string(uri)];if(!state||!state->webview||!url)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"Invalid WKWebView navigation URI");[state->webview loadRequest:[NSURLRequest requestWithURL:url]];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t* view,const std::string& uri,const std::string& method,const std::string& headers,const uint8_t* body,uint64_t body_length,neoastra_error_t** error) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);NSURL* url=[NSURL URLWithString:ns_string(uri)];if(!state||!state->webview||!url||method.empty()||body_length>NSUIntegerMax)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"Invalid WKWebView navigation request");NSMutableURLRequest* request=[NSMutableURLRequest requestWithURL:url];request.HTTPMethod=ns_string(method);if(body_length)request.HTTPBody=[NSData dataWithBytes:body length:(NSUInteger)body_length];for(NSString* line in [ns_string(headers) componentsSeparatedByCharactersInSet:[NSCharacterSet newlineCharacterSet]]){NSRange separator=[line rangeOfString:@":"];if(separator.location==NSNotFound)continue;NSString* name=[[line substringToIndex:separator.location] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];NSString* value=[[line substringFromIndex:separator.location+1] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];if(name.length)[request setValue:value forHTTPHeaderField:name];}[state->webview loadRequest:request];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_load_html(neoastra_view_t* view,const std::string& html,const std::string& base,neoastra_error_t**) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;NSURL* url=base.empty()?nil:[NSURL URLWithString:ns_string(base)];[state->webview loadHTMLString:ns_string(html) baseURL:url];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_command(neoastra_view_t* view,uint32_t command) noexcept {auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;switch(command){case 0:[state->webview stopLoading];break;case 1:[state->webview reload];break;case 2:[state->webview reloadFromOrigin];break;case 3:[state->webview goBack];break;case 4:[state->webview goForward];break;default:return NEOASTRA_ERROR_INVALID_ARGUMENT;}return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t* view,const std::string& script,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t**) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;[state->webview evaluateJavaScript:ns_string(script) completionHandler:^(id value,NSError* error){neoastra_result_t requested=error?NEOASTRA_ERROR_NATIVE_FAILURE:NEOASTRA_OK,actual{};neoastra_error_t* native_error=error?make_error(requested,utf8(error.localizedDescription).c_str(),error.code):nullptr;std::string output="null";
    // NSJSONSerialization raises an exception for a value without a JSON form, such as a date or a number that is not finite.
    if(value){@try{NSData* data=[NSJSONSerialization dataWithJSONObject:value options:NSJSONWritingFragmentsAllowed error:nil];if(data)output.assign((const char*)data.bytes,data.length);}@catch(NSException*){output="null";}}
    if(operation->try_complete(requested,actual))callback(context,actual,actual==NEOASTRA_OK?neo_string_view(output):neoastra_string_view_t{},native_error);if(native_error)native_error->release();operation->release();}];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_add_script(neoastra_view_t* view,const std::string& script,const neoastra_script_options_t* options,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t**) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;if(options->isolated_world)return NEOASTRA_ERROR_NOT_SUPPORTED;auto time=options->injection_time==NEOASTRA_SCRIPT_DOCUMENT_END?WKUserScriptInjectionTimeAtDocumentEnd:WKUserScriptInjectionTimeAtDocumentStart;WKUserScript* user_script=[[WKUserScript alloc]initWithSource:ns_string(script) injectionTime:time forMainFrameOnly:options->main_frame_only!=0];std::string identifier=utf8([NSUUID UUID].UUIDString);state->scripts.emplace(identifier,user_script);[state->webview.configuration.userContentController addUserScript:user_script];dispatch_async(dispatch_get_main_queue(),^{neoastra_result_t actual{};if(operation->try_complete(NEOASTRA_OK,actual))callback(context,actual,actual==NEOASTRA_OK?neo_string_view(identifier):neoastra_string_view_t{},nullptr);operation->release();});return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t* view,const std::string& identifier) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;auto found=state->scripts.find(identifier);if(found==state->scripts.end())return NEOASTRA_ERROR_INVALID_ARGUMENT;state->scripts.erase(found);auto* manager=state->webview.configuration.userContentController;[manager removeAllUserScripts];for(const auto& entry:state->scripts)[manager addUserScript:entry.second];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_post_message(neoastra_view_t* view,const std::string& message,bool json,neoastra_error_t**) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;std::string script;try{neo_append_post_script(script,message,json);}catch(...){return NEOASTRA_ERROR_NATIVE_FAILURE;}[state->webview evaluateJavaScript:ns_string(script) completionHandler:nil];return NEOASTRA_OK;}}
neoastra_result_t neo_platform_view_post_messages(neoastra_view_t* view,const neoastra_string_view_t* messages,uint32_t count,bool json,uint32_t* posted,neoastra_error_t**) noexcept {@autoreleasepool{
    auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;
    try{
        std::string script;size_t size=0;for(uint32_t index=0;index<count;index++)size+=static_cast<size_t>(messages[index].length)+64;script.reserve(size);
        for(uint32_t index=0;index<count;index++)neo_append_post_script(script,neo_view_text(messages[index]),json);
        [state->webview evaluateJavaScript:ns_string(script) completionHandler:nil];
    }catch(...){return NEOASTRA_ERROR_NATIVE_FAILURE;}
    if(posted)*posted=count;return NEOASTRA_OK;}}
// The zoom of a view is the page zoom of WKWebView, which lays the page out again in a viewport of the size of the view divided by
// the zoom, as the zoom of a browser does, and which a new document, a reload, and a new web content process keep. The magnification
// is the pinch of the user: it scales the picture of a page that keeps its layout, and it stays at 1 unless the user pinches.
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t* view,double* factor) noexcept {auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;*factor=state->webview.pageZoom;return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t* view,double factor) noexcept {auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;state->webview.pageZoom=factor;return NEOASTRA_OK;}
neoastra_result_t neo_platform_view_set_setting(neoastra_view_t* view,neoastra_view_setting_t setting,bool enabled) noexcept {@autoreleasepool{
    auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;
    switch(setting){
        // WKWebView has no browser shortcuts, status bar, or script dialogs of its own, so there is nothing to turn off or on.
        case NEOASTRA_VIEW_SETTING_BROWSER_ACCELERATOR_KEYS:case NEOASTRA_VIEW_SETTING_STATUS_BAR:case NEOASTRA_VIEW_SETTING_DEFAULT_SCRIPT_DIALOGS:return enabled?NEOASTRA_ERROR_NOT_SUPPORTED:NEOASTRA_OK;
        case NEOASTRA_VIEW_SETTING_DEFAULT_CONTEXT_MENUS:if(![state->webview isKindOfClass:[NeoAstraWebView class]])return NEOASTRA_ERROR_NOT_SUPPORTED;((NeoAstraWebView*)state->webview).contextMenusDisabled=!enabled;return NEOASTRA_OK;
        case NEOASTRA_VIEW_SETTING_DEVTOOLS: {
            // Retain the existing best-effort local Inspect Element item: WebKit has no public setting for it.
            // A missing private preference must not prevent the public Safari opt-in or its revocation.
            bool local_supported=false;
            @try{[state->webview.configuration.preferences setValue:@(enabled) forKey:@"developerExtrasEnabled"];local_supported=true;}@catch(NSException*){}
            if(@available(macOS 13.3,*)){
                @try{state->webview.inspectable=enabled;}@catch(NSException*){return NEOASTRA_ERROR_NOT_SUPPORTED;}
                if(enabled)return NEOASTRA_OK;
            }
            // Do not claim complete revocation if the local inspection switch could not be turned off.
            return local_supported?NEOASTRA_OK:NEOASTRA_ERROR_NOT_SUPPORTED;
        }
        case NEOASTRA_VIEW_SETTING_ZOOM_CONTROLS:state->webview.allowsMagnification=enabled;return NEOASTRA_OK;
        // WebKit reads the preference at each Tab key press, and the Option key reverses it for that press.
        case NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS:state->webview.configuration.preferences.tabFocusesLinks=enabled;return NEOASTRA_OK;
        // The requests that walk the history are refused as they come. The swipe of WKWebView is off unless a host turns it on
        // through the native view; it is turned off here so that it does not draw the page it would go to, and never on.
        case NEOASTRA_VIEW_SETTING_HISTORY_NAVIGATION:if(!enabled)state->webview.allowsBackForwardNavigationGestures=NO;report_history(view,state->webview);return NEOASTRA_OK;
        default:return NEOASTRA_ERROR_INVALID_ARGUMENT;
    }
}}
bool neo_platform_open_external(const std::string& uri,int64_t*) noexcept {@autoreleasepool{@try{NSURL* url=[NSURL URLWithString:ns_string(uri)];return url&&[[NSWorkspace sharedWorkspace]openURL:url];}@catch(NSException*){return false;}}}
// A mouse-moved event handed to the web view moves the pointer of the page itself: the system cursor stays where it is.
neoastra_result_t neo_platform_view_move_pointer(neoastra_view_t* view,double x,double y) noexcept {@autoreleasepool{
    auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;
    WKWebView* web=state->webview;if(!web.window)return NEOASTRA_ERROR_INVALID_STATE;
    const CGFloat zoom=(web.pageZoom>0?web.pageZoom:1)*(web.magnification>0?web.magnification:1);
    const NSPoint local=NSMakePoint(x*zoom,web.isFlipped?y*zoom:web.bounds.size.height-y*zoom);
    NSEvent* event=[NSEvent mouseEventWithType:NSEventTypeMouseMoved location:[web convertPoint:local toView:nil] modifierFlags:0
        timestamp:[NSProcessInfo processInfo].systemUptime windowNumber:web.window.windowNumber context:nil eventNumber:0 clickCount:0 pressure:0];
    if(!event)return NEOASTRA_ERROR_NATIVE_FAILURE;
    [web mouseMoved:event];
    return NEOASTRA_OK;
}}
// WKWebView has no public inspector-opening call or responder action for Option+Command+I; use Safari's Develop menu.
neoastra_result_t neo_platform_view_open_devtools(neoastra_view_t* view) noexcept {auto* state=static_cast<cocoa_view*>(view->platform);return !state||!state->webview?NEOASTRA_ERROR_NOT_INITIALIZED:NEOASTRA_ERROR_NOT_SUPPORTED;}
// WKWebView snapshots what the view shows; it has no call for the part of a document outside the viewport.
neoastra_result_t neo_platform_view_capture(neoastra_view_t* view,const neoastra_capture_options_t& options,neoastra_buffer_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {@autoreleasepool{
    auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WKWebView view is not initialized",0,"wkwebview");
    if(options.full_page)return neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WKWebView cannot capture a whole document",0,"wkwebview");
    WKSnapshotConfiguration* configuration=[[WKSnapshotConfiguration alloc] init];
    if(options.region.width>0&&options.region.height>0){
        // CSS pixels become view units through the page zoom and the magnification of a pinch. The rectangle is in view coordinates, whose origin depends on whether the view is flipped.
        const NSRect bounds=state->webview.bounds;const CGFloat zoom=(state->webview.pageZoom>0?state->webview.pageZoom:1)*(state->webview.magnification>0?state->webview.magnification:1);
        NSRect requested=NSMakeRect(options.region.x*zoom,options.region.y*zoom,options.region.width*zoom,options.region.height*zoom);
        if(!state->webview.isFlipped)requested.origin.y=NSHeight(bounds)-NSMaxY(requested);
        requested=NSIntersectionRect(requested,bounds);
        if(NSIsEmptyRect(requested))return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"The capture region is outside the visible viewport",0,"wkwebview");
        configuration.rect=requested;
    }
    const auto format=options.format;const auto quality=options.quality?options.quality:80u;
    [state->webview takeSnapshotWithConfiguration:configuration completionHandler:^(NSImage* image,NSError* failure){@autoreleasepool{
        NSData* data=nil;
        if(image){
            CGImageRef pixels=[image CGImageForProposedRect:nullptr context:nil hints:nil];
            if(pixels){
                NSBitmapImageRep* bitmap=[[NSBitmapImageRep alloc] initWithCGImage:pixels];
                data=format==NEOASTRA_CAPTURE_FORMAT_JPEG?[bitmap representationUsingType:NSBitmapImageFileTypeJPEG properties:@{NSImageCompressionFactor:@(quality/100.0)}]:[bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
            }
        }
        neoastra_result_t requested=data.length?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE,actual{};neoastra_error_t* native_error=nullptr;neoastra_buffer_t* buffer=nullptr;
        if(requested==NEOASTRA_OK){try{const auto* bytes=static_cast<const uint8_t*>(data.bytes);buffer=new neoastra_buffer(std::vector<uint8_t>(bytes,bytes+data.length));}catch(...){requested=NEOASTRA_ERROR_NATIVE_FAILURE;}}
        if(requested!=NEOASTRA_OK)native_error=make_error(requested,failure?utf8(failure.localizedDescription).c_str():"WKWebView returned no capture",failure?failure.code:0);
        if(operation->try_complete(requested,actual)){callback(context,actual,actual==NEOASTRA_OK?buffer:nullptr,actual==requested?native_error:nullptr);if(actual!=NEOASTRA_OK&&buffer)buffer->release();}else if(buffer)buffer->release();
        if(native_error)native_error->release();operation->release();
    }}];
    return NEOASTRA_OK;
}}
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t* view,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* handle) noexcept {if(kind!=NEOASTRA_NATIVE_HANDLE_WKWEBVIEW&&kind!=NEOASTRA_NATIVE_HANDLE_COCOA_NSVIEW)return NEOASTRA_ERROR_NOT_SUPPORTED;auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;handle->kind=kind;handle->value=(__bridge void*)state->webview;return NEOASTRA_OK;}

namespace {
CGDirectDisplayID screen_display(NSScreen* screen) {NSNumber* number=screen.deviceDescription[@"NSScreenNumber"];return [number isKindOfClass:NSNumber.class]?number.unsignedIntValue:0;}
// AppKit frames start at the bottom-left corner of the primary display, and the bounds of a window at its top-left corner.
neoastra_rect_t top_left_rect(NSRect frame,CGFloat top) {return {(int32_t)lround(NSMinX(frame)),(int32_t)lround(top-NSMaxY(frame)),(int32_t)lround(NSWidth(frame)),(int32_t)lround(NSHeight(frame))};}
}

// The first screen is the one with the menu bar, whose bottom-left corner is the origin of AppKit, and the primary display.
void neo_platform_get_displays(neoastra_app_t*,std::vector<neo_display>& displays) noexcept {@autoreleasepool{
    try{
        displays.clear();
        NSArray<NSScreen*>* screens=NSScreen.screens;
        if(!screens.count)return;
        const CGFloat top=NSHeight(screens[0].frame);
        for(NSUInteger index=0;index<screens.count;++index){
            NSScreen* screen=screens[index];
            const auto number=screen_display(screen);
            neo_display display;
            display.handle=number;
            display.bounds=top_left_rect(screen.frame,top);
            // The visible frame leaves out the menu bar and the Dock.
            display.work_area=top_left_rect(screen.visibleFrame,top);
            display.flags=NEOASTRA_DISPLAY_WORK_AREA_REPORTED|(index==0?NEOASTRA_DISPLAY_PRIMARY:0u);
            display.scale_factor=screen.backingScaleFactor;
            display.name=utf8(screen.localizedName);
            if(number){
                // The vendor, model, and serial numbers of the monitor name it after a restart; a display without them lasts for the session.
                const auto vendor=CGDisplayVendorNumber(number),model=CGDisplayModelNumber(number),serial=CGDisplaySerialNumber(number);
                char text[48]{};
                std::snprintf(text,sizeof(text),"cg:%08x-%08x-%08x",static_cast<unsigned>(vendor),static_cast<unsigned>(model),static_cast<unsigned>(serial));
                display.id=text;
                if(vendor||model)display.flags|=NEOASTRA_DISPLAY_ID_PERSISTENT;
                else display.id+=":"+std::to_string(number);
                const auto rotation=((static_cast<long>(lround(CGDisplayRotation(number)))%360)+360)%360;
                if(rotation==0||rotation==90||rotation==180||rotation==270){display.rotation_degrees=static_cast<uint32_t>(rotation);display.flags|=NEOASTRA_DISPLAY_ROTATION_REPORTED;}
                if(CGDisplayModeRef mode=CGDisplayCopyDisplayMode(number)){display.refresh_rate=CGDisplayModeGetRefreshRate(mode);CGDisplayModeRelease(mode);}
            }
            // A built-in panel reports no rate in its display mode.
            if(display.refresh_rate<=0&&screen.maximumFramesPerSecond>0)display.refresh_rate=static_cast<double>(screen.maximumFramesPerSecond);
            displays.push_back(std::move(display));
        }
    }catch(...){displays.clear();}
}}

// A window that is not on screen has no screen.
uint64_t neo_platform_window_display_handle(neoastra_window_t* window) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return 0;NSScreen* screen=state->window.screen;return screen?screen_display(screen):0;}}

void neo_platform_window_capability(neoastra_app_t*,neoastra_window_capability_t capability,neoastra_capability_info_t* info) noexcept {
    static const std::string displays="NSScreen frames and visible frames in the top-left coordinates of window bounds, in points; identifiers from the vendor, model, and serial numbers of CoreGraphics";
    static const std::string placement="AppKit keeps the title bar of a titled window below the menu bar, so a requested position can be adjusted";
    static const std::string desktops="NSWindow.isOnActiveSpace tells whether a window is on the active Space; macOS has no public identifier of a Space";
    static const std::string move="macOS has no public API to move a window to another Space";
    static const std::string all_desktops="NSWindowCollectionBehaviorCanJoinAllSpaces shows a window on every Space";
    switch(capability){
        case NEOASTRA_WINDOW_CAPABILITY_DISPLAYS:info->support=NEOASTRA_SUPPORT_NATIVE;info->details=neo_string_view(displays);break;
        case NEOASTRA_WINDOW_CAPABILITY_PLACEMENT:info->support=NEOASTRA_SUPPORT_NATIVE;info->details=neo_string_view(placement);break;
        case NEOASTRA_WINDOW_CAPABILITY_VIRTUAL_DESKTOPS:info->support=NEOASTRA_SUPPORT_LIMITED;info->details=neo_string_view(desktops);break;
        case NEOASTRA_WINDOW_CAPABILITY_VIRTUAL_DESKTOP_MOVE:info->details=neo_string_view(move);break;
        case NEOASTRA_WINDOW_CAPABILITY_ALL_VIRTUAL_DESKTOPS:info->support=NEOASTRA_SUPPORT_NATIVE;info->details=neo_string_view(all_desktops);break;
        default:break;
    }
}

// A Space has no public identifier, so only whether the window is on the active one is told.
neoastra_result_t neo_platform_window_get_virtual_desktop(neoastra_window_t* window,uint32_t* flags,std::string*) noexcept {@autoreleasepool{auto* state=static_cast<cocoa_window*>(window->platform);if(!state||!state->window)return NEOASTRA_ERROR_DISPOSED;*flags|=NEOASTRA_VIRTUAL_DESKTOP_CURRENT_KNOWN|(state->window.isOnActiveSpace?NEOASTRA_VIRTUAL_DESKTOP_ON_CURRENT:0u);return NEOASTRA_OK;}}
neoastra_result_t neo_platform_window_move_to_virtual_desktop(neoastra_window_t* window,const std::string&) noexcept {auto* state=static_cast<cocoa_window*>(window->platform);return !state||!state->window?NEOASTRA_ERROR_DISPOSED:NEOASTRA_ERROR_NOT_SUPPORTED;}

// The windows of the application, front first; a window on another Space is not on screen.
neoastra_result_t neo_platform_window_at(neoastra_app_t* app,neoastra_point_t point,const neoastra_window_t* excluded,uint64_t* found) noexcept {@autoreleasepool{
    for(NSWindow* native in NSApp.orderedWindows){
        if(!native.visible||native.miniaturized||!native.isOnActiveSpace)continue;
        if(![native.delegate isKindOfClass:NeoWindowDelegate.class])continue;
        auto* window=((NeoWindowDelegate*)native.delegate).nativeWindow;
        if(!window||window==excluded||window->app!=app||!window->platform)continue;
        neoastra_rect_t frame{};
        if(neo_platform_window_get_frame(window,&frame)!=NEOASTRA_OK)continue;
        if(point.x>=frame.x&&point.y>=frame.y&&int64_t{point.x}<int64_t{frame.x}+frame.width&&int64_t{point.y}<int64_t{frame.y}+frame.height){*found=window->id;break;}
    }
    return NEOASTRA_OK;
}}

// CSS pixels become view units through the page zoom and the magnification of a pinch, the view and its window place them on
// screen, and the screen point is turned to the top-left coordinates of window bounds.
neoastra_result_t neo_platform_view_convert_point(neoastra_view_t* view,bool to_screen,double x,double y,double* out_x,double* out_y) noexcept {@autoreleasepool{
    auto* state=static_cast<cocoa_view*>(view->platform);if(!state||!state->webview)return NEOASTRA_ERROR_NOT_INITIALIZED;
    WKWebView* web=state->webview;NSWindow* window=web.window;if(!window)return NEOASTRA_ERROR_INVALID_STATE;
    const CGFloat zoom=(web.pageZoom>0?web.pageZoom:1)*(web.magnification>0?web.magnification:1);
    const CGFloat top=primary_display_height();
    if(to_screen){
        const NSPoint local=NSMakePoint(x*zoom,web.isFlipped?y*zoom:NSHeight(web.bounds)-y*zoom);
        const NSPoint screen=[window convertPointToScreen:[web convertPoint:local toView:nil]];
        *out_x=screen.x;*out_y=top-screen.y;
    }else{
        const NSPoint local=[web convertPoint:[window convertPointFromScreen:NSMakePoint(x,top-y)] fromView:nil];
        *out_x=local.x/zoom;*out_y=(web.isFlipped?local.y:NSHeight(web.bounds)-local.y)/zoom;
    }
    return NEOASTRA_OK;
}}
