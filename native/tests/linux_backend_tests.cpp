#include "neoastra.h"

#include <gtk/gtk.h>
#include <webkit/webkit.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstdint>
#include <string>

namespace {

struct completion {
    bool done{};
    neoastra_result_t result{NEOASTRA_ERROR_UNKNOWN};
    void* value{};
    std::string text;
};

neoastra_string_view_t string_view(const std::string& value) {
    return {reinterpret_cast<const uint8_t*>(value.data()), value.size()};
}

// The application is attached, so the tests iterate the main context that delivers native completions.
void wait_for(const completion& state) {
    const auto deadline = g_get_monotonic_time() + 60 * G_USEC_PER_SEC;
    while (!state.done) {
        assert(g_get_monotonic_time() < deadline);
        if (!g_main_context_iteration(nullptr, FALSE)) g_usleep(1000);
    }
}

void complete(void* context, neoastra_result_t result, void* value) {
    auto* state = static_cast<completion*>(context);
    state->result = result;
    state->value = value;
    state->done = true;
}

void NEOASTRA_CALL environment_created(void* context, neoastra_result_t result, neoastra_environment_t* value, const neoastra_error_t*) { complete(context, result, value); }
void NEOASTRA_CALL view_created(void* context, neoastra_result_t result, neoastra_view_t* value, const neoastra_error_t*) { complete(context, result, value); }

neoastra_environment_t* create_environment(neoastra_app_t* app, const std::string& user_data_root, bool private_mode = false) {
    neoastra_environment_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    if (!user_data_root.empty()) options.user_data_root = string_view(user_data_root);
    options.private_mode = private_mode ? 1 : 0;
    completion state;
    assert(neoastra_environment_create_async(app, &options, environment_created, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    assert(state.result == NEOASTRA_OK && state.value != nullptr);
    return static_cast<neoastra_environment_t*>(state.value);
}

neoastra_window_t* create_window(neoastra_app_t* app) {
    neoastra_window_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.bounds = {100, 100, 640, 480};
    options.flags = 3;
    neoastra_window_t* window = nullptr;
    assert(neoastra_app_create_window(app, &options, &window, nullptr) == NEOASTRA_OK && window != nullptr);
    return window;
}

neoastra_view_t* create_view(neoastra_environment_t* environment, neoastra_window_t* window, neoastra_profile_t* profile = nullptr) {
    neoastra_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.profile = profile;
    options.window = window;
    options.fill_parent = 1;
    completion state;
    assert(neoastra_environment_create_view_async(environment, &options, view_created, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    assert(state.result == NEOASTRA_OK && state.value != nullptr);
    return static_cast<neoastra_view_t*>(state.value);
}

WebKitWebView* web_view(neoastra_view_t* view) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_view_get_native_handle(view, NEOASTRA_NATIVE_HANDLE_WEBKITGTK_WEBVIEW, &handle) == NEOASTRA_OK);
    return WEBKIT_WEB_VIEW(handle.value);
}

// A window keeps a pointer to its child. A view that goes away has to clear it, or the window frees the view a second
// time when it hosts the next one or closes.
void test_view_leaves_its_window(neoastra_app_t* app) {
    auto* environment = create_environment(app, "", true);
    auto* window = create_window(app);
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_window_get_native_handle(window, NEOASTRA_NATIVE_HANDLE_GTK_WINDOW, &handle) == NEOASTRA_OK);
    auto* host = GTK_WINDOW(handle.value);
    for (int index = 0; index < 2; index++) {
        auto* view = create_view(environment, window);
        assert(gtk_window_get_child(host) == GTK_WIDGET(web_view(view)));
        neoastra_view_release(view);
        assert(gtk_window_get_child(host) == nullptr);
    }
    neoastra_window_release(window);
    neoastra_environment_release(environment);
}

} // namespace

int main() {
    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    neoastra_app_t* app = nullptr;
    assert(neoastra_app_attach(&options, &app, nullptr) == NEOASTRA_OK && app != nullptr);

    test_view_leaves_its_window(app);

    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
    return 0;
}
