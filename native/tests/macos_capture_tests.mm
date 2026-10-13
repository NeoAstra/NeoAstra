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
#include <utility>
#include <vector>

// The settings that the tests of WebKit itself use: devices that need no hardware, which a page is given whatever the
// bundle of the process says. They are not part of the public interface of WKWebView, and only this test names them.
@interface WKPreferences (NeoAstraCaptureTests)
- (void)_setMockCaptureDevicesEnabled:(BOOL)enabled;
- (void)_setMediaDevicesEnabled:(BOOL)enabled;
@end

namespace {

struct completion {
    bool done{};
    neoastra_result_t result{NEOASTRA_ERROR_UNKNOWN};
    void* value{};
    std::string text;
};

// What the view asked its host about capture, as the kind and the origin of each request, and what the test answers
// for each device.
struct view_events {
    bool loaded{};
    bool failed{};
    std::vector<std::pair<uint64_t, std::string>> requests;
    neoastra_decision_action_t microphone{NEOASTRA_DECISION_ALLOW};
    neoastra_decision_action_t camera{NEOASTRA_DECISION_ALLOW};
};

const std::string capture_origin = "https://capture.invalid";

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
        events->loaded = true;
        break;
    case NEOASTRA_EVENT_NAVIGATION_FAILED:
    case NEOASTRA_EVENT_WEB_PROCESS_TERMINATED:
        events->failed = true;
        break;
    case NEOASTRA_EVENT_PERMISSION_REQUESTED: {
        std::string origin;
        if (event->uri.data) origin.assign(reinterpret_cast<const char*>(event->uri.data), static_cast<size_t>(event->uri.length));
        events->requests.emplace_back(event->value, origin);
        neoastra_decision_response_t response{};
        response.size = sizeof(response);
        response.version = 1;
        response.action = event->value == NEOASTRA_PERMISSION_MICROPHONE ? events->microphone
            : event->value == NEOASTRA_PERMISSION_CAMERA ? events->camera : NEOASTRA_DECISION_DENY;
        assert(neoastra_decision_complete(event->decision, &response, nullptr) == NEOASTRA_OK);
        break;
    }
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

// Evaluates the script until `done` takes what it gives, for at most a minute, and returns that.
std::string wait_for_script(neoastra_view_t* view, const std::string& script, bool (^done)(const std::string&)) {
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:60];
    for (;;) {
        const auto value = evaluate(view, script);
        if (done(value)) return value;
        if (deadline.timeIntervalSinceNow <= 0) {
            std::fprintf(stderr, "The script '%s' still gives %s\n", script.c_str(), value.c_str());
            assert(false);
        }
        pump();
    }
}

// Loads the page anew, since WebKit answers the later requests of a page from the first answer it got, lets the page ask
// for the devices of `constraints`, and checks what the host was asked and what the page got: the number of tracks it
// captured, or the name of its error.
void expect_capture(neoastra_view_t* view, view_events& events, const std::string& constraints,
                    const std::vector<uint64_t>& asked, const std::string& got) {
    events.loaded = false;
    events.requests.clear();
    const std::string page = "<!doctype html><title>Capture</title><p>Capture</p>";
    const std::string base = capture_origin + "/";
    assert(neoastra_view_load_html(view, string_view(page), string_view(base), nullptr) == NEOASTRA_OK);
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:60];
    while (!events.loaded) {
        assert(!events.failed && deadline.timeIntervalSinceNow > 0);
        pump();
    }
    wait_for_script(view, "document.visibilityState", ^bool(const std::string& value) { return value == "\"visible\""; });
    evaluate(view, "globalThis.outcome='pending';navigator.mediaDevices.getUserMedia(" + constraints + ").then("
        "stream=>{globalThis.outcome=String(stream.getTracks().length);stream.getTracks().forEach(track=>track.stop());},"
        "error=>{globalThis.outcome=error.name;});0");
    const auto outcome = wait_for_script(view, "globalThis.outcome", ^bool(const std::string& value) { return value != "\"pending\""; });
    bool as_expected = outcome == "\"" + got + "\"" && events.requests.size() == asked.size();
    for (size_t index = 0; as_expected && index < asked.size(); ++index)
        as_expected = events.requests[index].first == asked[index] && events.requests[index].second == capture_origin;
    if (!as_expected) {
        std::fprintf(stderr, "A page that asked for %s got %s instead of \"%s\", and the host was asked %zu times:", constraints.c_str(), outcome.c_str(), got.c_str(), events.requests.size());
        for (const auto& made : events.requests) std::fprintf(stderr, " kind %llu from '%s'", static_cast<unsigned long long>(made.first), made.second.c_str());
        std::fprintf(stderr, "\n");
        assert(false);
    }
}

} // namespace

// A page that asks for a microphone and a camera together makes one request, which WKWebView answers once. The host is
// asked about the microphone and about the camera, and the page captures when the host allows both. The window is shown:
// WebKit keeps the request of a page for the time when the page is visible.
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

        WKPreferences* preferences = web_view(view).configuration.preferences;
        if (![preferences respondsToSelector:@selector(_setMockCaptureDevicesEnabled:)] || ![preferences respondsToSelector:@selector(_setMediaDevicesEnabled:)]) {
            std::fprintf(stderr, "This WebKit has no devices for tests: the capture requests were not run\n");
        } else {
            [preferences _setMediaDevicesEnabled:YES];
            [preferences _setMockCaptureDevicesEnabled:YES];
            view_events events;
            assert(neoastra_view_set_event_callback(view, record_event, &events) == NEOASTRA_OK);
            assert(neoastra_window_show(window) == NEOASTRA_OK);

            const uint64_t microphone = NEOASTRA_PERMISSION_MICROPHONE, camera = NEOASTRA_PERMISSION_CAMERA;
            expect_capture(view, events, "{audio:true}", {microphone}, "1");
            expect_capture(view, events, "{video:true}", {camera}, "1");
            expect_capture(view, events, "{audio:true,video:true}", {microphone, camera}, "2");
            events.camera = NEOASTRA_DECISION_DENY;
            expect_capture(view, events, "{audio:true,video:true}", {microphone, camera}, "NotAllowedError");
            events.camera = NEOASTRA_DECISION_ALLOW;
            events.microphone = NEOASTRA_DECISION_DENY;
            expect_capture(view, events, "{audio:true,video:true}", {microphone, camera}, "NotAllowedError");
            assert(neoastra_view_set_event_callback(view, nullptr, nullptr) == NEOASTRA_OK);
        }

        neoastra_view_release(view);
        neoastra_environment_release(environment);
        neoastra_window_release(window);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    return 0;
}
