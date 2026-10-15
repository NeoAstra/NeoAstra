// iOS backend: UIKit and WKWebView.
//
// UIKit owns the application and its run loop, so an iOS application attaches to the loop that is already running
// (neoastra_app_attach) instead of running one. The backend implements the application level (creation, dispatch to the
// main queue, decision timeouts, teardown) and the browser: an environment, a profile, and a view that WKWebView draws
// inside a UIView of the application. Windows, popups, downloads, dialogs, permissions, and cookies answer
// NEOASTRA_ERROR_NOT_SUPPORTED until their implementation follows, and the capability report says so.
#include "../common/native_internal.hpp"

#import <CommonCrypto/CommonDigest.h>

// Module imports make the object file name the frameworks it uses, which the linker of an application honors.
@import Foundation;
@import UIKit;
@import WebKit;

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <memory>
#include <new>
#include <string>
#include <system_error>
#include <unordered_map>
#include <vector>

@interface NeoURLSchemeHandler : NSObject <WKURLSchemeHandler>
@property(nonatomic, assign) neoastra_environment_t* nativeEnvironment;
@property(nonatomic, assign) const neo_custom_scheme_registration* registration;
@end

@interface NeoAstraDelegate : NSObject <WKNavigationDelegate, WKScriptMessageHandler>
@property(nonatomic, assign) neoastra_view_t* nativeView;
@end

namespace {
struct uikit_app {};
struct uikit_environment { WKWebsiteDataStore* data_store; };
struct uikit_profile { WKWebsiteDataStore* data_store; };
struct uikit_view {
    WKWebView* webview;
    NeoAstraDelegate* delegate;
    NSMutableArray<NeoURLSchemeHandler*>* scheme_handlers;
    std::unordered_map<std::string, WKUserScript*> scripts;
    uint64_t history{};
    ~uikit_view() { if (delegate) delegate.nativeView = nullptr; }
};

constexpr const char* uikit_domain = "uikit";
constexpr const char* webkit_domain = "wkwebview";

neoastra_result_t unsupported(neoastra_error_t** error, const char* what) noexcept {
    return neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, std::string(what) + " is not implemented by the iOS backend yet", 0, uikit_domain);
}

NSString* ns_string(const std::string& value) {
    NSString* text = [[NSString alloc] initWithBytes:value.data() length:value.size() encoding:NSUTF8StringEncoding];
    return text ? text : @"";
}
std::string utf8(NSString* value) {
    if (!value) return {};
    const char* text = value.UTF8String;
    return text ? text : "";
}

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

// The origin of a URL as WebKit reports it for a frame, with the default port left out.
std::string url_origin(NSURL* url) {
    if (!url) return {};
    NSString* scheme = url.scheme.lowercaseString;
    NSString* host = url.host.lowercaseString;
    if (!scheme.length || !host.length) return utf8(url.absoluteString);
    NSNumber* port = url.port;
    const bool default_port = port && (([scheme isEqualToString:@"http"] && port.integerValue == 80) || ([scheme isEqualToString:@"https"] && port.integerValue == 443));
    if ([host containsString:@":"] && ![host hasPrefix:@"["]) host = [NSString stringWithFormat:@"[%@]", host];
    NSString* value = port && !default_port ? [NSString stringWithFormat:@"%@://%@:%@", scheme, host, port] : [NSString stringWithFormat:@"%@://%@", scheme, host];
    return utf8(value);
}

NSMutableDictionary<NSString*, NSString*>* response_headers(const neoastra_resource_response_t& response) {
    NSMutableDictionary<NSString*, NSString*>* result = [NSMutableDictionary dictionary];
    NSString* raw = ns_string(neo_string(response.headers));
    for (NSString* line in [raw componentsSeparatedByCharactersInSet:[NSCharacterSet newlineCharacterSet]]) {
        NSRange separator = [line rangeOfString:@":"];
        if (separator.location == NSNotFound || separator.location == 0) continue;
        NSString* name = [[line substringToIndex:separator.location] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
        NSString* value = [[line substringFromIndex:separator.location + 1] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
        if (name.length) result[name] = value;
    }
    return result;
}

bool contains_header(NSDictionary<NSString*, NSString*>* headers, NSString* name) {
    for (NSString* candidate in headers) if ([candidate caseInsensitiveCompare:name] == NSOrderedSame) return true;
    return false;
}

struct resource_response_guard {
    neoastra_resource_response_t& response;
    void release() noexcept {
        if (response.release && response.release_context) {
            try { response.release(response.release_context); } catch (...) { }
            response.release = nullptr;
            response.release_context = nullptr;
        }
    }
    ~resource_response_guard() { release(); }
};

void fail_scheme_task(id<WKURLSchemeTask> task, NSInteger code, NSString* message) noexcept {
    @try {
        NSError* error = [NSError errorWithDomain:@"NeoAstra.WKURLSchemeHandler" code:code userInfo:@{NSLocalizedDescriptionKey: message ? message : @"Custom-scheme request failed."}];
        [task didFailWithError:error];
    } @catch (NSException*) { }
}

// The public interface of WKNavigationAction has no user-gesture flag on iOS and no mouse button either, so a link that was
// activated is reported without user initiation: a script can activate a link as well as a tap.
uint64_t link_activation(WKNavigationAction* action) {
    const bool activated = action.navigationType == WKNavigationTypeLinkActivated;
    return NEOASTRA_REQUEST_LINK_ACTIVATION_REPORTED | (activated ? NEOASTRA_REQUEST_LINK_ACTIVATED : 0);
}
// WKWebView names the reload and the history navigation of a submitted form with one type, which does not tell the kind.
uint64_t navigation_kind(WKNavigationAction* action) {
    switch (action.navigationType) {
        case WKNavigationTypeBackForward: return NEOASTRA_NAVIGATION_REQUEST_KIND_BACK_FORWARD;
        case WKNavigationTypeReload: return NEOASTRA_NAVIGATION_REQUEST_KIND_RELOAD;
        case WKNavigationTypeFormResubmitted: return NEOASTRA_NAVIGATION_REQUEST_KIND_UNKNOWN;
        default: return NEOASTRA_NAVIGATION_REQUEST_KIND_NEW_DOCUMENT;
    }
}

struct navigation_context { void (^handler)(WKNavigationActionPolicy); std::string uri; };
void navigation_decided(void* pointer, const neoastra_decision_response_t* response) noexcept {
    @autoreleasepool {
        std::unique_ptr<navigation_context> context(static_cast<navigation_context*>(pointer));
        const bool allowed = response->action == NEOASTRA_DECISION_ALLOW || response->action == NEOASTRA_DECISION_DEFAULT;
        context->handler(allowed ? WKNavigationActionPolicyAllow : WKNavigationActionPolicyCancel);
    }
}

// WebKit changes canGoBack and canGoForward together and notifies once for each, so the repeated pair is not reported twice.
void report_history(neoastra_view_t* view, WKWebView* webview) {
    auto* state = static_cast<uikit_view*>(view->platform);
    if (!state) return;
    const uint64_t history = neo_history_directions(view, webview.canGoBack, webview.canGoForward);
    if (state->history == history) return;
    state->history = history;
    neo_emit_view(view, NEOASTRA_EVENT_HISTORY_CHANGED, 0, nullptr, nullptr, history);
}

// WebKit selects a persistent store by identifier and keeps it in its own container, not in a directory the application chooses.
// The identifier is the SHA-256 name-based UUID (RFC 9562, version 8) of the root, as on macOS, so that one root names one store.
NSUUID* data_store_identifier(const std::string& user_data_root) {
    std::error_code failure;
    auto path = std::filesystem::weakly_canonical(std::filesystem::path(user_data_root), failure);
    if (failure) path = std::filesystem::path(user_data_root).lexically_normal();
    if (path.has_relative_path() && !path.has_filename()) path = path.parent_path();
    const auto name = path.string();
    static constexpr char scope[] = "neoastra:user-data-root:";
    unsigned char digest[CC_SHA256_DIGEST_LENGTH];
    CC_SHA256_CTX hash;
    CC_SHA256_Init(&hash);
    CC_SHA256_Update(&hash, scope, sizeof(scope) - 1);
    CC_SHA256_Update(&hash, name.data(), static_cast<CC_LONG>(name.size()));
    CC_SHA256_Final(digest, &hash);
    digest[6] = (digest[6] & 0x0f) | 0x80;
    digest[8] = (digest[8] & 0x3f) | 0x80;
    return [[NSUUID alloc] initWithUUIDBytes:digest];
}
// Without a root the environment keeps the default store, which is also where data stored before roots were honored remains.
WKWebsiteDataStore* persistent_data_store(neoastra_app_t* app, const std::string& user_data_root) {
    if (user_data_root.empty()) return [WKWebsiteDataStore defaultDataStore];
    if (@available(iOS 17.0, *)) return [WKWebsiteDataStore dataStoreForIdentifier:data_store_identifier(user_data_root)];
    neo_log(app, NEOASTRA_LOG_WARNING, "environment", "WKWebView cannot give a user-data root its own store before iOS 17; the default store is shared");
    return [WKWebsiteDataStore defaultDataStore];
}

UIView* view_parent(neoastra_view_t* view) {
    return view->parent.kind == NEOASTRA_NATIVE_PARENT_UIKIT_UIVIEW ? (__bridge UIView*)view->parent.handle : nil;
}
CGRect view_frame(neoastra_view_t* view, UIView* parent) {
    if (view->fill_parent) return parent.bounds;
    return CGRectMake(view->bounds.x, view->bounds.y, std::max(view->bounds.width, 1), std::max(view->bounds.height, 1));
}
}  // namespace

@implementation NeoURLSchemeHandler
- (void)webView:(WKWebView*)webView startURLSchemeTask:(id<WKURLSchemeTask>)task {
    (void)webView;
    auto* environment = self.nativeEnvironment;
    const auto* scheme = self.registration;
    if (!environment || !scheme || !scheme->provider) { fail_scheme_task(task, NEOASTRA_ERROR_DISPOSED, @"The custom-scheme provider is unavailable."); return; }
    neoastra_resource_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    resource_response_guard response_guard{response};
    @try {
        try {
            NSURLRequest* native_request = task.request;
            const auto uri = utf8(native_request.URL.absoluteString);
            const auto method = utf8(native_request.HTTPMethod.length ? native_request.HTTPMethod : @"GET");
            const auto headers = request_headers(native_request);
            NSData* request_body = native_request.HTTPBody;
            if (request_body.length > neo_maximum_buffered_resource_body_size) {
                neo_log(environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme request body exceeded the 64 MiB limit");
                fail_scheme_task(task, NEOASTRA_ERROR_INVALID_ARGUMENT, @"The custom-scheme request body is too large.");
                return;
            }
            const bool main_frame = !native_request.mainDocumentURL || [native_request.URL isEqual:native_request.mainDocumentURL];
            const auto initiating_origin = main_frame ? std::string{} : url_origin(native_request.mainDocumentURL);
            if (!neo_resource_request_within_limits(uri, method, headers, initiating_origin)) {
                neo_log(environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme request metadata exceeded its size limit");
                fail_scheme_task(task, NEOASTRA_ERROR_INVALID_ARGUMENT, @"The custom-scheme request metadata is too large.");
                return;
            }
            neoastra_resource_request_t request{};
            request.size = sizeof(request);
            request.version = 1;
            request.uri = neo_string_view(uri);
            request.method = neo_string_view(method);
            request.headers = neo_string_view(headers);
            request.initiating_origin = neo_string_view(initiating_origin);
            request.resource_kind = main_frame ? NEOASTRA_RESOURCE_DOCUMENT : NEOASTRA_RESOURCE_OTHER;
            request.main_frame = main_frame ? 1u : 0u;
            request.body = static_cast<const uint8_t*>(request_body.bytes);
            request.body_length = request_body.length;
            neoastra_result_t provider_result = NEOASTRA_ERROR_NATIVE_FAILURE;
            try { provider_result = scheme->provider(scheme->provider_context, &request, &response); }
            catch (...) { provider_result = NEOASTRA_ERROR_NATIVE_FAILURE; }
            if (provider_result != NEOASTRA_OK) {
                neo_log(environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme resource provider failed", provider_result);
                response_guard.release();
                response = {};
                response.size = sizeof(response);
                response.version = 1;
                response.status_code = 500;
            }
            if (!neo_valid_resource_response(response) || response.byte_length > NSUIntegerMax) {
                neo_log(environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme resource provider returned an invalid response");
                fail_scheme_task(task, NEOASTRA_ERROR_INVALID_ARGUMENT, @"The custom-scheme provider returned an invalid response.");
                return;
            }
            NSMutableDictionary<NSString*, NSString*>* native_headers = response_headers(response);
            if (response.mime_type.length && !contains_header(native_headers, @"Content-Type")) native_headers[@"Content-Type"] = ns_string(neo_string(response.mime_type));
            if (response.content_length != UINT64_MAX && !contains_header(native_headers, @"Content-Length")) native_headers[@"Content-Length"] = [NSString stringWithFormat:@"%llu", static_cast<unsigned long long>(response.content_length)];
            NSHTTPURLResponse* native_response = [[NSHTTPURLResponse alloc] initWithURL:native_request.URL statusCode:response.status_code HTTPVersion:@"HTTP/1.1" headerFields:native_headers];
            if (!native_response) { fail_scheme_task(task, NEOASTRA_ERROR_NATIVE_FAILURE, @"WKWebView could not create the custom-scheme response."); return; }
            NSData* data = nil;
            if (response.body_kind == NEOASTRA_RESOURCE_BODY_BYTES) data = [NSData dataWithBytes:response.bytes length:(NSUInteger)response.byte_length];
            else if (response.body_kind == NEOASTRA_RESOURCE_BODY_FILE) {
                NSError* file_error = nil;
                data = [NSData dataWithContentsOfFile:ns_string(neo_string(response.file_path)) options:NSDataReadingMappedIfSafe error:&file_error];
                if (!data) {
                    neo_log(environment->app, NEOASTRA_LOG_ERROR, "resource", "Could not map a custom-scheme file response", file_error.code);
                    [task didFailWithError:file_error ? file_error : [NSError errorWithDomain:@"NeoAstra.WKURLSchemeHandler" code:NEOASTRA_ERROR_NATIVE_FAILURE userInfo:nil]];
                    return;
                }
            }
            [task didReceiveResponse:native_response];
            if (data.length) [task didReceiveData:data];
            [task didFinish];
        } catch (...) {
            neo_log(environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme request handling failed");
            fail_scheme_task(task, NEOASTRA_ERROR_NATIVE_FAILURE, @"Custom-scheme request handling failed.");
        }
    } @catch (NSException* exception) {
        neo_log(environment->app, NEOASTRA_LOG_ERROR, "resource", "WKWebView custom-scheme request handling raised an exception");
        fail_scheme_task(task, NEOASTRA_ERROR_NATIVE_FAILURE, exception.reason ? exception.reason : @"Custom-scheme request handling failed.");
    }
}
- (void)webView:(WKWebView*)webView stopURLSchemeTask:(id<WKURLSchemeTask>)task { (void)webView; (void)task; }
@end

@implementation NeoAstraDelegate
- (void)webView:(WKWebView*)webView decidePolicyForNavigationAction:(WKNavigationAction*)action decisionHandler:(void (^)(WKNavigationActionPolicy))handler {
    (void)webView;
    auto* view = self.nativeView;
    if (!view) { handler(WKNavigationActionPolicyCancel); return; }
    // An action without a target frame asks for a window of its own, which this backend does not provide yet.
    if (!action.targetFrame) { handler(WKNavigationActionPolicyCancel); return; }
    const uint64_t kind = navigation_kind(action);
    if (neo_refuses_navigation(view, kind)) { handler(WKNavigationActionPolicyCancel); return; }
    std::string uri = utf8(action.request.URL.absoluteString);
    auto* decision = new neoastra_decision;
    neo_configure_decision(decision, view, NEOASTRA_DECISION_NAVIGATION, NEOASTRA_DECISION_ALLOW);
    decision->completion = navigation_decided;
    decision->completion_context = new navigation_context{[handler copy], uri};
    decision->external_uri = uri;
    const uint64_t flags = (action.targetFrame.isMainFrame ? NEOASTRA_NAVIGATION_REQUEST_MAIN_FRAME : 0) | link_activation(action) | kind;
    neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_REQUESTED, 0, nullptr, &uri, flags, 0, decision);
    neo_finish_decision_event(view, decision);
    decision->release();
}
- (void)webView:(WKWebView*)webView didStartProvisionalNavigation:(WKNavigation*)navigation {
    (void)navigation;
    auto* view = self.nativeView;
    if (!view) return;
    std::string uri = utf8(webView.URL.absoluteString);
    neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_STARTED, 0, nullptr, &uri, 1);
}
- (void)webView:(WKWebView*)webView didFinishNavigation:(WKNavigation*)navigation {
    (void)navigation;
    auto* view = self.nativeView;
    if (!view) return;
    std::string uri = utf8(webView.URL.absoluteString);
    neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_COMPLETED, 0, nullptr, &uri);
}
- (void)webView:(WKWebView*)webView didFailNavigation:(WKNavigation*)navigation withError:(NSError*)error {
    (void)navigation;
    auto* view = self.nativeView;
    if (!view) return;
    std::string uri = utf8(webView.URL.absoluteString);
    neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_FAILED, 0, nullptr, &uri, NEOASTRA_ERROR_NATIVE_FAILURE, error.code);
}
- (void)webView:(WKWebView*)webView didFailProvisionalNavigation:(WKNavigation*)navigation withError:(NSError*)error { [self webView:webView didFailNavigation:navigation withError:error]; }
// iOS ends the web content process of a view under memory pressure, and a view that lost its process shows nothing. The
// application recreates the view, as it does on the desktop.
- (void)webViewWebContentProcessDidTerminate:(WKWebView*)webView {
    (void)webView;
    auto* view = self.nativeView;
    if (view) neo_emit_view(view, NEOASTRA_EVENT_WEB_PROCESS_TERMINATED, 0, nullptr, nullptr, NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED | NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW);
}
- (void)userContentController:(WKUserContentController*)controller didReceiveScriptMessage:(WKScriptMessage*)message {
    (void)controller;
    auto* view = self.nativeView;
    if (!view) return;
    @try {
        try {
            NSError* error = nil;
            NSData* data = [NSJSONSerialization dataWithJSONObject:message.body options:NSJSONWritingFragmentsAllowed error:&error];
            std::string text;
            if (data && !error) text.assign((const char*)data.bytes, data.length);
            else text = utf8([message.body description]);
            std::string uri = url_origin(message.frameInfo.request.URL);
            neo_emit_bridge_message(view, text, uri, message.frameInfo.mainFrame);
        } catch (...) { neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "bridge", "WKWebView web-message handling failed"); }
    } @catch (NSException*) { neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "bridge", "WKWebView web-message handling raised an exception"); }
}
- (void)observeValueForKeyPath:(NSString*)keyPath ofObject:(id)object change:(NSDictionary<NSKeyValueChangeKey, id>*)change context:(void*)context {
    (void)change;
    (void)context;
    auto* view = self.nativeView;
    if (!view) return;
    WKWebView* webview = (WKWebView*)object;
    if ([keyPath isEqualToString:@"title"]) {
        view->title = utf8(webview.title);
        neo_emit_view(view, NEOASTRA_EVENT_TITLE_CHANGED, 0, &view->title);
    } else if ([keyPath isEqualToString:@"URL"]) {
        view->source = utf8(webview.URL.absoluteString);
        neo_emit_view(view, NEOASTRA_EVENT_SOURCE_CHANGED, 0, nullptr, &view->source);
    } else if ([keyPath isEqualToString:@"canGoBack"] || [keyPath isEqualToString:@"canGoForward"]) report_history(view, webview);
}
@end

bool neo_platform_initialize(neoastra_app_t* app, neoastra_error_t** error) noexcept {
    auto* state = new (std::nothrow) uikit_app{};
    if (!state) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "the iOS application state could not be allocated", 0, uikit_domain); return false; }
    app->platform = state;
    return true;
}
void neo_platform_shutdown(neoastra_app_t* app) noexcept {
    delete static_cast<uikit_app*>(app->platform);
    app->platform = nullptr;
}
bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept {
    dispatch_async(dispatch_get_main_queue(), ^{ neo_destroy_app_on_ui(app); });
    return true;
}
// UIApplicationMain owns the run loop of an iOS process; an application attaches to it instead.
int32_t neo_platform_run(neoastra_app_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
// An iOS application cannot end its own process, so a quit request only leaves the application state.
void neo_platform_quit(neoastra_app_t*) noexcept {}
void neo_platform_wake(neoastra_app_t* app) noexcept {
    app->retain();
    dispatch_async(dispatch_get_main_queue(), ^{ neo_drain_dispatch(app); app->release(); });
}
bool neo_platform_schedule_decision_timeout(neoastra_app_t*, neoastra_decision_t* decision) noexcept {
    const auto remaining = std::chrono::duration_cast<std::chrono::nanoseconds>(decision->deadline - std::chrono::steady_clock::now()).count();
    decision->retain();
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, std::max<int64_t>(remaining, 1)), dispatch_get_main_queue(), ^{ decision->expire(); decision->release(); });
    return true;
}

bool neo_platform_window_create(neoastra_window_t*, const neoastra_window_options_t*, neoastra_error_t** error) noexcept { unsupported(error, "A window"); return false; }
void neo_platform_window_destroy(neoastra_window_t*) noexcept {}
neoastra_result_t neo_platform_window_show(neoastra_window_t*, bool) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_activate(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_force_close(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_title(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_get_frame(neoastra_window_t*, neoastra_rect_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_state(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_attribute(neoastra_window_t*, neoastra_window_attribute_t, bool) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_begin_drag(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_begin_resize(neoastra_window_t*, neoastra_window_resize_edge_t) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_title_bar(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_background_color(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_get_title_bar(neoastra_window_t*, neoastra_title_bar_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_get_handle(neoastra_window_t*, neoastra_native_handle_kind_t, neoastra_native_handle_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }


bool neo_platform_environment_create_async(neoastra_environment_t* environment, const neoastra_environment_options_t* options, neo_platform_created_callback_t callback, void* context, neoastra_error_t** error) noexcept {
    @autoreleasepool {
        @try {
            try {
                for (const auto& scheme : environment->custom_schemes) {
                    NSString* name = ns_string(scheme.name);
                    if ((scheme.flags & NEOASTRA_CUSTOM_SCHEME_SERVICE_WORKERS) != 0) {
                        neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, "WKWebView custom schemes do not support service workers", 0, webkit_domain);
                        return false;
                    }
                    if (!name || [WKWebView handlesURLScheme:name]) {
                        neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, "WKWebView cannot replace a built-in URL scheme", 0, webkit_domain);
                        return false;
                    }
                }
                auto state = std::make_unique<uikit_environment>();
                state->data_store = options->private_mode ? [WKWebsiteDataStore nonPersistentDataStore] : persistent_data_store(environment->app, neo_string(options->user_data_root));
                environment->platform = state.release();
                callback(context, nullptr);
                return true;
            } catch (const std::exception& exception) {
                neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, exception.what(), 0, webkit_domain);
                return false;
            }
        } @catch (NSException* exception) {
            neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, utf8(exception.reason).c_str(), 0, webkit_domain);
            return false;
        }
    }
}
void neo_platform_environment_destroy(neoastra_environment_t* environment) noexcept {
    delete static_cast<uikit_environment*>(environment->platform);
    environment->platform = nullptr;
}
// A profile that is not ephemeral is the store of its environment, so it follows the environment's user-data root and private mode.
bool neo_platform_profile_create(neoastra_profile_t* profile, neoastra_error_t** error) noexcept {
    @autoreleasepool {
        auto* environment = static_cast<uikit_environment*>(profile->environment->platform);
        if (!environment || !environment->data_store) { neo_fail(error, NEOASTRA_ERROR_INVALID_STATE, "WKWebView environment is unavailable", 0, webkit_domain); return false; }
        auto* state = new (std::nothrow) uikit_profile{};
        if (!state) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Could not allocate WKWebView profile state", 0, webkit_domain); return false; }
        state->data_store = profile->ephemeral ? [WKWebsiteDataStore nonPersistentDataStore] : environment->data_store;
        profile->platform = state;
        return true;
    }
}
void neo_platform_profile_destroy(neoastra_profile_t* profile) noexcept {
    delete static_cast<uikit_profile*>(profile->platform);
    profile->platform = nullptr;
}
neoastra_result_t neo_platform_profile_get_cookies(neoastra_profile_t*, const std::string&, neoastra_buffer_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Cookie access"); }
neoastra_result_t neo_platform_profile_set_cookie(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Cookie access"); }
neoastra_result_t neo_platform_profile_delete_cookie(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Cookie access"); }
neoastra_result_t neo_platform_profile_clear_data(neoastra_profile_t*, neoastra_data_kind_t, int64_t, int64_t, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Clearing browser data"); }

bool neo_platform_view_create_async(neoastra_view_t* view, const neoastra_view_options_t* options, neo_platform_created_callback_t callback, void* context, neoastra_error_t** error) noexcept {
    @autoreleasepool {
        @try {
            try {
                if (options->popup_request) { unsupported(error, "A popup view"); return false; }
                auto* environment = static_cast<uikit_environment*>(view->environment->platform);
                UIView* parent = view_parent(view);
                if (!environment || !parent) {
                    neo_fail(error, NEOASTRA_ERROR_INVALID_STATE, "WKWebView environment or parent is unavailable", 0, webkit_domain);
                    return false;
                }
                auto state = std::make_unique<uikit_view>();
                WKWebViewConfiguration* configuration = [WKWebViewConfiguration new];
                if (!configuration) {
                    neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "WKWebView could not allocate a view configuration", 0, webkit_domain);
                    return false;
                }
                auto* profile = view->profile ? static_cast<uikit_profile*>(view->profile->platform) : nullptr;
                configuration.websiteDataStore = profile ? profile->data_store : environment->data_store;
                state->scheme_handlers = [NSMutableArray arrayWithCapacity:view->environment->custom_schemes.size()];
                for (const auto& scheme : view->environment->custom_schemes) {
                    NeoURLSchemeHandler* handler = [NeoURLSchemeHandler new];
                    if (!handler) throw std::bad_alloc();
                    handler.nativeEnvironment = view->environment;
                    handler.registration = &scheme;
                    [configuration setURLSchemeHandler:handler forURLScheme:ns_string(scheme.name)];
                    [state->scheme_handlers addObject:handler];
                }
                state->delegate = [NeoAstraDelegate new];
                if (!state->delegate) throw std::bad_alloc();
                state->delegate.nativeView = view;
                if (view->bridge_policy != NEOASTRA_BRIDGE_DISABLED)
                    [configuration.userContentController addScriptMessageHandler:state->delegate name:@"_neoastra_transport_v1"];
                state->webview = [[WKWebView alloc] initWithFrame:view_frame(view, parent) configuration:configuration];
                if (!state->webview) {
                    neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "WKWebView could not create the browser view", 0, webkit_domain);
                    return false;
                }
                state->webview.navigationDelegate = state->delegate;
                if (view->fill_parent) state->webview.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
                [state->webview addObserver:state->delegate forKeyPath:@"title" options:NSKeyValueObservingOptionNew context:nullptr];
                [state->webview addObserver:state->delegate forKeyPath:@"URL" options:NSKeyValueObservingOptionNew context:nullptr];
                [state->webview addObserver:state->delegate forKeyPath:@"canGoBack" options:NSKeyValueObservingOptionNew context:nullptr];
                [state->webview addObserver:state->delegate forKeyPath:@"canGoForward" options:NSKeyValueObservingOptionNew context:nullptr];
                [parent addSubview:state->webview];
                view->platform = state.release();
                callback(context, nullptr);
                return true;
            } catch (const std::exception& exception) {
                neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, exception.what(), 0, webkit_domain);
                return false;
            }
        } @catch (NSException* exception) {
            neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, utf8(exception.reason).c_str(), 0, webkit_domain);
            return false;
        }
    }
}
void neo_platform_view_destroy(neoastra_view_t* view) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        if (!state) return;
        @try {
            [state->webview removeObserver:state->delegate forKeyPath:@"title"];
            [state->webview removeObserver:state->delegate forKeyPath:@"URL"];
            [state->webview removeObserver:state->delegate forKeyPath:@"canGoBack"];
            [state->webview removeObserver:state->delegate forKeyPath:@"canGoForward"];
        } @catch (NSException*) { }
        state->delegate.nativeView = nullptr;
        state->webview.navigationDelegate = nil;
        if (view->bridge_policy != NEOASTRA_BRIDGE_DISABLED)
            [state->webview.configuration.userContentController removeScriptMessageHandlerForName:@"_neoastra_transport_v1"];
        [state->webview removeFromSuperview];
        delete state;
        view->platform = nullptr;
    }
}
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t* view) noexcept {
    auto* state = static_cast<uikit_view*>(view->platform);
    if (!state || !state->webview) return NEOASTRA_ERROR_NOT_INITIALIZED;
    UIView* parent = view_parent(view);
    if (!parent) return NEOASTRA_ERROR_INVALID_STATE;
    state->webview.frame = view_frame(view, parent);
    state->webview.autoresizingMask = view->fill_parent ? (UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight) : UIViewAutoresizingNone;
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_view_navigate(neoastra_view_t* view, const std::string& uri, neoastra_error_t** error) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        NSURL* url = [NSURL URLWithString:ns_string(uri)];
        if (!state || !state->webview || !url) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "Invalid WKWebView navigation URI", 0, webkit_domain);
        [state->webview loadRequest:[NSURLRequest requestWithURL:url]];
        return NEOASTRA_OK;
    }
}
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t* view, const std::string& uri, const std::string& method, const std::string& headers, const uint8_t* body, uint64_t body_length, neoastra_error_t** error) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        NSURL* url = [NSURL URLWithString:ns_string(uri)];
        if (!state || !state->webview || !url || method.empty() || body_length > NSUIntegerMax) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "Invalid WKWebView navigation request", 0, webkit_domain);
        NSMutableURLRequest* request = [NSMutableURLRequest requestWithURL:url];
        request.HTTPMethod = ns_string(method);
        if (body_length) request.HTTPBody = [NSData dataWithBytes:body length:(NSUInteger)body_length];
        for (NSString* line in [ns_string(headers) componentsSeparatedByCharactersInSet:[NSCharacterSet newlineCharacterSet]]) {
            NSRange separator = [line rangeOfString:@":"];
            if (separator.location == NSNotFound) continue;
            NSString* name = [[line substringToIndex:separator.location] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
            NSString* value = [[line substringFromIndex:separator.location + 1] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
            if (name.length) [request setValue:value forHTTPHeaderField:name];
        }
        [state->webview loadRequest:request];
        return NEOASTRA_OK;
    }
}
neoastra_result_t neo_platform_view_load_html(neoastra_view_t* view, const std::string& html, const std::string& base, neoastra_error_t**) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        if (!state || !state->webview) return NEOASTRA_ERROR_NOT_INITIALIZED;
        NSURL* url = base.empty() ? nil : [NSURL URLWithString:ns_string(base)];
        [state->webview loadHTMLString:ns_string(html) baseURL:url];
        return NEOASTRA_OK;
    }
}
neoastra_result_t neo_platform_view_command(neoastra_view_t* view, uint32_t command) noexcept {
    auto* state = static_cast<uikit_view*>(view->platform);
    if (!state || !state->webview) return NEOASTRA_ERROR_NOT_INITIALIZED;
    switch (command) {
        case 0: [state->webview stopLoading]; break;
        case 1: [state->webview reload]; break;
        case 2: [state->webview reloadFromOrigin]; break;
        case 3: [state->webview goBack]; break;
        case 4: [state->webview goForward]; break;
        default: return NEOASTRA_ERROR_INVALID_ARGUMENT;
    }
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t* view, const std::string& script, neoastra_string_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t**) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        if (!state || !state->webview) return NEOASTRA_ERROR_NOT_INITIALIZED;
        [state->webview evaluateJavaScript:ns_string(script) completionHandler:^(id value, NSError* error) {
            neoastra_result_t requested = error ? NEOASTRA_ERROR_NATIVE_FAILURE : NEOASTRA_OK, actual{};
            neoastra_error_t* native_error = nullptr;
            if (error) neo_fail(&native_error, requested, utf8(error.localizedDescription), error.code, webkit_domain);
            std::string output = "null";
            // NSJSONSerialization raises an exception for a value without a JSON form, such as a date or a number that is not finite.
            if (value) {
                @try {
                    NSData* data = [NSJSONSerialization dataWithJSONObject:value options:NSJSONWritingFragmentsAllowed error:nil];
                    if (data) output.assign((const char*)data.bytes, data.length);
                } @catch (NSException*) { output = "null"; }
            }
            if (operation->try_complete(requested, actual)) callback(context, actual, actual == NEOASTRA_OK ? neo_string_view(output) : neoastra_string_view_t{}, native_error);
            if (native_error) native_error->release();
            operation->release();
        }];
        return NEOASTRA_OK;
    }
}
neoastra_result_t neo_platform_view_add_script(neoastra_view_t* view, const std::string& script, const neoastra_script_options_t* options, neoastra_string_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t**) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        if (!state || !state->webview) return NEOASTRA_ERROR_NOT_INITIALIZED;
        if (options->isolated_world) return NEOASTRA_ERROR_NOT_SUPPORTED;
        auto time = options->injection_time == NEOASTRA_SCRIPT_DOCUMENT_END ? WKUserScriptInjectionTimeAtDocumentEnd : WKUserScriptInjectionTimeAtDocumentStart;
        WKUserScript* user_script = [[WKUserScript alloc] initWithSource:ns_string(script) injectionTime:time forMainFrameOnly:options->main_frame_only != 0];
        std::string identifier = utf8([NSUUID UUID].UUIDString);
        state->scripts.emplace(identifier, user_script);
        [state->webview.configuration.userContentController addUserScript:user_script];
        dispatch_async(dispatch_get_main_queue(), ^{
            neoastra_result_t actual{};
            if (operation->try_complete(NEOASTRA_OK, actual)) callback(context, actual, actual == NEOASTRA_OK ? neo_string_view(identifier) : neoastra_string_view_t{}, nullptr);
            operation->release();
        });
        return NEOASTRA_OK;
    }
}
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t* view, const std::string& identifier) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        if (!state || !state->webview) return NEOASTRA_ERROR_NOT_INITIALIZED;
        auto found = state->scripts.find(identifier);
        if (found == state->scripts.end()) return NEOASTRA_ERROR_INVALID_ARGUMENT;
        state->scripts.erase(found);
        auto* manager = state->webview.configuration.userContentController;
        [manager removeAllUserScripts];
        for (const auto& entry : state->scripts) [manager addUserScript:entry.second];
        return NEOASTRA_OK;
    }
}
neoastra_result_t neo_platform_view_post_message(neoastra_view_t* view, const std::string& message, bool json, neoastra_error_t**) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        if (!state || !state->webview) return NEOASTRA_ERROR_NOT_INITIALIZED;
        std::string script = "window.dispatchEvent(new CustomEvent('neoastramessage',{detail:" + (json ? message : "null") + "}));";
        [state->webview evaluateJavaScript:ns_string(script) completionHandler:nil];
        return NEOASTRA_OK;
    }
}
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t*, double*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t*, double) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_set_setting(neoastra_view_t* view, neoastra_view_setting_t setting, bool enabled) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        if (!state || !state->webview) return NEOASTRA_ERROR_NOT_INITIALIZED;
        switch (setting) {
            // WKWebView has no browser shortcuts, status bar, or script dialogs of its own, and a touch screen has no context
            // menu and no Tab key, so there is nothing to turn off, and enabling one reports that it is missing.
            case NEOASTRA_VIEW_SETTING_BROWSER_ACCELERATOR_KEYS:
            case NEOASTRA_VIEW_SETTING_STATUS_BAR:
            case NEOASTRA_VIEW_SETTING_DEFAULT_SCRIPT_DIALOGS:
            case NEOASTRA_VIEW_SETTING_DEFAULT_CONTEXT_MENUS:
            case NEOASTRA_VIEW_SETTING_ZOOM_CONTROLS:
            case NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS:
                return enabled ? NEOASTRA_ERROR_NOT_SUPPORTED : NEOASTRA_OK;
            case NEOASTRA_VIEW_SETTING_DEVTOOLS:
                if (@available(iOS 16.4, *)) { state->webview.inspectable = enabled; return NEOASTRA_OK; }
                return enabled ? NEOASTRA_ERROR_NOT_SUPPORTED : NEOASTRA_OK;
            // The requests that walk the history are refused as they come. The swipe of WKWebView is off unless a host turns it on
            // through the native view, and it stays off here.
            case NEOASTRA_VIEW_SETTING_HISTORY_NAVIGATION:
                if (!enabled) state->webview.allowsBackForwardNavigationGestures = NO;
                report_history(view, state->webview);
                return NEOASTRA_OK;
            default: return NEOASTRA_ERROR_INVALID_ARGUMENT;
        }
    }
}
// A web address that the system takes opens in its default browser, and the system reports no reason when it does not.
bool neo_platform_open_external(const std::string& uri, int64_t* native_code) noexcept {
    @autoreleasepool {
        if (native_code) *native_code = 0;
        NSURL* url = [NSURL URLWithString:ns_string(uri)];
        if (!url) return false;
        [[UIApplication sharedApplication] openURL:url options:@{} completionHandler:nil];
        return true;
    }
}
neoastra_result_t neo_platform_view_move_pointer(neoastra_view_t*, double, double) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_open_devtools(neoastra_view_t* view) noexcept {
    auto* state = static_cast<uikit_view*>(view->platform);
    return !state || !state->webview ? NEOASTRA_ERROR_NOT_INITIALIZED : NEOASTRA_ERROR_NOT_SUPPORTED;
}
// WKWebView snapshots what the view shows; it has no call for the part of a document outside the viewport.
neoastra_result_t neo_platform_view_capture(neoastra_view_t* view, const neoastra_capture_options_t& options, neoastra_buffer_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept {
    @autoreleasepool {
        auto* state = static_cast<uikit_view*>(view->platform);
        if (!state || !state->webview) return neo_fail(error, NEOASTRA_ERROR_NOT_INITIALIZED, "WKWebView view is not initialized", 0, webkit_domain);
        if (options.full_page) return neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, "WKWebView cannot capture a whole document", 0, webkit_domain);
        WKSnapshotConfiguration* configuration = [[WKSnapshotConfiguration alloc] init];
        if (options.region.width > 0 && options.region.height > 0) {
            // The rectangle is in the points of the view, whose origin is the top-left corner, and a CSS pixel is one point.
            const CGRect requested = CGRectIntersection(CGRectMake(options.region.x, options.region.y, options.region.width, options.region.height), state->webview.bounds);
            if (CGRectIsNull(requested) || CGRectIsEmpty(requested)) return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, "The capture region is outside the visible viewport", 0, webkit_domain);
            configuration.rect = requested;
        }
        const auto format = options.format;
        const auto quality = options.quality ? options.quality : 80u;
        [state->webview takeSnapshotWithConfiguration:configuration completionHandler:^(UIImage* image, NSError* failure) {
            @autoreleasepool {
                NSData* data = nil;
                if (image) data = format == NEOASTRA_CAPTURE_FORMAT_JPEG ? UIImageJPEGRepresentation(image, quality / 100.0) : UIImagePNGRepresentation(image);
                neoastra_result_t requested = data.length ? NEOASTRA_OK : NEOASTRA_ERROR_NATIVE_FAILURE, actual{};
                neoastra_error_t* native_error = nullptr;
                neoastra_buffer_t* buffer = nullptr;
                if (requested == NEOASTRA_OK) {
                    try {
                        const auto* bytes = static_cast<const uint8_t*>(data.bytes);
                        buffer = new neoastra_buffer(std::vector<uint8_t>(bytes, bytes + data.length));
                    } catch (...) { requested = NEOASTRA_ERROR_NATIVE_FAILURE; }
                }
                if (requested != NEOASTRA_OK) neo_fail(&native_error, requested, failure ? utf8(failure.localizedDescription) : std::string("WKWebView returned no capture"), failure ? failure.code : 0, webkit_domain);
                if (operation->try_complete(requested, actual)) {
                    callback(context, actual, actual == NEOASTRA_OK ? buffer : nullptr, actual == requested ? native_error : nullptr);
                    if (actual != NEOASTRA_OK && buffer) buffer->release();
                } else if (buffer) buffer->release();
                if (native_error) native_error->release();
                operation->release();
            }
        }];
        return NEOASTRA_OK;
    }
}
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t* view, neoastra_native_handle_kind_t kind, neoastra_native_handle_t* handle) noexcept {
    if (kind != NEOASTRA_NATIVE_HANDLE_WKWEBVIEW && kind != NEOASTRA_NATIVE_HANDLE_UIKIT_UIVIEW) return NEOASTRA_ERROR_NOT_SUPPORTED;
    auto* state = static_cast<uikit_view*>(view->platform);
    if (!state || !state->webview) return NEOASTRA_ERROR_NOT_INITIALIZED;
    handle->kind = kind;
    handle->value = (__bridge void*)state->webview;
    return NEOASTRA_OK;
}
