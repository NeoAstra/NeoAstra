#include "neoastra.h"

#import <Cocoa/Cocoa.h>

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
};

// What the view has reported: its latest history flags, the page of the navigation it finished last,
// and whether a navigation failed or its web process exited.
struct view_events {
    uint64_t history{};
    std::string page;
    bool failed{};
};

// A history-changed event carries one bit for each direction that has an entry to go to.
constexpr uint64_t can_go_back = 1;
constexpr uint64_t can_go_forward = 2;

// Self-contained pages, so the test needs neither the network nor a resource provider.
const std::string first_page = "data:text/html,first";
const std::string second_page = "data:text/html,second";
const std::string third_page = "data:text/html,third";

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

void NEOASTRA_CALL record_event(void* context, const neoastra_event_t* event) {
    auto* events = static_cast<view_events*>(context);
    switch (event->header.type) {
    case NEOASTRA_EVENT_HISTORY_CHANGED:
        events->history = event->value;
        break;
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

void navigate(neoastra_view_t* view, view_events& events, const std::string& page) {
    events.page.clear();
    assert(neoastra_view_navigate(view, string_view(page), nullptr) == NEOASTRA_OK);
}

void go_back(neoastra_view_t* view, view_events& events) {
    events.page.clear();
    assert(neoastra_view_go_back(view) == NEOASTRA_OK);
}

void go_forward(neoastra_view_t* view, view_events& events) {
    events.page.clear();
    assert(neoastra_view_go_forward(view) == NEOASTRA_OK);
}

// Waits until the navigation just started has finished on `page` and the view reports `history`.
void expect_page(const view_events& events, const std::string& page, uint64_t history) {
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:60];
    while (events.page != page || events.history != history) {
        if (events.failed || deadline.timeIntervalSinceNow <= 0) {
            std::fprintf(stderr, "Expected page '%s' with history flags %llu, but the view finished on '%s' and reports %llu%s\n",
                page.c_str(), static_cast<unsigned long long>(history), events.page.c_str(), static_cast<unsigned long long>(events.history),
                events.failed ? " after a navigation failed or its web process exited" : "");
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

        // The first page has nowhere to go, and a second one adds an entry behind it.
        navigate(view, events, first_page);
        expect_page(events, first_page, 0);
        navigate(view, events, second_page);
        expect_page(events, second_page, can_go_back);

        // Moving through the history swaps the two directions.
        go_back(view, events);
        expect_page(events, first_page, can_go_forward);
        go_forward(view, events);
        expect_page(events, second_page, can_go_back);

        // A navigation from the middle of the history drops the entries ahead of it.
        go_back(view, events);
        expect_page(events, first_page, can_go_forward);
        navigate(view, events, third_page);
        expect_page(events, third_page, can_go_back);

        neoastra_view_release(view);
        neoastra_environment_release(environment);
        neoastra_window_release(window);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    return 0;
}
