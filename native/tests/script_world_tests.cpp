// The protected channel of an isolated-world script, through the common layer and a backend that records what reaches it:
// the versions of neoastra_script_options_t, the names of a world and of its binding, the messages of the world and to it,
// and the capabilities that fail closed where the backend has no isolated world.
#include "../src/common/native_internal.hpp"

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cstddef>
#include <cstdio>
#include <string>
#include <vector>

namespace completion_test_backend {
extern uint32_t script_calls;
extern neoastra_script_options_t last_script_options;
extern std::string last_script_world;
extern std::string last_script_binding;
extern uint32_t script_message_calls;
extern std::string last_script_message_identifier;
extern std::string last_script_message;
extern bool script_worlds_supported;
}

namespace {
namespace backend = completion_test_backend;

neoastra_string_view_t view_of(const std::string& value) { return neo_string_view(value); }

neoastra_app_t* create_app() {
    neoastra_app_options_t options{};
    options.size = sizeof(options);
    options.version = 1;
    options.shutdown_mode = NEOASTRA_APP_SHUTDOWN_EXPLICIT;
    options.maximum_pending_dispatches = 16;
    neoastra_app_t* app{};
    assert(neoastra_app_attach(&options, &app, nullptr) == NEOASTRA_OK);
    return app;
}

void NEOASTRA_CALL ignore_script(void*, neoastra_result_t, neoastra_string_view_t, const neoastra_error_t*) { }

// Adds a script with the options and reports the result of the call; the recording backend refuses every script it gets.
neoastra_result_t add(neoastra_view_t* view, const neoastra_script_options_t& options) {
    neoastra_operation_t* operation{};
    neoastra_error_t* error{};
    static const std::string script("void 0;");
    const auto result = neoastra_view_add_script_async(view, neo_string_view(script), &options, ignore_script, nullptr, &operation, &error);
    if (error) neoastra_error_release(error);
    if (operation) neoastra_operation_release(operation);
    return result;
}

neoastra_script_options_t channel_options(const std::string& world, const std::string& binding) {
    neoastra_script_options_t options{};
    options.size = sizeof(options);
    options.version = 2;
    options.main_frame_only = 1;
    options.isolated_world = 1;
    options.world_name = view_of(world);
    options.binding_name = view_of(binding);
    return options;
}

// What reaches the backend: the call is refused there, so the common layer let it through.
bool reached_backend(neoastra_view_t* view, const neoastra_script_options_t& options) {
    const auto before = backend::script_calls;
    const auto result = add(view, options);
    const auto reached = backend::script_calls == before + 1;
    assert(reached == (result == NEOASTRA_ERROR_NOT_SUPPORTED));
    if (!reached) assert(result == NEOASTRA_ERROR_INVALID_ARGUMENT);
    return reached;
}

void test_version_one_callers_keep_their_size() {
    auto* app = create_app();
    auto* environment = new neoastra_environment(app);
    auto* view = new neoastra_view(environment);

    // A caller built against the first version passes the size it knows, which ends before binding_name.
    neoastra_script_options_t options{};
    options.size = static_cast<uint32_t>(offsetof(neoastra_script_options_t, binding_name));
    options.version = 1;
    options.main_frame_only = 1;
    assert(reached_backend(view, options));
    assert(backend::last_script_options.size == sizeof(neoastra_script_options_t));
    assert(backend::last_script_binding.empty() && backend::last_script_world.empty());

    // Whatever lies after the first version is not read for a first-version caller, even with the larger size.
    const char garbage[] = "not-read";
    neoastra_script_options_t larger{};
    larger.size = sizeof(larger);
    larger.version = 1;
    larger.isolated_world = 1;
    larger.binding_name = {reinterpret_cast<const uint8_t*>(garbage), sizeof(garbage) - 1};
    assert(reached_backend(view, larger));
    assert(backend::last_script_binding.empty());
    assert(backend::last_script_world == "neoastra.default");

    // A second-version caller passes the whole structure, and no later version is known.
    auto truncated = channel_options("codealta.annotations", "codeAltaAnnotations");
    truncated.size = static_cast<uint32_t>(offsetof(neoastra_script_options_t, binding_name));
    assert(!reached_backend(view, truncated));
    auto future = channel_options("codealta.annotations", "codeAltaAnnotations");
    future.version = 3;
    assert(!reached_backend(view, future));

    neoastra_view_release(view);
    neoastra_environment_release(environment);
    neoastra_app_release(app);
}

void test_names_are_unambiguous_and_never_of_the_page() {
    auto* app = create_app();
    auto* environment = new neoastra_environment(app);
    auto* view = new neoastra_view(environment);

    assert(reached_backend(view, channel_options("codealta.annotations", "codeAltaAnnotations")));
    assert(backend::last_script_world == "codealta.annotations" && backend::last_script_binding == "codeAltaAnnotations");

    // A binding belongs to an isolated world with a name of its own, in the main frame only.
    auto page = channel_options("codealta.annotations", "codeAltaAnnotations");
    page.isolated_world = 0;
    assert(!reached_backend(view, page));
    assert(!reached_backend(view, channel_options("", "codeAltaAnnotations")));
    auto frames = channel_options("codealta.annotations", "codeAltaAnnotations");
    frames.main_frame_only = 0;
    assert(!reached_backend(view, frames));

    for (const std::string world : {std::string("page world"), std::string("x/y"), std::string(129, 'w'), std::string("é")})
        assert(!reached_backend(view, channel_options(world, "codeAltaAnnotations")));
    for (const std::string binding : {std::string("1binding"), std::string("a-b"), std::string(65, 'b'), std::string("_neoastra_transport_v1"),
                                      std::string("_neoastraAnything"), std::string("webkit"), std::string("chrome"), std::string("window"), std::string("document")})
        assert(!reached_backend(view, channel_options("codealta.annotations", binding)));

    // An isolated world without a binding may keep the default name; an explicit name must be valid all the same.
    neoastra_script_options_t isolated{};
    isolated.size = sizeof(isolated);
    isolated.version = 2;
    isolated.isolated_world = 1;
    assert(reached_backend(view, isolated));
    assert(backend::last_script_world == "neoastra.default");
    const std::string invalid_world("a b");
    isolated.world_name = view_of(invalid_world);
    assert(!reached_backend(view, isolated));
    // A world name without an isolated world names nothing.
    neoastra_script_options_t named{};
    named.size = sizeof(named);
    named.version = 1;
    const std::string world("codealta.annotations");
    named.world_name = view_of(world);
    assert(!reached_backend(view, named));

    neoastra_view_release(view);
    neoastra_environment_release(environment);
    neoastra_app_release(app);
}

struct captured_events {
    std::vector<neoastra_event_type_t> types;
    std::string text, uri, world, binding;
    uint64_t value{};
};
void NEOASTRA_CALL capture_event(void* context, const neoastra_event_t* event) {
    auto* captured = static_cast<captured_events*>(context);
    captured->types.push_back(event->header.type);
    captured->text = neo_string(event->text);
    captured->uri = neo_string(event->uri);
    captured->world = neo_string(event->text2);
    captured->binding = neo_string(event->text3);
    captured->value = event->value;
}

void test_world_messages_come_from_the_main_frame_within_the_limit() {
    auto* app = create_app();
    auto* environment = new neoastra_environment(app);
    auto* view = new neoastra_view(environment);
    view->maximum_message_size = 16;
    captured_events captured;
    assert(neoastra_view_set_event_callback(view, capture_event, &captured) == NEOASTRA_OK);

    assert(neo_emit_script_message(view, "codealta.annotations", "codeAltaAnnotations", "{\"send\":1}", "https://example.test", true));
    assert(captured.types.size() == 1 && captured.types[0] == NEOASTRA_EVENT_SCRIPT_MESSAGE_RECEIVED);
    assert(captured.text == "{\"send\":1}" && captured.uri == "https://example.test");
    assert(captured.world == "codealta.annotations" && captured.binding == "codeAltaAnnotations" && captured.value == 1);

    // A frame other than the main one, a message over the limit of the view, or a view that is going away: nothing.
    assert(!neo_emit_script_message(view, "codealta.annotations", "codeAltaAnnotations", "{}", "https://example.test", false));
    assert(!neo_emit_script_message(view, "codealta.annotations", "codeAltaAnnotations", std::string(17, 'x'), "https://example.test", true));
    assert(!neo_emit_script_message(view, "codealta.annotations", "codeAltaAnnotations", std::string("\xff", 1), "https://example.test", true));
    view->destroying = true;
    assert(!neo_emit_script_message(view, "codealta.annotations", "codeAltaAnnotations", "{}", "https://example.test", true));
    view->destroying = false;
    assert(captured.types.size() == 1);

    neoastra_view_release(view);
    neoastra_environment_release(environment);
    neoastra_app_release(app);
}

void test_messages_to_a_script_are_checked_before_the_backend() {
    auto* app = create_app();
    auto* environment = new neoastra_environment(app);
    auto* view = new neoastra_view(environment);
    view->maximum_message_size = 16;
    const std::string identifier("script-1"), message("{\"ok\":true}");

    assert(neoastra_view_post_script_message(nullptr, view_of(identifier), view_of(message), nullptr) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(neoastra_view_post_script_message(view, {}, view_of(message), nullptr) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    const std::string invalid("\xff", 1), large(17, 'x');
    assert(neoastra_view_post_script_message(view, view_of(identifier), view_of(invalid), nullptr) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    neoastra_error_t* error{};
    assert(neoastra_view_post_script_message(view, view_of(identifier), view_of(large), &error) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    assert(error);
    neoastra_error_release(error);
    assert(backend::script_message_calls == 0);

    assert(neoastra_view_post_script_message(view, view_of(identifier), view_of(message), nullptr) == NEOASTRA_OK);
    assert(backend::script_message_calls == 1);
    assert(backend::last_script_message_identifier == identifier && backend::last_script_message == message);

    neoastra_view_release(view);
    neoastra_environment_release(environment);
    neoastra_app_release(app);
}

neoastra_support_level_t support(neoastra_environment_t* environment, neoastra_capability_t capability) {
    neoastra_capability_info_t info{};
    info.size = sizeof(info);
    info.version = 1;
    assert(neoastra_environment_get_capability(environment, capability, &info) == NEOASTRA_OK);
    return info.support;
}

void test_capabilities_follow_the_backend_and_fail_closed() {
    auto* app = create_app();
    auto* environment = new neoastra_environment(app);
    backend::script_worlds_supported = false;
    assert(support(environment, NEOASTRA_CAPABILITY_SCRIPT_ISOLATED_WORLD) == NEOASTRA_SUPPORT_NONE);
    assert(support(environment, NEOASTRA_CAPABILITY_SCRIPT_WORLD_CHANNEL) == NEOASTRA_SUPPORT_NONE);
    backend::script_worlds_supported = true;
    assert(support(environment, NEOASTRA_CAPABILITY_SCRIPT_ISOLATED_WORLD) == NEOASTRA_SUPPORT_NATIVE);
    assert(support(environment, NEOASTRA_CAPABILITY_SCRIPT_WORLD_CHANNEL) == NEOASTRA_SUPPORT_NATIVE);
    neoastra_capability_info_t info{};
    info.size = sizeof(info);
    info.version = 1;
    assert(neoastra_environment_get_capability(environment, static_cast<neoastra_capability_t>(NEOASTRA_CAPABILITY_SCRIPT_WORLD_CHANNEL + 1), &info) == NEOASTRA_ERROR_INVALID_ARGUMENT);
    backend::script_worlds_supported = false;

    neoastra_runtime_info_t runtime{};
    runtime.size = sizeof(runtime);
    runtime.version = 1;
    assert(neoastra_get_runtime_info(&runtime, nullptr) == NEOASTRA_OK);
    assert((runtime.build_features & NEOASTRA_BUILD_FEATURE_SCRIPT_WORLD_CHANNEL) != 0);

    neoastra_environment_release(environment);
    neoastra_app_release(app);
}
} // namespace

int main() {
    test_version_one_callers_keep_their_size();
    test_names_are_unambiguous_and_never_of_the_page();
    test_world_messages_come_from_the_main_frame_within_the_limit();
    test_messages_to_a_script_are_checked_before_the_backend();
    test_capabilities_follow_the_backend_and_fail_closed();
    std::puts("script world tests passed");
}
