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
    ui_counted(neoastra_app_t* app, std::atomic<int>& teardown_count, std::atomic<int>& destructor_count,
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

static_assert(std::is_base_of_v<neo_ui_ref_counted, neoastra_environment_t>);
static_assert(std::is_base_of_v<neo_ui_ref_counted, neoastra_profile_t>);
static_assert(std::is_base_of_v<neo_ui_ref_counted, neoastra_window_t>);
static_assert(std::is_base_of_v<neo_ui_ref_counted, neoastra_view_t>);

void NEOASTRA_CALL increment(void* context) {
    ++*static_cast<std::atomic<int>*>(context);
}

void NEOASTRA_CALL ignore_view(void*, neoastra_result_t, neoastra_view_t*, const neoastra_error_t*) { }
void NEOASTRA_CALL ignore_environment(void*, neoastra_result_t, neoastra_environment_t*, const neoastra_error_t*) { }

neoastra_result_t NEOASTRA_CALL empty_resource(void*, const neoastra_resource_request_t*, neoastra_resource_response_t*) {
    return NEOASTRA_OK;
}

void NEOASTRA_CALL release_resource_context(void* context) {
    ++*static_cast<std::atomic<int>*>(context);
}

void NEOASTRA_CALL throw_releasing_resource_context(void* context) {
    ++*static_cast<std::atomic<int>*>(context);
    throw std::runtime_error("resource release failed");
}

void NEOASTRA_CALL quit_app(void* context) {
    neoastra_app_quit(static_cast<neoastra_app_t*>(context), 0);
}

neoastra_app_t* create_test_app(bool embedded = false) {
    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    const std::string name = "NeoAstra lifetime tests";
    options.application_name = neo_string_view(name);
    neoastra_app_t* app{};
    neoastra_error_t* error{};
    const auto result = embedded ? neoastra_app_attach(&options, &app, &error) : neoastra_app_create(&options, &app, &error);
    assert(result == NEOASTRA_OK);
    assert(app != nullptr && error == nullptr);
    return app;
}

void NEOASTRA_CALL release_app_from_worker_and_quit(void* context) {
    auto* app = static_cast<neoastra_app_t*>(context);
    std::thread worker([app] { neoastra_app_release(app); });
    worker.join();
    neoastra_app_quit(app, 0);
}

struct captured_decision {
    neoastra_decision_action_t action{};
    uint32_t persist{};
    std::string text;
    std::string path;
};

struct captured_logs {
    std::atomic<int> information{};
    std::atomic<int> warnings{};
    std::atomic<bool> metadata_valid{true};
};

void NEOASTRA_CALL capture_log(void* context, neoastra_log_level_t level,
                                  neoastra_string_view_t category, neoastra_string_view_t message,
                                  uint64_t thread_id, uint64_t timestamp_ns, int64_t native_code, uint64_t object_id) {
    auto* captured = static_cast<captured_logs*>(context);
    const auto valid = category.data != nullptr && category.length != 0 && message.data != nullptr &&
        message.length != 0 && thread_id != 0 && timestamp_ns != 0 && native_code == 0 && object_id == 0;
    if (!valid) captured->metadata_valid.store(false);
    if (level == NEOASTRA_LOG_INFORMATION) ++captured->information;
    else if (level == NEOASTRA_LOG_WARNING) ++captured->warnings;
    else captured->metadata_valid.store(false);
}

void NEOASTRA_CALL throw_from_log(void*, neoastra_log_level_t, neoastra_string_view_t,
                                     neoastra_string_view_t, uint64_t, uint64_t, int64_t, uint64_t) {
    throw std::runtime_error("logging callback failed");
}

struct releasing_log_context {
    neoastra_app_t* app{};
    std::atomic<int> calls{};
};

void NEOASTRA_CALL release_app_from_shutdown_log(void* context, neoastra_log_level_t,
                                                    neoastra_string_view_t, neoastra_string_view_t,
                                                    uint64_t, uint64_t, int64_t, uint64_t) {
    auto* value = static_cast<releasing_log_context*>(context);
    if (++value->calls == 2) neoastra_app_release(value->app);
}

void capture_decision(void* context, const neoastra_decision_response_t* response) noexcept {
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

    assert(neoastra_app_dispatch(app, quit_app, app) == NEOASTRA_OK);
    assert(neoastra_app_run(app) == 0);
    assert(teardown_count.load() == 1);
    assert(destructor_count.load() == 1);
    assert(teardown_on_ui.load());
    assert(destructor_on_ui.load());
    neoastra_app_release(app);
}

void test_ui_destruction_after_shutdown() {
    auto* app = create_test_app();
    std::atomic<int> teardown_count{};
    std::atomic<int> destructor_count{};
    std::atomic<bool> teardown_on_ui{};
    std::atomic<bool> destructor_on_ui{true};
    auto* value = new ui_counted(app, teardown_count, destructor_count, teardown_on_ui, destructor_on_ui);
    assert(neoastra_app_dispatch(app, quit_app, app) == NEOASTRA_OK);
    assert(neoastra_app_run(app) == 0);
    assert(teardown_count.load() == 1);
    assert(destructor_count.load() == 0);

    std::thread worker([value] { assert(value->release()); });
    worker.join();
    assert(teardown_count.load() == 1);
    assert(destructor_count.load() == 1);
    assert(teardown_on_ui.load());
    assert(!destructor_on_ui.load());
    neoastra_app_release(app);
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

    assert(neoastra_app_dispatch(app, quit_app, app) == NEOASTRA_OK);
    assert(neoastra_app_run(app) == 0);
    assert(target_teardown_count.load() == 1);
    assert(target_destructor_count.load() == 1);
    assert(target_teardown_on_ui.load());
    assert(target_destructor_on_ui.load());
    assert(trigger_teardown_count.load() == 1);
    assert(trigger_teardown_on_ui.load());

    assert(trigger->release());
    assert(trigger_destructor_count.load() == 1);
    assert(trigger_destructor_on_ui.load());
    neoastra_app_release(app);
}

void test_explicit_detach() {
    auto* app = create_test_app(true);
    std::atomic<int> calls{};
    assert(neoastra_app_dispatch(app, increment, &calls) == NEOASTRA_OK);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    assert(calls.load() == 1);
    assert(app->state.load(std::memory_order_acquire) == neo_app_state::stopped);
    assert(app->platform == nullptr);
    assert(neoastra_app_dispatch(app, increment, &calls) == NEOASTRA_ERROR_DISPOSED);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    std::thread worker([app] { neoastra_app_release(app); });
    worker.join();
}

void test_detach_requires_ui_thread() {
    auto* app = create_test_app(true);
    std::atomic<neoastra_result_t> result{NEOASTRA_OK};
    std::thread worker([&] { result.store(neoastra_app_detach(app, nullptr)); });
    worker.join();
    assert(result.load() == NEOASTRA_ERROR_WRONG_THREAD);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

void test_worker_release_during_run() {
    auto* app = create_test_app();
    assert(neoastra_app_dispatch(app, release_app_from_worker_and_quit, app) == NEOASTRA_OK);
    assert(neoastra_app_run(app) == 0);
}

#if defined(_WIN32)
void test_attached_worker_final_release_is_marshaled() {
    auto* app = create_test_app(true);
    std::thread worker([app] { neoastra_app_release(app); });
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
    neoastra_operation operation;
    operation.cancel();
    neoastra_result_t actual{};
    assert(operation.try_complete(NEOASTRA_OK, actual));
    assert(actual == NEOASTRA_ERROR_CANCELED);
    assert(!operation.try_complete(NEOASTRA_OK, actual));
}

void test_decision_state() {
    neoastra_decision decision;
    captured_decision captured;
    decision.kind = NEOASTRA_DECISION_PERMISSION;
    decision.default_action = NEOASTRA_DECISION_DENY;
    decision.completion = capture_decision;
    decision.completion_context = &captured;
    assert(neoastra_decision_get_kind(&decision) == NEOASTRA_DECISION_PERMISSION);
    assert(neoastra_decision_defer(&decision) == NEOASTRA_OK);
    assert(neoastra_decision_defer(&decision) == NEOASTRA_ERROR_INVALID_STATE);
    neoastra_decision_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    response.action = NEOASTRA_DECISION_ALLOW;
    const std::string text = "selected";
    const std::string path = "C:/selected.txt";
    const neoastra_string_view_t paths[]{neo_string_view(path)};
    response.text = neo_string_view(text);
    response.paths = paths;
    response.path_count = 1;
    response.persist = 1;
    assert(neoastra_decision_complete(&decision, &response, nullptr) == NEOASTRA_OK);
    assert(decision.resolved_action.load() == NEOASTRA_DECISION_ALLOW);
    assert(captured.action == NEOASTRA_DECISION_ALLOW);
    assert(captured.persist == 1 && captured.text == text && captured.path == path);
    assert(neoastra_decision_complete(&decision, &response, nullptr) == NEOASTRA_ERROR_INVALID_STATE);

    neoastra_decision explicit_default;
    captured_decision default_capture;
    explicit_default.kind = NEOASTRA_DECISION_DOWNLOAD_REQUEST;
    explicit_default.default_action = NEOASTRA_DECISION_CANCEL;
    explicit_default.completion = capture_decision;
    explicit_default.completion_context = &default_capture;
    response.action = NEOASTRA_DECISION_DEFAULT;
    response.text = {};
    response.paths = nullptr;
    response.path_count = 0;
    response.persist = 0;
    assert(neoastra_decision_complete(&explicit_default, &response, nullptr) == NEOASTRA_OK);
    assert(explicit_default.resolved_action.load() == NEOASTRA_DECISION_DEFAULT);
    assert(default_capture.action == NEOASTRA_DECISION_DEFAULT);
}

void test_decision_timeout() {
    neoastra_decision decision;
    decision.deadline = std::chrono::steady_clock::now() - std::chrono::milliseconds(1);
    assert(neoastra_decision_defer(&decision) == NEOASTRA_ERROR_TIMED_OUT);
    assert(decision.resolved_action.load() == NEOASTRA_DECISION_DENY);
}

void test_app_teardown_detaches_retained_decision() {
    auto* app = create_test_app(true);
    auto* decision = new neoastra_decision;
    captured_decision captured;
    decision->kind = NEOASTRA_DECISION_APPLICATION_QUIT;
    decision->default_action = NEOASTRA_DECISION_CANCEL;
    decision->completion = capture_decision;
    decision->completion_context = &captured;
    decision->attach_app(app);
    assert(neoastra_decision_defer(decision) == NEOASTRA_OK);

    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    assert(decision->state.load(std::memory_order_acquire) == neo_decision_state::abandoned);
    assert(decision->app_owner.load(std::memory_order_acquire) == nullptr);
    assert(captured.action == NEOASTRA_DECISION_CANCEL);
    neoastra_app_release(app);

    neoastra_decision_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    response.action = NEOASTRA_DECISION_ALLOW;
    assert(neoastra_decision_complete(decision, &response, nullptr) == NEOASTRA_ERROR_INVALID_STATE);
    neoastra_decision_release(decision);
}

void test_wrong_thread_decision_races_app_teardown() {
    auto* app = create_test_app(true);
    auto* decision = new neoastra_decision;
    captured_decision captured;
    decision->kind = NEOASTRA_DECISION_APPLICATION_QUIT;
    decision->default_action = NEOASTRA_DECISION_CANCEL;
    decision->completion = capture_decision;
    decision->completion_context = &captured;
    decision->attach_app(app);

    std::atomic<neoastra_result_t> defer_result{NEOASTRA_OK};
    std::atomic<bool> worker_ready{};
    std::atomic<bool> stop{};
    std::atomic<int> wrong_thread{};
    std::atomic<int> invalid_state{};
    std::atomic<bool> unexpected{};
    std::thread worker([&] {
        const auto deferred = neoastra_decision_defer(decision);
        defer_result.store(deferred, std::memory_order_release);
        if (deferred == NEOASTRA_ERROR_WRONG_THREAD) ++wrong_thread;
        else unexpected.store(true, std::memory_order_release);
        worker_ready.store(true, std::memory_order_release);
        neoastra_decision_response_t response{};
        response.size = sizeof(response);
        response.version = 1;
        response.action = NEOASTRA_DECISION_ALLOW;
        while (!stop.load(std::memory_order_acquire)) {
            const auto result = neoastra_decision_complete(decision, &response, nullptr);
            if (result == NEOASTRA_ERROR_WRONG_THREAD) ++wrong_thread;
            else if (result == NEOASTRA_ERROR_INVALID_STATE) ++invalid_state;
            else unexpected.store(true, std::memory_order_release);
        }
    });
    while (!worker_ready.load(std::memory_order_acquire)) std::this_thread::yield();
    assert(defer_result.load(std::memory_order_acquire) == NEOASTRA_ERROR_WRONG_THREAD);
    assert(decision->state.load(std::memory_order_acquire) == neo_decision_state::pending);
    assert(neoastra_decision_defer(decision) == NEOASTRA_OK);

    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
    while (invalid_state.load(std::memory_order_acquire) == 0) std::this_thread::yield();
    stop.store(true, std::memory_order_release);
    worker.join();

    assert(!unexpected.load(std::memory_order_acquire));
    assert(wrong_thread.load(std::memory_order_acquire) != 0);
    assert(invalid_state.load(std::memory_order_acquire) != 0);
    assert(decision->app_owner.load(std::memory_order_acquire) == nullptr);
    neoastra_decision_release(decision);
}

void test_deferred_decision_self_lifetime() {
    std::atomic<int> completed{};
    auto* decision = new neoastra_decision;
    decision->kind = NEOASTRA_DECISION_PERMISSION;
    decision->default_action = NEOASTRA_DECISION_DENY;
    decision->completion = [](void* context, const neoastra_decision_response_t*) noexcept { ++*static_cast<std::atomic<int>*>(context); };
    decision->completion_context = &completed;
    assert(neoastra_decision_defer(decision) == NEOASTRA_OK);
    decision->release();

    neoastra_decision_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    response.action = NEOASTRA_DECISION_ALLOW;
    assert(neoastra_decision_complete(decision, &response, nullptr) == NEOASTRA_OK);
    assert(completed.load() == 1);
}

void test_callback_quiescence() {
    neo_callback_slot<neoastra_dispatch_callback_t> slot;
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
    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    options.maximum_pending_dispatches = 1;
    options.log_callback = capture_log;
    options.log_context = &captured;
    neoastra_app_t* app{};
    assert(neoastra_app_attach(&options, &app, nullptr) == NEOASTRA_OK);
    assert(captured.information.load() == 1);
    std::atomic<int> dispatch_calls{};
    assert(neoastra_app_dispatch(app, increment, &dispatch_calls) == NEOASTRA_OK);

    constexpr int thread_count = 8;
    constexpr int messages_per_thread = 100;
    std::array<std::thread, thread_count> threads;
    for (auto& thread : threads) {
        thread = std::thread([app, &dispatch_calls] {
            for (int index = 0; index < messages_per_thread; ++index) {
                assert(neoastra_app_dispatch(app, increment, &dispatch_calls) == NEOASTRA_ERROR_INVALID_STATE);
            }
        });
    }
    for (auto& thread : threads) thread.join();

    assert(captured.warnings.load() == thread_count * messages_per_thread);
    assert(captured.metadata_valid.load());
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    assert(dispatch_calls.load() == 1);
    assert(captured.information.load() == 2);
    neoastra_app_release(app);

    options.log_callback = throw_from_log;
    options.log_context = nullptr;
    app = nullptr;
    assert(neoastra_app_attach(&options, &app, nullptr) == NEOASTRA_OK);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);

    releasing_log_context releasing;
    options.log_callback = release_app_from_shutdown_log;
    options.log_context = &releasing;
    app = nullptr;
    assert(neoastra_app_attach(&options, &app, nullptr) == NEOASTRA_OK);
    releasing.app = app;
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    assert(releasing.calls.load() == 2);
}

void test_utf8() {
    const uint8_t overlong[] = {0xc0, 0x80};
    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    options.application_name = {overlong, sizeof(overlong)};
    neoastra_app_t* app{};
    neoastra_error_t* error{};
    assert(neoastra_app_create(&options, &app, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(app == nullptr && error != nullptr);
    neoastra_error_release(error);
}

void test_structure_versions() {
    neoastra_error_t* error{};

    neoastra_runtime_info_t undersized{};
    undersized.size = sizeof(undersized) - 1;
    undersized.version = 1;
    assert(neoastra_get_runtime_info(&undersized, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);

    neoastra_runtime_info_t unsupported{};
    unsupported.size = sizeof(unsupported);
    unsupported.version = 2;
    error = nullptr;
    assert(neoastra_get_runtime_info(&unsupported, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);

    struct extended_runtime_info {
        neoastra_runtime_info_t value{};
        std::array<uint8_t, 32> trailing{};
    } extended;
    extended.trailing.fill(0xa5);
    extended.value.size = sizeof(extended);
    extended.value.version = 1;
    error = nullptr;
    assert(neoastra_get_runtime_info(&extended.value, &error) == NEOASTRA_OK);
    assert(error == nullptr);
    assert(extended.value.backend_name.length != 0);
    for (const auto byte : extended.trailing) assert(byte == 0xa5);
}

void test_native_parent_structure() {
    auto* environment = reinterpret_cast<neoastra_environment_t*>(1);
    neoastra_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.parent.kind = NEOASTRA_NATIVE_PARENT_WIN32_HWND;
    options.parent.handle = reinterpret_cast<void*>(1);

    neoastra_error_t* error{};
    options.parent.size = sizeof(options.parent) - 1;
    options.parent.version = 1;
    assert(neoastra_environment_create_view_async(environment, &options, ignore_view, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);

    error = nullptr;
    options.parent.size = sizeof(options.parent);
    options.parent.version = 2;
    assert(neoastra_environment_create_view_async(environment, &options, ignore_view, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);

    const std::string malformed_origin = "https:";
    const auto malformed_origin_view = neo_string_view(malformed_origin);
    options.parent.version = 1;
    options.bridge_policy = NEOASTRA_BRIDGE_TRUSTED_ORIGINS;
    options.bridge_origin_count = 1;
    options.bridge_origins = &malformed_origin_view;
    error = nullptr;
    assert(neoastra_environment_create_view_async(environment, &options, ignore_view, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);

    const std::string valid_origin = "https://trusted.example";
    const auto valid_origin_view = neo_string_view(valid_origin);
    options.bridge_origins = &valid_origin_view;
    options.bridge_policy = NEOASTRA_BRIDGE_DISABLED;
    error = nullptr;
    assert(neoastra_environment_create_view_async(environment, &options, ignore_view, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    neoastra_error_release(error);

    options.bridge_policy = NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW;
    error = nullptr;
    assert(neoastra_environment_create_view_async(environment, &options, ignore_view, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    neoastra_error_release(error);

    options.bridge_policy = NEOASTRA_BRIDGE_TRUSTED_ORIGINS;
    options.bridge_origin_count = 0;
    options.bridge_origins = nullptr;
    error = nullptr;
    assert(neoastra_environment_create_view_async(environment, &options, ignore_view, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    neoastra_error_release(error);

    options.bridge_policy = static_cast<neoastra_bridge_policy_t>(3);
    error = nullptr;
    assert(neoastra_environment_create_view_async(environment, &options, ignore_view, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    neoastra_error_release(error);
}

void test_custom_scheme_validation_and_trailing_bytes() {
    auto* app = create_test_app(true);
    const std::string name = "app";
    neoastra_custom_scheme_t scheme{};
    scheme.size = sizeof(scheme);
    scheme.version = 1;
    scheme.name = neo_string_view(name);
    scheme.flags = NEOASTRA_CUSTOM_SCHEME_HAS_AUTHORITY | NEOASTRA_CUSTOM_SCHEME_SECURE;
    scheme.resource_provider = empty_resource;

    neoastra_environment_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.custom_scheme_count = 1;
    options.custom_schemes = &scheme;
    options.custom_scheme_stride = sizeof(scheme) - 1;

    neoastra_error_t* error{};
    assert(neoastra_environment_create_async(app, &options, ignore_environment, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);
    options.custom_scheme_stride = sizeof(scheme);
    error = nullptr;
    scheme.size = sizeof(scheme) - 1;
    assert(neoastra_environment_create_async(app, &options, ignore_environment, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);

    scheme.size = sizeof(scheme);
    scheme.allowed_origin_count = 1;
    error = nullptr;
    assert(neoastra_environment_create_async(app, &options, ignore_environment, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);
    scheme.allowed_origin_count = 0;

    const std::string invalid_origin = "https://user@trusted.example";
    const auto invalid_origin_view = neo_string_view(invalid_origin);
    scheme.allowed_origin_count = 1;
    scheme.allowed_origins = &invalid_origin_view;
    error = nullptr;
    assert(neoastra_environment_create_async(app, &options, ignore_environment, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);
    scheme.allowed_origin_count = 0;
    scheme.allowed_origins = nullptr;

    const std::string built_in_name = "HTTPS";
    scheme.name = neo_string_view(built_in_name);
    error = nullptr;
    assert(neoastra_environment_create_async(app, &options, ignore_environment, nullptr, nullptr, &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error != nullptr);
    neoastra_error_release(error);
    scheme.name = neo_string_view(name);

    struct extended_scheme {
        neoastra_custom_scheme_t value{};
        std::array<uint8_t, 32> trailing{};
    };
    std::array<extended_scheme, 2> extended{};
    const std::string second_name = "assets";
    extended[0].value = scheme;
    extended[1].value = scheme;
    extended[1].value.name = neo_string_view(second_name);
    for (auto& descriptor : extended) {
        descriptor.value.size = sizeof(extended_scheme);
        descriptor.trailing.fill(0xa5);
    }
    options.custom_scheme_count = static_cast<uint32_t>(extended.size());
    options.custom_schemes = &extended[0].value;
    options.custom_scheme_stride = sizeof(extended_scheme);

    std::atomic<neoastra_result_t> result{NEOASTRA_OK};
    std::thread worker([&] {
        neoastra_error_t* worker_error{};
        result.store(neoastra_environment_create_async(app, &options, ignore_environment, nullptr, nullptr, &worker_error));
        assert(worker_error != nullptr);
        neoastra_error_release(worker_error);
    });
    worker.join();
    assert(result.load() == NEOASTRA_ERROR_WRONG_THREAD);
    for (const auto& descriptor : extended) for (const auto byte : descriptor.trailing) assert(byte == 0xa5);

    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

void test_custom_scheme_provider_release_once_and_exception_containment() {
    std::atomic<int> releases{};
    {
        neo_custom_scheme_registration registration;
        registration.provider_context = &releases;
        registration.release_provider_context = release_resource_context;
        std::vector<neo_custom_scheme_registration> registrations;
        registrations.push_back(std::move(registration));
        assert(releases.load() == 0);
    }
    assert(releases.load() == 1);

    {
        neo_custom_scheme_registration registration;
        registration.provider_context = &releases;
        registration.release_provider_context = throw_releasing_resource_context;
    }
    assert(releases.load() == 2);

    neoastra_resource_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    response.status_code = 200;
    assert(neo_valid_resource_response_shape(response));
    response.body_kind = NEOASTRA_RESOURCE_BODY_BYTES;
    response.byte_length = 1;
    assert(!neo_valid_resource_response_shape(response));
    const uint8_t byte{};
    response.bytes = &byte;
    assert(!neo_valid_resource_response_shape(response));
    response.content_length = 1;
    assert(neo_valid_resource_response_shape(response));
    response.byte_length = neo_maximum_buffered_resource_body_size + 1;
    response.content_length = response.byte_length;
    assert(!neo_valid_resource_response_shape(response));
    response.byte_length = 1;
    response.content_length = 2;
    assert(!neo_valid_resource_response_shape(response));
    response.content_length = 1;
    const std::string invalid_headers = "Good: value\r\nInjected\r\n";
    assert(!neo_valid_response_headers(invalid_headers));
    assert(!neo_valid_response_headers("Good: value\r\n\r\nInjected: value\r\n"));
    assert(neo_valid_response_headers("Good: value\r\nOther:\tvalue\n"));
    assert(neo_resource_request_within_limits("app://host/file", "GET", "Accept: */*\r\n"));
    assert(!neo_resource_request_within_limits(std::string(neo_maximum_resource_metadata_size + 1, 'a'), "GET", {}));
    response.body_kind = NEOASTRA_RESOURCE_BODY_FILE;
    response.bytes = nullptr;
    response.byte_length = 0;
    response.content_length = UINT64_MAX;
#if defined(_WIN32)
    const std::string path = "C:\\neoastra-resource";
#else
    const std::string path = "/tmp/neoastra-resource";
#endif
    response.file_path = neo_string_view(path);
    assert(neo_valid_resource_response_shape(response));
    const std::string relative_path = "relative-resource";
    response.file_path = neo_string_view(relative_path);
    assert(!neo_valid_resource_response_shape(response));
    response.file_path = neo_string_view(path);
    response.release_context = &releases;
    assert(!neo_valid_resource_response_shape(response));
    response.release = release_resource_context;
    assert(neo_valid_resource_response_shape(response));
    {
        neo_resource_response_release_guard guard{response};
        guard.release_once();
        guard.release_once();
    }
    assert(releases.load() == 3);

    response.release_context = &releases;
    response.release = throw_releasing_resource_context;
    { neo_resource_response_release_guard guard{response}; }
    assert(releases.load() == 4);
}

void test_bridge_origin_trust() {
    std::vector<neo_custom_scheme_registration> custom_schemes;
    neo_custom_scheme_registration application_scheme;
    application_scheme.name = "app";
    application_scheme.flags = NEOASTRA_CUSTOM_SCHEME_APPLICATION;
    custom_schemes.push_back(std::move(application_scheme));
    const std::vector<std::string> bridge_origins={"https://trusted.example", "custom://host/", "app://neoastra"};

    assert(!neo_bridge_access_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_DISABLED, "app://neoastra/index.html"));
    assert(!neo_bridge_access_allowed_for(custom_schemes, {}, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, "app://neoastra/index.html"));
    assert(neo_bridge_access_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, "app://neoastra/index.html"));
    assert(neo_bridge_access_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, "APP://NEOASTRA/index.html"));
    assert(neo_bridge_access_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, "https://trusted.example/path?q=1"));
    assert(neo_bridge_access_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, "CUSTOM://HOST/resource"));
    assert(!neo_bridge_access_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, "assets://neoastra/index.html"));
    assert(!neo_bridge_access_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, "app://other-host/index.html"));
    assert(!neo_bridge_access_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, "https://trusted.example.evil/path"));
    assert(!neo_bridge_access_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, "https://untrusted.example/"));
    assert(neo_bridge_access_allowed_for(custom_schemes, {}, NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW, "https://untrusted.example/"));
    assert(neo_bridge_access_allowed_for(custom_schemes, {}, NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW, ""));
    assert(neo_bridge_message_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, 4, false, "1234", "app://neoastra"));
    assert(!neo_bridge_message_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, 4, false, "12345", "app://neoastra"));
    assert(!neo_bridge_message_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, 4, true, "1", "app://neoastra"));
    assert(!neo_bridge_message_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_TRUSTED_ORIGINS, 4, false, "1", "app://other-host"));
    assert(!neo_bridge_message_allowed_for(custom_schemes, bridge_origins, NEOASTRA_BRIDGE_DISABLED, 4, false, "1", "app://neoastra"));
    assert(neo_bridge_message_allowed_for(custom_schemes, {}, NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW, 4, false, "1", ""));
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
    test_app_teardown_detaches_retained_decision();
    test_wrong_thread_decision_races_app_teardown();
    test_deferred_decision_self_lifetime();
    test_callback_quiescence();
    test_logging_thread_safety_and_exception_containment();
    test_utf8();
    test_structure_versions();
    test_native_parent_structure();
    test_custom_scheme_validation_and_trailing_bytes();
    test_custom_scheme_provider_release_once_and_exception_containment();
    test_bridge_origin_trust();
    return 0;
}
