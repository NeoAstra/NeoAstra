#include "../src/common/native_internal.hpp"
#include "../src/common/script_completion.hpp"

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <barrier>
#include <cstdio>
#include <memory>
#include <thread>
#include <utility>

namespace completion_test_backend {
extern neo_platform_created_callback_t environment_callback;
extern void* environment_context;
extern neo_platform_created_callback_t view_callback;
extern void* view_context;
extern uint32_t environments_destroyed;
extern uint32_t views_destroyed;
}

namespace {
struct captured_completion {
    uint32_t calls{};
    uint32_t contexts_released{};
    neoastra_result_t result{NEOASTRA_ERROR_UNKNOWN};
    void* value{};
    const std::thread::id ui_thread{std::this_thread::get_id()};
};
struct callback_context {
    captured_completion& captured;
    ~callback_context() { ++captured.contexts_released; }
};

void NEOASTRA_CALL capture_environment(void* pointer, neoastra_result_t result, neoastra_environment_t* value, const neoastra_error_t*) {
    std::unique_ptr<callback_context> context(static_cast<callback_context*>(pointer));
    auto* captured = &context->captured;
    assert(std::this_thread::get_id() == captured->ui_thread);
    ++captured->calls;
    captured->result = result;
    captured->value = value;
}
void NEOASTRA_CALL capture_view(void* pointer, neoastra_result_t result, neoastra_view_t* value, const neoastra_error_t*) {
    std::unique_ptr<callback_context> context(static_cast<callback_context*>(pointer));
    auto* captured = &context->captured;
    assert(std::this_thread::get_id() == captured->ui_thread);
    ++captured->calls;
    captured->result = result;
    captured->value = value;
}
void NEOASTRA_CALL ignore_dispatch(void*) { }
void NEOASTRA_CALL capture_script(void* pointer, neoastra_result_t result, neoastra_string_view_t value, const neoastra_error_t*) {
    std::unique_ptr<callback_context> context(static_cast<callback_context*>(pointer));
    auto* captured = &context->captured;
    assert(std::this_thread::get_id() == captured->ui_thread);
    ++captured->calls;
    captured->result = result;
    if (result == NEOASTRA_OK) assert(neo_string(value) == "42");
    else assert(value.length == 0);
}

neoastra_app_t* create_app() {
    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    options.maximum_pending_dispatches = 1;
    neoastra_app_t* app{};
    assert(neoastra_app_attach(&options, &app, nullptr) == NEOASTRA_OK);
    return app;
}

neoastra_operation_t* start_environment(neoastra_app_t* app, captured_completion& captured) {
    neoastra_environment_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    neoastra_operation_t* operation{};
    assert(neoastra_environment_create_async(app, &options, capture_environment, new callback_context{captured}, &operation, nullptr) == NEOASTRA_OK);
    assert(operation && captured.calls == 0);
    return operation;
}
neoastra_operation_t* start_view(neoastra_environment_t* environment, captured_completion& captured) {
    neoastra_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    neoastra_operation_t* operation{};
    assert(neoastra_environment_create_view_async(environment, &options, capture_view, new callback_context{captured}, &operation, nullptr) == NEOASTRA_OK);
    assert(operation && captured.calls == 0);
    return operation;
}

void finish_environment_on_worker(neoastra_error_t* error = nullptr) {
    const auto callback = std::exchange(completion_test_backend::environment_callback, nullptr);
    auto* context = std::exchange(completion_test_backend::environment_context, nullptr);
    assert(callback && context);
    std::thread worker([=] { callback(context, error); });
    worker.join();
}
void finish_view_on_worker(neoastra_error_t* error = nullptr) {
    const auto callback = std::exchange(completion_test_backend::view_callback, nullptr);
    auto* context = std::exchange(completion_test_backend::view_context, nullptr);
    assert(callback && context);
    std::thread worker([=] { callback(context, error); });
    worker.join();
}
void assert_saturated(neoastra_app_t* app) {
    assert(neoastra_app_dispatch(app, ignore_dispatch, nullptr) == NEOASTRA_OK);
    assert(neoastra_app_dispatch(app, ignore_dispatch, nullptr) == NEOASTRA_ERROR_INVALID_STATE);
}

void test_creation_queue_rejection() {
    auto* app = create_app();
    const auto environment_count = completion_test_backend::environments_destroyed;
    const auto view_count = completion_test_backend::views_destroyed;
    captured_completion environment_result;
    auto* environment_operation = start_environment(app, environment_result);
    assert_saturated(app);
    finish_environment_on_worker();
    assert(environment_result.calls == 0); // Never fall back to a callback on the worker.
    neo_drain_dispatch(app);
    assert(environment_result.calls == 1 && environment_result.result == NEOASTRA_OK);
    assert(environment_operation->references.load() == 1);
    auto* environment = static_cast<neoastra_environment_t*>(environment_result.value);
    assert(environment);
    captured_completion view_result;
    auto* view_operation = start_view(environment, view_result);
    assert_saturated(app);
    finish_view_on_worker();
    assert(view_result.calls == 0);
    neo_drain_dispatch(app);
    assert(view_result.calls == 1 && view_result.result == NEOASTRA_OK);
    assert(view_operation->references.load() == 1);
    neo_drain_dispatch(app);
    assert(environment_result.calls == 1 && view_result.calls == 1);
    assert(environment_result.contexts_released == 1 && view_result.contexts_released == 1);
    neoastra_view_release(static_cast<neoastra_view_t*>(view_result.value));
    neoastra_environment_release(environment);
    neoastra_operation_release(environment_operation);
    neoastra_operation_release(view_operation);
    assert(completion_test_backend::environments_destroyed == environment_count + 1);
    assert(completion_test_backend::views_destroyed == view_count + 1);
    assert(app->ui_objects == nullptr && app->references.load() == 1);
    neoastra_app_release(app);
}

void test_creation_shutdown_rejection() {
    auto* app = create_app();
    captured_completion environment_result;
    auto* operation = start_environment(app, environment_result);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    assert(environment_result.calls == 1 && environment_result.result == NEOASTRA_ERROR_CANCELED);
    assert(environment_result.value == nullptr);
    assert(operation->references.load() == 1); // Terminal ownership was released, not just the managed wait.
    finish_environment_on_worker(new neoastra_error(NEOASTRA_ERROR_NATIVE_FAILURE, 1, "test", "late failure"));
    neo_drain_dispatch(app);
    assert(environment_result.calls == 1);
    assert(environment_result.contexts_released == 1);
    assert(app->ui_objects == nullptr && app->references.load() == 1);
    neoastra_operation_release(operation);
    neoastra_app_release(app);
}

void test_script_queue_rejection() {
    auto* app = create_app();
    captured_completion result;
    auto* operation = new neoastra_operation;
    operation->retain();
    auto source = std::make_shared<neo_script_source>(app, capture_script, new callback_context{result}, operation);
    source->accepted = true;
    assert_saturated(app);
    std::thread worker([source] {
        auto* completion = source->completion;
        completion->result = "42";
        completion->requested = NEOASTRA_OK;
        completion->submit();
    });
    worker.join();
    source.reset();
    assert(result.calls == 0);
    neo_drain_dispatch(app);
    assert(result.calls == 1 && result.result == NEOASTRA_OK);
    assert(operation->references.load() == 1);
    neo_drain_dispatch(app);
    assert(result.calls == 1 && result.contexts_released == 1 && app->references.load() == 1);
    neoastra_operation_release(operation);
    neoastra_app_release(app);
}

void test_view_shutdown_and_late_success() {
    auto* app = create_app();
    captured_completion environment_result;
    auto* environment_operation = start_environment(app, environment_result);
    finish_environment_on_worker();
    neo_drain_dispatch(app);
    auto* environment = static_cast<neoastra_environment_t*>(environment_result.value);
    captured_completion view_result;
    auto* view_operation = start_view(environment, view_result);
    assert_saturated(app);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    assert(view_result.calls == 1 && view_result.result == NEOASTRA_ERROR_CANCELED);
    assert(view_result.value == nullptr && view_result.contexts_released == 1);
    assert(view_operation->references.load() == 1);
    finish_view_on_worker();
    neo_drain_dispatch(app);
    assert(view_result.calls == 1);
    assert(environment->references.load() == 1);
    neoastra_environment_release(environment);
    neoastra_operation_release(environment_operation);
    neoastra_operation_release(view_operation);
    assert(app->ui_objects == nullptr && app->references.load() == 1);
    neoastra_app_release(app);
}

void test_creation_cancellation_and_failure_cleanup() {
    auto* app = create_app();
    for (const auto canceled : {false, true}) {
        captured_completion captured;
        auto* operation = start_environment(app, captured);
        if (canceled) neoastra_operation_cancel(operation);
        assert_saturated(app);
        auto* error = new neoastra_error(NEOASTRA_ERROR_NATIVE_FAILURE, 7, "test", "creation failed");
        error->retain();
        finish_environment_on_worker(error);
        neo_drain_dispatch(app);
        assert(captured.calls == 1 && captured.contexts_released == 1 && captured.value == nullptr);
        assert(captured.result == (canceled ? NEOASTRA_ERROR_CANCELED : NEOASTRA_ERROR_NATIVE_FAILURE));
        assert(operation->references.load() == 1 && error->references.load() == 1);
        neoastra_operation_release(operation);
        error->release();
        assert(app->ui_objects == nullptr && app->references.load() == 1);
    }
    neoastra_app_release(app);
}

void test_popup_inline_completion_does_not_deliver_other_operations() {
    auto* app = create_app();
    auto* environment = new neoastra_environment(app);
    auto* opener = new neoastra_view(environment);
    auto* request = new neoastra_decision;
    neo_configure_decision(request, opener, NEOASTRA_DECISION_NEW_WINDOW, NEOASTRA_DECISION_CANCEL);

    captured_completion other_result;
    auto* other_operation = start_environment(app, other_result);
    finish_environment_on_worker(); // Leave an earlier ordinary completion queued.
    captured_completion popup_result;
    neoastra_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.popup_request = request;
    neoastra_operation_t* popup_operation{};
    assert(neoastra_environment_create_view_async(environment, &options, capture_view, new callback_context{popup_result}, &popup_operation, nullptr) == NEOASTRA_OK);
    const auto callback = std::exchange(completion_test_backend::view_callback, nullptr);
    auto* context = std::exchange(completion_test_backend::view_context, nullptr);
    callback(context, nullptr);
    assert(popup_result.calls == 1 && popup_result.contexts_released == 1 && popup_result.value);
    assert(other_result.calls == 0);
    neo_drain_dispatch(app);
    assert(other_result.calls == 1 && popup_result.calls == 1);
    neoastra_view_release(static_cast<neoastra_view_t*>(popup_result.value));
    neoastra_environment_release(static_cast<neoastra_environment_t*>(other_result.value));
    request->abandon();
    request->release();
    neoastra_view_release(opener);
    neoastra_environment_release(environment);
    neoastra_operation_release(other_operation);
    neoastra_operation_release(popup_operation);
    assert(app->ui_objects == nullptr && app->references.load() == 1);
    neoastra_app_release(app);
}

void test_script_shutdown_and_late_completion() {
    auto* app = create_app();
    captured_completion result;
    auto* operation = new neoastra_operation;
    operation->retain();
    auto source = std::make_shared<neo_script_source>(app, capture_script, new callback_context{result}, operation);
    source->accepted = true;
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    assert(result.calls == 1 && result.contexts_released == 1 && result.result == NEOASTRA_ERROR_CANCELED);
    assert(operation->references.load() == 1);
    auto* error = new neoastra_error(NEOASTRA_ERROR_NATIVE_FAILURE, 1, "test", "late script failure");
    error->retain();
    std::thread worker([source, error] {
        source->completion->requested = NEOASTRA_ERROR_NATIVE_FAILURE;
        source->completion->error = error;
        source->completion->submit();
        source->completion->submit(); // Duplicate posting cannot deliver the context twice.
    });
    worker.join();
    source.reset();
    neo_drain_dispatch(app);
    assert(result.calls == 1 && app->references.load() == 1 && error->references.load() == 1);
    error->release();
    neoastra_operation_release(operation);
    neoastra_app_release(app);
}

void test_script_cancellation_failure_and_dropped_handler() {
    auto* app = create_app();
    for (const auto requested : {NEOASTRA_OK, NEOASTRA_ERROR_NATIVE_FAILURE, NEOASTRA_ERROR_CANCELED}) {
        captured_completion result;
        auto* operation = new neoastra_operation;
        operation->retain();
        auto source = std::make_shared<neo_script_source>(app, capture_script, new callback_context{result}, operation);
        source->accepted = true;
        if (requested == NEOASTRA_OK) neoastra_operation_cancel(operation);
        if (requested != NEOASTRA_ERROR_CANCELED) {
            source->completion->requested = requested;
            source->completion->result = "42";
            source->completion->submit();
            source->completion->submit();
        }
        assert_saturated(app);
        std::thread worker([source = std::move(source)] { (void)source; }); // Final handler release may be off-thread.
        worker.join();
        assert(result.calls == 0 && result.contexts_released == 0);
        neo_drain_dispatch(app);
        assert(result.calls == 1 && result.contexts_released == 1);
        assert(result.result == (requested == NEOASTRA_ERROR_NATIVE_FAILURE ? requested : NEOASTRA_ERROR_CANCELED));
        assert(operation->references.load() == 1 && app->references.load() == 1);
        neoastra_operation_release(operation);
    }
    neoastra_app_release(app);
}

void test_failed_script_start_does_not_own_terminal_callback() {
    auto* app = create_app();
    captured_completion result;
    auto* operation = new neoastra_operation;
    auto context = std::unique_ptr<callback_context>(new callback_context{result});
    // The source never becomes accepted; the caller still owns operation/context.
    { auto source = std::make_shared<neo_script_source>(app, capture_script, context.get(), operation); }
    neo_drain_dispatch(app);
    assert(result.calls == 0 && result.contexts_released == 0 && operation->references.load() == 1);
    assert(app->pending_completions == nullptr && app->references.load() == 1);
    context.reset();
    assert(result.contexts_released == 1);
    neoastra_operation_release(operation);
    neoastra_app_release(app);
}

void test_completion_races_with_shutdown() {
    for (uint32_t iteration = 0; iteration < 100; ++iteration) {
        auto* app = create_app();
        captured_completion environment_result;
        auto* environment_operation = start_environment(app, environment_result);
        const auto callback = std::exchange(completion_test_backend::environment_callback, nullptr);
        auto* context = std::exchange(completion_test_backend::environment_context, nullptr);
        captured_completion script_result;
        auto* script_operation = new neoastra_operation;
        script_operation->retain();
        auto source = std::make_shared<neo_script_source>(app, capture_script, new callback_context{script_result}, script_operation);
        source->accepted = true;
        std::barrier start(2);
        std::thread worker([&, source = std::move(source)] {
            start.arrive_and_wait();
            callback(context, nullptr);
            source->completion->result = "42";
            source->completion->requested = NEOASTRA_OK;
            source->completion->submit();
        });
        start.arrive_and_wait();
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        worker.join();
        neo_drain_dispatch(app);
        assert(environment_result.calls == 1 && environment_result.contexts_released == 1);
        assert(script_result.calls == 1 && script_result.contexts_released == 1);
        assert(environment_operation->references.load() == 1 && script_operation->references.load() == 1);
        if (environment_result.value) neoastra_environment_release(static_cast<neoastra_environment_t*>(environment_result.value));
        assert(app->pending_completions == nullptr && app->ready_completions == nullptr);
        assert(app->ui_objects == nullptr && app->references.load() == 1);
        neoastra_operation_release(environment_operation);
        neoastra_operation_release(script_operation);
        neoastra_app_release(app);
    }
}
}

int main() {
    test_creation_queue_rejection();
    test_creation_shutdown_rejection();
    test_script_queue_rejection();
    test_view_shutdown_and_late_success();
    test_creation_cancellation_and_failure_cleanup();
    test_popup_inline_completion_does_not_deliver_other_operations();
    test_script_shutdown_and_late_completion();
    test_script_cancellation_failure_and_dropped_handler();
    test_failed_script_start_does_not_own_terminal_callback();
    test_completion_races_with_shutdown();
    std::puts("accepted-operation completion tests passed");
}
