#include "neoastra.h"

#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>
#import <objc/runtime.h>

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

// A request that the view asked its host to decide: a navigation or a new window, where to, and the bits of its value.
struct request {
    neoastra_event_type_t type{};
    std::string uri;
    uint64_t value{};
};

// What became of an address that a decision sent outside the view.
struct outcome {
    std::string uri;
    uint64_t status{};
};

// What the view has reported: whether it finished loading its page, whether a navigation failed or its web process
// exited, the requests to leave the page, and what became of the addresses sent outside. `answer` is what the test
// decides for the next request.
struct view_events {
    bool loaded{};
    bool failed{};
    neoastra_decision_action_t answer{NEOASTRA_DECISION_CANCEL};
    std::vector<request> requests;
    std::vector<outcome> outcomes;
};

// The test takes the place of the system: no browser opens, and the test says whether the system takes an address.
NSMutableArray<NSString*>* opened_addresses;
BOOL system_opens = YES;
BOOL record_open(id, SEL, NSURL* url) {
    [opened_addresses addObject:url.absoluteString];
    return system_opens;
}

// Where the links of the page lead. The name cannot be resolved, and the test lets no request for it through.
const std::string elsewhere = "https://example.invalid/";

// A self-contained page, so the test needs neither the network nor a resource provider. Its elements are placed
// where the test clicks. The first link sits between two text fields, which the Tab key reaches whatever the
// setting is, and the second one asks for a new window.
const std::string page =
    "<body style='margin:0'>"
    "<input id=first style='position:absolute;left:10px;top:10px;width:120px;height:20px'>"
    "<a id=link href='https://example.invalid/link' style='position:absolute;left:10px;top:50px;width:200px;height:30px;display:block'>link</a>"
    "<input id=second style='position:absolute;left:10px;top:100px;width:120px;height:20px'>"
    "<a id=blank target=_blank href='https://example.invalid/blank' style='position:absolute;left:10px;top:150px;width:200px;height:30px;display:block'>blank</a>"
    "</body>";
constexpr int link_x = 60, link_y = 65, blank_y = 165;

constexpr uint64_t main_frame = NEOASTRA_NAVIGATION_REQUEST_MAIN_FRAME;
constexpr uint64_t reported = NEOASTRA_REQUEST_LINK_ACTIVATION_REPORTED;
constexpr uint64_t activated = NEOASTRA_REQUEST_LINK_ACTIVATED;

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
    std::string uri;
    if (event->uri.data) uri.assign(reinterpret_cast<const char*>(event->uri.data), static_cast<size_t>(event->uri.length));
    switch (event->header.type) {
    case NEOASTRA_EVENT_NAVIGATION_COMPLETED:
        events->loaded = true;
        break;
    case NEOASTRA_EVENT_NAVIGATION_REQUESTED:
    case NEOASTRA_EVENT_NEW_WINDOW_REQUESTED:
        // The page itself loads; a request that leaves it is recorded and answered, so the view never leaves the page.
        if (uri == "about:blank") break;
        events->requests.push_back({event->header.type, uri, event->value});
        {
            neoastra_decision_response_t response{};
            response.size = sizeof(response);
            response.version = 1;
            response.action = events->answer;
            assert(neoastra_decision_complete(event->decision, &response, nullptr) == NEOASTRA_OK);
        }
        break;
    case NEOASTRA_EVENT_EXTERNAL_OPEN_COMPLETED:
        events->outcomes.push_back({uri, event->value});
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

// The click goes to the view as a key press does. `x` and `y` are CSS pixels from the top-left corner of the page.
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

// Waits for the one request that the action just made, and for a moment more in case a second one follows it.
void expect_request(view_events& events, neoastra_event_type_t type, const std::string& uri, uint64_t value) {
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:30];
    while (events.requests.empty()) {
        if (deadline.timeIntervalSinceNow <= 0) {
            std::fprintf(stderr, "Expected a request for '%s', but the view made none\n", uri.c_str());
            assert(false);
        }
        pump();
    }
    NSDate* settled = [NSDate dateWithTimeIntervalSinceNow:0.3];
    while (settled.timeIntervalSinceNow > 0) pump();
    const auto& made = events.requests.front();
    if (events.requests.size() != 1 || made.type != type || made.uri != uri || made.value != value) {
        std::fprintf(stderr, "Expected one request of type %u for '%s' with value %llu, but the view made %zu, the first of type %u for '%s' with value %llu\n",
            static_cast<unsigned>(type), uri.c_str(), static_cast<unsigned long long>(value), events.requests.size(),
            static_cast<unsigned>(made.type), made.uri.c_str(), static_cast<unsigned long long>(made.value));
        assert(false);
    }
    events.requests.clear();
}

// Checks what the request just answered with OPEN_EXTERNAL led to: whether the system was asked to open `uri`, and what
// the view was told.
void expect_outcome(view_events& events, const std::string& uri, neoastra_external_open_status_t status) {
    const bool asked = status != NEOASTRA_EXTERNAL_OPEN_REFUSED;
    const bool as_expected = events.outcomes.size() == 1 && events.outcomes.front().uri == uri && events.outcomes.front().status == status &&
        opened_addresses.count == (asked ? 1u : 0u) && (!asked || std::string(opened_addresses[0].UTF8String) == uri);
    if (!as_expected) {
        std::fprintf(stderr, "Expected the status %u for '%s', but the view reported %zu outcomes, the first with the status %llu for '%s', and the system was asked %lu times\n",
            static_cast<unsigned>(status), uri.c_str(), events.outcomes.size(),
            events.outcomes.empty() ? 0ull : static_cast<unsigned long long>(events.outcomes.front().status),
            events.outcomes.empty() ? "" : events.outcomes.front().uri.c_str(), static_cast<unsigned long>(opened_addresses.count));
        assert(false);
    }
    events.outcomes.clear();
    [opened_addresses removeAllObjects];
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
        opened_addresses = [NSMutableArray array];
        method_setImplementation(class_getInstanceMethod([NSWorkspace class], @selector(openURL:)), reinterpret_cast<IMP>(record_open));

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
        assert(neoastra_view_load_html(view, string_view(page), string_view(std::string()), nullptr) == NEOASTRA_OK);
        NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:60];
        while (!events.loaded) {
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

        // A link that the user clicks, or activates with the Enter key, is an action of the user.
        const auto navigation = NEOASTRA_EVENT_NAVIGATION_REQUESTED;
        click(web_view(view), link_x, link_y);
        expect_request(events, navigation, elsewhere + "link", main_frame | reported | activated | NEOASTRA_NAVIGATION_REQUEST_USER_INITIATED);
        assert(evaluate(view, "document.getElementById('link').focus();document.activeElement.id") == "\"link\"");
        press_key(web_view(view), @"\r", 36);
        expect_request(events, navigation, elsewhere + "link", main_frame | reported | activated | NEOASTRA_NAVIGATION_REQUEST_USER_INITIATED);

        // A script that clicks the link activates it as well, and no user did. A script that navigates activates no link.
        evaluate(view, "document.getElementById('link').click();0");
        expect_request(events, navigation, elsewhere + "link", main_frame | reported | activated);
        evaluate(view, "location.href='https://example.invalid/script';0");
        expect_request(events, navigation, elsewhere + "script", main_frame | reported);

        // A link that opens a new window asks for the window only, with the same distinction.
        const auto new_window = NEOASTRA_EVENT_NEW_WINDOW_REQUESTED;
        click(web_view(view), link_x, blank_y);
        expect_request(events, new_window, elsewhere + "blank", reported | activated | NEOASTRA_NEW_WINDOW_REQUEST_USER_INITIATED);
        evaluate(view, "document.getElementById('blank').click();0");
        expect_request(events, new_window, elsewhere + "blank", reported | activated);
        evaluate(view, "window.open('https://example.invalid/open');0");
        expect_request(events, new_window, elsewhere + "open", reported);
        assert(events.outcomes.empty() && opened_addresses.count == 0);

        // An address sent outside the view goes to the system, and the view is told whether the system took it.
        events.answer = NEOASTRA_DECISION_OPEN_EXTERNAL;
        click(web_view(view), link_x, link_y);
        expect_request(events, navigation, elsewhere + "link", main_frame | reported | activated | NEOASTRA_NAVIGATION_REQUEST_USER_INITIATED);
        expect_outcome(events, elsewhere + "link", NEOASTRA_EXTERNAL_OPEN_OPENED);
        click(web_view(view), link_x, blank_y);
        expect_request(events, new_window, elsewhere + "blank", reported | activated | NEOASTRA_NEW_WINDOW_REQUEST_USER_INITIATED);
        expect_outcome(events, elsewhere + "blank", NEOASTRA_EXTERNAL_OPEN_OPENED);
        system_opens = NO;
        click(web_view(view), link_x, link_y);
        expect_request(events, navigation, elsewhere + "link", main_frame | reported | activated | NEOASTRA_NAVIGATION_REQUEST_USER_INITIATED);
        expect_outcome(events, elsewhere + "link", NEOASTRA_EXTERNAL_OPEN_FAILED);
        system_opens = YES;

        // What is not a web address stays inside, whatever the host decided, and the system is not asked.
        for (const std::string address : {"mailto:someone@example.invalid", "neoastra-test://host/path", "https://user:secret@example.invalid/"}) {
            evaluate(view, "location.href='" + address + "';0");
            expect_request(events, navigation, address, main_frame | reported);
            expect_outcome(events, address, NEOASTRA_EXTERNAL_OPEN_REFUSED);
        }
        evaluate(view, "window.open('mailto:someone@example.invalid');0");
        expect_request(events, new_window, "mailto:someone@example.invalid", reported);
        expect_outcome(events, "mailto:someone@example.invalid", NEOASTRA_EXTERNAL_OPEN_REFUSED);
        assert(evaluate(view, "document.getElementById('link').id") == "\"link\"");

        neoastra_view_release(view);
        neoastra_environment_release(environment);
        neoastra_window_release(window);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    return 0;
}
