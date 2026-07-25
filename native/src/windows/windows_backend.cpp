#include "../common/native_internal.hpp"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <objbase.h>
#include <shellapi.h>
#include <wrl.h>
#include <WebView2.h>
#include <WebView2EnvironmentOptions.h>

#include <algorithm>
#include <cstdio>
#include <memory>
#include <string>
#include <vector>

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Make;

namespace {
constexpr UINT dispatch_message = WM_APP + 0x4e;
constexpr UINT quit_message = WM_APP + 0x4f;
constexpr wchar_t dispatch_class[] = L"NeoWebView.Dispatcher";
constexpr wchar_t window_class[] = L"NeoWebView.Window";

struct windows_app { HWND dispatcher{}; bool owns_com{}; };
struct windows_window {
    HWND hwnd{};
    bool fullscreen{};
    DWORD restored_style{};
    WINDOWPLACEMENT restored_placement{};
    neo_webview_window_state_t reported_state{NEO_WEBVIEW_WINDOW_NORMAL};
    windows_window() { restored_placement.length = sizeof(restored_placement); }
};
struct windows_environment { ComPtr<ICoreWebView2Environment> value; std::string version; };
struct windows_profile { ComPtr<ICoreWebView2CookieManager> cookies; ComPtr<ICoreWebView2Profile> profile; };
struct windows_view {
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> core;
    EventRegistrationToken navigation_starting{};
    EventRegistrationToken navigation_completed{};
    EventRegistrationToken source_changed{};
    EventRegistrationToken title_changed{};
    EventRegistrationToken history_changed{};
    EventRegistrationToken message_received{};
    EventRegistrationToken permission_requested{};
    bool events_registered{};
};

std::wstring widen(const std::string& value) {
    if (value.empty()) return {};
    if (value.size() > static_cast<size_t>(INT_MAX)) throw std::invalid_argument("UTF-8 string is too long");
    const auto count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) throw std::invalid_argument("invalid UTF-8");
    std::wstring result(static_cast<size_t>(count), L'\0');
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), count)) throw std::invalid_argument("invalid UTF-8");
    return result;
}

std::string narrow(const wchar_t* value) {
    if (!value || !*value) return {};
    const auto length = static_cast<int>(wcslen(value));
    const auto count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, length, nullptr, 0, nullptr, nullptr);
    if (count <= 0) return {};
    std::string result(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, length, result.data(), count, nullptr, nullptr);
    return result;
}

std::string take_string(LPWSTR value) {
    const auto result = narrow(value);
    CoTaskMemFree(value);
    return result;
}

void append_json_string(std::string& output, const std::string& value) {
    output.push_back('"');
    for (const auto byte : value) {
        const auto character = static_cast<unsigned char>(byte);
        switch (character) {
            case '"': output += "\\\""; break;
            case '\\': output += "\\\\"; break;
            case '\b': output += "\\b"; break;
            case '\f': output += "\\f"; break;
            case '\n': output += "\\n"; break;
            case '\r': output += "\\r"; break;
            case '\t': output += "\\t"; break;
            default:
                if (character < 0x20) {
                    char escape[7]{};
                    std::snprintf(escape, sizeof(escape), "\\u%04x", character);
                    output += escape;
                } else output.push_back(static_cast<char>(character));
                break;
        }
    }
    output.push_back('"');
}

neo_webview_error_t* make_error(neo_webview_result_t code, const char* message, HRESULT native_code, const char* domain = "webview2") noexcept {
    neo_webview_error_t* error{};
    neo_fail(&error, code, message, native_code, domain);
    return error;
}

HWND view_parent(const neo_webview_view_t* view) noexcept {
    if (view->window) {
        const auto* state = static_cast<windows_window*>(view->window->platform);
        return state ? state->hwnd : nullptr;
    }
    return view->parent.kind == NEO_WEBVIEW_NATIVE_PARENT_WIN32_HWND ? static_cast<HWND>(view->parent.handle) : nullptr;
}

RECT view_bounds(const neo_webview_view_t* view) noexcept {
    RECT bounds{};
    const auto parent = view_parent(view);
    if (view->fill_parent && parent) GetClientRect(parent, &bounds);
    else {
        bounds.left = view->bounds.x;
        bounds.top = view->bounds.y;
        bounds.right = view->bounds.x + std::max(view->bounds.width, 1);
        bounds.bottom = view->bounds.y + std::max(view->bounds.height, 1);
    }
    return bounds;
}

void remove_view_events(windows_view* state) noexcept {
    if (!state || !state->core || !state->events_registered) return;
    state->core->remove_NavigationStarting(state->navigation_starting);
    state->core->remove_NavigationCompleted(state->navigation_completed);
    state->core->remove_SourceChanged(state->source_changed);
    state->core->remove_DocumentTitleChanged(state->title_changed);
    state->core->remove_HistoryChanged(state->history_changed);
    state->core->remove_WebMessageReceived(state->message_received);
    state->core->remove_PermissionRequested(state->permission_requested);
    state->events_registered = false;
}

struct navigation_decision_context {
    ComPtr<ICoreWebView2NavigationStartingEventArgs> args;
};

neo_webview_permission_kind_t portable_permission(COREWEBVIEW2_PERMISSION_KIND kind) noexcept {
    switch (kind) {
        case COREWEBVIEW2_PERMISSION_KIND_GEOLOCATION: return NEO_WEBVIEW_PERMISSION_GEOLOCATION;
        case COREWEBVIEW2_PERMISSION_KIND_CAMERA: return NEO_WEBVIEW_PERMISSION_CAMERA;
        case COREWEBVIEW2_PERMISSION_KIND_MICROPHONE: return NEO_WEBVIEW_PERMISSION_MICROPHONE;
        case COREWEBVIEW2_PERMISSION_KIND_NOTIFICATIONS: return NEO_WEBVIEW_PERMISSION_NOTIFICATIONS;
        case COREWEBVIEW2_PERMISSION_KIND_CLIPBOARD_READ: return NEO_WEBVIEW_PERMISSION_CLIPBOARD_READ;
        case COREWEBVIEW2_PERMISSION_KIND_MIDI_SYSTEM_EXCLUSIVE_MESSAGES: return NEO_WEBVIEW_PERMISSION_MIDI;
        case COREWEBVIEW2_PERMISSION_KIND_LOCAL_FONTS: return NEO_WEBVIEW_PERMISSION_LOCAL_FONTS;
        case COREWEBVIEW2_PERMISSION_KIND_FILE_READ_WRITE: return NEO_WEBVIEW_PERMISSION_FILE_SYSTEM;
        default: return NEO_WEBVIEW_PERMISSION_UNKNOWN;
    }
}

struct permission_decision_context {
    ComPtr<ICoreWebView2PermissionRequestedEventArgs> args;
    ComPtr<ICoreWebView2Deferral> deferral;
};

void permission_decided(void* pointer, const neo_webview_decision_response_t* response) noexcept {
    std::unique_ptr<permission_decision_context> context(static_cast<permission_decision_context*>(pointer));
    const auto state = response->action == NEO_WEBVIEW_DECISION_ALLOW ? COREWEBVIEW2_PERMISSION_STATE_ALLOW
                     : response->action == NEO_WEBVIEW_DECISION_DEFAULT ? COREWEBVIEW2_PERMISSION_STATE_DEFAULT
                     : COREWEBVIEW2_PERMISSION_STATE_DENY;
    ComPtr<ICoreWebView2PermissionRequestedEventArgs3> args3;
    if (SUCCEEDED(context->args.As(&args3))) args3->put_SavesInProfile(response->persist ? TRUE : FALSE);
    context->args->put_State(state);
    context->deferral->Complete();
}

void navigation_decided(void* pointer, const neo_webview_decision_response_t* response) noexcept {
    std::unique_ptr<navigation_decision_context> context(static_cast<navigation_decision_context*>(pointer));
    const bool cancel = response->action != NEO_WEBVIEW_DECISION_ALLOW;
    context->args->put_Cancel(cancel ? TRUE : FALSE);
}

HRESULT register_view_events(neo_webview_view_t* view, windows_view* state) {
    state->events_registered = true;
    HRESULT result = state->core->add_NavigationStarting(
        Callback<ICoreWebView2NavigationStartingEventHandler>([view](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
            LPWSTR raw_uri{};
            BOOL user_initiated{};
            args->get_Uri(&raw_uri);
            args->get_IsUserInitiated(&user_initiated);
            auto uri = take_string(raw_uri);

            auto context = std::make_unique<navigation_decision_context>();
            context->args = args;
            auto* decision = new neo_webview_decision;
            decision->kind = NEO_WEBVIEW_DECISION_NAVIGATION;
            decision->default_action = NEO_WEBVIEW_DECISION_ALLOW;
            decision->completion = navigation_decided;
            decision->completion_context = context.release();
            neo_emit_view(view, NEO_WEBVIEW_EVENT_NAVIGATION_REQUESTED, 0, nullptr, &uri,
                          1u | (user_initiated ? 2u : 0u), 0, decision);
            const auto decision_state = decision->state.load(std::memory_order_acquire);
            // NavigationStarting has no WebView2 deferral API. A managed handler may
            // defer the portable decision, but WebView2 requires the final Cancel
            // value before this callback returns, so apply the safe default here.
            if (decision_state == neo_decision_state::pending || decision_state == neo_decision_state::deferred) {
                neo_webview_decision_response_t response{};
                response.size = sizeof(response);
                response.version = 1;
                response.action = NEO_WEBVIEW_DECISION_DEFAULT;
                neo_webview_decision_complete(decision, &response, nullptr);
            }
            const auto allowed = decision->resolved_action.load(std::memory_order_acquire) == NEO_WEBVIEW_DECISION_ALLOW;
            decision->release();
            if (allowed) neo_emit_view(view, NEO_WEBVIEW_EVENT_NAVIGATION_STARTED, 0, nullptr, &uri, 1);
            return S_OK;
        }).Get(), &state->navigation_starting);
    if (FAILED(result)) return result;

    result = state->core->add_NavigationCompleted(
        Callback<ICoreWebView2NavigationCompletedEventHandler>([view](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT {
            BOOL success{};
            COREWEBVIEW2_WEB_ERROR_STATUS status{};
            UINT64 navigation_id{};
            args->get_IsSuccess(&success);
            args->get_WebErrorStatus(&status);
            args->get_NavigationId(&navigation_id);
            neo_emit_view(view, success ? NEO_WEBVIEW_EVENT_NAVIGATION_COMPLETED : NEO_WEBVIEW_EVENT_NAVIGATION_FAILED,
                          0, nullptr, &view->source, success ? 0 : static_cast<uint64_t>(NEO_WEBVIEW_ERROR_NATIVE_FAILURE),
                          static_cast<int64_t>(status));
            return S_OK;
        }).Get(), &state->navigation_completed);
    if (FAILED(result)) return result;

    result = state->core->add_SourceChanged(
        Callback<ICoreWebView2SourceChangedEventHandler>([view](ICoreWebView2* core, ICoreWebView2SourceChangedEventArgs*) -> HRESULT {
            LPWSTR source{};
            if (SUCCEEDED(core->get_Source(&source))) {
                view->source = take_string(source);
                neo_emit_view(view, NEO_WEBVIEW_EVENT_SOURCE_CHANGED, 0, nullptr, &view->source);
            }
            return S_OK;
        }).Get(), &state->source_changed);
    if (FAILED(result)) return result;

    result = state->core->add_DocumentTitleChanged(
        Callback<ICoreWebView2DocumentTitleChangedEventHandler>([view](ICoreWebView2* core, IUnknown*) -> HRESULT {
            LPWSTR title{};
            if (SUCCEEDED(core->get_DocumentTitle(&title))) {
                view->title = take_string(title);
                neo_emit_view(view, NEO_WEBVIEW_EVENT_TITLE_CHANGED, 0, &view->title);
            }
            return S_OK;
        }).Get(), &state->title_changed);
    if (FAILED(result)) return result;

    result = state->core->add_HistoryChanged(
        Callback<ICoreWebView2HistoryChangedEventHandler>([view](ICoreWebView2* core, IUnknown*) -> HRESULT {
            BOOL back{}, forward{};
            core->get_CanGoBack(&back);
            core->get_CanGoForward(&forward);
            neo_emit_view(view, NEO_WEBVIEW_EVENT_HISTORY_CHANGED, 0, nullptr, nullptr, (back ? 1u : 0u) | (forward ? 2u : 0u));
            return S_OK;
        }).Get(), &state->history_changed);
    if (FAILED(result)) return result;

    result = state->core->add_WebMessageReceived(
        Callback<ICoreWebView2WebMessageReceivedEventHandler>([view](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) -> HRESULT {
            LPWSTR source{};
            LPWSTR message{};
            args->get_Source(&source);
            HRESULT message_result = args->TryGetWebMessageAsString(&message);
            uint64_t flags{};
            if (FAILED(message_result)) {
                args->get_WebMessageAsJson(&message);
                flags = 1;
            }
            auto source_utf8 = take_string(source);
            auto message_utf8 = take_string(message);
            neo_emit_view(view, NEO_WEBVIEW_EVENT_MESSAGE_RECEIVED, 0, &message_utf8, &source_utf8, flags);
            return S_OK;
        }).Get(), &state->message_received);
    if (FAILED(result)) return result;

    result = state->core->add_PermissionRequested(
        Callback<ICoreWebView2PermissionRequestedEventHandler>([view](ICoreWebView2*, ICoreWebView2PermissionRequestedEventArgs* args) -> HRESULT {
            LPWSTR raw_uri{};
            COREWEBVIEW2_PERMISSION_KIND kind{};
            BOOL user_initiated{};
            ComPtr<ICoreWebView2Deferral> deferral;
            auto result = args->get_Uri(&raw_uri);
            if (SUCCEEDED(result)) result = args->get_PermissionKind(&kind);
            if (SUCCEEDED(result)) result = args->get_IsUserInitiated(&user_initiated);
            if (SUCCEEDED(result)) result = args->GetDeferral(&deferral);
            if (FAILED(result)) return result;
            try {
                auto uri = take_string(raw_uri);
                raw_uri = nullptr;
                auto context = std::make_unique<permission_decision_context>();
                context->args = args;
                context->deferral = deferral;
                auto* decision = new neo_webview_decision;
                decision->kind = NEO_WEBVIEW_DECISION_PERMISSION;
                decision->default_action = NEO_WEBVIEW_DECISION_DENY;
                decision->completion = permission_decided;
                decision->completion_context = context.release();
                neo_emit_view(view, NEO_WEBVIEW_EVENT_PERMISSION_REQUESTED, 0, nullptr, &uri,
                              portable_permission(kind), user_initiated ? 1 : 0, decision);
                if (decision->state.load(std::memory_order_acquire) == neo_decision_state::pending) {
                    neo_webview_decision_response_t response{};
                    response.size = sizeof(response);
                    response.version = 1;
                    response.action = NEO_WEBVIEW_DECISION_DEFAULT;
                    neo_webview_decision_complete(decision, &response, nullptr);
                }
                decision->release();
                return S_OK;
            } catch (...) {
                CoTaskMemFree(raw_uri);
                args->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY);
                deferral->Complete();
                return S_OK;
            }
        }).Get(), &state->permission_requested);
    return result;
}

LRESULT CALLBACK dispatcher_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
    }
    auto* app = reinterpret_cast<neo_webview_app_t*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == dispatch_message && app) { neo_drain_dispatch(app); return 0; }
    if (message == quit_message && app) { neo_drain_dispatch(app); PostQuitMessage(app->exit_code.load()); return 0; }
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
            neo_emit_app(window->app, NEO_WEBVIEW_EVENT_WINDOW_CLOSE_REQUESTED, window->id);
            DestroyWindow(hwnd);
            return 0;
        case WM_DESTROY:
            static_cast<windows_window*>(window->platform)->hwnd = nullptr;
            neo_window_closed(window);
            return 0;
        case WM_MOVE:
            { std::lock_guard lock(window->state_mutex); window->bounds.x = static_cast<int16_t>(LOWORD(lparam)); window->bounds.y = static_cast<int16_t>(HIWORD(lparam)); }
            neo_emit_app(window->app, NEO_WEBVIEW_EVENT_WINDOW_MOVED, window->id);
            break;
        case WM_SIZE: {
            auto* native = static_cast<windows_window*>(window->platform);
            const auto state = native && native->fullscreen ? NEO_WEBVIEW_WINDOW_FULLSCREEN
                             : wparam == SIZE_MINIMIZED ? NEO_WEBVIEW_WINDOW_MINIMIZED
                             : wparam == SIZE_MAXIMIZED ? NEO_WEBVIEW_WINDOW_MAXIMIZED : NEO_WEBVIEW_WINDOW_NORMAL;
            const auto state_changed = native && native->reported_state != state;
            if (native) native->reported_state = state;
            { std::lock_guard lock(window->state_mutex); window->bounds.width = LOWORD(lparam); window->bounds.height = HIWORD(lparam);window->state=state; }
            for (auto* view : window->views) if (view && view->fill_parent) neo_platform_view_set_bounds(view);
            neo_emit_app(window->app, NEO_WEBVIEW_EVENT_WINDOW_RESIZED, window->id);
            if (state_changed) neo_emit_app(window->app, NEO_WEBVIEW_EVENT_WINDOW_STATE_CHANGED, window->id, nullptr, nullptr, state);
            break;
        }
        case WM_DPICHANGED: {
            const auto* suggested = reinterpret_cast<const RECT*>(lparam);
            SetWindowPos(hwnd, nullptr, suggested->left, suggested->top, suggested->right - suggested->left,
                         suggested->bottom - suggested->top, SWP_NOACTIVATE | SWP_NOZORDER);
            const auto dpi = HIWORD(wparam);
            neo_emit_app(window->app, NEO_WEBVIEW_EVENT_WINDOW_SCALE_FACTOR_CHANGED, window->id, nullptr, nullptr,
                         static_cast<uint64_t>(dpi) * 1000u / 96u);
            return 0;
        }
        case WM_GETMINMAXINFO: {
            auto* constraints = reinterpret_cast<MINMAXINFO*>(lparam);
            std::lock_guard lock(window->state_mutex);
            if (window->minimum_size.width > 0) constraints->ptMinTrackSize.x = window->minimum_size.width;
            if (window->minimum_size.height > 0) constraints->ptMinTrackSize.y = window->minimum_size.height;
            if (window->maximum_size.width > 0) constraints->ptMaxTrackSize.x = window->maximum_size.width;
            if (window->maximum_size.height > 0) constraints->ptMaxTrackSize.y = window->maximum_size.height;
            return 0;
        }
        case WM_SETFOCUS: case WM_KILLFOCUS:
            neo_emit_app(window->app, NEO_WEBVIEW_EVENT_WINDOW_FOCUS_CHANGED, window->id, nullptr, nullptr, message == WM_SETFOCUS ? 1 : 0);
            break;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

ATOM register_class(const wchar_t* name, WNDPROC procedure) {
    WNDCLASSEXW value{};
    value.cbSize = sizeof(value);
    value.lpfnWndProc = procedure;
    value.hInstance = GetModuleHandleW(nullptr);
    value.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    value.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    value.lpszClassName = name;
    const auto atom = RegisterClassExW(&value);
    return atom ? atom : (GetLastError() == ERROR_CLASS_ALREADY_EXISTS ? 1 : 0);
}

struct script_completion {
    neo_webview_string_callback_t callback{};
    void* context{};
    neo_webview_operation_t* operation{};
    std::string result;
    neo_webview_result_t requested{};
    neo_webview_error_t* error{};
};

struct profile_completion {
    neo_webview_completion_callback_t callback{};
    void* context{};
    neo_webview_operation_t* operation{};
    neo_webview_result_t requested{};
    neo_webview_error_t* error{};
};

void NEO_WEBVIEW_CALL finish_profile_operation(void* pointer) {
    std::unique_ptr<profile_completion> completion(static_cast<profile_completion*>(pointer));
    neo_webview_result_t result{};
    if (completion->operation->try_complete(completion->requested, result)) {
        completion->callback(completion->context, result, result == completion->requested ? completion->error : nullptr);
    }
    if (completion->error) completion->error->release();
    completion->operation->release();
}

neo_webview_result_t schedule_profile_completion(neo_webview_profile_t* profile, neo_webview_completion_callback_t callback,
                                                 void* context, neo_webview_operation_t* operation,
                                                 neo_webview_result_t requested, neo_webview_error_t* error,
                                                 neo_webview_error_t** start_error) noexcept {
    auto completion = std::make_unique<profile_completion>(profile_completion{callback, context, operation, requested, error});
    const auto result = neo_webview_app_dispatch(profile->environment->app, finish_profile_operation, completion.get());
    if (result != NEO_WEBVIEW_OK) {
        if (error) error->release();
        return neo_fail(start_error, result, "Could not schedule WebView2 profile completion");
    }
    completion.release();
    return NEO_WEBVIEW_OK;
}

void complete_cookie_buffer(neo_webview_buffer_callback_t callback, void* context, neo_webview_operation_t* operation,
                            neo_webview_result_t requested, neo_webview_buffer_t* buffer,
                            neo_webview_error_t* error) noexcept {
    neo_webview_result_t result{};
    if (operation->try_complete(requested, result)) {
        callback(context, result, result == NEO_WEBVIEW_OK ? buffer : nullptr, result == requested ? error : nullptr);
        if (result != NEO_WEBVIEW_OK && buffer) buffer->release();
    } else if (buffer) buffer->release();
    if (error) error->release();
    operation->release();
}

windows_profile* require_profile(neo_webview_profile_t* profile, neo_webview_error_t** error) noexcept {
    auto* state = static_cast<windows_profile*>(profile->platform);
    if (!state || !state->cookies || !state->profile) {
        neo_fail(error, NEO_WEBVIEW_ERROR_NOT_INITIALIZED, "The WebView2 profile is not initialized; create a view for the profile first", 0, "webview2");
        return nullptr;
    }
    return state;
}

struct cookie_delete_state {
    ComPtr<ICoreWebView2CookieManager> manager;
    std::string name;
    std::string domain;
    std::string path;
    std::string uri;
    neo_webview_completion_callback_t callback{};
    void* context{};
    neo_webview_operation_t* operation{};
    uint32_t attempts{};
};

void complete_cookie_delete(const std::shared_ptr<cookie_delete_state>& state, HRESULT result) noexcept {
    const auto requested = SUCCEEDED(result) ? NEO_WEBVIEW_OK : NEO_WEBVIEW_ERROR_NATIVE_FAILURE;
    auto* error = FAILED(result) ? make_error(requested, "Could not delete WebView2 cookie", result) : nullptr;
    neo_webview_result_t actual{};
    if (state->operation->try_complete(requested, actual)) state->callback(state->context, actual, actual == requested ? error : nullptr);
    if (error) error->release();
    state->operation->release();
}

HRESULT begin_cookie_delete_query(const std::shared_ptr<cookie_delete_state>& state) {
    return state->manager->GetCookies(
        widen(state->uri).c_str(),
        Callback<ICoreWebView2GetCookiesCompletedHandler>([state](HRESULT result, ICoreWebView2CookieList* list) -> HRESULT {
            bool found = false;
            if (SUCCEEDED(result) && list) {
                UINT32 count{};
                result = list->get_Count(&count);
                for (UINT32 index = 0; SUCCEEDED(result) && index < count; ++index) {
                    ComPtr<ICoreWebView2Cookie> current;
                    result = list->GetValueAtIndex(index, &current);
                    LPWSTR raw_name{}, raw_domain{}, raw_path{};
                    if (SUCCEEDED(result)) result = current->get_Name(&raw_name);
                    if (SUCCEEDED(result)) result = current->get_Domain(&raw_domain);
                    if (SUCCEEDED(result)) result = current->get_Path(&raw_path);
                    const auto name = take_string(raw_name);
                    const auto domain = take_string(raw_domain);
                    const auto path = take_string(raw_path);
                    if (SUCCEEDED(result) && name == state->name && domain == state->domain && path == state->path) {
                        found = true;
                        result = state->manager->DeleteCookie(current.Get());
                    }
                }
                if (SUCCEEDED(result) && found) result = state->manager->DeleteCookies(widen(state->name).c_str(), widen(state->uri).c_str());
            }
            if (FAILED(result) || !found) complete_cookie_delete(state, result);
            else if (++state->attempts >= 16) complete_cookie_delete(state, HRESULT_FROM_WIN32(ERROR_TIMEOUT));
            else {
                const auto retry = begin_cookie_delete_query(state);
                if (FAILED(retry)) complete_cookie_delete(state, retry);
            }
            return S_OK;
        }).Get());
}

void NEO_WEBVIEW_CALL finish_script(void* pointer) {
    std::unique_ptr<script_completion> completion(static_cast<script_completion*>(pointer));
    neo_webview_result_t result{};
    if (completion->operation->try_complete(completion->requested, result)) {
        completion->callback(completion->context, result, result == NEO_WEBVIEW_OK ? neo_string_view(completion->result) : neo_webview_string_view_t{}, completion->error);
    }
    if (completion->error) completion->error->release();
    completion->operation->release();
}

} // namespace

bool neo_platform_initialize(neo_webview_app_t* app, neo_webview_error_t** error) noexcept {
    try {
        auto* state = new windows_app;
        const auto hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        if (SUCCEEDED(hr)) state->owns_com = true;
        else if (hr != S_FALSE) { delete state; neo_fail(error, hr == RPC_E_CHANGED_MODE ? NEO_WEBVIEW_ERROR_WRONG_THREAD : NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "COM STA initialization failed", hr, "com"); return false; }
        if (!register_class(dispatch_class, dispatcher_proc) || !register_class(window_class, window_proc)) { const auto code=GetLastError(); if(state->owns_com)CoUninitialize();delete state;neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"Win32 window class registration failed",code,"win32");return false; }
        state->dispatcher = CreateWindowExW(0, dispatch_class, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, GetModuleHandleW(nullptr), app);
        if (!state->dispatcher) { const auto code=GetLastError();if(state->owns_com)CoUninitialize();delete state;neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"Win32 dispatcher creation failed",code,"win32");return false; }
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
    while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return app->exit_code.load();
}
void neo_platform_quit(neo_webview_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); if(state&&state->dispatcher)PostMessageW(state->dispatcher,quit_message,0,0); }
void neo_platform_wake(neo_webview_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); if(state&&state->dispatcher)PostMessageW(state->dispatcher,dispatch_message,0,0); }

bool neo_platform_window_create(neo_webview_window_t* window, const neo_webview_window_options_t* options, neo_webview_error_t** error) noexcept {
    try {
        auto* state=new windows_window; window->platform=state;
        const auto owner=window->owner?static_cast<windows_window*>(window->owner->platform)->hwnd:nullptr;
        auto style=WS_OVERLAPPEDWINDOW; if((options->flags&1u)==0) style&=~WS_THICKFRAME;
        if ((options->flags & 2u) == 0) style = WS_POPUP;
        const auto title=widen(window->title);
        DWORD extended=(options->flags&8u)?WS_EX_TOPMOST:0;if((options->flags&16u)==0)extended|=WS_EX_TOOLWINDOW;
        state->hwnd=CreateWindowExW(extended,window_class,title.c_str(),style,window->bounds.x,window->bounds.y,std::max(window->bounds.width,1),std::max(window->bounds.height,1),owner,nullptr,GetModuleHandleW(nullptr),window);
        if(!state->hwnd){const auto code=GetLastError();delete state;window->platform=nullptr;neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"Win32 window creation failed",code,"win32");return false;}
        if(options->flags&4u){const auto show=options->state==NEO_WEBVIEW_WINDOW_MINIMIZED?SW_SHOWMINIMIZED:options->state==NEO_WEBVIEW_WINDOW_MAXIMIZED?SW_SHOWMAXIMIZED:SW_SHOW;ShowWindow(state->hwnd,show);if(options->state==NEO_WEBVIEW_WINDOW_FULLSCREEN){{std::lock_guard lock(window->state_mutex);window->state=NEO_WEBVIEW_WINDOW_FULLSCREEN;}neo_platform_window_set_state(window);}}
        return true;
    } catch(const std::exception& ex){neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,ex.what());return false;}
}
void neo_platform_window_destroy(neo_webview_window_t* window) noexcept { auto* state=static_cast<windows_window*>(window->platform);if(!state)return;if(state->hwnd&&IsWindow(state->hwnd))DestroyWindow(state->hwnd);delete state;window->platform=nullptr; }
neo_webview_result_t neo_platform_window_show(neo_webview_window_t* w,bool visible) noexcept {auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEO_WEBVIEW_ERROR_DISPOSED;if(!visible){ShowWindow(s->hwnd,SW_HIDE);return NEO_WEBVIEW_OK;}neo_webview_window_state_t desired{};{std::lock_guard lock(w->state_mutex);desired=w->state;}const auto command=desired==NEO_WEBVIEW_WINDOW_MINIMIZED?SW_SHOWMINIMIZED:desired==NEO_WEBVIEW_WINDOW_MAXIMIZED?SW_SHOWMAXIMIZED:SW_SHOW;ShowWindow(s->hwnd,command);if(desired==NEO_WEBVIEW_WINDOW_FULLSCREEN){{std::lock_guard lock(w->state_mutex);w->state=desired;}return neo_platform_window_set_state(w);}return NEO_WEBVIEW_OK;}
neo_webview_result_t neo_platform_window_activate(neo_webview_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);return s&&s->hwnd&&SetForegroundWindow(s->hwnd)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_INVALID_STATE;}
neo_webview_result_t neo_platform_window_close(neo_webview_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);return s&&s->hwnd&&PostMessageW(s->hwnd,WM_CLOSE,0,0)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_DISPOSED;}
neo_webview_result_t neo_platform_window_set_title(neo_webview_window_t* w) noexcept {try{auto* s=static_cast<windows_window*>(w->platform);auto title=widen(w->title);return s&&s->hwnd&&SetWindowTextW(s->hwnd,title.c_str())?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_DISPOSED;}catch(...){return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}}
neo_webview_result_t neo_platform_window_set_bounds(neo_webview_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);return s&&s->hwnd&&SetWindowPos(s->hwnd,nullptr,w->bounds.x,w->bounds.y,w->bounds.width,w->bounds.height,SWP_NOZORDER|SWP_NOACTIVATE)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_DISPOSED;}
neo_webview_result_t neo_platform_window_set_size_constraints(neo_webview_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEO_WEBVIEW_ERROR_DISPOSED;SetWindowPos(s->hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);return NEO_WEBVIEW_OK;}
neo_webview_result_t neo_platform_window_set_state(neo_webview_window_t* w) noexcept {
    auto* state = static_cast<windows_window*>(w->platform);
    if (!state || !state->hwnd) return NEO_WEBVIEW_ERROR_DISPOSED;
    const auto visible = IsWindowVisible(state->hwnd) != FALSE;
    if (w->state == NEO_WEBVIEW_WINDOW_FULLSCREEN && !state->fullscreen) {
        if (!visible) return NEO_WEBVIEW_OK;
        state->restored_style = static_cast<DWORD>(GetWindowLongW(state->hwnd, GWL_STYLE));
        state->restored_placement.length = sizeof(WINDOWPLACEMENT);
        GetWindowPlacement(state->hwnd, &state->restored_placement);
        MONITORINFO monitor{};
        monitor.cbSize = sizeof(monitor);
        if (!GetMonitorInfoW(MonitorFromWindow(state->hwnd, MONITOR_DEFAULTTONEAREST), &monitor)) return NEO_WEBVIEW_ERROR_NATIVE_FAILURE;
        SetWindowLongW(state->hwnd, GWL_STYLE, static_cast<LONG>(state->restored_style & ~WS_OVERLAPPEDWINDOW));
        state->fullscreen = true;
        if (SetWindowPos(state->hwnd, HWND_TOP, monitor.rcMonitor.left, monitor.rcMonitor.top,
                         monitor.rcMonitor.right - monitor.rcMonitor.left, monitor.rcMonitor.bottom - monitor.rcMonitor.top,
                         SWP_NOOWNERZORDER | SWP_FRAMECHANGED)) return NEO_WEBVIEW_OK;
        SetWindowLongW(state->hwnd, GWL_STYLE, static_cast<LONG>(state->restored_style));
        state->fullscreen = false;
        return NEO_WEBVIEW_ERROR_NATIVE_FAILURE;
    }
    if (state->fullscreen) {
        SetWindowLongW(state->hwnd, GWL_STYLE, static_cast<LONG>(state->restored_style));
        SetWindowPlacement(state->hwnd, &state->restored_placement);
        SetWindowPos(state->hwnd, nullptr, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        state->fullscreen = false;
    }
    if (!visible) return NEO_WEBVIEW_OK;
    const auto command = w->state == NEO_WEBVIEW_WINDOW_MINIMIZED ? SW_MINIMIZE
                       : w->state == NEO_WEBVIEW_WINDOW_MAXIMIZED ? SW_MAXIMIZE : SW_RESTORE;
    ShowWindow(state->hwnd, command);
    return NEO_WEBVIEW_OK;
}
neo_webview_result_t neo_platform_window_get_handle(neo_webview_window_t* w,neo_webview_native_handle_kind_t kind,neo_webview_native_handle_t* h) noexcept {if(kind!=NEO_WEBVIEW_NATIVE_HANDLE_WIN32_HWND)return NEO_WEBVIEW_ERROR_NOT_SUPPORTED;auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEO_WEBVIEW_ERROR_DISPOSED;h->kind=kind;h->value=s->hwnd;return NEO_WEBVIEW_OK;}

bool neo_platform_environment_create_async(neo_webview_environment_t* environment,const neo_webview_environment_options_t* options,neo_platform_created_callback_t callback,void* context,neo_webview_error_t** error) noexcept {
    try {
        const auto runtime_path = widen(neo_string(options->browser_runtime_path));
        const auto user_data = widen(neo_string(options->user_data_root));
        LPWSTR version{};
        const auto version_result = GetAvailableCoreWebView2BrowserVersionString(runtime_path.empty() ? nullptr : runtime_path.c_str(), &version);
        if (FAILED(version_result)) { neo_fail(error, NEO_WEBVIEW_ERROR_RUNTIME_UNAVAILABLE, "Microsoft Edge WebView2 Runtime is unavailable. Install the Evergreen Runtime or configure BrowserRuntimePath.", version_result, "webview2"); return false; }
        auto* state = new windows_environment;
        state->version = take_string(version);
        environment->platform = state;
        auto environment_options = Make<CoreWebView2EnvironmentOptions>();
        const auto arguments = widen(neo_string(options->browser_arguments));
        const auto languages = widen(neo_string(options->preferred_languages));
        if (!arguments.empty()) environment_options->put_AdditionalBrowserArguments(arguments.c_str());
        if (!languages.empty()) environment_options->put_Language(languages.c_str());
        const auto result = CreateCoreWebView2EnvironmentWithOptions(
            runtime_path.empty() ? nullptr : runtime_path.c_str(), user_data.empty() ? nullptr : user_data.c_str(), environment_options.Get(),
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([environment, callback, context](HRESULT result, ICoreWebView2Environment* created) -> HRESULT {
                auto* state = static_cast<windows_environment*>(environment->platform);
                if (FAILED(result) || !created) callback(context, make_error(NEO_WEBVIEW_ERROR_RUNTIME_UNAVAILABLE, "WebView2 environment creation failed", result));
                else { state->value = created; callback(context, nullptr); }
                return S_OK;
            }).Get());
        if (FAILED(result)) { neo_fail(error, NEO_WEBVIEW_ERROR_RUNTIME_UNAVAILABLE, "WebView2 environment creation could not be started", result, "webview2"); return false; }
        return true;
    } catch (const std::exception& ex) { neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, ex.what(), 0, "webview2"); return false; }
}
void neo_platform_environment_destroy(neo_webview_environment_t* environment) noexcept { delete static_cast<windows_environment*>(environment->platform); environment->platform=nullptr; }

bool neo_platform_profile_create(neo_webview_profile_t* profile, neo_webview_error_t** error) noexcept {
    try { profile->platform = new windows_profile; return true; }
    catch (...) { neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "Could not allocate WebView2 profile state", E_OUTOFMEMORY, "webview2"); return false; }
}

void neo_platform_profile_destroy(neo_webview_profile_t* profile) noexcept {
    delete static_cast<windows_profile*>(profile->platform);
    profile->platform = nullptr;
}

neo_webview_result_t neo_platform_profile_get_cookies(neo_webview_profile_t* profile, const std::string& uri,
                                                       neo_webview_buffer_callback_t callback, void* context,
                                                       neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept {
    try {
        auto* state = require_profile(profile, error);
        if (!state) return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;
        const auto result = state->cookies->GetCookies(
            widen(uri).c_str(),
            Callback<ICoreWebView2GetCookiesCompletedHandler>([callback, context, operation](HRESULT result, ICoreWebView2CookieList* list) -> HRESULT {
                if (FAILED(result) || !list) {
                    complete_cookie_buffer(callback, context, operation, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, nullptr,
                                           make_error(NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "WebView2 cookie retrieval failed", result));
                    return S_OK;
                }
                try {
                    UINT32 count{};
                    auto current = list->get_Count(&count);
                    std::string json = "[";
                    for (UINT32 index = 0; SUCCEEDED(current) && index < count; ++index) {
                        ComPtr<ICoreWebView2Cookie> cookie;
                        current = list->GetValueAtIndex(index, &cookie);
                        if (FAILED(current) || !cookie) break;
                        LPWSTR name{}, value{}, domain{}, path{};
                        double expires{};
                        BOOL secure{}, http_only{}, session{};
                        COREWEBVIEW2_COOKIE_SAME_SITE_KIND same_site{};
                        current = cookie->get_Name(&name);
                        if (SUCCEEDED(current)) current = cookie->get_Value(&value);
                        if (SUCCEEDED(current)) current = cookie->get_Domain(&domain);
                        if (SUCCEEDED(current)) current = cookie->get_Path(&path);
                        if (SUCCEEDED(current)) current = cookie->get_Expires(&expires);
                        if (SUCCEEDED(current)) current = cookie->get_IsSecure(&secure);
                        if (SUCCEEDED(current)) current = cookie->get_IsHttpOnly(&http_only);
                        if (SUCCEEDED(current)) current = cookie->get_IsSession(&session);
                        if (SUCCEEDED(current)) current = cookie->get_SameSite(&same_site);
                        const auto name_utf8 = take_string(name);
                        const auto value_utf8 = take_string(value);
                        const auto domain_utf8 = take_string(domain);
                        const auto path_utf8 = take_string(path);
                        if (FAILED(current)) break;
                        if (index) json.push_back(',');
                        json += "{\"name\":"; append_json_string(json, name_utf8);
                        json += ",\"value\":"; append_json_string(json, value_utf8);
                        json += ",\"domain\":"; append_json_string(json, domain_utf8);
                        json += ",\"path\":"; append_json_string(json, path_utf8);
                        json += ",\"secure\":"; json += secure ? "true" : "false";
                        json += ",\"httpOnly\":"; json += http_only ? "true" : "false";
                        json += ",\"sameSite\":" + std::to_string(static_cast<uint32_t>(same_site) + 1u);
                        if (!session) json += ",\"expiresUnixMs\":" + std::to_string(static_cast<int64_t>(expires * 1000.0));
                        json.push_back('}');
                    }
                    if (FAILED(current)) {
                        complete_cookie_buffer(callback, context, operation, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, nullptr,
                                               make_error(NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "Could not read WebView2 cookies", current));
                        return S_OK;
                    }
                    json.push_back(']');
                    complete_cookie_buffer(callback, context, operation, NEO_WEBVIEW_OK,
                                           new neo_webview_buffer(std::vector<uint8_t>(json.begin(), json.end())), nullptr);
                } catch (...) {
                    complete_cookie_buffer(callback, context, operation, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, nullptr,
                                           make_error(NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "Could not serialize WebView2 cookies", E_OUTOFMEMORY));
                }
                return S_OK;
            }).Get());
        return SUCCEEDED(result) ? NEO_WEBVIEW_OK : neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "WebView2 cookie retrieval could not be started", result, "webview2");
    } catch (const std::exception& ex) { return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, ex.what(), 0, "webview2"); }
}

neo_webview_result_t neo_platform_profile_set_cookie(neo_webview_profile_t* profile, const neo_webview_cookie_t* cookie,
                                                      neo_webview_completion_callback_t callback, void* context,
                                                      neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept {
    try {
        auto* state = require_profile(profile, error);
        if (!state) return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;
        ComPtr<ICoreWebView2Cookie> value;
        auto result = state->cookies->CreateCookie(widen(neo_string(cookie->name)).c_str(), widen(neo_string(cookie->value)).c_str(),
                                                   widen(neo_string(cookie->domain)).c_str(), widen(neo_string(cookie->path)).c_str(), &value);
        if (SUCCEEDED(result)) result = value->put_IsSecure((cookie->flags & 1u) ? TRUE : FALSE);
        if (SUCCEEDED(result)) result = value->put_IsHttpOnly((cookie->flags & 2u) ? TRUE : FALSE);
        if (SUCCEEDED(result) && (cookie->flags & 4u) == 0 && cookie->expires_unix_ms > 0) result = value->put_Expires(cookie->expires_unix_ms / 1000.0);
        if (SUCCEEDED(result) && cookie->same_site > 0) result = value->put_SameSite(static_cast<COREWEBVIEW2_COOKIE_SAME_SITE_KIND>(cookie->same_site - 1u));
        if (SUCCEEDED(result)) result = state->cookies->AddOrUpdateCookie(value.Get());
        if (FAILED(result)) return neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "Could not set WebView2 cookie", result, "webview2");
        return schedule_profile_completion(profile, callback, context, operation, NEO_WEBVIEW_OK, nullptr, error);
    } catch (const std::exception& ex) { return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, ex.what(), 0, "webview2"); }
}

neo_webview_result_t neo_platform_profile_delete_cookie(neo_webview_profile_t* profile, const neo_webview_cookie_t* cookie,
                                                         neo_webview_completion_callback_t callback, void* context,
                                                         neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept {
    try {
        auto* state = require_profile(profile, error);
        if (!state) return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;
        auto deletion = std::make_shared<cookie_delete_state>();
        deletion->manager = state->cookies;
        deletion->name = neo_string(cookie->name);
        deletion->domain = neo_string(cookie->domain);
        deletion->path = neo_string(cookie->path);
        const auto host = !deletion->domain.empty() && deletion->domain.front() == '.' ? deletion->domain.substr(1) : deletion->domain;
        deletion->uri = std::string((cookie->flags & 1u) ? "https://" : "http://") + host + (deletion->path.empty() ? "/" : deletion->path);
        deletion->callback = callback;
        deletion->context = context;
        deletion->operation = operation;
        const auto result = begin_cookie_delete_query(deletion);
        return SUCCEEDED(result) ? NEO_WEBVIEW_OK : neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "WebView2 cookie deletion could not be started", result, "webview2");
    } catch (const std::exception& ex) { return neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_ARGUMENT, ex.what(), 0, "webview2"); }
}

neo_webview_result_t neo_platform_profile_clear_data(neo_webview_profile_t* profile, neo_webview_data_kind_t kinds,
                                                      int64_t start_unix_ms, int64_t end_unix_ms,
                                                      neo_webview_completion_callback_t callback, void* context,
                                                      neo_webview_operation_t* operation, neo_webview_error_t** error) noexcept {
    auto* state = require_profile(profile, error);
    if (!state) return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;
    if (kinds != NEO_WEBVIEW_DATA_ALL && (kinds & NEO_WEBVIEW_DATA_PERMISSIONS) != 0) {
        return neo_fail(error, NEO_WEBVIEW_ERROR_NOT_SUPPORTED, "WebView2 does not expose portable permission-data clearing", 0, "webview2");
    }
    ComPtr<ICoreWebView2Profile2> profile2;
    auto result = state->profile.As(&profile2);
    if (FAILED(result)) return neo_fail(error, NEO_WEBVIEW_ERROR_NOT_SUPPORTED, "This WebView2 runtime does not support browsing-data clearing", result, "webview2");
    auto handler = Callback<ICoreWebView2ClearBrowsingDataCompletedHandler>([callback, context, operation](HRESULT result) -> HRESULT {
        auto* error = FAILED(result) ? make_error(NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "WebView2 browsing-data clearing failed", result) : nullptr;
        neo_webview_result_t actual{};
        const auto requested = FAILED(result) ? NEO_WEBVIEW_ERROR_NATIVE_FAILURE : NEO_WEBVIEW_OK;
        if (operation->try_complete(requested, actual)) callback(context, actual, actual == requested ? error : nullptr);
        if (error) error->release();
        operation->release();
        return S_OK;
    });
    if (kinds == NEO_WEBVIEW_DATA_ALL) result = profile2->ClearBrowsingDataAll(handler.Get());
    else {
        auto native = static_cast<COREWEBVIEW2_BROWSING_DATA_KINDS>(0);
        if (kinds & NEO_WEBVIEW_DATA_COOKIES) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_COOKIES;
        if (kinds & NEO_WEBVIEW_DATA_CACHE) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_DISK_CACHE | COREWEBVIEW2_BROWSING_DATA_KINDS_CACHE_STORAGE;
        if (kinds & NEO_WEBVIEW_DATA_LOCAL_STORAGE) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_LOCAL_STORAGE;
        if (kinds & NEO_WEBVIEW_DATA_INDEXED_DB) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_INDEXED_DB;
        if (kinds & NEO_WEBVIEW_DATA_SERVICE_WORKERS) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_SERVICE_WORKERS;
        if (kinds & NEO_WEBVIEW_DATA_DOWNLOAD_HISTORY) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_DOWNLOAD_HISTORY;
        const bool all_time = start_unix_ms == INT64_MIN && end_unix_ms == INT64_MAX;
        result = all_time ? profile2->ClearBrowsingData(native, handler.Get())
                          : profile2->ClearBrowsingDataInTimeRange(native, start_unix_ms / 1000.0, end_unix_ms / 1000.0, handler.Get());
    }
    return SUCCEEDED(result) ? NEO_WEBVIEW_OK : neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "WebView2 browsing-data clearing could not be started", result, "webview2");
}

bool neo_platform_view_create_async(neo_webview_view_t* view,const neo_webview_view_options_t*,neo_platform_created_callback_t callback,void* context,neo_webview_error_t** error) noexcept {
    auto* environment = static_cast<windows_environment*>(view->environment->platform);
    const auto parent = view_parent(view);
    if (!environment || !environment->value || !parent) { neo_fail(error, NEO_WEBVIEW_ERROR_INVALID_STATE, "WebView2 environment or parent window is not ready", 0, "webview2"); return false; }
    auto* state = new windows_view;
    view->platform = state;
    auto completed = Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>([view, callback, context](HRESULT result, ICoreWebView2Controller* controller) -> HRESULT {
            auto* state = static_cast<windows_view*>(view->platform);
            if (FAILED(result) || !controller) { callback(context, make_error(NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "WebView2 controller creation failed", result)); return S_OK; }
            state->controller = controller;
            result = controller->get_CoreWebView2(&state->core);
            if (SUCCEEDED(result)) result = controller->put_Bounds(view_bounds(view));
            if (SUCCEEDED(result)) result = register_view_events(view, state);
            if (SUCCEEDED(result) && view->profile) {
                auto* profile = static_cast<windows_profile*>(view->profile->platform);
                ComPtr<ICoreWebView2_13> core13;
                if (profile && SUCCEEDED(state->core.As(&core13))) {
                    core13->get_Profile(&profile->profile);
                    core13->get_CookieManager(&profile->cookies);
                }
            }
            if (FAILED(result)) callback(context, make_error(NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "WebView2 view initialization failed", result));
            else callback(context, nullptr);
            return S_OK;
        });
    HRESULT result{};
    if (view->profile) {
        ComPtr<ICoreWebView2Environment10> environment10;
        ComPtr<ICoreWebView2ControllerOptions> controller_options;
        result = environment->value.As(&environment10);
        if (SUCCEEDED(result)) result = environment10->CreateCoreWebView2ControllerOptions(&controller_options);
        if (SUCCEEDED(result) && !view->profile->name.empty()) {
            try { result = controller_options->put_ProfileName(widen(view->profile->name).c_str()); }
            catch (...) { result = E_INVALIDARG; }
        }
        if (SUCCEEDED(result)) result = controller_options->put_IsInPrivateModeEnabled(view->profile->ephemeral ? TRUE : FALSE);
        if (SUCCEEDED(result)) result = environment10->CreateCoreWebView2ControllerWithOptions(parent, controller_options.Get(), completed.Get());
    } else {
        result = environment->value->CreateCoreWebView2Controller(parent, completed.Get());
    }
    if (FAILED(result)) { neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "WebView2 controller creation could not be started", result, "webview2"); return false; }
    return true;
}
void neo_platform_view_destroy(neo_webview_view_t* view) noexcept { auto* state=static_cast<windows_view*>(view->platform);if(!state)return;remove_view_events(state);if(state->controller)state->controller->Close();delete state;view->platform=nullptr; }
neo_webview_result_t neo_platform_view_set_bounds(neo_webview_view_t* view) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->controller)return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;return SUCCEEDED(state->controller->put_Bounds(view_bounds(view)))?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_NATIVE_FAILURE;}
neo_webview_result_t neo_platform_view_navigate(neo_webview_view_t* view,const std::string& uri,neo_webview_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(uri);const auto result=state->core->Navigate(value.c_str());return SUCCEEDED(result)?NEO_WEBVIEW_OK:neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"WebView2 navigation failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,ex.what());}}
neo_webview_result_t neo_platform_view_navigate_request(neo_webview_view_t* view,const std::string& uri,const std::string& method,const std::string& headers,const uint8_t* body,uint64_t body_length,neo_webview_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);auto* environment=static_cast<windows_environment*>(view->environment->platform);if(!state||!state->core||!environment||!environment->value)return neo_fail(error,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");if(method.empty()||body_length>ULONG_MAX)return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,"Invalid WebView2 request method or body length");ComPtr<IStream> content;if(body_length){HRESULT result=CreateStreamOnHGlobal(nullptr,TRUE,&content);if(FAILED(result))return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"Could not allocate WebView2 request body",result,"webview2");ULONG written{};result=content->Write(body,static_cast<ULONG>(body_length),&written);LARGE_INTEGER start{};if(SUCCEEDED(result)&&written==body_length)result=content->Seek(start,STREAM_SEEK_SET,nullptr);if(FAILED(result)||written!=body_length)return neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"Could not write WebView2 request body",FAILED(result)?result:E_FAIL,"webview2");}ComPtr<ICoreWebView2Environment2> environment2;ComPtr<ICoreWebView2_2> core2;auto result=environment->value.As(&environment2);if(SUCCEEDED(result))result=state->core.As(&core2);if(FAILED(result))return neo_fail(error,NEO_WEBVIEW_ERROR_NOT_SUPPORTED,"This WebView2 runtime does not support request navigation",result,"webview2");ComPtr<ICoreWebView2WebResourceRequest> request;result=environment2->CreateWebResourceRequest(widen(uri).c_str(),widen(method).c_str(),content.Get(),widen(headers).c_str(),&request);if(SUCCEEDED(result))result=core2->NavigateWithWebResourceRequest(request.Get());return SUCCEEDED(result)?NEO_WEBVIEW_OK:neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"WebView2 request navigation failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,ex.what());}}
neo_webview_result_t neo_platform_view_load_html(neo_webview_view_t* view,const std::string& html,const std::string&,neo_webview_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(html);const auto result=state->core->NavigateToString(value.c_str());return SUCCEEDED(result)?NEO_WEBVIEW_OK:neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"WebView2 HTML loading failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,ex.what());}}
neo_webview_result_t neo_platform_view_command(neo_webview_view_t* view,uint32_t command) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;HRESULT result=E_INVALIDARG;switch(command){case 0:result=state->core->Stop();break;case 1:case 2:result=state->core->Reload();break;case 3:result=state->core->GoBack();break;case 4:result=state->core->GoForward();break;}return SUCCEEDED(result)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_NATIVE_FAILURE;}
neo_webview_result_t neo_platform_view_evaluate(neo_webview_view_t* view,const std::string& script,neo_webview_string_callback_t callback,void* context,neo_webview_operation_t* operation,neo_webview_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(script);const auto result=state->core->ExecuteScript(value.c_str(),Callback<ICoreWebView2ExecuteScriptCompletedHandler>([view,callback,context,operation](HRESULT result,LPCWSTR value)->HRESULT{auto* completion=new script_completion{callback,context,operation,narrow(value),SUCCEEDED(result)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_NATIVE_FAILURE,nullptr};if(FAILED(result))completion->error=make_error(NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"WebView2 script evaluation failed",result);if(neo_webview_app_dispatch(view->environment->app,finish_script,completion)!=NEO_WEBVIEW_OK){if(completion->error)completion->error->release();operation->release();delete completion;}return S_OK;}).Get());return SUCCEEDED(result)?NEO_WEBVIEW_OK:neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"WebView2 script evaluation could not be started",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,ex.what());}}
neo_webview_result_t neo_platform_view_add_script(neo_webview_view_t* view,const std::string& script,const neo_webview_script_options_t* options,neo_webview_string_callback_t callback,void* context,neo_webview_operation_t* operation,neo_webview_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");if(options->injection_time!=NEO_WEBVIEW_SCRIPT_DOCUMENT_START||options->main_frame_only||options->isolated_world)return neo_fail(error,NEO_WEBVIEW_ERROR_NOT_SUPPORTED,"WebView2 supports document-start scripts in the default world for all frames");const auto value=widen(script);const auto result=state->core->AddScriptToExecuteOnDocumentCreated(value.c_str(),Callback<ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler>([view,callback,context,operation](HRESULT result,LPCWSTR identifier)->HRESULT{auto* completion=new script_completion{callback,context,operation,narrow(identifier),SUCCEEDED(result)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_NATIVE_FAILURE,nullptr};if(FAILED(result))completion->error=make_error(NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"WebView2 persistent script registration failed",result);if(neo_webview_app_dispatch(view->environment->app,finish_script,completion)!=NEO_WEBVIEW_OK){if(completion->error)completion->error->release();operation->release();delete completion;}return S_OK;}).Get());return SUCCEEDED(result)?NEO_WEBVIEW_OK:neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"WebView2 persistent script registration could not be started",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,ex.what());}}
neo_webview_result_t neo_platform_view_remove_script(neo_webview_view_t* view,const std::string& identifier) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;const auto value=widen(identifier);return SUCCEEDED(state->core->RemoveScriptToExecuteOnDocumentCreated(value.c_str()))?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_NATIVE_FAILURE;}catch(...){return NEO_WEBVIEW_ERROR_INVALID_ARGUMENT;}}
neo_webview_result_t neo_platform_view_post_message(neo_webview_view_t* view,const std::string& message,bool json,neo_webview_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEO_WEBVIEW_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(message);const auto result=json?state->core->PostWebMessageAsJson(value.c_str()):state->core->PostWebMessageAsString(value.c_str());return SUCCEEDED(result)?NEO_WEBVIEW_OK:neo_fail(error,NEO_WEBVIEW_ERROR_NATIVE_FAILURE,"WebView2 message posting failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEO_WEBVIEW_ERROR_INVALID_ARGUMENT,ex.what());}}
neo_webview_result_t neo_platform_view_get_zoom_factor(const neo_webview_view_t* view,double* factor) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->controller)return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;return SUCCEEDED(state->controller->get_ZoomFactor(factor))?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_NATIVE_FAILURE;}
neo_webview_result_t neo_platform_view_set_zoom_factor(neo_webview_view_t* view,double factor) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->controller)return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;return SUCCEEDED(state->controller->put_ZoomFactor(factor))?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_NATIVE_FAILURE;}
neo_webview_result_t neo_platform_view_get_handle(neo_webview_view_t* view,neo_webview_native_handle_kind_t kind,neo_webview_native_handle_t* handle) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state)return NEO_WEBVIEW_ERROR_NOT_INITIALIZED;if(kind==NEO_WEBVIEW_NATIVE_HANDLE_WEBVIEW2_CONTROLLER&&state->controller){handle->kind=kind;handle->value=state->controller.Get();return NEO_WEBVIEW_OK;}if(kind==NEO_WEBVIEW_NATIVE_HANDLE_WEBVIEW2_CORE&&state->core){handle->kind=kind;handle->value=state->core.Get();return NEO_WEBVIEW_OK;}return NEO_WEBVIEW_ERROR_NOT_SUPPORTED;}
