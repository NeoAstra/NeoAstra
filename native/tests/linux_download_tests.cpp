#include "../src/common/native_internal.hpp"

#include <gtk/gtk.h>
#include <webkit/webkit.h>
#include <libsoup/soup.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <filesystem>
#include <string>
#include <system_error>
#include <thread>

namespace {

template<typename TCondition> void wait_until(TCondition&& condition) {
    const auto deadline = g_get_monotonic_time() + 60 * G_USEC_PER_SEC;
    while (!condition()) {
        assert(g_get_monotonic_time() < deadline);
        if (!g_main_context_iteration(nullptr, FALSE)) g_usleep(1000);
    }
}

struct creation { void* value{}; bool done{}; };
void NEOASTRA_CALL environment_created(void* context, neoastra_result_t result, neoastra_environment_t* value, const neoastra_error_t*) {
    auto* created = static_cast<creation*>(context);
    assert(result == NEOASTRA_OK && value);
    created->value = value;
    created->done = true;
}
void NEOASTRA_CALL view_created(void* context, neoastra_result_t result, neoastra_view_t* value, const neoastra_error_t*) {
    auto* created = static_cast<creation*>(context);
    assert(result == NEOASTRA_OK && value);
    created->value = value;
    created->done = true;
}

void serve_download(SoupServer*, SoupServerMessage* message, const char*, GHashTable*, void*) {
    const std::string body = "NeoAstra deferred download";
    soup_server_message_set_status(message, SOUP_STATUS_OK, nullptr);
    soup_message_headers_replace(soup_server_message_get_response_headers(message), "Content-Disposition", "attachment; filename=server-name.bin");
    soup_server_message_set_response(message, "application/octet-stream", SOUP_MEMORY_COPY, body.data(), body.size());
}

neoastra_result_t answer(neoastra_decision_t* decision, neoastra_decision_action_t action, const std::string& path = {}) {
    neoastra_decision_response_t response{};
    response.size = sizeof(response);
    response.version = 1;
    response.action = action;
    response.text = neo_string_view(path);
    return neoastra_decision_complete(decision, &response, nullptr);
}

struct download_events {
    neoastra_decision_t* decision{}; // The host's retained reference, independent of the signal and the deferral.
    neoastra_download_t* download{};
    unsigned started{}, completed{}, decision_destroyed{};
    std::string immediate_destination;
};

void NEOASTRA_CALL record_download(void* context, const neoastra_event_t* event) {
    auto* events = static_cast<download_events*>(context);
    if (event->header.type == NEOASTRA_EVENT_DOWNLOAD_REQUESTED) {
        assert(!events->decision && event->decision && event->download);
        neoastra_decision_retain(event->decision);
        neoastra_download_retain(event->download);
        events->decision = event->decision;
        events->download = event->download;
        // A destruction observer catches the original unique_ptr deletion before a late completion touches freed memory.
        event->decision->popup_context = &events->decision_destroyed;
        event->decision->popup_context_release = [](void* value) noexcept { ++*static_cast<unsigned*>(value); };
        assert(neoastra_decision_defer(event->decision) == NEOASTRA_OK);
        if (!events->immediate_destination.empty()) assert(answer(event->decision, NEOASTRA_DECISION_DOWNLOAD, events->immediate_destination) == NEOASTRA_OK);
    } else if (event->header.type == NEOASTRA_EVENT_DOWNLOAD_STARTED) {
        ++events->started;
    } else if (event->header.type == NEOASTRA_EVENT_DOWNLOAD_COMPLETED) {
        ++events->completed;
    }
}

enum class outcome { inline_accept, accept, default_destination, cancel_policy, cancel_download, timeout, release_view, release_host_reference, invalid_path };

void test_deferred_download(neoastra_environment_t* environment, neoastra_window_t* window,
                            const std::string& uri, const std::filesystem::path& root, outcome requested) {
    neoastra_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.window = window;
    options.fill_parent = 1;
    options.decision_timeout_ms = 250;
    creation created;
    assert(neoastra_environment_create_view_async(environment, &options, view_created, &created, nullptr, nullptr) == NEOASTRA_OK);
    wait_until([&] { return created.done; });
    auto* view = static_cast<neoastra_view_t*>(created.value);
    const auto destination = requested == outcome::default_destination ? root / "downloads/server-name.bin" : root / "explicit path \xC3\xA9.bin";
    download_events events;
    if (requested == outcome::inline_accept) events.immediate_destination = destination.string();
    assert(neoastra_view_set_event_callback(view, record_download, &events) == NEOASTRA_OK);
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_view_get_native_handle(view, NEOASTRA_NATIVE_HANDLE_WEBKITGTK_WEBVIEW, &handle) == NEOASTRA_OK);
    auto* native = webkit_web_view_download_uri(WEBKIT_WEB_VIEW(handle.value), uri.c_str());
    assert(native);
    wait_until([&] { return events.decision; });

    // Returning from decide-destination must neither force cancellation nor delete a retained decision.
    assert(events.decision_destroyed == 0);
    if (requested == outcome::inline_accept) {
        assert(events.started == 1);
        assert(events.decision->state.load() == neo_decision_state::completed);
        assert(webkit_download_get_destination(native) && destination.string() == webkit_download_get_destination(native));
    } else {
        assert(events.completed == 0 && events.started == 0);
        assert(events.decision->state.load() == neo_decision_state::deferred);
        assert(events.download->state.load() == NEOASTRA_DOWNLOAD_REQUESTED);
        assert(!webkit_download_get_destination(native));
    }

    if (requested == outcome::accept || requested == outcome::default_destination) {
        assert(answer(events.decision, requested == outcome::accept ? NEOASTRA_DECISION_DOWNLOAD : NEOASTRA_DECISION_DEFAULT, destination) == NEOASTRA_OK);
    } else if (requested == outcome::cancel_policy) {
        assert(answer(events.decision, NEOASTRA_DECISION_CANCEL) == NEOASTRA_OK);
    } else if (requested == outcome::cancel_download) {
        assert(neoastra_download_cancel(events.download) == NEOASTRA_OK);
        assert(answer(events.decision, NEOASTRA_DECISION_DOWNLOAD, destination) == NEOASTRA_ERROR_INVALID_STATE);
    } else if (requested == outcome::release_view) {
        neoastra_view_release(view);
        view = nullptr;
        assert(answer(events.decision, NEOASTRA_DECISION_DOWNLOAD, destination) == NEOASTRA_ERROR_INVALID_STATE);
    } else if (requested == outcome::release_host_reference) {
        neoastra_decision_release(events.decision);
        events.decision = nullptr;
        assert(events.decision_destroyed == 0); // The native deferral and timeout still own it.
    } else if (requested == outcome::invalid_path) {
        assert(answer(events.decision, NEOASTRA_DECISION_DOWNLOAD, "relative.bin") == NEOASTRA_OK);
    }

    if (requested == outcome::release_view) {
        // A zero-reference view is tearing down: native cancellation still completes, but must not resurrect it to emit.
        wait_until([&] { return events.download->state.load() == NEOASTRA_DOWNLOAD_CANCELED; });
        assert(events.completed == 0);
    } else {
        wait_until([&] { return events.completed != 0; });
        assert(events.completed == 1);
    }
    neoastra_download_info_t info{};
    info.size = sizeof(info);
    info.version = 1;
    assert(neoastra_download_get_info(events.download, &info) == NEOASTRA_OK);
    const bool accepted = requested == outcome::inline_accept || requested == outcome::accept || requested == outcome::default_destination;
    assert(info.state == (accepted ? NEOASTRA_DOWNLOAD_COMPLETED : NEOASTRA_DOWNLOAD_CANCELED));
    assert(events.started == (accepted ? 1u : 0u));
    if (accepted) {
        assert(std::string(reinterpret_cast<const char*>(info.destination_path.data), static_cast<size_t>(info.destination_path.length)) == destination.string());
        gchar* contents{};
        gsize length{};
        assert(g_file_get_contents(destination.c_str(), &contents, &length, nullptr));
        assert(std::string(contents, length) == "NeoAstra deferred download");
        g_free(contents);
        assert(std::filesystem::remove(destination));
    } else {
        assert(!std::filesystem::exists(destination));
    }
    if (requested == outcome::timeout) {
        assert(answer(events.decision, NEOASTRA_DECISION_DOWNLOAD, destination) == NEOASTRA_ERROR_INVALID_STATE);
    }
    assert(events.download->platform == nullptr && events.download->view == nullptr);
    assert(events.download->references.load() == 1); // Only this host's reference remains after terminal cleanup.
    neoastra_download_release(events.download);
    if (events.decision) {
        // A SafeHandle finalizer can release the final host reference off the UI thread, after native completion/expiry.
        std::thread releasing([decision = events.decision] { neoastra_decision_release(decision); });
        releasing.join();
    }
    // A completion and final host release must eventually free the timed decision, not leak its deferral reference.
    wait_until([&] { return events.decision_destroyed != 0; });
    assert(events.decision_destroyed == 1 && events.completed == (requested == outcome::release_view ? 0u : 1u));
    g_object_unref(native);
    if (view) neoastra_view_release(view);
}

} // namespace

int main() {
    auto* created = g_dir_make_tmp("neoastra-linux-download-tests-XXXXXX", nullptr);
    assert(created);
    const std::filesystem::path root = std::filesystem::canonical(created);
    g_free(created);
    // Even Default writes only inside this test's directory, never to the user's real Downloads folder.
    assert(g_mkdir_with_parents((root / "config").c_str(), 0700) == 0);
    assert(g_mkdir_with_parents((root / "downloads").c_str(), 0700) == 0);
    const auto directories = "XDG_DOWNLOAD_DIR=\"" + (root / "downloads").string() + "\"\n";
    assert(g_file_set_contents((root / "config/user-dirs.dirs").c_str(), directories.c_str(), -1, nullptr));
    g_setenv("XDG_CONFIG_HOME", (root / "config").c_str(), TRUE);
    g_setenv("XDG_DATA_HOME", (root / "data").c_str(), TRUE);
    g_setenv("XDG_CACHE_HOME", (root / "cache").c_str(), TRUE);
    g_setenv("WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS", "1", TRUE);

    neoastra_app_options_t app_options{};
    app_options.size = sizeof(app_options);
    app_options.version = 1;
    app_options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    neoastra_app_t* app{};
    assert(neoastra_app_attach(&app_options, &app, nullptr) == NEOASTRA_OK);
    neoastra_environment_options_t environment_options{};
    environment_options.size = sizeof(environment_options);
    environment_options.version = 1;
    environment_options.private_mode = 1;
    creation environment_created_state;
    assert(neoastra_environment_create_async(app, &environment_options, environment_created, &environment_created_state, nullptr, nullptr) == NEOASTRA_OK);
    wait_until([&] { return environment_created_state.done; });
    auto* environment = static_cast<neoastra_environment_t*>(environment_created_state.value);
    neoastra_window_options_t window_options{};
    window_options.size = sizeof(window_options);
    window_options.version = 1;
    window_options.bounds = {0, 0, 640, 480};
    neoastra_window_t* window{};
    assert(neoastra_app_create_window(app, &window_options, &window, nullptr) == NEOASTRA_OK);

    auto* server = soup_server_new(nullptr, nullptr);
    soup_server_add_handler(server, nullptr, serve_download, nullptr, nullptr);
    assert(soup_server_listen_local(server, 0, SOUP_SERVER_LISTEN_IPV4_ONLY, nullptr));
    auto* uris = soup_server_get_uris(server);
    assert(uris);
    auto* raw_uri = g_uri_to_string(static_cast<GUri*>(uris->data));
    const std::string uri(raw_uri);
    g_free(raw_uri);
    g_slist_free_full(uris, reinterpret_cast<GDestroyNotify>(g_uri_unref));

    for (auto requested : {outcome::inline_accept, outcome::accept, outcome::default_destination, outcome::cancel_policy, outcome::cancel_download,
                           outcome::timeout, outcome::release_view, outcome::release_host_reference, outcome::invalid_path})
        test_deferred_download(environment, window, uri, root, requested);

    // Unrooted environments share WebKit's default session, but their multiple session signal handlers must not create
    // multiple decisions/lifecycle wrappers for one download. record_download asserts that the policy is asked only once.
    creation shared_environments[2];
    environment_options.private_mode = 0;
    for (auto& state : shared_environments) {
        assert(neoastra_environment_create_async(app, &environment_options, environment_created, &state, nullptr, nullptr) == NEOASTRA_OK);
        wait_until([&] { return state.done; });
    }
    test_deferred_download(static_cast<neoastra_environment_t*>(shared_environments[0].value), window, uri, root, outcome::accept);
    for (auto& state : shared_environments) neoastra_environment_release(static_cast<neoastra_environment_t*>(state.value));

    soup_server_disconnect(server);
    g_object_unref(server);
    neoastra_window_release(window);
    neoastra_environment_release(environment);
    assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
    neoastra_app_release(app);
    std::error_code ignored;
    std::filesystem::remove_all(root, ignored);
}
