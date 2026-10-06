#include "neoastra.h"

#include <gtk/gtk.h>
#include <webkit/webkit.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstdint>
#include <filesystem>
#include <string>
#include <system_error>
#include <unistd.h>

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
template<typename TCondition> void wait_until(TCondition&& condition) {
    const auto deadline = g_get_monotonic_time() + 60 * G_USEC_PER_SEC;
    while (!condition()) {
        assert(g_get_monotonic_time() < deadline);
        if (!g_main_context_iteration(nullptr, FALSE)) g_usleep(1000);
    }
}

void wait_for(const completion& state) { wait_until([&] { return state.done; }); }

void complete(void* context, neoastra_result_t result, void* value) {
    auto* state = static_cast<completion*>(context);
    state->result = result;
    state->value = value;
    state->done = true;
}

void NEOASTRA_CALL environment_created(void* context, neoastra_result_t result, neoastra_environment_t* value, const neoastra_error_t*) { complete(context, result, value); }
void NEOASTRA_CALL profile_created(void* context, neoastra_result_t result, neoastra_profile_t* value, const neoastra_error_t*) { complete(context, result, value); }
void NEOASTRA_CALL view_created(void* context, neoastra_result_t result, neoastra_view_t* value, const neoastra_error_t*) { complete(context, result, value); }
void NEOASTRA_CALL operation_completed(void* context, neoastra_result_t result, const neoastra_error_t*) { complete(context, result, nullptr); }

void NEOASTRA_CALL cookies_read(void* context, neoastra_result_t result, neoastra_buffer_t* buffer, const neoastra_error_t*) {
    auto* state = static_cast<completion*>(context);
    if (buffer) {
        state->text.assign(reinterpret_cast<const char*>(neoastra_buffer_get_data(buffer)), static_cast<size_t>(neoastra_buffer_get_length(buffer)));
        neoastra_buffer_release(buffer);
    }
    complete(context, result, nullptr);
}

completion start_environment(neoastra_app_t* app, const std::string& user_data_root, bool private_mode) {
    neoastra_environment_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    if (!user_data_root.empty()) options.user_data_root = string_view(user_data_root);
    options.private_mode = private_mode ? 1 : 0;
    completion state;
    assert(neoastra_environment_create_async(app, &options, environment_created, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    return state;
}

neoastra_environment_t* create_environment(neoastra_app_t* app, const std::string& user_data_root, bool private_mode = false) {
    const auto state = start_environment(app, user_data_root, private_mode);
    assert(state.result == NEOASTRA_OK && state.value != nullptr);
    return static_cast<neoastra_environment_t*>(state.value);
}

neoastra_profile_t* create_profile(neoastra_environment_t* environment, bool ephemeral = false) {
    neoastra_profile_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.ephemeral = ephemeral ? 1 : 0;
    completion state;
    assert(neoastra_environment_create_profile_async(environment, &options, profile_created, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    assert(state.result == NEOASTRA_OK && state.value != nullptr);
    return static_cast<neoastra_profile_t*>(state.value);
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

// Returns the session that a view of the environment keeps its website data in. The environment or the profile keeps it alive.
WebKitNetworkSession* view_session(neoastra_environment_t* environment, neoastra_window_t* window, neoastra_profile_t* profile = nullptr) {
    auto* view = create_view(environment, window, profile);
    auto* session = webkit_web_view_get_network_session(web_view(view));
    assert(session != nullptr);
    neoastra_view_release(view);
    return session;
}

std::string data_directory(WebKitNetworkSession* session) {
    const auto* value = webkit_website_data_manager_get_base_data_directory(webkit_network_session_get_website_data_manager(session));
    return value ? value : "";
}

std::string cache_directory(WebKitNetworkSession* session) {
    const auto* value = webkit_website_data_manager_get_base_cache_directory(webkit_network_session_get_website_data_manager(session));
    return value ? value : "";
}

const std::string cookie_name = "neoastra-backend-test";
const std::string cookie_domain = "neoastra.test";
const std::string cookie_path = "/";

void set_cookie(neoastra_profile_t* profile, const std::string& value) {
    neoastra_cookie_t cookie{};
    cookie.size = sizeof(cookie);
    cookie.version = 1;
    cookie.name = string_view(cookie_name);
    cookie.value = string_view(value);
    cookie.domain = string_view(cookie_domain);
    cookie.path = string_view(cookie_path);
    cookie.expires_unix_ms = g_get_real_time() / 1000 + 3600 * 1000;
    completion state;
    assert(neoastra_profile_set_cookie_async(profile, &cookie, operation_completed, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    assert(state.result == NEOASTRA_OK);
}

void clear_cookies(neoastra_profile_t* profile) {
    completion state;
    assert(neoastra_profile_clear_data_async(profile, NEOASTRA_DATA_COOKIES, INT64_MIN, INT64_MAX, operation_completed, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    assert(state.result == NEOASTRA_OK);
}

bool has_cookie(neoastra_profile_t* profile, const std::string& value) {
    const std::string uri = "https://" + cookie_domain + "/";
    completion state;
    assert(neoastra_profile_get_cookies_async(profile, string_view(uri), cookies_read, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    assert(state.result == NEOASTRA_OK);
    return state.text.find("\"" + value + "\"") != std::string::npos;
}

// Environments on different user-data roots must not share cookies, local storage, or any other website data.
void test_user_data_roots(neoastra_app_t* app, const std::filesystem::path& base) {
    const std::string first_root = base / "a";
    const std::string second_root = base / "b";
    const std::string private_root = base / "private";
    // The first root again, through a symbolic link, a parent segment, and a trailing separator.
    assert(symlink(base.c_str(), (base / "link").c_str()) == 0);
    const std::string first_root_alias = (base / "link" / "unused" / ".." / "a").string() + "/";

    auto* window = create_window(app);
    auto* first = create_environment(app, first_root);
    auto* first_again = create_environment(app, first_root_alias);
    auto* second = create_environment(app, second_root);
    auto* unrooted = create_environment(app, "");
    auto* private_environment = create_environment(app, private_root, true);

    auto* first_profile = create_profile(first);
    auto* first_again_profile = create_profile(first_again);
    auto* second_profile = create_profile(second);
    auto* unrooted_profile = create_profile(unrooted);
    auto* ephemeral_profile = create_profile(first, true);
    auto* private_profile = create_profile(private_environment);

    auto* first_session = view_session(first, window);
    auto* second_session = view_session(second, window);
    auto* unrooted_session = view_session(unrooted, window);
    auto* private_session = view_session(private_environment, window);

    // Without a root, and in private mode, the environment keeps the sessions it always had. A private one stores
    // nothing, so it does not create its root either.
    assert(unrooted_session == webkit_network_session_get_default());
    assert(webkit_network_session_is_ephemeral(private_session));
    assert(!std::filesystem::exists(private_root));

    // Each root has a persistent session of its own, which a second environment on the same root shares.
    assert(first_session != unrooted_session && second_session != unrooted_session && first_session != second_session);
    assert(!webkit_network_session_is_ephemeral(first_session) && !webkit_network_session_is_ephemeral(second_session));
    assert(view_session(first_again, window) == first_session);

    // A root must keep its directories across runs and releases: they are all that finds its stored data again.
    assert(data_directory(first_session) == first_root + "/data" && cache_directory(first_session) == first_root + "/cache");
    assert(data_directory(second_session) == second_root + "/data" && cache_directory(second_session) == second_root + "/cache");
    assert(std::filesystem::is_directory(first_root + "/data") && std::filesystem::is_directory(first_root + "/cache"));

    // A profile that is not ephemeral is the environment's own session, including in a private environment.
    assert(view_session(first, window, first_profile) == first_session);
    assert(view_session(unrooted, window, unrooted_profile) == unrooted_session);
    assert(webkit_network_session_is_ephemeral(view_session(first, window, ephemeral_profile)));
    assert(view_session(private_environment, window, private_profile) == private_session);

    // What one root stores is visible through that root only, and clearing it leaves the other roots alone.
    set_cookie(first_profile, "first-root");
    set_cookie(second_profile, "second-root");
    // WebKitGTK keeps cookies in memory unless it is given a file for them, which a root provides.
    wait_until([&] { return std::filesystem::exists(first_root + "/data/cookies.sqlite"); });
    assert(has_cookie(first_again_profile, "first-root") && !has_cookie(first_again_profile, "second-root"));
    assert(has_cookie(second_profile, "second-root") && !has_cookie(second_profile, "first-root"));
    assert(!has_cookie(unrooted_profile, "first-root") && !has_cookie(unrooted_profile, "second-root"));
    clear_cookies(first_profile);
    assert(!has_cookie(first_again_profile, "first-root") && has_cookie(second_profile, "second-root"));
    clear_cookies(second_profile);
    assert(!has_cookie(second_profile, "second-root"));

    neoastra_profile_release(private_profile);
    neoastra_profile_release(ephemeral_profile);
    neoastra_profile_release(unrooted_profile);
    neoastra_profile_release(second_profile);
    neoastra_profile_release(first_again_profile);
    neoastra_profile_release(first_profile);
    neoastra_environment_release(private_environment);
    neoastra_environment_release(unrooted);
    neoastra_environment_release(second);
    neoastra_environment_release(first_again);
    neoastra_environment_release(first);

    // A root that every environment has left can be opened again.
    auto* reopened = create_environment(app, first_root);
    assert(data_directory(view_session(reopened, window)) == first_root + "/data");
    neoastra_environment_release(reopened);

    // A root that cannot hold the data is an error, not a silent fallback to another store.
    assert(g_file_set_contents((base / "file").c_str(), "", 0, nullptr));
    const auto unusable = start_environment(app, base / "file" / "root", false);
    assert(unusable.result == NEOASTRA_ERROR_NATIVE_FAILURE && unusable.value == nullptr);

    neoastra_window_release(window);
}

} // namespace

int main() {
    char* created = g_dir_make_tmp("neoastra-linux-backend-tests-XXXXXX", nullptr);
    assert(created != nullptr);
    const auto base = std::filesystem::canonical(created);
    g_free(created);
    // Keeps what the default session stores inside the directory that the test removes.
    g_setenv("XDG_DATA_HOME", (base / "xdg-data").c_str(), TRUE);
    g_setenv("XDG_CACHE_HOME", (base / "xdg-cache").c_str(), TRUE);

    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    neoastra_app_t* app = nullptr;
    assert(neoastra_app_attach(&options, &app, nullptr) == NEOASTRA_OK && app != nullptr);

    test_view_leaves_its_window(app);
    test_user_data_roots(app, base);

    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);

    std::error_code ignored;
    std::filesystem::remove_all(base, ignored);
    return 0;
}
