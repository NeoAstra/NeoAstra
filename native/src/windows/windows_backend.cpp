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
constexpr wchar_t dispatch_class[] = L"NeoWebView.Dispatcher";
constexpr wchar_t window_class[] = L"NeoWebView.Window";

struct windows_app { HWND dispatcher{}; bool owns_com{}; std::vector<neo_webview_decision_t*> decision_timers; };
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
    bool events_registered{};
};

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

std::string take_string(LPWSTR value) {
    const auto result = narrow(value);
    CoTaskMemFree(value);
    return result;
}

neo_webview_resource_kind_t portable_resource_kind(COREWEBVIEW2_WEB_RESOURCE_CONTEXT kind) noexcept {
    switch (kind) {
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT: return NEO_WEBVIEW_RESOURCE_DOCUMENT;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_STYLESHEET: return NEO_WEBVIEW_RESOURCE_STYLESHEET;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_IMAGE: return NEO_WEBVIEW_RESOURCE_IMAGE;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_MEDIA: return NEO_WEBVIEW_RESOURCE_MEDIA;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_FONT: return NEO_WEBVIEW_RESOURCE_FONT;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_SCRIPT: return NEO_WEBVIEW_RESOURCE_SCRIPT;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_XML_HTTP_REQUEST: return NEO_WEBVIEW_RESOURCE_XML_HTTP_REQUEST;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_FETCH: return NEO_WEBVIEW_RESOURCE_FETCH;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_TEXT_TRACK: return NEO_WEBVIEW_RESOURCE_TEXT_TRACK;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_EVENT_SOURCE: return NEO_WEBVIEW_RESOURCE_EVENT_SOURCE;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_WEBSOCKET: return NEO_WEBVIEW_RESOURCE_WEBSOCKET;
        case COREWEBVIEW2_WEB_RESOURCE_CONTEXT_MANIFEST: return NEO_WEBVIEW_RESOURCE_MANIFEST;
        default: return NEO_WEBVIEW_RESOURCE_OTHER;
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

const neo_custom_scheme_registration* find_scheme(const neo_webview_environment_t* environment, std::string_view uri) noexcept {
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

HRESULT create_resource_response(neo_webview_view_t* view, const neo_webview_resource_response_t& response,
                                 ICoreWebView2WebResourceResponse** output) noexcept {
    try {
        if (!neo_valid_resource_response(response)) return E_INVALIDARG;
        ComPtr<IStream> content;
        HRESULT result = S_OK;
        if (response.body_kind == NEO_WEBVIEW_RESOURCE_BODY_BYTES) {
            if (response.byte_length > ULONG_MAX) return E_INVALIDARG;
            result = CreateStreamOnHGlobal(nullptr, TRUE, &content);
            if (SUCCEEDED(result) && response.byte_length) {
                ULONG written{};
                result = content->Write(response.bytes, static_cast<ULONG>(response.byte_length), &written);
                if (SUCCEEDED(result) && written != response.byte_length) result = E_FAIL;
                LARGE_INTEGER start{};
                if (SUCCEEDED(result)) result = content->Seek(start, STREAM_SEEK_SET, nullptr);
            }
        } else if (response.body_kind == NEO_WEBVIEW_RESOURCE_BODY_FILE) {
            result = SHCreateStreamOnFileEx(widen(neo_string(response.file_path)).c_str(), STGM_READ | STGM_SHARE_DENY_WRITE,
                                            FILE_ATTRIBUTE_NORMAL, FALSE, nullptr, &content);
        }
        if (FAILED(result)) return result;
        auto headers = neo_string(response.headers);
        const auto mime = neo_string(response.mime_type);
        if (!mime.empty() && !contains_header(headers, "content-type")) headers += "Content-Type: " + mime + "\r\n";
        if (response.body_kind != NEO_WEBVIEW_RESOURCE_BODY_EMPTY && response.content_length != UINT64_MAX && !contains_header(headers, "content-length")) {
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

neo_webview_result_t windows_download_command(neo_webview_download_t* download, uint32_t command) noexcept {
    auto* state=static_cast<windows_download*>(download->platform);
    if(!state||!state->operation)return NEO_WEBVIEW_ERROR_DISPOSED;
    const auto current=download->state.load(std::memory_order_acquire);
    if(current==NEO_WEBVIEW_DOWNLOAD_COMPLETED||current==NEO_WEBVIEW_DOWNLOAD_CANCELED||current==NEO_WEBVIEW_DOWNLOAD_FAILED)return NEO_WEBVIEW_ERROR_INVALID_STATE;
    HRESULT result=E_INVALIDARG;
    if(command==0)result=state->operation->Cancel();else if(command==1)result=state->operation->Pause();else if(command==2)result=state->operation->Resume();
    return SUCCEEDED(result)?NEO_WEBVIEW_OK:NEO_WEBVIEW_ERROR_NATIVE_FAILURE;
}

void destroy_windows_download(neo_webview_download_t* download) noexcept {
    auto* state=static_cast<windows_download*>(download->platform);if(!state)return;
    if(state->operation){state->operation->remove_BytesReceivedChanged(state->bytes_changed);state->operation->remove_StateChanged(state->state_changed);}
    delete state;
}

bool register_download_events(neo_webview_download_t* download) noexcept {
    try {
        auto* state=static_cast<windows_download*>(download->platform);
        auto result=state->operation->add_BytesReceivedChanged(Callback<ICoreWebView2BytesReceivedChangedEventHandler>([download](ICoreWebView2DownloadOperation* operation,IUnknown*)->HRESULT{
            INT64 received{},total{-1};operation->get_BytesReceived(&received);operation->get_TotalBytesToReceive(&total);
            download->bytes_received.store(received<0?0:static_cast<uint64_t>(received),std::memory_order_release);download->total_bytes.store(total<0?UINT64_MAX:static_cast<uint64_t>(total),std::memory_order_release);
            neo_download_emit(download,NEO_WEBVIEW_EVENT_DOWNLOAD_PROGRESS_CHANGED);return S_OK;
        }).Get(),&state->bytes_changed);
        if(FAILED(result))return false;
        result=state->operation->add_StateChanged(Callback<ICoreWebView2StateChangedEventHandler>([download](ICoreWebView2DownloadOperation* operation,IUnknown*)->HRESULT{
            COREWEBVIEW2_DOWNLOAD_STATE native{};if(FAILED(operation->get_State(&native))||native==COREWEBVIEW2_DOWNLOAD_STATE_IN_PROGRESS)return S_OK;
            COREWEBVIEW2_DOWNLOAD_INTERRUPT_REASON reason{};
            if(native==COREWEBVIEW2_DOWNLOAD_STATE_INTERRUPTED)operation->get_InterruptReason(&reason);
            const auto terminal=native==COREWEBVIEW2_DOWNLOAD_STATE_COMPLETED?NEO_WEBVIEW_DOWNLOAD_COMPLETED:
                reason==COREWEBVIEW2_DOWNLOAD_INTERRUPT_REASON_USER_CANCELED?NEO_WEBVIEW_DOWNLOAD_CANCELED:NEO_WEBVIEW_DOWNLOAD_FAILED;
            auto expected=NEO_WEBVIEW_DOWNLOAD_IN_PROGRESS;if(!download->state.compare_exchange_strong(expected,terminal,std::memory_order_acq_rel))return S_OK;
            if(terminal==NEO_WEBVIEW_DOWNLOAD_FAILED)download->failure_reason="WebView2 interrupt reason "+std::to_string(static_cast<uint32_t>(reason));
            neo_download_emit(download,NEO_WEBVIEW_EVENT_DOWNLOAD_COMPLETED);download->release_lifecycle();return S_OK;
        }).Get(),&state->state_changed);
        if(SUCCEEDED(result))return true;
        state->operation->remove_BytesReceivedChanged(state->bytes_changed);
    } catch (...) { }
    return false;
}

struct download_decision_context { ComPtr<ICoreWebView2DownloadStartingEventArgs> args; ComPtr<ICoreWebView2Deferral> deferral; neo_webview_download_t* download{}; };
void download_decided(void* pointer,const neo_webview_decision_response_t* response) noexcept {
    std::unique_ptr<download_decision_context> context(static_cast<download_decision_context*>(pointer));auto* download=context->download;
    if(response->action==NEO_WEBVIEW_DECISION_DOWNLOAD&&!response->text.length)response=nullptr;
    const auto cancel=[&](bool handled){context->args->put_Cancel(TRUE);if(handled)context->args->put_Handled(TRUE);download->state.store(NEO_WEBVIEW_DOWNLOAD_CANCELED);neo_download_emit(download,NEO_WEBVIEW_EVENT_DOWNLOAD_COMPLETED);download->release_lifecycle();};
    if(!response||response->action==NEO_WEBVIEW_DECISION_CANCEL||response->action==NEO_WEBVIEW_DECISION_DENY){cancel(false);}
    else if(response->action==NEO_WEBVIEW_DECISION_HANDLED_EXTERNAL){cancel(true);}
    else if(response->action==NEO_WEBVIEW_DECISION_DEFAULT||response->action==NEO_WEBVIEW_DECISION_ALLOW||response->action==NEO_WEBVIEW_DECISION_DOWNLOAD){
        if(response->action==NEO_WEBVIEW_DECISION_DOWNLOAD){try{download->destination_path=neo_string(response->text);context->args->put_ResultFilePath(widen(download->destination_path).c_str());context->args->put_Handled(TRUE);}catch(...){cancel(false);context->deferral->Complete();return;}}
        download->state.store(NEO_WEBVIEW_DOWNLOAD_IN_PROGRESS);
        if(register_download_events(download))neo_download_emit(download,NEO_WEBVIEW_EVENT_DOWNLOAD_STARTED);else cancel(false);
    } else cancel(false);
    context->deferral->Complete();
}

struct navigation_decision_context {
    ComPtr<ICoreWebView2NavigationStartingEventArgs> args;
};

struct script_dialog_context { ComPtr<ICoreWebView2ScriptDialogOpeningEventArgs> args; ComPtr<ICoreWebView2Deferral> deferral; };
void script_dialog_decided(void* pointer,const neo_webview_decision_response_t* response) noexcept {
    std::unique_ptr<script_dialog_context> context(static_cast<script_dialog_context*>(pointer));
    if(response->action==NEO_WEBVIEW_DECISION_ALLOW){if(response->text.length){try{context->args->put_ResultText(widen(neo_string(response->text)).c_str());}catch(...){}}context->args->Accept();}
    context->deferral->Complete();
}

struct basic_auth_context { ComPtr<ICoreWebView2BasicAuthenticationRequestedEventArgs> args; ComPtr<ICoreWebView2Deferral> deferral; };
void basic_auth_decided(void* pointer,const neo_webview_decision_response_t* response) noexcept {
    std::unique_ptr<basic_auth_context> context(static_cast<basic_auth_context*>(pointer));
    if(response->action==NEO_WEBVIEW_DECISION_ALLOW){try{ComPtr<ICoreWebView2BasicAuthenticationResponse> credentials;if(SUCCEEDED(context->args->get_Response(&credentials))){credentials->put_UserName(widen(neo_string(response->text)).c_str());credentials->put_Password(widen(neo_string(response->secondary_text)).c_str());}}catch(...){context->args->put_Cancel(TRUE);}}
    else if(response->action==NEO_WEBVIEW_DECISION_CANCEL||response->action==NEO_WEBVIEW_DECISION_DENY)context->args->put_Cancel(TRUE);
    context->deferral->Complete();
}

struct tls_context { ComPtr<ICoreWebView2ServerCertificateErrorDetectedEventArgs> args; ComPtr<ICoreWebView2Deferral> deferral; };
void tls_decided(void* pointer,const neo_webview_decision_response_t* response) noexcept {std::unique_ptr<tls_context> context(static_cast<tls_context*>(pointer));context->args->put_Action(response->action==NEO_WEBVIEW_DECISION_ALLOW?COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_ALWAYS_ALLOW:COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL);context->deferral->Complete();}

struct client_cert_context { ComPtr<ICoreWebView2ClientCertificateRequestedEventArgs> args; ComPtr<ICoreWebView2ClientCertificateCollection> certificates; ComPtr<ICoreWebView2Deferral> deferral; };
void client_cert_decided(void* pointer,const neo_webview_decision_response_t* response) noexcept {std::unique_ptr<client_cert_context> context(static_cast<client_cert_context*>(pointer));
    if(response->action==NEO_WEBVIEW_DECISION_ALLOW){UINT count{};context->certificates->get_Count(&count);if(response->selected_index<count){ComPtr<ICoreWebView2ClientCertificate> selected;context->certificates->GetValueAtIndex(response->selected_index,&selected);context->args->put_SelectedCertificate(selected.Get());context->args->put_Handled(TRUE);}else context->args->put_Cancel(TRUE);}
    else if(response->action==NEO_WEBVIEW_DECISION_CANCEL||response->action==NEO_WEBVIEW_DECISION_DENY){context->args->put_Cancel(TRUE);context->args->put_Handled(TRUE);}context->deferral->Complete();}

struct fullscreen_context { ComPtr<ICoreWebView2> core; };
void fullscreen_decided(void* pointer,const neo_webview_decision_response_t* response) noexcept {
    std::unique_ptr<fullscreen_context> context(static_cast<fullscreen_context*>(pointer));
    if(response->action!=NEO_WEBVIEW_DECISION_ALLOW)context->core->ExecuteScript(L"document.fullscreenElement && document.exitFullscreen()",nullptr);
}

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

struct new_window_decision_context {
    ComPtr<ICoreWebView2NewWindowRequestedEventArgs> args;
    ComPtr<ICoreWebView2Deferral> deferral;
    ComPtr<ICoreWebView2> core;
    std::wstring uri;
};

void new_window_decided(void* pointer, const neo_webview_decision_response_t* response) noexcept {
    std::unique_ptr<new_window_decision_context> context(static_cast<new_window_decision_context*>(pointer));
    context->args->put_Handled(TRUE);
    if (response->action == NEO_WEBVIEW_DECISION_ALLOW && response->target_view) {
        auto* target=static_cast<windows_view*>(response->target_view->platform);
        if(target&&target->core)context->args->put_NewWindow(target->core.Get());
    } else if (response->action == NEO_WEBVIEW_DECISION_ALLOW) {
        context->core->Navigate(context->uri.c_str());
    } else if (response->action == NEO_WEBVIEW_DECISION_OPEN_EXTERNAL) {
        ShellExecuteW(nullptr, L"open", context->uri.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    }
    context->deferral->Complete();
}

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
    const bool cancel = response->action != NEO_WEBVIEW_DECISION_ALLOW && response->action != NEO_WEBVIEW_DECISION_DEFAULT;
    context->args->put_Cancel(cancel ? TRUE : FALSE);
}

uint64_t portable_process_failure(COREWEBVIEW2_PROCESS_FAILED_KIND kind, COREWEBVIEW2_PROCESS_FAILED_REASON reason) noexcept {
    uint64_t value = NEO_WEBVIEW_PROCESS_FAILURE_WEB_PROCESS_EXITED | NEO_WEBVIEW_PROCESS_FAILURE_RECREATE_VIEW;
    if (kind == COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED) {
        value = NEO_WEBVIEW_PROCESS_FAILURE_BROWSER_PROCESS_EXITED | NEO_WEBVIEW_PROCESS_FAILURE_RESTART_APPLICATION;
    } else if (kind == COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_UNRESPONSIVE) {
        value = NEO_WEBVIEW_PROCESS_FAILURE_PROCESS_UNRESPONSIVE | NEO_WEBVIEW_PROCESS_FAILURE_RECREATE_VIEW;
    }
    if (reason == COREWEBVIEW2_PROCESS_FAILED_REASON_UNEXPECTED ||
        reason == COREWEBVIEW2_PROCESS_FAILED_REASON_CRASHED ||
        reason == COREWEBVIEW2_PROCESS_FAILED_REASON_OUT_OF_MEMORY ||
        reason == COREWEBVIEW2_PROCESS_FAILED_REASON_ABNORMAL_EXIT ||
        reason == COREWEBVIEW2_PROCESS_FAILED_REASON_INTEGRITY_FAILURE) {
        value |= NEO_WEBVIEW_PROCESS_FAILURE_CRASHED;
    }
    return value;
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
            neo_configure_decision(decision, view, NEO_WEBVIEW_DECISION_NAVIGATION, NEO_WEBVIEW_DECISION_ALLOW);
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
                response.action = decision->default_action;
                neo_webview_decision_complete(decision, &response, nullptr);
            }
            const auto allowed = decision->resolved_action.load(std::memory_order_acquire) == NEO_WEBVIEW_DECISION_ALLOW;
            decision->release();
            if (allowed) neo_emit_view(view, NEO_WEBVIEW_EVENT_NAVIGATION_STARTED, 0, nullptr, &uri, 1);
            return S_OK;
        }).Get(), &state->navigation_starting);
    if (FAILED(result)) return result;

    result=state->core->add_ScriptDialogOpening(Callback<ICoreWebView2ScriptDialogOpeningEventHandler>([view](ICoreWebView2*,ICoreWebView2ScriptDialogOpeningEventArgs* args)->HRESULT{
        LPWSTR raw_uri{},raw_message{},raw_default{};COREWEBVIEW2_SCRIPT_DIALOG_KIND kind{};ComPtr<ICoreWebView2Deferral> deferral;
        auto hr=args->get_Uri(&raw_uri);if(SUCCEEDED(hr))hr=args->get_Kind(&kind);if(SUCCEEDED(hr))hr=args->get_Message(&raw_message);if(SUCCEEDED(hr))hr=args->get_DefaultText(&raw_default);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);
        if(FAILED(hr)){CoTaskMemFree(raw_uri);CoTaskMemFree(raw_message);CoTaskMemFree(raw_default);return S_OK;}
        try{auto uri=take_string(raw_uri);auto message=take_string(raw_message);auto default_text=take_string(raw_default);auto context=std::make_unique<script_dialog_context>();context->args=args;context->deferral=deferral;
            auto* decision=new neo_webview_decision;const auto portable=kind==COREWEBVIEW2_SCRIPT_DIALOG_KIND_ALERT?NEO_WEBVIEW_SCRIPT_DIALOG_ALERT:kind==COREWEBVIEW2_SCRIPT_DIALOG_KIND_CONFIRM?NEO_WEBVIEW_SCRIPT_DIALOG_CONFIRM:kind==COREWEBVIEW2_SCRIPT_DIALOG_KIND_PROMPT?NEO_WEBVIEW_SCRIPT_DIALOG_PROMPT:NEO_WEBVIEW_SCRIPT_DIALOG_BEFORE_UNLOAD;
            neo_configure_decision(decision,view,NEO_WEBVIEW_DECISION_SCRIPT_DIALOG,portable==NEO_WEBVIEW_SCRIPT_DIALOG_ALERT?NEO_WEBVIEW_DECISION_ALLOW:NEO_WEBVIEW_DECISION_CANCEL);decision->completion=script_dialog_decided;decision->completion_context=context.release();neo_event_details details{};details.text2=&default_text;
            neo_emit_view_detailed(view,NEO_WEBVIEW_EVENT_SCRIPT_DIALOG_REQUESTED,0,&message,&uri,portable,0,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;
        }catch(...){deferral->Complete();return S_OK;}
    }).Get(),&state->script_dialog);
    if(FAILED(result))return result;

    result=state->core->add_ContainsFullScreenElementChanged(Callback<ICoreWebView2ContainsFullScreenElementChangedEventHandler>([view](ICoreWebView2* core,IUnknown*)->HRESULT{
        BOOL entering{};core->get_ContainsFullScreenElement(&entering);if(!entering)return S_OK;auto* decision=new(std::nothrow) neo_webview_decision;if(!decision){core->ExecuteScript(L"document.fullscreenElement && document.exitFullscreen()",nullptr);return S_OK;}
        neo_configure_decision(decision,view,NEO_WEBVIEW_DECISION_FULLSCREEN,NEO_WEBVIEW_DECISION_DENY);decision->completion=fullscreen_decided;decision->completion_context=new(std::nothrow) fullscreen_context{core};if(!decision->completion_context){decision->release();core->ExecuteScript(L"document.fullscreenElement && document.exitFullscreen()",nullptr);return S_OK;}
        neo_emit_view(view,NEO_WEBVIEW_EVENT_FULLSCREEN_REQUESTED,0,nullptr,&view->source,1,0,decision);neo_finish_decision_event(view,decision);decision->release();return S_OK;
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

    for (const auto& scheme : view->environment->custom_schemes) {
        const auto filter = widen(scheme.name + ":*");
        result = state->core->AddWebResourceRequestedFilter(filter.c_str(), COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL);
        if (FAILED(result)) return result;
    }
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
                const auto* scheme = find_scheme(view->environment, uri);
                if (!scheme || !scheme->provider) return S_OK;
                const auto headers = request_headers(native_request.Get());
                if (!neo_resource_request_within_limits(uri, method, headers)) {
                    neo_log(view->environment->app, NEO_WEBVIEW_LOG_ERROR, "resource", "Custom-scheme request metadata exceeded its size limit");
                    return S_OK;
                }
                neo_webview_resource_request_t request{};
                request.size = sizeof(request);
                request.version = 1;
                request.uri = neo_string_view(uri);
                request.method = neo_string_view(method);
                request.headers = neo_string_view(headers);
                request.resource_kind = portable_resource_kind(native_kind);
                request.main_frame = native_kind == COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT ? 1u : 0u;
                neo_webview_resource_response_t response{};
                response.size = sizeof(response);
                response.version = 1;
                neo_webview_result_t provider_result = NEO_WEBVIEW_ERROR_NATIVE_FAILURE;
                try { provider_result = scheme->provider(scheme->provider_context, &request, &response); }
                catch (...) { provider_result = NEO_WEBVIEW_ERROR_NATIVE_FAILURE; }
                if (provider_result != NEO_WEBVIEW_OK) {
                    neo_log(view->environment->app, NEO_WEBVIEW_LOG_ERROR, "resource", "Custom-scheme resource provider failed", provider_result);
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
                    neo_log(view->environment->app, NEO_WEBVIEW_LOG_ERROR, "resource", "Could not create a custom-scheme response", result);
                    return S_OK;
                }
                args->put_Response(native_response.Get());
            } catch (...) {
                CoTaskMemFree(raw_uri);
                CoTaskMemFree(raw_method);
                neo_log(view->environment->app, NEO_WEBVIEW_LOG_ERROR, "resource", "Custom-scheme request handling failed");
            }
            return S_OK;
        }).Get(), &state->web_resource_requested);
    if (FAILED(result)) return result;

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
                    neo_log(view->environment->app, NEO_WEBVIEW_LOG_ERROR, "bridge", "Could not read a WebView2 web message", result);
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
                neo_log(view->environment->app, NEO_WEBVIEW_LOG_ERROR, "bridge", "WebView2 web-message handling failed");
            }
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
                neo_configure_decision(decision, view, NEO_WEBVIEW_DECISION_PERMISSION, NEO_WEBVIEW_DECISION_DENY);
                decision->completion = permission_decided;
                decision->completion_context = context.release();
                neo_emit_view(view, NEO_WEBVIEW_EVENT_PERMISSION_REQUESTED, 0, nullptr, &uri,
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
                auto* decision = new neo_webview_decision;
                neo_configure_decision(decision, view, NEO_WEBVIEW_DECISION_NEW_WINDOW, NEO_WEBVIEW_DECISION_CANCEL);
                decision->completion = new_window_decided;
                decision->completion_context = context.release();
                neo_emit_view(view, NEO_WEBVIEW_EVENT_NEW_WINDOW_REQUESTED, 0, &name, &uri, user_initiated ? 1 : 0, 0, decision);
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
                auto download_guard=std::make_unique<neo_webview_download>(view);auto* download=download_guard.get();auto* platform=new windows_download;download->platform=platform;download->command=windows_download_command;download->platform_destroy=destroy_windows_download;platform->operation=operation;
                LPWSTR raw_uri{},raw_mime{},raw_disposition{};INT64 total{-1};operation->get_Uri(&raw_uri);operation->get_MimeType(&raw_mime);operation->get_ContentDisposition(&raw_disposition);operation->get_TotalBytesToReceive(&total);
                 download->source_uri=take_string(raw_uri);download->total_bytes.store(total<0?UINT64_MAX:static_cast<uint64_t>(total));download->destination_path=take_string(raw_path);download->can_pause=true;raw_path=nullptr;
                auto mime=take_string(raw_mime);auto disposition=take_string(raw_disposition);auto suggested=download->destination_path;const auto slash=suggested.find_last_of("/\\");if(slash!=std::string::npos)suggested.erase(0,slash+1);
                auto context=std::make_unique<download_decision_context>();context->args=args;context->deferral=deferral;context->download=download;
                auto decision_guard=std::make_unique<neo_webview_decision>();auto* decision=decision_guard.get();neo_configure_decision(decision,view,NEO_WEBVIEW_DECISION_DOWNLOAD_REQUEST,NEO_WEBVIEW_DECISION_CANCEL);decision->completion=download_decided;decision->completion_context=context.release();download_guard.release();decision_guard.release();
                neo_event_details details{};details.text2=&mime;details.text3=&disposition;details.value2=1;details.download=download;download->event_published=true;
                neo_emit_view_detailed(view,NEO_WEBVIEW_EVENT_DOWNLOAD_REQUESTED,download->id,&suggested,&download->source_uri,total<0?UINT64_MAX:static_cast<uint64_t>(total),0,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;
            }catch(...){CoTaskMemFree(raw_path);args->put_Cancel(TRUE);deferral->Complete();return S_OK;}
        }).Get(),&state->download_starting);
        if(FAILED(result))return result;
    }

    ComPtr<ICoreWebView2_10> core10;
    if(SUCCEEDED(state->core.As(&core10))){result=core10->add_BasicAuthenticationRequested(Callback<ICoreWebView2BasicAuthenticationRequestedEventHandler>([view](ICoreWebView2*,ICoreWebView2BasicAuthenticationRequestedEventArgs* args)->HRESULT{
        LPWSTR raw_uri{},raw_challenge{};ComPtr<ICoreWebView2Deferral> deferral;auto hr=args->get_Uri(&raw_uri);if(SUCCEEDED(hr))hr=args->get_Challenge(&raw_challenge);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(FAILED(hr)){CoTaskMemFree(raw_uri);CoTaskMemFree(raw_challenge);args->put_Cancel(TRUE);return S_OK;}
        try{auto uri=take_string(raw_uri);auto challenge=take_string(raw_challenge);auto context=std::make_unique<basic_auth_context>();context->args=args;context->deferral=deferral;auto* decision=new neo_webview_decision;neo_configure_decision(decision,view,NEO_WEBVIEW_DECISION_AUTHENTICATION,NEO_WEBVIEW_DECISION_DEFAULT);decision->completion=basic_auth_decided;decision->completion_context=context.release();neo_event_details details{};details.text2=&challenge;
            neo_emit_view_detailed(view,NEO_WEBVIEW_EVENT_AUTHENTICATION_REQUESTED,0,nullptr,&uri,0,0,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;}catch(...){args->put_Cancel(TRUE);deferral->Complete();return S_OK;}
    }).Get(),&state->basic_auth);if(FAILED(result))return result;}

    ComPtr<ICoreWebView2_5> core5;
    if(SUCCEEDED(state->core.As(&core5))){result=core5->add_ClientCertificateRequested(Callback<ICoreWebView2ClientCertificateRequestedEventHandler>([view](ICoreWebView2*,ICoreWebView2ClientCertificateRequestedEventArgs* args)->HRESULT{
        LPWSTR raw_host{};int port{};BOOL proxy{};ComPtr<ICoreWebView2ClientCertificateCollection> certificates;ComPtr<ICoreWebView2Deferral> deferral;auto hr=args->get_Host(&raw_host);if(SUCCEEDED(hr))hr=args->get_Port(&port);if(SUCCEEDED(hr))hr=args->get_IsProxy(&proxy);if(SUCCEEDED(hr))hr=args->get_MutuallyTrustedCertificates(&certificates);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(FAILED(hr)){CoTaskMemFree(raw_host);args->put_Cancel(TRUE);return S_OK;}
        try{auto host=take_string(raw_host);UINT count{};certificates->get_Count(&count);auto context=std::make_unique<client_cert_context>();context->args=args;context->certificates=certificates;context->deferral=deferral;auto* decision=new neo_webview_decision;neo_configure_decision(decision,view,NEO_WEBVIEW_DECISION_CLIENT_CERTIFICATE,NEO_WEBVIEW_DECISION_DEFAULT);decision->completion=client_cert_decided;decision->completion_context=context.release();neo_event_details details{};details.value2=proxy?1u:0u;
            neo_emit_view_detailed(view,NEO_WEBVIEW_EVENT_CLIENT_CERTIFICATE_REQUESTED,0,&host,nullptr,count,port,decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;}catch(...){args->put_Cancel(TRUE);deferral->Complete();return S_OK;}
    }).Get(),&state->client_certificate);if(FAILED(result))return result;}

    ComPtr<ICoreWebView2_14> core14;
    if(SUCCEEDED(state->core.As(&core14))){result=core14->add_ServerCertificateErrorDetected(Callback<ICoreWebView2ServerCertificateErrorDetectedEventHandler>([view](ICoreWebView2*,ICoreWebView2ServerCertificateErrorDetectedEventArgs* args)->HRESULT{
        LPWSTR raw_uri{};COREWEBVIEW2_WEB_ERROR_STATUS status{};ComPtr<ICoreWebView2Certificate> certificate;ComPtr<ICoreWebView2Deferral> deferral;auto hr=args->get_RequestUri(&raw_uri);if(SUCCEEDED(hr))hr=args->get_ErrorStatus(&status);if(SUCCEEDED(hr))hr=args->get_ServerCertificate(&certificate);if(SUCCEEDED(hr))hr=args->GetDeferral(&deferral);if(FAILED(hr)){CoTaskMemFree(raw_uri);args->put_Action(COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL);return S_OK;}
        try{auto uri=take_string(raw_uri);LPWSTR raw_subject{};certificate->get_Subject(&raw_subject);auto subject=take_string(raw_subject);auto context=std::make_unique<tls_context>();context->args=args;context->deferral=deferral;auto* decision=new neo_webview_decision;neo_configure_decision(decision,view,NEO_WEBVIEW_DECISION_CERTIFICATE_ERROR,NEO_WEBVIEW_DECISION_DENY);decision->completion=tls_decided;decision->completion_context=context.release();neo_event_details details{};details.text2=&subject;
            neo_emit_view_detailed(view,NEO_WEBVIEW_EVENT_CERTIFICATE_ERROR,0,nullptr,&uri,0,static_cast<int64_t>(status),decision,details);neo_finish_decision_event(view,decision);decision->release();return S_OK;}catch(...){args->put_Action(COREWEBVIEW2_SERVER_CERTIFICATE_ERROR_ACTION_CANCEL);deferral->Complete();return S_OK;}
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
                neo_emit_view(view, NEO_WEBVIEW_EVENT_WEB_PROCESS_TERMINATED, 0,
                              description.empty() ? nullptr : &description, nullptr, value, exit_code);
            } catch (...) {
                // Never allow allocation or conversion failures to cross the COM callback boundary.
            }
            return S_OK;
        }).Get(), &state->process_failed);
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
    if (message == destroy_app_message && app) { neo_destroy_app_on_ui(app); return 0; }
    if (message == WM_TIMER && app) {
        auto* decision = reinterpret_cast<neo_webview_decision_t*>(wparam);
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
    for (auto* decision : state->decision_timers) { KillTimer(state->dispatcher, reinterpret_cast<UINT_PTR>(decision)); decision->release(); }
    state->decision_timers.clear();
    if (state->dispatcher && IsWindow(state->dispatcher)) DestroyWindow(state->dispatcher);
    if (state->owns_com && app->ui_thread == std::this_thread::get_id()) CoUninitialize();
    delete state; app->platform = nullptr;
}

bool neo_platform_schedule_app_destruction(neo_webview_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); return state&&state->dispatcher&&PostMessageW(state->dispatcher,destroy_app_message,0,0)!=FALSE; }

int32_t neo_platform_run(neo_webview_app_t* app) noexcept {
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return app->exit_code.load();
}
void neo_platform_quit(neo_webview_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); if(state&&state->dispatcher)PostMessageW(state->dispatcher,quit_message,0,0); }
void neo_platform_wake(neo_webview_app_t* app) noexcept { auto* state=static_cast<windows_app*>(app->platform); if(state&&state->dispatcher)PostMessageW(state->dispatcher,dispatch_message,0,0); }
bool neo_platform_schedule_decision_timeout(neo_webview_view_t* view,neo_webview_decision_t* decision) noexcept {auto* state=static_cast<windows_app*>(view->environment->app->platform);if(!state||!state->dispatcher)return false;const auto remaining=std::chrono::duration_cast<std::chrono::milliseconds>(decision->deadline-std::chrono::steady_clock::now()).count();const auto delay=static_cast<UINT>(std::clamp<int64_t>(remaining+1,1,USER_TIMER_MAXIMUM));decision->retain();try{state->decision_timers.push_back(decision);}catch(...){decision->release();return false;}if(!SetTimer(state->dispatcher,reinterpret_cast<UINT_PTR>(decision),delay,nullptr)){state->decision_timers.pop_back();decision->release();return false;}return true;}

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
        std::vector<ComPtr<ICoreWebView2CustomSchemeRegistration>> registrations;
        std::vector<ICoreWebView2CustomSchemeRegistration*> registration_pointers;
        registrations.reserve(environment->custom_schemes.size());
        registration_pointers.reserve(environment->custom_schemes.size());
        for (const auto& scheme : environment->custom_schemes) {
            if ((scheme.flags & NEO_WEBVIEW_CUSTOM_SCHEME_SERVICE_WORKERS) != 0) {
                neo_fail(error, NEO_WEBVIEW_ERROR_NOT_SUPPORTED, "WebView2 custom schemes do not support service workers", E_NOTIMPL, "webview2");
                return false;
            }
            auto registration = Make<CoreWebView2CustomSchemeRegistration>(widen(scheme.name).c_str());
            if (!registration) { neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "Could not allocate a WebView2 custom-scheme registration", E_OUTOFMEMORY, "webview2"); return false; }
            auto configure_result = registration->put_HasAuthorityComponent((scheme.flags & NEO_WEBVIEW_CUSTOM_SCHEME_HAS_AUTHORITY) ? TRUE : FALSE);
            if (SUCCEEDED(configure_result)) configure_result = registration->put_TreatAsSecure((scheme.flags & NEO_WEBVIEW_CUSTOM_SCHEME_SECURE) ? TRUE : FALSE);
            if (SUCCEEDED(configure_result) && (scheme.flags & NEO_WEBVIEW_CUSTOM_SCHEME_CORS_ENABLED) != 0 && !scheme.allowed_origins.empty()) {
                std::vector<std::wstring> origins;
                std::vector<LPCWSTR> origin_pointers;
                origins.reserve(scheme.allowed_origins.size());
                origin_pointers.reserve(scheme.allowed_origins.size());
                for (const auto& origin : scheme.allowed_origins) origins.push_back(widen(origin));
                for (const auto& origin : origins) origin_pointers.push_back(origin.c_str());
                configure_result = registration->SetAllowedOrigins(static_cast<UINT32>(origin_pointers.size()), origin_pointers.data());
            }
            if (FAILED(configure_result)) { neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "Could not configure a WebView2 custom scheme", configure_result, "webview2"); return false; }
            registration_pointers.push_back(registration.Get());
            registrations.push_back(std::move(registration));
        }
        if (!registration_pointers.empty()) {
            const auto configure_result = environment_options->SetCustomSchemeRegistrations(static_cast<UINT32>(registration_pointers.size()), registration_pointers.data());
            if (FAILED(configure_result)) { neo_fail(error, NEO_WEBVIEW_ERROR_NATIVE_FAILURE, "Could not register WebView2 custom schemes", configure_result, "webview2"); return false; }
        }
        const auto result = CreateCoreWebView2EnvironmentWithOptions(
            runtime_path.empty() ? nullptr : runtime_path.c_str(), user_data.empty() ? nullptr : user_data.c_str(), environment_options.Get(),
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([environment, callback, context](HRESULT result, ICoreWebView2Environment* created) -> HRESULT {
                auto* state = static_cast<windows_environment*>(environment->platform);
                if (!state) { callback(context, make_error(NEO_WEBVIEW_ERROR_DISPOSED, "Application shutdown completed before WebView2 environment creation", E_ABORT)); return S_OK; }
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
            if (!state) { if (controller) controller->Close(); callback(context, make_error(NEO_WEBVIEW_ERROR_DISPOSED, "Application shutdown completed before WebView2 view creation", E_ABORT)); return S_OK; }
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
