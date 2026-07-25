#include "../src/common/native_internal.hpp"

#include <array>
#include <atomic>
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <chrono>
#include <string>
#include <thread>
#include <type_traits>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

namespace {

struct counted final : neo_ref_counted {
    explicit counted(std::atomic<int>& destroyed) : destroyed(destroyed) { }
    ~counted() override { ++destroyed; }
    std::atomic<int>& destroyed;
};

struct ui_counted final : neo_ui_ref_counted {
    ui_counted(neo_webview_app_t* app, std::atomic<int>& teardown_count, std::atomic<int>& destructor_count,
               std::atomic<bool>& teardown_on_ui, std::atomic<bool>& destructor_on_ui,
               neo_ui_ref_counted* release_during_teardown = nullptr)
        : neo_ui_ref_counted(app), teardown_count(teardown_count), destructor_count(destructor_count),
          teardown_on_ui(teardown_on_ui), destructor_on_ui(destructor_on_ui),
          release_during_teardown(release_during_teardown) { }

    ~ui_counted() override {
        destroy_ui_once();
        ++destructor_count;
        destructor_on_ui.store(destruction_app->ui_thread == std::this_thread::get_id());
    }

    void destroy_ui() noexcept override {
        ++teardown_count;
        teardown_on_ui.store(destruction_app->ui_thread == std::this_thread::get_id());
        if (release_during_teardown) {
            std::thread worker([value = release_during_teardown] { assert(value->release()); });
            worker.join();
            release_during_teardown = nullptr;
        }
    }

    std::atomic<int>& teardown_count;
    std::atomic<int>& destructor_count;
    std::atomic<bool>& teardown_on_ui;
    std::atomic<bool>& destructor_on_ui;
    neo_ui_ref_counted* release_during_teardown;
};

static_assert(std::is_base_of_v<neo_ui_ref_counted, neo_webview_environment_t>);
static_assert(std::is_base_of_v<neo_ui_ref_counted, neo_webview_profile_t>);
static_assert(std::is_base_of_v<neo_ui_ref_counted, neo_webview_window_t>);
static_assert(std::is_base_of_v<neo_ui_ref_counted, neo_webview_view_t>);

void NEO_WEBVIEW_CALL increment(void* context) {
    ++*static_cast<std::atomic<int>*>(context);
}

void NEO_WEBVIEW_CALL ignore_view(void*, neo_webview_result_t, neo_webview_view_t*, const neo_webview_error_t*) { }

void NEO_WEBVIEW_CALL quit_app(void* context) {
    neo_webview_app_quit(static_cast<neo_webview_app_t*>(context), 0);
}

neo_webview_app_t* create_test_app(bool embedded = false) {
    neo_webview_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEO_WEBVIEW_APP_SHUTDOWN_EXPLICIT;
    const std::string name = "NeoWebView lifetime tests";
    options.application_name = neo_string_view(name);
    neo_webview_app_t* app{};
    neo_webview_error_t* error{};
    const auto result = embedded ? neo_webview_app_attach(&options, &app, &error) : neo_webview_app_create(&options, &app, &error);
    assert(result == NEO_WEBVIEW_OK);
    assert(app != nullptr && error == nullptr);
    return app;
}

void NEO_WEBVIEW_CALL release_app_from_worker_and_quit(void* context) {
    auto* app = static_cast<neo_webview_app_t*>(context);
    std::thread worker([app] { neo_webview_app_release(app); });
    worker.join();
    neo_webview_app_quit(app, 0);
}

struct captured_decision {
    neo_webview_decision_action_t action{};
    uint32_t persist{};
    std::string text;
    std::string path;
};

struct captured_logs {
    std::atomic<int> information{};
    std::atomic<int> warnings{};
    std::atomic<bool> metadata_valid{true};
};

void NEO_WEBVIEW_CALL capture_log(void* context, neo_webview_log_level_t level,
                                  neo_webview_string_view_t category, neo_webview_string_view_t message,
                                  uint64_t thread_id, uint64_t timestamp_ns, int64_t native_code, uint64_t object_id) {
    auto* captured = static_cast<captured_logs*>(context);
    const auto valid = category.data != nullptr && category.length != 0 && message.data != nullptr &&
        message.length != 0 && thread_id != 0 && timestamp_ns != 0 && native_code == 0 && object_id == 0;
    if (!valid) captured->metadata_valid.store(false);
    if (level == NEO_WEBVIEW_LOG_INFORMATION) ++captured->information;
    else if (level == NEO_WEBVIEW_LOG_WARNING) ++captured->warnings;
    else captured->metadata_valid.store(false);
}

void NEO_WEBVIEW_CALL throw_from_log(void*, neo_webview_log_level_t, neo_webview_string_view_t,
                                     neo_webview_string_view_t, uint64_t, uint64_t, int64_t, uint64_t) {
    throw std::runtime_error("logging callback failed");
}

struct releasing_log_context {
    neo_webview_app_t* app{};
    std::atomic<int> calls{};
};

void NEO_WEBVIEW_CALL release_app_from_shutdown_log(void* context, neo_webview_log_level_t,
                                                    neo_webview_string_view_t, neo_webview_string_view_t,
                                                    uint64_t, uint64_t, int64_t, uint64_t) {
    auto* value = static_cast<releasing_log_context*>(context);
    if (++value->calls == 2) neo_webview_app_release(value->app);
}

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

void test_ui_destruction_from_worker() {
    auto* app = create_test_app();
    std::atomic<int> teardown_count{};
    std::atomic<int> destructor_count{};
    std::atomic<bool> teardown_on_ui{};
    std::atomic<bool> destructor_on_ui{};
    auto* value = new ui_counted(app, teardown_count, destructor_count, teardown_on_ui, destructor_on_ui);

    std::thread worker([value] { assert(value->release()); });
    worker.join();
    assert(teardown_count.load() == 0);
    assert(destructor_count.load() == 0);

    assert(neo_webview_app_dispatch(app, quit_app, app) == NEO_WEBVIEW_OK);
    assert(neo_webview_app_run(app) == 0);
    assert(teardown_count.load() == 1);
    assert(destructor_count.load() == 1);
    assert(teardown_on_ui.load());
    assert(destructor_on_ui.load());
    neo_webview_app_release(app);
}

void test_ui_destruction_after_shutdown() {
    auto* app = create_test_app();
    std::atomic<int> teardown_count{};
    std::atomic<int> destructor_count{};
    std::atomic<bool> teardown_on_ui{};
    std::atomic<bool> destructor_on_ui{true};
    auto* value = new ui_counted(app, teardown_count, destructor_count, teardown_on_ui, destructor_on_ui);
    assert(neo_webview_app_dispatch(app, quit_app, app) == NEO_WEBVIEW_OK);
    assert(neo_webview_app_run(app) == 0);
    assert(teardown_count.load() == 1);
    assert(destructor_count.load() == 0);

    std::thread worker([value] { assert(value->release()); });
    worker.join();
    assert(teardown_count.load() == 1);
    assert(destructor_count.load() == 1);
    assert(teardown_on_ui.load());
    assert(!destructor_on_ui.load());
    neo_webview_app_release(app);
}

void test_worker_release_while_shutdown_is_draining() {
    auto* app = create_test_app();
    std::atomic<int> target_teardown_count{};
    std::atomic<int> target_destructor_count{};
    std::atomic<bool> target_teardown_on_ui{};
    std::atomic<bool> target_destructor_on_ui{};
    auto* target = new ui_counted(app, target_teardown_count, target_destructor_count,
                                  target_teardown_on_ui, target_destructor_on_ui);
    std::atomic<int> trigger_teardown_count{};
    std::atomic<int> trigger_destructor_count{};
    std::atomic<bool> trigger_teardown_on_ui{};
    std::atomic<bool> trigger_destructor_on_ui{};
    auto* trigger = new ui_counted(app, trigger_teardown_count, trigger_destructor_count,
                                   trigger_teardown_on_ui, trigger_destructor_on_ui, target);

    assert(neo_webview_app_dispatch(app, quit_app, app) == NEO_WEBVIEW_OK);
    assert(neo_webview_app_run(app) == 0);
    assert(target_teardown_count.load() == 1);
    assert(target_destructor_count.load() == 1);
    assert(target_teardown_on_ui.load());
    assert(target_destructor_on_ui.load());
    assert(trigger_teardown_count.load() == 1);
    assert(trigger_teardown_on_ui.load());

    assert(trigger->release());
    assert(trigger_destructor_count.load() == 1);
    assert(trigger_destructor_on_ui.load());
    neo_webview_app_release(app);
}

void test_explicit_detach() {
    auto* app = create_test_app(true);
    std::atomic<int> calls{};
    assert(neo_webview_app_dispatch(app, increment, &calls) == NEO_WEBVIEW_OK);
    assert(neo_webview_app_detach(app, nullptr) == NEO_WEBVIEW_OK);
    assert(calls.load() == 1);
    assert(app->state.load(std::memory_order_acquire) == neo_app_state::stopped);
    assert(app->platform == nullptr);
    assert(neo_webview_app_dispatch(app, increment, &calls) == NEO_WEBVIEW_ERROR_DISPOSED);
    assert(neo_webview_app_detach(app, nullptr) == NEO_WEBVIEW_OK);
    std::thread worker([app] { neo_webview_app_release(app); });
    worker.join();
}

void test_detach_requires_ui_thread() {
    auto* app = create_test_app(true);
    std::atomic<neo_webview_result_t> result{NEO_WEBVIEW_OK};
    std::thread worker([&] { result.store(neo_webview_app_detach(app, nullptr)); });
    worker.join();
    assert(result.load() == NEO_WEBVIEW_ERROR_WRONG_THREAD);
    assert(neo_webview_app_detach(app, nullptr) == NEO_WEBVIEW_OK);
    neo_webview_app_release(app);
}

void test_worker_release_during_run() {
    auto* app = create_test_app();
    assert(neo_webview_app_dispatch(app, release_app_from_worker_and_quit, app) == NEO_WEBVIEW_OK);
    assert(neo_webview_app_run(app) == 0);
}

#if defined(_WIN32)
void test_attached_worker_final_release_is_marshaled() {
    auto* app = create_test_app(true);
    std::thread worker([app] { neo_webview_app_release(app); });
    worker.join();

    MSG message{};
    bool dispatched{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
        dispatched = true;
    }
    assert(dispatched);
}
#endif

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

    neo_webview_decision explicit_default;
    captured_decision default_capture;
    explicit_default.kind = NEO_WEBVIEW_DECISION_DOWNLOAD_REQUEST;
    explicit_default.default_action = NEO_WEBVIEW_DECISION_CANCEL;
    explicit_default.completion = capture_decision;
    explicit_default.completion_context = &default_capture;
    response.action = NEO_WEBVIEW_DECISION_DEFAULT;
    response.text = {};
    response.paths = nullptr;
    response.path_count = 0;
    response.persist = 0;
    assert(neo_webview_decision_complete(&explicit_default, &response, nullptr) == NEO_WEBVIEW_OK);
    assert(explicit_default.resolved_action.load() == NEO_WEBVIEW_DECISION_DEFAULT);
    assert(default_capture.action == NEO_WEBVIEW_DECISION_DEFAULT);
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

    std::atomic<bool> worker_entered{};
    std::atomic<bool> release_worker{};
    slot.set(increment, &calls);
    std::thread worker([&] {
        assert(slot.invoke([&](auto, void*) {
            worker_entered.store(true, std::memory_order_release);
            while (!release_worker.load(std::memory_order_acquire)) std::this_thread::yield();
        }));
    });
    while (!worker_entered.load(std::memory_order_acquire)) std::this_thread::yield();
    std::thread releaser([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        release_worker.store(true, std::memory_order_release);
    });
    assert(slot.invoke([&](auto, void*) { slot.clear(); }));
    worker.join();
    releaser.join();
    assert(!slot.invoke([](auto callback, void* context) { callback(context); }));
}

void test_logging_thread_safety_and_exception_containment() {
    captured_logs captured;
    neo_webview_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEO_WEBVIEW_APP_SHUTDOWN_EXPLICIT;
    options.maximum_pending_dispatches = 1;
    options.log_callback = capture_log;
    options.log_context = &captured;
    neo_webview_app_t* app{};
    assert(neo_webview_app_attach(&options, &app, nullptr) == NEO_WEBVIEW_OK);
    assert(captured.information.load() == 1);
    std::atomic<int> dispatch_calls{};
    assert(neo_webview_app_dispatch(app, increment, &dispatch_calls) == NEO_WEBVIEW_OK);

    constexpr int thread_count = 8;
    constexpr int messages_per_thread = 100;
    std::array<std::thread, thread_count> threads;
    for (auto& thread : threads) {
        thread = std::thread([app, &dispatch_calls] {
            for (int index = 0; index < messages_per_thread; ++index) {
                assert(neo_webview_app_dispatch(app, increment, &dispatch_calls) == NEO_WEBVIEW_ERROR_INVALID_STATE);
            }
        });
    }
    for (auto& thread : threads) thread.join();

    assert(captured.warnings.load() == thread_count * messages_per_thread);
    assert(captured.metadata_valid.load());
    assert(neo_webview_app_detach(app, nullptr) == NEO_WEBVIEW_OK);
    assert(dispatch_calls.load() == 1);
    assert(captured.information.load() == 2);
    neo_webview_app_release(app);

    options.log_callback = throw_from_log;
    options.log_context = nullptr;
    app = nullptr;
    assert(neo_webview_app_attach(&options, &app, nullptr) == NEO_WEBVIEW_OK);
    assert(neo_webview_app_detach(app, nullptr) == NEO_WEBVIEW_OK);
    neo_webview_app_release(app);

    releasing_log_context releasing;
    options.log_callback = release_app_from_shutdown_log;
    options.log_context = &releasing;
    app = nullptr;
    assert(neo_webview_app_attach(&options, &app, nullptr) == NEO_WEBVIEW_OK);
    releasing.app = app;
    assert(neo_webview_app_detach(app, nullptr) == NEO_WEBVIEW_OK);
    assert(releasing.calls.load() == 2);
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
    neo_webview_error_t* error{};

    neo_webview_runtime_info_t undersized{};
    undersized.size = sizeof(undersized) - 1;
    undersized.version = 1;
    assert(neo_webview_get_runtime_info(&undersized, &error) == NEO_WEBVIEW_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neo_webview_error_release(error);

    neo_webview_runtime_info_t unsupported{};
    unsupported.size = sizeof(unsupported);
    unsupported.version = 2;
    error = nullptr;
    assert(neo_webview_get_runtime_info(&unsupported, &error) == NEO_WEBVIEW_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neo_webview_error_release(error);

    struct extended_runtime_info {
        neo_webview_runtime_info_t value{};
        std::array<uint8_t, 32> trailing{};
    } extended;
    extended.trailing.fill(0xa5);
    extended.value.size = sizeof(extended);
    extended.value.version = 1;
    error = nullptr;
    assert(neo_webview_get_runtime_info(&extended.value, &error) == NEO_WEBVIEW_OK);
    assert(error == nullptr);
    assert(extended.value.backend_name.length != 0);
    for (const auto byte : extended.trailing) assert(byte == 0xa5);
}

void test_native_parent_structure() {
    auto* environment = reinterpret_cast<neo_webview_environment_t*>(1);
    neo_webview_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.parent.kind = NEO_WEBVIEW_NATIVE_PARENT_WIN32_HWND;
    options.parent.handle = reinterpret_cast<void*>(1);

    neo_webview_error_t* error{};
    options.parent.size = sizeof(options.parent) - 1;
    options.parent.version = 1;
    assert(neo_webview_environment_create_view_async(environment, &options, ignore_view, nullptr, nullptr, &error) == NEO_WEBVIEW_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neo_webview_error_release(error);

    error = nullptr;
    options.parent.size = sizeof(options.parent);
    options.parent.version = 2;
    assert(neo_webview_environment_create_view_async(environment, &options, ignore_view, nullptr, nullptr, &error) == NEO_WEBVIEW_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neo_webview_error_release(error);
}

} // namespace

int main() {
    test_reference_counting();
    test_reference_counting_threads();
    test_ui_destruction_from_worker();
    test_ui_destruction_after_shutdown();
    test_worker_release_while_shutdown_is_draining();
    test_explicit_detach();
    test_detach_requires_ui_thread();
    test_worker_release_during_run();
#if defined(_WIN32)
    test_attached_worker_final_release_is_marshaled();
#endif
    test_operation_terminal_state();
    test_decision_state();
    test_decision_timeout();
    test_deferred_decision_self_lifetime();
    test_callback_quiescence();
    test_logging_thread_safety_and_exception_containment();
    test_utf8();
    test_structure_versions();
    test_native_parent_structure();
    return 0;
}
