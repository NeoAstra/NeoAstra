#include "neoastra.h"

#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstdint>

namespace {

struct completion {
    bool done{};
    neoastra_result_t result{NEOASTRA_ERROR_UNKNOWN};
    void* value{};
};

void pump() {
    [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.01]];
}

void wait_for(const completion& state) {
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:60];
    while (!state.done) {
        assert(deadline.timeIntervalSinceNow > 0);
        pump();
    }
}

void NEOASTRA_CALL environment_created(void* context, neoastra_result_t result, neoastra_environment_t* value, const neoastra_error_t*) {
    auto* state = static_cast<completion*>(context);
    state->result = result;
    state->value = value;
    state->done = true;
}

void NEOASTRA_CALL view_created(void* context, neoastra_result_t result, neoastra_view_t* value, const neoastra_error_t*) {
    auto* state = static_cast<completion*>(context);
    state->result = result;
    state->value = value;
    state->done = true;
}

WKWebView* web_view(neoastra_view_t* view) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_view_get_native_handle(view, NEOASTRA_NATIVE_HANDLE_WKWEBVIEW, &handle) == NEOASTRA_OK);
    return (__bridge WKWebView*)handle.value;
}

NSWindow* ns_window(neoastra_window_t* window) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_window_get_native_handle(window, NEOASTRA_NATIVE_HANDLE_COCOA_NSWINDOW, &handle) == NEOASTRA_OK);
    return (__bridge NSWindow*)handle.value;
}

// The distance of a view from the top of its parent, whichever way the parent counts.
CGFloat top_of(NSView* view) {
    auto* parent = view.superview;
    return parent.isFlipped ? NSMinY(view.frame) : NSHeight(parent.bounds) - NSMaxY(view.frame);
}

}

// Explicit bounds count from the top-left corner of the window, and the view keeps that place when the window resizes.
int main() {
    @autoreleasepool {
        neoastra_app_options_t app_options{};
        app_options.size = sizeof(app_options);
        app_options.version = 1;
        app_options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
        neoastra_app_t* app = nullptr;
        assert(neoastra_app_attach(&app_options, &app, nullptr) == NEOASTRA_OK && app != nullptr);

        neoastra_window_options_t window_options{};
        window_options.size = sizeof(window_options);
        window_options.version = 1;
        window_options.bounds = {100, 100, 640, 480};
        window_options.flags = 3;
        neoastra_window_t* window = nullptr;
        assert(neoastra_app_create_window(app, &window_options, &window, nullptr) == NEOASTRA_OK && window != nullptr);

        neoastra_environment_options_t environment_options{};
        environment_options.size = sizeof(environment_options);
        environment_options.version = 1;
        completion environment_state;
        assert(neoastra_environment_create_async(app, &environment_options, environment_created, &environment_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(environment_state);
        assert(environment_state.result == NEOASTRA_OK);
        auto* environment = static_cast<neoastra_environment_t*>(environment_state.value);

        neoastra_view_options_t view_options{};
        view_options.size = sizeof(view_options);
        view_options.version = 1;
        view_options.window = window;
        view_options.bounds = {40, 30, 200, 100};
        completion view_state;
        assert(neoastra_environment_create_view_async(environment, &view_options, view_created, &view_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(view_state);
        assert(view_state.result == NEOASTRA_OK);
        auto* view = static_cast<neoastra_view_t*>(view_state.value);
        auto* web = web_view(view);
        assert(NSMinX(web.frame) == 40 && top_of(web) == 30 && NSWidth(web.frame) == 200 && NSHeight(web.frame) == 100);

        assert(neoastra_view_set_bounds(view, {60, 50, 300, 120}, 0) == NEOASTRA_OK);
        assert(NSMinX(web.frame) == 60 && top_of(web) == 50 && NSWidth(web.frame) == 300 && NSHeight(web.frame) == 120);

        auto* host = ns_window(window);
        NSRect larger = host.frame;
        larger.size.height += 120;
        larger.size.width += 80;
        [host setFrame:larger display:NO];
        assert(NSMinX(web.frame) == 60 && top_of(web) == 50 && NSWidth(web.frame) == 300 && NSHeight(web.frame) == 120);

        assert(neoastra_view_set_visible(view, 0) == NEOASTRA_OK && web.hidden);
        assert(neoastra_view_set_visible(view, 1) == NEOASTRA_OK && !web.hidden);

        assert(neoastra_view_set_bounds(view, {60, 50, 300, 120}, 1) == NEOASTRA_OK);
        assert(NSEqualRects(web.frame, web.superview.bounds));

        neoastra_view_release(view);
        neoastra_window_release(window);
        neoastra_environment_release(environment);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    return 0;
}
