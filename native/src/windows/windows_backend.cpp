#include "../common/native_internal.hpp"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <objbase.h>

#include <memory>

namespace {
constexpr UINT dispatch_message = WM_APP + 0x4e;
constexpr UINT quit_message = WM_APP + 0x4f;
constexpr wchar_t dispatch_class[] = L"NeoWebView.Dispatcher";
constexpr wchar_t window_class[] = L"NeoWebView.Window";

struct windows_app { HWND dispatcher{}; bool owns_com{}; };
struct windows_window { HWND hwnd{}; };

std::wstring widen(const std::string& value) {
    if (value.empty()) return {};
    auto count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) throw std::invalid_argument("invalid UTF-8");
    std::wstring result(static_cast<size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), count);
    return result;
}

LRESULT CALLBACK dispatcher_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
    }
    auto* app = reinterpret_cast<neo_webview_app_t*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == dispatch_message && app) { neo_drain_dispatch(app); return 0; }
    if (message == quit_message && app) { PostQuitMessage(app->exit_code.load()); return 0; }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
        auto* window = static_cast<neo_webview_window_t*>(create->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(window));
    }
    auto* window = reinterpret_cast<neo_webview_window_t*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (!window) return DefWindowProcW(hwnd, message, wparam, lparam);
    switch (message) {
        case WM_CLOSE:
            neo_emit(window->app, window->app->event_callback, window->app->event_context, NEO_WEBVIEW_EVENT_WINDOW_CLOSE_REQUESTED, window->id);
            DestroyWindow(hwnd);
            return 0;
        case WM_DESTROY:
            if (!window->closed) {
                window->closed = true;
                static_cast<windows_window*>(window->platform)->hwnd = nullptr;
                auto remaining = window->app->window_count.fetch_sub(1) - 1;
                neo_emit(window->app, window->app->event_callback, window->app->event_context, NEO_WEBVIEW_EVENT_WINDOW_CLOSED, window->id);
                if (remaining == 0 && window->app->shutdown_mode == NEO_WEBVIEW_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED) neo_webview_app_quit(window->app, 0);
            }
            return 0;
        case WM_MOVE:
            window->bounds.x = static_cast<int16_t>(LOWORD(lparam)); window->bounds.y = static_cast<int16_t>(HIWORD(lparam));
            neo_emit(window->app, window->app->event_callback, window->app->event_context, NEO_WEBVIEW_EVENT_WINDOW_MOVED, window->id);
            break;
        case WM_SIZE:
            window->bounds.width = LOWORD(lparam); window->bounds.height = HIWORD(lparam);
            neo_emit(window->app, window->app->event_callback, window->app->event_context, NEO_WEBVIEW_EVENT_WINDOW_RESIZED, window->id);
            break;
        case WM_SETFOCUS: case WM_KILLFOCUS:
            neo_emit(window->app, window->app->event_callback, window->app->event_context, NEO_WEBVIEW_EVENT_WINDOW_FOCUS_CHANGED, window->id, nullptr, nullptr, message == WM_SETFOCUS ? 1 : 0);
            break;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

ATOM register_class(const wchar_t* name, WNDPROC procedure) {
    WNDCLASSEXW value{sizeof(value)};
    value.lpfnWndProc = procedure;
    value.hInstance = GetModuleHandleW(nullptr);
    value.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    value.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    value.lpszClassName = name;
    auto atom = RegisterClassExW(&value);
    return atom ? atom : (GetLastError() == ERROR_CLASS_ALREADY_EXISTS ? 1 : 0);
}
}

bool neo_platform_initialize(neo_webview_app_t* app, neo_webview_error_t** error) noexcept {
    try {
        auto* state = new windows_app;
        auto hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        if (SUCCEEDED(hr)) state->owns_com = true;
        else if (hr != RPC_E_CHANGED_MODE) { delete state; neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "COM STA initialization failed", hr, "com"); return false; }
        if (!register_class(dispatch_class, dispatcher_proc) || !register_class(window_class, window_proc)) { auto code=GetLastError(); if(state->owns_com)CoUninitialize();delete state;neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"Win32 window class registration failed",code,"win32");return false; }
        state->dispatcher = CreateWindowExW(0, dispatch_class, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, GetModuleHandleW(nullptr), app);
        if (!state->dispatcher) { auto code=GetLastError();if(state->owns_com)CoUninitialize();delete state;neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"Win32 dispatcher creation failed",code,"win32");return false; }
        app->platform = state; return true;
    } catch (...) { neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "Windows backend initialization failed"); return false; }
}

void neo_platform_shutdown(neo_webview_app_t* app) noexcept {
    auto* state = static_cast<windows_app*>(app->platform); if (!state) return;
    if (state->dispatcher && IsWindow(state->dispatcher)) DestroyWindow(state->dispatcher);
    if (state->owns_com && app->ui_thread == std::this_thread::get_id()) CoUninitialize();
    delete state; app->platform = nullptr;
}

int32_t neo_platform_run(neo_webview_app_t* app) noexcept {
    MSG message{};
    while (!app->stopping.load() && GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    app->stopped.store(true); return app->exit_code.load();
}
void neo_platform_quit(neo_webview_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); if(state&&state->dispatcher)PostMessageW(state->dispatcher,quit_message,0,0); }
void neo_platform_wake(neo_webview_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); if(state&&state->dispatcher)PostMessageW(state->dispatcher,dispatch_message,0,0); }

bool neo_platform_window_create(neo_webview_window_t* window, const neo_webview_window_options_t* options, neo_webview_error_t** error) noexcept {
    try {
        auto* state=new windows_window; window->platform=state;
        auto owner=window->owner?static_cast<windows_window*>(window->owner->platform)->hwnd:nullptr;
        auto style=WS_OVERLAPPEDWINDOW; if((options->flags&1u)==0) style&=~WS_THICKFRAME;
        auto title=widen(window->title);
        state->hwnd=CreateWindowExW((options->flags&8u)?WS_EX_TOPMOST:0,window_class,title.c_str(),style,window->bounds.x,window->bounds.y,std::max(window->bounds.width,1),std::max(window->bounds.height,1),owner,nullptr,GetModuleHandleW(nullptr),window);
        if(!state->hwnd){auto code=GetLastError();delete state;window->platform=nullptr;neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"Win32 window creation failed",code,"win32");return false;}
        if(options->flags&4u)ShowWindow(state->hwnd,SW_SHOW);
        return true;
    } catch(const std::exception& ex){neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());return false;}
}
void neo_platform_window_destroy(neo_webview_window_t* window) noexcept { auto* state=static_cast<windows_window*>(window->platform);if(!state)return;if(state->hwnd&&IsWindow(state->hwnd))DestroyWindow(state->hwnd);delete state;window->platform=nullptr; }
neo_webview_result_t neo_platform_window_show(neo_webview_window_t* w,bool visible) noexcept {auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEO_WEBVIEW_ERROR_DISPOSED;ShowWindow(s->hwnd,visible?SW_SHOW:SW_HIDE);return NEO_WEBVIEW_OK;}
neo_webview_result_t neo_platform_window_activate(neo_webview_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);return s&&s->hwnd&&SetForegroundWindow(s->hwnd)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_INVALID_STATE;}
neo_webview_result_t neo_platform_window_close(neo_webview_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);return s&&s->hwnd&&PostMessageW(s->hwnd,WM_CLOSE,0,0)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_DISPOSED;}
neo_webview_result_t neo_platform_window_set_title(neo_webview_window_t* w) noexcept {try{auto* s=static_cast<windows_window*>(w->platform);auto title=widen(w->title);return s&&s->hwnd&&SetWindowTextW(s->hwnd,title.c_str())?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_DISPOSED;}catch(...){return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}}
neo_webview_result_t neo_platform_window_set_bounds(neo_webview_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);return s&&s->hwnd&&SetWindowPos(s->hwnd,nullptr,w->bounds.x,w->bounds.y,w->bounds.width,w->bounds.height,SWP_NOZORDER|SWP_NOACTIVATE)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_DISPOSED;}
neo_webview_result_t neo_platform_window_get_handle(neo_webview_window_t* w,neo_webview_native_handle_kind_t kind,neo_webview_native_handle_t* h) noexcept {if(kind!=NEO_WEBVIEW_NATIVE_HANDLE_WIN32_HWND)return NEO_WEBVIEW_ERROR_NOT_SUPPORTED;auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEO_WEBVIEW_ERROR_DISPOSED;h->kind=kind;h->value=s->hwnd;return NEO_WEBVIEW_OK;}

bool neo_platform_environment_create(neo_webview_environment_t*,const neo_webview_environment_options_t*,neo_webview_error_t**) noexcept{return true;}
void neo_platform_environment_destroy(neo_webview_environment_t*) noexcept{}
bool neo_platform_view_create(neo_webview_view_t*,const neo_webview_view_options_t*,neo_webview_error_t** error) noexcept{neo_fail(error,NEO_WEBVIEW_ERROR_RUNTIME_UNAVAILABLE,"WebView2 backend is not initialized",0,"webview2");return false;}
void neo_platform_view_destroy(neo_webview_view_t*) noexcept{}
neo_webview_result_t neo_platform_view_set_bounds(neo_webview_view_t*) noexcept{return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;}
neo_webview_result_t neo_platform_view_navigate(neo_webview_view_t*,const std::string&,neo_webview_error_t** e) noexcept{return neo_fail(e,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");}
neo_webview_result_t neo_platform_view_load_html(neo_webview_view_t*,const std::string&,const std::string&,neo_webview_error_t** e) noexcept{return neo_fail(e,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");}
neo_webview_result_t neo_platform_view_command(neo_webview_view_t*,uint32_t) noexcept{return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;}
neo_webview_result_t neo_platform_view_evaluate(neo_webview_view_t*,const std::string&,neo_webview_string_callback_t,void*,neo_webview_operation_t*,neo_webview_error_t** e) noexcept{return neo_fail(e,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");}
neo_webview_result_t neo_platform_view_post_message(neo_webview_view_t*,const std::string&,bool,neo_webview_error_t** e) noexcept{return neo_fail(e,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");}
neo_webview_result_t neo_platform_view_get_handle(neo_webview_view_t*,neo_webview_native_handle_kind_t,neo_webview_native_handle_t*) noexcept{return NEO_WEBVIEW_ERROR_NOT_SUPPORTED;}
