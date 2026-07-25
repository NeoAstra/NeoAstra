#include "../src/common/native_internal.hpp"

#include <array>
#include <atomic>
#include <cassert>
#include <chrono>
#include <string>
#include <thread>

namespace {

struct counted final : neo_ref_counted {
    explicit counted(std::atomic<int>& destroyed) : destroyed(destroyed) { }
    ~counted() override { ++destroyed; }
    std::atomic<int>& destroyed;
};

void NEO_WEBVIEW_CALL increment(void* context) {
    ++*static_cast<std::atomic<int>*>(context);
}

struct captured_decision {
    neo_webview_decision_action_t action{};
    uint32_t persist{};
    std::string text;
    std::string path;
};

void capture_decision(void* context, const neo_webview_decision_response_t* response) noexcept {
    auto* captured = static_cast<captured_decision*>(context);
    captured->action = response->action;
    captured->persist = response->persist;
    captured->text.assign(reinterpret_cast<const char*>(response->text.data), static_cast<size_t>(response->text.length));
    if (response->path_count) captured->path.assign(reinterpret_cast<const char*>(response->paths[0].data), static_cast<size_t>(response->paths[0].length));
}

void test_reference_counting() {
    std::atomic<int> destroyed{};
    auto* value = new counted(destroyed);
    assert(value->retain());
    assert(value->references.load() == 2);
    assert(value->release());
    assert(destroyed.load() == 0);
    assert(value->release());
    assert(destroyed.load() == 1);
}

void test_reference_counting_threads() {
    std::atomic<int> destroyed{};
    auto* value = new counted(destroyed);
    constexpr int thread_count = 8;
    constexpr int iterations = 10000;
    std::array<std::thread, thread_count> threads;
    for (auto& thread : threads) {
        thread = std::thread([value] {
            for (int index = 0; index < iterations; ++index) {
                assert(value->retain());
                assert(value->release());
            }
        });
    }
    for (auto& thread : threads) thread.join();
    value->release();
    assert(destroyed.load() == 1);
}

void test_operation_terminal_state() {
    neo_webview_operation operation;
    operation.cancel();
    neo_webview_result_t actual{};
    assert(operation.try_complete(NEO_WEBVIEW_OK, actual));
    assert(actual == NEO_WEBVIEW_ERROR_CANCELED);
    assert(!operation.try_complete(NEO_WEBVIEW_OK, actual));
}

void test_decision_state() {
    neo_webview_decision decision;
    captured_decision captured;
    decision.kind = NEO_WEBVIEW_DECISION_PERMISSION;
    decision.default_action = NEO_WEBVIEW_DECISION_DENY;
    decision.completion = capture_decision;
    decision.completion_context = &captured;
    assert(neo_webview_decision_get_kind(&decision) == NEO_WEBVIEW_DECISION_PERMISSION);
    assert(neo_webview_decision_defer(&decision) == NEO_WEBVIEW_OK);
    assert(neo_webview_decision_defer(&decision) == NEO_WEBVIEW_ERROR_INVALID_STATE);
    neo_webview_decision_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    response.action = NEO_WEBVIEW_DECISION_ALLOW;
    const std::string text = "selected";
    const std::string path = "C:/selected.txt";
    const neo_webview_string_view_t paths[]{neo_string_view(path)};
    response.text = neo_string_view(text);
    response.paths = paths;
    response.path_count = 1;
    response.persist = 1;
    assert(neo_webview_decision_complete(&decision, &response, nullptr) == NEO_WEBVIEW_OK);
    assert(decision.resolved_action.load() == NEO_WEBVIEW_DECISION_ALLOW);
    assert(captured.action == NEO_WEBVIEW_DECISION_ALLOW);
    assert(captured.persist == 1 && captured.text == text && captured.path == path);
    assert(neo_webview_decision_complete(&decision, &response, nullptr) == NEO_WEBVIEW_ERROR_INVALID_STATE);
}

void test_decision_timeout() {
    neo_webview_decision decision;
    decision.deadline = std::chrono::steady_clock::now() - std::chrono::milliseconds(1);
    assert(neo_webview_decision_defer(&decision) == NEO_WEBVIEW_ERROR_TIMED_OUT);
    assert(decision.resolved_action.load() == NEO_WEBVIEW_DECISION_DENY);
}

void test_deferred_decision_self_lifetime() {
    std::atomic<int> completed{};
    auto* decision = new neo_webview_decision;
    decision->kind = NEO_WEBVIEW_DECISION_PERMISSION;
    decision->default_action = NEO_WEBVIEW_DECISION_DENY;
    decision->completion = [](void* context, const neo_webview_decision_response_t*) noexcept { ++*static_cast<std::atomic<int>*>(context); };
    decision->completion_context = &completed;
    assert(neo_webview_decision_defer(decision) == NEO_WEBVIEW_OK);
    decision->release();

    neo_webview_decision_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    response.action = NEO_WEBVIEW_DECISION_ALLOW;
    assert(neo_webview_decision_complete(decision, &response, nullptr) == NEO_WEBVIEW_OK);
    assert(completed.load() == 1);
}

void test_callback_quiescence() {
    neo_callback_slot<neo_webview_dispatch_callback_t> slot;
    std::atomic<int> calls{};
    slot.set(increment, &calls);
    assert(slot.invoke([](auto callback, void* context) { callback(context); }));
    slot.clear();
    assert(!slot.invoke([](auto callback, void* context) { callback(context); }));
    assert(calls.load() == 1);
}

void test_utf8() {
    const uint8_t overlong[] = {0xc0, 0x80};
    neo_webview_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEO_WEBVIEW_APP_SHUTDOWN_EXPLICIT;
    options.application_name = {overlong, sizeof(overlong)};
    neo_webview_app_t* app{};
    neo_webview_error_t* error{};
    assert(neo_webview_app_create(&options, &app, &error) == NEO_WEBVIEW_ERROR_INVALID_ARGUMENT);
    assert(app == nullptr && error != nullptr);
    neo_webview_error_release(error);
}

void test_structure_versions() {
    neo_webview_runtime_info_t info{};
    neo_webview_error_t* error{};
    info.size = sizeof(info);
    info.version = 2;
    assert(neo_webview_get_runtime_info(&info, &error) == NEO_WEBVIEW_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neo_webview_error_release(error);
}

} // namespace

int main() {
    test_reference_counting();
    test_reference_counting_threads();
    test_operation_terminal_state();
    test_decision_state();
    test_decision_timeout();
    test_deferred_decision_self_lifetime();
    test_callback_quiescence();
    test_utf8();
    test_structure_versions();
    return 0;
}
