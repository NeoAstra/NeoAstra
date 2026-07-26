#include "../src/common/native_internal.hpp"

#include <array>
#include <atomic>
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <thread>

namespace {

constexpr int worker_count = 4;
constexpr int dispatches_per_worker = 2000;
constexpr int releases_per_worker = 10000;

neoastra_app_t* create_attached_app(uint32_t dispatch_limit = 0) {
    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    options.maximum_pending_dispatches = dispatch_limit;
    neoastra_app_t* app{};
    neoastra_error_t* error{};
    assert(neoastra_app_attach(&options, &app, &error) == NEOASTRA_OK);
    assert(app != nullptr && error == nullptr);
    return app;
}

void NEOASTRA_CALL increment(void* context) {
    ++*static_cast<std::atomic<uint32_t>*>(context);
}

struct stressed_ui_object final : neo_ui_ref_counted {
    stressed_ui_object(neoastra_app_t* app, std::atomic<uint32_t>& teardowns,
                       std::atomic<uint32_t>& destructors, std::atomic<bool>& teardown_on_ui)
        : neo_ui_ref_counted(app), teardowns(teardowns), destructors(destructors),
          teardown_on_ui(teardown_on_ui) { }

    ~stressed_ui_object() override {
        destroy_ui_once();
        ++destructors;
    }

    void destroy_ui() noexcept override {
        teardown_on_ui.store(destruction_app->ui_thread == std::this_thread::get_id(), std::memory_order_release);
        ++teardowns;
    }

    std::atomic<uint32_t>& teardowns;
    std::atomic<uint32_t>& destructors;
    std::atomic<bool>& teardown_on_ui;
};

void test_dispatch_racing_detach() {
    constexpr auto total_dispatches = worker_count * dispatches_per_worker;
    auto* app = create_attached_app(total_dispatches + 1);
    std::atomic<bool> start{};
    std::atomic<uint32_t> ready{};
    std::atomic<uint32_t> attempts{};
    std::atomic<uint32_t> accepted{};
    std::atomic<uint32_t> rejected{};
    std::atomic<uint32_t> callbacks{};
    std::array<std::thread, worker_count> workers;

    for (auto& worker : workers) {
        worker = std::thread([&] {
            ++ready;
            while (!start.load(std::memory_order_acquire)) std::this_thread::yield();
            for (auto index = 0; index < dispatches_per_worker; ++index) {
                const auto result = neoastra_app_dispatch(app, increment, &callbacks);
                if (result == NEOASTRA_OK) ++accepted;
                else {
                    assert(result == NEOASTRA_ERROR_DISPOSED);
                    ++rejected;
                }
                ++attempts;
            }
        });
    }

    while (ready.load(std::memory_order_acquire) != worker_count) std::this_thread::yield();
    start.store(true, std::memory_order_release);
    while (attempts.load(std::memory_order_acquire) < 100) std::this_thread::yield();
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    for (auto& worker : workers) worker.join();

    assert(attempts.load() == total_dispatches);
    assert(accepted.load() + rejected.load() == total_dispatches);
    assert(accepted.load() != 0);
    assert(callbacks.load() == accepted.load());
    assert(neoastra_app_dispatch(app, increment, &callbacks) == NEOASTRA_ERROR_DISPOSED);
    neoastra_app_release(app);
}

void test_ui_release_racing_detach() {
    constexpr auto total_releases = worker_count * releases_per_worker;
    auto* app = create_attached_app();
    std::atomic<uint32_t> teardowns{};
    std::atomic<uint32_t> destructors{};
    std::atomic<bool> teardown_on_ui{};
    auto* value = new stressed_ui_object(app, teardowns, destructors, teardown_on_ui);
    for (auto index = 1; index < total_releases; ++index) assert(value->retain());

    std::atomic<bool> start{};
    std::atomic<uint32_t> ready{};
    std::atomic<uint32_t> releases{};
    std::array<std::thread, worker_count> workers;
    for (auto& worker : workers) {
        worker = std::thread([&] {
            ++ready;
            while (!start.load(std::memory_order_acquire)) std::this_thread::yield();
            for (auto index = 0; index < releases_per_worker; ++index) {
                assert(value->release());
                ++releases;
            }
        });
    }

    while (ready.load(std::memory_order_acquire) != worker_count) std::this_thread::yield();
    start.store(true, std::memory_order_release);
    while (releases.load(std::memory_order_acquire) < 100) std::this_thread::yield();
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    for (auto& worker : workers) worker.join();

    assert(releases.load() == total_releases);
    assert(teardowns.load() == 1);
    assert(destructors.load() == 1);
    assert(teardown_on_ui.load(std::memory_order_acquire));
    neoastra_app_release(app);
}

} // namespace

int main() {
    for (auto iteration = 0; iteration < 8; ++iteration) test_dispatch_racing_detach();
    for (auto iteration = 0; iteration < 8; ++iteration) test_ui_release_racing_detach();
    return 0;
}
