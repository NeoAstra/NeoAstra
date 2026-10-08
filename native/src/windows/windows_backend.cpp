#include "../common/native_internal.hpp"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <objbase.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <wrl.h>
#include <WebView2.h>
#include <WebView2EnvironmentOptions.h>

#include <algorithm>
#include <cctype>
#include <cmath>
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
constexpr UINT destroy_app_message = WM_APP + 0x50;
constexpr wchar_t dispatch_class[] = L"NeoAstra.Dispatcher";
constexpr wchar_t window_class[] = L"NeoAstra.Window";

struct windows_app { HWND dispatcher{}; bool owns_com{}; std::vector<neoastra_decision_t*> decision_timers; };
struct windows_window {
    HWND hwnd{};
    bool fullscreen{};
    bool leaving_fullscreen{}; // The sizes a window passes through on its way out of the fullscreen state are not states of its own.
    DWORD restored_style{};
    WINDOWPLACEMENT restored_placement{};
    neoastra_window_state_t reported_state{NEOASTRA_WINDOW_NORMAL};
    uint32_t modal_children{};
    bool modal_active{};
    bool modal{};
    HWND caption{}; // Caption-button overlay for NEOASTRA_TITLE_BAR_OVERLAY.
    HWND top_grip{}; // Input-only strip restoring top-edge resizing above the browser.
    bool caption_layered{};
    bool caption_tracking{};
    bool grip_unavailable{};
    bool active{true};
    int caption_hot{-1};
    int caption_pressed{-1};
    windows_window() { restored_placement.length = sizeof(restored_placement); }
};
struct windows_environment { ComPtr<ICoreWebView2Environment> value; std::string version; bool private_mode{}; };
struct windows_profile { ComPtr<ICoreWebView2CookieManager> cookies; ComPtr<ICoreWebView2Profile> profile; };
struct windows_drop_registration { HWND window{}; ComPtr<IDropTarget> target; };
struct windows_view {
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> core;
    HWND drop_window{};
    EventRegistrationToken navigation_starting{};
    // The main-frame navigation that the host refused last, without its fragment, and when it did.
    std::string refused_navigation;
    ULONGLONG refused_navigation_time{};
    EventRegistrationToken navigation_completed{};
    EventRegistrationToken source_changed{};
    EventRegistrationToken title_changed{};
    EventRegistrationToken history_changed{};
    EventRegistrationToken message_received{};
    bool message_registered{};
    EventRegistrationToken web_resource_requested{};
    EventRegistrationToken permission_requested{};
    EventRegistrationToken new_window_requested{};
    EventRegistrationToken process_failed{};
    EventRegistrationToken script_dialog{};
    EventRegistrationToken fullscreen_changed{};
    EventRegistrationToken download_starting{};
    EventRegistrationToken basic_auth{};
    EventRegistrationToken client_certificate{};
    EventRegistrationToken server_certificate_error{};
    EventRegistrationToken accelerator_key{};
    bool events_registered{};
    std::vector<windows_drop_registration> drop_registrations;
};

void register_view_drop_targets(neoastra_view_t* view, windows_view* state);

struct windows_download {
    ComPtr<ICoreWebView2DownloadOperation> operation;
    EventRegistrationToken bytes_changed{};
    EventRegistrationToken state_changed{};
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

class view_drop_target final : public IDropTarget {
public:
    view_drop_target(neoastra_view_t* view, HWND window) : view_(view), window_(window) { }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid != IID_IUnknown && iid != IID_IDropTarget) return E_NOINTERFACE;
        *value = static_cast<IDropTarget*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references_)); }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto value = static_cast<ULONG>(InterlockedDecrement(&references_));
        if (value == 0) delete this;
        return value;
    }
    HRESULT STDMETHODCALLTYPE DragEnter(IDataObject* data, DWORD, POINTL, DWORD* effect) override {
        if (!effect) return E_POINTER;
        FORMATETC files{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        FORMATETC text{CF_UNICODETEXT, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        FORMATETC url{static_cast<CLIPFORMAT>(RegisterClipboardFormatW(L"UniformResourceLocatorW")), nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        valid_ = data && (SUCCEEDED(data->QueryGetData(&files)) || SUCCEEDED(data->QueryGetData(&text)) || SUCCEEDED(data->QueryGetData(&url)));
        *effect = valid_ ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE DragOver(DWORD, POINTL, DWORD* effect) override {
        if (!effect) return E_POINTER;
        *effect = valid_ ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE DragLeave() override { valid_ = false; return S_OK; }
    HRESULT STDMETHODCALLTYPE Drop(IDataObject* data, DWORD, POINTL point, DWORD* effect) override {
        if (!data || !effect) return E_POINTER;
        *effect = DROPEFFECT_NONE;
        if (!valid_) return S_OK;
        valid_ = false;
        FORMATETC format{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        STGMEDIUM medium{};
        bool files = true;
        bool url_format{};
        if (FAILED(data->GetData(&format, &medium))) {
            files = false;
            format.cfFormat = static_cast<CLIPFORMAT>(RegisterClipboardFormatW(L"UniformResourceLocatorW"));
            if (SUCCEEDED(data->GetData(&format, &medium))) url_format = true;
            else { format.cfFormat = CF_UNICODETEXT; if (FAILED(data->GetData(&format, &medium))) return S_OK; }
        }
        try {
            if (!files) {
                const auto bytes = GlobalSize(medium.hGlobal);
                auto* value = static_cast<const wchar_t*>(GlobalLock(medium.hGlobal));
                try {
                    if (value && bytes >= sizeof(wchar_t) && bytes <= 65536) {
                        const auto maximum = bytes / sizeof(wchar_t);
                        const auto length = wcsnlen(value, maximum);
                        if (length > 0 && length < maximum) {
                            auto text = narrow(std::wstring(value, length).c_str());
                            POINT client{point.x, point.y};
                            ScreenToClient(window_, &client);
                            if (!text.empty() && text.size() <= 32768) {
                                const uint64_t kind = url_format || _wcsnicmp(value, L"https://", 8) == 0 || _wcsnicmp(value, L"http://", 7) == 0 || _wcsnicmp(value, L"ftp://", 6) == 0 ? 1 : 0;
                                neo_event_details details{};
                                details.bounds = {static_cast<int32_t>(client.x), static_cast<int32_t>(client.y), 0, 0};
                                neo_emit_view_detailed(view_, NEOASTRA_EVENT_MESSAGE_RECEIVED, 0, &text, nullptr,
                                                       (UINT64_C(1) << 63) | (kind << 56) | 1, 0, nullptr, details);
                                *effect = DROPEFFECT_COPY;
                            }
                        }
                    }
                } catch (...) { if (value) GlobalUnlock(medium.hGlobal); throw; }
                if (value) GlobalUnlock(medium.hGlobal);
                ReleaseStgMedium(&medium);
                return S_OK;
            }
            const auto drop = static_cast<HDROP>(medium.hGlobal);
            const auto count = DragQueryFileW(drop, UINT_MAX, nullptr, 0);
            if (count == 0 || count > 256) { ReleaseStgMedium(&medium); return S_OK; }
            std::string paths;
            for (UINT index = 0; index < count; ++index) {
                const auto length = DragQueryFileW(drop, index, nullptr, 0);
                if (length == 0 || length > 32768) throw std::invalid_argument("drop path limit");
                std::wstring path(static_cast<size_t>(length) + 1, L'\0');
                if (DragQueryFileW(drop, index, path.data(), length + 1) != length) throw std::invalid_argument("drop path read");
                path.resize(length);
                std::wstring canonical(32768, L'\0');
                const auto canonical_length = GetFullPathNameW(path.c_str(), static_cast<DWORD>(canonical.size()), canonical.data(), nullptr);
                if (canonical_length == 0 || canonical_length >= canonical.size()) throw std::invalid_argument("drop path canonicalization");
                canonical.resize(canonical_length);
                auto utf8 = narrow(canonical.c_str());
                if (paths.size() + utf8.size() + 1 > 1024 * 1024) throw std::invalid_argument("drop metadata limit");
                if (!paths.empty()) paths.push_back('\0');
                paths += utf8;
            }
            POINT client{point.x, point.y};
            ScreenToClient(window_, &client);
            neo_event_details details{};
            details.bounds = {static_cast<int32_t>(client.x), static_cast<int32_t>(client.y), 0, 0};
            neo_emit_view_detailed(view_, NEOASTRA_EVENT_MESSAGE_RECEIVED, 0, &paths, nullptr,
                                   (UINT64_C(1) << 63) | (UINT64_C(2) << 56) | count, 0, nullptr, details);
            *effect = DROPEFFECT_COPY;
        } catch (...) {
            neo_log(view_->environment->app, NEOASTRA_LOG_WARNING, "drag-drop", "WebView2 native drop decoding failed");
        }
        ReleaseStgMedium(&medium);
        return S_OK;
    }
private:
    ~view_drop_target() = default;
    volatile LONG references_{1};
    neoastra_view_t* view_{};
    HWND window_{};
    bool valid_{};
};

std::string take_string(LPWSTR value) {
    const auto result = narrow(value);
    CoTaskMemFree(value);
    return result;
}

neoastra_resource_kind_t portable_resource_kind(COREWEBVIEW2_WEB_RESOURCE_CONTEXT kind) noexcept {
    switch (kind) {
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT: return NEOASTRA_RESOURCE_DOCUMENT;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_STYLESHEET: return NEOASTRA_RESOURCE_STYLESHEET;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_IMAGE: return NEOASTRA_RESOURCE_IMAGE;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_MEDIA: return NEOASTRA_RESOURCE_MEDIA;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_FONT: return NEOASTRA_RESOURCE_FONT;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_SCRIPT: return NEOASTRA_RESOURCE_SCRIPT;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_XML_HTTP_REQUEST: return NEOASTRA_RESOURCE_XML_HTTP_REQUEST;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_FETCH: return NEOASTRA_RESOURCE_FETCH;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_TEXT_TRACK: return NEOASTRA_RESOURCE_TEXT_TRACK;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_EVENT_SOURCE: return NEOASTRA_RESOURCE_EVENT_SOURCE;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_WEBSOCKET: return NEOASTRA_RESOURCE_WEBSOCKET;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_MANIFEST: return NEOASTRA_RESOURCE_MANIFEST;
        default: return NEOASTRA_RESOURCE_OTHER;
    }
}

std::string request_headers(ICoreWebView2WebResourceRequest* request) {
    ComPtr<ICoreWebView2HttpRequestHeaders> collection;
    ComPtr<ICoreWebView2HttpHeadersCollectionIterator> iterator;
    if (FAILED(request->get_Headers(&collection)) || !collection || FAILED(collection->GetIterator(&iterator)) || !iterator) return {};
    std::string result;
    BOOL current{};
    if (FAILED(iterator->get_HasCurrentHeader(&current))) return {};
    while (current) {
        LPWSTR name{}, value{};
        if (FAILED(iterator->GetCurrentHeader(&name, &value))) { CoTaskMemFree(name); CoTaskMemFree(value); break; }
        result += take_string(name);
        result += ": ";
        result += take_string(value);
        result += "\r\n";
        if (FAILED(iterator->MoveNext(&current))) break;
    }
    return result;
}

const neo_custom_scheme_registration* find_scheme(const neoastra_environment_t* environment, std::string_view uri) noexcept {
    const auto colon = uri.find(':');
    if (colon == std::string_view::npos) return nullptr;
    std::string name(uri.substr(0, colon));
    std::transform(name.begin(), name.end(), name.begin(), [](unsigned char value) { return static_cast<char>(std::tolower(value)); });
    const auto found = std::find_if(environment->custom_schemes.begin(), environment->custom_schemes.end(),
        [&name](const auto& scheme) { return scheme.name == name; });
    return found == environment->custom_schemes.end() ? nullptr : &*found;
}

const wchar_t* default_reason(uint32_t status) noexcept {
    switch (status) {
        case 200: return L"OK";
        case 204: return L"No Content";
        case 400: return L"Bad Request";
        case 403: return L"Forbidden";
        case 404: return L"Not Found";
        case 405: return L"Method Not Allowed";
        case 500: return L"Internal Server Error";
        default: return L"Response";
    }
}

bool contains_header(std::string_view headers, std::string_view name) noexcept {
    for (size_t position = 0; position < headers.size();) {
        const auto end = headers.find('\n', position);
        auto line = headers.substr(position, end == std::string_view::npos ? headers.size() - position : end - position);
        while (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        const auto colon = line.find(':');
        if (colon != std::string_view::npos && colon == name.size()) {
            bool match = true;
            for (size_t index = 0; index < name.size(); ++index) {
                if (std::tolower(static_cast<unsigned char>(line[index])) != std::tolower(static_cast<unsigned char>(name[index]))) { match = false; break; }
            }
            if (match) return true;
        }
        if (end == std::string_view::npos) break;
        position = end + 1;
    }
    return false;
}

HRESULT create_resource_response(neoastra_view_t* view, const neoastra_resource_response_t& response,
                                 ICoreWebView2WebResourceResponse** output) noexcept {
    try {
        if (!neo_valid_resource_response(response)) return E_INVALIDARG;
        ComPtr<IStream> content;
        HRESULT result = S_OK;
        if (response.body_kind == NEOASTRA_RESOURCE_BODY_BYTES) {
            if (response.byte_length > ULONG_MAX) return E_INVALIDARG;
            result = CreateStreamOnHGlobal(nullptr, TRUE, &content);
            if (SUCCEEDED(result) && response.byte_length) {
                ULONG written{};
                result = content->Write(response.bytes, static_cast<ULONG>(response.byte_length), &written);
                if (SUCCEEDED(result) && written != response.byte_length) result = E_FAIL;
                LARGE_INTEGER start{};
                if (SUCCEEDED(result)) result = content->Seek(start, STREAM_SEEK_SET, nullptr);
            }
        } else if (response.body_kind == NEOASTRA_RESOURCE_BODY_FILE) {
            result = SHCreateStreamOnFileEx(widen(neo_string(response.file_path)).c_str(), STGM_READ | STGM_SHARE_DENY_WRITE,
                                            FILE_ATTRIBUTE_NORMAL, FALSE, nullptr, &content);
        }
        if (FAILED(result)) return result;
        auto headers = neo_string(response.headers);
        const auto mime = neo_string(response.mime_type);
        if (!mime.empty() && !contains_header(headers, "content-type")) headers += "Content-Type: " + mime + "\r\n";
        if (response.body_kind != NEOASTRA_RESOURCE_BODY_EMPTY && response.content_length != UINT64_MAX && !contains_header(headers, "content-length")) {
            headers += "Content-Length: " + std::to_string(response.content_length) + "\r\n";
        }
        const auto reason = response.reason_phrase.length ? widen(neo_string(response.reason_phrase)) : std::wstring(default_reason(response.status_code));
        const auto native_headers = widen(headers);
        auto* environment = static_cast<windows_environment*>(view->environment->platform);
        if (!environment || !environment->value) return E_ABORT;
        return environment->value->CreateWebResourceResponse(content.Get(), static_cast<int>(response.status_code), reason.c_str(), native_headers.c_str(), output);
    } catch (...) { return E_FAIL; }
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

neoastra_error_t* make_error(neoastra_result_t code, const char* message, HRESULT native_code, const char* domain = "webview2") noexcept {
    neoastra_error_t* error{};
    neo_fail(&error, code, message, native_code, domain);
    return error;
}

HWND view_parent(const neoastra_view_t* view) noexcept {
    if (view->window) {
        const auto* state = static_cast<windows_window*>(view->window->platform);
        return state ? state->hwnd : nullptr;
    }
    return view->parent.kind == NEOASTRA_NATIVE_PARENT_WIN32_HWND ? static_cast<HWND>(view->parent.handle) : nullptr;
}

RECT view_bounds(const neoastra_view_t* view) noexcept {
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

void sync_window_view_visibility(neoastra_window_t* window, bool visible) noexcept {
    for (auto* view : window->views) {
        if (!view) continue;
        auto* state = static_cast<windows_view*>(view->platform);
        if (!state || !state->controller) continue;
        if (visible && view->fill_parent) (void)state->controller->put_Bounds(view_bounds(view));
        (void)state->controller->put_IsVisible(visible ? TRUE : FALSE);
    }
}

void remove_view_events(windows_view* state) noexcept {
    if (!state || !state->core || !state->events_registered) return;
    state->core->remove_NavigationStarting(state->navigation_starting);
    state->core->remove_NavigationCompleted(state->navigation_completed);
    state->core->remove_SourceChanged(state->source_changed);
    state->core->remove_DocumentTitleChanged(state->title_changed);
    state->core->remove_HistoryChanged(state->history_changed);
    if (state->message_registered) state->core->remove_WebMessageReceived(state->message_received);
    state->core->remove_WebResourceRequested(state->web_resource_requested);
    state->core->remove_PermissionRequested(state->permission_requested);
    state->core->remove_NewWindowRequested(state->new_window_requested);
    state->core->remove_ProcessFailed(state->process_failed);
    state->core->remove_ScriptDialogOpening(state->script_dialog);
    state->core->remove_ContainsFullScreenElementChanged(state->fullscreen_changed);
    ComPtr<ICoreWebView2_4> core4;
    if (SUCCEEDED(state->core.As(&core4))) core4->remove_DownloadStarting(state->download_starting);
    ComPtr<ICoreWebView2_10> core10;if(SUCCEEDED(state->core.As(&core10)))core10->remove_BasicAuthenticationRequested(state->basic_auth);
    ComPtr<ICoreWebView2_5> core5;if(SUCCEEDED(state->core.As(&core5)))core5->remove_ClientCertificateRequested(state->client_certificate);
    ComPtr<ICoreWebView2_14> core14;if(SUCCEEDED(state->core.As(&core14)))core14->remove_ServerCertificateErrorDetected(state->server_certificate_error);
    state->events_registered = false;
}

neoastra_result_t windows_download_command(neoastra_download_t* download, uint32_t command) noexcept {
    auto* state=static_cast<windows_download*>(download->platform);
    if(!state||!state->operation)return NEOASTRA_ERROR_DISPOSED;
    const auto current=download->state.load(std::memory_order_acquire);
    if(current==NEOASTRA_DOWNLOAD_COMPLETED||current==NEOASTRA_DOWNLOAD_CANCELED||current==NEOASTRA_DOWNLOAD_FAILED)return NEOASTRA_ERROR_INVALID_STATE;
    HRESULT result=E_INVALIDARG;
    if(command==0)result=state->operation->Cancel();else if(command==1)result=state->operation->Pause();else if(command==2)result=state->operation->Resume();
    return SUCCEEDED(result)?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;
}

void destroy_windows_download(neoastra_download_t* download) noexcept {
    auto* state=static_cast<windows_download*>(download->platform);if(!state)return;
    if(state->operation){state->operation->remove_BytesReceivedChanged(state->bytes_changed);state->operation->remove_StateChanged(state->state_changed);}
    delete state;
}

bool register_download_events(neoastra_download_t* download) noexcept {
    try {
        auto* state=static_cast<windows_download*>(download->platform);
        auto result=state->operation->add_BytesReceivedChanged(Callback<ICoreWebView2BytesReceivedChangedEventHandler>([download](ICoreWebView2DownloadOperation* operation,IUnknown*)->HRESULT{
            INT64 received{},total{-1};operation->get_BytesReceived(&received);operation->get_TotalBytesToReceive(&total);
            download->bytes_received.store(received<0?0:static_cast<uint64_t>(received),std::memory_order_release);download->total_bytes.store(total<0?UINT64_MAX:static_cast<uint64_t>(total),std::memory_order_release);
            neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED);return S_OK;
        }).Get(),&state->bytes_changed);
        if(FAILED(result))return false;
        result=state->operation->add_StateChanged(Callback<ICoreWebView2StateChangedEventHandler>([download](ICoreWebView2DownloadOperation* operation,IUnknown*)->HRESULT{
            COREWEBVIEW2_DOWNLOAD_STATE native{};if(FAILED(operation->get_State(&native))||native==COREWEBVIEW2_DOWNLOAD_STATE_IN_PROGRESS)return S_OK;
            COREWEBVIEW2_DOWNLOAD_INTERRUPT_REASON reason{};
            if(native==COREWEBVIEW2_DOWNLOAD_STATE_INTERRUPTED)operation->get_InterruptReason(&reason);
            const auto terminal=native==COREWEBVIEW2_DOWNLOAD_STATE_COMPLETED?NEOASTRA_DOWNLOAD_COMPLETED:
                reason==COREWEBVIEW2_DOWNLOAD_INTERRUPT_REASON_USER_CANCELED?NEOASTRA_DOWNLOAD_CANCELED:NEOASTRA_DOWNLOAD_FAILED;
            auto expected=NEOASTRA_DOWNLOAD_IN_PROGRESS;if(!download->state.compare_exchange_strong(expected,terminal,std::memory_order_acq_rel))return S_OK;
            if(terminal==NEOASTRA_DOWNLOAD_FAILED)download->failure_reason="WebView2 interrupt reason "+std::to_string(static_cast<uint32_t>(reason));
            neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_COMPLETED);download->release_lifecycle();return S_OK;
        }).Get(),&state->state_changed);
        if(SUCCEEDED(result))return true;
        state->operation->remove_BytesReceivedChanged(state->bytes_changed);
    } catch (...) { }
    return false;
}

struct download_decision_context { ComPtr<ICoreWebView2DownloadStartingEventArgs> args; ComPtr<ICoreWebView2Deferral> deferral; neoastra_download_t* download{}; };
void download_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {
    std::unique_ptr<download_decision_context> context(static_cast<download_decision_context*>(pointer));auto* download=context->download;
    if(response->action==NEOASTRA_DECISION_DOWNLOAD&&!response->text.length)response=nullptr;
    const auto cancel=[&](bool handled){context->args->put_Cancel(TRUE);if(handled)context->args->put_Handled(TRUE);download->state.store(NEOASTRA_DOWNLOAD_CANCELED);neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_COMPLETED);download->release_lifecycle();};
    if(!response||response->action==NEOASTRA_DECISION_CANCEL||response->action==NEOASTRA_DECISION_DENY){cancel(false);}
    else if(response->action==NEOASTRA_DECISION_HANDLED_EXTERNAL){cancel(true);}
    else if(response->action==NEOASTRA_DECISION_DEFAULT||response->action==NEOASTRA_DECISION_ALLOW||response->action==NEOASTRA_DECISION_DOWNLOAD){
        if(response->action==NEOASTRA_DECISION_DOWNLOAD){try{download->destination_path=neo_string(response->text);context->args->put_ResultFilePath(widen(download->destination_path).c_str());context->args->put_Handled(TRUE);}catch(...){cancel(false);context->deferral->Complete();return;}}
        download->state.store(NEOASTRA_DOWNLOAD_IN_PROGRESS);
        if(register_download_events(download))neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_STARTED);else cancel(false);
    } else cancel(false);
    context->deferral->Complete();
}

struct navigation_decision_context {
    ComPtr<ICoreWebView2NavigationStartingEventArgs> args;
    neoastra_view_t* view{};
    std::string uri;
};

// WebView2 starts the request of a navigation while it asks its host about the navigation, and drops the response when the
// host refuses: the address is requested by a view that does not show it, and a script of the host waits for that response.
// WebView2 asks about the request right after the navigation, so the request of a navigation that was just refused is
// answered by the host instead of being sent. Nothing but that request follows a refusal so closely.
constexpr ULONGLONG refused_navigation_lifetime = 10000;

std::string_view without_fragment(std::string_view uri) noexcept { return uri.substr(0, uri.find('#')); }

void remember_navigation(neoastra_view_t* view, const std::string& uri, bool refused) noexcept {
    auto* state = static_cast<windows_view*>(view->platform);
    if (!state) return;
    state->refused_navigation.clear();
    if (!refused) return;
    try { state->refused_navigation = without_fragment(uri); } catch (...) { }
    state->refused_navigation_time = GetTickCount64();
}

// Whether a document is requested for the navigation that the host refused last. That navigation has one request.
bool take_refused_navigation(neoastra_view_t* view, std::string_view uri) noexcept {
    auto* state = static_cast<windows_view*>(view->platform);
    if (!state || state->refused_navigation.empty()) return false;
    const bool refused = GetTickCount64() - state->refused_navigation_time <= refused_navigation_lifetime && state->refused_navigation == without_fragment(uri);
    if (refused) state->refused_navigation.clear();
    return refused;
}

struct script_dialog_context { ComPtr<ICoreWebView2ScriptDialogOpeningEventArgs> args; ComPtr<ICoreWebView2Deferral> deferral; };
void script_dialog_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {
    std::unique_ptr<script_dialog_context> context(static_cast<script_dialog_context*>(pointer));
    if(response->action==NEOASTRA_DECISION_ALLOW){if(response->text.length){try{context->args->put_ResultText(widen(neo_string(response->text)).c_str());}catch(...){}}context->args->Accept();}
    context->deferral->Complete();
}

struct basic_auth_context { ComPtr<ICoreWebView2BasicAuthenticationRequestedEventArgs> args; ComPtr<ICoreWebView2Deferral> deferral; };
void basic_auth_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {
    std::unique_ptr<basic_auth_context> context(static_cast<basic_auth_context*>(pointer));
    if(response->action==NEOASTRA_DECISION_ALLOW){try{ComPtr<ICoreWebView2BasicAuthenticationResponse> credentials;if(SUCCEEDED(context->args->get_Response(&credentials))){credentials->put_UserName(widen(neo_string(response->text)).c_str());credentials->put_Password(widen(neo_string(response->secondary_text)).c_str());}}catch(...){context->args->put_Cancel(TRUE);}}
    else if(response->action==NEOASTRA_DECISION_CANCEL||response->action==NEOASTRA_DECISION_DENY)context->args->put_Cancel(TRUE);
    context->deferral->Complete();
}

struct tls_context { ComPtr<ICoreWebView2ServerCertificateErrorDetectedEventArgs> args; ComPtr<ICoreWebView2Deferral> deferral; };
void tls_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<tls_context> context(static_cast<tls_context*>(pointer));context->args->put_Action(response->action==NEOASTRA_DECISION_ALLOW?COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_ALWAYS_ALLOW:COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL);context->deferral->Complete();}

struct client_cert_context { ComPtr<ICoreWebView2ClientCertificateRequestedEventArgs> args; ComPtr<ICoreWebView2ClientCertificateCollection> certificates; ComPtr<ICoreWebView2Deferral> deferral; };
void client_cert_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {std::unique_ptr<client_cert_context> context(static_cast<client_cert_context*>(pointer));
    if(response->action==NEOASTRA_DECISION_ALLOW){UINT count{};context->certificates->get_Count(&count);if(response->selected_index<count){ComPtr<ICoreWebView2ClientCertificate> selected;context->certificates->GetValueAtIndex(response->selected_index,&selected);context->args->put_SelectedCertificate(selected.Get());context->args->put_Handled(TRUE);}else context->args->put_Cancel(TRUE);}
    else if(response->action==NEOASTRA_DECISION_CANCEL||response->action==NEOASTRA_DECISION_DENY){context->args->put_Cancel(TRUE);context->args->put_Handled(TRUE);}context->deferral->Complete();}

struct fullscreen_context { ComPtr<ICoreWebView2> core; };
void fullscreen_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {
    std::unique_ptr<fullscreen_context> context(static_cast<fullscreen_context*>(pointer));
    if(response->action!=NEOASTRA_DECISION_ALLOW)context->core->ExecuteScript(L"document.fullscreenElement && document.exitFullscreen()",nullptr);
}

neoastra_permission_kind_t portable_permission(COREWEBVIEW2_PERMISSION_KIND kind) noexcept {
    switch (kind) {
        case COREWEBVIEW2_PERMISSION_KIND_GEOLOCATION: return NEOASTRA_PERMISSION_GEOLOCATION;
        case COREWEBVIEW2_PERMISSION_KIND_CAMERA: return NEOASTRA_PERMISSION_CAMERA;
        case COREWEBVIEW2_PERMISSION_KIND_MICROPHONE: return NEOASTRA_PERMISSION_MICROPHONE;
        case COREWEBVIEW2_PERMISSION_KIND_NOTIFICATIONS: return NEOASTRA_PERMISSION_NOTIFICATIONS;
        case COREWEBVIEW2_PERMISSION_KIND_CLIPBOARD_READ: return NEOASTRA_PERMISSION_CLIPBOARD_READ;
        case COREWEBVIEW2_PERMISSION_KIND_MIDI_SYSTEM_EXCLUSIVE_MESSAGES: return NEOASTRA_PERMISSION_MIDI;
        case COREWEBVIEW2_PERMISSION_KIND_LOCAL_FONTS: return NEOASTRA_PERMISSION_LOCAL_FONTS;
        case COREWEBVIEW2_PERMISSION_KIND_FILE_READ_WRITE: return NEOASTRA_PERMISSION_FILE_SYSTEM;
        default: return NEOASTRA_PERMISSION_UNKNOWN;
    }
}

struct permission_decision_context {
    ComPtr<ICoreWebView2PermissionRequestedEventArgs> args;
    ComPtr<ICoreWebView2Deferral> deferral;
};

struct new_window_decision_context {
    ComPtr<ICoreWebView2NewWindowRequestedEventArgs> args;
    ComPtr<ICoreWebView2Deferral> deferral;
    ComPtr<ICoreWebView2> core;
    std::wstring uri;
};

void new_window_decided(void* pointer, const neoastra_decision_response_t* response) noexcept {
    std::unique_ptr<new_window_decision_context> context(static_cast<new_window_decision_context*>(pointer));
    context->args->put_Handled(TRUE);
    if (response->action == NEOASTRA_DECISION_ALLOW && response->target_view) {
        auto* target=static_cast<windows_view*>(response->target_view->platform);
        if(target&&target->core)context->args->put_NewWindow(target->core.Get());
    } else if (response->action == NEOASTRA_DECISION_ALLOW) {
        context->core->Navigate(context->uri.c_str());
    }
    context->deferral->Complete();
}

void permission_decided(void* pointer, const neoastra_decision_response_t* response) noexcept {
    std::unique_ptr<permission_decision_context> context(static_cast<permission_decision_context*>(pointer));
    const auto state = response->action == NEOASTRA_DECISION_ALLOW ? COREWEBVIEW2_PERMISSION_STATE_ALLOW
                     : response->action == NEOASTRA_DECISION_DEFAULT ? COREWEBVIEW2_PERMISSION_STATE_DEFAULT
                     : COREWEBVIEW2_PERMISSION_STATE_DENY;
    ComPtr<ICoreWebView2PermissionRequestedEventArgs3> args3;
    if (SUCCEEDED(context->args.As(&args3))) args3->put_SavesInProfile(response->persist ? TRUE : FALSE);
    context->args->put_State(state);
    context->deferral->Complete();
}

void navigation_decided(void* pointer, const neoastra_decision_response_t* response) noexcept {
    std::unique_ptr<navigation_decision_context> context(static_cast<navigation_decision_context*>(pointer));
    const bool cancel = response->action != NEOASTRA_DECISION_ALLOW && response->action != NEOASTRA_DECISION_DEFAULT;
    context->args->put_Cancel(cancel ? TRUE : FALSE);
    // Here and not after the decision: opening the address outside the view comes next, and lets the thread take the request.
    remember_navigation(context->view, context->uri, cancel);
}

uint64_t portable_process_failure(COREWEBVIEW2_PROCESS_FAILED_KIND kind, COREWEBVIEW2_PROCESS_FAILED_REASON reason) noexcept {
    uint64_t value = NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED | NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW;
    if (kind == COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED) {
        value = NEOASTRA_PROCESS_FAILURE_BROWSER_PROCESS_EXITED | NEOASTRA_PROCESS_FAILURE_RESTART_APPLICATION;
    } else if (kind == COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_UNRESPONSIVE) {
        value = NEOASTRA_PROCESS_FAILURE_PROCESS_UNRESPONSIVE | NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW;
    }
    if (reason == COREWEBVIEW2_PROCESS_FAILED_REASON_UNEXPECTED ||
        reason == COREWEBVIEW2_PROCESS_FAILED_REASON_CRASHED ||
        reason == COREWEBVIEW2_PROCESS_FAILED_REASON_OUT_OF_MEMORY ||
        reason == COREWEBVIEW2_PROCESS_FAILED_REASON_ABNORMAL_EXIT ||
        reason == COREWEBVIEW2_PROCESS_FAILED_REASON_INTEGRITY_FAILURE) {
        value |= NEOASTRA_PROCESS_FAILURE_CRASHED;
    }
    return value;
}

HRESULT register_view_events(neoastra_view_t* view, windows_view* state) {
    state->events_registered = true;
    HRESULT result = state->core->add_NavigationStarting(
        Callback<ICoreWebView2NavigationStartingEventHandler>([view](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
            LPWSTR raw_uri{};
            BOOL user_initiated{};
            BOOL redirected{};
            args->get_Uri(&raw_uri);
            args->get_IsUserInitiated(&user_initiated);
            args->get_IsRedirected(&redirected);
            auto uri = take_string(raw_uri);

            auto context = std::make_unique<navigation_decision_context>();
            context->args = args;
            context->view = view;
            context->uri = uri;
            auto* decision = new neoastra_decision;
            neo_configure_decision(decision, view, NEOASTRA_DECISION_NAVIGATION, NEOASTRA_DECISION_ALLOW);
            decision->completion = navigation_decided;
            decision->completion_context = context.release();
            decision->external_uri = uri;
            neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_REQUESTED, 0, nullptr, &uri,
                          NEOASTRA_NAVIGATION_REQUEST_MAIN_FRAME | (user_initiated && !redirected ? NEOASTRA_NAVIGATION_REQUEST_USER_INITIATED : 0), 0, decision);
            const auto decision_state = decision->state.load(std::memory_order_acquire);
            // NavigationStarting has no WebView2 deferral API. A managed handler may
            // defer the portable decision, but WebView2 requires the final Cancel
            // value before this callback returns, so apply the safe default here.
            if (decision_state == neo_decision_state::pending || decision_state == neo_decision_state::deferred) {
                neoastra_decision_response_t response{};
                response.size = sizeof(response);
                response.version = 1;
                response.action = decision->default_action;
                neoastra_decision_complete(decision, &response, nullptr);
            }
            const auto allowed = decision->resolved_action.load(std::memory_order_acquire) == NEOASTRA_DECISION_ALLOW;
            decision->release();
            if (allowed) neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_STARTED, 0, nullptr, &uri, 1);
            return S_OK;
        }).Get(), &state->navigation_starting);
    if (FAILED(result)) return result;

    result=state->core->add_ScriptDialogOpening(Callback<ICoreWebView2ScriptDialogOpeningEventHandler>([view](ICoreWebView2*,ICoreWebView2ScriptDialogOpeningEventArgs* args)->HRESULT{
        LPWSTR raw_uri{},raw_message{},raw_default{};COREWEBVIEW2_SCRIPT_DIALOG_KIND kind{};ComPtr<ICoreWebView2Deferral> deferral;
        auto hr=args->get_Uri(&raw_uri);if(SUCCEEDED(hr))hr=args->get_Kind(&kind);if(SUCCEEDED(hr))hr=args->get_Message(&raw_message);if(SUCCEEDED(hr))hr=args->get_DefaultText(&raw_default);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);
        if(FAILED(hr)){CoTaskMemFree(raw_uri);CoTaskMemFree(raw_message);CoTaskMemFree(raw_default);return S_OK;}
        try{auto uri=take_string(raw_uri);auto message=take_string(raw_message);auto default_text=take_string(raw_default);auto context=std::make_unique<script_dialog_context>();context->args=args;context->deferral=deferral;
            auto* decision=new neoastra_decision;const auto portable=kind==COREWEBVIEW2_SCRIPT_DIALOG_KIND_ALERT?NEOASTRA_SCRIPT_DIALOG_ALERT:kind==COREWEBVIEW2_SCRIPT_DIALOG_KIND_CONFIRM?NEOASTRA_SCRIPT_DIALOG_CONFIRM:kind==COREWEBVIEW2_SCRIPT_DIALOG_KIND_PROMPT?NEOASTRA_SCRIPT_DIALOG_PROMPT:NEOASTRA_SCRIPT_DIALOG_BEFORE_UNLOAD;
            neo_configure_decision(decision,view,NEOASTRA_DECISION_SCRIPT_DIALOG,portable==NEOASTRA_SCRIPT_DIALOG_ALERT?NEOASTRA_DECISION_ALLOW:NEOASTRA_DECISION_CANCEL);decision->completion=script_dialog_decided;decision->completion_context=context.release();neo_event_details details{};details.text2=&default_text;
            neo_emit_view_detailed(view,NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED,0,&message,&uri,portable,0,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;
        }catch(...){deferral->Complete();return S_OK;}
    }).Get(),&state->script_dialog);
    if(FAILED(result))return result;

    result=state->core->add_ContainsFullScreenElementChanged(Callback<ICoreWebView2ContainsFullScreenElementChangedEventHandler>([view](ICoreWebView2* core,IUnknown*)->HRESULT{
        BOOL entering{};core->get_ContainsFullScreenElement(&entering);if(!entering)return S_OK;auto* decision=new(std::nothrow) neoastra_decision;if(!decision){core->ExecuteScript(L"document.fullscreenElement && document.exitFullscreen()",nullptr);return S_OK;}
        neo_configure_decision(decision,view,NEOASTRA_DECISION_FULLSCREEN,NEOASTRA_DECISION_DENY);decision->completion=fullscreen_decided;decision->completion_context=new(std::nothrow) fullscreen_context{core};if(!decision->completion_context){decision->release();core->ExecuteScript(L"document.fullscreenElement && document.exitFullscreen()",nullptr);return S_OK;}
        neo_emit_view(view,NEOASTRA_EVENT_FULLSCREEN_REQUESTED,0,nullptr,&view->source,1,0,decision);neo_finish_decision_event(view,decision);decision->release();return S_OK;
    }).Get(),&state->fullscreen_changed);
    if(FAILED(result))return result;

    result = state->core->add_NavigationCompleted(
        Callback<ICoreWebView2NavigationCompletedEventHandler>([view](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT {
            BOOL success{};
            COREWEBVIEW2_WEB_ERROR_STATUS status{};
            UINT64 navigation_id{};
            args->get_IsSuccess(&success);
            args->get_WebErrorStatus(&status);
            args->get_NavigationId(&navigation_id);
            if (success) register_view_drop_targets(view, static_cast<windows_view*>(view->platform));
            neo_emit_view(view, success ? NEOASTRA_EVENT_NAVIGATION_COMPLETED : NEOASTRA_EVENT_NAVIGATION_FAILED,
                          0, nullptr, &view->source, success ? 0 : static_cast<uint64_t>(NEOASTRA_ERROR_NATIVE_FAILURE),
                          static_cast<int64_t>(status));
            return S_OK;
        }).Get(), &state->navigation_completed);
    if (FAILED(result)) return result;

    result = state->core->add_SourceChanged(
        Callback<ICoreWebView2SourceChangedEventHandler>([view](ICoreWebView2* core, ICoreWebView2SourceChangedEventArgs*) -> HRESULT {
            LPWSTR source{};
            if (SUCCEEDED(core->get_Source(&source))) {
                view->source = take_string(source);
                neo_emit_view(view, NEOASTRA_EVENT_SOURCE_CHANGED, 0, nullptr, &view->source);
            }
            return S_OK;
        }).Get(), &state->source_changed);
    if (FAILED(result)) return result;

    result = state->core->add_DocumentTitleChanged(
        Callback<ICoreWebView2DocumentTitleChangedEventHandler>([view](ICoreWebView2* core, IUnknown*) -> HRESULT {
            LPWSTR title{};
            if (SUCCEEDED(core->get_DocumentTitle(&title))) {
                view->title = take_string(title);
                neo_emit_view(view, NEOASTRA_EVENT_TITLE_CHANGED, 0, &view->title);
            }
            return S_OK;
        }).Get(), &state->title_changed);
    if (FAILED(result)) return result;

    result = state->core->add_HistoryChanged(
        Callback<ICoreWebView2HistoryChangedEventHandler>([view](ICoreWebView2* core, IUnknown*) -> HRESULT {
            BOOL back{}, forward{};
            core->get_CanGoBack(&back);
            core->get_CanGoForward(&forward);
            neo_emit_view(view, NEOASTRA_EVENT_HISTORY_CHANGED, 0, nullptr, nullptr, (back ? 1u : 0u) | (forward ? 2u : 0u));
            return S_OK;
        }).Get(), &state->history_changed);
    if (FAILED(result)) return result;

    for (const auto& scheme : view->environment->custom_schemes) {
        const auto filter = widen(scheme.name + ":*");
        result = state->core->AddWebResourceRequestedFilter(filter.c_str(), COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL);
        if (FAILED(result)) return result;
    }
    // Every document, for the request of a navigation that the host refuses.
    result = state->core->AddWebResourceRequestedFilter(L"*", COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT);
    if (FAILED(result)) return result;
    result = state->core->add_WebResourceRequested(
        Callback<ICoreWebView2WebResourceRequestedEventHandler>([view](ICoreWebView2*, ICoreWebView2WebResourceRequestedEventArgs* args) -> HRESULT {
            ComPtr<ICoreWebView2WebResourceRequest> native_request;
            COREWEBVIEW2_WEB_RESOURCE_CONTEXT native_kind{};
            LPWSTR raw_uri{}, raw_method{};
            auto result = args->get_Request(&native_request);
            if (SUCCEEDED(result)) result = args->get_ResourceContext(&native_kind);
            if (SUCCEEDED(result)) result = native_request->get_Uri(&raw_uri);
            if (SUCCEEDED(result)) result = native_request->get_Method(&raw_method);
            if (FAILED(result)) { CoTaskMemFree(raw_uri); CoTaskMemFree(raw_method); return S_OK; }
            try {
                const auto uri = take_string(raw_uri);
                raw_uri = nullptr;
                const auto method = take_string(raw_method);
                raw_method = nullptr;
                if (native_kind == COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT && take_refused_navigation(view, uri)) {
                    // WebView2 has dropped the navigation, or drops it with this response: no content, and nothing to show.
                    neoastra_resource_response_t refused{};
                    refused.size = sizeof(refused);
                    refused.version = 1;
                    refused.status_code = 204;
                    ComPtr<ICoreWebView2WebResourceResponse> native_response;
                    if (SUCCEEDED(create_resource_response(view, refused, &native_response)) && native_response) args->put_Response(native_response.Get());
                    return S_OK;
                }
                const auto* scheme = find_scheme(view->environment, uri);
                if (!scheme || !scheme->provider) return S_OK;
                const auto headers = request_headers(native_request.Get());
                if (!neo_resource_request_within_limits(uri, method, headers)) {
                    neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme request metadata exceeded its size limit");
                    return S_OK;
                }
                neoastra_resource_request_t request{};
                request.size = sizeof(request);
                request.version = 1;
                request.uri = neo_string_view(uri);
                request.method = neo_string_view(method);
                request.headers = neo_string_view(headers);
                request.resource_kind = portable_resource_kind(native_kind);
                request.main_frame = native_kind == COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT ? 1u : 0u;
                neoastra_resource_response_t response{};
                response.size = sizeof(response);
                response.version = 1;
                neoastra_result_t provider_result = NEOASTRA_ERROR_NATIVE_FAILURE;
                try { provider_result = scheme->provider(scheme->provider_context, &request, &response); }
                catch (...) { provider_result = NEOASTRA_ERROR_NATIVE_FAILURE; }
                if (provider_result != NEOASTRA_OK) {
                    neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme resource provider failed", provider_result);
                    if (response.release && response.release_context) {
                        try { response.release(response.release_context); } catch (...) { }
                    }
                    response = {};
                    response.size = sizeof(response);
                    response.version = 1;
                    response.status_code = 500;
                }
                ComPtr<ICoreWebView2WebResourceResponse> native_response;
                result = create_resource_response(view, response, &native_response);
                if (response.release && response.release_context) {
                    try { response.release(response.release_context); } catch (...) { }
                }
                if (FAILED(result) || !native_response) {
                    neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "resource", "Could not create a custom-scheme response", result);
                    return S_OK;
                }
                args->put_Response(native_response.Get());
            } catch (...) {
                CoTaskMemFree(raw_uri);
                CoTaskMemFree(raw_method);
                neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme request handling failed");
            }
            return S_OK;
        }).Get(), &state->web_resource_requested);
    if (FAILED(result)) return result;

    if (view->bridge_policy != NEOASTRA_BRIDGE_DISABLED) {
    result = state->core->add_WebMessageReceived(
        Callback<ICoreWebView2WebMessageReceivedEventHandler>([view](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) -> HRESULT {
            LPWSTR source{};
            LPWSTR message{};
            try {
                if (!args) return S_OK;
                auto result = args->get_Source(&source);
                if (SUCCEEDED(result)) result = args->get_WebMessageAsJson(&message);
                if (FAILED(result)) {
                    CoTaskMemFree(source);
                    CoTaskMemFree(message);
                    neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "bridge", "Could not read a WebView2 web message", result);
                    return S_OK;
                }
                auto source_utf8 = take_string(source);
                source = nullptr;
                auto message_utf8 = take_string(message);
                message = nullptr;
                neo_emit_bridge_message(view, message_utf8, source_utf8, true);
            } catch (...) {
                CoTaskMemFree(source);
                CoTaskMemFree(message);
                neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "bridge", "WebView2 web-message handling failed");
            }
            return S_OK;
        }).Get(), &state->message_received);
    if (FAILED(result)) return result;
    state->message_registered = true;
    }

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
                auto* decision = new neoastra_decision;
                neo_configure_decision(decision, view, NEOASTRA_DECISION_PERMISSION, NEOASTRA_DECISION_DENY);
                decision->completion = permission_decided;
                decision->completion_context = context.release();
                neo_emit_view(view, NEOASTRA_EVENT_PERMISSION_REQUESTED, 0, nullptr, &uri,
                              portable_permission(kind), user_initiated ? 1 : 0, decision);
                neo_finish_decision_event(view, decision);
                decision->release();
                return S_OK;
            } catch (...) {
                CoTaskMemFree(raw_uri);
                args->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY);
                deferral->Complete();
                return S_OK;
            }
        }).Get(), &state->permission_requested);
    if (FAILED(result)) return result;

    result = state->core->add_NewWindowRequested(
        Callback<ICoreWebView2NewWindowRequestedEventHandler>([view, state](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT {
            LPWSTR raw_uri{};
            LPWSTR raw_name{};
            BOOL user_initiated{};
            ComPtr<ICoreWebView2Deferral> deferral;
            auto result = args->get_Uri(&raw_uri);
            if (SUCCEEDED(result)) result = args->get_IsUserInitiated(&user_initiated);
            if (SUCCEEDED(result)) result = args->GetDeferral(&deferral);
            if (FAILED(result)) { CoTaskMemFree(raw_uri); return result; }
            try {
                auto uri = take_string(raw_uri);
                raw_uri = nullptr;
                std::string name;
                ComPtr<ICoreWebView2NewWindowRequestedEventArgs2> args2;
                if (SUCCEEDED(args->QueryInterface(IID_PPV_ARGS(&args2))) && SUCCEEDED(args2->get_Name(&raw_name))) {
                    name = take_string(raw_name);
                    raw_name = nullptr;
                }
                auto context = std::make_unique<new_window_decision_context>();
                context->args = args;
                context->deferral = deferral;
                context->core = state->core;
                context->uri = widen(uri);
                auto* decision = new neoastra_decision;
                neo_configure_decision(decision, view, NEOASTRA_DECISION_NEW_WINDOW, NEOASTRA_DECISION_CANCEL);
                decision->completion = new_window_decided;
                decision->completion_context = context.release();
                decision->external_uri = uri;
                neo_emit_view(view, NEOASTRA_EVENT_NEW_WINDOW_REQUESTED, 0, &name, &uri, user_initiated ? NEOASTRA_NEW_WINDOW_REQUEST_USER_INITIATED : 0, 0, decision);
                neo_finish_decision_event(view, decision);
                decision->release();
                return S_OK;
            } catch (...) {
                CoTaskMemFree(raw_uri);
                CoTaskMemFree(raw_name);
                args->put_Handled(TRUE);
                deferral->Complete();
                return S_OK;
            }
        }).Get(), &state->new_window_requested);
    if (FAILED(result)) return result;

    ComPtr<ICoreWebView2_4> core4;
    if (SUCCEEDED(state->core.As(&core4))) {
        result=core4->add_DownloadStarting(Callback<ICoreWebView2DownloadStartingEventHandler>([view](ICoreWebView2*,ICoreWebView2DownloadStartingEventArgs* args)->HRESULT{
            ComPtr<ICoreWebView2DownloadOperation> operation;ComPtr<ICoreWebView2Deferral> deferral;LPWSTR raw_path{};
            auto hr=args->get_DownloadOperation(&operation);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(SUCCEEDED(hr))args->get_ResultFilePath(&raw_path);
            if(FAILED(hr)){CoTaskMemFree(raw_path);args->put_Cancel(TRUE);return S_OK;}
            try{
                auto download_guard=std::make_unique<neoastra_download>(view);auto* download=download_guard.get();auto* platform=new windows_download;download->platform=platform;download->command=windows_download_command;download->platform_destroy=destroy_windows_download;platform->operation=operation;
                LPWSTR raw_uri{},raw_mime{},raw_disposition{};INT64 total{-1};operation->get_Uri(&raw_uri);operation->get_MimeType(&raw_mime);operation->get_ContentDisposition(&raw_disposition);operation->get_TotalBytesToReceive(&total);
                 download->source_uri=take_string(raw_uri);download->total_bytes.store(total<0?UINT64_MAX:static_cast<uint64_t>(total));download->destination_path=take_string(raw_path);download->can_pause=true;raw_path=nullptr;
                auto mime=take_string(raw_mime);auto disposition=take_string(raw_disposition);auto suggested=download->destination_path;const auto slash=suggested.find_last_of("/\\");if(slash!=std::string::npos)suggested.erase(0,slash+1);
                auto context=std::make_unique<download_decision_context>();context->args=args;context->deferral=deferral;context->download=download;
                auto decision_guard=std::make_unique<neoastra_decision>();auto* decision=decision_guard.get();neo_configure_decision(decision,view,NEOASTRA_DECISION_DOWNLOAD_REQUEST,NEOASTRA_DECISION_CANCEL);decision->completion=download_decided;decision->completion_context=context.release();download_guard.release();decision_guard.release();
                neo_event_details details{};details.text2=&mime;details.text3=&disposition;details.value2=1;details.download=download;download->event_published=true;
                neo_emit_view_detailed(view,NEOASTRA_EVENT_DOWNLOAD_REQUESTED,download->id,&suggested,&download->source_uri,total<0?UINT64_MAX:static_cast<uint64_t>(total),0,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;
            }catch(...){CoTaskMemFree(raw_path);args->put_Cancel(TRUE);deferral->Complete();return S_OK;}
        }).Get(),&state->download_starting);
        if(FAILED(result))return result;
    }

    ComPtr<ICoreWebView2_10> core10;
    if(SUCCEEDED(state->core.As(&core10))){result=core10->add_BasicAuthenticationRequested(Callback<ICoreWebView2BasicAuthenticationRequestedEventHandler>([view](ICoreWebView2*,ICoreWebView2BasicAuthenticationRequestedEventArgs* args)->HRESULT{
        LPWSTR raw_uri{},raw_challenge{};ComPtr<ICoreWebView2Deferral> deferral;auto hr=args->get_Uri(&raw_uri);if(SUCCEEDED(hr))hr=args->get_Challenge(&raw_challenge);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(FAILED(hr)){CoTaskMemFree(raw_uri);CoTaskMemFree(raw_challenge);args->put_Cancel(TRUE);return S_OK;}
        try{auto uri=take_string(raw_uri);auto challenge=take_string(raw_challenge);auto context=std::make_unique<basic_auth_context>();context->args=args;context->deferral=deferral;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_AUTHENTICATION,NEOASTRA_DECISION_DEFAULT);decision->completion=basic_auth_decided;decision->completion_context=context.release();neo_event_details details{};details.text2=&challenge;
            neo_emit_view_detailed(view,NEOASTRA_EVENT_AUTHENTICATION_REQUESTED,0,nullptr,&uri,0,0,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;}catch(...){args->put_Cancel(TRUE);deferral->Complete();return S_OK;}
    }).Get(),&state->basic_auth);if(FAILED(result))return result;}

    ComPtr<ICoreWebView2_5> core5;
    if(SUCCEEDED(state->core.As(&core5))){result=core5->add_ClientCertificateRequested(Callback<ICoreWebView2ClientCertificateRequestedEventHandler>([view](ICoreWebView2*,ICoreWebView2ClientCertificateRequestedEventArgs* args)->HRESULT{
        LPWSTR raw_host{};int port{};BOOL proxy{};ComPtr<ICoreWebView2ClientCertificateCollection> certificates;ComPtr<ICoreWebView2Deferral> deferral;auto hr=args->get_Host(&raw_host);if(SUCCEEDED(hr))hr=args->get_Port(&port);if(SUCCEEDED(hr))hr=args->get_IsProxy(&proxy);if(SUCCEEDED(hr))hr=args->get_MutuallyTrustedCertificates(&certificates);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(FAILED(hr)){CoTaskMemFree(raw_host);args->put_Cancel(TRUE);return S_OK;}
        try{auto host=take_string(raw_host);UINT count{};certificates->get_Count(&count);auto context=std::make_unique<client_cert_context>();context->args=args;context->certificates=certificates;context->deferral=deferral;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_CLIENT_CERTIFICATE,NEOASTRA_DECISION_DEFAULT);decision->completion=client_cert_decided;decision->completion_context=context.release();neo_event_details details{};details.value2=proxy?1u:0u;
            neo_emit_view_detailed(view,NEOASTRA_EVENT_CLIENT_CERTIFICATE_REQUESTED,0,&host,nullptr,count,port,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;}catch(...){args->put_Cancel(TRUE);deferral->Complete();return S_OK;}
    }).Get(),&state->client_certificate);if(FAILED(result))return result;}

    ComPtr<ICoreWebView2_14> core14;
    if(SUCCEEDED(state->core.As(&core14))){result=core14->add_ServerCertificateErrorDetected(Callback<ICoreWebView2ServerCertificateErrorDetectedEventHandler>([view](ICoreWebView2*,ICoreWebView2ServerCertificateErrorDetectedEventArgs* args)->HRESULT{
        LPWSTR raw_uri{};COREWEBVIEW2_WEB_ERROR_STATUS status{};ComPtr<ICoreWebView2Certificate> certificate;ComPtr<ICoreWebView2Deferral> deferral;auto hr=args->get_RequestUri(&raw_uri);if(SUCCEEDED(hr))hr=args->get_ErrorStatus(&status);if(SUCCEEDED(hr))hr=args->get_ServerCertificate(&certificate);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(FAILED(hr)){CoTaskMemFree(raw_uri);args->put_Action(COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL);return S_OK;}
        try{auto uri=take_string(raw_uri);LPWSTR raw_subject{};certificate->get_Subject(&raw_subject);auto subject=take_string(raw_subject);auto context=std::make_unique<tls_context>();context->args=args;context->deferral=deferral;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_CERTIFICATE_ERROR,NEOASTRA_DECISION_DENY);decision->completion=tls_decided;decision->completion_context=context.release();neo_event_details details{};details.text2=&subject;
            neo_emit_view_detailed(view,NEOASTRA_EVENT_CERTIFICATE_ERROR,0,nullptr,&uri,0,static_cast<int64_t>(status),decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;}catch(...){args->put_Action(COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL);deferral->Complete();return S_OK;}
    }).Get(),&state->server_certificate_error);if(FAILED(result))return result;}

    result = state->core->add_ProcessFailed(
        Callback<ICoreWebView2ProcessFailedEventHandler>([view](ICoreWebView2*, ICoreWebView2ProcessFailedEventArgs* args) -> HRESULT {
            try {
                COREWEBVIEW2_PROCESS_FAILED_KIND kind{};
                if (FAILED(args->get_ProcessFailedKind(&kind))) return S_OK;
                COREWEBVIEW2_PROCESS_FAILED_REASON reason = COREWEBVIEW2_PROCESS_FAILED_REASON_NORMAL_EXIT;
                int32_t exit_code{};
                std::string description;
                ComPtr<ICoreWebView2ProcessFailedEventArgs2> args2;
                if (SUCCEEDED(args->QueryInterface(IID_PPV_ARGS(&args2)))) {
                    LPWSTR raw_description{};
                    args2->get_Reason(&reason);
                    args2->get_ExitCode(&exit_code);
                    args2->get_ProcessDescription(&raw_description);
                    description = take_string(raw_description);
                }
                const auto value = portable_process_failure(kind, reason);
                neo_emit_view(view, NEOASTRA_EVENT_WEB_PROCESS_TERMINATED, 0,
                              description.empty() ? nullptr : &description, nullptr, value, exit_code);
            } catch (...) {
                // Never allow allocation or conversion failures to cross the COM callback boundary.
            }
            return S_OK;
        }).Get(), &state->process_failed);
    return result;
}

LRESULT handle_session_message(neoastra_app_t* app, UINT message, WPARAM wparam) {
    if (message == WM_QUERYENDSESSION) {
        // Windows does not permit an asynchronous veto here. Publish a bounded best-effort
        // request and truthfully report that cancellation is unavailable.
        if (app->session_end_phase.exchange(1, std::memory_order_acq_rel) == 0) {
            auto* decision = new(std::nothrow) neoastra_decision_t;
            if (decision) {
                decision->kind = NEOASTRA_DECISION_APPLICATION_QUIT;
                decision->default_action = NEOASTRA_DECISION_ALLOW;
                decision->deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
                decision->attach_app(app);
                neo_emit_app(app, NEOASTRA_EVENT_APPLICATION_SESSION_END, 0, nullptr, nullptr, 0, 0, decision);
                neo_finish_decision_event(app, decision);
                decision->release();
            } else neo_emit_app(app, NEOASTRA_EVENT_APPLICATION_SESSION_END, 0, nullptr, nullptr, 0);
        }
        return TRUE;
    }
    if (!wparam) {
        if (app->session_end_phase.exchange(0, std::memory_order_acq_rel) != 0)
            neo_emit_app(app, NEOASTRA_EVENT_APPLICATION_SESSION_END, 0, nullptr, nullptr, 3);
    } else if (const auto previous = app->session_end_phase.exchange(2, std::memory_order_acq_rel); previous != 2) {
        neo_emit_app(app, NEOASTRA_EVENT_APPLICATION_SESSION_END, 0, nullptr, nullptr, previous == 0 ? 2 : 1);
        neoastra_app_quit(app, 0);
    }
    return 0;
}

LRESULT CALLBACK dispatcher_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
    }
    auto* app = reinterpret_cast<neoastra_app_t*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (app && (message == WM_QUERYENDSESSION || message == WM_ENDSESSION)) return handle_session_message(app, message, wparam);
    if (message == dispatch_message && app) { neo_drain_dispatch(app); return 0; }
    if (message == quit_message && app) { neo_drain_dispatch(app); PostQuitMessage(app->exit_code.load()); return 0; }
    if (message == destroy_app_message && app) { neo_destroy_app_on_ui(app); return 0; }
    if (message == WM_TIMER && app) {
        auto* decision = reinterpret_cast<neoastra_decision_t*>(wparam);
        auto* state = static_cast<windows_app*>(app->platform);
        KillTimer(hwnd, wparam);
        if (state) {
            auto& timers = state->decision_timers;
            timers.erase(std::remove(timers.begin(), timers.end(), decision), timers.end());
        }
        decision->expire();
        decision->release();
        return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

constexpr wchar_t caption_class[] = L"NeoAstra.CaptionControls";
constexpr wchar_t grip_class[] = L"NeoAstra.ResizeGrip";
// Logical units matching the Windows 11 caption buttons.
constexpr int caption_button_width = 46;
constexpr int caption_default_height = 32;
constexpr int top_grip_height = 4;
constexpr int top_grip_corner = 16;
enum caption_button : int { caption_none = -1, caption_minimize = 0, caption_maximize = 1, caption_close = 2 };

ATOM register_class(const wchar_t* name, WNDPROC procedure);

UINT window_dpi(HWND hwnd) noexcept { const auto dpi = GetDpiForWindow(hwnd); return dpi ? dpi : USER_DEFAULT_SCREEN_DPI; }
// What the system reports depends on the awareness of the process: a process that declares none is told 96 on every display.
double window_scale_factor(HWND hwnd) noexcept { return window_dpi(hwnd) / static_cast<double>(USER_DEFAULT_SCREEN_DPI); }
int scale_for_dpi(int value, UINT dpi) noexcept { return MulDiv(value, static_cast<int>(dpi), USER_DEFAULT_SCREEN_DPI); }
int title_bar_height(const neoastra_window_t* window) noexcept { return window->title_bar.height > 0 ? window->title_bar.height : caption_default_height; }
POINT client_point(HWND hwnd, LPARAM screen) noexcept {
    POINT point{static_cast<short>(LOWORD(screen)), static_cast<short>(HIWORD(screen))};
    ScreenToClient(hwnd, &point);
    return point;
}

// The caption is replaced only while the window owns a standard frame; borderless and fullscreen windows have none.
bool title_bar_extended(const neoastra_window_t* window, HWND hwnd) noexcept {
    const auto* state = static_cast<const windows_window*>(window->platform);
    return state && !state->fullscreen && window->title_bar.style != NEOASTRA_TITLE_BAR_DEFAULT &&
           (static_cast<DWORD>(GetWindowLongW(hwnd, GWL_STYLE)) & WS_CAPTION) == WS_CAPTION;
}

// Portable sizes describe the client area, while Win32 sizes and track limits describe the whole window.
SIZE frame_size(const neoastra_window_t* window, HWND hwnd) noexcept {
    RECT frame{};
    AdjustWindowRectExForDpi(&frame, static_cast<DWORD>(GetWindowLongW(hwnd, GWL_STYLE)), FALSE,
                             static_cast<DWORD>(GetWindowLongW(hwnd, GWL_EXSTYLE)), window_dpi(hwnd));
    // An extended title bar hands the caption to the client and keeps the left, right, and bottom frame.
    if (title_bar_extended(window, hwnd)) frame.top = 0;
    return {frame.right - frame.left, frame.bottom - frame.top};
}

SIZE outer_size(const neoastra_window_t* window, HWND hwnd, int32_t client_width, int32_t client_height) noexcept {
    const auto frame = frame_size(window, hwnd);
    return {std::max(client_width, 1) + frame.cx, std::max(client_height, 1) + frame.cy};
}

// Portable bounds pair the window's own origin with the size of its client area.
void sync_bounds(neoastra_window_t* window, HWND hwnd) noexcept {
    RECT frame{}, client{};
    if (!GetWindowRect(hwnd, &frame) || !GetClientRect(hwnd, &client)) return;
    std::lock_guard lock(window->state_mutex);
    window->bounds = {frame.left, frame.top, client.right, client.bottom};
}

bool top_edge_resizable(HWND hwnd) noexcept {
    return (static_cast<DWORD>(GetWindowLongW(hwnd, GWL_STYLE)) & WS_THICKFRAME) != 0 && !IsZoomed(hwnd);
}

// Keeps the system frame (shadow, resize borders, snapping) while handing the caption area to the client.
LRESULT extend_client_into_title_bar(HWND hwnd, WPARAM wparam, LPARAM lparam) noexcept {
    // Both forms of the message begin with the proposed window rectangle.
    auto* client = reinterpret_cast<RECT*>(lparam);
    const auto top = client->top;
    const auto result = DefWindowProcW(hwnd, WM_NCCALCSIZE, wparam, lparam);
    client->top = top;
    if (!IsZoomed(hwnd)) return result;
    // A maximized window overhangs its monitor by the frame thickness on every side.
    const auto dpi = window_dpi(hwnd);
    const auto resizable = (static_cast<DWORD>(GetWindowLongW(hwnd, GWL_STYLE)) & WS_THICKFRAME) != 0;
    client->top += GetSystemMetricsForDpi(resizable ? SM_CYSIZEFRAME : SM_CYFIXEDFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
    // Leave a sliver for an auto-hide taskbar; a client area covering the whole monitor would keep it from appearing.
    APPBARDATA bar{};
    bar.cbSize = sizeof(bar);
    MONITORINFO monitor{};
    monitor.cbSize = sizeof(monitor);
    if ((SHAppBarMessage(ABM_GETSTATE, &bar) & ABS_AUTOHIDE) != 0 && GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &monitor)) {
        const auto hidden_bar = [&monitor](UINT edge) noexcept {
            APPBARDATA query{};
            query.cbSize = sizeof(query);
            query.uEdge = edge;
            query.rc = monitor.rcMonitor;
            return SHAppBarMessage(ABM_GETAUTOHIDEBAREX, &query) != 0;
        };
        if (hidden_bar(ABE_TOP)) client->top += 2;
        if (hidden_bar(ABE_BOTTOM)) client->bottom -= 2;
        if (hidden_bar(ABE_LEFT)) client->left += 2;
        if (hidden_bar(ABE_RIGHT)) client->right -= 2;
    }
    return result;
}

struct caption_color { float red{}, green{}, blue{}, alpha{}; }; // Premultiplied channels in [0, 1].
caption_color premultiply(neoastra_color_t color, float opacity = 1.f) noexcept {
    const auto alpha = static_cast<float>(color.alpha) / 255.f * opacity;
    return {static_cast<float>(color.red) / 255.f * alpha, static_cast<float>(color.green) / 255.f * alpha, static_cast<float>(color.blue) / 255.f * alpha, alpha};
}
caption_color scaled(caption_color color, float factor) noexcept { return {color.red * factor, color.green * factor, color.blue * factor, color.alpha * factor}; }
caption_color over(caption_color top, caption_color bottom) noexcept {
    const auto keep = 1.f - top.alpha;
    return {top.red + bottom.red * keep, top.green + bottom.green * keep, top.blue + bottom.blue * keep, top.alpha + bottom.alpha * keep};
}
bool is_light(neoastra_color_t color) noexcept { return color.red * 299 + color.green * 587 + color.blue * 114 > 127500; }

bool system_uses_dark_theme() noexcept {
    DWORD light = 1, size = sizeof(light);
    return SHRegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"AppsUseLightTheme",
                          SRRF_RT_REG_DWORD, nullptr, &light, &size) == ERROR_SUCCESS && light == 0;
}

int CALLBACK font_found(const LOGFONTW*, const TEXTMETRICW*, DWORD, LPARAM found) { *reinterpret_cast<bool*>(found) = true; return 0; }
const wchar_t* caption_font(HDC dc) noexcept {
    // Windows 11 ships the Fluent glyphs; Windows 10 only has their MDL2 predecessors at the same code points.
    LOGFONTW query{};
    query.lfCharSet = DEFAULT_CHARSET;
    wcscpy_s(query.lfFaceName, L"Segoe Fluent Icons");
    bool found{};
    EnumFontFamiliesExW(dc, &query, font_found, reinterpret_cast<LPARAM>(&found), 0);
    return found ? L"Segoe Fluent Icons" : L"Segoe MDL2 Assets";
}

bool caption_button_enabled(HWND parent, int button) noexcept {
    const auto style = static_cast<DWORD>(GetWindowLongW(parent, GWL_STYLE));
    return button == caption_minimize ? (style & WS_MINIMIZEBOX) != 0 : button == caption_maximize ? (style & WS_MAXIMIZEBOX) != 0 : button == caption_close;
}
int caption_button_from_hit(WPARAM hit) noexcept { return hit == HTMINBUTTON ? caption_minimize : hit == HTMAXBUTTON ? caption_maximize : hit == HTCLOSE ? caption_close : caption_none; }

void render_caption(neoastra_window_t* window, windows_window* state) noexcept {
    if (!state->caption) return;
    RECT rect{};
    GetClientRect(state->caption, &rect);
    const int width = rect.right, height = rect.bottom;
    if (width <= 0 || height <= 0) return;
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(info.bmiHeader);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    const auto screen = GetDC(nullptr);
    const auto dc = CreateCompatibleDC(screen);
    void* bits{};
    const auto bitmap = dc ? CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0) : nullptr;
    if (bitmap && bits) {
        const auto previous_bitmap = SelectObject(dc, bitmap);
        std::memset(bits, 0, static_cast<size_t>(width) * static_cast<size_t>(height) * 4u);
        // GDI text carries no alpha, so the glyphs are drawn white on black and read back as coverage.
        const auto font = CreateFontW(-scale_for_dpi(10, window_dpi(state->hwnd)), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                                      CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH | FF_DONTCARE, caption_font(dc));
        const auto previous_font = SelectObject(dc, font);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(255, 255, 255));
        const wchar_t glyphs[3] = {L'', IsZoomed(state->hwnd) ? L'' : L'', L''};
        for (int button = 0; button < 3; ++button) {
            RECT cell{width * button / 3, 0, width * (button + 1) / 3, height};
            DrawTextW(dc, &glyphs[button], 1, &cell, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_NOCLIP);
        }
        GdiFlush();
        SelectObject(dc, previous_font);
        DeleteObject(font);

        const auto& configured = window->title_bar;
        const auto light_symbols = configured.symbol_color.alpha ? is_light(configured.symbol_color)
                                 : configured.background_color.alpha ? !is_light(configured.background_color) : system_uses_dark_theme();
        const neoastra_color_t tint = light_symbols ? neoastra_color_t{255, 255, 255, 255} : neoastra_color_t{0, 0, 0, 255};
        const auto symbol = configured.symbol_color.alpha ? configured.symbol_color : tint;
        const auto base = premultiply(configured.background_color);
        const auto opaque_base = premultiply(light_symbols ? neoastra_color_t{32, 32, 32, 255} : neoastra_color_t{243, 243, 243, 255});
        caption_color backgrounds[3]{}, symbols[3]{};
        for (int button = 0; button < 3; ++button) {
            const auto enabled = caption_button_enabled(state->hwnd, button);
            const auto hot = enabled && state->caption_hot == button;
            const auto pressed = hot && state->caption_pressed == button;
            backgrounds[button] = base;
            symbols[button] = premultiply(symbol, !enabled || (!state->active && !hot) ? .36f : 1.f);
            if (hot && button == caption_close) {
                backgrounds[button] = over(premultiply({196, 43, 28, 255}, pressed ? .9f : 1.f), base);
                symbols[button] = premultiply({255, 255, 255, 255}, pressed ? .7f : 1.f);
            } else if (hot) {
                backgrounds[button] = over(premultiply(tint, light_symbols ? (pressed ? .04f : .06f) : (pressed ? .024f : .037f)), base);
            }
        }
        auto* pixel = static_cast<uint8_t*>(bits);
        for (int y = 0; y < height; ++y) {
            for (int x = 0; x < width; ++x, pixel += 4) {
                const auto button = std::min(x * 3 / width, 2);
                auto color = over(scaled(symbols[button], static_cast<float>(pixel[0]) / 255.f), backgrounds[button]);
                if (!state->caption_layered) color = over(color, opaque_base);
                // Fully transparent layered pixels are not hit-testable, so keep an imperceptible alpha floor.
                const auto alpha = std::clamp(static_cast<int>(color.alpha * 255.f + .5f), 1, 255);
                pixel[0] = static_cast<uint8_t>(std::min(static_cast<int>(color.blue * 255.f + .5f), alpha));
                pixel[1] = static_cast<uint8_t>(std::min(static_cast<int>(color.green * 255.f + .5f), alpha));
                pixel[2] = static_cast<uint8_t>(std::min(static_cast<int>(color.red * 255.f + .5f), alpha));
                pixel[3] = static_cast<uint8_t>(alpha);
            }
        }
        if (state->caption_layered) {
            POINT origin{};
            SIZE size{width, height};
            BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
            UpdateLayeredWindow(state->caption, screen, nullptr, &size, dc, &origin, 0, &blend, ULW_ALPHA);
        } else if (const auto target = GetDC(state->caption)) {
            BitBlt(target, 0, 0, width, height, dc, 0, 0, SRCCOPY);
            ReleaseDC(state->caption, target);
        }
        SelectObject(dc, previous_bitmap);
    }
    if (bitmap) DeleteObject(bitmap);
    if (dc) DeleteDC(dc);
    ReleaseDC(nullptr, screen);
}

void set_caption_state(neoastra_window_t* window, windows_window* state, int hot, int pressed) noexcept {
    if (state->caption_hot == hot && state->caption_pressed == pressed) return;
    state->caption_hot = hot;
    state->caption_pressed = pressed;
    render_caption(window, state);
}

// Native caption buttons drawn over the browser. Reporting the standard hit codes keeps Windows 11 snap layouts available.
LRESULT CALLBACK caption_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams));
    auto* window = reinterpret_cast<neoastra_window_t*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    auto* state = window ? static_cast<windows_window*>(window->platform) : nullptr;
    if (!state || !state->hwnd) return DefWindowProcW(hwnd, message, wparam, lparam);
    switch (message) {
        case WM_NCHITTEST: {
            RECT rect{};
            GetClientRect(hwnd, &rect);
            const auto point = client_point(hwnd, lparam);
            const auto dpi = window_dpi(hwnd);
            if (top_edge_resizable(state->hwnd) && point.y < scale_for_dpi(top_grip_height, dpi))
                return point.x >= rect.right - scale_for_dpi(top_grip_corner, dpi) ? HTTOPRIGHT : HTTOP;
            const auto button = std::clamp(static_cast<int>(point.x * 3 / std::max(rect.right, 1L)), 0, 2);
            if (!caption_button_enabled(state->hwnd, button)) return HTBORDER;
            return button == caption_minimize ? HTMINBUTTON : button == caption_maximize ? HTMAXBUTTON : HTCLOSE;
        }
        case WM_NCMOUSEMOVE:
            if (!state->caption_tracking) {
                TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE | TME_NONCLIENT, hwnd, 0};
                state->caption_tracking = TrackMouseEvent(&track) != FALSE;
            }
            set_caption_state(window, state, caption_button_from_hit(wparam), state->caption_pressed);
            return 0;
        case WM_NCMOUSELEAVE:
            state->caption_tracking = false;
            set_caption_state(window, state, caption_none, caption_none);
            return 0;
        case WM_NCLBUTTONDOWN: case WM_NCLBUTTONDBLCLK: {
            if (wparam == HTTOP || wparam == HTTOPRIGHT) return SendMessageW(state->hwnd, message, wparam, lparam);
            // DefWindowProc must not see these: it would draw and track legacy caption buttons.
            const auto button = caption_button_from_hit(wparam);
            set_caption_state(window, state, button, button);
            return 0;
        }
        case WM_NCLBUTTONUP: {
            const auto button = caption_button_from_hit(wparam);
            const auto pressed = state->caption_pressed;
            set_caption_state(window, state, button, caption_none);
            if (button != caption_none && button == pressed)
                PostMessageW(state->hwnd, WM_SYSCOMMAND, button == caption_minimize ? SC_MINIMIZE : button == caption_close ? SC_CLOSE : IsZoomed(state->hwnd) ? SC_RESTORE : SC_MAXIMIZE, 0);
            return 0;
        }
        case WM_NCRBUTTONDOWN: case WM_NCRBUTTONUP: case WM_NCRBUTTONDBLCLK: case WM_NCMBUTTONDOWN: case WM_NCMBUTTONUP: case WM_NCMBUTTONDBLCLK:
            return 0;
        case WM_ERASEBKGND:
            return 1;
        case WM_PAINT: {
            PAINTSTRUCT paint{};
            BeginPaint(hwnd, &paint);
            EndPaint(hwnd, &paint);
            render_caption(window, state);
            return 0;
        }
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

// Invisible strip that restores top-edge resizing where the browser covers the former caption.
LRESULT CALLBACK grip_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    const auto parent = GetAncestor(hwnd, GA_PARENT);
    if (message == WM_NCHITTEST && parent) {
        RECT rect{}, parent_rect{};
        GetClientRect(hwnd, &rect);
        GetClientRect(parent, &parent_rect);
        const auto point = client_point(hwnd, lparam);
        const auto corner = scale_for_dpi(top_grip_corner, window_dpi(hwnd));
        if (point.x < corner) return HTTOPLEFT;
        return rect.right >= parent_rect.right && point.x >= rect.right - corner ? HTTOPRIGHT : HTTOP;
    }
    if ((message == WM_NCLBUTTONDOWN || message == WM_NCLBUTTONDBLCLK) && parent) return SendMessageW(parent, message, wparam, lparam);
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

void destroy_title_bar_windows(windows_window* state) noexcept {
    for (auto* child : {&state->caption, &state->top_grip}) {
        if (!*child) continue;
        SetWindowLongPtrW(*child, GWLP_USERDATA, 0);
        DestroyWindow(*child);
        *child = nullptr;
    }
}

// Positions the caption buttons and resize grip above the browser views; call after anything that changes the frame or child order.
void layout_title_bar(neoastra_window_t* window) noexcept {
    auto* state = static_cast<windows_window*>(window->platform);
    if (!state || !state->hwnd) return;
    const auto extended = title_bar_extended(window, state->hwnd);
    const auto controls = extended && window->title_bar.style == NEOASTRA_TITLE_BAR_OVERLAY;
    const auto grip = extended && top_edge_resizable(state->hwnd);
    const auto instance = GetModuleHandleW(nullptr);
    if (controls && !state->caption && register_class(caption_class, caption_proc)) {
        // Layered child windows require a Windows 8+ application manifest; without one the buttons fall back to an opaque strip.
        state->caption = CreateWindowExW(WS_EX_LAYERED, caption_class, L"", WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 0, 0, state->hwnd, nullptr, instance, window);
        state->caption_layered = state->caption != nullptr;
        if (!state->caption) state->caption = CreateWindowExW(0, caption_class, L"", WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 0, 0, state->hwnd, nullptr, instance, window);
    }
    if (grip && !state->top_grip && !state->grip_unavailable && register_class(grip_class, grip_proc)) {
        // Opaque to hit testing yet never drawn: no redirection bitmap backs the layered window.
        state->top_grip = CreateWindowExW(WS_EX_LAYERED | WS_EX_NOREDIRECTIONBITMAP, grip_class, L"", WS_CHILD, 0, 0, 0, 0, state->hwnd, nullptr, instance, nullptr);
        if (state->top_grip) SetLayeredWindowAttributes(state->top_grip, 0, 255, LWA_ALPHA);
        else state->grip_unavailable = true;
    }
    RECT client{};
    GetClientRect(state->hwnd, &client);
    const auto dpi = window_dpi(state->hwnd);
    int controls_width{};
    if (controls && state->caption) {
        controls_width = scale_for_dpi(caption_button_width, dpi) * 3;
        SetWindowPos(state->caption, HWND_TOP, client.right - controls_width, 0, controls_width, scale_for_dpi(title_bar_height(window), dpi), SWP_NOACTIVATE | SWP_SHOWWINDOW);
        render_caption(window, state);
    } else if (state->caption) {
        ShowWindow(state->caption, SW_HIDE);
    }
    if (grip && state->top_grip) SetWindowPos(state->top_grip, HWND_TOP, 0, 0, std::max(client.right - controls_width, 0L), scale_for_dpi(top_grip_height, dpi), SWP_NOACTIVATE | SWP_SHOWWINDOW);
    else if (state->top_grip) ShowWindow(state->top_grip, SW_HIDE);
}

LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
        auto* window = static_cast<neoastra_window_t*>(create->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(window));
    }
    auto* window = reinterpret_cast<neoastra_window_t*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (!window) return DefWindowProcW(hwnd, message, wparam, lparam);
    switch (message) {
        case WM_CLOSE:
            if (window->force_closing) {
                window->force_closing = false;
                DestroyWindow(hwnd);
            } else {
                neo_window_request_close(window, NEOASTRA_WINDOW_CLOSE_USER, true);
            }
            return 0;
        case WM_QUERYENDSESSION:
            return handle_session_message(window->app, message, wparam);
        case WM_ENDSESSION:
            handle_session_message(window->app, message, wparam);
            if (wparam) {
                window->force_closing = true;
                DestroyWindow(hwnd);
            }
            return 0;
        case WM_NCCALCSIZE:
            if (title_bar_extended(window, hwnd)) return extend_client_into_title_bar(hwnd, wparam, lparam);
            break;
        case WM_NCACTIVATE:
            if (auto* native = static_cast<windows_window*>(window->platform)) {
                native->active = wparam != FALSE;
                render_caption(window, native);
                // The caption belongs to the client area now, so suppress the legacy non-client repaint.
                if (title_bar_extended(window, hwnd)) return DefWindowProcW(hwnd, message, wparam, -1);
            }
            break;
        case WM_SETTINGCHANGE:
            if (auto* native = static_cast<windows_window*>(window->platform)) render_caption(window, native);
            break;
        case WM_DESTROY:
            {auto* state=static_cast<windows_window*>(window->platform);if(state)destroy_title_bar_windows(state);if(state&&state->modal_active&&window->owner){auto* owner=static_cast<windows_window*>(window->owner->platform);if(owner&&owner->modal_children&&--owner->modal_children==0&&owner->hwnd){EnableWindow(owner->hwnd,TRUE);SetActiveWindow(owner->hwnd);}state->modal_active=false;}if(state)state->hwnd=nullptr;}
            neo_window_closed(window);
            return 0;
        case WM_MOVE:
            // The message carries the client origin, which is not where a position request places the window.
            sync_bounds(window, hwnd);
            neo_emit_app(window->app, NEOASTRA_EVENT_WINDOW_MOVED, window->id);
            break;
        case WM_SIZE: {
            auto* native = static_cast<windows_window*>(window->platform);
            const auto state = native && native->fullscreen ? NEOASTRA_WINDOW_FULLSCREEN
                             : wparam == SIZE_MINIMIZED ? NEOASTRA_WINDOW_MINIMIZED
                             : wparam == SIZE_MAXIMIZED ? NEOASTRA_WINDOW_MAXIMIZED : NEOASTRA_WINDOW_NORMAL;
            // A window that leaves the fullscreen state says which state it reached once it is there.
            const auto settled = !native || !native->leaving_fullscreen;
            const auto state_changed = settled && native && native->reported_state != state;
            if (settled && native) native->reported_state = state;
            { std::lock_guard lock(window->state_mutex); window->bounds.width = LOWORD(lparam); window->bounds.height = HIWORD(lparam);if(settled)window->state=state; }
            for (auto* view : window->views) if (view && view->fill_parent) neo_platform_view_set_bounds(view);
            layout_title_bar(window);
            neo_emit_app(window->app, NEOASTRA_EVENT_WINDOW_RESIZED, window->id);
            if (state_changed) neo_emit_app(window->app, NEOASTRA_EVENT_WINDOW_STATE_CHANGED, window->id, nullptr, nullptr, state);
            break;
        }
        case WM_DPICHANGED: {
            const auto* suggested = reinterpret_cast<const RECT*>(lparam);
            SetWindowPos(hwnd, nullptr, suggested->left, suggested->top, suggested->right - suggested->left,
                         suggested->bottom - suggested->top, SWP_NOACTIVATE | SWP_NOZORDER);
            layout_title_bar(window);
            neo_window_set_scale_factor(window, HIWORD(wparam) / static_cast<double>(USER_DEFAULT_SCREEN_DPI));
            return 0;
        }
        case WM_GETMINMAXINFO: {
            auto* constraints = reinterpret_cast<MINMAXINFO*>(lparam);
            const auto frame = frame_size(window, hwnd);
            std::lock_guard lock(window->state_mutex);
            if (window->minimum_size.width > 0) constraints->ptMinTrackSize.x = window->minimum_size.width + frame.cx;
            if (window->minimum_size.height > 0) constraints->ptMinTrackSize.y = window->minimum_size.height + frame.cy;
            if (window->maximum_size.width > 0) constraints->ptMaxTrackSize.x = window->maximum_size.width + frame.cx;
            if (window->maximum_size.height > 0) constraints->ptMaxTrackSize.y = window->maximum_size.height + frame.cy;
            return 0;
        }
        case WM_ACTIVATE:
            // Keyboard focus normally sits in the browser's own child window, so activation is what reports the window as in use.
            neo_emit_app(window->app, NEOASTRA_EVENT_WINDOW_FOCUS_CHANGED, window->id, nullptr, nullptr, LOWORD(wparam) != WA_INACTIVE ? 1 : 0);
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
    neoastra_string_callback_t callback{};
    void* context{};
    neoastra_operation_t* operation{};
    std::string result;
    neoastra_result_t requested{};
    neoastra_error_t* error{};
};

struct profile_completion {
    neoastra_completion_callback_t callback{};
    void* context{};
    neoastra_operation_t* operation{};
    neoastra_result_t requested{};
    neoastra_error_t* error{};
};

void NEOASTRA_CALL finish_profile_operation(void* pointer) {
    std::unique_ptr<profile_completion> completion(static_cast<profile_completion*>(pointer));
    neoastra_result_t result{};
    if (completion->operation->try_complete(completion->requested, result)) {
        completion->callback(completion->context, result, result == completion->requested ? completion->error : nullptr);
    }
    if (completion->error) completion->error->release();
    completion->operation->release();
}

neoastra_result_t schedule_profile_completion(neoastra_profile_t* profile, neoastra_completion_callback_t callback,
                                                 void* context, neoastra_operation_t* operation,
                                                 neoastra_result_t requested, neoastra_error_t* error,
                                                 neoastra_error_t** start_error) noexcept {
    auto completion = std::make_unique<profile_completion>(profile_completion{callback, context, operation, requested, error});
    const auto result = neoastra_app_dispatch(profile->environment->app, finish_profile_operation, completion.get());
    if (result != NEOASTRA_OK) {
        if (error) error->release();
        return neo_fail(start_error, result, "Could not schedule WebView2 profile completion");
    }
    completion.release();
    return NEOASTRA_OK;
}

void complete_cookie_buffer(neoastra_buffer_callback_t callback, void* context, neoastra_operation_t* operation,
                            neoastra_result_t requested, neoastra_buffer_t* buffer,
                            neoastra_error_t* error) noexcept {
    neoastra_result_t result{};
    if (operation->try_complete(requested, result)) {
        callback(context, result, result == NEOASTRA_OK ? buffer : nullptr, result == requested ? error : nullptr);
        if (result != NEOASTRA_OK && buffer) buffer->release();
    } else if (buffer) buffer->release();
    if (error) error->release();
    operation->release();
}

windows_profile* require_profile(neoastra_profile_t* profile, neoastra_error_t** error) noexcept {
    auto* state = static_cast<windows_profile*>(profile->platform);
    if (!state || !state->cookies || !state->profile) {
        neo_fail(error, NEOASTRA_ERROR_NOT_INITIALIZED, "The WebView2 profile is not initialized; create a view for the profile first", 0, "webview2");
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
    neoastra_completion_callback_t callback{};
    void* context{};
    neoastra_operation_t* operation{};
    uint32_t attempts{};
};

void complete_cookie_delete(const std::shared_ptr<cookie_delete_state>& state, HRESULT result) noexcept {
    const auto requested = SUCCEEDED(result) ? NEOASTRA_OK : NEOASTRA_ERROR_NATIVE_FAILURE;
    auto* error = FAILED(result) ? make_error(requested, "Could not delete WebView2 cookie", result) : nullptr;
    neoastra_result_t actual{};
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

void NEOASTRA_CALL finish_script(void* pointer) {
    std::unique_ptr<script_completion> completion(static_cast<script_completion*>(pointer));
    neoastra_result_t result{};
    if (completion->operation->try_complete(completion->requested, result)) {
        completion->callback(completion->context, result, result == NEOASTRA_OK ? neo_string_view(completion->result) : neoastra_string_view_t{}, completion->error);
    }
    if (completion->error) completion->error->release();
    completion->operation->release();
}

} // namespace

bool neo_platform_initialize(neoastra_app_t* app, neoastra_error_t** error) noexcept {
    try {
        auto* state = new windows_app;
        const auto hr = OleInitialize(nullptr);
        if (SUCCEEDED(hr)) state->owns_com = true;
        else if (hr != S_FALSE) { delete state; neo_fail(error, hr == RPC_E_CHANGED_MODE ? NEOASTRA_ERROR_WRONG_THREAD : NEOASTRA_ERROR_NATIVE_FAILURE, "COM STA initialization failed", hr, "com"); return false; }
        if (!register_class(dispatch_class, dispatcher_proc) || !register_class(window_class, window_proc)) { const auto code=GetLastError(); if(state->owns_com)OleUninitialize();delete state;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Win32 window class registration failed",code,"win32");return false; }
        // A hidden top-level window, rather than HWND_MESSAGE, is required to receive session-end broadcasts.
        state->dispatcher = CreateWindowExW(0, dispatch_class, L"", WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, GetModuleHandleW(nullptr), app);
        if (!state->dispatcher) { const auto code=GetLastError();if(state->owns_com)OleUninitialize();delete state;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Win32 dispatcher creation failed",code,"win32");return false; }
        app->platform = state; return true;
    } catch (...) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Windows backend initialization failed"); return false; }
}

void neo_platform_shutdown(neoastra_app_t* app) noexcept {
    auto* state = static_cast<windows_app*>(app->platform); if (!state) return;
    for (auto* decision : state->decision_timers) { KillTimer(state->dispatcher, reinterpret_cast<UINT_PTR>(decision)); decision->abandon(); decision->release(); }
    state->decision_timers.clear();
    if (state->dispatcher && IsWindow(state->dispatcher)) DestroyWindow(state->dispatcher);
    if (state->owns_com && app->ui_thread == std::this_thread::get_id()) OleUninitialize();
    delete state; app->platform = nullptr;
}

bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); return state&&state->dispatcher&&PostMessageW(state->dispatcher,destroy_app_message,0,0)!=FALSE; }

int32_t neo_platform_run(neoastra_app_t* app) noexcept {
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return app->exit_code.load();
}
void neo_platform_quit(neoastra_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); if(state&&state->dispatcher)PostMessageW(state->dispatcher,quit_message,0,0); }
void neo_platform_wake(neoastra_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); if(state&&state->dispatcher)PostMessageW(state->dispatcher,dispatch_message,0,0); }
bool neo_platform_schedule_decision_timeout(neoastra_app_t* app,neoastra_decision_t* decision) noexcept {auto* state=static_cast<windows_app*>(app->platform);if(!state||!state->dispatcher)return false;const auto remaining=std::chrono::duration_cast<std::chrono::milliseconds>(decision->deadline-std::chrono::steady_clock::now()).count();const auto delay=static_cast<UINT>(std::clamp<int64_t>(remaining+1,1,USER_TIMER_MAXIMUM));decision->retain();try{state->decision_timers.push_back(decision);}catch(...){decision->release();return false;}if(!SetTimer(state->dispatcher,reinterpret_cast<UINT_PTR>(decision),delay,nullptr)){state->decision_timers.pop_back();decision->release();return false;}return true;}

bool neo_platform_window_create(neoastra_window_t* window, const neoastra_window_options_t* options, neoastra_error_t** error) noexcept {
    try {
        auto* state=new windows_window; window->platform=state;
        const auto owner=window->owner?static_cast<windows_window*>(window->owner->platform)->hwnd:nullptr;
        auto style=WS_OVERLAPPEDWINDOW; if((options->flags&1u)==0) style&=~WS_THICKFRAME;
        if ((options->flags & 2u) == 0) style = WS_POPUP;
        const auto title=widen(window->title);
        DWORD extended=(options->flags&8u)?WS_EX_TOPMOST:0;if((options->flags&16u)==0)extended|=WS_EX_TOOLWINDOW;
        const auto requested=window->bounds;
        const auto x=(options->flags&32u)?CW_USEDEFAULT:requested.x;
        const auto y=(options->flags&32u)?CW_USEDEFAULT:requested.y;
        state->hwnd=CreateWindowExW(extended,window_class,title.c_str(),style,x,y,std::max(requested.width,1),std::max(requested.height,1),owner,nullptr,GetModuleHandleW(nullptr),window);
        if(!state->hwnd){const auto code=GetLastError();delete state;window->platform=nullptr;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Win32 window creation failed",code,"win32");return false;}
        layout_title_bar(window);
        // The frame depends on the DPI of the monitor the window landed on, so the client size is applied once the window exists.
        const auto outer=outer_size(window,state->hwnd,requested.width,requested.height);
        if(options->flags&64u){RECT area{};if(owner){RECT owner_rect{};GetWindowRect(owner,&owner_rect);area=owner_rect;}else{MONITORINFO monitor{};monitor.cbSize=sizeof(MONITORINFO);if(GetMonitorInfoW(MonitorFromWindow(state->hwnd,MONITOR_DEFAULTTOPRIMARY),&monitor))area=monitor.rcWork;}SetWindowPos(state->hwnd,nullptr,area.left+((area.right-area.left)-outer.cx)/2,area.top+((area.bottom-area.top)-outer.cy)/2,outer.cx,outer.cy,SWP_NOZORDER|SWP_NOACTIVATE);}
        else SetWindowPos(state->hwnd,nullptr,0,0,outer.cx,outer.cy,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE);
        sync_bounds(window,state->hwnd);
        // A window is told of a scale only when it changes, so the one it starts with is read here.
        neo_window_set_scale_factor(window,window_scale_factor(state->hwnd),false);
        state->modal=(options->flags&128u)!=0;if(state->modal&&(options->flags&4u)&&window->owner){auto* owner_state=static_cast<windows_window*>(window->owner->platform);if(owner_state&&owner_state->hwnd){if(owner_state->modal_children++==0)EnableWindow(owner_state->hwnd,FALSE);state->modal_active=true;}}
        if(options->flags&4u){const auto show=options->state==NEOASTRA_WINDOW_MINIMIZED?SW_SHOWMINIMIZED:options->state==NEOASTRA_WINDOW_MAXIMIZED?SW_SHOWMAXIMIZED:SW_SHOW;ShowWindow(state->hwnd,show);if(options->state==NEOASTRA_WINDOW_FULLSCREEN){{std::lock_guard lock(window->state_mutex);window->state=NEOASTRA_WINDOW_FULLSCREEN;}neo_platform_window_set_state(window);}}
        return true;
    } catch(const std::exception& ex){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,ex.what());return false;}
}
void neo_platform_window_destroy(neoastra_window_t* window) noexcept { auto* state=static_cast<windows_window*>(window->platform);if(!state)return;if(state->modal_active&&window->owner){auto* owner=static_cast<windows_window*>(window->owner->platform);if(owner&&owner->modal_children&&--owner->modal_children==0&&owner->hwnd){EnableWindow(owner->hwnd,TRUE);SetActiveWindow(owner->hwnd);}}if(state->hwnd&&IsWindow(state->hwnd))DestroyWindow(state->hwnd);delete state;window->platform=nullptr; }
neoastra_result_t neo_platform_window_show(neoastra_window_t* w,bool visible) noexcept {auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEOASTRA_ERROR_DISPOSED;if(!visible){sync_window_view_visibility(w,false);ShowWindow(s->hwnd,SW_HIDE);if(s->modal_active&&w->owner){auto* owner=static_cast<windows_window*>(w->owner->platform);if(owner&&owner->modal_children&&--owner->modal_children==0&&owner->hwnd)EnableWindow(owner->hwnd,TRUE);s->modal_active=false;}return NEOASTRA_OK;}if(s->modal&&!s->modal_active&&w->owner){auto* owner=static_cast<windows_window*>(w->owner->platform);if(owner&&owner->hwnd){if(owner->modal_children++==0)EnableWindow(owner->hwnd,FALSE);s->modal_active=true;}}neoastra_window_state_t desired{};{std::lock_guard lock(w->state_mutex);desired=w->state;}const auto command=desired==NEOASTRA_WINDOW_MINIMIZED?SW_SHOWMINIMIZED:desired==NEOASTRA_WINDOW_MAXIMIZED?SW_SHOWMAXIMIZED:SW_SHOW;ShowWindow(s->hwnd,command);sync_window_view_visibility(w,true);layout_title_bar(w);if(desired==NEOASTRA_WINDOW_FULLSCREEN){{std::lock_guard lock(w->state_mutex);w->state=desired;}return neo_platform_window_set_state(w);}return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_activate(neoastra_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEOASTRA_ERROR_DISPOSED;SetForegroundWindow(s->hwnd);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_force_close(neoastra_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);return s&&s->hwnd&&PostMessageW(s->hwnd,WM_CLOSE,0,0)?NEOASTRA_OK:NEOASTRA_ERROR_DISPOSED;}
neoastra_result_t neo_platform_window_set_title(neoastra_window_t* w) noexcept {try{auto* s=static_cast<windows_window*>(w->platform);auto title=widen(w->title);return s&&s->hwnd&&SetWindowTextW(s->hwnd,title.c_str())?NEOASTRA_OK:NEOASTRA_ERROR_DISPOSED;}catch(...){return NEOASTRA_ERROR_INVALID_ARGUMENT;}}
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t* w) noexcept {
    auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEOASTRA_ERROR_DISPOSED;
    const auto outer=outer_size(w,s->hwnd,w->bounds.width,w->bounds.height);
    if(!SetWindowPos(s->hwnd,nullptr,w->bounds.x,w->bounds.y,outer.cx,outer.cy,SWP_NOZORDER|SWP_NOACTIVATE))return NEOASTRA_ERROR_DISPOSED;
    // A request the system adjusted to a placement the window already had sends no move or size message.
    if(!IsIconic(s->hwnd))sync_bounds(w,s->hwnd);
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEOASTRA_ERROR_DISPOSED;SetWindowPos(s->hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_set_state(neoastra_window_t* w) noexcept {
    auto* state = static_cast<windows_window*>(w->platform);
    if (!state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
    // The window is told of each size it takes below and writes the state it then has over the one that was asked for.
    const auto requested = w->state;
    const auto visible = IsWindowVisible(state->hwnd) != FALSE;
    if (requested == NEOASTRA_WINDOW_FULLSCREEN && !state->fullscreen) {
        if (!visible) return NEOASTRA_OK;
        state->restored_style = static_cast<DWORD>(GetWindowLongW(state->hwnd, GWL_STYLE));
        state->restored_placement.length = sizeof(WINDOWPLACEMENT);
        GetWindowPlacement(state->hwnd, &state->restored_placement);
        MONITORINFO monitor{};
        monitor.cbSize = sizeof(monitor);
        if (!GetMonitorInfoW(MonitorFromWindow(state->hwnd, MONITOR_DEFAULTTONEAREST), &monitor)) return NEOASTRA_ERROR_NATIVE_FAILURE;
        SetWindowLongW(state->hwnd, GWL_STYLE, static_cast<LONG>(state->restored_style & ~WS_OVERLAPPEDWINDOW));
        state->fullscreen = true;
        if (SetWindowPos(state->hwnd, HWND_TOP, monitor.rcMonitor.left, monitor.rcMonitor.top,
                         monitor.rcMonitor.right - monitor.rcMonitor.left, monitor.rcMonitor.bottom - monitor.rcMonitor.top,
                         SWP_NOOWNERZORDER | SWP_FRAMECHANGED)) return NEOASTRA_OK;
        SetWindowLongW(state->hwnd, GWL_STYLE, static_cast<LONG>(state->restored_style));
        state->fullscreen = false;
        return NEOASTRA_ERROR_NATIVE_FAILURE;
    }
    if (state->fullscreen) {
        // The window stops being fullscreen before it gets its frame and its placement back, so that its frame is computed
        // for the title bar it has. On the way it is told of sizes that are not states of its own, and it says which state it
        // reached when it is there. It used to keep saying that it was fullscreen where no later size told it otherwise, and
        // to come back maximized, whatever it was asked for, where it had been maximized before.
        state->fullscreen = false;
        state->leaving_fullscreen = true;
        // The placement has the bounds that the window goes back to. Its state is the one that is asked for now, which may
        // be another one than the window had, and a hidden window stays hidden.
        state->restored_placement.showCmd = !visible ? SW_HIDE
                                          : requested == NEOASTRA_WINDOW_MINIMIZED ? SW_SHOWMINIMIZED
                                          : requested == NEOASTRA_WINDOW_MAXIMIZED ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
        SetWindowLongW(state->hwnd, GWL_STYLE, static_cast<LONG>(state->restored_style));
        SetWindowPlacement(state->hwnd, &state->restored_placement);
        SetWindowPos(state->hwnd, nullptr, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        state->leaving_fullscreen = false;
        layout_title_bar(w);
        // A hidden window takes the state it was asked for when it is shown.
        const auto reached = !visible ? requested
                           : IsIconic(state->hwnd) ? NEOASTRA_WINDOW_MINIMIZED
                           : IsZoomed(state->hwnd) ? NEOASTRA_WINDOW_MAXIMIZED : NEOASTRA_WINDOW_NORMAL;
        const auto changed = state->reported_state != reached;
        state->reported_state = reached;
        { std::lock_guard lock(w->state_mutex); w->state = reached; }
        if (changed) neo_emit_app(w->app, NEOASTRA_EVENT_WINDOW_STATE_CHANGED, w->id, nullptr, nullptr, reached);
        return NEOASTRA_OK;
    }
    if (!visible) return NEOASTRA_OK;
    const auto command = requested == NEOASTRA_WINDOW_MINIMIZED ? SW_MINIMIZE
                       : requested == NEOASTRA_WINDOW_MAXIMIZED ? SW_MAXIMIZE : SW_RESTORE;
    ShowWindow(state->hwnd, command);
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_window_set_attribute(neoastra_window_t* w,neoastra_window_attribute_t attribute,bool enabled) noexcept {
    auto* state=static_cast<windows_window*>(w->platform);if(!state||!state->hwnd)return NEOASTRA_ERROR_DISPOSED;
    auto attributes=w->attributes;if(enabled)attributes|=1u<<attribute;else attributes&=~(1u<<attribute);
    if(attribute==NEOASTRA_WINDOW_ALWAYS_ON_TOP)return SetWindowPos(state->hwnd,enabled?HWND_TOPMOST:HWND_NOTOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;
    if(attribute==NEOASTRA_WINDOW_SHOW_IN_TASKBAR){auto extended=static_cast<DWORD>(GetWindowLongW(state->hwnd,GWL_EXSTYLE));if(enabled){extended&=~WS_EX_TOOLWINDOW;extended|=WS_EX_APPWINDOW;}else{extended&=~WS_EX_APPWINDOW;extended|=WS_EX_TOOLWINDOW;}SetWindowLongW(state->hwnd,GWL_EXSTYLE,static_cast<LONG>(extended));return SetWindowPos(state->hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED)?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}
    auto style=(attributes&(1u<<NEOASTRA_WINDOW_DECORATED))?static_cast<DWORD>(WS_OVERLAPPEDWINDOW):static_cast<DWORD>(WS_POPUP);
    if((attributes&(1u<<NEOASTRA_WINDOW_RESIZABLE))==0)style&=~static_cast<DWORD>(WS_THICKFRAME|WS_MAXIMIZEBOX);
    const auto current=static_cast<DWORD>(GetWindowLongW(state->hwnd,GWL_STYLE));style|=current&static_cast<DWORD>(WS_VISIBLE|WS_DISABLED|WS_CLIPCHILDREN|WS_CLIPSIBLINGS);
    if(state->fullscreen){state->restored_style=style;return NEOASTRA_OK;}
    SetWindowLongW(state->hwnd,GWL_STYLE,static_cast<LONG>(style));
    const auto changed=SetWindowPos(state->hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);
    layout_title_bar(w);
    return changed?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;
}
neoastra_result_t neo_platform_window_set_title_bar(neoastra_window_t* w) noexcept {
    auto* state=static_cast<windows_window*>(w->platform);if(!state||!state->hwnd)return NEOASTRA_ERROR_DISPOSED;
    const auto changed=SetWindowPos(state->hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);
    layout_title_bar(w);
    return changed?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;
}
neoastra_result_t neo_platform_window_get_title_bar(neoastra_window_t* w,neoastra_title_bar_t* value) noexcept {
    auto* state=static_cast<windows_window*>(w->platform);if(!state||!state->hwnd)return NEOASTRA_ERROR_DISPOSED;
    value->height=w->title_bar.style==NEOASTRA_TITLE_BAR_DEFAULT?0:title_bar_height(w);
    // Fullscreen and borderless windows have no caption, so no caption buttons cover the content.
    value->right_inset=w->title_bar.style==NEOASTRA_TITLE_BAR_OVERLAY&&state->caption&&title_bar_extended(w,state->hwnd)?caption_button_width*3:0;
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_window_begin_drag(neoastra_window_t* w) noexcept {auto* state=static_cast<windows_window*>(w->platform);if(!state||!state->hwnd)return NEOASTRA_ERROR_DISPOSED;ReleaseCapture();SendMessageW(state->hwnd,WM_NCLBUTTONDOWN,HTCAPTION,0);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_begin_resize(neoastra_window_t* w,neoastra_window_resize_edge_t edge) noexcept {auto* state=static_cast<windows_window*>(w->platform);if(!state||!state->hwnd)return NEOASTRA_ERROR_DISPOSED;static constexpr WPARAM hit_tests[]={HTLEFT,HTTOP,HTRIGHT,HTBOTTOM,HTTOPLEFT,HTTOPRIGHT,HTBOTTOMLEFT,HTBOTTOMRIGHT};ReleaseCapture();SendMessageW(state->hwnd,WM_NCLBUTTONDOWN,hit_tests[edge],0);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_get_handle(neoastra_window_t* w,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* h) noexcept {if(kind!=NEOASTRA_NATIVE_HANDLE_WIN32_HWND)return NEOASTRA_ERROR_NOT_SUPPORTED;auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEOASTRA_ERROR_DISPOSED;h->kind=kind;h->value=s->hwnd;return NEOASTRA_OK;}

bool neo_platform_environment_create_async(neoastra_environment_t* environment,const neoastra_environment_options_t* options,neo_platform_created_callback_t callback,void* context,neoastra_error_t** error) noexcept {
    try {
        const auto runtime_path = widen(neo_string(options->browser_runtime_path));
        const auto user_data = widen(neo_string(options->user_data_root));
        LPWSTR version{};
        const auto version_result = GetAvailableCoreWebView2BrowserVersionString(runtime_path.empty() ? nullptr : runtime_path.c_str(), &version);
        if (FAILED(version_result)) { neo_fail(error, NEOASTRA_ERROR_RUNTIME_UNAVAILABLE, "Microsoft Edge WebView2 Runtime is unavailable. Install the Evergreen Runtime or configure BrowserRuntimePath.", version_result, "webview2"); return false; }
        auto* state = new windows_environment;
        state->version = take_string(version);
        state->private_mode = options->private_mode != 0;
        environment->platform = state;
        auto environment_options = Make<CoreWebView2EnvironmentOptions>();
        const auto arguments = widen(neo_string(options->browser_arguments));
        const auto languages = widen(neo_string(options->preferred_languages));
        if (!arguments.empty()) environment_options->put_AdditionalBrowserArguments(arguments.c_str());
        if (!languages.empty()) environment_options->put_Language(languages.c_str());
        std::vector<ComPtr<ICoreWebView2CustomSchemeRegistration>> registrations;
        std::vector<ICoreWebView2CustomSchemeRegistration*> registration_pointers;
        registrations.reserve(environment->custom_schemes.size());
        registration_pointers.reserve(environment->custom_schemes.size());
        for (const auto& scheme : environment->custom_schemes) {
            if ((scheme.flags & NEOASTRA_CUSTOM_SCHEME_SERVICE_WORKERS) != 0) {
                neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, "WebView2 custom schemes do not support service workers", E_NOTIMPL, "webview2");
                return false;
            }
            auto registration = Make<CoreWebView2CustomSchemeRegistration>(widen(scheme.name).c_str());
            if (!registration) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Could not allocate a WebView2 custom-scheme registration", E_OUTOFMEMORY, "webview2"); return false; }
            auto configure_result = registration->put_HasAuthorityComponent((scheme.flags & NEOASTRA_CUSTOM_SCHEME_HAS_AUTHORITY) ? TRUE : FALSE);
            if (SUCCEEDED(configure_result)) configure_result = registration->put_TreatAsSecure((scheme.flags & NEOASTRA_CUSTOM_SCHEME_SECURE) ? TRUE : FALSE);
            if (SUCCEEDED(configure_result) && (scheme.flags & NEOASTRA_CUSTOM_SCHEME_CORS_ENABLED) != 0 && !scheme.allowed_origins.empty()) {
                std::vector<std::wstring> origins;
                std::vector<LPCWSTR> origin_pointers;
                origins.reserve(scheme.allowed_origins.size());
                origin_pointers.reserve(scheme.allowed_origins.size());
                for (const auto& origin : scheme.allowed_origins) origins.push_back(widen(origin));
                for (const auto& origin : origins) origin_pointers.push_back(origin.c_str());
                configure_result = registration->SetAllowedOrigins(static_cast<UINT32>(origin_pointers.size()), origin_pointers.data());
            }
            if (FAILED(configure_result)) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Could not configure a WebView2 custom scheme", configure_result, "webview2"); return false; }
            registration_pointers.push_back(registration.Get());
            registrations.push_back(std::move(registration));
        }
        if (!registration_pointers.empty()) {
            const auto configure_result = environment_options->SetCustomSchemeRegistrations(static_cast<UINT32>(registration_pointers.size()), registration_pointers.data());
            if (FAILED(configure_result)) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Could not register WebView2 custom schemes", configure_result, "webview2"); return false; }
        }
        const auto result = CreateCoreWebView2EnvironmentWithOptions(
            runtime_path.empty() ? nullptr : runtime_path.c_str(), user_data.empty() ? nullptr : user_data.c_str(), environment_options.Get(),
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([environment, callback, context](HRESULT result, ICoreWebView2Environment* created) -> HRESULT {
                auto* state = static_cast<windows_environment*>(environment->platform);
                if (!state) { callback(context, make_error(NEOASTRA_ERROR_DISPOSED, "Application shutdown completed before WebView2 environment creation", E_ABORT)); return S_OK; }
                if (FAILED(result) || !created) callback(context, make_error(NEOASTRA_ERROR_RUNTIME_UNAVAILABLE, "WebView2 environment creation failed", result));
                else { state->value = created; callback(context, nullptr); }
                return S_OK;
            }).Get());
        if (FAILED(result)) { neo_fail(error, NEOASTRA_ERROR_RUNTIME_UNAVAILABLE, "WebView2 environment creation could not be started", result, "webview2"); return false; }
        return true;
    } catch (const std::exception& ex) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, ex.what(), 0, "webview2"); return false; }
}
void neo_platform_environment_destroy(neoastra_environment_t* environment) noexcept { delete static_cast<windows_environment*>(environment->platform); environment->platform=nullptr; }

bool neo_platform_profile_create(neoastra_profile_t* profile, neoastra_error_t** error) noexcept {
    try { profile->platform = new windows_profile; return true; }
    catch (...) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Could not allocate WebView2 profile state", E_OUTOFMEMORY, "webview2"); return false; }
}

void neo_platform_profile_destroy(neoastra_profile_t* profile) noexcept {
    delete static_cast<windows_profile*>(profile->platform);
    profile->platform = nullptr;
}

neoastra_result_t neo_platform_profile_get_cookies(neoastra_profile_t* profile, const std::string& uri,
                                                       neoastra_buffer_callback_t callback, void* context,
                                                       neoastra_operation_t* operation, neoastra_error_t** error) noexcept {
    try {
        auto* state = require_profile(profile, error);
        if (!state) return NEOASTRA_ERROR_NOT_INITIALIZED;
        const auto result = state->cookies->GetCookies(
            widen(uri).c_str(),
            Callback<ICoreWebView2GetCookiesCompletedHandler>([callback, context, operation](HRESULT result, ICoreWebView2CookieList* list) -> HRESULT {
                if (FAILED(result) || !list) {
                    complete_cookie_buffer(callback, context, operation, NEOASTRA_ERROR_NATIVE_FAILURE, nullptr,
                                           make_error(NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 cookie retrieval failed", result));
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
                        complete_cookie_buffer(callback, context, operation, NEOASTRA_ERROR_NATIVE_FAILURE, nullptr,
                                               make_error(NEOASTRA_ERROR_NATIVE_FAILURE, "Could not read WebView2 cookies", current));
                        return S_OK;
                    }
                    json.push_back(']');
                    complete_cookie_buffer(callback, context, operation, NEOASTRA_OK,
                                           new neoastra_buffer(std::vector<uint8_t>(json.begin(), json.end())), nullptr);
                } catch (...) {
                    complete_cookie_buffer(callback, context, operation, NEOASTRA_ERROR_NATIVE_FAILURE, nullptr,
                                           make_error(NEOASTRA_ERROR_NATIVE_FAILURE, "Could not serialize WebView2 cookies", E_OUTOFMEMORY));
                }
                return S_OK;
            }).Get());
        return SUCCEEDED(result) ? NEOASTRA_OK : neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 cookie retrieval could not be started", result, "webview2");
    } catch (const std::exception& ex) { return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, ex.what(), 0, "webview2"); }
}

neoastra_result_t neo_platform_profile_set_cookie(neoastra_profile_t* profile, const neoastra_cookie_t* cookie,
                                                      neoastra_completion_callback_t callback, void* context,
                                                      neoastra_operation_t* operation, neoastra_error_t** error) noexcept {
    try {
        auto* state = require_profile(profile, error);
        if (!state) return NEOASTRA_ERROR_NOT_INITIALIZED;
        ComPtr<ICoreWebView2Cookie> value;
        auto result = state->cookies->CreateCookie(widen(neo_string(cookie->name)).c_str(), widen(neo_string(cookie->value)).c_str(),
                                                   widen(neo_string(cookie->domain)).c_str(), widen(neo_string(cookie->path)).c_str(), &value);
        if (SUCCEEDED(result)) result = value->put_IsSecure((cookie->flags & 1u) ? TRUE : FALSE);
        if (SUCCEEDED(result)) result = value->put_IsHttpOnly((cookie->flags & 2u) ? TRUE : FALSE);
        if (SUCCEEDED(result) && (cookie->flags & 4u) == 0 && cookie->expires_unix_ms > 0) result = value->put_Expires(cookie->expires_unix_ms / 1000.0);
        if (SUCCEEDED(result) && cookie->same_site > 0) result = value->put_SameSite(static_cast<COREWEBVIEW2_COOKIE_SAME_SITE_KIND>(cookie->same_site - 1u));
        if (SUCCEEDED(result)) result = state->cookies->AddOrUpdateCookie(value.Get());
        if (FAILED(result)) return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Could not set WebView2 cookie", result, "webview2");
        return schedule_profile_completion(profile, callback, context, operation, NEOASTRA_OK, nullptr, error);
    } catch (const std::exception& ex) { return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, ex.what(), 0, "webview2"); }
}

neoastra_result_t neo_platform_profile_delete_cookie(neoastra_profile_t* profile, const neoastra_cookie_t* cookie,
                                                         neoastra_completion_callback_t callback, void* context,
                                                         neoastra_operation_t* operation, neoastra_error_t** error) noexcept {
    try {
        auto* state = require_profile(profile, error);
        if (!state) return NEOASTRA_ERROR_NOT_INITIALIZED;
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
        return SUCCEEDED(result) ? NEOASTRA_OK : neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 cookie deletion could not be started", result, "webview2");
    } catch (const std::exception& ex) { return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, ex.what(), 0, "webview2"); }
}

neoastra_result_t neo_platform_profile_clear_data(neoastra_profile_t* profile, neoastra_data_kind_t kinds,
                                                      int64_t start_unix_ms, int64_t end_unix_ms,
                                                      neoastra_completion_callback_t callback, void* context,
                                                      neoastra_operation_t* operation, neoastra_error_t** error) noexcept {
    auto* state = require_profile(profile, error);
    if (!state) return NEOASTRA_ERROR_NOT_INITIALIZED;
    if (kinds != NEOASTRA_DATA_ALL && (kinds & NEOASTRA_DATA_PERMISSIONS) != 0) {
        return neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, "WebView2 does not expose portable permission-data clearing", 0, "webview2");
    }
    ComPtr<ICoreWebView2Profile2> profile2;
    auto result = state->profile.As(&profile2);
    if (FAILED(result)) return neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, "This WebView2 runtime does not support browsing-data clearing", result, "webview2");
    auto handler = Callback<ICoreWebView2ClearBrowsingDataCompletedHandler>([callback, context, operation](HRESULT result) -> HRESULT {
        auto* error = FAILED(result) ? make_error(NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 browsing-data clearing failed", result) : nullptr;
        neoastra_result_t actual{};
        const auto requested = FAILED(result) ? NEOASTRA_ERROR_NATIVE_FAILURE : NEOASTRA_OK;
        if (operation->try_complete(requested, actual)) callback(context, actual, actual == requested ? error : nullptr);
        if (error) error->release();
        operation->release();
        return S_OK;
    });
    if (kinds == NEOASTRA_DATA_ALL) result = profile2->ClearBrowsingDataAll(handler.Get());
    else {
        auto native = static_cast<COREWEBVIEW2_BROWSING_DATA_KINDS>(0);
        if (kinds & NEOASTRA_DATA_COOKIES) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_COOKIES;
        if (kinds & NEOASTRA_DATA_CACHE) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_DISK_CACHE | COREWEBVIEW2_BROWSING_DATA_KINDS_CACHE_STORAGE;
        if (kinds & NEOASTRA_DATA_LOCAL_STORAGE) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_LOCAL_STORAGE;
        if (kinds & NEOASTRA_DATA_INDEXED_DB) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_INDEXED_DB;
        if (kinds & NEOASTRA_DATA_SERVICE_WORKERS) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_SERVICE_WORKERS;
        if (kinds & NEOASTRA_DATA_DOWNLOAD_HISTORY) native |= COREWEBVIEW2_BROWSING_DATA_KINDS_DOWNLOAD_HISTORY;
        const bool all_time = start_unix_ms == INT64_MIN && end_unix_ms == INT64_MAX;
        result = all_time ? profile2->ClearBrowsingData(native, handler.Get())
                          : profile2->ClearBrowsingDataInTimeRange(native, start_unix_ms / 1000.0, end_unix_ms / 1000.0, handler.Get());
    }
    return SUCCEEDED(result) ? NEOASTRA_OK : neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 browsing-data clearing could not be started", result, "webview2");
}

void register_drop_target(neoastra_view_t* view, windows_view* state, HWND window) {
    if (!window) return;
    const auto existing = std::find_if(state->drop_registrations.begin(), state->drop_registrations.end(),
                                       [window](const windows_drop_registration& value) { return value.window == window; });
    if (existing != state->drop_registrations.end()) {
        // WebView2 can replace its OLE registration without touching our window property.
        // Re-register the retained target after navigation instead of treating the property
        // as proof that our target is still active.
        (void)RevokeDragDrop(window);
        if (SUCCEEDED(RegisterDragDrop(window, existing->target.Get()))) {
            (void)SetPropW(window, L"NeoAstra.DropTarget", existing->target.Get());
        } else {
            if (GetPropW(window, L"NeoAstra.DropTarget") == existing->target.Get()) RemovePropW(window, L"NeoAstra.DropTarget");
            state->drop_registrations.erase(existing);
        }
        return;
    }
    ComPtr<IDropTarget> target;
    target.Attach(new (std::nothrow) view_drop_target(view, window));
    if (!target) return;
    (void)RevokeDragDrop(window);
    if (SUCCEEDED(RegisterDragDrop(window, target.Get()))) {
        (void)SetPropW(window, L"NeoAstra.DropTarget", target.Get());
        state->drop_registrations.push_back({window, std::move(target)});
    }
}

struct enumerate_drop_context { neoastra_view_t* view{}; windows_view* state{}; };
BOOL CALLBACK enumerate_drop_window(HWND window, LPARAM parameter) {
    auto* context = reinterpret_cast<enumerate_drop_context*>(parameter);
    register_drop_target(context->view, context->state, window);
    return TRUE;
}

namespace {

void register_view_drop_targets(neoastra_view_t* view, windows_view* state) {
    if (!state) return;
    const auto parent = view_parent(view);
    if (!IsWindow(state->drop_window) || !parent || !IsChild(parent, state->drop_window)) {
        state->drop_window = nullptr;
        HWND candidate{};
        while (parent && (candidate = FindWindowExW(parent, candidate, L"Chrome_WidgetWin_0", nullptr)) != nullptr) {
            if (!GetPropW(candidate, L"NeoAstra.DropTarget")) { state->drop_window = candidate; break; }
        }
    }
    std::erase_if(state->drop_registrations, [](const windows_drop_registration& registration) {
        return !IsWindow(registration.window) || GetPropW(registration.window, L"NeoAstra.DropTarget") != registration.target.Get();
    });
    if (state->drop_window) {
        register_drop_target(view, state, state->drop_window);
        enumerate_drop_context context{view, state};
        // OLE hit testing reaches nested WebView2 renderer windows, including windows
        // owned by the renderer process, so replacing only Chrome_WidgetWin_0 is insufficient.
        EnumChildWindows(state->drop_window, enumerate_drop_window, reinterpret_cast<LPARAM>(&context));
    }
    if (state->drop_registrations.empty()) {
        neo_log(view->environment->app, NEOASTRA_LOG_WARNING, "drag-drop", "Could not replace any WebView2 OLE drop targets");
    }
}

} // namespace

namespace {
// F12 and Ctrl+Shift+I are browser accelerators, so turning those off would also take the DevTools shortcut away.
// It stays available for as long as DevTools themselves are enabled.
HRESULT register_devtools_shortcut(neoastra_view_t* view, windows_view* state) {
    return state->controller->add_AcceleratorKeyPressed(Callback<ICoreWebView2AcceleratorKeyPressedEventHandler>(
        [view](ICoreWebView2Controller*, ICoreWebView2AcceleratorKeyPressedEventArgs* args) -> HRESULT {
            auto* state = static_cast<windows_view*>(view->platform);
            COREWEBVIEW2_KEY_EVENT_KIND kind{};
            COREWEBVIEW2_PHYSICAL_KEY_STATUS status{};
            UINT key{};
            if (!state || !state->core || FAILED(args->get_KeyEventKind(&kind)) || kind != COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN ||
                FAILED(args->get_VirtualKey(&key)) || FAILED(args->get_PhysicalKeyStatus(&status)) || status.WasKeyDown) return S_OK;
            const auto control = GetKeyState(VK_CONTROL) < 0, shift = GetKeyState(VK_SHIFT) < 0, alt = GetKeyState(VK_MENU) < 0;
            if (!(key == VK_F12 && !control && !shift && !alt) && !(key == 'I' && control && shift && !alt)) return S_OK;
            ComPtr<ICoreWebView2Settings> settings;
            ComPtr<ICoreWebView2Settings3> settings3;
            BOOL accelerators = TRUE, devtools = FALSE;
            // While browser accelerators are on, the engine opens DevTools itself.
            if (FAILED(state->core->get_Settings(&settings)) || FAILED(settings.As(&settings3)) ||
                FAILED(settings3->get_AreBrowserAcceleratorKeysEnabled(&accelerators)) || accelerators) return S_OK;
            if (SUCCEEDED(settings->get_AreDevToolsEnabled(&devtools)) && devtools && SUCCEEDED(state->core->OpenDevToolsWindow())) (void)args->put_Handled(TRUE);
            return S_OK;
        }).Get(), &state->accelerator_key);
}
} // namespace

bool neo_platform_view_create_async(neoastra_view_t* view,const neoastra_view_options_t*,neo_platform_created_callback_t callback,void* context,neoastra_error_t** error) noexcept {
    auto* environment = static_cast<windows_environment*>(view->environment->platform);
    const auto parent = view_parent(view);
    if (!environment || !environment->value || !parent) { neo_fail(error, NEOASTRA_ERROR_INVALID_STATE, "WebView2 environment or parent window is not ready", 0, "webview2"); return false; }
    auto* state = new windows_view;
    view->platform = state;
    auto completed = Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>([view, callback, context](HRESULT result, ICoreWebView2Controller* controller) -> HRESULT {
            auto* state = static_cast<windows_view*>(view->platform);
            if (!state) { if (controller) controller->Close(); callback(context, make_error(NEOASTRA_ERROR_DISPOSED, "Application shutdown completed before WebView2 view creation", E_ABORT)); return S_OK; }
            if (FAILED(result) || !controller) { callback(context, make_error(NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 controller creation failed", result)); return S_OK; }
            state->controller = controller;
            result = controller->get_CoreWebView2(&state->core);
            if (SUCCEEDED(result)) result = controller->put_Bounds(view_bounds(view));
            // A controller created for a hidden owned window must begin hidden too. Otherwise
            // WebView2 never observes the visibility transition when its parent is first shown
            // and can leave the controller's composition surface blank.
            if (SUCCEEDED(result) && view->window) result = controller->put_IsVisible(IsWindowVisible(view_parent(view)));
            ComPtr<ICoreWebView2Controller4> controller4;
            if (SUCCEEDED(result)) result=state->controller.As(&controller4);
            if (SUCCEEDED(result)) result=controller4->put_AllowExternalDrop(FALSE);
            if (SUCCEEDED(result)) register_view_drop_targets(view, state);
            if (SUCCEEDED(result) && view->window) {
                // Lets `app-region: drag` content move, maximize, and open the system menu of its host window like a native caption.
                ComPtr<ICoreWebView2Settings> settings;
                ComPtr<ICoreWebView2Settings9> settings9;
                if (SUCCEEDED(state->core->get_Settings(&settings)) && SUCCEEDED(settings.As(&settings9))) (void)settings9->put_IsNonClientRegionSupportEnabled(TRUE);
                // The new controller window is created above the caption overlays.
                layout_title_bar(view->window);
            }
            if (SUCCEEDED(result)) result = register_view_events(view, state);
            if (SUCCEEDED(result)) result = register_devtools_shortcut(view, state);
            if (SUCCEEDED(result) && view->profile) {
                auto* profile = static_cast<windows_profile*>(view->profile->platform);
                ComPtr<ICoreWebView2_13> core13;
                if (profile && SUCCEEDED(state->core.As(&core13))) {
                    core13->get_Profile(&profile->profile);
                    core13->get_CookieManager(&profile->cookies);
                }
            }
            if (FAILED(result)) { char message[96]{}; std::snprintf(message,sizeof(message),"WebView2 view initialization failed (0x%08lx)",static_cast<unsigned long>(result)); callback(context, make_error(NEOASTRA_ERROR_NATIVE_FAILURE, message, result)); }
            else callback(context, nullptr);
            return S_OK;
        });
    HRESULT result{};
    // WebView2 has no private environment: InPrivate is chosen for each controller, and a controller created
    // without options uses the persistent default profile of the user-data folder. A private environment
    // therefore makes every view InPrivate, with or without a profile and whatever that profile asked for.
    // A popup target comes through here with the environment and profile of its opener, so it gets the same
    // WebView2 profile, as put_NewWindow requires. A runtime without controller options fails the creation
    // rather than storing the data of a private view.
    const bool in_private = environment->private_mode || (view->profile && view->profile->ephemeral);
    if (view->profile || in_private) {
        ComPtr<ICoreWebView2Environment10> environment10;
        ComPtr<ICoreWebView2ControllerOptions> controller_options;
        result = environment->value.As(&environment10);
        if (SUCCEEDED(result)) result = environment10->CreateCoreWebView2ControllerOptions(&controller_options);
        if (SUCCEEDED(result) && view->profile && !view->profile->name.empty()) {
            try { result = controller_options->put_ProfileName(widen(view->profile->name).c_str()); }
            catch (...) { result = E_INVALIDARG; }
        }
        if (SUCCEEDED(result)) result = controller_options->put_IsInPrivateModeEnabled(in_private ? TRUE : FALSE);
        if (SUCCEEDED(result)) result = environment10->CreateCoreWebView2ControllerWithOptions(parent, controller_options.Get(), completed.Get());
    } else {
        result = environment->value->CreateCoreWebView2Controller(parent, completed.Get());
    }
    if (FAILED(result)) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 controller creation could not be started", result, "webview2"); return false; }
    return true;
}
void neo_platform_view_destroy(neoastra_view_t* view) noexcept { auto* state=static_cast<windows_view*>(view->platform);if(!state)return;remove_view_events(state);for(auto& registration:state->drop_registrations){if(GetPropW(registration.window,L"NeoAstra.DropTarget")==registration.target.Get())RemovePropW(registration.window,L"NeoAstra.DropTarget");(void)RevokeDragDrop(registration.window);}state->drop_registrations.clear();if(state->controller)state->controller->Close();delete state;view->platform=nullptr; }
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t* view) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->controller)return NEOASTRA_ERROR_NOT_INITIALIZED;return SUCCEEDED(state->controller->put_Bounds(view_bounds(view)))?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}
neoastra_result_t neo_platform_view_navigate(neoastra_view_t* view,const std::string& uri,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(uri);const auto result=state->core->Navigate(value.c_str());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 navigation failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t* view,const std::string& uri,const std::string& method,const std::string& headers,const uint8_t* body,uint64_t body_length,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);auto* environment=static_cast<windows_environment*>(view->environment->platform);if(!state||!state->core||!environment||!environment->value)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");if(method.empty()||body_length>ULONG_MAX)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"Invalid WebView2 request method or body length");ComPtr<IStream> content;if(body_length){HRESULT result=CreateStreamOnHGlobal(nullptr,TRUE,&content);if(FAILED(result))return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Could not allocate WebView2 request body",result,"webview2");ULONG written{};result=content->Write(body,static_cast<ULONG>(body_length),&written);LARGE_INTEGER start{};if(SUCCEEDED(result)&&written==body_length)result=content->Seek(start,STREAM_SEEK_SET,nullptr);if(FAILED(result)||written!=body_length)return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Could not write WebView2 request body",FAILED(result)?result:E_FAIL,"webview2");}ComPtr<ICoreWebView2Environment2> environment2;ComPtr<ICoreWebView2_2> core2;auto result=environment->value.As(&environment2);if(SUCCEEDED(result))result=state->core.As(&core2);if(FAILED(result))return neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"This WebView2 runtime does not support request navigation",result,"webview2");ComPtr<ICoreWebView2WebResourceRequest> request;result=environment2->CreateWebResourceRequest(widen(uri).c_str(),widen(method).c_str(),content.Get(),widen(headers).c_str(),&request);if(SUCCEEDED(result))result=core2->NavigateWithWebResourceRequest(request.Get());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 request navigation failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_load_html(neoastra_view_t* view,const std::string& html,const std::string&,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(html);const auto result=state->core->NavigateToString(value.c_str());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 HTML loading failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_command(neoastra_view_t* view,uint32_t command) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return NEOASTRA_ERROR_NOT_INITIALIZED;HRESULT result=E_INVALIDARG;switch(command){case 0:result=state->core->Stop();break;case 1:result=state->core->Reload();break;case 2:
    // WebView2 has no reload that leaves its cache out; the DevTools protocol has one.
    result=state->core->CallDevToolsProtocolMethod(L"Page.reload",L"{\"ignoreCache\":true}",Callback<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>([](HRESULT,LPCWSTR)->HRESULT{return S_OK;}).Get());
    if(FAILED(result))result=state->core->Reload();
    break;case 3:result=state->core->GoBack();break;case 4:result=state->core->GoForward();break;}return SUCCEEDED(result)?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t* view,const std::string& script,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(script);const auto result=state->core->ExecuteScript(value.c_str(),Callback<ICoreWebView2ExecuteScriptCompletedHandler>([view,callback,context,operation](HRESULT result,LPCWSTR value)->HRESULT{auto* completion=new script_completion{callback,context,operation,narrow(value),SUCCEEDED(result)?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE,nullptr};if(FAILED(result))completion->error=make_error(NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 script evaluation failed",result);if(neoastra_app_dispatch(view->environment->app,finish_script,completion)!=NEOASTRA_OK){if(completion->error)completion->error->release();operation->release();delete completion;}return S_OK;}).Get());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 script evaluation could not be started",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_add_script(neoastra_view_t* view,const std::string& script,const neoastra_script_options_t* options,neoastra_string_callback_t callback,void* context,neoastra_operation_t* operation,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");if(options->injection_time!=NEOASTRA_SCRIPT_DOCUMENT_START||options->main_frame_only||options->isolated_world)return neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"WebView2 supports document-start scripts in the default world for all frames");const auto value=widen(script);const auto result=state->core->AddScriptToExecuteOnDocumentCreated(value.c_str(),Callback<ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler>([view,callback,context,operation](HRESULT result,LPCWSTR identifier)->HRESULT{auto* completion=new script_completion{callback,context,operation,narrow(identifier),SUCCEEDED(result)?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE,nullptr};if(FAILED(result))completion->error=make_error(NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 persistent script registration failed",result);if(neoastra_app_dispatch(view->environment->app,finish_script,completion)!=NEOASTRA_OK){if(completion->error)completion->error->release();operation->release();delete completion;}return S_OK;}).Get());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 persistent script registration could not be started",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t* view,const std::string& identifier) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return NEOASTRA_ERROR_NOT_INITIALIZED;const auto value=widen(identifier);return SUCCEEDED(state->core->RemoveScriptToExecuteOnDocumentCreated(value.c_str()))?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}catch(...){return NEOASTRA_ERROR_INVALID_ARGUMENT;}}
neoastra_result_t neo_platform_view_post_message(neoastra_view_t* view,const std::string& message,bool json,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(message);const auto result=json?state->core->PostWebMessageAsJson(value.c_str()):state->core->PostWebMessageAsString(value.c_str());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 message posting failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t* view,double* factor) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->controller)return NEOASTRA_ERROR_NOT_INITIALIZED;return SUCCEEDED(state->controller->get_ZoomFactor(factor))?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t* view,double factor) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->controller)return NEOASTRA_ERROR_NOT_INITIALIZED;return SUCCEEDED(state->controller->put_ZoomFactor(factor))?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}
neoastra_result_t neo_platform_view_set_setting(neoastra_view_t* view,neoastra_view_setting_t setting,bool enabled) noexcept {
    auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return NEOASTRA_ERROR_NOT_INITIALIZED;
    ComPtr<ICoreWebView2Settings> settings;if(FAILED(state->core->get_Settings(&settings)))return NEOASTRA_ERROR_NATIVE_FAILURE;
    const BOOL value=enabled?TRUE:FALSE;HRESULT result=E_NOINTERFACE;
    switch(setting){
        // WebView2 always stops on links with the Tab key and has no setting for it.
        case NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS:return enabled?NEOASTRA_OK:NEOASTRA_ERROR_NOT_SUPPORTED;
        case NEOASTRA_VIEW_SETTING_BROWSER_ACCELERATOR_KEYS:{ComPtr<ICoreWebView2Settings3> settings3;if(SUCCEEDED(settings.As(&settings3)))result=settings3->put_AreBrowserAcceleratorKeysEnabled(value);break;}
        case NEOASTRA_VIEW_SETTING_DEFAULT_CONTEXT_MENUS:result=settings->put_AreDefaultContextMenusEnabled(value);break;
        case NEOASTRA_VIEW_SETTING_DEVTOOLS:result=settings->put_AreDevToolsEnabled(value);break;
        case NEOASTRA_VIEW_SETTING_STATUS_BAR:result=settings->put_IsStatusBarEnabled(value);break;
        // WebView2 raises ScriptDialogOpening only while its own dialogs are off, and reads the switch when it loads a document.
        case NEOASTRA_VIEW_SETTING_DEFAULT_SCRIPT_DIALOGS:result=settings->put_AreDefaultScriptDialogsEnabled(value);break;
        case NEOASTRA_VIEW_SETTING_ZOOM_CONTROLS:{
            // Pinch zoom is a separate setting on newer runtimes; older ones only have the wheel and keyboard control.
            result=settings->put_IsZoomControlEnabled(value);ComPtr<ICoreWebView2Settings5> settings5;if(SUCCEEDED(result)&&SUCCEEDED(settings.As(&settings5)))result=settings5->put_IsPinchZoomEnabled(value);break;}
        default:return NEOASTRA_ERROR_INVALID_ARGUMENT;
    }
    return SUCCEEDED(result)?NEOASTRA_OK:result==E_NOINTERFACE?NEOASTRA_ERROR_NOT_SUPPORTED:NEOASTRA_ERROR_NATIVE_FAILURE;
}
// ShellExecute reports success with a value above 32, and its error with a smaller one.
bool neo_platform_open_external(const std::string& uri,int64_t* native_code) noexcept {try{const auto result=reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr,L"open",widen(uri).c_str(),nullptr,nullptr,SW_SHOWNORMAL));if(result>32)return true;*native_code=static_cast<int64_t>(result);return false;}catch(...){return false;}}
neoastra_result_t neo_platform_view_open_devtools(neoastra_view_t* view) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return NEOASTRA_ERROR_NOT_INITIALIZED;return SUCCEEDED(state->core->OpenDevToolsWindow())?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}

namespace {
// CapturePreview has neither a region nor a full-page form, so captures go through the DevTools protocol,
// which needs no debugging port and no open DevTools window.
constexpr double maximum_capture_dimension = 16384.0;

struct capture_state {
    ComPtr<ICoreWebView2> core;
    neoastra_capture_options_t options{};
    neoastra_buffer_callback_t callback{};
    void* context{};
    neoastra_operation_t* operation{};
    void complete(neoastra_result_t requested, neoastra_buffer_t* buffer, const char* message, HRESULT native_code) noexcept {
        auto* pending = operation;
        operation = nullptr;
        if (!pending) { if (buffer) buffer->release(); return; }
        auto* error = requested == NEOASTRA_OK ? nullptr : make_error(requested, message, native_code);
        neoastra_result_t actual{};
        if (pending->try_complete(requested, actual)) {
            callback(context, actual, actual == NEOASTRA_OK ? buffer : nullptr, actual == requested ? error : nullptr);
            if (actual != NEOASTRA_OK && buffer) buffer->release();
        } else if (buffer) buffer->release();
        if (error) error->release();
        pending->release();
    }
    // WebView2 drops a pending protocol handler without calling it when its view closes first.
    ~capture_state() { complete(NEOASTRA_ERROR_CANCELED, nullptr, "The view closed before its capture completed", E_ABORT); }
};

// Reads a number member of a flat object member of a DevTools protocol result, such as cssVisualViewport.pageX.
// The layout metrics are plain decimals; they are read without the C locale, which may use another decimal separator.
bool protocol_number(const wchar_t* json, const wchar_t* object, const wchar_t* member, double& value) {
    if (!json) return false;
    const auto object_key = std::wstring(L"\"") + object + L"\":{";
    const auto* start = wcsstr(json, object_key.c_str());
    if (!start) return false;
    start += object_key.size();
    const auto* end = wcschr(start, L'}');
    const auto member_key = std::wstring(L"\"") + member + L"\":";
    const auto* found = wcsstr(start, member_key.c_str());
    if (!found || (end && found > end)) return false;
    found += member_key.size();
    const bool negative = *found == L'-';
    if (negative) ++found;
    if (*found < L'0' || *found > L'9') return false;
    double parsed{};
    for (; *found >= L'0' && *found <= L'9'; ++found) parsed = parsed * 10.0 + (*found - L'0');
    if (*found == L'.') {
        double place = 0.1;
        for (++found; *found >= L'0' && *found <= L'9'; ++found, place /= 10.0) parsed += (*found - L'0') * place;
    }
    value = negative ? -parsed : parsed;
    return true;
}

// Appends a number with three decimals, again independently of the C locale.
void append_protocol_number(std::wstring& output, double value) {
    const auto scaled = std::llround(value * 1000.0);
    const auto magnitude = scaled < 0 ? -scaled : scaled;
    if (scaled < 0) output.push_back(L'-');
    output += std::to_wstring(magnitude / 1000);
    const auto fraction = magnitude % 1000;
    output.push_back(L'.');
    if (fraction < 100) output.push_back(L'0');
    if (fraction < 10) output.push_back(L'0');
    output += std::to_wstring(fraction);
}

// Decodes the base64 "data" member of a Page.captureScreenshot result.
bool protocol_image(const wchar_t* json, std::vector<uint8_t>& bytes) {
    constexpr wchar_t key[] = L"\"data\":\"";
    const auto* current = json ? wcsstr(json, key) : nullptr;
    if (!current) return false;
    current += std::size(key) - 1;
    const auto* end = wcschr(current, L'"');
    if (!end) return false;
    bytes.reserve(static_cast<size_t>(end - current) / 4 * 3 + 3);
    uint32_t accumulator{};
    int bits{};
    for (; current < end; ++current) {
        const auto character = *current;
        uint32_t value{};
        if (character >= L'A' && character <= L'Z') value = static_cast<uint32_t>(character - L'A');
        else if (character >= L'a' && character <= L'z') value = static_cast<uint32_t>(character - L'a') + 26u;
        else if (character >= L'0' && character <= L'9') value = static_cast<uint32_t>(character - L'0') + 52u;
        else if (character == L'+') value = 62u;
        else if (character == L'/') value = 63u;
        else if (character == L'=') break;
        else if (character == L'\\') continue; // JSON may escape the solidus.
        else return false;
        accumulator = (accumulator << 6) | value;
        bits += 6;
        if (bits >= 8) { bits -= 8; bytes.push_back(static_cast<uint8_t>((accumulator >> bits) & 0xffu)); }
    }
    return !bytes.empty();
}

HRESULT start_capture(const std::shared_ptr<capture_state>& state, const std::wstring& clip) {
    std::wstring parameters = state->options.format == NEOASTRA_CAPTURE_FORMAT_JPEG ? L"{\"format\":\"jpeg\"" : L"{\"format\":\"png\"";
    if (state->options.format == NEOASTRA_CAPTURE_FORMAT_JPEG && state->options.quality) parameters += L",\"quality\":" + std::to_wstring(state->options.quality);
    parameters += clip;
    parameters += state->options.full_page ? L",\"captureBeyondViewport\":true}" : L",\"captureBeyondViewport\":false}";
    return state->core->CallDevToolsProtocolMethod(L"Page.captureScreenshot", parameters.c_str(),
        Callback<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>([state](HRESULT result, LPCWSTR json) -> HRESULT {
            if (FAILED(result)) { state->complete(NEOASTRA_ERROR_NATIVE_FAILURE, nullptr, "WebView2 could not capture the view", result); return S_OK; }
            try {
                std::vector<uint8_t> bytes;
                if (!protocol_image(json, bytes)) state->complete(NEOASTRA_ERROR_NATIVE_FAILURE, nullptr, "WebView2 returned no capture data", E_UNEXPECTED);
                else state->complete(NEOASTRA_OK, new neoastra_buffer(std::move(bytes)), nullptr, S_OK);
            } catch (...) { state->complete(NEOASTRA_ERROR_NATIVE_FAILURE, nullptr, "The WebView2 capture could not be decoded", E_OUTOFMEMORY); }
            return S_OK;
        }).Get());
}
} // namespace

neoastra_result_t neo_platform_view_capture(neoastra_view_t* view, const neoastra_capture_options_t& options, neoastra_buffer_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept {
    try {
        auto* native = static_cast<windows_view*>(view->platform);
        if (!native || !native->core || !native->controller) return neo_fail(error, NEOASTRA_ERROR_NOT_INITIALIZED, "WebView2 view is not initialized");
        // A hidden controller renders no frame, and a capture request for it would stay pending.
        BOOL visible{};
        if (FAILED(native->controller->get_IsVisible(&visible)) || !visible) return neo_fail(error, NEOASTRA_ERROR_INVALID_STATE, "A WebView2 view must be visible to be captured", 0, "webview2");
        auto state = std::make_shared<capture_state>();
        state->core = native->core;
        state->options = options;
        state->callback = callback;
        state->context = context;
        state->operation = operation;
        const bool region = !options.full_page && options.region.width > 0 && options.region.height > 0;
        HRESULT result{};
        if (!options.full_page && !region) result = start_capture(state, {});
        else result = state->core->CallDevToolsProtocolMethod(L"Page.getLayoutMetrics", L"{}",
            Callback<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>([state](HRESULT result, LPCWSTR json) -> HRESULT {
                if (FAILED(result)) { state->complete(NEOASTRA_ERROR_NATIVE_FAILURE, nullptr, "WebView2 could not measure the view for capture", result); return S_OK; }
                try {
                    double x{}, y{}, width{}, height{};
                    if (state->options.full_page) {
                        if (!protocol_number(json, L"cssContentSize", L"width", width) || !protocol_number(json, L"cssContentSize", L"height", height)) { state->complete(NEOASTRA_ERROR_NATIVE_FAILURE, nullptr, "WebView2 returned no document size", E_UNEXPECTED); return S_OK; }
                        width = std::min(width, maximum_capture_dimension);
                        height = std::min(height, maximum_capture_dimension);
                    } else {
                        double page_x{}, page_y{}, client_width{}, client_height{};
                        if (!protocol_number(json, L"cssVisualViewport", L"pageX", page_x) || !protocol_number(json, L"cssVisualViewport", L"pageY", page_y) ||
                            !protocol_number(json, L"cssVisualViewport", L"clientWidth", client_width) || !protocol_number(json, L"cssVisualViewport", L"clientHeight", client_height)) { state->complete(NEOASTRA_ERROR_NATIVE_FAILURE, nullptr, "WebView2 returned no viewport size", E_UNEXPECTED); return S_OK; }
                        const auto& requested = state->options.region;
                        const auto left = std::max(static_cast<double>(requested.x), 0.0), top = std::max(static_cast<double>(requested.y), 0.0);
                        const auto right = std::min(static_cast<double>(requested.x) + requested.width, client_width), bottom = std::min(static_cast<double>(requested.y) + requested.height, client_height);
                        x = page_x + left; y = page_y + top; width = right - left; height = bottom - top;
                    }
                    if (!(width >= 1.0) || !(height >= 1.0)) { state->complete(NEOASTRA_ERROR_INVALID_ARGUMENT, nullptr, "The capture region is outside the visible viewport", E_INVALIDARG); return S_OK; }
                    std::wstring clip = L",\"clip\":{\"x\":";
                    append_protocol_number(clip, x);
                    clip += L",\"y\":";
                    append_protocol_number(clip, y);
                    clip += L",\"width\":";
                    append_protocol_number(clip, width);
                    clip += L",\"height\":";
                    append_protocol_number(clip, height);
                    clip += L",\"scale\":1}";
                    const auto started = start_capture(state, clip);
                    if (FAILED(started)) state->complete(NEOASTRA_ERROR_NATIVE_FAILURE, nullptr, "WebView2 capture could not be started", started);
                } catch (...) { state->complete(NEOASTRA_ERROR_NATIVE_FAILURE, nullptr, "The WebView2 capture could not be prepared", E_OUTOFMEMORY); }
                return S_OK;
            }).Get());
        // A capture that did not start leaves the operation to the caller.
        if (FAILED(result)) { state->operation = nullptr; return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 capture could not be started", result, "webview2"); }
        return NEOASTRA_OK;
    } catch (const std::exception& ex) { return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, ex.what()); }
}
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t* view,neoastra_native_handle_kind_t kind,neoastra_native_handle_t* handle) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state)return NEOASTRA_ERROR_NOT_INITIALIZED;if(kind==NEOASTRA_NATIVE_HANDLE_WEBVIEW2_CONTROLLER&&state->controller){handle->kind=kind;handle->value=state->controller.Get();return NEOASTRA_OK;}if(kind==NEOASTRA_NATIVE_HANDLE_WEBVIEW2_CORE&&state->core){handle->kind=kind;handle->value=state->core.Get();return NEOASTRA_OK;}return NEOASTRA_ERROR_NOT_SUPPORTED;}
