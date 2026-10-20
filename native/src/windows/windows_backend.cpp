#include "../common/native_internal.hpp"
#include "../common/script_completion.hpp"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dwmapi.h>
#include <objbase.h>
#include <shellapi.h>
#include <shellscalingapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <wrl.h>
#include <WebView2.h>
#include <WebView2EnvironmentOptions.h>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Make;

namespace {
constexpr UINT dispatch_message = WM_APP + 0x4e;
constexpr UINT quit_message = WM_APP + 0x4f;
constexpr UINT destroy_app_message = WM_APP + 0x50;
// Decision timers use the address of their decision as identifier, which is never 1 or 2.
constexpr UINT_PTR dispatch_timer = 1;
constexpr UINT_PTR display_timer = 2;
constexpr wchar_t dispatch_class[] = L"NeoAstra.Dispatcher";
constexpr wchar_t window_class[] = L"NeoAstra.Window";

// `dispatch_armed` is true while a dispatch message or the dispatch timer waits: that one wake-up stands for all queued
// work. `dispatch_deferred`, on the UI thread only, turns a dispatch message that waits into the timer when it arrives.
struct windows_app {
    HWND dispatcher{}; bool owns_com{}; std::atomic<bool> dispatch_armed{false}; bool dispatch_deferred{}; std::vector<neoastra_decision_t*> decision_timers;
    // The virtual desktops of the shell, created on first use; absent where the shell has none (Windows Server, no Explorer).
    ComPtr<IVirtualDesktopManager> desktops;
    bool desktops_created{};
};
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
    // While a request places the window, the bounds it asked for: a display of another scale that the window lands on takes them at that scale.
    bool placing{};
    neoastra_rect_t placement{};
    HWND caption{}; // Caption-button overlay for NEOASTRA_TITLE_BAR_OVERLAY.
    HWND top_grip{}; // Input-only strip restoring top-edge resizing above the browser.
    bool caption_layered{};
    bool caption_tracking{};
    bool grip_unavailable{};
    bool active{true};
    int caption_hot{-1};
    int caption_pressed{-1};
    // The icon of the executable at the sizes of the scale of the window's display (see apply_executable_icons).
    HICON executable_big_icon{};
    HICON executable_small_icon{};
    windows_window() { restored_placement.length = sizeof(restored_placement); }
    windows_window(const windows_window&) = delete;
    windows_window& operator=(const windows_window&) = delete;
    // The native window is destroyed before its state, so it no longer shows these icons.
    ~windows_window() {
        if (executable_big_icon) DestroyIcon(executable_big_icon);
        if (executable_small_icon) DestroyIcon(executable_small_icon);
    }
};
struct windows_environment { ComPtr<ICoreWebView2Environment> value; std::string version; bool private_mode{}; };
struct windows_profile { ComPtr<ICoreWebView2CookieManager> cookies; ComPtr<ICoreWebView2Profile> profile; };
struct windows_drop_registration { HWND window{}; ComPtr<IDropTarget> target; };
struct windows_view {
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> core;
    HWND drop_window{};
    EventRegistrationToken navigation_starting{};
    EventRegistrationToken frame_navigation_starting{};
    // The main-frame navigation that the host refused last, without its fragment, and when it did.
    std::string refused_navigation;
    ULONGLONG refused_navigation_time{};
    EventRegistrationToken navigation_completed{};
    EventRegistrationToken content_loading{};
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
    // A drag that started in the page: WebView2 reports it through the DevTools protocol instead of running its own
    // drag loop, and NeoAstra runs it. The "data" member of Input.dragIntercepted (UTF-8), and the state of the loop.
    ComPtr<ICoreWebView2DevToolsProtocolEventReceiver> drag_intercepted;
    EventRegistrationToken drag_intercepted_token{};
    std::string page_drag_data;
    UINT_PTR page_drag_timer{};
    bool page_drag_active{};
    bool page_drag_dropped{};
};

void register_view_drop_targets(neoastra_view_t* view, windows_view* state);
void intercept_page_drags(neoastra_view_t* view, windows_view* state) noexcept;

// The browser of a view in a window shows the window color until its page paints (WebView2 takes it opaque or clear).
void apply_view_background(neoastra_view_t* view) noexcept {
    auto* state = static_cast<windows_view*>(view->platform);
    ComPtr<ICoreWebView2Controller2> controller2;
    if (!view->window || !state || !state->controller || FAILED(state->controller.As(&controller2))) return;
    const auto color = view->window->background_color;
    const COREWEBVIEW2_COLOR value{static_cast<BYTE>(color.alpha ? 255 : 0), color.red, color.green, color.blue};
    (void)controller2->put_DefaultBackgroundColor(value);
}

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

// Widens UTF-8 that the caller validated into a buffer that keeps its capacity from one message to the next.
void widen_into(std::wstring& result, neoastra_string_view_t value) {
    result.clear();
    if (value.length == 0) return;
    if (value.length > static_cast<uint64_t>(INT_MAX)) throw std::invalid_argument("UTF-8 string is too long");
    const auto* data = reinterpret_cast<const char*>(value.data);
    const auto length = static_cast<int>(value.length);
    const auto count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, length, nullptr, 0);
    if (count <= 0) throw std::invalid_argument("invalid UTF-8");
    result.resize(static_cast<size_t>(count));
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, length, result.data(), count)) throw std::invalid_argument("invalid UTF-8");
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

void append_protocol_number(std::wstring& output, double value);
DWORD drag_started_in_view_browser(const neoastra_view_t* view) noexcept;
const std::string* own_page_drag(const neoastra_view_t* view) noexcept;
bool app_page_drag(IDataObject* data, std::string& value) noexcept;
bool drop_page_drag(neoastra_view_t* view, POINTL point, DWORD keys) noexcept;
bool drop_app_page_drag(neoastra_view_t* view, POINTL point, DWORD keys, const std::string& data) noexcept;
std::string page_drag_data(IDataObject* data, DWORD allowed) noexcept;
bool forward_page_drag(neoastra_view_t* view, const wchar_t* type, POINTL point, DWORD keys, const std::string& data, std::shared_ptr<bool> done = {}) noexcept;

// The page, not NeoAstra, takes a drag it forwards: the effect shown is the first one its source allows.
DWORD page_drop_effect(DWORD allowed) noexcept {
    if (allowed & DROPEFFECT_COPY) return DROPEFFECT_COPY;
    if (allowed & DROPEFFECT_MOVE) return DROPEFFECT_MOVE;
    return allowed & DROPEFFECT_LINK;
}

// Replaces WebView2's OLE drop targets on the windows of a view. External file, text, and URL drops go to the host.
// The page gets, through the DevTools protocol, the drags that started in it or in another view of the application
// (which NeoAstra runs, see begin_page_drag) and the external drags with none of those formats (custom MIME types):
// WebView2's own target, which lives in its browser process, is withdrawn while external drops are not allowed,
// in-page drops included.
class view_drop_target final : public IDropTarget {
public:
    view_drop_target(neoastra_view_t* view, HWND window) : view_(view), window_(window) { }
    // OLE can keep its COM reference after the registration (and the view) is gone.
    void detach_view() noexcept { view_ = nullptr; }
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
    HRESULT STDMETHODCALLTYPE DragEnter(IDataObject* data, DWORD keys, POINTL point, DWORD* effect) override {
        if (!effect) return E_POINTER;
        ComPtr<IDropTarget> target_owner(this);
        auto* view = view_;
        neo_retain_guard owner(view);
        if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { *effect = DROPEFFECT_NONE; return S_OK; }
        FORMATETC files{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        FORMATETC text{CF_UNICODETEXT, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        FORMATETC url{static_cast<CLIPFORMAT>(RegisterClipboardFormatW(L"UniformResourceLocatorW")), nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        valid_ = data && (SUCCEEDED(data->QueryGetData(&files)) || SUCCEEDED(data->QueryGetData(&text)) || SUCCEEDED(data->QueryGetData(&url)));
        page_ = false;
        own_ = false;
        app_ = false;
        if (const auto* own = own_page_drag(view)) {
            // A drag of this page, run by NeoAstra: the page has its original data.
            valid_ = false;
            own_ = true;
            page_data_ = *own;
            page_ = forward_page_drag(view, L"dragEnter", point, keys, page_data_);
            last_point_ = point;
            *effect = page_ ? page_drop_effect(*effect) : DROPEFFECT_NONE;
            return S_OK;
        }
        if (data && app_page_drag(data, page_data_)) {
            // A drag of the page of another view of the application, run by NeoAstra: this page has its original data,
            // HTML included, rather than its text and URL taken as an external drop of the host.
            valid_ = false;
            app_ = true;
            page_ = forward_page_drag(view, L"dragEnter", point, keys, page_data_);
            last_point_ = point;
            *effect = page_ ? page_drop_effect(*effect) : DROPEFFECT_NONE;
            return S_OK;
        }
        if (drag_started_in_view_browser(view)) {
            // A drag of the page that WebView2 runs itself (interception unavailable): neither the host's nor deliverable.
            valid_ = false;
            *effect = DROPEFFECT_NONE;
            return S_OK;
        }
        if (data && !valid_) {
            page_data_ = page_drag_data(data, *effect);
            page_ = !page_data_.empty() && forward_page_drag(view, L"dragEnter", point, keys, page_data_);
        }
        if (page_) {
            valid_ = false;
            last_point_ = point;
            *effect = page_drop_effect(*effect);
            return S_OK;
        }
        page_data_.clear();
        *effect = valid_ ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE DragOver(DWORD keys, POINTL point, DWORD* effect) override {
        if (!effect) return E_POINTER;
        ComPtr<IDropTarget> target_owner(this);
        auto* view = view_;
        neo_retain_guard owner(view);
        if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { *effect = DROPEFFECT_NONE; return S_OK; }
        if (page_) {
            last_point_ = point;
            (void)forward_page_drag(view, L"dragOver", point, keys, page_data_);
            *effect = page_drop_effect(*effect);
            return S_OK;
        }
        *effect = valid_ ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE DragLeave() override {
        ComPtr<IDropTarget> target_owner(this);
        auto* view = view_;
        neo_retain_guard owner(view);
        if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
        // A drag of this page goes on outside the view; its loop ends it in the page.
        if (page_ && !own_) (void)forward_page_drag(view, L"dragCancel", last_point_, 0, page_data_);
        page_ = false;
        own_ = false;
        app_ = false;
        page_data_.clear();
        valid_ = false;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Drop(IDataObject* data, DWORD keys, POINTL point, DWORD* effect) override {
        if (!data || !effect) return E_POINTER;
        ComPtr<IDropTarget> target_owner(this);
        auto* view = view_;
        neo_retain_guard owner(view);
        if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { *effect = DROPEFFECT_NONE; return S_OK; }
        if (page_) {
            page_ = false;
            const auto forwarded = own_ ? drop_page_drag(view, point, keys)
                                 : app_ ? drop_app_page_drag(view, point, keys, page_data_)
                                        : forward_page_drag(view, L"drop", point, keys, page_data_);
            own_ = false;
            app_ = false;
            page_data_.clear();
            *effect = forwarded ? page_drop_effect(*effect) : DROPEFFECT_NONE;
            return S_OK;
        }
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
                                neo_emit_view_detailed(view, NEOASTRA_EVENT_MESSAGE_RECEIVED, 0, &text, nullptr,
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
            neo_emit_view_detailed(view, NEOASTRA_EVENT_MESSAGE_RECEIVED, 0, &paths, nullptr,
                                   (UINT64_C(1) << 63) | (UINT64_C(2) << 56) | count, 0, nullptr, details);
            *effect = DROPEFFECT_COPY;
        } catch (...) {
            neo_log(view->environment->app, NEOASTRA_LOG_WARNING, "drag-drop", "WebView2 native drop decoding failed");
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
    bool page_{};
    bool own_{};
    bool app_{};
    std::string page_data_;
    POINTL last_point_{};
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
    for (size_t index = 0; index < window->views.size();) {
        auto* view = window->views[index];
        {
            neo_retain_guard owner(view);
            auto* state = owner ? static_cast<windows_view*>(view->platform) : nullptr;
            const auto controller = state ? state->controller : ComPtr<ICoreWebView2Controller>{};
            if (controller) {
                if (visible && view->fill_parent) (void)controller->put_Bounds(view_bounds(view));
                if (view->platform == state) (void)controller->put_IsVisible(visible ? TRUE : FALSE);
            }
        }
        if (index < window->views.size() && window->views[index] == view) ++index;
    }
}

void remove_view_events(windows_view* state) noexcept {
    if (!state || !state->core || !state->events_registered) return;
    state->core->remove_NavigationStarting(state->navigation_starting);
    state->core->remove_FrameNavigationStarting(state->frame_navigation_starting);
    state->core->remove_NavigationCompleted(state->navigation_completed);
    state->core->remove_ContentLoading(state->content_loading);
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
    if (state->drag_intercepted) { state->drag_intercepted->remove_DevToolsProtocolEventReceived(state->drag_intercepted_token); state->drag_intercepted.Reset(); }
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
            neo_retain_guard owner(download);
            neo_retain_guard view_owner(owner ? download->view : nullptr);
            if (!owner || !view_owner || download->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
            INT64 received{},total{-1};operation->get_BytesReceived(&received);operation->get_TotalBytesToReceive(&total);
            download->bytes_received.store(received<0?0:static_cast<uint64_t>(received),std::memory_order_release);download->total_bytes.store(total<0?UINT64_MAX:static_cast<uint64_t>(total),std::memory_order_release);
            neo_download_emit(download,NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED);return S_OK;
        }).Get(),&state->bytes_changed);
        if(FAILED(result))return false;
        result=state->operation->add_StateChanged(Callback<ICoreWebView2StateChangedEventHandler>([download](ICoreWebView2DownloadOperation* operation,IUnknown*)->HRESULT{
            neo_retain_guard owner(download);
            neo_retain_guard view_owner(owner ? download->view : nullptr);
            if (!owner || !view_owner || download->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
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

struct download_decision_context {
    // Shutdown can release the lifecycle reference before the view abandons its
    // deferred decision. Keep the download through completion and COM cleanup.
    neo_retain_guard download_owner;
    ComPtr<ICoreWebView2DownloadStartingEventArgs> args;
    ComPtr<ICoreWebView2Deferral> deferral;
    neoastra_download_t* download;

    explicit download_decision_context(neoastra_download_t* value) noexcept
        : download_owner(value), download(value) { }
};
void download_decided(void* pointer,const neoastra_decision_response_t* response) noexcept {
    auto* download=static_cast<download_decision_context*>(pointer)->download;
    neo_retain_guard owner(download);
    neo_retain_guard view_owner(owner ? download->view : nullptr);
    std::unique_ptr<download_decision_context> context(static_cast<download_decision_context*>(pointer));
    if (!owner || !view_owner) {
        context->args->put_Cancel(TRUE);
        if (owner) { download->state.store(NEOASTRA_DOWNLOAD_CANCELED); download->release_lifecycle(); }
        context->deferral->Complete();
        return;
    }
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
    uint64_t value{};
    switch (kind) {
    case COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED:
        value = NEOASTRA_PROCESS_FAILURE_BROWSER_PROCESS_EXITED | NEOASTRA_PROCESS_FAILURE_RESTART_APPLICATION;
        break;
    case COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED:
        value = NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED | NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW;
        break;
    // The document still runs in its renderer and may answer again.
    case COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_UNRESPONSIVE:
        value = NEOASTRA_PROCESS_FAILURE_PROCESS_UNRESPONSIVE | NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW | NEOASTRA_PROCESS_FAILURE_DOCUMENT_INTACT;
        break;
    // Only the subframes that renderer drew are gone; the main document runs on.
    case COREWEBVIEW2_PROCESS_FAILED_KIND_FRAME_RENDER_PROCESS_EXITED:
        value = NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED | NEOASTRA_PROCESS_FAILURE_DOCUMENT_INTACT;
        break;
    // A GPU, utility, sandbox helper, or plugin process, or one WebView2 does not name: WebView2 starts another one
    // when it needs it, and the document runs on.
    default:
        value = NEOASTRA_PROCESS_FAILURE_UNKNOWN | NEOASTRA_PROCESS_FAILURE_DOCUMENT_INTACT;
        break;
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

// WebView2 tells the kind of a navigation from the runtime 115 on.
uint64_t navigation_kind(ICoreWebView2NavigationStartingEventArgs* args) noexcept {
    ComPtr<ICoreWebView2NavigationStartingEventArgs3> args3;
    COREWEBVIEW2_NAVIGATION_KIND kind{};
    if (FAILED(args->QueryInterface(IID_PPV_ARGS(&args3))) || FAILED(args3->get_NavigationKind(&kind))) return NEOASTRA_NAVIGATION_REQUEST_KIND_UNKNOWN;
    switch (kind) {
        case COREWEBVIEW2_NAVIGATION_KIND_NEW_DOCUMENT: return NEOASTRA_NAVIGATION_REQUEST_KIND_NEW_DOCUMENT;
        case COREWEBVIEW2_NAVIGATION_KIND_RELOAD: return NEOASTRA_NAVIGATION_REQUEST_KIND_RELOAD;
        case COREWEBVIEW2_NAVIGATION_KIND_BACK_OR_FORWARD: return NEOASTRA_NAVIGATION_REQUEST_KIND_BACK_FORWARD;
        default: return NEOASTRA_NAVIGATION_REQUEST_KIND_UNKNOWN;
    }
}

void report_history(neoastra_view_t* view, ICoreWebView2* core) noexcept {
    BOOL back{}, forward{};
    core->get_CanGoBack(&back);
    core->get_CanGoForward(&forward);
    neo_emit_view(view, NEOASTRA_EVENT_HISTORY_CHANGED, 0, nullptr, nullptr, neo_history_directions(view, back, forward));
}

HRESULT register_view_events(neoastra_view_t* view, windows_view* state) {
    state->events_registered = true;
    HRESULT result = state->core->add_NavigationStarting(
        Callback<ICoreWebView2NavigationStartingEventHandler>([view](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
            // A released or torn-down view settles a decision as NeoAstra's default would: returning with
            // the arguments untouched would let WebView2 apply its own (popup, download UI, prompt, dialog).
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { args->put_Cancel(TRUE); return S_OK; }
            LPWSTR raw_uri{};
            BOOL user_initiated{};
            BOOL redirected{};
            args->get_Uri(&raw_uri);
            args->get_IsUserInitiated(&user_initiated);
            args->get_IsRedirected(&redirected);
            auto uri = take_string(raw_uri);
            const auto kind = navigation_kind(args);
            if (neo_refuses_navigation(view, kind)) {
                args->put_Cancel(TRUE);
                remember_navigation(view, uri, true);
                return S_OK;
            }

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
                          NEOASTRA_NAVIGATION_REQUEST_MAIN_FRAME | (user_initiated && !redirected ? NEOASTRA_NAVIGATION_REQUEST_USER_INITIATED : 0) | kind, 0, decision);
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

    // The navigations of the other frames are not reported to the host. One that walks the history is refused like that of the main frame.
    result = state->core->add_FrameNavigationStarting(
        Callback<ICoreWebView2NavigationStartingEventHandler>([view](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { args->put_Cancel(TRUE); return S_OK; }
            if (neo_refuses_navigation(view, navigation_kind(args))) args->put_Cancel(TRUE);
            return S_OK;
        }).Get(), &state->frame_navigation_starting);
    if (FAILED(result)) return result;

    result=state->core->add_ScriptDialogOpening(Callback<ICoreWebView2ScriptDialogOpeningEventHandler>([view](ICoreWebView2*,ICoreWebView2ScriptDialogOpeningEventArgs* args)->HRESULT{
        neo_retain_guard owner(view);
        if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
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
        neo_retain_guard owner(view);
        if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
        BOOL entering{};core->get_ContainsFullScreenElement(&entering);if(!entering)return S_OK;auto* decision=new(std::nothrow) neoastra_decision;if(!decision){core->ExecuteScript(L"document.fullscreenElement && document.exitFullscreen()",nullptr);return S_OK;}
        neo_configure_decision(decision,view,NEOASTRA_DECISION_FULLSCREEN,NEOASTRA_DECISION_DENY);decision->completion=fullscreen_decided;decision->completion_context=new(std::nothrow) fullscreen_context{core};if(!decision->completion_context){decision->release();core->ExecuteScript(L"document.fullscreenElement && document.exitFullscreen()",nullptr);return S_OK;}
        neo_emit_view(view,NEOASTRA_EVENT_FULLSCREEN_REQUESTED,0,nullptr,&view->source,1,0,decision);neo_finish_decision_event(view,decision);decision->release();return S_OK;
    }).Get(),&state->fullscreen_changed);
    if(FAILED(result))return result;

    result = state->core->add_NavigationCompleted(
        Callback<ICoreWebView2NavigationCompletedEventHandler>([view](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT {
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
            BOOL success{};
            COREWEBVIEW2_WEB_ERROR_STATUS status{};
            UINT64 navigation_id{};
            args->get_IsSuccess(&success);
            args->get_WebErrorStatus(&status);
            args->get_NavigationId(&navigation_id);
            if (success) {
                register_view_drop_targets(view, static_cast<windows_view*>(view->platform));
                intercept_page_drags(view, static_cast<windows_view*>(view->platform));
            }
            neo_emit_view(view, success ? NEOASTRA_EVENT_NAVIGATION_COMPLETED : NEOASTRA_EVENT_NAVIGATION_FAILED,
                          0, nullptr, &view->source, success ? 0 : static_cast<uint64_t>(NEOASTRA_ERROR_NATIVE_FAILURE),
                          static_cast<int64_t>(status));
            return S_OK;
        }).Get(), &state->navigation_completed);
    if (FAILED(result)) return result;

    // ContentLoading is the commit of a new main-frame document, error pages included; WebView2 raises it for no same-document
    // navigation, and for no navigation that ends without a document, such as a download or an answer without content.
    result = state->core->add_ContentLoading(
        Callback<ICoreWebView2ContentLoadingEventHandler>([view](ICoreWebView2*, ICoreWebView2ContentLoadingEventArgs*) -> HRESULT {
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
            // The new document can take input before its load completes: the renderer windows it brought get NeoAstra's
            // drop targets now, and again when the navigation completes.
            register_view_drop_targets(view, static_cast<windows_view*>(view->platform));
            if (view->platform == nullptr || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
            neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_COMMITTED, 0, nullptr, &view->source);
            return S_OK;
        }).Get(), &state->content_loading);
    if (FAILED(result)) return result;

    result = state->core->add_SourceChanged(
        Callback<ICoreWebView2SourceChangedEventHandler>([view](ICoreWebView2* core, ICoreWebView2SourceChangedEventArgs*) -> HRESULT {
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
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
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
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
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
            report_history(view, core);
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
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
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
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
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
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { args->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY); return S_OK; }
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
                // An answer is kept in the profile through the third interface of the request, which an older runtime lacks.
                ComPtr<ICoreWebView2PermissionRequestedEventArgs3> args3;
                int64_t flags = user_initiated ? NEOASTRA_PERMISSION_REQUEST_USER_INITIATED : 0;
                if (SUCCEEDED(args->QueryInterface(IID_PPV_ARGS(&args3)))) flags |= NEOASTRA_PERMISSION_REQUEST_CAN_PERSIST;
                neo_emit_view(view, NEOASTRA_EVENT_PERMISSION_REQUESTED, 0, nullptr, &uri,
                              portable_permission(kind), flags, decision);
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
        Callback<ICoreWebView2NewWindowRequestedEventHandler>([view](ICoreWebView2* core, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT {
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { args->put_Handled(TRUE); return S_OK; }
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
                context->core = core;
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
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { args->put_Cancel(TRUE); return S_OK; }
            ComPtr<ICoreWebView2DownloadOperation> operation;ComPtr<ICoreWebView2Deferral> deferral;LPWSTR raw_path{};
            auto hr=args->get_DownloadOperation(&operation);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(SUCCEEDED(hr))args->get_ResultFilePath(&raw_path);
            if(FAILED(hr)){CoTaskMemFree(raw_path);args->put_Cancel(TRUE);return S_OK;}
            try{
                auto download_guard=std::make_unique<neoastra_download>(view);auto* download=download_guard.get();auto* platform=new windows_download;download->platform=platform;download->command=windows_download_command;download->platform_destroy=destroy_windows_download;platform->operation=operation;
                LPWSTR raw_uri{},raw_mime{},raw_disposition{};INT64 total{-1};operation->get_Uri(&raw_uri);operation->get_MimeType(&raw_mime);operation->get_ContentDisposition(&raw_disposition);operation->get_TotalBytesToReceive(&total);
                 download->source_uri=take_string(raw_uri);download->total_bytes.store(total<0?UINT64_MAX:static_cast<uint64_t>(total));download->destination_path=take_string(raw_path);download->can_pause=true;raw_path=nullptr;
                auto mime=take_string(raw_mime);auto disposition=take_string(raw_disposition);auto suggested=download->destination_path;const auto slash=suggested.find_last_of("/\\");if(slash!=std::string::npos)suggested.erase(0,slash+1);
                auto context=std::make_unique<download_decision_context>(download);context->args=args;context->deferral=deferral;
                auto decision_guard=std::make_unique<neoastra_decision>();auto* decision=decision_guard.get();neo_configure_decision(decision,view,NEOASTRA_DECISION_DOWNLOAD_REQUEST,NEOASTRA_DECISION_CANCEL);decision->completion=download_decided;decision->completion_context=context.release();download_guard.release();decision_guard.release();
                neo_event_details details{};details.text2=&mime;details.text3=&disposition;details.value2=1;details.download=download;download->event_published=true;
                neo_retain_guard download_owner(download);
                neo_emit_view_detailed(view,NEOASTRA_EVENT_DOWNLOAD_REQUESTED,download->id,&suggested,&download->source_uri,total<0?UINT64_MAX:static_cast<uint64_t>(total),0,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;
            }catch(...){CoTaskMemFree(raw_path);args->put_Cancel(TRUE);deferral->Complete();return S_OK;}
        }).Get(),&state->download_starting);
        if(FAILED(result))return result;
    }

    ComPtr<ICoreWebView2_10> core10;
    if(SUCCEEDED(state->core.As(&core10))){result=core10->add_BasicAuthenticationRequested(Callback<ICoreWebView2BasicAuthenticationRequestedEventHandler>([view](ICoreWebView2*,ICoreWebView2BasicAuthenticationRequestedEventArgs* args)->HRESULT{
        neo_retain_guard owner(view);
        if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { args->put_Cancel(TRUE); return S_OK; }
        LPWSTR raw_uri{},raw_challenge{};ComPtr<ICoreWebView2Deferral> deferral;auto hr=args->get_Uri(&raw_uri);if(SUCCEEDED(hr))hr=args->get_Challenge(&raw_challenge);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(FAILED(hr)){CoTaskMemFree(raw_uri);CoTaskMemFree(raw_challenge);args->put_Cancel(TRUE);return S_OK;}
        try{auto uri=take_string(raw_uri);auto challenge=take_string(raw_challenge);auto context=std::make_unique<basic_auth_context>();context->args=args;context->deferral=deferral;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_AUTHENTICATION,NEOASTRA_DECISION_DEFAULT);decision->completion=basic_auth_decided;decision->completion_context=context.release();neo_event_details details{};details.text2=&challenge;
            neo_emit_view_detailed(view,NEOASTRA_EVENT_AUTHENTICATION_REQUESTED,0,nullptr,&uri,0,0,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;}catch(...){args->put_Cancel(TRUE);deferral->Complete();return S_OK;}
    }).Get(),&state->basic_auth);if(FAILED(result))return result;}

    ComPtr<ICoreWebView2_5> core5;
    if(SUCCEEDED(state->core.As(&core5))){result=core5->add_ClientCertificateRequested(Callback<ICoreWebView2ClientCertificateRequestedEventHandler>([view](ICoreWebView2*,ICoreWebView2ClientCertificateRequestedEventArgs* args)->HRESULT{
        neo_retain_guard owner(view);
        if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { args->put_Cancel(TRUE); args->put_Handled(TRUE); return S_OK; }
        LPWSTR raw_host{};int port{};BOOL proxy{};ComPtr<ICoreWebView2ClientCertificateCollection> certificates;ComPtr<ICoreWebView2Deferral> deferral;auto hr=args->get_Host(&raw_host);if(SUCCEEDED(hr))hr=args->get_Port(&port);if(SUCCEEDED(hr))hr=args->get_IsProxy(&proxy);if(SUCCEEDED(hr))hr=args->get_MutuallyTrustedCertificates(&certificates);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(FAILED(hr)){CoTaskMemFree(raw_host);args->put_Cancel(TRUE);return S_OK;}
        try{auto host=take_string(raw_host);UINT count{};certificates->get_Count(&count);auto context=std::make_unique<client_cert_context>();context->args=args;context->certificates=certificates;context->deferral=deferral;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_CLIENT_CERTIFICATE,NEOASTRA_DECISION_DEFAULT);decision->completion=client_cert_decided;decision->completion_context=context.release();neo_event_details details{};details.value2=proxy?1u:0u;
            neo_emit_view_detailed(view,NEOASTRA_EVENT_CLIENT_CERTIFICATE_REQUESTED,0,&host,nullptr,count,port,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;}catch(...){args->put_Cancel(TRUE);deferral->Complete();return S_OK;}
    }).Get(),&state->client_certificate);if(FAILED(result))return result;}

    ComPtr<ICoreWebView2_14> core14;
    if(SUCCEEDED(state->core.As(&core14))){result=core14->add_ServerCertificateErrorDetected(Callback<ICoreWebView2ServerCertificateErrorDetectedEventHandler>([view](ICoreWebView2*,ICoreWebView2ServerCertificateErrorDetectedEventArgs* args)->HRESULT{
        neo_retain_guard owner(view);
        if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) { args->put_Action(COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL); return S_OK; }
        LPWSTR raw_uri{};COREWEBVIEW2_WEB_ERROR_STATUS status{};ComPtr<ICoreWebView2Certificate> certificate;ComPtr<ICoreWebView2Deferral> deferral;auto hr=args->get_RequestUri(&raw_uri);if(SUCCEEDED(hr))hr=args->get_ErrorStatus(&status);if(SUCCEEDED(hr))hr=args->get_ServerCertificate(&certificate);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(FAILED(hr)){CoTaskMemFree(raw_uri);args->put_Action(COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL);return S_OK;}
        try{auto uri=take_string(raw_uri);LPWSTR raw_subject{};certificate->get_Subject(&raw_subject);auto subject=take_string(raw_subject);auto context=std::make_unique<tls_context>();context->args=args;context->deferral=deferral;auto* decision=new neoastra_decision;neo_configure_decision(decision,view,NEOASTRA_DECISION_CERTIFICATE_ERROR,NEOASTRA_DECISION_DENY);decision->completion=tls_decided;decision->completion_context=context.release();neo_event_details details{};details.text2=&subject;
            neo_emit_view_detailed(view,NEOASTRA_EVENT_CERTIFICATE_ERROR,0,nullptr,&uri,0,static_cast<int64_t>(status),decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;}catch(...){args->put_Action(COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL);deferral->Complete();return S_OK;}
    }).Get(),&state->server_certificate_error);if(FAILED(result))return result;}

    result = state->core->add_ProcessFailed(
        Callback<ICoreWebView2ProcessFailedEventHandler>([view](ICoreWebView2*, ICoreWebView2ProcessFailedEventArgs* args) -> HRESULT {
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
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
    neo_retain_guard owner(app);
    if (!owner) return message == WM_QUERYENDSESSION ? TRUE : 0;
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

// The system tells of a change of the displays in several messages, and the displays are read once it is done telling.
void schedule_display_check(neoastra_app_t* app) noexcept {
    auto* state = app ? static_cast<windows_app*>(app->platform) : nullptr;
    if (state && state->dispatcher) SetTimer(state->dispatcher, display_timer, 100, nullptr);
}

// One wake-up at a time stands for the whole queue; a failed post leaves the next producer to post again.
void post_dispatch(windows_app* state) noexcept {
    if (state && state->dispatcher && !state->dispatch_armed.exchange(true) && !PostMessageW(state->dispatcher, dispatch_message, 0, 0)) state->dispatch_armed.store(false);
}

// Runs one slice of queued work on the UI thread, then arms the next wake-up if work remains. Posted messages are
// retrieved before input, painting, and timers, so while any of those waits the rest of the queue waits for a timer
// instead: a busy queue then shares the loop with them rather than holding them back.
void run_dispatch_slice(neoastra_app_t* app, bool posted) noexcept {
    if (auto* state = static_cast<windows_app*>(app->platform)) {
        // A message that waited while input or painting did now gives them their turn: the timer stands for it.
        if (std::exchange(state->dispatch_deferred, false) && posted && SetTimer(state->dispatcher, dispatch_timer, USER_TIMER_MINIMUM, nullptr)) return;
        // Cleared before the slice, so work queued meanwhile posts its own wake-up. Work already queued behind the next
        // callback gets one too: a nested message loop of a callback (a menu, a dialog) still runs it, as it did before.
        state->dispatch_armed.store(false);
        if (neo_dispatch_backlog(app)) post_dispatch(state);
    }
    if (!neo_drain_dispatch_slice(app)) return;
    auto* state = static_cast<windows_app*>(app->platform);
    if (!state || !state->dispatcher || app->stopped.load(std::memory_order_acquire)) return;
    const bool yield = HIWORD(GetQueueStatus(QS_INPUT | QS_PAINT | QS_TIMER)) != 0;
    if (state->dispatch_armed.exchange(true)) {
        // A wake-up was armed during the slice; a posted one would again go ahead of what waits.
        if (yield) state->dispatch_deferred = true;
        return;
    }
    if (yield && SetTimer(state->dispatcher, dispatch_timer, USER_TIMER_MINIMUM, nullptr)) return;
    if (PostMessageW(state->dispatcher, dispatch_message, 0, 0)) return;
    if (!yield && SetTimer(state->dispatcher, dispatch_timer, USER_TIMER_MINIMUM, nullptr)) return;
    state->dispatch_armed.store(false);
}

LRESULT CALLBACK dispatcher_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
    }
    auto* app = reinterpret_cast<neoastra_app_t*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    // This message consumes an already-zero-reference app; it must never retain it.
    if (message == destroy_app_message && app) { neo_destroy_app_on_ui(app); return 0; }
    neo_retain_guard owner(app);
    if (!owner) return DefWindowProcW(hwnd, message, wparam, lparam);
    if (app && (message == WM_QUERYENDSESSION || message == WM_ENDSESSION)) return handle_session_message(app, message, wparam);
    if (message == dispatch_message && app) { run_dispatch_slice(app, true); return 0; }
    if (message == quit_message && app) { neo_drain_dispatch(app); PostQuitMessage(app->exit_code.load()); return 0; }
    if (message == WM_TIMER && wparam == dispatch_timer && app) { KillTimer(hwnd, dispatch_timer); run_dispatch_slice(app, false); return 0; }
    if (message == WM_TIMER && wparam == display_timer && app) { KillTimer(hwnd, display_timer); neo_app_displays_changed(app); return 0; }
    // The hidden top-level dispatcher hears of the displays: their topology, the work areas that the taskbar leaves, and their scale.
    if (app && (message == WM_DISPLAYCHANGE || message == WM_DPICHANGED || (message == WM_SETTINGCHANGE && wparam == SPI_SETWORKAREA))) schedule_display_check(app);
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

// The first icon group of the executable, the one Explorer shows for it: in a .NET application, its <ApplicationIcon>.
struct executable_icon_group { std::wstring name; WORD id{}; bool found{}; };
BOOL CALLBACK find_executable_icon_group(HMODULE, LPCWSTR, LPWSTR name, LONG_PTR parameter) {
    auto* group = reinterpret_cast<executable_icon_group*>(parameter);
    try {
        if (IS_INTRESOURCE(name)) group->id = static_cast<WORD>(reinterpret_cast<ULONG_PTR>(name));
        else group->name = name;
        group->found = true;
    } catch (...) {}
    return FALSE;
}
const wchar_t* executable_icon_name() noexcept {
    static const executable_icon_group group = [] {
        executable_icon_group value;
        EnumResourceNamesW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(14) /* RT_GROUP_ICON */, find_executable_icon_group, reinterpret_cast<LONG_PTR>(&value));
        return value;
    }();
    if (!group.found) return nullptr;
    return group.id ? MAKEINTRESOURCEW(group.id) : group.name.c_str();
}

// A window shows the icon of its executable, when it has one, at the sizes the system draws for the scale of the window's
// display: a big one for the taskbar and Alt+Tab, a small one for the title bar. Loading the icon without a size would give
// the first picture of its group, often 16 pixels, which the taskbar enlarges. Called again when that scale changes; an icon
// that the application gave the window with WM_SETICON stays, and these come back when the application takes it away.
void apply_executable_icons(windows_window* state, HWND hwnd, UINT dpi) noexcept {
    const auto* name = executable_icon_name();
    if (!name) return;
    const auto instance = GetModuleHandleW(nullptr);
    const auto big_size = GetSystemMetricsForDpi(SM_CXICON, dpi);
    const auto small_size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
    // `small` is a macro of the Windows headers.
    const auto big_icon = static_cast<HICON>(LoadImageW(instance, name, IMAGE_ICON, big_size, big_size, LR_DEFAULTCOLOR));
    const auto small_icon = static_cast<HICON>(LoadImageW(instance, name, IMAGE_ICON, small_size, small_size, LR_DEFAULTCOLOR));
    if (!big_icon || !small_icon) {
        if (big_icon) DestroyIcon(big_icon);
        if (small_icon) DestroyIcon(small_icon);
        return;
    }
    const auto shown_big = reinterpret_cast<HICON>(SendMessageW(hwnd, WM_GETICON, ICON_BIG, 0));
    const auto shown_small = reinterpret_cast<HICON>(SendMessageW(hwnd, WM_GETICON, ICON_SMALL, 0));
    const auto previous_big = std::exchange(state->executable_big_icon, big_icon);
    const auto previous_small = std::exchange(state->executable_small_icon, small_icon);
    if (!shown_big || shown_big == previous_big) SendMessageW(hwnd, WM_SETICON, ICON_BIG, reinterpret_cast<LPARAM>(big_icon));
    if (!shown_small || shown_small == previous_small) SendMessageW(hwnd, WM_SETICON, ICON_SMALL, reinterpret_cast<LPARAM>(small_icon));
    if (previous_big) DestroyIcon(previous_big);
    if (previous_small) DestroyIcon(previous_small);
}
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
    neo_retain_guard owner(window);
    if (!owner || window->ui_destroyed.load(std::memory_order_acquire)) return DefWindowProcW(hwnd, message, wparam, lparam);
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
    if (message == WM_NCDESTROY) SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
    neo_retain_guard owner(window);
    // DestroyWindow also calls us from final-release destruction. Keep its native
    // teardown, but do not resurrect the window or deliver user callbacks there.
    if ((!owner || window->ui_destroyed.load(std::memory_order_acquire)) && message != WM_DESTROY) return DefWindowProcW(hwnd, message, wparam, lparam);
    switch (message) {
        case WM_ERASEBKGND: {
            // The window shows its own color, not the white of its class, before and behind its content.
            RECT client{};
            GetClientRect(hwnd, &client);
            const auto color = window->background_color;
            if (const auto brush = CreateSolidBrush(RGB(color.red, color.green, color.blue))) {
                FillRect(reinterpret_cast<HDC>(wparam), &client, brush);
                DeleteObject(brush);
            }
            return 1;
        }
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
            if (wparam && window->platform && static_cast<windows_window*>(window->platform)->hwnd == hwnd) {
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
            // WM_NCDESTROY follows after this callback returns, when the last
            // retained owner may already have released the window's storage.
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            if (auto* state = static_cast<windows_window*>(window->platform)) {
                destroy_title_bar_windows(state);
                HWND reactivate{};
                if (state->modal_active && window->owner) {
                    auto* native_owner = static_cast<windows_window*>(window->owner->platform);
                    if (native_owner && native_owner->modal_children && --native_owner->modal_children == 0) reactivate = native_owner->hwnd;
                }
                state->modal_active = false;
                state->hwnd = nullptr;
                // Enabling/activating the owner can reenter and detach the app,
                // deleting platform state even though the window is retained.
                if (reactivate) {
                    EnableWindow(reactivate, TRUE);
                    const auto* native_owner = window->owner ? static_cast<windows_window*>(window->owner->platform) : nullptr;
                    if (native_owner && native_owner->hwnd == reactivate) SetActiveWindow(reactivate);
                }
            }
            // A zero-reference window publishes no event, but its release still applies the shutdown policy.
            neo_window_closed(window, static_cast<bool>(owner));
            return 0;
        case WM_MOVE:
            // The message carries the client origin, which is not where a position request places the window.
            sync_bounds(window, hwnd);
            neo_emit_app(window->app, NEOASTRA_EVENT_WINDOW_MOVED, window->id);
            neo_window_display_changed(window);
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
            for (size_t index = 0; index < window->views.size();) {
                auto* view = window->views[index];
                {
                    neo_retain_guard view_owner(view);
                    auto* view_state = view_owner ? static_cast<windows_view*>(view->platform) : nullptr;
                    const auto controller = view_state ? view_state->controller : ComPtr<ICoreWebView2Controller>{};
                    if (controller && view->fill_parent) (void)controller->put_Bounds(view_bounds(view));
                }
                // Resizing a controller can reenter user code that removes views.
                if (index < window->views.size() && window->views[index] == view) ++index;
            }
            layout_title_bar(window);
            neo_emit_app(window->app, NEOASTRA_EVENT_WINDOW_RESIZED, window->id);
            if (state_changed) neo_emit_app(window->app, NEOASTRA_EVENT_WINDOW_STATE_CHANGED, window->id, nullptr, nullptr, state);
            break;
        }
        case WM_SETICON:
            // Taking an icon away gives the window the icon of its executable back.
            if (auto* native = static_cast<windows_window*>(window->platform); native && !lparam) {
                if (const auto icon = wparam == ICON_BIG ? native->executable_big_icon : native->executable_small_icon) lparam = reinterpret_cast<LPARAM>(icon);
            }
            break;
        case WM_DPICHANGED: {
            // Before the move below, which can reenter user code that releases the window.
            if (auto* native = static_cast<windows_window*>(window->platform)) apply_executable_icons(native, hwnd, HIWORD(wparam));
            const auto* suggested = reinterpret_cast<const RECT*>(lparam);
            if (auto* native = static_cast<windows_window*>(window->platform); native && native->placing) {
                // A position and a size that were asked for are in the pixels of the display that the window goes to: the window
                // takes them there, with the frame of that scale, instead of the size it had scaled to the new display.
                const auto placement = native->placement;
                const auto outer = outer_size(window, hwnd, placement.width, placement.height);
                SetWindowPos(hwnd, nullptr, placement.x, placement.y, outer.cx, outer.cy, SWP_NOACTIVATE | SWP_NOZORDER);
            } else {
                SetWindowPos(hwnd, nullptr, suggested->left, suggested->top, suggested->right - suggested->left,
                             suggested->bottom - suggested->top, SWP_NOACTIVATE | SWP_NOZORDER);
            }
            layout_title_bar(window);
            neo_window_set_scale_factor(window, HIWORD(wparam) / static_cast<double>(USER_DEFAULT_SCREEN_DPI));
            neo_window_display_changed(window);
            // The scale of a display that a user changes reaches its windows, and the list of displays tells it too.
            schedule_display_check(window->app);
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
        case WM_SETFOCUS:
            // Activation gives the keyboard to the window itself, which has nothing to type into: hand it to the
            // browser of its first visible view, which gives it back to the element of the page that had it.
            if (!IsWindowVisible(hwnd)) break;
            for (size_t index = 0; index < window->views.size();) {
                auto* view = window->views[index];
                {
                    neo_retain_guard view_owner(view);
                    auto* state = view_owner ? static_cast<windows_view*>(view->platform) : nullptr;
                    const auto controller = state ? state->controller : ComPtr<ICoreWebView2Controller>{};
                    BOOL visible{};
                    if (controller && SUCCEEDED(controller->get_IsVisible(&visible)) && visible &&
                        SUCCEEDED(controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC))) break;
                }
                if (index < window->views.size() && window->views[index] == view) ++index;
            }
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
    state->desktops.Reset();
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
void neo_platform_wake(neoastra_app_t* app) noexcept { post_dispatch(static_cast<windows_app*>(app->platform)); }
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
        const auto hwnd=CreateWindowExW(extended,window_class,title.c_str(),style,x,y,std::max(requested.width,1),std::max(requested.height,1),owner,nullptr,GetModuleHandleW(nullptr),window);
        if (window->platform != state) {
            // Creation can deliver resize events before returning its HWND, so
            // shutdown may not have had a handle with which to destroy it.
            if (hwnd && IsWindow(hwnd)) { SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0); DestroyWindow(hwnd); }
            neo_fail(error, NEOASTRA_ERROR_DISPOSED, "Application shutdown interrupted Win32 window creation", 0, "win32");
            return false;
        }
        state->hwnd=hwnd;
        if(!state->hwnd){const auto code=GetLastError();delete state;window->platform=nullptr;neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Win32 window creation failed",code,"win32");return false;}
        layout_title_bar(window);
        if (window->platform != state || !state->hwnd) { neo_fail(error, NEOASTRA_ERROR_DISPOSED, "Win32 window was destroyed during creation", 0, "win32"); return false; }
        // The frame depends on the DPI of the monitor the window landed on, so the client size is applied once the window exists.
        const auto outer=outer_size(window,state->hwnd,requested.width,requested.height);
        if(options->flags&64u){RECT area{};if(owner){RECT owner_rect{};GetWindowRect(owner,&owner_rect);area=owner_rect;}else{MONITORINFO monitor{};monitor.cbSize=sizeof(MONITORINFO);if(GetMonitorInfoW(MonitorFromWindow(state->hwnd,MONITOR_DEFAULTTOPRIMARY),&monitor))area=monitor.rcWork;}SetWindowPos(state->hwnd,nullptr,area.left+((area.right-area.left)-outer.cx)/2,area.top+((area.bottom-area.top)-outer.cy)/2,outer.cx,outer.cy,SWP_NOZORDER|SWP_NOACTIVATE);}
        else SetWindowPos(state->hwnd,nullptr,0,0,outer.cx,outer.cy,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE);
        if (window->platform != state || !state->hwnd) { neo_fail(error, NEOASTRA_ERROR_DISPOSED, "Win32 window was destroyed during creation", 0, "win32"); return false; }
        sync_bounds(window,state->hwnd);
        // A window is told of a scale only when it changes, so the one it starts with is read here.
        neo_window_set_scale_factor(window,window_scale_factor(state->hwnd),false);
        apply_executable_icons(state,state->hwnd,window_dpi(state->hwnd));
        state->modal=(options->flags&128u)!=0;if(state->modal&&(options->flags&4u)&&window->owner){auto* owner_state=static_cast<windows_window*>(window->owner->platform);if(owner_state&&owner_state->hwnd){state->modal_active=true;if(owner_state->modal_children++==0)EnableWindow(owner_state->hwnd,FALSE);}}
        if (window->platform != state || !state->hwnd) { neo_fail(error, NEOASTRA_ERROR_DISPOSED, "Win32 window was destroyed during creation", 0, "win32"); return false; }
        if(options->flags&4u){const auto show=options->state==NEOASTRA_WINDOW_MINIMIZED?SW_SHOWMINIMIZED:options->state==NEOASTRA_WINDOW_MAXIMIZED?SW_SHOWMAXIMIZED:SW_SHOW;ShowWindow(state->hwnd,show);if(options->state==NEOASTRA_WINDOW_FULLSCREEN){{std::lock_guard lock(window->state_mutex);window->state=NEOASTRA_WINDOW_FULLSCREEN;}neo_platform_window_set_state(window);}}
        if (window->platform != state || !state->hwnd) { neo_fail(error, NEOASTRA_ERROR_DISPOSED, "Win32 window was destroyed during creation", 0, "win32"); return false; }
        return true;
    } catch(const std::exception& ex){neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,ex.what());return false;}
}
void neo_platform_window_destroy(neoastra_window_t* window) noexcept {
    auto* state = static_cast<windows_window*>(window->platform);
    if (!state) return;
    HWND reactivate{};
    if (state->modal_active && window->owner) {
        auto* owner = static_cast<windows_window*>(window->owner->platform);
        if (owner && owner->modal_children && --owner->modal_children == 0) reactivate = owner->hwnd;
    }
    // DestroyWindow synchronously sends WM_DESTROY, whose teardown must not
    // consume the same modal activation (and enable an owner with modal siblings).
    state->modal_active = false;
    if (reactivate) {
        EnableWindow(reactivate, TRUE);
        const auto* owner = window->owner ? static_cast<windows_window*>(window->owner->platform) : nullptr;
        if (owner && owner->hwnd == reactivate) SetActiveWindow(reactivate);
    }
    if (state->hwnd && IsWindow(state->hwnd)) DestroyWindow(state->hwnd);
    delete state;
    window->platform = nullptr;
}
neoastra_result_t neo_platform_window_show(neoastra_window_t* w,bool visible) noexcept {
    auto* state = static_cast<windows_window*>(w->platform);
    if (!state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
    if (!visible) {
        sync_window_view_visibility(w, false);
        if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
        ShowWindow(state->hwnd, SW_HIDE);
        if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
        if (state->modal_active && w->owner) {
            state->modal_active = false;
            auto* owner = static_cast<windows_window*>(w->owner->platform);
            if (owner && owner->modal_children && --owner->modal_children == 0 && owner->hwnd) EnableWindow(owner->hwnd, TRUE);
        }
        return w->platform == state ? NEOASTRA_OK : NEOASTRA_ERROR_DISPOSED;
    }
    if (state->modal && !state->modal_active && w->owner) {
        auto* owner = static_cast<windows_window*>(w->owner->platform);
        if (owner && owner->hwnd) {
            state->modal_active = true;
            if (owner->modal_children++ == 0) EnableWindow(owner->hwnd, FALSE);
        }
    }
    if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
    neoastra_window_state_t desired{};
    { std::lock_guard lock(w->state_mutex); desired = w->state; }
    const auto command = desired == NEOASTRA_WINDOW_MINIMIZED ? SW_SHOWMINIMIZED : desired == NEOASTRA_WINDOW_MAXIMIZED ? SW_SHOWMAXIMIZED : SW_SHOW;
    ShowWindow(state->hwnd, command);
    if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
    sync_window_view_visibility(w, true);
    if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
    layout_title_bar(w);
    if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
    if (desired == NEOASTRA_WINDOW_FULLSCREEN) {
        { std::lock_guard lock(w->state_mutex); w->state = desired; }
        return neo_platform_window_set_state(w);
    }
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_window_activate(neoastra_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEOASTRA_ERROR_DISPOSED;SetForegroundWindow(s->hwnd);return NEOASTRA_OK;}
neoastra_result_t neo_platform_window_force_close(neoastra_window_t* w) noexcept {auto* s=static_cast<windows_window*>(w->platform);return s&&s->hwnd&&PostMessageW(s->hwnd,WM_CLOSE,0,0)?NEOASTRA_OK:NEOASTRA_ERROR_DISPOSED;}
neoastra_result_t neo_platform_window_set_title(neoastra_window_t* w) noexcept {try{auto* s=static_cast<windows_window*>(w->platform);auto title=widen(w->title);return s&&s->hwnd&&SetWindowTextW(s->hwnd,title.c_str())?NEOASTRA_OK:NEOASTRA_ERROR_DISPOSED;}catch(...){return NEOASTRA_ERROR_INVALID_ARGUMENT;}}
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t* w) noexcept {
    auto* s=static_cast<windows_window*>(w->platform);if(!s||!s->hwnd)return NEOASTRA_ERROR_DISPOSED;
    neoastra_rect_t requested{};{std::lock_guard lock(w->state_mutex);requested=w->bounds;}
    const auto dpi=window_dpi(s->hwnd);
    const auto outer=outer_size(w,s->hwnd,requested.width,requested.height);
    // A window that the move takes to a display of another scale is told of it during the move (WM_DPICHANGED), and keeps the
    // requested bounds rather than the size that the system scales for that display.
    s->placing=true;s->placement=requested;
    const auto moved=SetWindowPos(s->hwnd,nullptr,requested.x,requested.y,outer.cx,outer.cy,SWP_NOZORDER|SWP_NOACTIVATE);
    if (w->platform != s || !s->hwnd) return NEOASTRA_ERROR_DISPOSED;
    s->placing=false;
    if(!moved)return NEOASTRA_ERROR_DISPOSED;
    // The frame of the new scale has another size, which a window that changed its scale without being told takes here.
    if(window_dpi(s->hwnd)!=dpi&&!IsIconic(s->hwnd)&&!IsZoomed(s->hwnd)){
        const auto scaled=outer_size(w,s->hwnd,requested.width,requested.height);
        SetWindowPos(s->hwnd,nullptr,requested.x,requested.y,scaled.cx,scaled.cy,SWP_NOZORDER|SWP_NOACTIVATE);
        if (w->platform != s || !s->hwnd) return NEOASTRA_ERROR_DISPOSED;
    }
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
        if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
        state->fullscreen = true;
        const auto positioned = SetWindowPos(state->hwnd, HWND_TOP, monitor.rcMonitor.left, monitor.rcMonitor.top,
                         monitor.rcMonitor.right - monitor.rcMonitor.left, monitor.rcMonitor.bottom - monitor.rcMonitor.top,
                          SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
        if (positioned) return NEOASTRA_OK;
        SetWindowLongW(state->hwnd, GWL_STYLE, static_cast<LONG>(state->restored_style));
        if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
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
        if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
        // Win32 may still read this input while delivering reentrant size events.
        const auto placement = state->restored_placement;
        SetWindowPlacement(state->hwnd, &placement);
        if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
        SetWindowPos(state->hwnd, nullptr, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
        state->leaving_fullscreen = false;
        layout_title_bar(w);
        if (w->platform != state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
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
    return w->platform == state && state->hwnd ? NEOASTRA_OK : NEOASTRA_ERROR_DISPOSED;
}
neoastra_result_t neo_platform_window_set_attribute(neoastra_window_t* w,neoastra_window_attribute_t attribute,bool enabled) noexcept {
    auto* state=static_cast<windows_window*>(w->platform);if(!state||!state->hwnd)return NEOASTRA_ERROR_DISPOSED;
    auto attributes=w->attributes;if(enabled)attributes|=1u<<attribute;else attributes&=~(1u<<attribute);
    // Windows shows a window on every virtual desktop only through interfaces of the shell that are not documented.
    if(attribute==NEOASTRA_WINDOW_ALL_VIRTUAL_DESKTOPS)return NEOASTRA_ERROR_NOT_SUPPORTED;
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
neoastra_result_t neo_platform_window_set_background_color(neoastra_window_t* w) noexcept {
    auto* state=static_cast<windows_window*>(w->platform);if(!state||!state->hwnd)return NEOASTRA_ERROR_DISPOSED;
    InvalidateRect(state->hwnd,nullptr,TRUE);
    for(auto* view:w->views)if(view)apply_view_background(view);
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_window_set_title_bar(neoastra_window_t* w) noexcept {
    auto* state=static_cast<windows_window*>(w->platform);if(!state||!state->hwnd)return NEOASTRA_ERROR_DISPOSED;
    const auto changed=SetWindowPos(state->hwnd,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED);
    layout_title_bar(w);
    return changed?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;
}
// The frame of a window that can be resized has borders that are not drawn on its left, right, and bottom sides. The desktop
// window manager tells what is drawn, in the pixels of the display, also for a window that is hidden.
neoastra_result_t neo_platform_window_get_frame(neoastra_window_t* w,neoastra_rect_t* value) noexcept {
    auto* state=static_cast<windows_window*>(w->platform);if(!state||!state->hwnd)return NEOASTRA_ERROR_DISPOSED;
    RECT outer{};
    if(!GetWindowRect(state->hwnd,&outer))return NEOASTRA_ERROR_NATIVE_FAILURE;
    *value={outer.left,outer.top,outer.right-outer.left,outer.bottom-outer.top};
    // A thread that is not aware of the scale of its display is told of its windows in other units than those pixels.
    RECT pixels=outer,shown{};
    if(const auto previous=SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)){if(!GetWindowRect(state->hwnd,&pixels))pixels=outer;SetThreadDpiAwarenessContext(previous);}
    if(FAILED(DwmGetWindowAttribute(state->hwnd,DWMWA_EXTENDED_FRAME_BOUNDS,&shown,sizeof(shown))))return NEOASTRA_OK;
    const auto width=pixels.right-pixels.left,height=pixels.bottom-pixels.top;
    if(width<=0||height<=0||value->width<=0||value->height<=0||shown.left<pixels.left||shown.top<pixels.top||shown.right>pixels.right||shown.bottom>pixels.bottom||shown.right<=shown.left||shown.bottom<=shown.top)return NEOASTRA_OK;
    const auto across=value->width/static_cast<double>(width),down=value->height/static_cast<double>(height);
    const auto left=std::lround((shown.left-pixels.left)*across),right=std::lround((pixels.right-shown.right)*across);
    const auto top=std::lround((shown.top-pixels.top)*down),bottom=std::lround((pixels.bottom-shown.bottom)*down);
    *value={outer.left+left,outer.top+top,value->width-left-right,value->height-top-bottom};
    return NEOASTRA_OK;
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

namespace {
// What the display configuration tells of the monitor that shows a source of the desktop, named by the device of that source
// (\\.\DISPLAY1, as GetMonitorInfo names it): the device path of the monitor, which names the monitor and its connection and
// stays the same after a restart, its name, its refresh rate, and its rotation. A source that several monitors show (a mirror)
// is told of by the first of them.
struct display_path { std::wstring device; std::string id; std::string name; double refresh_rate{}; uint32_t rotation{}; bool rotation_known{}; };

std::vector<display_path> display_paths() {
    std::vector<display_path> values;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths;
    std::vector<DISPLAYCONFIG_MODE_INFO> modes;
    LONG result = ERROR_INSUFFICIENT_BUFFER;
    // The configuration can change between the size and the query.
    for (int attempt = 0; attempt < 4 && result == ERROR_INSUFFICIENT_BUFFER; ++attempt) {
        UINT32 path_count{}, mode_count{};
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &path_count, &mode_count) != ERROR_SUCCESS) return values;
        paths.resize(path_count);
        modes.resize(mode_count);
        result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &path_count, paths.data(), &mode_count, modes.data(), nullptr);
        paths.resize(path_count);
    }
    if (result != ERROR_SUCCESS) return values;
    for (const auto& path : paths) {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.size = sizeof(source);
        source.header.adapterId = path.sourceInfo.adapterId;
        source.header.id = path.sourceInfo.id;
        if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS) continue;
        if (std::any_of(values.begin(), values.end(), [&](const display_path& value) { return value.device == source.viewGdiDeviceName; })) continue;
        display_path value;
        value.device = source.viewGdiDeviceName;
        DISPLAYCONFIG_TARGET_DEVICE_NAME target{};
        target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.size = sizeof(target);
        target.header.adapterId = path.targetInfo.adapterId;
        target.header.id = path.targetInfo.id;
        if (DisplayConfigGetDeviceInfo(&target.header) == ERROR_SUCCESS) {
            value.id = narrow(target.monitorDevicePath);
            value.name = narrow(target.monitorFriendlyDeviceName);
        }
        if (path.targetInfo.refreshRate.Denominator) value.refresh_rate = static_cast<double>(path.targetInfo.refreshRate.Numerator) / path.targetInfo.refreshRate.Denominator;
        switch (path.targetInfo.rotation) {
            case DISPLAYCONFIG_ROTATION_IDENTITY: value.rotation = 0; value.rotation_known = true; break;
            case DISPLAYCONFIG_ROTATION_ROTATE90: value.rotation = 90; value.rotation_known = true; break;
            case DISPLAYCONFIG_ROTATION_ROTATE180: value.rotation = 180; value.rotation_known = true; break;
            case DISPLAYCONFIG_ROTATION_ROTATE270: value.rotation = 270; value.rotation_known = true; break;
            default: break;
        }
        values.push_back(std::move(value));
    }
    return values;
}

BOOL CALLBACK collect_monitor(HMONITOR monitor, HDC, LPRECT, LPARAM data) {
    try { reinterpret_cast<std::vector<HMONITOR>*>(data)->push_back(monitor); } catch (...) { return FALSE; }
    return TRUE;
}

// The scale that a window on the monitor reports. It depends on the awareness of the thread, as the units of the bounds do: a
// thread that is not aware of display scaling counts in logical units on every display.
double monitor_scale_factor(HMONITOR monitor) noexcept {
    const auto awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
    if (awareness == DPI_AWARENESS_UNAWARE) return 1.0;
    if (awareness == DPI_AWARENESS_SYSTEM_AWARE) return GetDpiForSystem() / static_cast<double>(USER_DEFAULT_SCREEN_DPI);
    UINT x{}, y{};
    return SUCCEEDED(GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &x, &y)) && x ? x / static_cast<double>(USER_DEFAULT_SCREEN_DPI) : 1.0;
}

bool rect_contains(const neoastra_rect_t& rect, neoastra_point_t point) noexcept {
    return point.x >= rect.x && point.y >= rect.y && int64_t{point.x} < int64_t{rect.x} + rect.width && int64_t{point.y} < int64_t{rect.y} + rect.height;
}

IVirtualDesktopManager* desktop_manager(neoastra_app_t* app) noexcept {
    auto* state = static_cast<windows_app*>(app->platform);
    if (!state) return nullptr;
    if (!state->desktops_created) {
        state->desktops_created = true;
        if (FAILED(CoCreateInstance(__uuidof(VirtualDesktopManager), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&state->desktops)))) state->desktops.Reset();
    }
    return state->desktops.Get();
}
}

// GetMonitorInfo and the bounds of a window count in the units of the awareness of the thread, so the two agree.
void neo_platform_get_displays(neoastra_app_t*, std::vector<neo_display>& displays) noexcept {
    try {
        displays.clear();
        std::vector<HMONITOR> monitors;
        EnumDisplayMonitors(nullptr, nullptr, collect_monitor, reinterpret_cast<LPARAM>(&monitors));
        const auto paths = display_paths();
        for (const auto monitor : monitors) {
            MONITORINFOEXW info{};
            info.cbSize = sizeof(info);
            if (!GetMonitorInfoW(monitor, &info)) continue;
            neo_display display;
            display.handle = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(monitor));
            display.bounds = {info.rcMonitor.left, info.rcMonitor.top, info.rcMonitor.right - info.rcMonitor.left, info.rcMonitor.bottom - info.rcMonitor.top};
            display.work_area = {info.rcWork.left, info.rcWork.top, info.rcWork.right - info.rcWork.left, info.rcWork.bottom - info.rcWork.top};
            display.flags = NEOASTRA_DISPLAY_WORK_AREA_REPORTED | ((info.dwFlags & MONITORINFOF_PRIMARY) ? NEOASTRA_DISPLAY_PRIMARY : 0u);
            display.scale_factor = monitor_scale_factor(monitor);
            const auto path = std::find_if(paths.begin(), paths.end(), [&](const display_path& value) { return value.device == info.szDevice; });
            if (path != paths.end()) {
                if (!path->id.empty()) { display.id = path->id; display.flags |= NEOASTRA_DISPLAY_ID_PERSISTENT; }
                display.name = path->name;
                display.refresh_rate = path->refresh_rate;
                if (path->rotation_known) { display.rotation_degrees = path->rotation; display.flags |= NEOASTRA_DISPLAY_ROTATION_REPORTED; }
            }
            DEVMODEW mode{};
            mode.dmSize = sizeof(mode);
            if (EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode)) {
                // 0 and 1 stand for the default rate of the hardware.
                if (display.refresh_rate <= 0 && mode.dmDisplayFrequency > 1) display.refresh_rate = mode.dmDisplayFrequency;
                if ((display.flags & NEOASTRA_DISPLAY_ROTATION_REPORTED) == 0 && (mode.dmFields & DM_DISPLAYORIENTATION) && mode.dmDisplayOrientation <= DMDO_270) {
                    display.rotation_degrees = mode.dmDisplayOrientation * 90u;
                    display.flags |= NEOASTRA_DISPLAY_ROTATION_REPORTED;
                }
            }
            // Without the display configuration (a remote session), the device lasts for the session.
            if (display.id.empty()) display.id = narrow(info.szDevice);
            if (display.name.empty()) {
                DISPLAY_DEVICEW device{};
                device.cb = sizeof(device);
                if (EnumDisplayDevicesW(info.szDevice, 0, &device, 0)) display.name = narrow(device.DeviceString);
            }
            displays.push_back(std::move(display));
        }
    } catch (...) { displays.clear(); }
}

// A minimized window is on the display of the bounds it goes back to.
uint64_t neo_platform_window_display_handle(neoastra_window_t* window) noexcept {
    auto* state = static_cast<windows_window*>(window->platform);
    if (!state || !state->hwnd) return 0;
    return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(MonitorFromWindow(state->hwnd, MONITOR_DEFAULTTONEAREST)));
}

void neo_platform_window_capability(neoastra_app_t* app, neoastra_window_capability_t capability, neoastra_capability_info_t* info) noexcept {
    static const std::string displays = "Win32 monitors and their work areas, in the units that the awareness of the thread to display scaling gives; the device path of the monitor from the display configuration is its identifier, with its friendly name, scale, rotation, and refresh rate";
    static const std::string placement = "Positions are those of the frame in virtual-screen coordinates; a window placed on a display of another scale keeps the bounds it was given";
    static const std::string desktops = "IVirtualDesktopManager tells whether a window is on the current virtual desktop and the identifier of its desktop";
    static const std::string move = "IVirtualDesktopManager moves a window of the application to another virtual desktop";
    static const std::string no_desktops = "The shell provides no virtual desktops: IVirtualDesktopManager is unavailable";
    static const std::string all_desktops = "Windows shows a window on every virtual desktop only through undocumented shell interfaces";
    switch (capability) {
        case NEOASTRA_WINDOW_CAPABILITY_DISPLAYS: info->support = NEOASTRA_SUPPORT_NATIVE; info->details = neo_string_view(displays); break;
        case NEOASTRA_WINDOW_CAPABILITY_PLACEMENT: info->support = NEOASTRA_SUPPORT_NATIVE; info->details = neo_string_view(placement); break;
        case NEOASTRA_WINDOW_CAPABILITY_VIRTUAL_DESKTOPS:
        case NEOASTRA_WINDOW_CAPABILITY_VIRTUAL_DESKTOP_MOVE: {
            // The shell object is created on the UI thread; another thread is told what that thread found, or that it is expected.
            const auto* state = static_cast<windows_app*>(app->platform);
            const auto available = app->ui_thread == std::this_thread::get_id() ? desktop_manager(app) != nullptr : !state || !state->desktops_created || state->desktops;
            info->support = available ? NEOASTRA_SUPPORT_NATIVE : NEOASTRA_SUPPORT_NONE;
            info->details = neo_string_view(!available ? no_desktops : capability == NEOASTRA_WINDOW_CAPABILITY_VIRTUAL_DESKTOPS ? desktops : move);
            break;
        }
        case NEOASTRA_WINDOW_CAPABILITY_ALL_VIRTUAL_DESKTOPS: info->details = neo_string_view(all_desktops); break;
        default: break;
    }
}

neoastra_result_t neo_platform_window_get_virtual_desktop(neoastra_window_t* w, uint32_t* flags, std::string* id) noexcept {
    auto* state = static_cast<windows_window*>(w->platform);
    if (!state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
    auto* manager = desktop_manager(w->app);
    if (!manager) return NEOASTRA_ERROR_NOT_SUPPORTED;
    BOOL current{};
    GUID desktop{};
    const auto told = manager->IsWindowOnCurrentVirtualDesktop(state->hwnd, &current);
    const auto named = manager->GetWindowDesktopId(state->hwnd, &desktop);
    if (SUCCEEDED(told)) *flags |= NEOASTRA_VIRTUAL_DESKTOP_CURRENT_KNOWN | (current ? NEOASTRA_VIRTUAL_DESKTOP_ON_CURRENT : 0u);
    // A window that was never shown is on no desktop yet.
    if (SUCCEEDED(named) && desktop != GUID{}) {
        wchar_t text[40]{};
        if (StringFromGUID2(desktop, text, 40) > 0) *id = narrow(text);
    }
    return SUCCEEDED(told) || !id->empty() ? NEOASTRA_OK : NEOASTRA_ERROR_INVALID_STATE;
}

neoastra_result_t neo_platform_window_move_to_virtual_desktop(neoastra_window_t* w, const std::string& id) noexcept {
    auto* state = static_cast<windows_window*>(w->platform);
    if (!state || !state->hwnd) return NEOASTRA_ERROR_DISPOSED;
    auto* manager = desktop_manager(w->app);
    if (!manager) return NEOASTRA_ERROR_NOT_SUPPORTED;
    GUID desktop{};
    try {
        const auto text = widen(id);
        if (FAILED(IIDFromString(text.c_str(), &desktop)) || desktop == GUID{}) return NEOASTRA_ERROR_INVALID_ARGUMENT;
    } catch (...) { return NEOASTRA_ERROR_INVALID_ARGUMENT; }
    GUID current{};
    if (FAILED(manager->GetWindowDesktopId(state->hwnd, &current)) || current == GUID{}) return NEOASTRA_ERROR_INVALID_STATE;
    if (current == desktop) return NEOASTRA_OK;
    const auto result = manager->MoveWindowToDesktop(state->hwnd, desktop);
    if (SUCCEEDED(result)) return NEOASTRA_OK;
    // The shell names a desktop that does not exist an element it did not find.
    return result == TYPE_E_ELEMENTNOTFOUND || result == E_INVALIDARG ? NEOASTRA_ERROR_INVALID_ARGUMENT : NEOASTRA_ERROR_NATIVE_FAILURE;
}

// The top-level windows in their order on screen, front first. A window on another virtual desktop is cloaked by the desktop
// window manager while it says it is visible.
neoastra_result_t neo_platform_window_at(neoastra_app_t* app, neoastra_point_t point, const neoastra_window_t* excluded, uint64_t* id) noexcept {
    std::vector<std::pair<HWND, neoastra_window_t*>> windows;
    try {
        {
            std::lock_guard lock(app->windows_mutex);
            windows.reserve(app->windows.size());
            for (const auto& entry : app->windows) {
                auto* window = entry.second;
                auto* state = static_cast<windows_window*>(window->platform);
                if (window == excluded || !state || !state->hwnd || !window->retain()) continue;
                windows.emplace_back(state->hwnd, window);
            }
        }
        for (HWND hwnd = GetTopWindow(nullptr); hwnd && *id == 0; hwnd = GetWindow(hwnd, GW_HWNDNEXT)) {
            const auto found = std::find_if(windows.begin(), windows.end(), [hwnd](const auto& value) { return value.first == hwnd; });
            if (found == windows.end() || !IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            DWORD cloaked{};
            if (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked) continue;
            auto* window = found->second;
            neoastra_rect_t frame{};
            { std::lock_guard lock(window->state_mutex); frame = window->bounds; }
            if (neo_platform_window_get_frame(window, &frame) == NEOASTRA_OK && rect_contains(frame, point)) *id = window->id;
        }
    } catch (...) { *id = 0; }
    for (const auto& entry : windows) entry.second->release();
    return NEOASTRA_OK;
}

// A point of the page is in CSS pixels, which the zoom and the rasterization scale of the controller make pixels of its bounds in
// the client area of its parent, and the client area is placed on screen in the units of the awareness of that parent: those of
// the bounds of the window, which its messages give it.
neoastra_result_t neo_platform_view_convert_point(neoastra_view_t* view, bool to_screen, double x, double y, double* out_x, double* out_y) noexcept {
    auto* state = static_cast<windows_view*>(view->platform);
    if (!state || !state->controller) return NEOASTRA_ERROR_NOT_INITIALIZED;
    HWND parent{};
    RECT bounds{};
    if (FAILED(state->controller->get_ParentWindow(&parent)) || !parent || FAILED(state->controller->get_Bounds(&bounds))) return NEOASTRA_ERROR_NATIVE_FAILURE;
    POINT origin{};
    bool converted{};
    if (const auto previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(parent))) {
        converted = ClientToScreen(parent, &origin) != FALSE;
        SetThreadDpiAwarenessContext(previous);
    } else converted = ClientToScreen(parent, &origin) != FALSE;
    if (!converted) return NEOASTRA_ERROR_NATIVE_FAILURE;
    double scale = 1.0, zoom = 1.0;
    ComPtr<ICoreWebView2Controller3> controller3;
    if (SUCCEEDED(state->controller.As(&controller3)) && (FAILED(controller3->get_RasterizationScale(&scale)) || !(scale > 0.0))) scale = 1.0;
    if (FAILED(state->controller->get_ZoomFactor(&zoom)) || !(zoom > 0.0)) zoom = 1.0;
    const auto unit = scale * zoom;
    const auto left = static_cast<double>(origin.x) + bounds.left, top = static_cast<double>(origin.y) + bounds.top;
    if (to_screen) { *out_x = left + x * unit; *out_y = top + y * unit; }
    else { *out_x = (x - left) / unit; *out_y = (y - top) / unit; }
    return NEOASTRA_OK;
}

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

namespace {

// OLE gives a drop target no source. While a drag runs, DoDragDrop holds the mouse capture of its source thread, so
// a capture held by a thread of this view's browser process (not this process: its own outbound drags stay
// external) means the drag started in that browser, as an in-page HTML5 drag does. Returns that thread, or 0.
DWORD drag_started_in_view_browser(const neoastra_view_t* view) noexcept {
    const auto* state = view ? static_cast<const windows_view*>(view->platform) : nullptr;
    if (!state) return 0;
    const auto self = GetCurrentProcessId();
    for (const auto& registration : state->drop_registrations) {
        DWORD process{};
        const auto thread = GetWindowThreadProcessId(registration.window, &process);
        if (!thread || process == self) continue;
        GUITHREADINFO info{};
        info.cbSize = sizeof(info);
        if (GetGUIThreadInfo(thread, &info) && info.hwndCapture) return thread;
    }
    return 0;
}

constexpr SIZE_T maximum_page_drag_format_bytes = 8u * 1024u * 1024u;

bool read_global_text(IDataObject* data, CLIPFORMAT format, std::string& value) {
    FORMATETC request{format, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    STGMEDIUM medium{};
    if (FAILED(data->GetData(&request, &medium))) return false;
    bool read{};
    const auto bytes = medium.tymed == TYMED_HGLOBAL ? GlobalSize(medium.hGlobal) : 0;
    if (bytes >= sizeof(wchar_t) && bytes <= maximum_page_drag_format_bytes) {
        if (const auto* text = static_cast<const wchar_t*>(GlobalLock(medium.hGlobal))) {
            const auto length = wcsnlen(text, bytes / sizeof(wchar_t));
            try { value = narrow(std::wstring(text, length).c_str()); read = true; } catch (...) { }
            GlobalUnlock(medium.hGlobal);
        }
    }
    ReleaseStgMedium(&medium);
    return read;
}

// Chromium writes the custom MIME types of a drag as a base::Pickle: a 32-bit payload size, a 32-bit entry count,
// then for each entry a type and a value as a 32-bit UTF-16 length and its code units, each field padded to 4 bytes.
void read_custom_drag_data(IDataObject* data, std::vector<std::pair<std::string, std::string>>& values) {
    FORMATETC request{static_cast<CLIPFORMAT>(RegisterClipboardFormatW(L"Chromium Web Custom MIME Data Format")), nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    STGMEDIUM medium{};
    if (FAILED(data->GetData(&request, &medium))) return;
    const auto bytes = medium.tymed == TYMED_HGLOBAL ? GlobalSize(medium.hGlobal) : 0;
    const auto* pickle = bytes >= 8 && bytes <= maximum_page_drag_format_bytes ? static_cast<const uint8_t*>(GlobalLock(medium.hGlobal)) : nullptr;
    if (pickle) {
        try {
            uint32_t payload{}, count{};
            std::memcpy(&payload, pickle, sizeof(payload));
            const size_t end = std::min<size_t>(bytes, size_t{4} + payload);
            size_t offset = 4;
            auto read_string = [&](std::string& value) {
                int32_t length{};
                if (end - offset < sizeof(length)) return false;
                std::memcpy(&length, pickle + offset, sizeof(length));
                offset += sizeof(length);
                if (length < 0 || static_cast<size_t>(length) > (end - offset) / sizeof(wchar_t)) return false;
                std::wstring text(static_cast<size_t>(length), L'\0');
                if (length) std::memcpy(text.data(), pickle + offset, static_cast<size_t>(length) * sizeof(wchar_t));
                offset += (static_cast<size_t>(length) * sizeof(wchar_t) + 3u) & ~size_t{3};
                if (offset > end) return false;
                value = narrow(text.c_str());
                return true;
            };
            if (end >= 8) {
                std::memcpy(&count, pickle + 4, sizeof(count));
                offset = 8;
                for (uint32_t index = 0; index < count && index < 256; ++index) {
                    std::string type, value;
                    if (!read_string(type) || !read_string(value)) break;
                    if (!type.empty()) values.emplace_back(std::move(type), std::move(value));
                }
            }
        } catch (...) { values.clear(); }
        GlobalUnlock(medium.hGlobal);
    }
    ReleaseStgMedium(&medium);
}

// The "data" member of Input.dispatchDragEvent, in UTF-8: text, URL, and custom MIME items, and the allowed operations.
std::string page_drag_data(IDataObject* data, DWORD allowed) noexcept {
    try {
        int mask{};
        if (allowed & DROPEFFECT_COPY) mask |= 1;
        if (allowed & DROPEFFECT_LINK) mask |= 2;
        if (allowed & DROPEFFECT_MOVE) mask |= 16;
        if (!mask) return {};
        std::vector<std::pair<std::string, std::string>> items;
        std::string value;
        if (read_global_text(data, CF_UNICODETEXT, value)) items.emplace_back("text/plain", std::move(value));
        if (read_global_text(data, static_cast<CLIPFORMAT>(RegisterClipboardFormatW(L"UniformResourceLocatorW")), value)) items.emplace_back("text/uri-list", std::move(value));
        read_custom_drag_data(data, items);
        std::string json = "{\"items\":[";
        for (size_t index = 0; index < items.size(); ++index) {
            if (index) json.push_back(',');
            json += "{\"mimeType\":";
            append_json_string(json, items[index].first);
            json += ",\"data\":";
            append_json_string(json, items[index].second);
            json.push_back('}');
        }
        json += "],\"dragOperationsMask\":" + std::to_string(mask) + "}";
        return json;
    } catch (...) { return {}; }
}

// Dispatches one drag event to the page at a screen point, in CSS pixels of its viewport.
bool forward_page_drag(neoastra_view_t* view, const wchar_t* type, POINTL point, DWORD keys, const std::string& data, std::shared_ptr<bool> done) noexcept {
    try {
        auto* state = view ? static_cast<windows_view*>(view->platform) : nullptr;
        if (!state || !state->core || !state->controller) return false;
        const auto core = state->core;
        const auto controller = state->controller;
        HWND parent{};
        RECT bounds{};
        if (FAILED(controller->get_ParentWindow(&parent)) || !parent || FAILED(controller->get_Bounds(&bounds))) return false;
        // OLE hands the screen point of the drag loop in physical pixels, whatever the DPI awareness of this call (a COM
        // call arrives through a window of its own): convert it in the coordinate space of the parent of the controller.
        POINT client{point.x, point.y};
        (void)PhysicalToLogicalPointForPerMonitorDPI(parent, &client);
        bool converted{};
        if (const auto previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(parent))) {
            converted = ScreenToClient(parent, &client);
            SetThreadDpiAwarenessContext(previous);
        } else converted = ScreenToClient(parent, &client);
        if (!converted) return false;
        double scale = 1.0, zoom = 1.0;
        ComPtr<ICoreWebView2Controller3> controller3;
        if (SUCCEEDED(controller.As(&controller3)) && (FAILED(controller3->get_RasterizationScale(&scale)) || !(scale > 0.0))) scale = 1.0;
        if (FAILED(controller->get_ZoomFactor(&zoom)) || !(zoom > 0.0)) zoom = 1.0;
        int modifiers{};
        if (keys & MK_ALT) modifiers |= 1;
        if (keys & MK_CONTROL) modifiers |= 2;
        if (keys & MK_SHIFT) modifiers |= 8;
        std::wstring parameters = L"{\"type\":\"";
        parameters += type;
        parameters += L"\",\"x\":";
        append_protocol_number(parameters, (client.x - bounds.left) / (scale * zoom));
        parameters += L",\"y\":";
        append_protocol_number(parameters, (client.y - bounds.top) / (scale * zoom));
        parameters += L",\"modifiers\":" + std::to_wstring(modifiers) + L",\"data\":" + widen(data) + L"}";
        auto* app = view->environment->app;
        if (!app->retain()) return false;
        // The completion can arrive after the view and its environment are gone.
        // Own the app at scheduling time, including synchronous start failures.
        auto app_owner = std::shared_ptr<neoastra_app_t>(app, [](neoastra_app_t* value) { value->release(); });
        const bool report = wcscmp(type, L"dragOver") != 0;
        return SUCCEEDED(core->CallDevToolsProtocolMethod(L"Input.dispatchDragEvent", parameters.c_str(),
            Callback<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>([app_owner, report, done](HRESULT result, LPCWSTR) -> HRESULT {
                if (done) *done = true;
                if (FAILED(result) && report) neo_log(app_owner.get(), NEOASTRA_LOG_WARNING, "drag-drop", "WebView2 did not dispatch a page drag event");
                return S_OK;
            }).Get()));
    } catch (...) { return false; }
}

// Reads the JSON string whose opening quote is at `index` into UTF-16 and moves past it.
bool read_json_string(const std::wstring& json, size_t& index, std::wstring& value) {
    if (index >= json.size() || json[index] != L'"') return false;
    value.clear();
    for (++index; index < json.size(); ++index) {
        const auto character = json[index];
        if (character == L'"') { ++index; return true; }
        if (character != L'\\') { value.push_back(character); continue; }
        if (++index >= json.size()) return false;
        switch (json[index]) {
            case L'"': value.push_back(L'"'); break;
            case L'\\': value.push_back(L'\\'); break;
            case L'/': value.push_back(L'/'); break;
            case L'b': value.push_back(L'\b'); break;
            case L'f': value.push_back(L'\f'); break;
            case L'n': value.push_back(L'\n'); break;
            case L'r': value.push_back(L'\r'); break;
            case L't': value.push_back(L'\t'); break;
            case L'u': {
                if (json.size() - index <= 4) return false;
                unsigned code{};
                for (size_t digit = 1; digit <= 4; ++digit) {
                    const auto hex = json[index + digit];
                    code <<= 4;
                    if (hex >= L'0' && hex <= L'9') code |= static_cast<unsigned>(hex - L'0');
                    else if (hex >= L'a' && hex <= L'f') code |= static_cast<unsigned>(hex - L'a' + 10);
                    else if (hex >= L'A' && hex <= L'F') code |= static_cast<unsigned>(hex - L'A' + 10);
                    else return false;
                }
                value.push_back(static_cast<wchar_t>(code));
                index += 4;
                break;
            }
            default: return false;
        }
    }
    return false;
}

// The JSON text of the object value of the first member with this name.
bool json_object_member(const std::wstring& json, const wchar_t* member, std::wstring& value) {
    const auto key = std::wstring(L"\"") + member + L"\"";
    const auto at = json.find(key);
    const auto start = at == std::wstring::npos ? at : json.find(L'{', at + key.size());
    if (start == std::wstring::npos) return false;
    int depth{};
    bool quoted{};
    for (auto index = start; index < json.size(); ++index) {
        const auto character = json[index];
        if (quoted) { if (character == L'\\') ++index; else if (character == L'"') quoted = false; continue; }
        if (character == L'"') quoted = true;
        else if (character == L'{') ++depth;
        else if (character == L'}' && --depth == 0) { value = json.substr(start, index - start + 1); return true; }
    }
    return false;
}

// The string value following a member name at or after `index`, or false.
bool json_string_member(const std::wstring& json, const wchar_t* member, size_t& index, std::wstring& value) {
    const auto key = std::wstring(L"\"") + member + L"\"";
    const auto at = json.find(key, index);
    if (at == std::wstring::npos) return false;
    const auto colon = json.find(L':', at + key.size());
    const auto quote = colon == std::wstring::npos ? colon : json.find(L'"', colon);
    if (quote == std::wstring::npos) return false;
    index = quote;
    return read_json_string(json, index, value);
}

bool set_global_data(IDataObject* object, CLIPFORMAT format, const void* bytes, size_t size) noexcept {
    const auto handle = GlobalAlloc(GMEM_MOVEABLE, size);
    if (!handle) return false;
    if (auto* target = GlobalLock(handle)) { std::memcpy(target, bytes, size); GlobalUnlock(handle); }
    else { GlobalFree(handle); return false; }
    FORMATETC request{format, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    STGMEDIUM medium{};
    medium.tymed = TYMED_HGLOBAL;
    medium.hGlobal = handle;
    if (SUCCEEDED(object->SetData(&request, &medium, TRUE))) return true;
    GlobalFree(handle);
    return false;
}

// What other applications get from a drag of the page: its text, its URL, and its custom MIME types the way Chromium
// writes them (see read_custom_drag_data).
ComPtr<IDataObject> page_drag_object(const std::wstring& data) {
    ComPtr<IDataObject> object;
    if (FAILED(SHCreateDataObject(nullptr, 0, nullptr, nullptr, IID_PPV_ARGS(&object)))) return {};
    std::vector<std::pair<std::wstring, std::wstring>> custom;
    size_t index{};
    std::wstring type, value;
    while (json_string_member(data, L"mimeType", index, type) && json_string_member(data, L"data", index, value)) {
        if (type == L"text/plain") (void)set_global_data(object.Get(), CF_UNICODETEXT, value.c_str(), (value.size() + 1) * sizeof(wchar_t));
        else if (type == L"text/uri-list") {
            const auto url = value.substr(0, value.find_first_of(L"\r\n"));
            (void)set_global_data(object.Get(), static_cast<CLIPFORMAT>(RegisterClipboardFormatW(L"UniformResourceLocatorW")), url.c_str(), (url.size() + 1) * sizeof(wchar_t));
        } else if (type != L"text/html" && custom.size() < 256) custom.emplace_back(type, value);
    }
    if (!custom.empty()) {
        std::vector<uint8_t> pickle(8);
        auto append = [&pickle](const std::wstring& text) {
            const auto length = static_cast<int32_t>(text.size());
            const auto offset = pickle.size();
            pickle.resize(offset + sizeof(length) + ((text.size() * sizeof(wchar_t) + 3u) & ~size_t{3}));
            std::memcpy(pickle.data() + offset, &length, sizeof(length));
            if (length) std::memcpy(pickle.data() + offset + sizeof(length), text.data(), text.size() * sizeof(wchar_t));
        };
        for (const auto& [name, content] : custom) { append(name); append(content); }
        const auto payload = static_cast<uint32_t>(pickle.size() - 4), count = static_cast<uint32_t>(custom.size());
        std::memcpy(pickle.data(), &payload, sizeof(payload));
        std::memcpy(pickle.data() + 4, &count, sizeof(count));
        (void)set_global_data(object.Get(), static_cast<CLIPFORMAT>(RegisterClipboardFormatW(L"Chromium Web Custom MIME Data Format")), pickle.data(), pickle.size());
    }
    return object;
}

class page_drop_source final : public IDropSource {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid != IID_IUnknown && iid != IID_IDropSource) return E_NOINTERFACE;
        *value = static_cast<IDropSource*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references_)); }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto value = static_cast<ULONG>(InterlockedDecrement(&references_));
        if (value == 0) delete this;
        return value;
    }
    // The button was pressed while the browser had the mouse, so the key state of this thread, which OLE passes, never
    // saw it go down: the drag goes on while the physical button is held.
    HRESULT STDMETHODCALLTYPE QueryContinueDrag(BOOL escape, DWORD) override {
        if (escape) { escaped_ = true; return DRAGDROP_S_CANCEL; }
        return GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) ? VK_RBUTTON : VK_LBUTTON) & 0x8000 ? S_OK : DRAGDROP_S_DROP;
    }
    HRESULT STDMETHODCALLTYPE GiveFeedback(DWORD) override { return DRAGDROP_S_USEDEFAULTCURSORS; }
    // The user ended the drag with the Escape key, rather than by releasing it where nothing took it: OLE reports both
    // as a cancel.
    bool escaped() const noexcept { return escaped_; }
private:
    ~page_drop_source() = default;
    volatile LONG references_{1};
    bool escaped_{};
};

std::vector<std::pair<UINT_PTR, neoastra_view_t*>> page_drags; // Views whose page drag starts on a timer (UI thread only).

const std::string* own_page_drag(const neoastra_view_t* view) noexcept {
    const auto* state = view ? static_cast<const windows_view*>(view->platform) : nullptr;
    return state && state->page_drag_active ? &state->page_drag_data : nullptr;
}

// The page drag that NeoAstra runs in this process, while its loop runs (see run_page_drag). Its data object carries
// a nonce of the drag in a format of its own, by which a view of another window of the application, on any thread,
// recognizes the drag and gives its page the original data. The data stays here: what another process could put in
// that format cannot pass for a page drag, as it does not know the nonce of the drag that runs. `target` is the view
// that took the drop, never dereferenced: the host is told its address, by which it finds the view.
constexpr wchar_t app_page_drag_format[] = L"NeoAstra Page Drag";
struct app_page_drag_record { std::mutex lock; std::string nonce; std::string data; const neoastra_view_t* target{}; };
app_page_drag_record& running_app_page_drag() noexcept {
    static app_page_drag_record value;
    return value;
}

bool app_page_drag(IDataObject* data, std::string& value) noexcept {
    try {
        std::string nonce;
        if (!read_global_text(data, static_cast<CLIPFORMAT>(RegisterClipboardFormatW(app_page_drag_format)), nonce) || nonce.empty()) return false;
        auto& running = running_app_page_drag();
        std::lock_guard guard(running.lock);
        if (running.nonce.empty() || running.nonce != nonce) return false;
        value = running.data;
        return true;
    } catch (...) { return false; }
}

// Waits for the page to have its drop, for at most two seconds, so that the drag ends in its source after it.
void wait_for_page_drop(const std::shared_ptr<bool>& done) noexcept {
    const auto deadline = GetTickCount64() + 2000;
    MSG message{};
    while (!*done && GetTickCount64() < deadline) {
        if (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
        else MsgWaitForMultipleObjectsEx(0, nullptr, 10, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    }
}

// The loop of the page drag waits here for the page to have its drop: once it returns, the drag ends in the page.
bool drop_page_drag(neoastra_view_t* view, POINTL point, DWORD keys) noexcept {
    auto* state = view ? static_cast<windows_view*>(view->platform) : nullptr;
    if (!state || !state->page_drag_active) return false;
    auto done = std::make_shared<bool>(false);
    if (!forward_page_drag(view, L"drop", point, keys, state->page_drag_data, done)) return false;
    state = static_cast<windows_view*>(view->platform);
    if (!state) return false;
    state->page_drag_dropped = true;
    wait_for_page_drop(done);
    return true;
}

// A drag of another view dropped on this one: its loop ends it in its own page once this returns.
bool drop_app_page_drag(neoastra_view_t* view, POINTL point, DWORD keys, const std::string& data) noexcept {
    auto done = std::make_shared<bool>(false);
    if (!forward_page_drag(view, L"drop", point, keys, data, done)) return false;
    try {
        auto& running = running_app_page_drag();
        std::lock_guard guard(running.lock);
        if (!running.nonce.empty()) running.target = view;
    } catch (...) { }
    wait_for_page_drop(done);
    return true;
}

// How a drag of the page that NeoAstra ran ended, in bits 0-3 of the value of its event (see NeoAstra.PageDragEnded).
enum class page_drag_outcome : uint64_t { canceled = 0, not_dropped = 1, dropped_on_view = 2, dropped_on_other_application = 3 };

// Tells the host of the source view how the drag of its page ended: a drop event of kind 3 whose value holds the outcome
// and, in bits 4-7, the effect the drop target answered; its text is the data of the drag, its position the pointer on
// the screen, and its second value the view of the application that took the drop. Raised last: the host may close the
// view in its handler.
void emit_page_drag_ended(neoastra_view_t* view, page_drag_outcome outcome, DWORD effect, const neoastra_view_t* target, const std::string& data) noexcept {
    POINT cursor{};
    (void)GetCursorPos(&cursor);
    neo_event_details details{};
    details.bounds = {static_cast<int32_t>(cursor.x), static_cast<int32_t>(cursor.y), 0, 0};
    details.value2 = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(target));
    const auto effects = static_cast<uint64_t>(effect & (DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK));
    neo_emit_view_detailed(view, NEOASTRA_EVENT_MESSAGE_RECEIVED, 0, &data, nullptr,
                           (UINT64_C(1) << 63) | (UINT64_C(3) << 56) | static_cast<uint64_t>(outcome) | (effects << 4), 0, nullptr, details);
}

// The loop of DoDragDrop waits for mouse and keyboard messages of its own window, and runs the other messages of the
// thread only once one arrives: while the pointer is held still, nothing of the application runs, neither its queued
// work nor the callbacks of WebView2 (its messages from the pages among them), and timers and posted messages do not
// wake it. While a drag of the page runs, a timer of the thread pool posts to that window, every 50 ms (the pace at
// which HTML fires dragover), a move of the pointer where it is, which wakes the loop. It posts only to the window of
// OLE that holds the capture of the UI thread, so it does nothing should OLE run its loop otherwise.
class drag_loop_waker final {
public:
    explicit drag_loop_waker(DWORD thread) noexcept : thread_(thread) {
        timer_ = CreateThreadpoolTimer(&drag_loop_waker::tick, this, nullptr);
        if (!timer_) return;
        ULARGE_INTEGER due{};
        due.QuadPart = static_cast<ULONGLONG>(-static_cast<LONGLONG>(period_ms) * 10000);
        FILETIME start{due.LowPart, due.HighPart};
        SetThreadpoolTimer(timer_, &start, period_ms, 10);
    }
    drag_loop_waker(const drag_loop_waker&) = delete;
    drag_loop_waker& operator=(const drag_loop_waker&) = delete;
    ~drag_loop_waker() {
        if (!timer_) return;
        SetThreadpoolTimer(timer_, nullptr, 0, 0);
        WaitForThreadpoolTimerCallbacks(timer_, TRUE);
        CloseThreadpoolTimer(timer_);
    }
private:
    static constexpr DWORD period_ms = 50;
    static void CALLBACK tick(PTP_CALLBACK_INSTANCE, void* context, PTP_TIMER) noexcept {
        const auto* self = static_cast<const drag_loop_waker*>(context);
        GUITHREADINFO info{};
        info.cbSize = sizeof(info);
        wchar_t name[32]{};
        if (!GetGUIThreadInfo(self->thread_, &info) || !info.hwndCapture || !GetClassNameW(info.hwndCapture, name, 32) || wcscmp(name, L"CLIPBRDWNDCLASS") != 0) return;
        POINT cursor{};
        if (!GetCursorPos(&cursor) || !ScreenToClient(info.hwndCapture, &cursor)) return;
        WPARAM keys{};
        const bool swapped = GetSystemMetrics(SM_SWAPBUTTON) != 0;
        if (GetAsyncKeyState(swapped ? VK_RBUTTON : VK_LBUTTON) & 0x8000) keys |= MK_LBUTTON;
        if (GetAsyncKeyState(swapped ? VK_LBUTTON : VK_RBUTTON) & 0x8000) keys |= MK_RBUTTON;
        if (GetAsyncKeyState(VK_SHIFT) & 0x8000) keys |= MK_SHIFT;
        if (GetAsyncKeyState(VK_CONTROL) & 0x8000) keys |= MK_CONTROL;
        (void)PostMessageW(info.hwndCapture, WM_MOUSEMOVE, keys, MAKELPARAM(static_cast<short>(cursor.x), static_cast<short>(cursor.y)));
    }
    DWORD thread_{};
    PTP_TIMER timer_{};
};

// Runs the drag the page started, outside of the DevTools event that reported it: an OLE drag loop of this process,
// so that the drag can leave the window and NeoAstra's own target can give the page its drop before the drag ends.
void run_page_drag(neoastra_view_t* view) noexcept {
    auto* state = static_cast<windows_view*>(view->platform);
    if (!state || state->page_drag_data.empty()) return;
    std::string data = std::move(state->page_drag_data);
    state->page_drag_data.clear();
    try {
        const auto wide = widen(data);
        DWORD allowed{};
        if (const auto at = wide.find(L"\"dragOperationsMask\""); at != std::wstring::npos) {
            const auto colon = wide.find(L':', at);
            const auto mask = colon == std::wstring::npos ? 0 : std::wcstol(wide.c_str() + colon + 1, nullptr, 10);
            if (mask & 1) allowed |= DROPEFFECT_COPY;
            if (mask & 2) allowed |= DROPEFFECT_LINK;
            if (mask & 16) allowed |= DROPEFFECT_MOVE;
        }
        const auto button = GetSystemMetrics(SM_SWAPBUTTON) ? VK_RBUTTON : VK_LBUTTON;
        auto object = page_drag_object(wide);
        auto* drop_source = new (std::nothrow) page_drop_source();
        ComPtr<IDropSource> source;
        source.Attach(drop_source);
        POINT cursor{};
        if (allowed && object && source && (GetAsyncKeyState(button) & 0x8000)) {
            // The nonce by which the other views of the application recognize this drag (see app_page_drag).
            GUID guid{};
            wchar_t nonce[40]{};
            const bool marked = SUCCEEDED(CoCreateGuid(&guid)) && StringFromGUID2(guid, nonce, 40) > 0 &&
                                set_global_data(object.Get(), static_cast<CLIPFORMAT>(RegisterClipboardFormatW(app_page_drag_format)), nonce, (wcslen(nonce) + 1) * sizeof(wchar_t));
            auto& running = running_app_page_drag();
            if (marked) { std::lock_guard guard(running.lock); running.nonce = narrow(nonce); running.data = data; running.target = nullptr; }
            state->page_drag_data = data;
            state->page_drag_active = true;
            state->page_drag_dropped = false;
            DWORD effect{};
            HRESULT result{};
            {
                drag_loop_waker waker(GetCurrentThreadId());
                result = DoDragDrop(object.Get(), source.Get(), allowed, &effect);
            }
            const neoastra_view_t* target{};
            if (marked) { std::lock_guard guard(running.lock); target = running.target; running.nonce.clear(); running.data.clear(); running.target = nullptr; }
            state = static_cast<windows_view*>(view->platform); // The view may have been closed during the loop.
            const bool dropped = state && state->page_drag_dropped;
            if (dropped) target = view;
            if (state) { state->page_drag_active = false; state->page_drag_data.clear(); }
            if (state && !dropped && GetPhysicalCursorPos(&cursor)) (void)forward_page_drag(view, L"dragCancel", {cursor.x, cursor.y}, 0, data);
            const auto outcome = target ? page_drag_outcome::dropped_on_view
                               : drop_source->escaped() ? page_drag_outcome::canceled
                               : result == DRAGDROP_S_DROP && effect != DROPEFFECT_NONE ? page_drag_outcome::dropped_on_other_application
                                                                                       : page_drag_outcome::not_dropped;
            emit_page_drag_ended(view, outcome, outcome == page_drag_outcome::dropped_on_view || outcome == page_drag_outcome::dropped_on_other_application ? effect : DROPEFFECT_NONE, target, data);
            return;
        }
        // No drag to run (the button is already up, or nothing to carry): the page still waits for its end.
        if (GetPhysicalCursorPos(&cursor)) (void)forward_page_drag(view, L"dragCancel", {cursor.x, cursor.y}, 0, data);
        emit_page_drag_ended(view, page_drag_outcome::not_dropped, DROPEFFECT_NONE, nullptr, data);
    } catch (...) {
        neo_log(view->environment->app, NEOASTRA_LOG_WARNING, "drag-drop", "A drag of the page could not be run");
    }
}

void CALLBACK page_drag_tick(HWND, UINT, UINT_PTR timer, DWORD) noexcept {
    KillTimer(nullptr, timer);
    const auto found = std::find_if(page_drags.begin(), page_drags.end(), [timer](const auto& value) { return value.first == timer; });
    if (found == page_drags.end()) return;
    auto* view = found->second;
    page_drags.erase(found);
    neo_retain_guard owner(view);
    if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return;
    auto* state = static_cast<windows_view*>(view->platform);
    if (!state || state->page_drag_timer != timer) return;
    state->page_drag_timer = 0;
    run_page_drag(view);
}

void begin_page_drag(neoastra_view_t* view, const std::wstring& event) noexcept {
    auto* state = static_cast<windows_view*>(view->platform);
    if (!state || state->page_drag_active || state->page_drag_timer) return;
    try {
        std::wstring data;
        if (!json_object_member(event, L"data", data)) return;
        state->page_drag_data = narrow(data.c_str());
        page_drags.reserve(page_drags.size() + 1);
        state->page_drag_timer = SetTimer(nullptr, 0, USER_TIMER_MINIMUM, page_drag_tick);
        if (state->page_drag_timer) page_drags.emplace_back(state->page_drag_timer, view);
    } catch (...) { }
}

void end_page_drag(windows_view* state) noexcept {
    if (state->page_drag_timer) {
        KillTimer(nullptr, state->page_drag_timer);
        std::erase_if(page_drags, [timer = state->page_drag_timer](const auto& value) { return value.first == timer; });
        state->page_drag_timer = 0;
    }
    state->page_drag_active = false;
}

// Drags that start in the page are reported to NeoAstra instead of starting WebView2's own drag loop, whose drop
// could not reach the page (see view_drop_target).
void intercept_page_drags(neoastra_view_t* view, windows_view* state) noexcept {
    if (!state || !state->core) return;
    if (!state->drag_intercepted) {
        ComPtr<ICoreWebView2DevToolsProtocolEventReceiver> receiver;
        if (SUCCEEDED(state->core->GetDevToolsProtocolEventReceiver(L"Input.dragIntercepted", &receiver)) &&
            SUCCEEDED(receiver->add_DevToolsProtocolEventReceived(Callback<ICoreWebView2DevToolsProtocolEventReceivedEventHandler>(
                [view](ICoreWebView2*, ICoreWebView2DevToolsProtocolEventReceivedEventArgs* args) -> HRESULT {
                    neo_retain_guard owner(view);
                    if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
                    LPWSTR json{};
                    if (FAILED(args->get_ParameterObjectAsJson(&json)) || !json) return S_OK;
                    try { begin_page_drag(view, json); } catch (...) { }
                    CoTaskMemFree(json);
                    return S_OK;
                }).Get(), &state->drag_intercepted_token))) state->drag_intercepted = receiver;
    }
    (void)state->core->CallDevToolsProtocolMethod(L"Input.setInterceptDrags", L"{\"enabled\":true}",
        Callback<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>([](HRESULT, LPCWSTR) -> HRESULT { return S_OK; }).Get());
}

} // namespace

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
            static_cast<view_drop_target*>(existing->target.Get())->detach_view();
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
        const auto removed = !IsWindow(registration.window) || GetPropW(registration.window, L"NeoAstra.DropTarget") != registration.target.Get();
        if (removed) static_cast<view_drop_target*>(registration.target.Get())->detach_view();
        return removed;
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
            neo_retain_guard owner(view);
            if (!owner || view->ui_destroyed.load(std::memory_order_acquire)) return S_OK;
            auto* state = static_cast<windows_view*>(view->platform);
            COREWEBVIEW2_KEY_EVENT_KIND kind{};
            COREWEBVIEW2_PHYSICAL_KEY_STATUS status{};
            UINT key{};
            if (!state || !state->core || FAILED(args->get_KeyEventKind(&kind)) || kind != COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN ||
                FAILED(args->get_VirtualKey(&key)) || FAILED(args->get_PhysicalKeyStatus(&status)) || status.WasKeyDown) return S_OK;
            const auto core = state->core;
            const auto control = GetKeyState(VK_CONTROL) < 0, shift = GetKeyState(VK_SHIFT) < 0, alt = GetKeyState(VK_MENU) < 0;
            if (!(key == VK_F12 && !control && !shift && !alt) && !(key == 'I' && control && shift && !alt)) return S_OK;
            ComPtr<ICoreWebView2Settings> settings;
            ComPtr<ICoreWebView2Settings3> settings3;
            BOOL accelerators = TRUE, devtools = FALSE;
            // While browser accelerators are on, the engine opens DevTools itself.
            if (FAILED(core->get_Settings(&settings)) || FAILED(settings.As(&settings3)) ||
                FAILED(settings3->get_AreBrowserAcceleratorKeysEnabled(&accelerators)) || accelerators) return S_OK;
            if (SUCCEEDED(settings->get_AreDevToolsEnabled(&devtools)) && devtools && SUCCEEDED(core->OpenDevToolsWindow())) (void)args->put_Handled(TRUE);
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
            neo_retain_guard owner(view);
            if (!owner) { if (controller) controller->Close(); callback(context, make_error(NEOASTRA_ERROR_DISPOSED, "WebView2 view was released before creation completed", E_ABORT)); return S_OK; }
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
            // Bounds and visibility changes can reach the host synchronously; an explicit detach there frees `state`.
            if (view->platform != state) { callback(context, make_error(NEOASTRA_ERROR_DISPOSED, "Application shutdown interrupted WebView2 view creation", E_ABORT)); return S_OK; }
            ComPtr<ICoreWebView2Controller4> controller4;
            if (SUCCEEDED(result)) result=state->controller.As(&controller4);
            if (SUCCEEDED(result)) result=controller4->put_AllowExternalDrop(FALSE);
            if (SUCCEEDED(result)) apply_view_background(view);
            if (SUCCEEDED(result)) register_view_drop_targets(view, state);
            // Drop-target diagnostics invoke host logging; an explicit detach can
            // destroy platform state independently of the retained view object.
            if (view->platform != state) { callback(context, make_error(NEOASTRA_ERROR_DISPOSED, "Application shutdown interrupted WebView2 view creation", E_ABORT)); return S_OK; }
            if (SUCCEEDED(result) && view->window) {
                // Lets `app-region: drag` content move, maximize, and open the system menu of its host window like a native caption.
                ComPtr<ICoreWebView2Settings> settings;
                ComPtr<ICoreWebView2Settings9> settings9;
                if (SUCCEEDED(state->core->get_Settings(&settings)) && SUCCEEDED(settings.As(&settings9))) (void)settings9->put_IsNonClientRegionSupportEnabled(TRUE);
                // The new controller window is created above the caption overlays.
                layout_title_bar(view->window);
            }
            if (SUCCEEDED(result)) result = register_view_events(view, state);
            if (SUCCEEDED(result)) intercept_page_drags(view, state);
            if (SUCCEEDED(result)) result = register_devtools_shortcut(view, state);
            // A shown window that holds the keyboard has nothing else to type into: its new browser takes it, as on
            // activation. A hidden window keeps it, so that its page is not told it has the focus.
            if (SUCCEEDED(result) && view->window && GetFocus() == view_parent(view) && IsWindowVisible(view_parent(view)))
                (void)controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
            if (view->platform != state) { callback(context, make_error(NEOASTRA_ERROR_DISPOSED, "Application shutdown interrupted WebView2 view creation", E_ABORT)); return S_OK; }
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
void neo_platform_view_destroy(neoastra_view_t* view) noexcept { auto* state=static_cast<windows_view*>(view->platform);if(!state)return;end_page_drag(state);remove_view_events(state);for(auto& registration:state->drop_registrations){static_cast<view_drop_target*>(registration.target.Get())->detach_view();if(GetPropW(registration.window,L"NeoAstra.DropTarget")==registration.target.Get())RemovePropW(registration.window,L"NeoAstra.DropTarget");(void)RevokeDragDrop(registration.window);}state->drop_registrations.clear();if(state->controller)state->controller->Close();delete state;view->platform=nullptr; }
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t* view) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->controller)return NEOASTRA_ERROR_NOT_INITIALIZED;return SUCCEEDED(state->controller->put_Bounds(view_bounds(view)))?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}
neoastra_result_t neo_platform_view_navigate(neoastra_view_t* view,const std::string& uri,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(uri);const auto result=state->core->Navigate(value.c_str());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 navigation failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t* view,const std::string& uri,const std::string& method,const std::string& headers,const uint8_t* body,uint64_t body_length,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);auto* environment=static_cast<windows_environment*>(view->environment->platform);if(!state||!state->core||!environment||!environment->value)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");if(method.empty()||body_length>ULONG_MAX)return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,"Invalid WebView2 request method or body length");ComPtr<IStream> content;if(body_length){HRESULT result=CreateStreamOnHGlobal(nullptr,TRUE,&content);if(FAILED(result))return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Could not allocate WebView2 request body",result,"webview2");ULONG written{};result=content->Write(body,static_cast<ULONG>(body_length),&written);LARGE_INTEGER start{};if(SUCCEEDED(result)&&written==body_length)result=content->Seek(start,STREAM_SEEK_SET,nullptr);if(FAILED(result)||written!=body_length)return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"Could not write WebView2 request body",FAILED(result)?result:E_FAIL,"webview2");}ComPtr<ICoreWebView2Environment2> environment2;ComPtr<ICoreWebView2_2> core2;auto result=environment->value.As(&environment2);if(SUCCEEDED(result))result=state->core.As(&core2);if(FAILED(result))return neo_fail(error,NEOASTRA_ERROR_NOT_SUPPORTED,"This WebView2 runtime does not support request navigation",result,"webview2");ComPtr<ICoreWebView2WebResourceRequest> request;result=environment2->CreateWebResourceRequest(widen(uri).c_str(),widen(method).c_str(),content.Get(),widen(headers).c_str(),&request);if(SUCCEEDED(result))result=core2->NavigateWithWebResourceRequest(request.Get());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 request navigation failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_load_html(neoastra_view_t* view,const std::string& html,const std::string&,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(html);const auto result=state->core->NavigateToString(value.c_str());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 HTML loading failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_command(neoastra_view_t* view,uint32_t command) noexcept {auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return NEOASTRA_ERROR_NOT_INITIALIZED;HRESULT result=E_INVALIDARG;switch(command){case 0:result=state->core->Stop();break;case 1:result=state->core->Reload();break;case 2:
    // WebView2 has no reload that leaves its cache out; the DevTools protocol has one.
    result=state->core->CallDevToolsProtocolMethod(L"Page.reload",L"{\"ignoreCache\":true}",Callback<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>([](HRESULT,LPCWSTR)->HRESULT{return S_OK;}).Get());
    if(FAILED(result))result=state->core->Reload();
    break;case 3:result=state->core->GoBack();break;case 4:result=state->core->GoForward();break;}return SUCCEEDED(result)?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t* view, const std::string& script, neoastra_string_callback_t callback,
                                           void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept {
    try {
        auto* state = static_cast<windows_view*>(view->platform);
        if (!state || !state->core) return neo_fail(error, NEOASTRA_ERROR_NOT_INITIALIZED, "WebView2 view is not initialized");
        if (view->environment->app->stopping.load(std::memory_order_acquire)) return neo_fail(error, NEOASTRA_ERROR_DISPOSED, "Application shutdown has begun");
        const auto value = widen(script);
        auto source = std::make_shared<neo_script_source>(view->environment->app, callback, context, operation);
        const auto result = state->core->ExecuteScript(value.c_str(),
            Callback<ICoreWebView2ExecuteScriptCompletedHandler>([source](HRESULT result, LPCWSTR value) -> HRESULT {
                auto* completion = source->completion;
                try {
                    completion->result = narrow(value);
                    completion->requested = SUCCEEDED(result) ? NEOASTRA_OK : NEOASTRA_ERROR_NATIVE_FAILURE;
                    if (FAILED(result)) completion->error = make_error(NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 script evaluation failed", result);
                } catch (...) { completion->requested = NEOASTRA_ERROR_NATIVE_FAILURE; }
                completion->submit();
                return S_OK;
            }).Get());
        source->accepted = SUCCEEDED(result);
        return SUCCEEDED(result) ? NEOASTRA_OK : neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 script evaluation could not be started", result, "webview2");
    } catch (const std::exception& ex) { return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, ex.what()); }
}
neoastra_result_t neo_platform_view_add_script(neoastra_view_t* view, const std::string& script, const neoastra_script_options_t* options,
                                             neoastra_string_callback_t callback, void* context, neoastra_operation_t* operation,
                                             neoastra_error_t** error) noexcept {
    try {
        auto* state = static_cast<windows_view*>(view->platform);
        if (!state || !state->core) return neo_fail(error, NEOASTRA_ERROR_NOT_INITIALIZED, "WebView2 view is not initialized");
        if (view->environment->app->stopping.load(std::memory_order_acquire)) return neo_fail(error, NEOASTRA_ERROR_DISPOSED, "Application shutdown has begun");
        if (options->injection_time != NEOASTRA_SCRIPT_DOCUMENT_START || options->main_frame_only || options->isolated_world)
            return neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, "WebView2 supports document-start scripts in the default world for all frames");
        const auto value = widen(script);
        auto source = std::make_shared<neo_script_source>(view->environment->app, callback, context, operation);
        const auto result = state->core->AddScriptToExecuteOnDocumentCreated(value.c_str(),
            Callback<ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler>([source](HRESULT result, LPCWSTR identifier) -> HRESULT {
                auto* completion = source->completion;
                try {
                    completion->result = narrow(identifier);
                    completion->requested = SUCCEEDED(result) ? NEOASTRA_OK : NEOASTRA_ERROR_NATIVE_FAILURE;
                    if (FAILED(result)) completion->error = make_error(NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 persistent script registration failed", result);
                } catch (...) { completion->requested = NEOASTRA_ERROR_NATIVE_FAILURE; }
                completion->submit();
                return S_OK;
            }).Get());
        source->accepted = SUCCEEDED(result);
        return SUCCEEDED(result) ? NEOASTRA_OK : neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "WebView2 persistent script registration could not be started", result, "webview2");
    } catch (const std::exception& ex) { return neo_fail(error, NEOASTRA_ERROR_INVALID_ARGUMENT, ex.what()); }
}
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t* view,const std::string& identifier) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return NEOASTRA_ERROR_NOT_INITIALIZED;const auto value=widen(identifier);return SUCCEEDED(state->core->RemoveScriptToExecuteOnDocumentCreated(value.c_str()))?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;}catch(...){return NEOASTRA_ERROR_INVALID_ARGUMENT;}}
neoastra_result_t neo_platform_view_post_message(neoastra_view_t* view,const std::string& message,bool json,neoastra_error_t** error) noexcept {try{auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");const auto value=widen(message);const auto result=json?state->core->PostWebMessageAsJson(value.c_str()):state->core->PostWebMessageAsString(value.c_str());return SUCCEEDED(result)?NEOASTRA_OK:neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 message posting failed",result,"webview2");}catch(const std::exception& ex){return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what());}}
neoastra_result_t neo_platform_view_post_messages(neoastra_view_t* view,const neoastra_string_view_t* messages,uint32_t count,bool json,uint32_t* posted,neoastra_error_t** error) noexcept {
    try {
        auto* state=static_cast<windows_view*>(view->platform);
        if(!state||!state->core)return neo_fail(error,NEOASTRA_ERROR_NOT_INITIALIZED,"WebView2 view is not initialized");
        // WebView2 copies each message when it is posted: one buffer serves them all.
        std::wstring value;
        for(uint32_t index=0;index<count;index++){
            widen_into(value,messages[index]);
            const auto result=json?state->core->PostWebMessageAsJson(value.c_str()):state->core->PostWebMessageAsString(value.c_str());
            if(FAILED(result))return neo_fail(error,NEOASTRA_ERROR_NATIVE_FAILURE,"WebView2 message posting failed",result,"webview2");
            if(posted)*posted=index+1;
        }
        return NEOASTRA_OK;
    } catch(const std::exception& ex) { return neo_fail(error,NEOASTRA_ERROR_INVALID_ARGUMENT,ex.what()); }
}
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
        // The requests that walk the history are refused as they come, whatever asks for them. The swipe is switched as well, so
        // that it does not draw the page it would go to; a runtime without that setting only refuses the request of the swipe.
        case NEOASTRA_VIEW_SETTING_HISTORY_NAVIGATION:{ComPtr<ICoreWebView2Settings6> settings6;if(SUCCEEDED(settings.As(&settings6)))(void)settings6->put_IsSwipeNavigationEnabled(value);report_history(view,state->core.Get());return NEOASTRA_OK;}
        case NEOASTRA_VIEW_SETTING_ZOOM_CONTROLS:{
            // Pinch zoom is a separate setting on newer runtimes; older ones only have the wheel and keyboard control.
            result=settings->put_IsZoomControlEnabled(value);ComPtr<ICoreWebView2Settings5> settings5;if(SUCCEEDED(result)&&SUCCEEDED(settings.As(&settings5)))result=settings5->put_IsPinchZoomEnabled(value);break;}
        default:return NEOASTRA_ERROR_INVALID_ARGUMENT;
    }
    return SUCCEEDED(result)?NEOASTRA_OK:result==E_NOINTERFACE?NEOASTRA_ERROR_NOT_SUPPORTED:NEOASTRA_ERROR_NATIVE_FAILURE;
}
// ShellExecute reports success with a value above 32, and its error with a smaller one.
bool neo_platform_open_external(const std::string& uri,int64_t* native_code) noexcept {try{const auto result=reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr,L"open",widen(uri).c_str(),nullptr,nullptr,SW_SHOWNORMAL));if(result>32)return true;*native_code=static_cast<int64_t>(result);return false;}catch(...){return false;}}
// The DevTools protocol moves the pointer of the page itself: the system cursor stays where it is.
neoastra_result_t neo_platform_view_move_pointer(neoastra_view_t* view,double x,double y) noexcept {
    try {
        auto* state=static_cast<windows_view*>(view->platform);if(!state||!state->core)return NEOASTRA_ERROR_NOT_INITIALIZED;
        std::wstring parameters=L"{\"type\":\"mouseMoved\",\"x\":";append_protocol_number(parameters,x);
        parameters+=L",\"y\":";append_protocol_number(parameters,y);parameters+=L",\"button\":\"none\",\"pointerType\":\"mouse\"}";
        return SUCCEEDED(state->core->CallDevToolsProtocolMethod(L"Input.dispatchMouseEvent",parameters.c_str(),
            Callback<ICoreWebView2CallDevToolsProtocolMethodCompletedHandler>([](HRESULT,LPCWSTR)->HRESULT{return S_OK;}).Get()))?NEOASTRA_OK:NEOASTRA_ERROR_NATIVE_FAILURE;
    } catch(...) { return NEOASTRA_ERROR_NATIVE_FAILURE; }
}
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
                    // The protocol counts a clip in the units of the view at a zoom of 100 percent, of which a CSS pixel of a
                    // zoomed page takes as many as the zoom says: a clip in CSS pixels shows another part of such a page.
                    double zoom{};
                    if (!protocol_number(json, L"cssVisualViewport", L"zoom", zoom) || !(zoom > 0.0)) zoom = 1.0;
                    x *= zoom; y *= zoom; width *= zoom; height *= zoom;
                    if (state->options.full_page) {
                        width = std::min(width, maximum_capture_dimension);
                        height = std::min(height, maximum_capture_dimension);
                    }
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
