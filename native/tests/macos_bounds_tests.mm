#include "neoastra.h"

#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstdint>
#include <string>

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

struct key_chords {
    int count{};
    std::string last;
};

// Takes Cmd+P and leaves every other chord to the page.
uint32_t NEOASTRA_CALL take_chord(void* context, neoastra_string_view_t chord) {
    auto* state = static_cast<key_chords*>(context);
    state->count++;
    state->last.assign(reinterpret_cast<const char*>(chord.data), static_cast<size_t>(chord.length));
    return state->last == "Meta+P" ? 1 : 0;
}

NSEvent* command_key(NSWindow* window, NSString* characters, unsigned short code, NSTimeInterval timestamp) {
    return [NSEvent keyEventWithType:NSEventTypeKeyDown location:NSZeroPoint modifierFlags:NSEventModifierFlagCommand timestamp:timestamp
                        windowNumber:window.windowNumber context:nil characters:characters charactersIgnoringModifiers:characters isARepeat:NO keyCode:code];
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
        assert(neoastra_view_focus(view) == NEOASTRA_ERROR_INVALID_STATE);
        assert(neoastra_view_set_visible(view, 1) == NEOASTRA_OK && !web.hidden);

        // The keyboard goes to a sibling when the view that holds it hides.
        view_options.bounds = {300, 200, 100, 80};
        completion sibling_state;
        assert(neoastra_environment_create_view_async(environment, &view_options, view_created, &sibling_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(sibling_state);
        assert(sibling_state.result == NEOASTRA_OK);
        auto* sibling = static_cast<neoastra_view_t*>(sibling_state.value);
        auto* sibling_web = web_view(sibling);
        assert(neoastra_view_focus(view) == NEOASTRA_OK);
        assert([(NSView*)host.firstResponder isDescendantOf:web]);
        assert(neoastra_view_set_visible(view, 0) == NEOASTRA_OK);
        assert([host.firstResponder isKindOfClass:NSView.class] && [(NSView*)host.firstResponder isDescendantOf:sibling_web]);
        assert(neoastra_view_set_visible(view, 1) == NEOASTRA_OK);

        // A chord goes to the view that holds the keyboard, and only one that the host takes stops there.
        key_chords chords;
        assert(neoastra_view_set_key_chord_callback(view, take_chord, &chords) == NEOASTRA_OK);
        assert(![web performKeyEquivalent:command_key(host, @"p", 35, 1)] && chords.count == 0);
        assert(neoastra_view_focus(view) == NEOASTRA_OK);
        assert([web performKeyEquivalent:command_key(host, @"p", 35, 2)] && chords.count == 1 && chords.last == "Meta+P");
        (void)[web performKeyEquivalent:command_key(host, @"k", 40, 3)];
        assert(chords.count == 2 && chords.last == "Meta+K");
        assert(neoastra_view_set_key_chord_callback(view, nullptr, nullptr) == NEOASTRA_OK);

        // A view placed over a view of another window moves there, as the same web view, on the rectangle of the anchor.
        neoastra_window_t* other = nullptr;
        assert(neoastra_app_create_window(app, &window_options, &other, nullptr) == NEOASTRA_OK && other != nullptr);
        neoastra_view_options_t anchor_options{};
        anchor_options.size = sizeof(anchor_options);
        anchor_options.version = 1;
        anchor_options.window = other;
        anchor_options.fill_parent = 1;
        completion anchor_state;
        assert(neoastra_environment_create_view_async(environment, &anchor_options, view_created, &anchor_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(anchor_state);
        assert(anchor_state.result == NEOASTRA_OK);
        auto* anchor = static_cast<neoastra_view_t*>(anchor_state.value);
        neoastra_rect_t placed{};
        assert(neoastra_view_place_over(sibling, anchor, 20, 30, 150, 90, &placed) == NEOASTRA_OK);
        assert(placed.x == 20 && placed.y == 30 && placed.width == 150 && placed.height == 90);
        assert(web_view(sibling) == sibling_web && sibling_web.window == ns_window(other));
        assert(NSMinX(sibling_web.frame) == 20 && top_of(sibling_web) == 30 && NSWidth(sibling_web.frame) == 150 && NSHeight(sibling_web.frame) == 90);
        assert(neoastra_view_place_over(anchor, sibling, 0, 0, 10, 10, &placed) == NEOASTRA_ERROR_INVALID_STATE);
        neoastra_view_release(sibling);
        neoastra_view_release(anchor);
        neoastra_window_release(other);

        // A view that fills the window goes below a view on explicit bounds, also when it comes second.
        anchor_options.window = window;
        completion filling_state;
        assert(neoastra_environment_create_view_async(environment, &anchor_options, view_created, &filling_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(filling_state);
        assert(filling_state.result == NEOASTRA_OK);
        auto* filling = static_cast<neoastra_view_t*>(filling_state.value);
        assert([web.superview.subviews indexOfObject:web_view(filling)] < [web.superview.subviews indexOfObject:web]);
        neoastra_view_release(filling);

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
