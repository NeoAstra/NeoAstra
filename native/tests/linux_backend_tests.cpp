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

GtkWindow* gtk_window(neoastra_window_t* window) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_window_get_native_handle(window, NEOASTRA_NATIVE_HANDLE_GTK_WINDOW, &handle) == NEOASTRA_OK);
    return GTK_WINDOW(handle.value);
}

// A window reports the size it has. The size it was asked for is what it starts with: once it is on screen GTK lays
// it out, and the user, a screen, or a size limit can give it another size. The backend used to wait for the width and
// height properties of the window, which a GTK 4 window does not have, and kept reporting the size it had asked for.
void test_window_reports_the_size_it_has(neoastra_app_t* app) {
    auto* window = create_window(app);
    auto* host = gtk_window(window);
    neoastra_rect_t bounds{};
    assert(neoastra_window_get_bounds(window, &bounds) == NEOASTRA_OK && bounds.width == 640 && bounds.height == 480);
    double scale_factor{};
    assert(neoastra_window_get_scale_factor(window, &scale_factor) == NEOASTRA_OK);
    assert(scale_factor == gtk_widget_get_scale_factor(GTK_WIDGET(host)));
    assert(neoastra_window_show(window) == NEOASTRA_OK);
    wait_until([&] { return gtk_widget_get_width(GTK_WIDGET(host)) > 0; });
    // A size the window takes without being asked through this library is what dragging an edge gives it.
    gtk_window_set_default_size(host, 700, 500);
    wait_until([&] { return gtk_widget_get_width(GTK_WIDGET(host)) == 700 && gtk_widget_get_height(GTK_WIDGET(host)) == 500; });
    wait_until([&] { return neoastra_window_get_bounds(window, &bounds) == NEOASTRA_OK && bounds.width == 700 && bounds.height == 500; });
    // A size asked for through the library is the one reported once the window has it.
    assert(neoastra_window_set_bounds(window, {bounds.x, bounds.y, 520, 420}) == NEOASTRA_OK);
    wait_until([&] { return gtk_widget_get_width(GTK_WIDGET(host)) == 520 && gtk_widget_get_height(GTK_WIDGET(host)) == 420; });
    assert(neoastra_window_get_bounds(window, &bounds) == NEOASTRA_OK && bounds.width == 520 && bounds.height == 420);
    // A hidden window keeps the size it had, and takes a size it is asked for when it is shown again.
    assert(neoastra_window_hide(window) == NEOASTRA_OK);
    assert(neoastra_window_set_bounds(window, {bounds.x, bounds.y, 600, 450}) == NEOASTRA_OK);
    assert(neoastra_window_get_bounds(window, &bounds) == NEOASTRA_OK && bounds.width == 600 && bounds.height == 450);
    assert(neoastra_window_show(window) == NEOASTRA_OK);
    wait_until([&] { return gtk_widget_get_width(GTK_WIDGET(host)) == 600 && gtk_widget_get_height(GTK_WIDGET(host)) == 450; });
    assert(neoastra_window_get_bounds(window, &bounds) == NEOASTRA_OK && bounds.width == 600 && bounds.height == 450);
    neoastra_window_release(window);
}

// A GTK window has one child, so the views of a window share a stack that the window keeps. A view that goes away
// has to leave the stack, or its widget is freed a second time when the window hosts the next view or closes.
void test_view_leaves_its_window(neoastra_app_t* app) {
    auto* environment = create_environment(app, "", true);
    auto* window = create_window(app);
    auto* host = gtk_window(window);
    GtkWidget* stack = nullptr;
    for (int index = 0; index < 2; index++) {
        auto* view = create_view(environment, window);
        // The window gets its stack with its first view and keeps it for the next one.
        assert(GTK_IS_OVERLAY(gtk_window_get_child(host)));
        assert(stack == nullptr || stack == gtk_window_get_child(host));
        stack = gtk_window_get_child(host);
        assert(gtk_widget_get_parent(GTK_WIDGET(web_view(view))) == stack);
        neoastra_view_release(view);
        assert(gtk_window_get_child(host) == stack);
        assert(gtk_widget_get_first_child(stack) == nullptr);
    }
    neoastra_window_release(window);
    neoastra_environment_release(environment);
}

// WebKitGTK stops on links with the Tab key unless it is told otherwise, and the setting of a view reaches its settings.
void test_tab_key_setting_reaches_the_view(neoastra_app_t* app) {
    auto* environment = create_environment(app, "", true);
    auto* window = create_window(app);
    auto* view = create_view(environment, window);
    auto* settings = webkit_web_view_get_settings(web_view(view));
    assert(webkit_settings_get_enable_tabs_to_links(settings));
    assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS, 0) == NEOASTRA_OK);
    assert(!webkit_settings_get_enable_tabs_to_links(settings));
    assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_TAB_FOCUSES_LINKS, 1) == NEOASTRA_OK);
    assert(webkit_settings_get_enable_tabs_to_links(settings));
    neoastra_view_release(view);
    neoastra_window_release(window);
    neoastra_environment_release(environment);
}

// The swipe of WebKitGTK that walks the history is off while history navigation is off for a view, which then refuses the
// commands of the history. Turning history navigation on again does not turn the swipe on: a host does that itself.
void test_history_navigation_setting_reaches_the_view(neoastra_app_t* app) {
    auto* environment = create_environment(app, "", true);
    auto* window = create_window(app);
    auto* view = create_view(environment, window);
    auto* settings = webkit_web_view_get_settings(web_view(view));
    webkit_settings_set_enable_back_forward_navigation_gestures(settings, TRUE);
    assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_HISTORY_NAVIGATION, 1) == NEOASTRA_OK);
    assert(webkit_settings_get_enable_back_forward_navigation_gestures(settings));
    assert(neoastra_view_go_back(view) == NEOASTRA_OK);
    assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_HISTORY_NAVIGATION, 0) == NEOASTRA_OK);
    assert(!webkit_settings_get_enable_back_forward_navigation_gestures(settings));
    assert(neoastra_view_go_back(view) == NEOASTRA_ERROR_INVALID_STATE);
    assert(neoastra_view_go_forward(view) == NEOASTRA_ERROR_INVALID_STATE);
    assert(neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_HISTORY_NAVIGATION, 1) == NEOASTRA_OK);
    assert(!webkit_settings_get_enable_back_forward_navigation_gestures(settings));
    assert(neoastra_view_go_forward(view) == NEOASTRA_OK);
    neoastra_view_release(view);
    neoastra_window_release(window);
    neoastra_environment_release(environment);
}

// The views of a window are stacked in the order they were created in, the newest on top, and each of them goes away
// without taking another one with it. A second view used to take the place of the first, whose widget was destroyed.
void test_views_share_their_window(neoastra_app_t* app) {
    auto* environment = create_environment(app, "", true);
    auto* window = create_window(app);
    auto* host = gtk_window(window);
    auto* first = create_view(environment, window);
    auto* first_widget = GTK_WIDGET(web_view(first));
    auto* stack = gtk_window_get_child(host);
    auto* second = create_view(environment, window);
    auto* second_widget = GTK_WIDGET(web_view(second));
    assert(gtk_window_get_child(host) == stack);
    // The first view is still there, and still the widget that its handle names.
    assert(GTK_WIDGET(web_view(first)) == first_widget);
    assert(gtk_widget_get_parent(first_widget) == stack && gtk_widget_get_parent(second_widget) == stack);
    // A later sibling is drawn over an earlier one.
    assert(gtk_widget_get_first_child(stack) == first_widget && gtk_widget_get_next_sibling(first_widget) == second_widget);
    auto* third = create_view(environment, window);
    auto* third_widget = GTK_WIDGET(web_view(third));
    assert(gtk_widget_get_last_child(stack) == third_widget);
    // A view in the middle of the stack leaves the others where they are.
    neoastra_view_release(second);
    assert(gtk_widget_get_first_child(stack) == first_widget && gtk_widget_get_next_sibling(first_widget) == third_widget);
    neoastra_view_release(first);
    assert(gtk_widget_get_first_child(stack) == third_widget && gtk_widget_get_next_sibling(third_widget) == nullptr);
    neoastra_view_release(third);
    assert(gtk_window_get_child(host) == stack && gtk_widget_get_first_child(stack) == nullptr);
    neoastra_window_release(window);
    neoastra_environment_release(environment);
}

// The menu presenter of the managed side (LinuxMenus.cs) puts a box, which it marks, between a window and its content
// for a menu bar above the content. The views of the window stay together inside that box, whether the box came after
// the first of them or before it.
void test_views_follow_the_menu_host(neoastra_app_t* app) {
    auto* environment = create_environment(app, "", true);
    for (int box_first = 0; box_first < 2; box_first++) {
        auto* window = create_window(app);
        auto* host = gtk_window(window);
        auto* first = box_first ? nullptr : create_view(environment, window);
        // What the menu presenter does for the first menu bar of a window.
        auto* content = gtk_window_get_child(host);
        if (content) g_object_ref(content);
        gtk_window_set_child(host, nullptr);
        auto* box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
        g_object_set_data(G_OBJECT(box), "neoastra.menu-host", box);
        gtk_window_set_child(host, box);
        if (content) {
            gtk_box_append(GTK_BOX(box), content);
            g_object_unref(content);
        }
        auto* bar = gtk_label_new("menu bar");
        gtk_box_prepend(GTK_BOX(box), bar);

        auto* second = create_view(environment, window);
        // The box is still the child of the window, with the menu bar above the one stack.
        assert(gtk_window_get_child(host) == box);
        auto* stack = gtk_widget_get_parent(GTK_WIDGET(web_view(second)));
        assert(GTK_IS_OVERLAY(stack) && gtk_widget_get_parent(stack) == box);
        assert(gtk_widget_get_first_child(box) == bar && gtk_widget_get_next_sibling(bar) == stack && gtk_widget_get_next_sibling(stack) == nullptr);
        if (first) {
            assert(gtk_widget_get_parent(GTK_WIDGET(web_view(first))) == stack);
            neoastra_view_release(first);
        }
        neoastra_view_release(second);
        assert(gtk_widget_get_first_child(stack) == nullptr);
        neoastra_window_release(window);
    }
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

    test_window_reports_the_size_it_has(app);
    test_view_leaves_its_window(app);
    test_views_share_their_window(app);
    test_views_follow_the_menu_host(app);
    test_tab_key_setting_reaches_the_view(app);
    test_history_navigation_setting_reaches_the_view(app);
    test_user_data_roots(app, base);

    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);

    std::error_code ignored;
    std::filesystem::remove_all(base, ignored);
    return 0;
}
