#include "neoastra.h"

#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstdint>
#include <cstdio>
#include <string>

namespace {

struct completion {
    bool done{};
    neoastra_result_t result{NEOASTRA_ERROR_UNKNOWN};
    void* value{};
    std::string text;
};

// What the view has reported: the page of the navigation it finished last, and whether a navigation failed
// or its web process exited.
struct view_events {
    std::string page;
    bool failed{};
};

// A self-contained page, so the test needs neither the network nor a resource provider. The link sits between
// two text fields, which the Tab key reaches whatever the setting is.
const std::string page =
    "data:text/html,<input id=first><a id=link href='https://example.invalid/'>link</a><input id=second>";

neoastra_string_view_t string_view(const std::string& value) {
    return {reinterpret_cast<const uint8_t*>(value.data()), value.size()};
}

// The application is attached, so the test pumps the main run loop that delivers completions and view events.
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

void complete(void* context, neoastra_result_t result, void* value) {
    auto* state = static_cast<completion*>(context);
    state->result = result;
    state->value = value;
    state->done = true;
}

void NEOASTRA_CALL environment_created(void* context, neoastra_result_t result, neoastra_environment_t* value, const neoastra_error_t*) { complete(context, result, value); }
void NEOASTRA_CALL view_created(void* context, neoastra_result_t result, neoastra_view_t* value, const neoastra_error_t*) { complete(context, result, value); }
void NEOASTRA_CALL script_evaluated(void* context, neoastra_result_t result, neoastra_string_view_t value, const neoastra_error_t*) {
    auto* state = static_cast<completion*>(context);
    if (value.data) state->text.assign(reinterpret_cast<const char*>(value.data), static_cast<size_t>(value.length));
    complete(context, result, nullptr);
}

void NEOASTRA_CALL record_event(void* context, const neoastra_event_t* event) {
    auto* events = static_cast<view_events*>(context);
    switch (event->header.type) {
    case NEOASTRA_EVENT_NAVIGATION_COMPLETED:
        events->page.clear();
        if (event->uri.data) events->page.assign(reinterpret_cast<const char*>(event->uri.data), static_cast<size_t>(event->uri.length));
        break;
    case NEOASTRA_EVENT_NAVIGATION_FAILED:
    case NEOASTRA_EVENT_WEB_PROCESS_TERMINATED:
        events->failed = true;
        break;
    default:
        break;
    }
}

// Returns the JSON text of the value the script evaluates to.
std::string evaluate(neoastra_view_t* view, const std::string& script) {
    completion state;
    assert(neoastra_view_evaluate_script_async(view, string_view(script), script_evaluated, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    assert(state.result == NEOASTRA_OK);
    return state.text;
}

WKWebView* web_view(neoastra_view_t* view) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_view_get_native_handle(view, NEOASTRA_NATIVE_HANDLE_WKWEBVIEW, &handle) == NEOASTRA_OK && handle.value != nullptr);
    return (__bridge WKWebView*)handle.value;
}

// The window is hidden, so the key press is handed to the view the way AppKit hands it one from the keyboard.
void press_key(WKWebView* target, NSString* characters, unsigned short key_code) {
    for (const auto type : {NSEventTypeKeyDown, NSEventTypeKeyUp}) {
        NSEvent* event = [NSEvent keyEventWithType:type location:NSZeroPoint modifierFlags:0 timestamp:NSProcessInfo.processInfo.systemUptime
            windowNumber:target.window.windowNumber context:nil characters:characters charactersIgnoringModifiers:characters isARepeat:NO keyCode:key_code];
        if (type == NSEventTypeKeyDown) [target keyDown:event];
        else [target keyUp:event];
    }
}

// Presses Tab with the first text field focused and waits until the focus has left it for `expected`.
void expect_tab_reaches(neoastra_view_t* view, const std::string& expected) {
    assert(evaluate(view, "document.getElementById('first').focus();document.activeElement.id") == "\"first\"");
    press_key(web_view(view), @"\t", 48);
    const std::string moved = "\"" + expected + "\"";
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:30];
    for (;;) {
        const auto focused = evaluate(view, "document.activeElement.id");
        if (focused == moved) return;
        if (focused != "\"first\"" || deadline.timeIntervalSinceNow <= 0) {
            std::fprintf(stderr, "Expected the Tab key to reach '%s', but the focus is on %s\n", expected.c_str(), focused.c_str());
            assert(false);
        }
        pump();
    }
}

} // namespace

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

        // A private environment keeps its website data in memory, so the test leaves nothing behind.
        neoastra_environment_options_t environment_options{};
        environment_options.size = sizeof(environment_options);
        environment_options.version = 1;
        environment_options.private_mode = 1;
        completion environment_state;
        assert(neoastra_environment_create_async(app, &environment_options, environment_created, &environment_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(environment_state);
        assert(environment_state.result == NEOASTRA_OK && environment_state.value != nullptr);
        auto* environment = static_cast<neoastra_environment_t*>(environment_state.value);

        neoastra_view_options_t view_options{};
        view_options.size = sizeof(view_options);
        view_options.version = 1;
        view_options.window = window;
        view_options.fill_parent = 1;
        completion view_state;
        assert(neoastra_environment_create_view_async(environment, &view_options, view_created, &view_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(view_state);
        assert(view_state.result == NEOASTRA_OK && view_state.value != nullptr);
        auto* view = static_cast<neoastra_view_t*>(view_state.value);

        view_events events;
        assert(neoastra_view_set_event_callback(view, record_event, &events) == NEOASTRA_OK);
        assert(neoastra_view_navigate(view, string_view(page), nullptr) == NEOASTRA_OK);
        NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:60];
        while (events.page.empty()) {
            assert(!events.failed && deadline.timeIntervalSinceNow > 0);
            pump();
        }
        [web_view(view).window makeFirstResponder:web_view(view)];

        // WKWebView skips links until it is told otherwise, and the setting can change while a page is shown.
        expect_tab_reaches(view, "second");
        assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS, 1) == NEOASTRA_OK);
        expect_tab_reaches(view, "link");
        assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS, 0) == NEOASTRA_OK);
        expect_tab_reaches(view, "second");

        neoastra_view_release(view);
        neoastra_environment_release(environment);
        neoastra_window_release(window);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    return 0;
}
