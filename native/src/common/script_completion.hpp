#pragma once

#include "operation_completion.hpp"

#include <memory>

// Shared with GUI-free tests so the Windows completion path is exercised on every host.
struct neo_script_completion final : neo_operation_completion {
    neoastra_string_callback_t callback;
    void* context;
    std::string result;
    neoastra_result_t requested{NEOASTRA_ERROR_CANCELED};
    neoastra_error_t* error{};

    neo_script_completion(neoastra_app_t* app, neoastra_string_callback_t callback, void* context, neoastra_operation_t* operation)
        : neo_operation_completion(app), callback(callback), context(context) { accept(operation); }
    ~neo_script_completion() override {
        if (error) error->release();
    }
    void invoke(neoastra_operation_t* pending, bool shutdown) override {
        neoastra_result_t actual{};
        const auto desired = shutdown ? NEOASTRA_ERROR_CANCELED : requested;
        if (pending->try_complete(desired, actual)) {
            callback(context, actual, actual == NEOASTRA_OK ? neo_string_view(result) : neoastra_string_view_t{},
                     !shutdown && actual == desired ? error : nullptr);
        }
        callback = nullptr;
        context = nullptr;
    }
};

// WebView2 may release an accepted handler without invoking it when its view
// closes. Its final owner queues the default canceled result in that case.
struct neo_script_source final {
    neo_script_completion* const completion;
    bool accepted{};

    neo_script_source(neoastra_app_t* app, neoastra_string_callback_t callback, void* context, neoastra_operation_t* operation)
        : completion(new neo_script_completion(app, callback, context, operation)) { }
    neo_script_source(const neo_script_source&) = delete;
    neo_script_source& operator=(const neo_script_source&) = delete;
    ~neo_script_source() {
        if (accepted) completion->submit();
        else completion->discard_start();
        completion->release();
    }
};
