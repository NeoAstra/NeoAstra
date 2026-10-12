#include "neoastra.h"

#import <Cocoa/Cocoa.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cmath>
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

// What the view has reported: the navigations it finished, and whether one failed or its web process exited.
struct view_events {
    uint64_t completed{};
    bool failed{};
};

// What a page says of its size: the layout viewport and the window in CSS pixels, and the scale of the visual viewport.
struct page_size {
    int width{};
    int height{};
    int inner_width{};
    int inner_height{};
    double visual_scale{};
};

// Self-contained pages in standards mode, so the test needs neither the network nor a resource provider.
const std::string first_page = "data:text/html,%3C!doctype%20html%3Efirst";
const std::string second_page = "data:text/html,%3C!doctype%20html%3Esecond";

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
        events->completed++;
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

// Waits until the view has finished one more navigation than `before`, on a page with the text `text`.
void expect_page(neoastra_view_t* view, const view_events& events, uint64_t before, const std::string& text) {
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:60];
    while (events.completed == before) {
        if (events.failed || deadline.timeIntervalSinceNow <= 0) {
            std::fprintf(stderr, "Expected the page '%s', but its navigation %s\n", text.c_str(), events.failed ? "failed or its web process exited" : "did not finish");
            assert(false);
        }
        pump();
    }
    assert(evaluate(view, "document.body.textContent") == "\"" + text + "\"");
}

void navigate(neoastra_view_t* view, const view_events& events, const std::string& page, const std::string& text) {
    const auto before = events.completed;
    assert(neoastra_view_navigate(view, string_view(page), nullptr) == NEOASTRA_OK);
    expect_page(view, events, before, text);
}

double zoom_factor(neoastra_view_t* view) {
    double factor{};
    assert(neoastra_view_get_zoom_factor(view, &factor) == NEOASTRA_OK);
    return factor;
}

page_size read_size(neoastra_view_t* view) {
    const auto text = evaluate(view, "[document.documentElement.clientWidth,document.documentElement.clientHeight,innerWidth,innerHeight,visualViewport.scale].join(' ')");
    page_size size;
    assert(std::sscanf(text.c_str(), "\"%d %d %d %d %lf\"", &size.width, &size.height, &size.inner_width, &size.inner_height, &size.visual_scale) == 5);
    return size;
}

// A zoomed page is laid out in a viewport of the size of the view divided by the zoom, and it is not magnified: it
// fills the view. The page learns its size a moment after the zoom was set, so the size is read until it is that one.
page_size expect_size(neoastra_view_t* view, double width, double height, const char* when) {
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:10];
    for (;;) {
        const auto size = read_size(view);
        if (std::fabs(size.width - width) <= 1 && std::fabs(size.height - height) <= 1 && std::fabs(size.inner_width - width) <= 1
            && std::fabs(size.inner_height - height) <= 1 && std::fabs(size.visual_scale - 1) < 0.001) return size;
        if (deadline.timeIntervalSinceNow <= 0) {
            std::fprintf(stderr, "Expected a page laid out in %.0f by %.0f CSS pixels that fills its view %s, but its layout viewport is %d by %d, its window %d by %d, and its visual viewport is scaled by %g\n",
                width, height, when, size.width, size.height, size.inner_width, size.inner_height, size.visual_scale);
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

        // A view that is zoomed before its first document shows that document zoomed.
        assert(zoom_factor(view) == 1);
        assert(neoastra_view_set_zoom_factor(view, 0.5) == NEOASTRA_OK);
        assert(zoom_factor(view) == 0.5);
        navigate(view, events, first_page, "first");
        assert(zoom_factor(view) == 0.5);
        const auto zoomed_out = read_size(view);

        // At 100 percent the page has the size of the view, of which a page at 50 percent has twice as much in each direction.
        assert(neoastra_view_set_zoom_factor(view, 1) == NEOASTRA_OK);
        assert(zoom_factor(view) == 1);
        const auto full = expect_size(view, zoomed_out.width / 2.0, zoomed_out.height / 2.0, "at 100 percent");
        assert(full.width > 0 && full.height > 0);
        assert(neoastra_view_set_zoom_factor(view, 0.5) == NEOASTRA_OK);
        expect_size(view, full.width * 2.0, full.height * 2.0, "at 50 percent");
        assert(neoastra_view_set_zoom_factor(view, 2) == NEOASTRA_OK);
        assert(zoom_factor(view) == 2);
        expect_size(view, full.width / 2.0, full.height / 2.0, "at 200 percent");

        // The zoom belongs to the view: a new document and a reload keep it.
        navigate(view, events, second_page, "second");
        assert(zoom_factor(view) == 2);
        expect_size(view, full.width / 2.0, full.height / 2.0, "at 200 percent in a new document");
        const auto before = events.completed;
        assert(neoastra_view_reload(view, 0) == NEOASTRA_OK);
        expect_page(view, events, before, "second");
        assert(zoom_factor(view) == 2);
        expect_size(view, full.width / 2.0, full.height / 2.0, "at 200 percent after a reload");

        assert(neoastra_view_set_zoom_factor(view, 1) == NEOASTRA_OK);
        expect_size(view, full.width, full.height, "back at 100 percent");

        neoastra_view_release(view);
        neoastra_environment_release(environment);
        neoastra_window_release(window);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    return 0;
}
