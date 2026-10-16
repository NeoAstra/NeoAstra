// Android backend: the Android NDK and the Android System WebView.
//
// The Android framework owns the process and the run loop of its main thread, so an Android application attaches to the
// native looper of the thread that it calls from (neoastra_app_attach) instead of running a loop. The backend implements
// the application level and a view: a WebView that the Java class com.neoastra.backend.NeoAstraWebHost creates inside a
// ViewGroup of the application, driven through JNI. Android has no custom schemes, so a custom-scheme origin is served as
// a projected HTTPS origin under the reserved .invalid domain. Windows, popups, downloads, dialogs, permissions, cookies,
// zoom, and capture answer NEOASTRA_ERROR_NOT_SUPPORTED until their implementation follows.
#include "../common/native_internal.hpp"

#include <android/looper.h>
#include <jni.h>
#include <fcntl.h>
#include <sys/timerfd.h>
#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cerrno>
#include <chrono>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <string_view>
#include <vector>

namespace {
struct timeout_context {
    neoastra_decision_t* decision{};
    int descriptor{-1};
};

struct android_app {
    ALooper* looper{};
    int wake_read{-1};
    int wake_write{-1};
    std::atomic<bool> destroy_requested{false};
    std::mutex timers_mutex;
    std::vector<timeout_context*> timers;
};

constexpr const char* android_domain = "android";

neoastra_result_t unsupported(neoastra_error_t** error, const char* what) noexcept {
    return neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, std::string(what) + " is not implemented by the Android backend yet", 0, android_domain);
}

void signal_wake(android_app* state) noexcept {
    const char byte = 1;
    // A full pipe already holds a pending wake-up, so a short write can be ignored.
    while (write(state->wake_write, &byte, 1) < 0 && errno == EINTR) {}
}

// Runs on the looper thread: either the application asked for its destruction, or dispatches are pending.
int drain_wake(int descriptor, int, void* data) noexcept {
    auto* app = static_cast<neoastra_app_t*>(data);
    auto* state = static_cast<android_app*>(app->platform);
    char buffer[64];
    while (read(descriptor, buffer, sizeof buffer) > 0) {}
    if (state && state->destroy_requested.load(std::memory_order_acquire)) {
        neo_destroy_app_on_ui(app);
        return 0;
    }
    neo_drain_dispatch(app);
    return 1;
}

int expire_decision(int descriptor, int, void* data) noexcept {
    auto* app = static_cast<neoastra_app_t*>(data);
    auto* state = static_cast<android_app*>(app->platform);
    uint64_t expirations{};
    if (read(descriptor, &expirations, sizeof expirations) < 0) return 1;
    timeout_context* context{};
    if (state) {
        std::lock_guard lock(state->timers_mutex);
        const auto found = std::find_if(state->timers.begin(), state->timers.end(), [descriptor](const auto* timer) { return timer->descriptor == descriptor; });
        if (found != state->timers.end()) { context = *found; state->timers.erase(found); }
    }
    if (!context) return 0;
    ALooper_removeFd(state->looper, descriptor);
    close(descriptor);
    context->decision->expire();
    context->decision->release();
    delete context;
    return 0;
}
}  // namespace

bool neo_platform_initialize(neoastra_app_t* app, neoastra_error_t** error) noexcept {
    auto* state = new (std::nothrow) android_app{};
    if (!state) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "the Android application state could not be allocated", 0, android_domain); return false; }
    state->looper = ALooper_prepare(0);
    int pipes[2]{-1, -1};
    if (!state->looper || pipe2(pipes, O_NONBLOCK | O_CLOEXEC) != 0) {
        neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "the looper of the calling thread could not be prepared", errno, android_domain);
        delete state;
        return false;
    }
    state->wake_read = pipes[0];
    state->wake_write = pipes[1];
    if (ALooper_addFd(state->looper, state->wake_read, ALOOPER_POLL_CALLBACK, ALOOPER_EVENT_INPUT, drain_wake, app) != 1) {
        neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "the wake descriptor could not be added to the looper", 0, android_domain);
        close(state->wake_read);
        close(state->wake_write);
        delete state;
        return false;
    }
    app->platform = state;
    return true;
}
void neo_platform_shutdown(neoastra_app_t* app) noexcept {
    auto* state = static_cast<android_app*>(app->platform);
    if (!state) return;
    ALooper_removeFd(state->looper, state->wake_read);
    {
        std::lock_guard lock(state->timers_mutex);
        for (auto* timer : state->timers) {
            ALooper_removeFd(state->looper, timer->descriptor);
            close(timer->descriptor);
            timer->decision->release();
            delete timer;
        }
        state->timers.clear();
    }
    close(state->wake_read);
    close(state->wake_write);
    delete state;
    app->platform = nullptr;
}
bool neo_platform_schedule_app_destruction(neoastra_app_t* app) noexcept {
    auto* state = static_cast<android_app*>(app->platform);
    if (!state) return false;
    state->destroy_requested.store(true, std::memory_order_release);
    signal_wake(state);
    return true;
}
// The Android framework owns the run loop of the main thread; an application attaches to it instead.
int32_t neo_platform_run(neoastra_app_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
// An Android application cannot end its own process, so a quit request only leaves the application state.
void neo_platform_quit(neoastra_app_t*) noexcept {}
void neo_platform_wake(neoastra_app_t* app) noexcept {
    if (auto* state = static_cast<android_app*>(app->platform)) signal_wake(state);
}
bool neo_platform_schedule_decision_timeout(neoastra_app_t* app, neoastra_decision_t* decision) noexcept {
    auto* state = static_cast<android_app*>(app->platform);
    if (!state) return false;
    const auto remaining = std::max<int64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(decision->deadline - std::chrono::steady_clock::now()).count(), 1);
    const int descriptor = timerfd_create(CLOCK_MONOTONIC, TFD_NONBLOCK | TFD_CLOEXEC);
    if (descriptor < 0) return false;
    itimerspec spec{};
    spec.it_value.tv_sec = remaining / 1'000'000'000;
    spec.it_value.tv_nsec = remaining % 1'000'000'000;
    auto* context = new (std::nothrow) timeout_context{decision, descriptor};
    if (!context || timerfd_settime(descriptor, 0, &spec, nullptr) != 0) { close(descriptor); delete context; return false; }
    decision->retain();
    std::lock_guard lock(state->timers_mutex);
    if (ALooper_addFd(state->looper, descriptor, ALOOPER_POLL_CALLBACK, ALOOPER_EVENT_INPUT, expire_decision, app) != 1) {
        decision->release();
        close(descriptor);
        delete context;
        return false;
    }
    state->timers.push_back(context);
    return true;
}

bool neo_platform_window_create(neoastra_window_t*, const neoastra_window_options_t*, neoastra_error_t** error) noexcept { unsupported(error, "A window"); return false; }
void neo_platform_window_destroy(neoastra_window_t*) noexcept {}
neoastra_result_t neo_platform_window_show(neoastra_window_t*, bool) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_activate(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_force_close(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_title(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_bounds(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_size_constraints(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_get_frame(neoastra_window_t*, neoastra_rect_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_state(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_attribute(neoastra_window_t*, neoastra_window_attribute_t, bool) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_begin_drag(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_begin_resize(neoastra_window_t*, neoastra_window_resize_edge_t) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_title_bar(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_set_background_color(neoastra_window_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_get_title_bar(neoastra_window_t*, neoastra_title_bar_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_window_get_handle(neoastra_window_t*, neoastra_native_handle_kind_t, neoastra_native_handle_t*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }

namespace {
// The Java side: the class that NeoAstraWebHost.java defines, found once when Java loads the library.
JavaVM* java_vm{};
jclass host_class{};
jclass response_class{};
jclass string_class{};
jmethodID host_is_supported{}, host_create{}, host_set_bounds{}, host_navigate{}, host_load_html{}, host_command{}, host_evaluate{};
jmethodID host_add_script{}, host_remove_script{}, host_post_message{}, host_web_view{}, host_destroy{}, response_new{};
jclass services_class{};
jmethodID services_call{};

// A custom-scheme origin such as app://neoastra is served to the WebView as https://neoastra.app.invalid: Android has no
// custom schemes, and the reserved top-level domain can never reach the network. The managed side only ever sees the
// custom-scheme form.
constexpr std::string_view projected_suffix = ".invalid";

struct android_environment {};
struct android_profile {};
struct android_view {
    jobject host{};
    jobject web_view{};
    uint64_t history{};
};

// Resource requests arrive on a thread of the WebView; a view that is being destroyed must no longer be reached from there.
std::mutex live_mutex;
std::vector<neoastra_view_t*> live_views;

bool is_live(neoastra_view_t* view) noexcept { return std::find(live_views.begin(), live_views.end(), view) != live_views.end(); }

JNIEnv* jni() noexcept {
    if (!java_vm) return nullptr;
    JNIEnv* env{};
    if (java_vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6) == JNI_OK) return env;
    return java_vm->AttachCurrentThread(&env, nullptr) == JNI_OK ? env : nullptr;
}

bool failed(JNIEnv* env, neoastra_app_t* app, const char* what) noexcept {
    if (!env->ExceptionCheck()) return false;
    env->ExceptionDescribe();
    env->ExceptionClear();
    if (app) neo_log(app, NEOASTRA_LOG_ERROR, "android", what);
    return true;
}

// JNI's own UTF-8 is modified UTF-8, which encodes a character outside the basic plane as two surrogates; the strings cross
// as UTF-16 instead so that both sides see the same characters.
std::string to_utf8(JNIEnv* env, jstring value) {
    if (!value) return {};
    const jsize length = env->GetStringLength(value);
    const jchar* chars = env->GetStringChars(value, nullptr);
    std::string result;
    result.reserve(static_cast<size_t>(length));
    for (jsize index = 0; index < length; ++index) {
        uint32_t code = chars[index];
        if (code >= 0xD800 && code <= 0xDBFF && index + 1 < length && chars[index + 1] >= 0xDC00 && chars[index + 1] <= 0xDFFF) {
            code = 0x10000 + ((code - 0xD800) << 10) + (chars[++index] - 0xDC00);
        } else if (code >= 0xD800 && code <= 0xDFFF) code = 0xFFFD;
        if (code < 0x80) result.push_back(static_cast<char>(code));
        else if (code < 0x800) { result.push_back(static_cast<char>(0xC0 | (code >> 6))); result.push_back(static_cast<char>(0x80 | (code & 0x3F))); }
        else if (code < 0x10000) { result.push_back(static_cast<char>(0xE0 | (code >> 12))); result.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F))); result.push_back(static_cast<char>(0x80 | (code & 0x3F))); }
        else { result.push_back(static_cast<char>(0xF0 | (code >> 18))); result.push_back(static_cast<char>(0x80 | ((code >> 12) & 0x3F))); result.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F))); result.push_back(static_cast<char>(0x80 | (code & 0x3F))); }
    }
    env->ReleaseStringChars(value, chars);
    return result;
}

jstring to_java(JNIEnv* env, std::string_view value) {
    std::vector<jchar> chars;
    chars.reserve(value.size());
    size_t index = 0;
    while (index < value.size()) {
        const auto lead = static_cast<unsigned char>(value[index]);
        size_t extra = lead < 0x80 ? 0 : (lead & 0xE0) == 0xC0 ? 1 : (lead & 0xF0) == 0xE0 ? 2 : (lead & 0xF8) == 0xF0 ? 3 : 4;
        uint32_t code = 0xFFFD;
        if (extra == 0) code = lead;
        else if (extra < 4 && index + extra < value.size()) {
            code = lead & (0x3F >> extra);
            for (size_t offset = 1; offset <= extra; ++offset) {
                const auto next = static_cast<unsigned char>(value[index + offset]);
                if ((next & 0xC0) != 0x80) { code = 0xFFFD; extra = offset - 1; break; }
                code = (code << 6) | (next & 0x3F);
            }
        } else extra = 0;
        index += extra + 1;
        if (code >= 0x10000) {
            code -= 0x10000;
            chars.push_back(static_cast<jchar>(0xD800 + (code >> 10)));
            chars.push_back(static_cast<jchar>(0xDC00 + (code & 0x3FF)));
        } else chars.push_back(static_cast<jchar>(code));
    }
    return env->NewString(chars.data(), static_cast<jsize>(chars.size()));
}

bool equals_ignoring_case(std::string_view left, std::string_view right) noexcept {
    return left.size() == right.size() && std::equal(left.begin(), left.end(), right.begin(), [](unsigned char a, unsigned char b) { return std::tolower(a) == std::tolower(b); });
}

const neo_custom_scheme_registration* find_scheme(const neoastra_environment_t* environment, std::string_view name) noexcept {
    for (const auto& scheme : environment->custom_schemes) if (equals_ignoring_case(scheme.name, name)) return &scheme;
    return nullptr;
}

// app://neoastra/index.html -> https://neoastra.app.invalid/index.html; every other URI is left as it is.
std::string project(const neoastra_environment_t* environment, std::string_view uri) {
    const auto separator = uri.find("://");
    if (separator == std::string_view::npos || !find_scheme(environment, uri.substr(0, separator))) return std::string(uri);
    const auto rest = uri.substr(separator + 3);
    const auto end = rest.find_first_of("/?#");
    const auto authority = rest.substr(0, end);
    std::string result = "https://";
    result.append(authority).append(".").append(uri.substr(0, separator)).append(projected_suffix);
    if (end != std::string_view::npos) result.append(rest.substr(end));
    return result;
}

// https://neoastra.app.invalid/index.html -> app://neoastra/index.html, for a registered scheme only.
std::string unproject(const neoastra_environment_t* environment, std::string_view uri) {
    constexpr std::string_view https = "https://";
    if (uri.size() <= https.size() || !equals_ignoring_case(uri.substr(0, https.size()), https)) return std::string(uri);
    const auto rest = uri.substr(https.size());
    const auto end = rest.find_first_of("/?#");
    const auto host = rest.substr(0, end);
    if (host.size() <= projected_suffix.size() || !equals_ignoring_case(host.substr(host.size() - projected_suffix.size()), projected_suffix)) return std::string(uri);
    const auto labels = host.substr(0, host.size() - projected_suffix.size());
    const auto dot = labels.rfind('.');
    if (dot == std::string_view::npos || dot == 0) return std::string(uri);
    const auto* scheme = find_scheme(environment, labels.substr(dot + 1));
    if (!scheme) return std::string(uri);
    std::string result = scheme->name;
    result.append("://").append(labels.substr(0, dot));
    result.append(end == std::string_view::npos ? std::string_view("/") : rest.substr(end));
    return result;
}

std::string origin_of(std::string_view uri) {
    const auto separator = uri.find("://");
    if (separator == std::string_view::npos) return std::string(uri);
    const auto end = uri.find_first_of("/?#", separator + 3);
    return std::string(uri.substr(0, end));
}

jobjectArray string_array(JNIEnv* env, const std::vector<std::string>& values) {
    jobjectArray array = env->NewObjectArray(static_cast<jsize>(values.size()), string_class, nullptr);
    for (size_t index = 0; array && index < values.size(); ++index) {
        jstring value = to_java(env, values[index]);
        env->SetObjectArrayElement(array, static_cast<jsize>(index), value);
        env->DeleteLocalRef(value);
    }
    return array;
}

android_view* view_state(neoastra_view_t* view) noexcept { return static_cast<android_view*>(view->platform); }

void report_history(neoastra_view_t* view, bool back, bool forward) {
    auto* state = view_state(view);
    if (!state) return;
    const uint64_t history = neo_history_directions(view, back, forward);
    if (state->history == history) return;
    state->history = history;
    neo_emit_view(view, NEOASTRA_EVENT_HISTORY_CHANGED, 0, nullptr, nullptr, history);
}

struct evaluation { neoastra_string_callback_t callback; void* context; neoastra_operation_t* operation; };
struct script_added { neoastra_string_callback_t callback; void* context; neoastra_operation_t* operation; std::string identifier; };

void complete_script_added(void* pointer) {
    std::unique_ptr<script_added> added(static_cast<script_added*>(pointer));
    neoastra_result_t actual{};
    if (added->operation->try_complete(NEOASTRA_OK, actual)) added->callback(added->context, actual, actual == NEOASTRA_OK ? neo_string_view(added->identifier) : neoastra_string_view_t{}, nullptr);
    added->operation->release();
}

void JNICALL on_message(JNIEnv* env, jclass, jlong pointer, jstring data, jstring origin, jboolean main_frame) {
    auto* view = reinterpret_cast<neoastra_view_t*>(pointer);
    {
        std::lock_guard lock(live_mutex);
        if (!is_live(view)) return;
    }
    try {
        const auto text = to_utf8(env, data);
        const auto uri = origin_of(unproject(view->environment, to_utf8(env, origin)));
        neo_emit_bridge_message(view, text, uri, main_frame == JNI_TRUE);
    } catch (...) { neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "bridge", "Android web-message handling failed"); }
}

jobject JNICALL on_resource(JNIEnv* env, jclass, jlong pointer, jstring url, jstring method_value, jstring headers_value, jboolean main_frame) {
    auto* view = reinterpret_cast<neoastra_view_t*>(pointer);
    std::lock_guard lock(live_mutex);
    if (!is_live(view)) return nullptr;
    auto* environment = view->environment;
    try {
        const auto uri = unproject(environment, to_utf8(env, url));
        const auto separator = uri.find("://");
        const auto* scheme = separator == std::string::npos ? nullptr : find_scheme(environment, std::string_view(uri).substr(0, separator));
        if (!scheme || !scheme->provider) return nullptr;
        auto method = to_utf8(env, method_value);
        if (method.empty()) method = "GET";
        const auto headers = to_utf8(env, headers_value);
        const std::string initiating_origin;
        if (!neo_resource_request_within_limits(uri, method, headers, initiating_origin)) return nullptr;
        neoastra_resource_request_t request{};
        request.size = sizeof(request);
        request.version = 1;
        request.uri = neo_string_view(uri);
        request.method = neo_string_view(method);
        request.headers = neo_string_view(headers);
        request.initiating_origin = neo_string_view(initiating_origin);
        request.resource_kind = main_frame ? NEOASTRA_RESOURCE_DOCUMENT : NEOASTRA_RESOURCE_OTHER;
        request.main_frame = main_frame ? 1u : 0u;
        neoastra_resource_response_t response{};
        response.size = sizeof(response);
        response.version = 1;
        neoastra_result_t result = NEOASTRA_ERROR_NATIVE_FAILURE;
        try { result = scheme->provider(scheme->provider_context, &request, &response); } catch (...) { result = NEOASTRA_ERROR_NATIVE_FAILURE; }
        struct release_guard {
            neoastra_resource_response_t& value;
            ~release_guard() { if (value.release && value.release_context) { try { value.release(value.release_context); } catch (...) { } } }
        } guard{response};
        int status = 500;
        jbyteArray body{};
        jstring file{};
        std::string reason, response_headers, mime;
        if (result == NEOASTRA_OK && neo_valid_resource_response(response)) {
            status = static_cast<int>(response.status_code);
            reason = neo_string(response.reason_phrase);
            response_headers = neo_string(response.headers);
            mime = neo_string(response.mime_type);
            if (response.body_kind == NEOASTRA_RESOURCE_BODY_BYTES && response.byte_length) {
                body = env->NewByteArray(static_cast<jsize>(response.byte_length));
                if (body) env->SetByteArrayRegion(body, 0, static_cast<jsize>(response.byte_length), reinterpret_cast<const jbyte*>(response.bytes));
            } else if (response.body_kind == NEOASTRA_RESOURCE_BODY_FILE) file = to_java(env, neo_string(response.file_path));
        } else neo_log(environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme resource provider failed or returned an invalid response", result);
        jstring reason_value = to_java(env, reason), headers_java = to_java(env, response_headers), mime_value = to_java(env, mime);
        jobject value = env->NewObject(response_class, response_new, static_cast<jint>(status), reason_value, mime_value, headers_java, body, file);
        if (failed(env, environment->app, "Could not build the Android resource response")) return nullptr;
        return value;
    } catch (...) {
        neo_log(environment->app, NEOASTRA_LOG_ERROR, "resource", "Custom-scheme request handling failed");
        return nullptr;
    }
}

void JNICALL on_event(JNIEnv* env, jclass, jlong pointer, jint kind, jstring url_value, jstring text_value, jint value) {
    auto* view = reinterpret_cast<neoastra_view_t*>(pointer);
    {
        std::lock_guard lock(live_mutex);
        if (!is_live(view)) return;
    }
    try {
        std::string uri = url_value ? unproject(view->environment, to_utf8(env, url_value)) : std::string{};
        switch (kind) {
            case 0:
                view->source = uri;
                neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_STARTED, 0, nullptr, &uri, 1);
                neo_emit_view(view, NEOASTRA_EVENT_SOURCE_CHANGED, 0, nullptr, &view->source);
                break;
            case 1: neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_COMPLETED, 0, nullptr, &uri); break;
            case 2: neo_emit_view(view, NEOASTRA_EVENT_NAVIGATION_FAILED, 0, nullptr, &uri, NEOASTRA_ERROR_NATIVE_FAILURE, value); break;
            case 3:
                view->title = to_utf8(env, text_value);
                neo_emit_view(view, NEOASTRA_EVENT_TITLE_CHANGED, 0, &view->title);
                break;
            case 4:
                // Android ends the renderer of a view when it crashes or under memory pressure; the application recreates the view.
                neo_emit_view(view, NEOASTRA_EVENT_WEB_PROCESS_TERMINATED, 0, nullptr, nullptr, NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED | NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW);
                break;
            case 5: report_history(view, (value & 1) != 0, (value & 2) != 0); break;
            default: break;
        }
    } catch (...) { neo_log(view->environment->app, NEOASTRA_LOG_ERROR, "android", "Android view event handling failed"); }
}

void JNICALL on_evaluated(JNIEnv* env, jclass, jlong token, jstring json) {
    std::unique_ptr<evaluation> pending(reinterpret_cast<evaluation*>(token));
    if (!pending) return;
    std::string output = json ? to_utf8(env, json) : std::string("null");
    if (output.empty()) output = "null";
    neoastra_result_t actual{};
    if (pending->operation->try_complete(NEOASTRA_OK, actual)) pending->callback(pending->context, actual, actual == NEOASTRA_OK ? neo_string_view(output) : neoastra_string_view_t{}, nullptr);
    pending->operation->release();
}

neoastra_result_t not_ready(neoastra_error_t** error) noexcept {
    return neo_fail(error, NEOASTRA_ERROR_BACKEND_UNAVAILABLE, "The Java side of the Android backend is not loaded: load com.neoastra.backend.NeoAstraWebHost before NeoAstra", 0, android_domain);
}
}  // namespace

// Java runs this when NeoAstraWebHost loads the library; a load by .NET alone does not, so the class has to load first.
extern "C" JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void*) {
    JNIEnv* env{};
    if (vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6) != JNI_OK) return JNI_ERR;
    jclass host = env->FindClass("com/neoastra/backend/NeoAstraWebHost");
    jclass response = env->FindClass("com/neoastra/backend/NeoAstraWebHost$Response");
    jclass string = env->FindClass("java/lang/String");
    if (!host || !response || !string) { env->ExceptionClear(); return JNI_VERSION_1_6; }
    host_class = static_cast<jclass>(env->NewGlobalRef(host));
    response_class = static_cast<jclass>(env->NewGlobalRef(response));
    string_class = static_cast<jclass>(env->NewGlobalRef(string));
    host_is_supported = env->GetStaticMethodID(host_class, "isSupported", "()Z");
    host_create = env->GetStaticMethodID(host_class, "create", "(Landroid/view/ViewGroup;J[Ljava/lang/String;Ljava/lang/String;)Lcom/neoastra/backend/NeoAstraWebHost;");
    host_set_bounds = env->GetMethodID(host_class, "setBounds", "(IIIIZ)V");
    host_navigate = env->GetMethodID(host_class, "navigate", "(Ljava/lang/String;)V");
    host_load_html = env->GetMethodID(host_class, "loadHtml", "(Ljava/lang/String;Ljava/lang/String;)V");
    host_command = env->GetMethodID(host_class, "command", "(I)V");
    host_evaluate = env->GetMethodID(host_class, "evaluate", "(Ljava/lang/String;J)V");
    host_add_script = env->GetMethodID(host_class, "addScript", "(Ljava/lang/String;[Ljava/lang/String;)I");
    host_remove_script = env->GetMethodID(host_class, "removeScript", "(I)Z");
    host_post_message = env->GetMethodID(host_class, "postMessage", "(Ljava/lang/String;)Z");
    host_web_view = env->GetMethodID(host_class, "webView", "()Landroid/webkit/WebView;");
    host_destroy = env->GetMethodID(host_class, "destroy", "()V");
    response_new = env->GetMethodID(response_class, "<init>", "(ILjava/lang/String;Ljava/lang/String;Ljava/lang/String;[BLjava/lang/String;)V");
    const JNINativeMethod methods[] = {
        {const_cast<char*>("nativeMessage"), const_cast<char*>("(JLjava/lang/String;Ljava/lang/String;Z)V"), reinterpret_cast<void*>(on_message)},
        {const_cast<char*>("nativeResource"), const_cast<char*>("(JLjava/lang/String;Ljava/lang/String;Ljava/lang/String;Z)Lcom/neoastra/backend/NeoAstraWebHost$Response;"), reinterpret_cast<void*>(on_resource)},
        {const_cast<char*>("nativeEvent"), const_cast<char*>("(JILjava/lang/String;Ljava/lang/String;I)V"), reinterpret_cast<void*>(on_event)},
        {const_cast<char*>("nativeEvaluated"), const_cast<char*>("(JLjava/lang/String;)V"), reinterpret_cast<void*>(on_evaluated)},
    };
    if (env->ExceptionCheck() || env->RegisterNatives(host_class, methods, 4) != JNI_OK) { env->ExceptionClear(); host_class = nullptr; return JNI_VERSION_1_6; }
    if (jclass services = env->FindClass("com/neoastra/backend/NeoAstraServices")) {
        services_class = static_cast<jclass>(env->NewGlobalRef(services));
        services_call = env->GetStaticMethodID(services_class, "call", "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
    }
    if (env->ExceptionCheck()) { env->ExceptionClear(); services_class = nullptr; }
    java_vm = vm;
    return JNI_VERSION_1_6;
}

bool neo_platform_environment_create_async(neoastra_environment_t* environment, const neoastra_environment_options_t* options, neo_platform_created_callback_t callback, void* context, neoastra_error_t** error) noexcept {
    JNIEnv* env = jni();
    if (!env || !host_class) { not_ready(error); return false; }
    const bool supported = env->CallStaticBooleanMethod(host_class, host_is_supported) == JNI_TRUE;
    if (failed(env, environment->app, "Could not query the Android WebView features") || !supported) {
        neo_fail(error, NEOASTRA_ERROR_RUNTIME_UNAVAILABLE, "The Android System WebView of this device lacks web message listeners or document-start scripts", 0, android_domain);
        return false;
    }
    // One process has one WebView data directory, so an environment can neither be private nor have a root of its own.
    if (options->private_mode) { neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, "The Android WebView has no private browsing environment", 0, android_domain); return false; }
    if (options->user_data_root.length) neo_log(environment->app, NEOASTRA_LOG_WARNING, "environment", "The Android WebView keeps the data of the process in one directory; the user-data root is not used");
    for (const auto& scheme : environment->custom_schemes) {
        if ((scheme.flags & NEOASTRA_CUSTOM_SCHEME_SERVICE_WORKERS) != 0) { neo_fail(error, NEOASTRA_ERROR_NOT_SUPPORTED, "Projected Android custom schemes do not support service workers", 0, android_domain); return false; }
    }
    auto* state = new (std::nothrow) android_environment{};
    if (!state) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Could not allocate the Android environment", 0, android_domain); return false; }
    environment->platform = state;
    callback(context, nullptr);
    return true;
}
void neo_platform_environment_destroy(neoastra_environment_t* environment) noexcept {
    delete static_cast<android_environment*>(environment->platform);
    environment->platform = nullptr;
}
bool neo_platform_profile_create(neoastra_profile_t* profile, neoastra_error_t** error) noexcept {
    if (profile->ephemeral) { unsupported(error, "An ephemeral profile"); return false; }
    auto* state = new (std::nothrow) android_profile{};
    if (!state) { neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Could not allocate the Android profile", 0, android_domain); return false; }
    profile->platform = state;
    return true;
}
void neo_platform_profile_destroy(neoastra_profile_t* profile) noexcept {
    delete static_cast<android_profile*>(profile->platform);
    profile->platform = nullptr;
}
neoastra_result_t neo_platform_profile_get_cookies(neoastra_profile_t*, const std::string&, neoastra_buffer_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Cookie access"); }
neoastra_result_t neo_platform_profile_set_cookie(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Cookie access"); }
neoastra_result_t neo_platform_profile_delete_cookie(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Cookie access"); }
neoastra_result_t neo_platform_profile_clear_data(neoastra_profile_t*, neoastra_data_kind_t, int64_t, int64_t, neoastra_completion_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Clearing browser data"); }

bool neo_platform_view_create_async(neoastra_view_t* view, const neoastra_view_options_t* options, neo_platform_created_callback_t callback, void* context, neoastra_error_t** error) noexcept {
    if (options->popup_request) { unsupported(error, "A popup view"); return false; }
    JNIEnv* env = jni();
    if (!env || !host_class) { not_ready(error); return false; }
    if (view->parent.kind != NEOASTRA_NATIVE_PARENT_ANDROID_VIEW_GROUP || !view->parent.handle) {
        neo_fail(error, NEOASTRA_ERROR_INVALID_STATE, "An Android view needs a ViewGroup of the application as its parent", 0, android_domain);
        return false;
    }
    try {
        auto state = std::make_unique<android_view>();
        jobjectArray origins{};
        if (view->bridge_policy == NEOASTRA_BRIDGE_TRUSTED_ORIGINS) {
            std::vector<std::string> rules;
            for (const auto& origin : view->bridge_origins) rules.push_back(origin_of(project(view->environment, origin)));
            origins = string_array(env, rules);
        } else if (view->bridge_policy == NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW) origins = string_array(env, {"*"});
        jstring suffix = to_java(env, projected_suffix);
        jobject host = env->CallStaticObjectMethod(host_class, host_create, static_cast<jobject>(view->parent.handle), reinterpret_cast<jlong>(view), origins, suffix);
        env->DeleteLocalRef(suffix);
        if (origins) env->DeleteLocalRef(origins);
        if (failed(env, view->environment->app, "The Android WebView could not be created") || !host) {
            neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "The Android WebView could not be created", 0, android_domain);
            return false;
        }
        state->host = env->NewGlobalRef(host);
        env->DeleteLocalRef(host);
        jobject web_view = env->CallObjectMethod(state->host, host_web_view);
        if (!failed(env, view->environment->app, "Could not read the Android WebView") && web_view) {
            state->web_view = env->NewGlobalRef(web_view);
            env->DeleteLocalRef(web_view);
        }
        if (!view->fill_parent) env->CallVoidMethod(state->host, host_set_bounds, view->bounds.x, view->bounds.y, view->bounds.width, view->bounds.height, JNI_FALSE);
        failed(env, view->environment->app, "Could not place the Android WebView");
        view->platform = state.release();
        {
            std::lock_guard lock(live_mutex);
            live_views.push_back(view);
        }
        callback(context, nullptr);
        return true;
    } catch (const std::exception& exception) {
        neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, exception.what(), 0, android_domain);
        return false;
    }
}
void neo_platform_view_destroy(neoastra_view_t* view) noexcept {
    auto* state = view_state(view);
    if (!state) return;
    {
        std::lock_guard lock(live_mutex);
        live_views.erase(std::remove(live_views.begin(), live_views.end(), view), live_views.end());
    }
    if (JNIEnv* env = jni()) {
        if (state->host) {
            env->CallVoidMethod(state->host, host_destroy);
            failed(env, view->environment->app, "Could not destroy the Android WebView");
            env->DeleteGlobalRef(state->host);
        }
        if (state->web_view) env->DeleteGlobalRef(state->web_view);
    }
    delete state;
    view->platform = nullptr;
}
neoastra_result_t neo_platform_view_set_bounds(neoastra_view_t* view) noexcept {
    auto* state = view_state(view);
    JNIEnv* env = jni();
    if (!state || !state->host || !env) return NEOASTRA_ERROR_NOT_INITIALIZED;
    env->CallVoidMethod(state->host, host_set_bounds, view->bounds.x, view->bounds.y, view->bounds.width, view->bounds.height, view->fill_parent ? JNI_TRUE : JNI_FALSE);
    return failed(env, view->environment->app, "Could not place the Android WebView") ? NEOASTRA_ERROR_NATIVE_FAILURE : NEOASTRA_OK;
}
neoastra_result_t neo_platform_view_navigate(neoastra_view_t* view, const std::string& uri, neoastra_error_t** error) noexcept {
    auto* state = view_state(view);
    JNIEnv* env = jni();
    if (!state || !state->host || !env) return neo_fail(error, NEOASTRA_ERROR_NOT_INITIALIZED, "The Android view is not initialized", 0, android_domain);
    try {
        jstring value = to_java(env, project(view->environment, uri));
        env->CallVoidMethod(state->host, host_navigate, value);
        env->DeleteLocalRef(value);
    } catch (...) { return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Android navigation failed", 0, android_domain); }
    return failed(env, view->environment->app, "Android navigation failed") ? neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Android navigation failed", 0, android_domain) : NEOASTRA_OK;
}
// The Android WebView loads a request with a method and a body only through a form; a plain GET is an ordinary navigation.
neoastra_result_t neo_platform_view_navigate_request(neoastra_view_t* view, const std::string& uri, const std::string& method, const std::string& headers, const uint8_t*, uint64_t body_length, neoastra_error_t** error) noexcept {
    if (method != "GET" || body_length || !headers.empty()) return unsupported(error, "A navigation with a method, headers, or a body");
    return neo_platform_view_navigate(view, uri, error);
}
neoastra_result_t neo_platform_view_load_html(neoastra_view_t* view, const std::string& html, const std::string& base, neoastra_error_t** error) noexcept {
    auto* state = view_state(view);
    JNIEnv* env = jni();
    if (!state || !state->host || !env) return NEOASTRA_ERROR_NOT_INITIALIZED;
    try {
        jstring content = to_java(env, html);
        jstring base_value = base.empty() ? nullptr : to_java(env, project(view->environment, base));
        env->CallVoidMethod(state->host, host_load_html, content, base_value);
        env->DeleteLocalRef(content);
        if (base_value) env->DeleteLocalRef(base_value);
    } catch (...) { return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Loading HTML into the Android view failed", 0, android_domain); }
    return failed(env, view->environment->app, "Loading HTML into the Android view failed") ? NEOASTRA_ERROR_NATIVE_FAILURE : NEOASTRA_OK;
}
neoastra_result_t neo_platform_view_command(neoastra_view_t* view, uint32_t command) noexcept {
    auto* state = view_state(view);
    JNIEnv* env = jni();
    if (!state || !state->host || !env) return NEOASTRA_ERROR_NOT_INITIALIZED;
    if (command > 4) return NEOASTRA_ERROR_INVALID_ARGUMENT;
    env->CallVoidMethod(state->host, host_command, static_cast<jint>(command));
    return failed(env, view->environment->app, "An Android view command failed") ? NEOASTRA_ERROR_NATIVE_FAILURE : NEOASTRA_OK;
}
neoastra_result_t neo_platform_view_evaluate(neoastra_view_t* view, const std::string& script, neoastra_string_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept {
    auto* state = view_state(view);
    JNIEnv* env = jni();
    if (!state || !state->host || !env) return NEOASTRA_ERROR_NOT_INITIALIZED;
    auto* pending = new (std::nothrow) evaluation{callback, context, operation};
    if (!pending) return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Could not allocate the script evaluation", 0, android_domain);
    try {
        jstring value = to_java(env, script);
        env->CallVoidMethod(state->host, host_evaluate, value, reinterpret_cast<jlong>(pending));
        env->DeleteLocalRef(value);
    } catch (...) { delete pending; return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Android script evaluation failed", 0, android_domain); }
    if (failed(env, view->environment->app, "Android script evaluation failed")) { delete pending; return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Android script evaluation failed", 0, android_domain); }
    return NEOASTRA_OK;
}
// Document-start scripts run in every frame whose origin matches; a main-frame script is guarded so that subframes skip it.
neoastra_result_t neo_platform_view_add_script(neoastra_view_t* view, const std::string& script, const neoastra_script_options_t* options, neoastra_string_callback_t callback, void* context, neoastra_operation_t* operation, neoastra_error_t** error) noexcept {
    auto* state = view_state(view);
    JNIEnv* env = jni();
    if (!state || !state->host || !env) return NEOASTRA_ERROR_NOT_INITIALIZED;
    if (options->isolated_world || options->injection_time == NEOASTRA_SCRIPT_DOCUMENT_END) return NEOASTRA_ERROR_NOT_SUPPORTED;
    try {
        const std::string source = options->main_frame_only ? "if (globalThis === globalThis.top) {\n" + script + "\n}" : script;
        jstring value = to_java(env, source);
        jobjectArray rules = string_array(env, {"*"});
        const jint identifier = env->CallIntMethod(state->host, host_add_script, value, rules);
        env->DeleteLocalRef(value);
        env->DeleteLocalRef(rules);
        if (failed(env, view->environment->app, "Adding an Android document script failed")) return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Adding an Android document script failed", 0, android_domain);
        auto* added = new script_added{callback, context, operation, std::to_string(identifier)};
        if (neoastra_app_dispatch(view->environment->app, complete_script_added, added) != NEOASTRA_OK) {
            delete added;
            return neo_fail(error, NEOASTRA_ERROR_INVALID_STATE, "The application no longer accepts work", 0, android_domain);
        }
    } catch (...) { return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Adding an Android document script failed", 0, android_domain); }
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_view_remove_script(neoastra_view_t* view, const std::string& identifier) noexcept {
    auto* state = view_state(view);
    JNIEnv* env = jni();
    if (!state || !state->host || !env) return NEOASTRA_ERROR_NOT_INITIALIZED;
    int value{};
    try { value = std::stoi(identifier); } catch (...) { return NEOASTRA_ERROR_INVALID_ARGUMENT; }
    const bool removed = env->CallBooleanMethod(state->host, host_remove_script, static_cast<jint>(value)) == JNI_TRUE;
    if (failed(env, view->environment->app, "Removing an Android document script failed")) return NEOASTRA_ERROR_NATIVE_FAILURE;
    return removed ? NEOASTRA_OK : NEOASTRA_ERROR_INVALID_ARGUMENT;
}
// Host messages reach the page through the reply proxy of the last message of its main frame, as JSON text for the bootstrap.
neoastra_result_t neo_platform_view_post_message(neoastra_view_t* view, const std::string& message, bool json, neoastra_error_t** error) noexcept {
    auto* state = view_state(view);
    JNIEnv* env = jni();
    if (!state || !state->host || !env) return NEOASTRA_ERROR_NOT_INITIALIZED;
    if (!json) return unsupported(error, "A message that is not JSON");
    bool delivered{};
    try {
        jstring value = to_java(env, message);
        delivered = env->CallBooleanMethod(state->host, host_post_message, value) == JNI_TRUE;
        env->DeleteLocalRef(value);
    } catch (...) { return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Posting to the Android view failed", 0, android_domain); }
    if (failed(env, view->environment->app, "Posting to the Android view failed")) return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "Posting to the Android view failed", 0, android_domain);
    if (!delivered) neo_log(view->environment->app, NEOASTRA_LOG_DEBUG, "bridge", "The page has not sent a message yet, so the Android view has no reply channel; the message was dropped");
    return NEOASTRA_OK;
}
neoastra_result_t neo_platform_view_get_zoom_factor(const neoastra_view_t*, double*) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_set_zoom_factor(neoastra_view_t*, double) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_set_setting(neoastra_view_t* view, neoastra_view_setting_t setting, bool enabled) noexcept {
    if (!view_state(view)) return NEOASTRA_ERROR_NOT_INITIALIZED;
    switch (setting) {
        case NEOASTRA_VIEW_SETTING_BROWSER_ACCELERATOR_KEYS:
        case NEOASTRA_VIEW_SETTING_STATUS_BAR:
        case NEOASTRA_VIEW_SETTING_DEFAULT_SCRIPT_DIALOGS:
        case NEOASTRA_VIEW_SETTING_DEFAULT_CONTEXT_MENUS:
        case NEOASTRA_VIEW_SETTING_ZOOM_CONTROLS:
        case NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS:
        case NEOASTRA_VIEW_SETTING_DEVTOOLS:
            return enabled ? NEOASTRA_ERROR_NOT_SUPPORTED : NEOASTRA_OK;
        case NEOASTRA_VIEW_SETTING_HISTORY_NAVIGATION: return NEOASTRA_OK;
        default: return NEOASTRA_ERROR_INVALID_ARGUMENT;
    }
}
bool neo_platform_open_external(const std::string&, int64_t* native_code) noexcept { if (native_code) *native_code = 0; return false; }
neoastra_result_t neo_platform_view_move_pointer(neoastra_view_t*, double, double) noexcept { return NEOASTRA_ERROR_NOT_SUPPORTED; }
neoastra_result_t neo_platform_view_open_devtools(neoastra_view_t* view) noexcept { return view_state(view) ? NEOASTRA_ERROR_NOT_SUPPORTED : NEOASTRA_ERROR_NOT_INITIALIZED; }
neoastra_result_t neo_platform_view_capture(neoastra_view_t*, const neoastra_capture_options_t&, neoastra_buffer_callback_t, void*, neoastra_operation_t*, neoastra_error_t** error) noexcept { return unsupported(error, "Capture"); }
neoastra_result_t neo_platform_view_get_handle(neoastra_view_t* view, neoastra_native_handle_kind_t kind, neoastra_native_handle_t* handle) noexcept {
    auto* state = view_state(view);
    if (!state) return NEOASTRA_ERROR_NOT_INITIALIZED;
    if (kind != NEOASTRA_NATIVE_HANDLE_ANDROID_WEBVIEW || !state->web_view) return NEOASTRA_ERROR_NOT_SUPPORTED;
    handle->kind = kind;
    handle->value = state->web_view;
    return NEOASTRA_OK;
}

// The services of the Java side answer one JSON request with one JSON response; a Java exception becomes a native failure.
neoastra_result_t neo_android_platform_call(const std::string& service, const std::string& request, neoastra_buffer_t** response, neoastra_error_t** error) noexcept {
    JNIEnv* env = jni();
    if (!env || !services_class || !services_call) return not_ready(error);
    try {
        jstring service_value = to_java(env, service);
        jstring request_value = to_java(env, request);
        auto* result = static_cast<jstring>(env->CallStaticObjectMethod(services_class, services_call, service_value, request_value));
        env->DeleteLocalRef(service_value);
        env->DeleteLocalRef(request_value);
        if (env->ExceptionCheck()) {
            jthrowable thrown = env->ExceptionOccurred();
            env->ExceptionClear();
            std::string message = "The Android platform service failed";
            if (thrown) {
                jclass throwable = env->FindClass("java/lang/Throwable");
                jmethodID describe = throwable ? env->GetMethodID(throwable, "toString", "()Ljava/lang/String;") : nullptr;
                if (describe) {
                    auto* text = static_cast<jstring>(env->CallObjectMethod(thrown, describe));
                    if (!env->ExceptionCheck() && text) { message += ": " + to_utf8(env, text); env->DeleteLocalRef(text); }
                    env->ExceptionClear();
                }
                env->DeleteLocalRef(thrown);
            }
            return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, message, 0, android_domain);
        }
        const auto text = to_utf8(env, result);
        if (result) env->DeleteLocalRef(result);
        *response = new neoastra_buffer(std::vector<uint8_t>(text.begin(), text.end()));
        return NEOASTRA_OK;
    } catch (...) {
        return neo_fail(error, NEOASTRA_ERROR_NATIVE_FAILURE, "The Android platform service call failed", 0, android_domain);
    }
}
