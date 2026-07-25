#include "../src/common/native_internal.hpp"

#include <array>
#include <atomic>
#include <cassert>
#include <chrono>
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
    decision.kind = NEO_WEBVIEW_DECISION_PERMISSION;
    decision.default_action = NEO_WEBVIEW_DECISION_DENY;
    assert(neo_webview_decision_get_kind(&decision) == NEO_WEBVIEW_DECISION_PERMISSION);
    assert(neo_webview_decision_defer(&decision) == NEO_WEBVIEW_OK);
    assert(neo_webview_decision_defer(&decision) == NEO_WEBVIEW_ERROR_INVALID_STATE);
    neo_webview_decision_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    response.action = NEO_WEBVIEW_DECISION_ALLOW;
    assert(neo_webview_decision_complete(&decision, &response, nullptr) == NEO_WEBVIEW_OK);
    assert(decision.resolved_action.load() == NEO_WEBVIEW_DECISION_ALLOW);
    assert(neo_webview_decision_complete(&decision, &response, nullptr) == NEO_WEBVIEW_ERROR_INVALID_STATE);
}

void test_decision_timeout() {
    neo_webview_decision decision;
    decision.deadline = std::chrono::steady_clock::now() - std::chrono::milliseconds(1);
    assert(neo_webview_decision_defer(&decision) == NEO_WEBVIEW_ERROR_TIMED_OUT);
    assert(decision.resolved_action.load() == NEO_WEBVIEW_DECISION_DENY);
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
    test_callback_quiescence();
    test_utf8();
    test_structure_versions();
    return 0;
}
