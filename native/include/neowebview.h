#ifndef NEOWEBVIEW_H
#define NEOWEBVIEW_H

#include <stddef.h>
#include <stdint.h>
#include "neowebview_version.h"

#if defined(_WIN32)
# if defined(NEOWEBVIEW_BUILD)
#  define NEO_WEBVIEW_API __declspec(dllexport)
# else
#  define NEO_WEBVIEW_API __declspec(dllimport)
# endif
# define NEO_WEBVIEW_CALL __cdecl
#else
# define NEO_WEBVIEW_API __attribute__((visibility("default")))
# define NEO_WEBVIEW_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct neo_webview_app neo_webview_app_t;
typedef struct neo_webview_environment neo_webview_environment_t;
typedef struct neo_webview_profile neo_webview_profile_t;
typedef struct neo_webview_window neo_webview_window_t;
typedef struct neo_webview_view neo_webview_view_t;
typedef struct neo_webview_operation neo_webview_operation_t;
typedef struct neo_webview_decision neo_webview_decision_t;
typedef struct neo_webview_download neo_webview_download_t;
typedef struct neo_webview_error neo_webview_error_t;
typedef struct neo_webview_buffer neo_webview_buffer_t;
typedef struct neo_webview_stream neo_webview_stream_t;

typedef struct neo_webview_string_view {
    const uint8_t* data;
    uint64_t length;
} neo_webview_string_view_t;

typedef struct neo_webview_struct_header { uint32_t size; uint32_t version; } neo_webview_struct_header_t;
typedef struct neo_webview_point { int32_t x; int32_t y; } neo_webview_point_t;
typedef struct neo_webview_size { int32_t width; int32_t height; } neo_webview_size_t;
typedef struct neo_webview_rect { int32_t x; int32_t y; int32_t width; int32_t height; } neo_webview_rect_t;
typedef struct neo_webview_color { uint8_t red; uint8_t green; uint8_t blue; uint8_t alpha; } neo_webview_color_t;

typedef enum neo_webview_result : int32_t {
    NEO_WEBVIEW_OK = 0,
    NEO_WEBVIEW_ERROR_UNKNOWN = -1,
    NEO_WEBVIEW_ERROR_INVALID_ARGUMENT = -2,
    NEO_WEBVIEW_ERROR_INVALID_STATE = -3,
    NEO_WEBVIEW_ERROR_NOT_SUPPORTED = -4,
    NEO_WEBVIEW_ERROR_NOT_INITIALIZED = -5,
    NEO_WEBVIEW_ERROR_ALREADY_INITIALIZED = -6,
    NEO_WEBVIEW_ERROR_WRONG_THREAD = -7,
    NEO_WEBVIEW_ERROR_CANCELED = -8,
    NEO_WEBVIEW_ERROR_TIMED_OUT = -9,
    NEO_WEBVIEW_ERROR_BACKEND_UNAVAILABLE = -10,
    NEO_WEBVIEW_ERROR_RUNTIME_UNAVAILABLE = -11,
    NEO_WEBVIEW_ERROR_NATIVE_FAILURE = -12,
    NEO_WEBVIEW_ERROR_DISPOSED = -13,
    NEO_WEBVIEW_ERROR_SECURITY = -14
} neo_webview_result_t;

typedef enum neo_webview_support_level : uint32_t { NEO_WEBVIEW_SUPPORT_NONE = 0, NEO_WEBVIEW_SUPPORT_NATIVE = 1, NEO_WEBVIEW_SUPPORT_EMULATED = 2, NEO_WEBVIEW_SUPPORT_LIMITED = 3 } neo_webview_support_level_t;
typedef enum neo_webview_app_shutdown_mode : uint32_t { NEO_WEBVIEW_APP_SHUTDOWN_EXPLICIT = 0, NEO_WEBVIEW_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED = 1, NEO_WEBVIEW_APP_SHUTDOWN_ON_MAIN_WINDOW_CLOSED = 2 } neo_webview_app_shutdown_mode_t;
typedef enum neo_webview_native_parent_kind : uint32_t { NEO_WEBVIEW_NATIVE_PARENT_NONE = 0, NEO_WEBVIEW_NATIVE_PARENT_WIN32_HWND = 1, NEO_WEBVIEW_NATIVE_PARENT_COCOA_NSVIEW = 2, NEO_WEBVIEW_NATIVE_PARENT_GTK_WIDGET = 3 } neo_webview_native_parent_kind_t;
typedef enum neo_webview_native_handle_kind : uint32_t { NEO_WEBVIEW_NATIVE_HANDLE_NONE = 0, NEO_WEBVIEW_NATIVE_HANDLE_WIN32_HWND = 1, NEO_WEBVIEW_NATIVE_HANDLE_COCOA_NSWINDOW = 2, NEO_WEBVIEW_NATIVE_HANDLE_COCOA_NSVIEW = 3, NEO_WEBVIEW_NATIVE_HANDLE_GTK_WINDOW = 4, NEO_WEBVIEW_NATIVE_HANDLE_GTK_WIDGET = 5, NEO_WEBVIEW_NATIVE_HANDLE_WEBVIEW2_CONTROLLER = 6, NEO_WEBVIEW_NATIVE_HANDLE_WEBVIEW2_CORE = 7, NEO_WEBVIEW_NATIVE_HANDLE_WKWEBVIEW = 8, NEO_WEBVIEW_NATIVE_HANDLE_WEBKITGTK_WEBVIEW = 9 } neo_webview_native_handle_kind_t;
typedef enum neo_webview_window_state : uint32_t { NEO_WEBVIEW_WINDOW_NORMAL = 0, NEO_WEBVIEW_WINDOW_MINIMIZED = 1, NEO_WEBVIEW_WINDOW_MAXIMIZED = 2, NEO_WEBVIEW_WINDOW_FULLSCREEN = 3 } neo_webview_window_state_t;
typedef enum neo_webview_option_state : uint32_t { NEO_WEBVIEW_OPTION_DEFAULT = 0, NEO_WEBVIEW_OPTION_ENABLED = 1, NEO_WEBVIEW_OPTION_DISABLED = 2 } neo_webview_option_state_t;
typedef enum neo_webview_script_injection_time : uint32_t { NEO_WEBVIEW_SCRIPT_DOCUMENT_START = 0, NEO_WEBVIEW_SCRIPT_DOCUMENT_END = 1 } neo_webview_script_injection_time_t;
typedef enum neo_webview_decision_action : uint32_t { NEO_WEBVIEW_DECISION_DEFAULT = 0, NEO_WEBVIEW_DECISION_ALLOW = 1, NEO_WEBVIEW_DECISION_DENY = 2, NEO_WEBVIEW_DECISION_CANCEL = 3, NEO_WEBVIEW_DECISION_OPEN_EXTERNAL = 4, NEO_WEBVIEW_DECISION_DOWNLOAD = 5, NEO_WEBVIEW_DECISION_HANDLED_EXTERNAL = 6 } neo_webview_decision_action_t;
typedef enum neo_webview_decision_kind : uint32_t { NEO_WEBVIEW_DECISION_UNKNOWN = 0, NEO_WEBVIEW_DECISION_NAVIGATION = 1, NEO_WEBVIEW_DECISION_NEW_WINDOW = 2, NEO_WEBVIEW_DECISION_PERMISSION = 3, NEO_WEBVIEW_DECISION_DOWNLOAD_REQUEST = 4, NEO_WEBVIEW_DECISION_SCRIPT_DIALOG = 5, NEO_WEBVIEW_DECISION_FILE_CHOOSER = 6, NEO_WEBVIEW_DECISION_AUTHENTICATION = 7, NEO_WEBVIEW_DECISION_CERTIFICATE_ERROR = 8, NEO_WEBVIEW_DECISION_FULLSCREEN = 9, NEO_WEBVIEW_DECISION_CLIENT_CERTIFICATE = 10 } neo_webview_decision_kind_t;
typedef enum neo_webview_script_dialog_kind : uint32_t { NEO_WEBVIEW_SCRIPT_DIALOG_ALERT = 0, NEO_WEBVIEW_SCRIPT_DIALOG_CONFIRM = 1, NEO_WEBVIEW_SCRIPT_DIALOG_PROMPT = 2, NEO_WEBVIEW_SCRIPT_DIALOG_BEFORE_UNLOAD = 3 } neo_webview_script_dialog_kind_t;
typedef enum neo_webview_download_state : uint32_t { NEO_WEBVIEW_DOWNLOAD_REQUESTED = 0, NEO_WEBVIEW_DOWNLOAD_IN_PROGRESS = 1, NEO_WEBVIEW_DOWNLOAD_COMPLETED = 2, NEO_WEBVIEW_DOWNLOAD_CANCELED = 3, NEO_WEBVIEW_DOWNLOAD_FAILED = 4 } neo_webview_download_state_t;
typedef enum neo_webview_permission_kind : uint32_t { NEO_WEBVIEW_PERMISSION_UNKNOWN = 0, NEO_WEBVIEW_PERMISSION_GEOLOCATION, NEO_WEBVIEW_PERMISSION_CAMERA, NEO_WEBVIEW_PERMISSION_MICROPHONE, NEO_WEBVIEW_PERMISSION_NOTIFICATIONS, NEO_WEBVIEW_PERMISSION_CLIPBOARD_READ, NEO_WEBVIEW_PERMISSION_CLIPBOARD_WRITE, NEO_WEBVIEW_PERMISSION_MIDI, NEO_WEBVIEW_PERMISSION_SCREEN_CAPTURE, NEO_WEBVIEW_PERMISSION_POINTER_LOCK, NEO_WEBVIEW_PERMISSION_LOCAL_FONTS, NEO_WEBVIEW_PERMISSION_FILE_SYSTEM, NEO_WEBVIEW_PERMISSION_PERSISTENT_STORAGE } neo_webview_permission_kind_t;
typedef enum neo_webview_process_failure_kind : uint32_t { NEO_WEBVIEW_PROCESS_FAILURE_UNKNOWN = 0, NEO_WEBVIEW_PROCESS_FAILURE_WEB_PROCESS_EXITED, NEO_WEBVIEW_PROCESS_FAILURE_BROWSER_PROCESS_EXITED, NEO_WEBVIEW_PROCESS_FAILURE_PROCESS_UNRESPONSIVE } neo_webview_process_failure_kind_t;
typedef enum neo_webview_event_type : uint32_t {
    NEO_WEBVIEW_EVENT_NONE = 0,
    NEO_WEBVIEW_EVENT_WINDOW_CLOSE_REQUESTED, NEO_WEBVIEW_EVENT_WINDOW_CLOSED, NEO_WEBVIEW_EVENT_WINDOW_MOVED, NEO_WEBVIEW_EVENT_WINDOW_RESIZED, NEO_WEBVIEW_EVENT_WINDOW_FOCUS_CHANGED, NEO_WEBVIEW_EVENT_WINDOW_SCALE_FACTOR_CHANGED, NEO_WEBVIEW_EVENT_WINDOW_STATE_CHANGED,
    NEO_WEBVIEW_EVENT_NAVIGATION_REQUESTED, NEO_WEBVIEW_EVENT_NAVIGATION_STARTED, NEO_WEBVIEW_EVENT_NAVIGATION_REDIRECTED, NEO_WEBVIEW_EVENT_NAVIGATION_COMMITTED, NEO_WEBVIEW_EVENT_NAVIGATION_COMPLETED, NEO_WEBVIEW_EVENT_NAVIGATION_FAILED,
    NEO_WEBVIEW_EVENT_SOURCE_CHANGED, NEO_WEBVIEW_EVENT_TITLE_CHANGED, NEO_WEBVIEW_EVENT_HISTORY_CHANGED, NEO_WEBVIEW_EVENT_LOADING_PROGRESS_CHANGED, NEO_WEBVIEW_EVENT_FAVICON_CHANGED,
    NEO_WEBVIEW_EVENT_MESSAGE_RECEIVED, NEO_WEBVIEW_EVENT_CONSOLE_MESSAGE, NEO_WEBVIEW_EVENT_NEW_WINDOW_REQUESTED, NEO_WEBVIEW_EVENT_PERMISSION_REQUESTED, NEO_WEBVIEW_EVENT_DOWNLOAD_REQUESTED, NEO_WEBVIEW_EVENT_SCRIPT_DIALOG_REQUESTED, NEO_WEBVIEW_EVENT_FILE_CHOOSER_REQUESTED, NEO_WEBVIEW_EVENT_AUTHENTICATION_REQUESTED, NEO_WEBVIEW_EVENT_CERTIFICATE_ERROR, NEO_WEBVIEW_EVENT_FULLSCREEN_REQUESTED, NEO_WEBVIEW_EVENT_WEB_PROCESS_TERMINATED,
    NEO_WEBVIEW_EVENT_DOWNLOAD_STARTED, NEO_WEBVIEW_EVENT_DOWNLOAD_PROGRESS_CHANGED, NEO_WEBVIEW_EVENT_DOWNLOAD_COMPLETED, NEO_WEBVIEW_EVENT_CLIENT_CERTIFICATE_REQUESTED
} neo_webview_event_type_t;
typedef enum neo_webview_capability : uint32_t { NEO_WEBVIEW_CAPABILITY_CUSTOM_SCHEME = 0, NEO_WEBVIEW_CAPABILITY_SCRIPT_DOCUMENT_START, NEO_WEBVIEW_CAPABILITY_SCRIPT_DOCUMENT_END, NEO_WEBVIEW_CAPABILITY_SCRIPT_ISOLATED_WORLD, NEO_WEBVIEW_CAPABILITY_SCRIPT_ALL_FRAMES, NEO_WEBVIEW_CAPABILITY_MESSAGE_ORIGIN, NEO_WEBVIEW_CAPABILITY_MESSAGE_SUBFRAMES, NEO_WEBVIEW_CAPABILITY_PROFILE_NAMED, NEO_WEBVIEW_CAPABILITY_PROFILE_EPHEMERAL, NEO_WEBVIEW_CAPABILITY_COOKIES, NEO_WEBVIEW_CAPABILITY_CLEAR_DATA_BY_TIME, NEO_WEBVIEW_CAPABILITY_DOWNLOADS, NEO_WEBVIEW_CAPABILITY_DOWNLOAD_PAUSE, NEO_WEBVIEW_CAPABILITY_PERMISSIONS, NEO_WEBVIEW_CAPABILITY_PERMISSION_PERSISTENCE, NEO_WEBVIEW_CAPABILITY_NETWORK_OBSERVATION, NEO_WEBVIEW_CAPABILITY_NETWORK_INTERCEPTION, NEO_WEBVIEW_CAPABILITY_PRINT_DIALOG, NEO_WEBVIEW_CAPABILITY_PRINT_PDF, NEO_WEBVIEW_CAPABILITY_CAPTURE_VIEWPORT, NEO_WEBVIEW_CAPABILITY_CAPTURE_FULL_PAGE, NEO_WEBVIEW_CAPABILITY_DEVTOOLS, NEO_WEBVIEW_CAPABILITY_FIND, NEO_WEBVIEW_CAPABILITY_TRANSPARENT_BACKGROUND, NEO_WEBVIEW_CAPABILITY_COMPOSITION, NEO_WEBVIEW_CAPABILITY_ZOOM, NEO_WEBVIEW_CAPABILITY_TRACKED_POPUPS, NEO_WEBVIEW_CAPABILITY_SCRIPT_DIALOGS, NEO_WEBVIEW_CAPABILITY_FILE_CHOOSER, NEO_WEBVIEW_CAPABILITY_HTTP_AUTHENTICATION, NEO_WEBVIEW_CAPABILITY_CLIENT_CERTIFICATES, NEO_WEBVIEW_CAPABILITY_TLS_ERROR_DECISIONS, NEO_WEBVIEW_CAPABILITY_FULLSCREEN_DECISIONS } neo_webview_capability_t;
typedef enum neo_webview_log_level : uint32_t { NEO_WEBVIEW_LOG_TRACE = 0, NEO_WEBVIEW_LOG_DEBUG, NEO_WEBVIEW_LOG_INFORMATION, NEO_WEBVIEW_LOG_WARNING, NEO_WEBVIEW_LOG_ERROR, NEO_WEBVIEW_LOG_CRITICAL } neo_webview_log_level_t;

typedef uint64_t neo_webview_data_kind_t;
#define NEO_WEBVIEW_DATA_COOKIES (1ull << 0)
#define NEO_WEBVIEW_DATA_CACHE (1ull << 1)
#define NEO_WEBVIEW_DATA_LOCAL_STORAGE (1ull << 2)
#define NEO_WEBVIEW_DATA_INDEXED_DB (1ull << 3)
#define NEO_WEBVIEW_DATA_SERVICE_WORKERS (1ull << 4)
#define NEO_WEBVIEW_DATA_PERMISSIONS (1ull << 5)
#define NEO_WEBVIEW_DATA_DOWNLOAD_HISTORY (1ull << 6)
#define NEO_WEBVIEW_DATA_ALL UINT64_MAX
#define NEO_WEBVIEW_PROCESS_FAILURE_KIND_MASK UINT64_C(0xffffffff)
#define NEO_WEBVIEW_PROCESS_FAILURE_CRASHED (UINT64_C(1) << 32)
#define NEO_WEBVIEW_PROCESS_FAILURE_RECREATE_VIEW (UINT64_C(1) << 33)
#define NEO_WEBVIEW_PROCESS_FAILURE_RESTART_APPLICATION (UINT64_C(1) << 34)

typedef struct neo_webview_native_parent { uint32_t size; uint32_t version; neo_webview_native_parent_kind_t kind; void* handle; } neo_webview_native_parent_t;
typedef struct neo_webview_native_handle { uint32_t size; uint32_t version; neo_webview_native_handle_kind_t kind; void* value; } neo_webview_native_handle_t;
typedef struct neo_webview_event_header { uint32_t size; uint32_t version; neo_webview_event_type_t type; uint64_t sequence; uint64_t timestamp_ns; } neo_webview_event_header_t;
typedef struct neo_webview_event { neo_webview_event_header_t header; uint64_t object_id; neo_webview_string_view_t text; neo_webview_string_view_t uri; uint64_t value; int64_t native_code; neo_webview_decision_t* decision; neo_webview_string_view_t text2; neo_webview_string_view_t text3; uint64_t value2; neo_webview_rect_t bounds; neo_webview_download_t* download; } neo_webview_event_t;
typedef struct neo_webview_capability_info { uint32_t size; uint32_t version; neo_webview_support_level_t support; uint32_t capability_version; uint64_t flags; neo_webview_string_view_t details; } neo_webview_capability_info_t;

typedef void (NEO_WEBVIEW_CALL *neo_webview_dispatch_callback_t)(void* context);
typedef void (NEO_WEBVIEW_CALL *neo_webview_event_callback_t)(void* context, const neo_webview_event_t* event);
typedef void (NEO_WEBVIEW_CALL *neo_webview_log_callback_t)(void* context, neo_webview_log_level_t level, neo_webview_string_view_t category, neo_webview_string_view_t message, uint64_t thread_id, uint64_t timestamp_ns, int64_t native_code, uint64_t object_id);

typedef struct neo_webview_app_options { uint32_t size; uint32_t version; neo_webview_string_view_t application_name; neo_webview_app_shutdown_mode_t shutdown_mode; uint32_t maximum_pending_dispatches; uint32_t reserved; neo_webview_log_callback_t log_callback; void* log_context; } neo_webview_app_options_t;
typedef struct neo_webview_environment_options { uint32_t size; uint32_t version; neo_webview_string_view_t user_data_root; neo_webview_string_view_t browser_runtime_path; neo_webview_string_view_t browser_arguments; neo_webview_string_view_t preferred_languages; uint32_t private_mode; uint32_t custom_scheme_count; const void* custom_schemes; } neo_webview_environment_options_t;
typedef struct neo_webview_profile_options { uint32_t size; uint32_t version; neo_webview_string_view_t name; uint32_t ephemeral; uint32_t reserved; } neo_webview_profile_options_t;
typedef struct neo_webview_window_options { uint32_t size; uint32_t version; neo_webview_string_view_t title; neo_webview_rect_t bounds; neo_webview_size_t minimum_size; neo_webview_size_t maximum_size; neo_webview_window_t* owner; neo_webview_window_state_t state; uint32_t flags; neo_webview_color_t background_color; } neo_webview_window_options_t;
typedef struct neo_webview_view_options { uint32_t size; uint32_t version; neo_webview_profile_t* profile; neo_webview_native_parent_t parent; neo_webview_window_t* window; neo_webview_rect_t bounds; uint32_t fill_parent; uint32_t maximum_message_size; uint64_t decision_timeout_ms; neo_webview_decision_t* popup_request; } neo_webview_view_options_t;
typedef struct neo_webview_script_options { uint32_t size; uint32_t version; neo_webview_script_injection_time_t injection_time; uint32_t main_frame_only; uint32_t isolated_world; neo_webview_string_view_t world_name; } neo_webview_script_options_t;
typedef struct neo_webview_decision_response { uint32_t size; uint32_t version; neo_webview_decision_action_t action; neo_webview_string_view_t text; const neo_webview_string_view_t* paths; uint32_t path_count; uint32_t persist; neo_webview_string_view_t secondary_text; neo_webview_view_t* target_view; uint32_t selected_index; uint32_t reserved; } neo_webview_decision_response_t;
typedef struct neo_webview_download_info { uint32_t size; uint32_t version; uint64_t id; neo_webview_download_state_t state; uint32_t can_pause; neo_webview_string_view_t source_uri; neo_webview_string_view_t destination_path; uint64_t bytes_received; uint64_t total_bytes; neo_webview_string_view_t failure_reason; } neo_webview_download_info_t;
typedef struct neo_webview_runtime_info { uint32_t size; uint32_t version; neo_webview_string_view_t backend_name; neo_webview_string_view_t backend_version; neo_webview_string_view_t browser_version; neo_webview_string_view_t operating_system; neo_webview_string_view_t architecture; uint64_t build_features; uint32_t debug_build; uint32_t reserved; } neo_webview_runtime_info_t;
typedef struct neo_webview_cookie { uint32_t size; uint32_t version; neo_webview_string_view_t name; neo_webview_string_view_t value; neo_webview_string_view_t domain; neo_webview_string_view_t path; int64_t expires_unix_ms; uint32_t flags; uint32_t same_site; } neo_webview_cookie_t;

typedef void (NEO_WEBVIEW_CALL *neo_webview_environment_created_callback_t)(void*, neo_webview_result_t, neo_webview_environment_t*, const neo_webview_error_t*);
typedef void (NEO_WEBVIEW_CALL *neo_webview_profile_created_callback_t)(void*, neo_webview_result_t, neo_webview_profile_t*, const neo_webview_error_t*);
typedef void (NEO_WEBVIEW_CALL *neo_webview_view_created_callback_t)(void*, neo_webview_result_t, neo_webview_view_t*, const neo_webview_error_t*);
typedef void (NEO_WEBVIEW_CALL *neo_webview_string_callback_t)(void*, neo_webview_result_t, neo_webview_string_view_t, const neo_webview_error_t*);
typedef void (NEO_WEBVIEW_CALL *neo_webview_completion_callback_t)(void*, neo_webview_result_t, const neo_webview_error_t*);
typedef void (NEO_WEBVIEW_CALL *neo_webview_buffer_callback_t)(void*, neo_webview_result_t, neo_webview_buffer_t*, const neo_webview_error_t*);

NEO_WEBVIEW_API uint32_t NEO_WEBVIEW_CALL neo_webview_get_abi_version_major(void);
NEO_WEBVIEW_API uint32_t NEO_WEBVIEW_CALL neo_webview_get_abi_version_minor(void);
NEO_WEBVIEW_API neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_get_version(void);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_get_runtime_info(neo_webview_runtime_info_t*, neo_webview_error_t**);

#define NEO_WEBVIEW_DECLARE_LIFETIME(name) NEO_WEBVIEW_API void NEO_WEBVIEW_CALL neo_webview_##name##_retain(neo_webview_##name##_t*); NEO_WEBVIEW_API void NEO_WEBVIEW_CALL neo_webview_##name##_release(neo_webview_##name##_t*)
NEO_WEBVIEW_DECLARE_LIFETIME(app);
NEO_WEBVIEW_DECLARE_LIFETIME(environment);
NEO_WEBVIEW_DECLARE_LIFETIME(profile);
NEO_WEBVIEW_DECLARE_LIFETIME(window);
NEO_WEBVIEW_DECLARE_LIFETIME(view);
NEO_WEBVIEW_DECLARE_LIFETIME(operation);
NEO_WEBVIEW_DECLARE_LIFETIME(decision);
NEO_WEBVIEW_DECLARE_LIFETIME(download);
NEO_WEBVIEW_DECLARE_LIFETIME(error);
NEO_WEBVIEW_DECLARE_LIFETIME(buffer);
NEO_WEBVIEW_DECLARE_LIFETIME(stream);
#undef NEO_WEBVIEW_DECLARE_LIFETIME

NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_error_get_code(const neo_webview_error_t*);
NEO_WEBVIEW_API int64_t NEO_WEBVIEW_CALL neo_webview_error_get_native_code(const neo_webview_error_t*);
NEO_WEBVIEW_API neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_error_get_domain(const neo_webview_error_t*);
NEO_WEBVIEW_API neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_error_get_message(const neo_webview_error_t*);
NEO_WEBVIEW_API const uint8_t* NEO_WEBVIEW_CALL neo_webview_buffer_get_data(const neo_webview_buffer_t*);
NEO_WEBVIEW_API uint64_t NEO_WEBVIEW_CALL neo_webview_buffer_get_length(const neo_webview_buffer_t*);
NEO_WEBVIEW_API void NEO_WEBVIEW_CALL neo_webview_operation_cancel(neo_webview_operation_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_decision_defer(neo_webview_decision_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_decision_complete(neo_webview_decision_t*, const neo_webview_decision_response_t*, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_decision_kind_t NEO_WEBVIEW_CALL neo_webview_decision_get_kind(const neo_webview_decision_t*);
NEO_WEBVIEW_API neo_webview_decision_action_t NEO_WEBVIEW_CALL neo_webview_decision_get_default_action(const neo_webview_decision_t*);
NEO_WEBVIEW_API uint64_t NEO_WEBVIEW_CALL neo_webview_decision_get_deadline_ns(const neo_webview_decision_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_download_get_info(const neo_webview_download_t*, neo_webview_download_info_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_download_cancel(neo_webview_download_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_download_pause(neo_webview_download_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_download_resume(neo_webview_download_t*);

NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_create(const neo_webview_app_options_t*, neo_webview_app_t**, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_attach(const neo_webview_app_options_t*, neo_webview_app_t**, neo_webview_error_t**);
/* Must be called on the owning UI thread before an attached host stops pumping.
   Drains accepted dispatch work, rejects new work, and completes platform teardown. Idempotent after shutdown. */
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_detach(neo_webview_app_t*, neo_webview_error_t**);
NEO_WEBVIEW_API int32_t NEO_WEBVIEW_CALL neo_webview_app_run(neo_webview_app_t*);
NEO_WEBVIEW_API void NEO_WEBVIEW_CALL neo_webview_app_quit(neo_webview_app_t*, int32_t);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_dispatch(neo_webview_app_t*, neo_webview_dispatch_callback_t, void*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_set_event_callback(neo_webview_app_t*, neo_webview_event_callback_t, void*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_create_window(neo_webview_app_t*, const neo_webview_window_options_t*, neo_webview_window_t**, neo_webview_error_t**);
NEO_WEBVIEW_API uint64_t NEO_WEBVIEW_CALL neo_webview_app_get_window_count(const neo_webview_app_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_app_get_window(neo_webview_app_t*, uint64_t, neo_webview_window_t**);

NEO_WEBVIEW_API uint64_t NEO_WEBVIEW_CALL neo_webview_window_get_id(const neo_webview_window_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_bounds(const neo_webview_window_t*, neo_webview_rect_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_bounds(neo_webview_window_t*, neo_webview_rect_t);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_minimum_size(const neo_webview_window_t*, neo_webview_size_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_minimum_size(neo_webview_window_t*, neo_webview_size_t);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_maximum_size(const neo_webview_window_t*, neo_webview_size_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_maximum_size(neo_webview_window_t*, neo_webview_size_t);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_state(const neo_webview_window_t*, neo_webview_window_state_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_state(neo_webview_window_t*, neo_webview_window_state_t);
NEO_WEBVIEW_API neo_webview_string_view_t NEO_WEBVIEW_CALL neo_webview_window_get_title(const neo_webview_window_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_set_title(neo_webview_window_t*, neo_webview_string_view_t);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_show(neo_webview_window_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_hide(neo_webview_window_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_activate(neo_webview_window_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_close(neo_webview_window_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_window_get_native_handle(neo_webview_window_t*, neo_webview_native_handle_kind_t, neo_webview_native_handle_t*);

NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_create_async(neo_webview_app_t*, const neo_webview_environment_options_t*, neo_webview_environment_created_callback_t, void*, neo_webview_operation_t**, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_create_profile_async(neo_webview_environment_t*, const neo_webview_profile_options_t*, neo_webview_profile_created_callback_t, void*, neo_webview_operation_t**, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_create_view_async(neo_webview_environment_t*, const neo_webview_view_options_t*, neo_webview_view_created_callback_t, void*, neo_webview_operation_t**, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_environment_get_capability(const neo_webview_environment_t*, neo_webview_capability_t, neo_webview_capability_info_t*);

NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_get_cookies_async(neo_webview_profile_t*, neo_webview_string_view_t, neo_webview_buffer_callback_t, void*, neo_webview_operation_t**, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_set_cookie_async(neo_webview_profile_t*, const neo_webview_cookie_t*, neo_webview_completion_callback_t, void*, neo_webview_operation_t**, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_delete_cookie_async(neo_webview_profile_t*, const neo_webview_cookie_t*, neo_webview_completion_callback_t, void*, neo_webview_operation_t**, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_profile_clear_data_async(neo_webview_profile_t*, neo_webview_data_kind_t, int64_t, int64_t, neo_webview_completion_callback_t, void*, neo_webview_operation_t**, neo_webview_error_t**);

NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_set_event_callback(neo_webview_view_t*, neo_webview_event_callback_t, void*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_set_bounds(neo_webview_view_t*, neo_webview_rect_t, uint32_t);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_navigate(neo_webview_view_t*, neo_webview_string_view_t, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_navigate_request(neo_webview_view_t*, neo_webview_string_view_t, neo_webview_string_view_t, neo_webview_string_view_t, const uint8_t*, uint64_t, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_load_html(neo_webview_view_t*, neo_webview_string_view_t, neo_webview_string_view_t, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_stop(neo_webview_view_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_reload(neo_webview_view_t*, uint32_t);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_go_back(neo_webview_view_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_go_forward(neo_webview_view_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_evaluate_script_async(neo_webview_view_t*, neo_webview_string_view_t, neo_webview_string_callback_t, void*, neo_webview_operation_t**, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_add_script_async(neo_webview_view_t*, neo_webview_string_view_t, const neo_webview_script_options_t*, neo_webview_string_callback_t, void*, neo_webview_operation_t**, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_remove_script(neo_webview_view_t*, neo_webview_string_view_t);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_post_message(neo_webview_view_t*, neo_webview_string_view_t, uint32_t, neo_webview_error_t**);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_get_zoom_factor(const neo_webview_view_t*, double*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_set_zoom_factor(neo_webview_view_t*, double);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_view_get_native_handle(neo_webview_view_t*, neo_webview_native_handle_kind_t, neo_webview_native_handle_t*);
NEO_WEBVIEW_API neo_webview_result_t NEO_WEBVIEW_CALL neo_webview_query_extension(const void*, neo_webview_string_view_t, uint32_t, const void**);

#ifdef __cplusplus
}
#endif
#endif
