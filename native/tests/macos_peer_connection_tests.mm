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
    std::string text;
};

// What the view has reported: the navigations it finished, and whether one failed or its web process exited.
struct view_events {
    uint64_t completed{};
    bool failed{};
};

// Self-contained pages in standards mode, so the test needs neither the network nor a resource provider.
const std::string first_page = "data:text/html,%3C!doctype%20html%3Efirst";
const std::string second_page = "data:text/html,%3C!doctype%20html%3Esecond";

// Collects, in globalThis.report, the names of the peer connection interfaces that each document and worker of the page
// has: the page, an empty frame that a script adds (the initial about:blank document, which loads nothing), an empty
// frame inside that one, a document that a script writes, a sandboxed frame with a document of its own, a frame of
// another address, and a worker. A context without any reports an empty text.
const std::string probe = R"js((() => {
  const names = ["RTCPeerConnection", "webkitRTCPeerConnection", "RTCDataChannel", "RTCIceCandidate", "RTCSessionDescription",
    "RTCPeerConnectionIceEvent", "RTCDataChannelEvent", "RTCTrackEvent", "RTCRtpSender", "RTCRtpReceiver", "RTCRtpTransceiver",
    "RTCCertificate", "RTCDtlsTransport", "RTCIceTransport", "RTCSctpTransport", "RTCStatsReport"];
  const source = "target => " + JSON.stringify(names) + ".filter(name => typeof target[name] !== 'undefined').join(',')";
  const found = new Function("return " + source)();
  const report = globalThis.report = { count: 0 };
  const set = (key, value) => { if (!(key in report)) report.count++; report[key] = value; };
  set("page", found(globalThis));
  const blank = document.createElement("iframe");
  document.body.appendChild(blank);
  set("blank", found(blank.contentWindow));
  const nested = blank.contentDocument.createElement("iframe");
  blank.contentDocument.body.appendChild(nested);
  set("nested", found(nested.contentWindow));
  const written = document.createElement("iframe");
  document.body.appendChild(written);
  written.contentDocument.open();
  written.contentDocument.write("<!doctype html>written");
  written.contentDocument.close();
  set("written", found(written.contentWindow));
  addEventListener("message", event => set(event.data.key, event.data.value));
  const post = key => "<script>parent.postMessage({ key: '" + key + "', value: (" + source + ")(globalThis) }, '*')</" + "script>";
  const sandboxed = document.createElement("iframe");
  sandboxed.setAttribute("sandbox", "allow-scripts");
  sandboxed.srcdoc = post("sandboxed");
  document.body.appendChild(sandboxed);
  const other = document.createElement("iframe");
  other.src = "data:text/html," + encodeURIComponent(post("other"));
  document.body.appendChild(other);
  try {
    const worker = new Worker(URL.createObjectURL(new Blob(["postMessage((" + source + ")(self))"], { type: "text/javascript" })));
    worker.onmessage = event => set("worker", event.data);
    worker.onerror = () => set("worker", "");
  } catch (error) { set("worker", ""); }
  return true;
})())js";

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


// Runs the probe in the document that the view shows and returns what each of its seven contexts has.
struct probe_report {
    std::string page, blank, nested, written, sandboxed, other, worker;
};

probe_report run_probe(neoastra_view_t* view) {
    assert(evaluate(view, probe) == "true");
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:30];
    while (evaluate(view, "report.count") != "7") {
        if (deadline.timeIntervalSinceNow <= 0) {
            std::fprintf(stderr, "The probe did not finish: %s\n", evaluate(view, "JSON.stringify(report)").c_str());
            assert(false);
        }
        pump();
    }
    const auto read = [view](const char* key) {
        const auto text = evaluate(view, std::string("report.") + key);
        assert(text.size() >= 2 && text.front() == '"' && text.back() == '"');
        return text.substr(1, text.size() - 2);
    };
    probe_report report{read("page"), read("blank"), read("nested"), read("written"), read("sandboxed"), read("other"), read("worker")};
    return report;
}

bool has(const std::string& names, const char* name) { return ("," + names + ",").find(std::string(",") + name + ",") != std::string::npos; }

void fail(const probe_report& report, const char* expected) {
    std::fprintf(stderr, "Expected %s, but: page [%s] blank [%s] nested [%s] written [%s] sandboxed [%s] other [%s] worker [%s]\n", expected, report.page.c_str(),
        report.blank.c_str(), report.nested.c_str(), report.written.c_str(), report.sandboxed.c_str(), report.other.c_str(), report.worker.c_str());
    assert(false);
}

// No document and no worker of the page has any of the interfaces.
void expect_none(const probe_report& report) {
    for (const auto* names : {&report.page, &report.blank, &report.nested, &report.written, &report.sandboxed, &report.other, &report.worker}) {
        if (!names->empty()) fail(report, "no peer connection interface in any document or worker");
    }
}

// Every document of the page has peer connections.
void expect_all(const probe_report& report) {
    for (const auto* names : {&report.page, &report.blank, &report.nested, &report.written, &report.sandboxed, &report.other}) {
        if (!has(*names, "RTCPeerConnection") || !has(*names, "RTCDataChannel") || !has(*names, "RTCIceCandidate")) fail(report, "peer connections in every document");
    }
}

neoastra_view_t* create_view(neoastra_environment_t* environment, neoastra_window_t* window, view_events* events) {
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
    assert(neoastra_view_set_event_callback(view, record_event, events) == NEOASTRA_OK);
    return view;
}

neoastra_window_t* create_window(neoastra_app_t* app) {
    neoastra_window_options_t window_options{};
    window_options.size = sizeof(window_options);
    window_options.version = 1;
    window_options.bounds = {100, 100, 640, 480};
    window_options.flags = 3;
    neoastra_window_t* window = nullptr;
    assert(neoastra_app_create_window(app, &window_options, &window, nullptr) == NEOASTRA_OK && window != nullptr);
    return window;
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

        neoastra_runtime_info_t info{};
        info.size = sizeof(info);
        info.version = 1;
        assert(neoastra_get_runtime_info(&info, nullptr) == NEOASTRA_OK);
        assert((info.build_features & NEOASTRA_BUILD_FEATURE_PEER_CONNECTIONS) != 0);

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

        // A view that is not asked anything keeps the peer connections of WKWebView, and they work.
        auto* plain_window = create_window(app);
        view_events plain_events;
        auto* plain = create_view(environment, plain_window, &plain_events);
        navigate(plain, plain_events, first_page, "first");
        expect_all(run_probe(plain));
        assert(evaluate(plain, "(() => { const connection = new RTCPeerConnection(); const channel = connection.createDataChannel('probe'); const label = channel.label; connection.close(); return label; })()") == "\"probe\"");

        // A view that has them turned off before its first document gives them to no document and to no worker.
        auto* window = create_window(app);
        view_events events;
        auto* view = create_view(environment, window, &events);
        assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_PEER_CONNECTIONS, 0) == NEOASTRA_OK);
        navigate(view, events, first_page, "first");
        expect_none(run_probe(view));
        navigate(view, events, second_page, "second");
        expect_none(run_probe(view));
        // An empty document at the top of the view, and a reload, have none either.
        auto before = events.completed;
        assert(neoastra_view_navigate(view, string_view(std::string("about:blank")), nullptr) == NEOASTRA_OK);
        expect_page(view, events, before, "");
        expect_none(run_probe(view));
        navigate(view, events, first_page, "first");
        before = events.completed;
        assert(neoastra_view_reload(view, 0) == NEOASTRA_OK);
        expect_page(view, events, before, "first");
        expect_none(run_probe(view));

        // The setting is the one of its view: the other view of the environment still has its peer connections.
        navigate(plain, plain_events, second_page, "second");
        expect_all(run_probe(plain));

        // Turned on again, and off again: the documents that the view loads afterwards follow.
        assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_PEER_CONNECTIONS, 1) == NEOASTRA_OK);
        navigate(view, events, second_page, "second");
        expect_all(run_probe(view));
        assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_PEER_CONNECTIONS, 0) == NEOASTRA_OK);
        navigate(view, events, first_page, "first");
        expect_none(run_probe(view));

        neoastra_view_release(view);
        neoastra_window_release(window);
        neoastra_view_release(plain);
        neoastra_window_release(plain_window);
        neoastra_environment_release(environment);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    return 0;
}
