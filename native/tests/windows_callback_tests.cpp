#include "../src/common/native_internal.hpp"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <chrono>
#include <thread>
#include <vector>

namespace {

neoastra_app_t* create_app() {
    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    neoastra_app_t* app{};
    assert(neoastra_app_attach(&options, &app, nullptr) == NEOASTRA_OK);
    return app;
}

neoastra_window_t* create_window(neoastra_app_t* app) {
    neoastra_window_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.bounds = {100, 100, 320, 240};
    options.flags = 3;
    neoastra_window_t* window{};
    assert(neoastra_app_create_window(app, &options, &window, nullptr) == NEOASTRA_OK);
    return window;
}

HWND window_handle(neoastra_window_t* window) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_window_get_native_handle(window, NEOASTRA_NATIVE_HANDLE_WIN32_HWND, &handle) == NEOASTRA_OK);
    return static_cast<HWND>(handle.value);
}

HWND dispatcher_handle(neoastra_app_t* app) {
    HWND candidate{};
    while ((candidate = FindWindowExW(nullptr, candidate, L"NeoAstra.Dispatcher", nullptr)) != nullptr) {
        if (reinterpret_cast<neoastra_app_t*>(GetWindowLongPtrW(candidate, GWLP_USERDATA)) == app) return candidate;
    }
    assert(false && "The attached application must have a dispatcher window");
    return nullptr;
}

struct window_callback_context {
    neoastra_app_t* app{};
    neoastra_window_t* window{};
    HWND hwnd{};
    uint64_t id{};
    bool detach{};
    int resized{};
    int state_changed{};
    int closed{};
    int scale_changed{};
    uint64_t expected_scale{1500};
};

void NEOASTRA_CALL release_window_on_resize(void* pointer, const neoastra_event_t* event) {
    auto* context = static_cast<window_callback_context*>(pointer);
    if (event->object_id != context->id) return;
    if (event->header.type == NEOASTRA_EVENT_WINDOW_RESIZED) {
        ++context->resized;
        auto* window = context->window;
        context->window = nullptr;
        if (context->detach) assert(neoastra_app_detach(context->app, nullptr) == NEOASTRA_OK);
        neoastra_window_release(window);
        // The window procedure, not just the event emitter, owns the callback's
        // lifetime. The next state event still belongs to the same native window.
        if (!context->detach) assert(IsWindow(context->hwnd));
    } else if (event->header.type == NEOASTRA_EVENT_WINDOW_STATE_CHANGED) {
        ++context->state_changed;
        assert(event->value == NEOASTRA_WINDOW_MINIMIZED);
        assert(IsWindow(context->hwnd));
    } else if (event->header.type == NEOASTRA_EVENT_WINDOW_CLOSED) ++context->closed;
    else if (event->header.type == NEOASTRA_EVENT_WINDOW_SCALE_FACTOR_CHANGED) {
        ++context->scale_changed;
        assert(event->value == context->expected_scale && IsWindow(context->hwnd));
    }
}

void test_window_callback_release(bool detach) {
    auto* app = create_app();
    auto* window = create_window(app);
    const auto hwnd = window_handle(window);
    window_callback_context context{app, window, hwnd, neoastra_window_get_id(window), detach};
    assert(neoastra_app_set_event_callback(app, release_window_on_resize, &context) == NEOASTRA_OK);
    SendMessageW(hwnd, WM_SIZE, SIZE_MINIMIZED, MAKELPARAM(320, 240));
    assert(context.resized == 1 && context.window == nullptr);
    assert(context.state_changed == (detach ? 0 : 1));
    assert(context.closed == 0); // Final-release teardown does not publish callbacks from a zero-reference window.
    assert(!IsWindow(hwnd));
    assert(neoastra_app_get_window_count(app) == 0);
    if (!detach) assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

void NEOASTRA_CALL count_window_events(void* pointer, const neoastra_event_t*) {
    ++*static_cast<int*>(pointer);
}

void test_dpi_callback_release() {
    auto* app = create_app();
    auto* window = create_window(app);
    const auto hwnd = window_handle(window);
    window_callback_context context{app, window, hwnd, neoastra_window_get_id(window)};
    double initial_scale{};
    assert(neoastra_window_get_scale_factor(window, &initial_scale) == NEOASTRA_OK);
    const UINT dpi = initial_scale == 1.5 ? 192 : 144;
    context.expected_scale = dpi * UINT64_C(1000) / USER_DEFAULT_SCREEN_DPI;
    assert(neoastra_app_set_event_callback(app, release_window_on_resize, &context) == NEOASTRA_OK);
    RECT suggested{};
    assert(GetWindowRect(hwnd, &suggested));
    suggested.right += 32;
    suggested.bottom += 32;
    // SetWindowPos inside WM_DPICHANGED synchronously sends WM_SIZE, whose event
    // releases the window before the outer DPI callback publishes its scale.
    SendMessageW(hwnd, WM_DPICHANGED, MAKELONG(dpi, dpi), reinterpret_cast<LPARAM>(&suggested));
    assert(context.resized == 1 && context.scale_changed == 1 && context.window == nullptr);
    assert(!IsWindow(hwnd));
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

// The width of the big or small icon that a window shows; 0 when it has none.
int icon_size(HWND hwnd, WPARAM kind) {
    const auto icon = reinterpret_cast<HICON>(SendMessageW(hwnd, WM_GETICON, kind, 0));
    ICONINFO info{};
    if (!icon || !GetIconInfo(icon, &info)) return 0;
    BITMAP bitmap{};
    const auto size = GetObjectW(info.hbmColor ? info.hbmColor : info.hbmMask, sizeof(bitmap), &bitmap) ? bitmap.bmWidth : 0;
    if (info.hbmColor) DeleteObject(info.hbmColor);
    if (info.hbmMask) DeleteObject(info.hbmMask);
    return size;
}

void assert_icon_sizes(HWND hwnd, UINT dpi) {
    assert(icon_size(hwnd, ICON_BIG) == GetSystemMetricsForDpi(SM_CXICON, dpi));
    assert(icon_size(hwnd, ICON_SMALL) == GetSystemMetricsForDpi(SM_CXSMICON, dpi));
}

void test_executable_icon() {
    // The executable of this test has an icon group whose first picture is 16 pixels, and pictures up to 64 pixels.
    auto* app = create_app();
    auto* window = create_window(app);
    const auto hwnd = window_handle(window);
    const auto dpi = GetDpiForWindow(hwnd);
    // A window shows the icon of its executable at the sizes of the scale of its display, not the first picture.
    assert_icon_sizes(hwnd, dpi);
    // It moves to a display of another scale.
    const UINT other = dpi == 192 ? 144 : 192;
    RECT suggested{};
    assert(GetWindowRect(hwnd, &suggested));
    SendMessageW(hwnd, WM_DPICHANGED, MAKELONG(other, other), reinterpret_cast<LPARAM>(&suggested));
    assert_icon_sizes(hwnd, other);
    // An icon that the application gives the window stays through a change of scale.
    const auto own = LoadIconW(nullptr, MAKEINTRESOURCEW(32515)); // IDI_WARNING, shared
    assert(own);
    SendMessageW(hwnd, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(own));
    assert(GetWindowRect(hwnd, &suggested));
    SendMessageW(hwnd, WM_DPICHANGED, MAKELONG(dpi, dpi), reinterpret_cast<LPARAM>(&suggested));
    assert(reinterpret_cast<HICON>(SendMessageW(hwnd, WM_GETICON, ICON_BIG, 0)) == own);
    assert(icon_size(hwnd, ICON_SMALL) == GetSystemMetricsForDpi(SM_CXSMICON, dpi));
    // Taking it away gives the window the icon of its executable back, at the sizes of the current scale.
    SendMessageW(hwnd, WM_SETICON, ICON_BIG, 0);
    assert_icon_sizes(hwnd, dpi);
    neoastra_window_release(window);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

void NEOASTRA_CALL release_window_on_close(void* pointer, const neoastra_event_t* event) {
    auto* context = static_cast<window_callback_context*>(pointer);
    if (event->header.type != NEOASTRA_EVENT_WINDOW_CLOSED || event->object_id != context->id) return;
    ++context->closed;
    neoastra_window_release(context->window);
    context->window = nullptr;
}

void test_window_destroy_callback_release() {
    auto* app = create_app();
    auto* window = create_window(app);
    const auto hwnd = window_handle(window);
    window_callback_context context{app, window, hwnd, neoastra_window_get_id(window)};
    assert(neoastra_app_set_event_callback(app, release_window_on_close, &context) == NEOASTRA_OK);
    // WM_NCDESTROY must not read a stale owner after WM_DESTROY's guard releases.
    assert(DestroyWindow(hwnd));
    assert(context.closed == 1 && context.window == nullptr && !IsWindow(hwnd));
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

void test_zero_reference_window_native_cleanup() {
    auto* app = create_app();
    auto* window = create_window(app);
    const auto hwnd = window_handle(window);
    int events{};
    assert(neoastra_app_set_event_callback(app, count_window_events, &events) == NEOASTRA_OK);
    std::thread worker([window] { neoastra_window_release(window); });
    worker.join();
    // Storage is still queued for UI destruction, but it cannot be retained.
    assert(window->references.load() == 0);
    assert(DestroyWindow(hwnd));
    assert(window->references.load() == 0 && window->closed.load());
    assert(!IsWindow(hwnd) && events == 0);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    assert(app->pending_ui_destructions == nullptr);
    neoastra_app_release(app);
}

struct session_callback_context {
    neoastra_app_t* app{};
    int calls{};
};

void NEOASTRA_CALL release_app_on_session_end(void* pointer, const neoastra_event_t* event) {
    if (event->header.type != NEOASTRA_EVENT_APPLICATION_SESSION_END) return;
    auto* context = static_cast<session_callback_context*>(pointer);
    ++context->calls;
    neoastra_app_release(context->app);
}

void test_session_callback_release() {
    auto* app = create_app();
    const auto dispatcher = dispatcher_handle(app);
    session_callback_context context{app};
    assert(neoastra_app_set_event_callback(app, release_app_on_session_end, &context) == NEOASTRA_OK);
    SendMessageW(dispatcher, WM_ENDSESSION, TRUE, 0);
    // handle_session_message still requests quit after delivering this event.
    assert(context.calls == 1 && !IsWindow(dispatcher));
}

void NEOASTRA_CALL detach_on_window_session_end(void* pointer, const neoastra_event_t* event) {
    if (event->header.type != NEOASTRA_EVENT_APPLICATION_SESSION_END) return;
    auto* context = static_cast<window_callback_context*>(pointer);
    assert(neoastra_app_detach(context->app, nullptr) == NEOASTRA_OK);
    neoastra_window_release(context->window);
    context->window = nullptr;
}

void test_window_session_callback_detach() {
    auto* app = create_app();
    auto* window = create_window(app);
    const auto hwnd = window_handle(window);
    window_callback_context context{app, window, hwnd, neoastra_window_get_id(window)};
    assert(neoastra_app_set_event_callback(app, detach_on_window_session_end, &context) == NEOASTRA_OK);
    SendMessageW(hwnd, WM_ENDSESSION, TRUE, 0);
    assert(context.window == nullptr && !IsWindow(hwnd));
    neoastra_app_release(app);
}

void NEOASTRA_CALL release_dispatched_app(void* pointer) {
    neoastra_app_release(static_cast<neoastra_app_t*>(pointer));
}

void test_dispatcher_callback_release() {
    auto* app = create_app();
    const auto dispatcher = dispatcher_handle(app);
    app->exit_code.store(73);
    assert(neoastra_app_dispatch(app, release_dispatched_app, app) == NEOASTRA_OK);
    // The quit path reads exit_code after draining user callbacks.
    SendMessageW(dispatcher, WM_APP + 0x4f, 0, 0);
    MSG quit{};
    assert(PeekMessageW(&quit, nullptr, WM_QUIT, WM_QUIT, PM_REMOVE));
    assert(quit.wParam == 73 && !IsWindow(dispatcher));
}

constexpr UINT dispatch_message = WM_APP + 0x4e;
constexpr UINT_PTR dispatch_timer = 1;

struct slow_dispatch {
    std::vector<uint32_t>* ran{};
    uint32_t index{};
};

void NEOASTRA_CALL run_slow_dispatch(void* pointer) {
    auto* item = static_cast<slow_dispatch*>(pointer);
    item->ran->push_back(item->index);
    const auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(1);
    while (std::chrono::steady_clock::now() < until) { }
}

// Queues callbacks that take longer together than a slice, then lets the message loop run them. With `paint`, a
// visible window waits to be painted when the first slice ends, so the rest waits for a timer and runs after the paint.
void test_dispatch_drain_returns_to_message_loop(bool paint) {
    auto* app = create_app();
    const auto dispatcher = dispatcher_handle(app);
    HWND painted{};
    if (paint) {
        painted = CreateWindowExW(0, L"STATIC", L"", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 100, 100, 200, 120, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        assert(painted);
        UpdateWindow(painted);
    }
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&message);
    if (paint) InvalidateRect(painted, nullptr, TRUE);

    constexpr uint32_t count = 64;
    std::vector<uint32_t> ran;
    std::vector<slow_dispatch> items(count);
    for (uint32_t index = 0; index < count; ++index) {
        items[index] = {&ran, index};
        assert(neoastra_app_dispatch(app, run_slow_dispatch, &items[index]) == NEOASTRA_OK);
    }
    // One wake-up message stands for the whole queue.
    MSG first{};
    assert(PeekMessageW(&first, dispatcher, dispatch_message, dispatch_message, PM_REMOVE));
    assert(!PeekMessageW(&message, dispatcher, dispatch_message, dispatch_message, PM_NOREMOVE));
    DispatchMessageW(&first);
    assert(!ran.empty() && ran.size() < count);
    const auto first_slice = ran.size();

    uint32_t wakes = 1;
    uint32_t timer_wakes{};
    bool painted_before_next_slice{};
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (ran.size() < count && std::chrono::steady_clock::now() < deadline) {
        if (!PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            MsgWaitForMultipleObjects(0, nullptr, FALSE, 50, QS_ALLINPUT);
            continue;
        }
        if (message.hwnd == dispatcher && (message.message == dispatch_message || (message.message == WM_TIMER && message.wParam == dispatch_timer))) {
            if (message.message == WM_TIMER) ++timer_wakes;
            ++wakes;
        }
        if (paint && message.hwnd == painted && message.message == WM_PAINT && ran.size() == first_slice) painted_before_next_slice = true;
        DispatchMessageW(&message);
    }
    assert(ran.size() == count && wakes > 1);
    for (uint32_t index = 0; index < count; ++index) assert(ran[index] == index);
    if (paint) assert(painted_before_next_slice && timer_wakes != 0);
    // The wake-up left for a nested loop may still come once and find the queue empty; that one arms no further wake-up.
    if (PeekMessageW(&message, dispatcher, dispatch_message, dispatch_message, PM_REMOVE)) DispatchMessageW(&message);
    assert(!PeekMessageW(&message, dispatcher, dispatch_message, dispatch_message, PM_NOREMOVE));
    assert(!KillTimer(dispatcher, dispatch_timer));
    if (painted) DestroyWindow(painted);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

struct nested_dispatch {
    bool ran{};
    bool ran_in_nested_loop{};
};

void NEOASTRA_CALL mark_dispatch(void* pointer) { static_cast<nested_dispatch*>(pointer)->ran = true; }

// Runs a message loop inside a callback, as a menu or a dialog does, until the next queued callback has run.
void NEOASTRA_CALL pump_until_next_dispatch(void* pointer) {
    auto* context = static_cast<nested_dispatch*>(pointer);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    MSG message{};
    while (!context->ran && std::chrono::steady_clock::now() < deadline) {
        if (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&message);
        else MsgWaitForMultipleObjects(0, nullptr, FALSE, 50, QS_ALLINPUT);
    }
    context->ran_in_nested_loop = context->ran;
}

void test_nested_message_loop_runs_queued_dispatch() {
    auto* app = create_app();
    const auto dispatcher = dispatcher_handle(app);
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&message);
    nested_dispatch context;
    assert(neoastra_app_dispatch(app, pump_until_next_dispatch, &context) == NEOASTRA_OK);
    assert(neoastra_app_dispatch(app, mark_dispatch, &context) == NEOASTRA_OK);
    // Both callbacks were queued behind the one wake-up message that this drain consumes.
    assert(PeekMessageW(&message, dispatcher, dispatch_message, dispatch_message, PM_REMOVE));
    DispatchMessageW(&message);
    assert(context.ran_in_nested_loop);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
}

void test_zero_reference_app_native_cleanup() {
    auto* app = create_app();
    const auto dispatcher = dispatcher_handle(app);
    std::thread worker([app] { neoastra_app_release(app); });
    worker.join();
    assert(app->references.load() == 0);
    MSG message{};
    assert(PeekMessageW(&message, dispatcher, WM_APP + 0x50, WM_APP + 0x50, PM_REMOVE));
    DispatchMessageW(&message);
    assert(!IsWindow(dispatcher));
}

neoastra_window_t* create_modal_window(neoastra_app_t* app, neoastra_window_t* owner) {
    neoastra_window_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.bounds = {120, 120, 240, 160};
    options.owner = owner;
    options.flags = 3u | 4u | 128u; // Resizable, decorated, shown, modal.
    neoastra_window_t* window{};
    assert(neoastra_app_create_window(app, &options, &window, nullptr) == NEOASTRA_OK);
    return window;
}

void test_modal_sibling_final_release(bool shutdown) {
    auto* app = create_app();
    auto* owner = create_window(app);
    const auto owner_hwnd = window_handle(owner);
    auto* first = create_modal_window(app, owner);
    auto* second = create_modal_window(app, owner);
    const auto second_hwnd = window_handle(second);
    assert(!IsWindowEnabled(owner_hwnd));
    // Final release calls native teardown and WM_DESTROY synchronously. Those
    // paths must consume the modal activation once, not once each.
    neoastra_window_release(first);
    assert(!IsWindowEnabled(owner_hwnd));
    if (shutdown) {
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        assert(!IsWindow(owner_hwnd) && !IsWindow(second_hwnd));
        neoastra_window_release(second);
    } else {
        neoastra_window_release(second);
        assert(IsWindowEnabled(owner_hwnd));
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    }
    neoastra_window_release(owner);
    neoastra_app_release(app);
}

struct detaching_window_context {
    neoastra_app_t* app{};
    uint64_t id{};
    int calls{};
};

void NEOASTRA_CALL detach_on_resize(void* pointer, const neoastra_event_t* event) {
    auto* context = static_cast<detaching_window_context*>(pointer);
    if (event->header.type != NEOASTRA_EVENT_WINDOW_RESIZED || (context->id && event->object_id != context->id)) return;
    ++context->calls;
    assert(neoastra_app_detach(context->app, nullptr) == NEOASTRA_OK);
}

enum class window_change { bounds, show_minimized, leave_fullscreen };

void test_window_api_callback_detach(window_change change) {
    auto* app = create_app();
    auto* window = create_window(app);
    const auto hwnd = window_handle(window);
    if (change == window_change::show_minimized) assert(neoastra_window_set_state(window, NEOASTRA_WINDOW_MINIMIZED) == NEOASTRA_OK);
    if (change == window_change::leave_fullscreen) {
        assert(neoastra_window_show(window) == NEOASTRA_OK);
        assert(neoastra_window_set_state(window, NEOASTRA_WINDOW_FULLSCREEN) == NEOASTRA_OK);
    }
    detaching_window_context context{app, neoastra_window_get_id(window)};
    assert(neoastra_app_set_event_callback(app, detach_on_resize, &context) == NEOASTRA_OK);
    neoastra_result_t result{};
    switch (change) {
        case window_change::bounds: result = neoastra_window_set_bounds(window, {100, 100, 400, 300}); break;
        case window_change::show_minimized: result = neoastra_window_show(window); break;
        case window_change::leave_fullscreen: result = neoastra_window_set_state(window, NEOASTRA_WINDOW_NORMAL); break;
    }
    // Retaining the native object does not retain its platform state across an
    // explicit detach from a callback nested inside the platform API.
    assert(context.calls == 1 && result == NEOASTRA_ERROR_DISPOSED);
    assert(window->platform == nullptr && !IsWindow(hwnd));
    neoastra_window_release(window);
    neoastra_app_release(app);
}

void test_window_creation_callback_detach() {
    auto* app = create_app();
    detaching_window_context context{app};
    assert(neoastra_app_set_event_callback(app, detach_on_resize, &context) == NEOASTRA_OK);
    neoastra_window_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.bounds = {100, 100, 320, 240};
    options.flags = 3;
    neoastra_window_t* window{};
    neoastra_error_t* error{};
    assert(neoastra_app_create_window(app, &options, &window, &error) == NEOASTRA_ERROR_DISPOSED);
    assert(context.calls == 1 && window == nullptr && error != nullptr);
    neoastra_error_release(error);
    neoastra_app_release(app);
}

} // namespace

int main() {
    test_window_callback_release(false);
    test_window_callback_release(true);
    test_dpi_callback_release();
    test_executable_icon();
    test_window_destroy_callback_release();
    test_zero_reference_window_native_cleanup();
    test_session_callback_release();
    test_window_session_callback_detach();
    test_dispatcher_callback_release();
    test_dispatch_drain_returns_to_message_loop(false);
    test_dispatch_drain_returns_to_message_loop(true);
    test_nested_message_loop_runs_queued_dispatch();
    test_zero_reference_app_native_cleanup();
    test_modal_sibling_final_release(false);
    test_modal_sibling_final_release(true);
    test_window_api_callback_detach(window_change::bounds);
    test_window_api_callback_detach(window_change::show_minimized);
    test_window_api_callback_detach(window_change::leave_fullscreen);
    test_window_creation_callback_detach();
    return 0;
}
