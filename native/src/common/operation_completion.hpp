#pragma once

#include "native_internal.hpp"

// Accepted operations reserve their terminal delivery before starting native work.
// Intrusive links make posting a completion allocation-free and independent of the
// bounded public dispatcher. The registry owns one reference; the native source
// owns another until it has returned its result (or released its handler).
struct neo_operation_completion : neo_ref_counted {
    neoastra_app_t* const app;
    neoastra_operation_t* operation{};

    explicit neo_operation_completion(neoastra_app_t* app) : app(app) {
        if (!app->retain()) throw std::bad_alloc();
    }
    ~neo_operation_completion() override {
        if (operation) operation->release();
        app->release();
    }

    // Called by the initiating UI thread before native work can publish a result.
    void accept(neoastra_operation_t* value) noexcept {
        std::lock_guard lock(app->dispatch_mutex);
        operation = value;
        _next = app->pending_completions;
        if (_next) _next->_previous = this;
        app->pending_completions = this;
        _registered = true;
        retain();
    }

    void submit() noexcept {
        bool wake{};
        {
            std::lock_guard lock(app->dispatch_mutex);
            if (_registered && !_queued) {
                if (app->ready_completions_tail) app->ready_completions_tail->_ready_next = this;
                else app->ready_completions = this;
                app->ready_completions_tail = this;
                _queued = true;
                wake = true;
            }
        }
        if (wake) neo_wake_app(app);
    }

    // A failed start still belongs to the caller; it must not receive a callback
    // or lose the operation reference that its ordinary start-failure path frees.
    void discard_start() noexcept {
        bool release_registry{};
        {
            std::lock_guard lock(app->dispatch_mutex);
            operation = nullptr;
            if (_registered) {
                unlink();
                release_registry = true;
            }
        }
        if (release_registry) release();
    }

    // Transfers the registry reference to the UI drain, never to a worker.
    static neo_operation_completion* take(neoastra_app_t* app, bool shutdown = false) noexcept {
        std::lock_guard lock(app->dispatch_mutex);
        auto* value = app->ready_completions;
        if (!value && shutdown) value = app->pending_completions;
        if (value) value->unlink();
        return value;
    }

    // UI-thread only. Shutdown invocation must not inspect the native result
    // payload: its source may still be writing it on another thread.
    void deliver(bool shutdown) noexcept {
        auto* pending = std::exchange(operation, nullptr);
        if (pending) {
            try { invoke(pending, shutdown); } catch (...) { }
            pending->release();
        }
        release();
    }

    void deliver_inline() noexcept {
        bool detached{};
        {
            std::lock_guard lock(app->dispatch_mutex);
            if (_registered) {
                unlink();
                detached = true;
            }
        }
        if (detached) deliver(false);
    }

protected:
    virtual void invoke(neoastra_operation_t* pending, bool shutdown) = 0;

private:
    neo_operation_completion* _previous{};
    neo_operation_completion* _next{};
    neo_operation_completion* _ready_next{};
    bool _registered{};
    bool _queued{};

    // All registry/queue links are protected by dispatch_mutex. Callbacks and
    // reference releases must run only after that mutex has been released.
    void unlink() noexcept {
        if (_previous) _previous->_next = _next;
        else app->pending_completions = _next;
        if (_next) _next->_previous = _previous;
        _previous = _next = nullptr;
        if (_queued) {
            auto** link = &app->ready_completions;
            neo_operation_completion* previous{};
            while (*link != this) {
                previous = *link;
                link = &previous->_ready_next;
            }
            *link = _ready_next;
            if (app->ready_completions_tail == this) app->ready_completions_tail = previous;
            _ready_next = nullptr;
            _queued = false;
        }
        _registered = false;
    }
};

inline void neo_cancel_pending_completions(neoastra_app_t* app) noexcept {
    while (auto* completion = neo_operation_completion::take(app, true)) completion->deliver(true);
}
