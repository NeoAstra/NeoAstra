#include "neoastra.h"

#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstdint>
#include <cstdio>
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

// The application is attached, so the test pumps the main run loop that delivers native completions.
void wait_for(const completion& state) {
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:60];
    while (!state.done) {
        assert(deadline.timeIntervalSinceNow > 0);
        [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.01]];
    }
}

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

// Returns the store that a view of the environment keeps its website data in.
WKWebsiteDataStore* view_data_store(neoastra_environment_t* environment, neoastra_window_t* window, neoastra_profile_t* profile = nullptr) {
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
    auto* view = static_cast<neoastra_view_t*>(state.value);
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_view_get_native_handle(view, NEOASTRA_NATIVE_HANDLE_WKWEBVIEW, &handle) == NEOASTRA_OK);
    WKWebsiteDataStore* store = ((__bridge WKWebView*)handle.value).configuration.websiteDataStore;
    assert(store != nil);
    neoastra_view_release(view);
    return store;
}

// The roots only name a store: the backend never creates or reads these directories.
const std::string first_root = "/private/tmp/neoastra-data-store-tests/a";
const std::string second_root = "/private/tmp/neoastra-data-store-tests/b";
// The first root again, through a symbolic link, a parent segment, and a trailing separator.
const std::string first_root_alias = "/tmp/neoastra-data-store-tests/unused/../a/";
// A root must keep its identifier across runs and releases: it is all that finds its stored data again.
NSString* const first_identifier = @"60647C65-F420-8260-A343-0DC62E91D239";
NSString* const second_identifier = @"49015C4B-A943-82DD-867A-7AF13E3163E7";

const std::string cookie_name = "neoastra-data-store-test";
const std::string cookie_domain = "neoastra.test";
const std::string cookie_path = "/";

neoastra_cookie_t test_cookie(const std::string& value) {
    neoastra_cookie_t cookie{};
    cookie.size = sizeof(cookie);
    cookie.version = 1;
    cookie.name = string_view(cookie_name);
    cookie.value = string_view(value);
    cookie.domain = string_view(cookie_domain);
    cookie.path = string_view(cookie_path);
    cookie.expires_unix_ms = static_cast<int64_t>(([NSDate date].timeIntervalSince1970 + 3600) * 1000);
    return cookie;
}

void set_cookie(neoastra_profile_t* profile, const std::string& value) {
    const auto cookie = test_cookie(value);
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

// Leaves nothing behind for the next run. A store that WebKit still considers in use is simply reused then.
void remove_data_store(NSString* identifier) API_AVAILABLE(macos(14.0)) {
    completion state;
    auto* removal = &state;
    [WKWebsiteDataStore removeDataStoreForIdentifier:[[NSUUID alloc] initWithUUIDString:identifier] completionHandler:^(NSError*) { removal->done = true; }];
    wait_for(state);
}

} // namespace

int main() {
    @autoreleasepool {
        neoastra_app_options_t app_options{};
        app_options.size = sizeof(app_options);
        app_options.version = 1;
        app_options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
        neoastra_app_t* app = nullptr;
        assert(neoastra_app_attach(&app_options, &app, nullptr) == NEOASTRA_OK && app != nullptr);

        neoastra_window_options_t window_options{};
        window_options.size = sizeof(window_options);
        window_options.version = 1;
        window_options.bounds = {100, 100, 640, 480};
        window_options.flags = 3;
        neoastra_window_t* window = nullptr;
        assert(neoastra_app_create_window(app, &window_options, &window, nullptr) == NEOASTRA_OK && window != nullptr);

        auto* first = create_environment(app, first_root);
        auto* first_again = create_environment(app, first_root_alias);
        auto* second = create_environment(app, second_root);
        auto* unrooted = create_environment(app, "");
        auto* private_environment = create_environment(app, first_root, true);

        auto* first_profile = create_profile(first);
        auto* first_again_profile = create_profile(first_again);
        auto* second_profile = create_profile(second);
        auto* unrooted_profile = create_profile(unrooted);
        auto* ephemeral_profile = create_profile(first, true);
        auto* private_profile = create_profile(private_environment);

        WKWebsiteDataStore* first_store = view_data_store(first, window);
        WKWebsiteDataStore* second_store = view_data_store(second, window);
        WKWebsiteDataStore* unrooted_store = view_data_store(unrooted, window);

        // Without a root, and in private mode, the environment keeps the stores it always had.
        assert(unrooted_store == [WKWebsiteDataStore defaultDataStore]);
        assert(!view_data_store(private_environment, window).persistent);

        // A profile that is not ephemeral is the environment's own store, including in a private environment.
        assert(view_data_store(first, window, first_profile) == first_store);
        assert(view_data_store(unrooted, window, unrooted_profile) == unrooted_store);
        assert(!view_data_store(first, window, ephemeral_profile).persistent);
        assert(!view_data_store(private_environment, window, private_profile).persistent);

        if (@available(macOS 14.0, *)) {
            // Each root has a persistent store of its own, which a second environment on the same root shares.
            assert(first_store.persistent && second_store.persistent);
            assert(first_store != unrooted_store && second_store != unrooted_store && first_store != second_store);
            assert(view_data_store(first_again, window) == first_store);

            assert([first_store.identifier isEqual:[[NSUUID alloc] initWithUUIDString:first_identifier]]);
            assert([second_store.identifier isEqual:[[NSUUID alloc] initWithUUIDString:second_identifier]]);

            // What one root stores is visible through that root only, and clearing it leaves the other roots alone.
            set_cookie(first_profile, "first-root");
            set_cookie(second_profile, "second-root");
            assert(has_cookie(first_again_profile, "first-root") && !has_cookie(first_again_profile, "second-root"));
            assert(has_cookie(second_profile, "second-root") && !has_cookie(second_profile, "first-root"));
            assert(!has_cookie(unrooted_profile, "first-root") && !has_cookie(unrooted_profile, "second-root"));
            clear_cookies(first_profile);
            assert(!has_cookie(first_again_profile, "first-root") && has_cookie(second_profile, "second-root"));
            clear_cookies(second_profile);
            assert(!has_cookie(second_profile, "second-root"));
        } else {
            // Stores selected by an identifier need macOS 14: before it, every persistent environment shares the default store.
            assert(first_store == unrooted_store && second_store == unrooted_store);
            std::puts("SKIP user-data-root isolation requires macOS 14 or later");
        }

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
        neoastra_window_release(window);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    // Every view and store is released by now, which WebKit requires before it removes a store.
    @autoreleasepool {
        if (@available(macOS 14.0, *)) {
            remove_data_store(first_identifier);
            remove_data_store(second_identifier);
        }
    }
    return 0;
}
