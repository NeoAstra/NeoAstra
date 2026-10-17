#include "neoastra.h"

#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>
#import <dispatch/dispatch.h>
#import <objc/runtime.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstdio>
#include <string>

namespace {

struct completion {
    bool done{};
    neoastra_result_t result{NEOASTRA_ERROR_UNKNOWN};
    void* value{};
};

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
void NEOASTRA_CALL view_created(void* context, neoastra_result_t result, neoastra_view_t* value, const neoastra_error_t*) { complete(context, result, value); }
void NEOASTRA_CALL navigation_completed(void* context, const neoastra_event_t* event) {
    if (event->header.type == NEOASTRA_EVENT_NAVIGATION_COMPLETED) complete(context, NEOASTRA_OK, nullptr);
    else if (event->header.type == NEOASTRA_EVENT_NAVIGATION_FAILED || event->header.type == NEOASTRA_EVENT_WEB_PROCESS_TERMINATED)
        complete(context, NEOASTRA_ERROR_NATIVE_FAILURE, nullptr);
}

neoastra_view_t* create_view(neoastra_environment_t* environment, neoastra_window_t* window) {
    neoastra_view_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.window = window;
    options.fill_parent = 1;
    completion state;
    assert(neoastra_environment_create_view_async(environment, &options, view_created, &state, nullptr, nullptr) == NEOASTRA_OK);
    wait_for(state);
    assert(state.result == NEOASTRA_OK && state.value != nullptr);
    return static_cast<neoastra_view_t*>(state.value);
}

WKWebView* web_view(neoastra_view_t* view) {
    neoastra_native_handle_t handle{};
    handle.size = sizeof(handle);
    handle.version = 1;
    assert(neoastra_view_get_native_handle(view, NEOASTRA_NATIVE_HANDLE_WKWEBVIEW, &handle) == NEOASTRA_OK);
    return (__bridge WKWebView*)handle.value;
}

// Observe the public KVC entry point only during the synchronous setting call. The inspectable property must use its
// public setter, independently of the existing best-effort private preference used for the local context-menu item.
struct kvc_observer {
    Class type;
    IMP original;
    IMP replacement;
    const char* encoding;

    kvc_observer(id observed, unsigned* writes, bool unavailable = false) : type(object_getClass(observed)) {
        const auto method = class_getInstanceMethod(type, @selector(setValue:forKey:));
        original = method_getImplementation(method);
        encoding = method_getTypeEncoding(method);
        const auto invoke = reinterpret_cast<void (*)(id, SEL, id, NSString*)>(original);
        replacement = imp_implementationWithBlock(^(id receiver, id value, NSString* key) {
            if (receiver == observed) {
                ++*writes;
                if (unavailable) [NSException raise:NSUndefinedKeyException format:@"Simulated unavailable WebKit preference"];
            }
            invoke(receiver, @selector(setValue:forKey:), value, key);
        });
        class_replaceMethod(type, @selector(setValue:forKey:), replacement, encoding);
    }

    ~kvc_observer() {
        class_replaceMethod(type, @selector(setValue:forKey:), original, encoding);
        imp_removeBlock(replacement);
    }
};

neoastra_result_t set_devtools(neoastra_view_t* view, bool enabled) {
    WKWebView* web = web_view(view);
    unsigned writes = 0;
    neoastra_result_t result;
    {
        kvc_observer web_observer(web, &writes);
        result = neoastra_view_set_setting(view, NEOASTRA_VIEW_SETTING_DEVTOOLS, enabled ? 1 : 0);
    }
    if (writes) std::fprintf(stderr, "DevTools used %u string-keyed WKWebView writes instead of the public inspectable property\n", writes);
    assert(writes == 0);
    return result;
}

void load_page(neoastra_view_t* view) {
    const std::string html = "<!doctype html><title>NeoAstra inspector test</title><p>Inspect this paragraph</p>";
    const neoastra_string_view_t text{reinterpret_cast<const uint8_t*>(html.data()), html.size()};
    completion navigation;
    assert(neoastra_view_set_event_callback(view, navigation_completed, &navigation) == NEOASTRA_OK);
    assert(neoastra_view_load_html(view, text, {}, nullptr) == NEOASTRA_OK);
    wait_for(navigation);
    assert(navigation.result == NEOASTRA_OK);
    assert(neoastra_view_set_event_callback(view, nullptr, nullptr) == NEOASTRA_OK);
}

// Exercise a real WebKit-generated menu without calling its private action. The identifier is observed here only to
// guard the existing best-effort local inspection behavior; it is not a public contract or used by production code.
bool has_inspect_element(WKWebView* web) {
    __block bool seen = false, closed = false, inspect_element = false;
    id observer = [NSNotificationCenter.defaultCenter addObserverForName:NSMenuDidBeginTrackingNotification object:nil queue:nil usingBlock:^(NSNotification* notification) {
        if (seen) return;
        seen = true;
        NSMenu* menu = notification.object;
        for (NSMenuItem* item in menu.itemArray)
            if ([item.identifier isEqualToString:@"WKMenuItemIdentifierInspectElement"]) inspect_element = true;
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 20 * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{
            [menu cancelTracking];
            closed = true;
        });
    }];
    NSEvent* event = [NSEvent mouseEventWithType:NSEventTypeRightMouseDown location:[web convertPoint:NSMakePoint(30, 30) toView:nil] modifierFlags:0
        timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:web.window.windowNumber context:nil eventNumber:1 clickCount:1 pressure:0];
    assert(event != nil);
    [web rightMouseDown:event];
    NSDate* deadline = [NSDate dateWithTimeIntervalSinceNow:15];
    while (!closed) {
        assert(deadline.timeIntervalSinceNow > 0);
        [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.01]];
    }
    [NSNotificationCenter.defaultCenter removeObserver:observer];
    return inspect_element;
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
        [NSApp finishLaunching];

        neoastra_window_options_t window_options{};
        window_options.size = sizeof(window_options);
        window_options.version = 1;
        window_options.bounds = {100, 100, 640, 480};
        window_options.flags = 7;
        neoastra_window_t* window = nullptr;
        assert(neoastra_app_create_window(app, &window_options, &window, nullptr) == NEOASTRA_OK && window != nullptr);

        neoastra_environment_options_t environment_options{};
        environment_options.size = sizeof(environment_options);
        environment_options.version = 1;
        environment_options.private_mode = 1;
        completion environment_state;
        assert(neoastra_environment_create_async(app, &environment_options, environment_created, &environment_state, nullptr, nullptr) == NEOASTRA_OK);
        wait_for(environment_state);
        assert(environment_state.result == NEOASTRA_OK && environment_state.value != nullptr);
        auto* environment = static_cast<neoastra_environment_t*>(environment_state.value);

        auto* first = create_view(environment, window);
        auto* second = create_view(environment, window);
        WKWebView* first_web = web_view(first);
        WKWebView* second_web = web_view(second);
        second_web.hidden = YES;
        load_page(first);
        assert(neoastra_window_activate(window) == NEOASTRA_OK);
        assert(!has_inspect_element(first_web));

        if (@available(macOS 13.3, *)) {
            // Inspection is off by default, and opting in one view never exposes another view of the same environment.
            assert(!first_web.inspectable && !second_web.inspectable);
            assert(set_devtools(first, false) == NEOASTRA_OK);
            assert(!first_web.inspectable);
            assert(set_devtools(first, true) == NEOASTRA_OK);
            assert(first_web.inspectable && !second_web.inspectable);
            assert(has_inspect_element(first_web));

            // The private local preference can disappear. It must not block the supported Safari opt-in, and its
            // failure must never leave the public flag enabled when the host asks to turn inspection off.
            unsigned preference_writes = 0;
            {
                kvc_observer unavailable(first_web.configuration.preferences, &preference_writes, true);
                assert(set_devtools(first, false) == NEOASTRA_ERROR_NOT_SUPPORTED);
                assert(!first_web.inspectable);
                assert(set_devtools(first, true) == NEOASTRA_OK);
                assert(first_web.inspectable && !second_web.inspectable);
            }
            assert(preference_writes == 2);

            // Application shells can keep the context menu and other browser shortcuts off. Inspection remains
            // available through Safari, but enabling it must not restore menus the host has explicitly hidden.
            assert(neoastra_view_set_setting(first, NEOASTRA_VIEW_SETTING_DEFAULT_CONTEXT_MENUS, 0) == NEOASTRA_OK);
            assert(neoastra_view_set_setting(first, NEOASTRA_VIEW_SETTING_BROWSER_ACCELERATOR_KEYS, 0) == NEOASTRA_OK);
            assert(set_devtools(first, true) == NEOASTRA_OK && first_web.inspectable);
            NSMenu* menu = [NSMenu new];
            [menu addItemWithTitle:@"Test item" action:nil keyEquivalent:@""];
            NSEvent* event = [NSEvent mouseEventWithType:NSEventTypeRightMouseDown location:NSMakePoint(10, 10) modifierFlags:0
                timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:first_web.window.windowNumber context:nil eventNumber:1 clickCount:1 pressure:0];
            assert(event != nil);
            [first_web willOpenMenu:menu withEvent:event];
            assert(menu.numberOfItems == 0);

            // Public WKWebView APIs expose inspectability, not a command that opens the inspector or a responder
            // action to which an application could bind Option+Command+I. Do not pretend the open call succeeded.
            assert(neoastra_view_open_devtools(first) == NEOASTRA_ERROR_NOT_SUPPORTED);
            assert(set_devtools(first, false) == NEOASTRA_OK);
            assert(!first_web.inspectable && !second_web.inspectable);
            assert(neoastra_view_open_devtools(first) == NEOASTRA_ERROR_NOT_SUPPORTED);
            assert(neoastra_view_set_setting(first, NEOASTRA_VIEW_SETTING_DEFAULT_CONTEXT_MENUS, 1) == NEOASTRA_OK);
            assert(!has_inspect_element(first_web));
        } else {
            assert(set_devtools(first, false) == NEOASTRA_OK);
            assert(set_devtools(first, true) == NEOASTRA_OK);
            assert(has_inspect_element(first_web));
            assert(set_devtools(first, false) == NEOASTRA_OK);
            assert(!has_inspect_element(first_web));
            assert(neoastra_view_open_devtools(first) == NEOASTRA_ERROR_NOT_SUPPORTED);
        }

        neoastra_view_release(second);
        neoastra_view_release(first);
        neoastra_environment_release(environment);
        neoastra_window_release(window);
        assert(neoastra_app_detach(app, nullptr) == NEOASTRA_OK);
        neoastra_app_release(app);
    }
    return 0;
}
