#include "../src/common/native_internal.hpp"

#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>

@interface LifetimeTitleSource : NSObject
@property(nonatomic, readonly) NSString* title;
@end
@implementation LifetimeTitleSource
- (NSString*)title { return @"The outer event owns these title bytes until its handler returns"; }
@end

@interface LifetimeFrameInfo : NSObject
@end
@implementation LifetimeFrameInfo
- (NSURLRequest*)request { return [NSURLRequest requestWithURL:[NSURL URLWithString:@"https://example.invalid/"]]; }
- (BOOL)isMainFrame { return YES; }
@end

@interface LifetimeScriptMessage : NSObject
@end
@implementation LifetimeScriptMessage
- (id)body { return @"release the view from MessageReceived"; }
- (WKFrameInfo*)frameInfo { return (WKFrameInfo*)[LifetimeFrameInfo new]; }
@end

// A deterministic WKDownload protocol boundary: no network or files are used.
@interface LifetimeDownload : NSObject
@property(nonatomic, strong) NSProgress* progress;
@property(nonatomic, weak) id<WKDownloadDelegate> delegate;
@property(nonatomic) BOOL cancelled;
- (void)cancel:(void (^)(NSData*))completionHandler;
@end
@implementation LifetimeDownload
- (void)cancel:(void (^)(NSData*))completionHandler {
    if (!self.cancelled) {
        self.cancelled = YES;
        [self.delegate download:(WKDownload*)self didFailWithError:[NSError errorWithDomain:NSURLErrorDomain code:NSURLErrorCancelled userInfo:nil] resumeData:nil];
    }
    if (completionHandler) completionHandler(nil);
}
@end

@interface NSObject (LifetimeDownloadBoundary)
- (void)beginDownload:(WKDownload*)download request:(NSURLRequest*)request;
@end

namespace {

struct completion {
    bool done{};
    void* value{};
};

void NEOASTRA_CALL environment_created(void* context, neoastra_result_t result, neoastra_environment_t* value, const neoastra_error_t*) {
    assert(result == NEOASTRA_OK && value);
    *static_cast<completion*>(context) = {true, value};
}

void NEOASTRA_CALL view_created(void* context, neoastra_result_t result, neoastra_view_t* value, const neoastra_error_t*) {
    assert(result == NEOASTRA_OK && value);
    *static_cast<completion*>(context) = {true, value};
}

void wait_for(const completion& state) {
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:30];
    while (!state.done) {
        assert(deadline.timeIntervalSinceNow > 0);
        [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.01]];
    }
}

neoastra_app_t* create_app() {
    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    neoastra_app_t* app{};
    assert(neoastra_app_attach(&options, &app, nullptr) == NEOASTRA_OK && app);
    return app;
}

neoastra_window_t* create_window(neoastra_app_t* app) {
    neoastra_window_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.bounds = {100, 100, 320, 240};
    neoastra_window_t* window{};
    assert(neoastra_app_create_window(app, &options, &window, nullptr) == NEOASTRA_OK && window);
    return window;
}

void* native_handle(neoastra_window_t* window) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_window_get_native_handle(window, NEOASTRA_NATIVE_HANDLE_COCOA_NSWINDOW, &handle) == NEOASTRA_OK);
    return handle.value;
}

WKWebView* native_handle(neoastra_view_t* view) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_view_get_native_handle(view, NEOASTRA_NATIVE_HANDLE_WKWEBVIEW, &handle) == NEOASTRA_OK);
    return (__bridge WKWebView*)handle.value;
}

struct view_release {
    neoastra_view_t* view;
    int calls{};
    neoastra_decision_t* decision{};
};

void NEOASTRA_CALL release_view(void* context, const neoastra_event_t* event) {
    auto& state = *static_cast<view_release*>(context);
    ++state.calls;
    if (event->decision) {
        state.decision = event->decision;
        neoastra_decision_retain(state.decision);
    }
    assert(neoastra_view_set_event_callback(state.view, nullptr, nullptr) == NEOASTRA_OK);
    neoastra_view_release(state.view);
}

enum class release_event { confirm, prompt, message };

void test_view_release_in_policy(release_event kind) {
    auto* app = create_app();
    auto* window = create_window(app);
    neoastra_environment_options_t environment_options{};
    environment_options.size = sizeof(environment_options);
    environment_options.version = 1;
    environment_options.private_mode = 1;
    completion environment_state;
    assert(neoastra_environment_create_async(app, &environment_options, environment_created, &environment_state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(environment_state);
    auto* environment = static_cast<neoastra_environment_t*>(environment_state.value);
    neoastra_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.window = window;
    options.fill_parent = 1;
    if (kind == release_event::message) options.bridge_policy = NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW;
    completion view_state;
    assert(neoastra_environment_create_view_async(environment, &options, view_created, &view_state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(view_state);
    auto* view = static_cast<neoastra_view_t*>(view_state.value);
    WKWebView* webview = native_handle(view);
    id<WKUIDelegate, WKScriptMessageHandler> delegate = (id<WKUIDelegate, WKScriptMessageHandler>)webview.UIDelegate;
    view_release state{view};
    assert(neoastra_view_set_event_callback(view, release_view, &state) == NEOASTRA_OK);
    __block int completions = 0;
    WKFrameInfo* frame = (WKFrameInfo*)[LifetimeFrameInfo new];
    if (kind == release_event::message) {
        [delegate userContentController:webview.configuration.userContentController didReceiveScriptMessage:(WKScriptMessage*)[LifetimeScriptMessage new]];
    } else if (kind == release_event::prompt) {
        [delegate webView:webview runJavaScriptTextInputPanelWithPrompt:@"prompt" defaultText:@"initial" initiatedByFrame:frame completionHandler:^(NSString* result) {
            assert(result == nil);
            ++completions;
        }];
    } else {
        [delegate webView:webview runJavaScriptConfirmPanelWithMessage:@"confirm" initiatedByFrame:frame completionHandler:^(BOOL result) {
            assert(!result);
            ++completions;
        }];
    }
    assert(state.calls == 1 && completions == (kind == release_event::message ? 0 : 1));
    assert(window->views.empty());
    if (kind != release_event::message) {
        assert(state.decision);
        neoastra_decision_response_t response{};
        response.size = sizeof(response);
        response.version = 1;
        response.action = NEOASTRA_DECISION_ALLOW;
        assert(neoastra_decision_complete(state.decision, &response, nullptr) == NEOASTRA_ERROR_INVALID_STATE);
        neoastra_decision_release(state.decision);
    }
    neoastra_environment_release(environment);
    neoastra_window_release(window);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

struct window_release {
    neoastra_app_t* app;
    neoastra_window_t* window;
    int calls{};
};

void NEOASTRA_CALL release_window(void* context, const neoastra_event_t* event) {
    if (event->header.type != NEOASTRA_EVENT_WINDOW_RESIZED) return;
    auto& state = *static_cast<window_release*>(context);
    ++state.calls;
    assert(neoastra_app_set_event_callback(state.app, nullptr, nullptr) == NEOASTRA_OK);
    neoastra_window_release(state.window);
}

void test_window_release_in_resize() {
    auto* app = create_app();
    auto* window = create_window(app);
    NSWindow* native = (__bridge NSWindow*)native_handle(window);
    id<NSWindowDelegate> delegate = native.delegate;
    window_release state{app, window};
    assert(neoastra_app_set_event_callback(app, release_window, &state) == NEOASTRA_OK);
    [delegate windowDidResize:[NSNotification notificationWithName:NSWindowDidResizeNotification object:native]];
    assert(state.calls == 1);
    assert(neoastra_app_get_window_count(app) == 0);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

struct resize_detach {
    neoastra_app_t* app;
    bool detached{};
};

void NEOASTRA_CALL detach_in_resize(void* context, const neoastra_event_t* event) {
    if (event->header.type != NEOASTRA_EVENT_WINDOW_RESIZED) return;
    auto& state = *static_cast<resize_detach*>(context);
    state.detached = true;
    assert(neoastra_app_detach(state.app, nullptr) == NEOASTRA_OK);
}

void test_detach_during_window_api() {
    auto* app = create_app();
    auto* window = create_window(app);
    resize_detach state{app};
    assert(neoastra_app_set_event_callback(app, detach_in_resize, &state) == NEOASTRA_OK);
    const auto result = neoastra_window_set_bounds(window, {100, 100, 400, 300});
    assert(result == NEOASTRA_OK || result == NEOASTRA_ERROR_DISPOSED);
    assert(state.detached);
    assert(neoastra_app_get_window_count(app) == 0);
    neoastra_window_release(window);
    neoastra_app_release(app);
}

struct application_release {
    neoastra_app_t* app;
    bool detach;
};

void NEOASTRA_CALL release_application(void* context, const neoastra_event_t* event) {
    assert(event->header.type == NEOASTRA_EVENT_APPLICATION_OPEN_FILE);
    const auto& state = *static_cast<application_release*>(context);
    auto* app = state.app;
    assert(neoastra_app_set_event_callback(app, nullptr, nullptr) == NEOASTRA_OK);
    if (state.detach) assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    else neoastra_app_release(app);
}

void test_application_release_in_launch() {
    for (const bool detach : {false, true}) {
        auto* app = create_app();
        __unsafe_unretained id<NSApplicationDelegate> delegate = NSApp.delegate;
        application_release state{app, detach};
        assert(neoastra_app_set_event_callback(app, release_application, &state) == NEOASTRA_OK);
        // The second entry and delegate chaining run after the callback releases
        // the client reference or destroys cocoa_app and its delegate ownership.
        [delegate application:NSApp openFiles:@[@"/tmp/first", @"/tmp/second"]];
        assert(NSApp.delegate != delegate);
        if (detach) neoastra_app_release(app);
    }
}

void NEOASTRA_CALL mutate_title(void* context, const neoastra_event_t* event) {
    assert(event->header.type == NEOASTRA_EVENT_TITLE_CHANGED);
    const auto read_title = [event] { return std::string(reinterpret_cast<const char*>(event->text.data), static_cast<size_t>(event->text.length)); };
    const auto original = read_title();
    assert(original == "The outer event owns these title bytes until its handler returns");
    auto* view = static_cast<neoastra_view_t*>(context);
    view->title.assign(8192, 'x');
    assert(read_title() == original);
}

void test_event_payload_snapshot() {
    auto* app = create_app();
    auto* window = create_window(app);
    neoastra_environment_options_t environment_options{};
    environment_options.size = sizeof(environment_options);
    environment_options.version = 1;
    environment_options.private_mode = 1;
    completion environment_state;
    assert(neoastra_environment_create_async(app, &environment_options, environment_created, &environment_state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(environment_state);
    auto* environment = static_cast<neoastra_environment_t*>(environment_state.value);
    neoastra_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.window = window;
    completion view_state;
    assert(neoastra_environment_create_view_async(environment, &options, view_created, &view_state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(view_state);
    auto* view = static_cast<neoastra_view_t*>(view_state.value);
    WKWebView* webview = native_handle(view);
    NSObject* delegate = (NSObject*)webview.UIDelegate;
    assert(neoastra_view_set_event_callback(view, mutate_title, view) == NEOASTRA_OK);
    [delegate observeValueForKeyPath:@"title" ofObject:[LifetimeTitleSource new] change:@{} context:nullptr];
    assert(neoastra_view_set_event_callback(view, nullptr, nullptr) == NEOASTRA_OK);
    neoastra_view_release(view);
    neoastra_environment_release(environment);
    neoastra_window_release(window);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

enum class download_release_phase { requested, started, progress };

struct download_events {
    neoastra_view_t* view;
    bool defer;
    download_release_phase release_phase;
    bool detach;
    bool retain_download;
    neoastra_download_t* download{};
    neoastra_decision_t* decision{};
    int started{};
    int progress{};
    bool view_released{};
};

void NEOASTRA_CALL release_from_download(void* context, const neoastra_event_t* event) {
    auto& state = *static_cast<download_events*>(context);
    bool release_view{};
    if (event->header.type == NEOASTRA_EVENT_DOWNLOAD_REQUESTED) {
        state.download = event->download;
        if (state.retain_download) neoastra_download_retain(state.download);
        if (state.defer) {
            state.decision = event->decision;
            neoastra_decision_retain(state.decision);
            assert(neoastra_decision_defer(state.decision) == NEOASTRA_OK);
        } else {
            neoastra_decision_response_t response{};
            response.size = sizeof(response);
            response.version = 1;
            response.action = NEOASTRA_DECISION_ALLOW;
            assert(neoastra_decision_complete(event->decision, &response, nullptr) == NEOASTRA_OK);
        }
        release_view = state.release_phase == download_release_phase::requested;
    } else if (event->header.type == NEOASTRA_EVENT_DOWNLOAD_STARTED) {
        ++state.started;
        release_view = state.release_phase == download_release_phase::started;
    } else if (event->header.type == NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED) {
        ++state.progress;
        release_view = state.release_phase == download_release_phase::progress;
    }
    if (release_view) {
        state.view_released = true;
        assert(neoastra_view_set_event_callback(state.view, nullptr, nullptr) == NEOASTRA_OK);
        if (state.detach) assert(neoastra_app_detach(state.view->environment->app, nullptr) == NEOASTRA_OK);
        neoastra_view_release(state.view);
    }
}

void test_download_reentrant_release(bool deferred, download_release_phase phase, bool detach, bool retain_download) {
    auto* app = create_app();
    auto* window = create_window(app);
    neoastra_environment_options_t environment_options{};
    environment_options.size = sizeof(environment_options);
    environment_options.version = 1;
    environment_options.private_mode = 1;
    completion environment_state;
    assert(neoastra_environment_create_async(app, &environment_options, environment_created, &environment_state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(environment_state);
    auto* environment = static_cast<neoastra_environment_t*>(environment_state.value);
    neoastra_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.window = window;
    completion view_state;
    assert(neoastra_environment_create_view_async(environment, &options, view_created, &view_state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(view_state);
    auto* view = static_cast<neoastra_view_t*>(view_state.value);
    WKWebView* webview = native_handle(view);
    NSObject<WKDownloadDelegate>* delegate = (NSObject<WKDownloadDelegate>*)webview.UIDelegate;
    LifetimeDownload* download = [LifetimeDownload new];
    download.progress = [NSProgress progressWithTotalUnitCount:10];
    NSURL* uri = [NSURL URLWithString:@"https://example.invalid/lifetime.bin"];
    [delegate beginDownload:(WKDownload*)download request:[NSURLRequest requestWithURL:uri]];
    download_events events{view, deferred, phase, detach, retain_download};
    assert(neoastra_view_set_event_callback(view, release_from_download, &events) == NEOASTRA_OK);
    __block int destinations = 0;
    __block bool accepted = false;
    [delegate download:(WKDownload*)download decideDestinationUsingResponse:[[NSURLResponse alloc] initWithURL:uri MIMEType:@"application/octet-stream" expectedContentLength:10 textEncodingName:nil]
        suggestedFilename:@"neoastra-lifetime-fixture.bin" completionHandler:^(NSURL* destination) {
            ++destinations;
            accepted = destination != nil;
            if (!destination) [download cancel:nil];
        }];
    assert(events.download);
    if (deferred && !events.view_released) {
        assert(destinations == 0 && events.started == 0);
        assert(events.download->state.load() == NEOASTRA_DOWNLOAD_REQUESTED);
        neoastra_decision_response_t response{};
        response.size = sizeof(response);
        response.version = 1;
        response.action = NEOASTRA_DECISION_ALLOW;
        assert(neoastra_decision_complete(events.decision, &response, nullptr) == NEOASTRA_OK);
    }
    if (phase == download_release_phase::progress) [delegate downloadDidFinish:(WKDownload*)download];
    assert(destinations == 1);
    assert(accepted == (phase != download_release_phase::requested));
    assert(events.started == (phase == download_release_phase::requested ? 0 : 1));
    assert(events.view_released && window->views.empty());
    if (retain_download) {
        assert(events.download->view == nullptr && events.download->platform == nullptr);
        assert(events.download->lifecycle_released.load());
    } else {
        // No client retained the borrowed handle. After the event/operation
        // guards and lifecycle release, no registered object owns its storage.
        for (auto* object = app->ui_objects; object; object = object->ui_next) assert(object != events.download);
    }
    if (events.decision) neoastra_decision_release(events.decision);
    if (retain_download) neoastra_download_release(events.download);
    neoastra_environment_release(environment);
    neoastra_window_release(window);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

} // namespace

int main() {
    @autoreleasepool {
        test_view_release_in_policy(release_event::confirm);
        test_view_release_in_policy(release_event::prompt);
        test_view_release_in_policy(release_event::message);
        test_window_release_in_resize();
        test_detach_during_window_api();
        test_application_release_in_launch();
        test_event_payload_snapshot();
        test_download_reentrant_release(true, download_release_phase::requested, false, true);
        test_download_reentrant_release(true, download_release_phase::started, false, true);
        test_download_reentrant_release(false, download_release_phase::started, false, true);
        test_download_reentrant_release(true, download_release_phase::progress, false, true);
        test_download_reentrant_release(true, download_release_phase::requested, true, true);
        test_download_reentrant_release(false, download_release_phase::started, false, false);
    }
    return 0;
}
