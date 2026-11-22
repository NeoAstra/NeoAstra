// The protected channel of an isolated-world script in a real WKWebView: the page neither sees nor reaches the binding, the
// world does not see the page's globals, an event that the page dispatches is not trusted while a click of the user is, and
// removing the script takes the binding away.
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
#include <vector>

namespace {

struct completion {
    bool done{};
    neoastra_result_t result{NEOASTRA_ERROR_UNKNOWN};
    void* value{};
    std::string text;
};

struct script_message {
    std::string text, uri, world, binding;
    uint64_t value{};
};

struct view_events {
    bool loaded{};
    bool failed{};
    std::vector<script_message> script_messages;
    std::vector<std::string> page_messages;
};

const std::string world = "neoastra.tests";
const std::string binding = "neoTestChannel";

// The page keeps a global of its own, and offers the host bridge of the view, through which it posts what it likes.
const std::string page = "<body style='margin:0'><script>window.pageSecret = 1;</script></body>";

// The script of the world: a host element at the top-left corner whose clicks it reports with their isTrusted, an echo of
// what the host posts, and a first message that says what the world sees of the page.
const std::string world_script =
    "const host = document.createElement('div');"
    "host.id = 'neo-host';"
    "host.style.cssText = 'position:absolute;left:0;top:0;width:200px;height:100px';"
    "document.body.append(host);"
    "host.addEventListener('click', event => neoTestChannel.postMessage('click:' + event.isTrusted));"
    "neoTestChannel.onmessage = event => neoTestChannel.postMessage('echo:' + event.data);"
    "neoTestChannel.postMessage('ready:' + typeof window.pageSecret + ':' + typeof neoTestChannel.postMessage);";

neoastra_string_view_t string_view(const std::string& value) {
    return {reinterpret_cast<const uint8_t*>(value.data()), value.size()};
}

std::string text_of(neoastra_string_view_t value) {
    return value.data ? std::string(reinterpret_cast<const char*>(value.data), static_cast<size_t>(value.length)) : std::string();
}

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

// Lets the engine deliver whatever was on its way.
void settle() {
    NSDate* settled = [NSDate dateWithTimeIntervalSinceNow:0.5];
    while (settled.timeIntervalSinceNow > 0) pump();
}

void complete(void* context, neoastra_result_t result, void* value) {
    auto* state = static_cast<completion*>(context);
    state->result = result;
    state->value = value;
    state->done = true;
}

void NEOASTRA_CALL environment_created(void* context, neoastra_result_t result, neoastra_environment_t* value, const neoastra_error_t*) { complete(context, result, value); }
void NEOASTRA_CALL view_created(void* context, neoastra_result_t result, neoastra_view_t* value, const neoastra_error_t*) { complete(context, result, value); }
void NEOASTRA_CALL text_completed(void* context, neoastra_result_t result, neoastra_string_view_t value, const neoastra_error_t*) {
    static_cast<completion*>(context)->text = text_of(value);
    complete(context, result, nullptr);
}

void NEOASTRA_CALL record_event(void* context, const neoastra_event_t* event) {
    auto* events = static_cast<view_events*>(context);
    switch (event->header.type) {
    case NEOASTRA_EVENT_NAVIGATION_COMPLETED:
        events->loaded = true;
        break;
    case NEOASTRA_EVENT_NAVIGATION_FAILED:
    case NEOASTRA_EVENT_WEB_PROCESS_TERMINATED:
        events->failed = true;
        break;
    case NEOASTRA_EVENT_SCRIPT_MESSAGE_RECEIVED:
        events->script_messages.push_back({text_of(event->text), text_of(event->uri), text_of(event->text2), text_of(event->text3), event->value});
        break;
    case NEOASTRA_EVENT_MESSAGE_RECEIVED:
        events->page_messages.push_back(text_of(event->text));
        break;
    default:
        break;
    }
}

std::string evaluate(neoastra_view_t* view, const std::string& script) {
    completion state;
    assert(neoastra_view_evaluate_script_async(view, string_view(script), text_completed, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    assert(state.result == NEOASTRA_OK);
    return state.text;
}

neoastra_result_t add_script(neoastra_view_t* view, const std::string& source, const std::string& world_name, const std::string& binding_name, std::string& identifier) {
    neoastra_script_options_t options{};
    options.size = sizeof(options);
    options.version = 2;
    options.injection_time = NEOASTRA_SCRIPT_DOCUMENT_END;
    options.main_frame_only = 1;
    options.isolated_world = 1;
    options.world_name = string_view(world_name);
    options.binding_name = string_view(binding_name);
    completion state;
    const auto started = neoastra_view_add_script_async(view, string_view(source), &options, text_completed, &state, nullptr, nullptr);
    if (started != NEOASTRA_OK) return started;
    wait_for(state);
    identifier = state.text;
    return state.result;
}

// Waits for the script message `text`, which has to be the next one, and checks where it comes from.
void expect_script_message(view_events& events, const std::string& text) {
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:30];
    while (events.script_messages.empty()) {
        if (deadline.timeIntervalSinceNow <= 0) {
            std::fprintf(stderr, "Expected the script message '%s', but none came\n", text.c_str());
            assert(false);
        }
        pump();
    }
    const auto message = events.script_messages.front();
    events.script_messages.erase(events.script_messages.begin());
    if (message.text != text || message.world != world || message.binding != binding || message.value != 1) {
        std::fprintf(stderr, "Expected the script message '%s' of %s/%s, but got '%s' of %s/%s\n", text.c_str(), world.c_str(), binding.c_str(),
            message.text.c_str(), message.world.c_str(), message.binding.c_str());
        assert(false);
    }
}

WKWebView* web_view(neoastra_view_t* view) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_view_get_native_handle(view, NEOASTRA_NATIVE_HANDLE_WKWEBVIEW, &handle) == NEOASTRA_OK && handle.value != nullptr);
    return (__bridge WKWebView*)handle.value;
}

// A click handed to the view the way AppKit hands it one from the mouse. `x` and `y` are CSS pixels from the top-left corner.
void click(WKWebView* target, int x, int y) {
    const NSPoint in_view = NSMakePoint(x, target.isFlipped ? y : NSHeight(target.bounds) - y);
    const NSPoint in_window = [target convertPoint:in_view toView:nil];
    for (const auto type : {NSEventTypeLeftMouseDown, NSEventTypeLeftMouseUp}) {
        NSEvent* event = [NSEvent mouseEventWithType:type location:in_window modifierFlags:0 timestamp:NSProcessInfo.processInfo.systemUptime
            windowNumber:target.window.windowNumber context:nil eventNumber:1 clickCount:1 pressure:type == NSEventTypeLeftMouseDown ? 1 : 0];
        if (type == NSEventTypeLeftMouseDown) [target mouseDown:event];
        else [target mouseUp:event];
    }
}

neoastra_result_t post(neoastra_view_t* view, const std::string& identifier, const std::string& message) {
    neoastra_error_t* error{};
    const auto result = neoastra_view_post_script_message(view, string_view(identifier), string_view(message), &error);
    if (error) neoastra_error_release(error);
    return result;
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

        neoastra_environment_options_t environment_options{};
        environment_options.size = sizeof(environment_options);
        environment_options.version = 1;
        environment_options.private_mode = 1;
        completion environment_state;
        assert(neoastra_environment_create_async(app, &environment_options, environment_created, &environment_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(environment_state);
        assert(environment_state.result == NEOASTRA_OK && environment_state.value != nullptr);
        auto* environment = static_cast<neoastra_environment_t*>(environment_state.value);

        neoastra_capability_info_t capability{};
        capability.size = sizeof(capability);
        capability.version = 1;
        assert(neoastra_environment_get_capability(environment, NEOASTRA_CAPABILITY_SCRIPT_WORLD_CHANNEL, &capability) == NEOASTRA_OK);
        assert(capability.support == NEOASTRA_SUPPORT_NATIVE);

        // The page has the host bridge of the view, so that a message it forges has a way to the host.
        neoastra_view_options_t view_options{};
        view_options.size = sizeof(view_options);
        view_options.version = 1;
        view_options.window = window;
        view_options.fill_parent = 1;
        view_options.bridge_policy = NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW;
        completion view_state;
        assert(neoastra_environment_create_view_async(environment, &view_options, view_created, &view_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(view_state);
        assert(view_state.result == NEOASTRA_OK && view_state.value != nullptr);
        auto* view = static_cast<neoastra_view_t*>(view_state.value);

        view_events events;
        assert(neoastra_view_set_event_callback(view, record_event, &events) == NEOASTRA_OK);
        std::string identifier;
        assert(add_script(view, world_script, world, binding, identifier) == NEOASTRA_OK && !identifier.empty());
        // One binding name is used once per world; another world may have it.
        std::string duplicate;
        assert(add_script(view, "void 0;", world, binding, duplicate) == NEOASTRA_ERROR_INVALID_STATE);
        std::string other;
        assert(add_script(view, "void 0;", "neoastra.tests.other", binding, other) == NEOASTRA_OK);
        assert(neoastra_view_remove_script(view, string_view(other)) == NEOASTRA_OK);

        // A script whose addition was canceled does not stay: its binding name is free again.
        {
            neoastra_script_options_t options{};
            options.size = sizeof(options);
            options.version = 2;
            options.main_frame_only = 1;
            options.isolated_world = 1;
            const std::string canceled_world = "neoastra.tests.canceled";
            options.world_name = string_view(canceled_world);
            options.binding_name = string_view(binding);
            completion state;
            neoastra_operation_t* operation{};
            assert(neoastra_view_add_script_async(view, string_view(std::string("void 0;")), &options, text_completed, &state, &operation, nullptr) == NEOASTRA_OK);
            neoastra_operation_cancel(operation);
            wait_for(state);
            assert(state.result == NEOASTRA_ERROR_CANCELED && state.text.empty());
            neoastra_operation_release(operation);
            std::string again;
            assert(add_script(view, "void 0;", canceled_world, binding, again) == NEOASTRA_OK);
            assert(neoastra_view_remove_script(view, string_view(again)) == NEOASTRA_OK);
        }

        assert(neoastra_view_load_html(view, string_view(page), string_view(std::string()), nullptr) == NEOASTRA_OK);
        NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:60];
        while (!events.loaded) {
            assert(!events.failed && deadline.timeIntervalSinceNow > 0);
            pump();
        }
        [web_view(view).window makeFirstResponder:web_view(view)];

        // The world sees the binding and not the page's globals; the page sees neither the binding nor its handler.
        expect_script_message(events, "ready:undefined:function");
        assert(evaluate(view, "typeof globalThis.neoTestChannel") == "\"undefined\"");
        assert(evaluate(view, "typeof (globalThis.webkit && webkit.messageHandlers && webkit.messageHandlers.neoTestChannel)") == "\"undefined\"");

        // What the page forges goes through its own bridge, never the binding of the world.
        evaluate(view, "try { webkit.messageHandlers._neoastra_transport_v1.postMessage('forged'); } catch (error) {} 0");
        // An event that the page dispatches on the element of the world is not trusted; a click of the user is.
        evaluate(view, "document.getElementById('neo-host').dispatchEvent(new MouseEvent('click', { bubbles: true })); 0");
        expect_script_message(events, "click:false");
        click(web_view(view), 50, 50);
        expect_script_message(events, "click:true");
        settle();
        assert(events.script_messages.empty());
        assert(events.page_messages.size() == 1 && events.page_messages.front() == "\"forged\"");

        // The host reaches the world through the binding, also after the page replaced the builtins of its own world.
        assert(post(view, identifier, "ping") == NEOASTRA_OK);
        expect_script_message(events, "echo:ping");
        evaluate(view, "EventTarget.prototype.addEventListener = function () {}; JSON.stringify = () => 'tampered'; MessageEvent = function () {}; 0");
        assert(post(view, identifier, "after") == NEOASTRA_OK);
        expect_script_message(events, "echo:after");

        // Only a script with a binding has a channel.
        assert(post(view, "unknown", "ping") == NEOASTRA_ERROR_INVALID_ARGUMENT);

        // Removing the script takes its binding away: nothing more comes from the world, and nothing can be posted to it.
        assert(neoastra_view_remove_script(view, string_view(identifier)) == NEOASTRA_OK);
        assert(post(view, identifier, "ping") == NEOASTRA_ERROR_INVALID_ARGUMENT);
        click(web_view(view), 50, 50);
        settle();
        assert(events.script_messages.empty());

        neoastra_view_release(view);
        neoastra_environment_release(environment);
        neoastra_window_release(window);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    std::puts("macOS script world tests passed");
    return 0;
}
