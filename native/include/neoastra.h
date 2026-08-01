#ifndef NEOASTRA_H
#define NEOASTRA_H

#include <stddef.h>
#include <stdint.h>
#include "neoastra_version.h"

/** Hard upper bound accepted for a view's incoming or outgoing message size. */
#define NEOASTRA_HARD_MAXIMUM_MESSAGE_SIZE (16u * 1024u * 1024u)

#if defined(_WIN32)
# if defined(NEOASTRA_BUILD)
#  define NEOASTRA_API __declspec(dllexport)
# else
#  define NEOASTRA_API __declspec(dllimport)
# endif
# define NEOASTRA_CALL __cdecl
#else
# define NEOASTRA_API __attribute__((visibility("default")))
# define NEOASTRA_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct neoastra_app neoastra_app_t;
typedef struct neoastra_environment neoastra_environment_t;
typedef struct neoastra_profile neoastra_profile_t;
typedef struct neoastra_window neoastra_window_t;
typedef struct neoastra_view neoastra_view_t;
typedef struct neoastra_operation neoastra_operation_t;
typedef struct neoastra_decision neoastra_decision_t;
typedef struct neoastra_download neoastra_download_t;
typedef struct neoastra_error neoastra_error_t;
typedef struct neoastra_buffer neoastra_buffer_t;
typedef struct neoastra_stream neoastra_stream_t;
typedef struct neoastra_resource_request neoastra_resource_request_t;
typedef struct neoastra_resource_response neoastra_resource_response_t;
typedef struct neoastra_custom_scheme neoastra_custom_scheme_t;

typedef struct neoastra_string_view {
    const uint8_t* data;
    uint64_t length;
} neoastra_string_view_t;

typedef struct neoastra_struct_header { uint32_t size; uint32_t version; } neoastra_struct_header_t;
typedef struct neoastra_point { int32_t x; int32_t y; } neoastra_point_t;
typedef struct neoastra_size { int32_t width; int32_t height; } neoastra_size_t;
typedef struct neoastra_rect { int32_t x; int32_t y; int32_t width; int32_t height; } neoastra_rect_t;
typedef struct neoastra_color { uint8_t red; uint8_t green; uint8_t blue; uint8_t alpha; } neoastra_color_t;

typedef enum neoastra_result : int32_t {
    NEOASTRA_OK = 0,
    NEOASTRA_ERROR_UNKNOWN = -1,
    NEOASTRA_ERROR_INVALID_ARGUMENT = -2,
    NEOASTRA_ERROR_INVALID_STATE = -3,
    NEOASTRA_ERROR_NOT_SUPPORTED = -4,
    NEOASTRA_ERROR_NOT_INITIALIZED = -5,
    NEOASTRA_ERROR_ALREADY_INITIALIZED = -6,
    NEOASTRA_ERROR_WRONG_THREAD = -7,
    NEOASTRA_ERROR_CANCELED = -8,
    NEOASTRA_ERROR_TIMED_OUT = -9,
    NEOASTRA_ERROR_BACKEND_UNAVAILABLE = -10,
    NEOASTRA_ERROR_RUNTIME_UNAVAILABLE = -11,
    NEOASTRA_ERROR_NATIVE_FAILURE = -12,
    NEOASTRA_ERROR_DISPOSED = -13,
    NEOASTRA_ERROR_SECURITY = -14
} neoastra_result_t;

typedef enum neoastra_support_level : uint32_t { NEOASTRA_SUPPORT_NONE = 0, NEOASTRA_SUPPORT_NATIVE = 1, NEOASTRA_SUPPORT_EMULATED = 2, NEOASTRA_SUPPORT_LIMITED = 3 } neoastra_support_level_t;
typedef enum neoastra_app_shutdown_mode : uint32_t { NEOASTRA_APP_SHUTDOWN_EXPLICIT = 0, NEOASTRA_APP_SHUTDOWN_ON_LAST_WINDOW_CLOSED = 1, NEOASTRA_APP_SHUTDOWN_ON_MAIN_WINDOW_CLOSED = 2 } neoastra_app_shutdown_mode_t;
typedef enum neoastra_native_parent_kind : uint32_t { NEOASTRA_NATIVE_PARENT_NONE = 0, NEOASTRA_NATIVE_PARENT_WIN32_HWND = 1, NEOASTRA_NATIVE_PARENT_COCOA_NSVIEW = 2, NEOASTRA_NATIVE_PARENT_GTK_WIDGET = 3 } neoastra_native_parent_kind_t;
typedef enum neoastra_native_handle_kind : uint32_t { NEOASTRA_NATIVE_HANDLE_NONE = 0, NEOASTRA_NATIVE_HANDLE_WIN32_HWND = 1, NEOASTRA_NATIVE_HANDLE_COCOA_NSWINDOW = 2, NEOASTRA_NATIVE_HANDLE_COCOA_NSVIEW = 3, NEOASTRA_NATIVE_HANDLE_GTK_WINDOW = 4, NEOASTRA_NATIVE_HANDLE_GTK_WIDGET = 5, NEOASTRA_NATIVE_HANDLE_WEBVIEW2_CONTROLLER = 6, NEOASTRA_NATIVE_HANDLE_WEBVIEW2_CORE = 7, NEOASTRA_NATIVE_HANDLE_WKWEBVIEW = 8, NEOASTRA_NATIVE_HANDLE_WEBKITGTK_WEBVIEW = 9 } neoastra_native_handle_kind_t;
typedef enum neoastra_window_state : uint32_t { NEOASTRA_WINDOW_NORMAL = 0, NEOASTRA_WINDOW_MINIMIZED = 1, NEOASTRA_WINDOW_MAXIMIZED = 2, NEOASTRA_WINDOW_FULLSCREEN = 3 } neoastra_window_state_t;
typedef enum neoastra_window_attribute : uint32_t { NEOASTRA_WINDOW_RESIZABLE = 0, NEOASTRA_WINDOW_DECORATED = 1, NEOASTRA_WINDOW_ALWAYS_ON_TOP = 2, NEOASTRA_WINDOW_SHOW_IN_TASKBAR = 3 } neoastra_window_attribute_t;
typedef enum neoastra_window_resize_edge : uint32_t { NEOASTRA_WINDOW_RESIZE_LEFT = 0, NEOASTRA_WINDOW_RESIZE_TOP = 1, NEOASTRA_WINDOW_RESIZE_RIGHT = 2, NEOASTRA_WINDOW_RESIZE_BOTTOM = 3, NEOASTRA_WINDOW_RESIZE_TOP_LEFT = 4, NEOASTRA_WINDOW_RESIZE_TOP_RIGHT = 5, NEOASTRA_WINDOW_RESIZE_BOTTOM_LEFT = 6, NEOASTRA_WINDOW_RESIZE_BOTTOM_RIGHT = 7 } neoastra_window_resize_edge_t;
typedef enum neoastra_option_state : uint32_t { NEOASTRA_OPTION_DEFAULT = 0, NEOASTRA_OPTION_ENABLED = 1, NEOASTRA_OPTION_DISABLED = 2 } neoastra_option_state_t;
typedef enum neoastra_script_injection_time : uint32_t { NEOASTRA_SCRIPT_DOCUMENT_START = 0, NEOASTRA_SCRIPT_DOCUMENT_END = 1 } neoastra_script_injection_time_t;
typedef enum neoastra_decision_action : uint32_t { NEOASTRA_DECISION_DEFAULT = 0, NEOASTRA_DECISION_ALLOW = 1, NEOASTRA_DECISION_DENY = 2, NEOASTRA_DECISION_CANCEL = 3, NEOASTRA_DECISION_OPEN_EXTERNAL = 4, NEOASTRA_DECISION_DOWNLOAD = 5, NEOASTRA_DECISION_HANDLED_EXTERNAL = 6 } neoastra_decision_action_t;
typedef enum neoastra_decision_kind : uint32_t { NEOASTRA_DECISION_UNKNOWN = 0, NEOASTRA_DECISION_NAVIGATION = 1, NEOASTRA_DECISION_NEW_WINDOW = 2, NEOASTRA_DECISION_PERMISSION = 3, NEOASTRA_DECISION_DOWNLOAD_REQUEST = 4, NEOASTRA_DECISION_SCRIPT_DIALOG = 5, NEOASTRA_DECISION_FILE_CHOOSER = 6, NEOASTRA_DECISION_AUTHENTICATION = 7, NEOASTRA_DECISION_CERTIFICATE_ERROR = 8, NEOASTRA_DECISION_FULLSCREEN = 9, NEOASTRA_DECISION_CLIENT_CERTIFICATE = 10, NEOASTRA_DECISION_WINDOW_CLOSE = 11, NEOASTRA_DECISION_APPLICATION_QUIT = 12 } neoastra_decision_kind_t;
/** Portable reason for a native window close request. */
typedef enum neoastra_window_close_reason : uint32_t { NEOASTRA_WINDOW_CLOSE_USER = 0, NEOASTRA_WINDOW_CLOSE_OWNER = 1, NEOASTRA_WINDOW_CLOSE_APPLICATION_QUIT = 2, NEOASTRA_WINDOW_CLOSE_SESSION_END = 3, NEOASTRA_WINDOW_CLOSE_SYSTEM = 4, NEOASTRA_WINDOW_CLOSE_PROGRAMMATIC = 5 } neoastra_window_close_reason_t;
typedef enum neoastra_script_dialog_kind : uint32_t { NEOASTRA_SCRIPT_DIALOG_ALERT = 0, NEOASTRA_SCRIPT_DIALOG_CONFIRM = 1, NEOASTRA_SCRIPT_DIALOG_PROMPT = 2, NEOASTRA_SCRIPT_DIALOG_BEFORE_UNLOAD = 3 } neoastra_script_dialog_kind_t;
typedef enum neoastra_download_state : uint32_t { NEOASTRA_DOWNLOAD_REQUESTED = 0, NEOASTRA_DOWNLOAD_IN_PROGRESS = 1, NEOASTRA_DOWNLOAD_COMPLETED = 2, NEOASTRA_DOWNLOAD_CANCELED = 3, NEOASTRA_DOWNLOAD_FAILED = 4 } neoastra_download_state_t;
typedef enum neoastra_permission_kind : uint32_t { NEOASTRA_PERMISSION_UNKNOWN = 0, NEOASTRA_PERMISSION_GEOLOCATION, NEOASTRA_PERMISSION_CAMERA, NEOASTRA_PERMISSION_MICROPHONE, NEOASTRA_PERMISSION_NOTIFICATIONS, NEOASTRA_PERMISSION_CLIPBOARD_READ, NEOASTRA_PERMISSION_CLIPBOARD_WRITE, NEOASTRA_PERMISSION_MIDI, NEOASTRA_PERMISSION_SCREEN_CAPTURE, NEOASTRA_PERMISSION_POINTER_LOCK, NEOASTRA_PERMISSION_LOCAL_FONTS, NEOASTRA_PERMISSION_FILE_SYSTEM, NEOASTRA_PERMISSION_PERSISTENT_STORAGE } neoastra_permission_kind_t;
typedef enum neoastra_process_failure_kind : uint32_t { NEOASTRA_PROCESS_FAILURE_UNKNOWN = 0, NEOASTRA_PROCESS_FAILURE_WEB_PROCESS_EXITED, NEOASTRA_PROCESS_FAILURE_BROWSER_PROCESS_EXITED, NEOASTRA_PROCESS_FAILURE_PROCESS_UNRESPONSIVE } neoastra_process_failure_kind_t;
typedef enum neoastra_event_type : uint32_t {
    NEOASTRA_EVENT_NONE = 0,
    NEOASTRA_EVENT_WINDOW_CLOSE_REQUESTED, NEOASTRA_EVENT_WINDOW_CLOSED, NEOASTRA_EVENT_WINDOW_MOVED, NEOASTRA_EVENT_WINDOW_RESIZED, NEOASTRA_EVENT_WINDOW_FOCUS_CHANGED, NEOASTRA_EVENT_WINDOW_SCALE_FACTOR_CHANGED, NEOASTRA_EVENT_WINDOW_STATE_CHANGED,
    NEOASTRA_EVENT_NAVIGATION_REQUESTED, NEOASTRA_EVENT_NAVIGATION_STARTED, NEOASTRA_EVENT_NAVIGATION_REDIRECTED, NEOASTRA_EVENT_NAVIGATION_COMMITTED, NEOASTRA_EVENT_NAVIGATION_COMPLETED, NEOASTRA_EVENT_NAVIGATION_FAILED,
    NEOASTRA_EVENT_SOURCE_CHANGED, NEOASTRA_EVENT_TITLE_CHANGED, NEOASTRA_EVENT_HISTORY_CHANGED, NEOASTRA_EVENT_LOADING_PROGRESS_CHANGED, NEOASTRA_EVENT_FAVICON_CHANGED,
    NEOASTRA_EVENT_MESSAGE_RECEIVED, NEOASTRA_EVENT_CONSOLE_MESSAGE, NEOASTRA_EVENT_NEW_WINDOW_REQUESTED, NEOASTRA_EVENT_PERMISSION_REQUESTED, NEOASTRA_EVENT_DOWNLOAD_REQUESTED, NEOASTRA_EVENT_SCRIPT_DIALOG_REQUESTED, NEOASTRA_EVENT_FILE_CHOOSER_REQUESTED, NEOASTRA_EVENT_AUTHENTICATION_REQUESTED, NEOASTRA_EVENT_CERTIFICATE_ERROR, NEOASTRA_EVENT_FULLSCREEN_REQUESTED, NEOASTRA_EVENT_WEB_PROCESS_TERMINATED,
    NEOASTRA_EVENT_DOWNLOAD_STARTED, NEOASTRA_EVENT_DOWNLOAD_PROGRESS_CHANGED, NEOASTRA_EVENT_DOWNLOAD_COMPLETED, NEOASTRA_EVENT_CLIENT_CERTIFICATE_REQUESTED,
    NEOASTRA_EVENT_APPLICATION_ACTIVATED, NEOASTRA_EVENT_APPLICATION_OPEN_FILE, NEOASTRA_EVENT_APPLICATION_OPEN_URL, NEOASTRA_EVENT_APPLICATION_SESSION_END
} neoastra_event_type_t;
typedef enum neoastra_capability : uint32_t { NEOASTRA_CAPABILITY_CUSTOM_SCHEME = 0, NEOASTRA_CAPABILITY_SCRIPT_DOCUMENT_START, NEOASTRA_CAPABILITY_SCRIPT_DOCUMENT_END, NEOASTRA_CAPABILITY_SCRIPT_ISOLATED_WORLD, NEOASTRA_CAPABILITY_SCRIPT_ALL_FRAMES, NEOASTRA_CAPABILITY_MESSAGE_ORIGIN, NEOASTRA_CAPABILITY_MESSAGE_SUBFRAMES, NEOASTRA_CAPABILITY_PROFILE_NAMED, NEOASTRA_CAPABILITY_PROFILE_EPHEMERAL, NEOASTRA_CAPABILITY_COOKIES, NEOASTRA_CAPABILITY_CLEAR_DATA_BY_TIME, NEOASTRA_CAPABILITY_DOWNLOADS, NEOASTRA_CAPABILITY_DOWNLOAD_PAUSE, NEOASTRA_CAPABILITY_PERMISSIONS, NEOASTRA_CAPABILITY_PERMISSION_PERSISTENCE, NEOASTRA_CAPABILITY_NETWORK_OBSERVATION, NEOASTRA_CAPABILITY_NETWORK_INTERCEPTION, NEOASTRA_CAPABILITY_PRINT_DIALOG, NEOASTRA_CAPABILITY_PRINT_PDF, NEOASTRA_CAPABILITY_CAPTURE_VIEWPORT, NEOASTRA_CAPABILITY_CAPTURE_FULL_PAGE, NEOASTRA_CAPABILITY_DEVTOOLS, NEOASTRA_CAPABILITY_FIND, NEOASTRA_CAPABILITY_TRANSPARENT_BACKGROUND, NEOASTRA_CAPABILITY_COMPOSITION, NEOASTRA_CAPABILITY_ZOOM, NEOASTRA_CAPABILITY_TRACKED_POPUPS, NEOASTRA_CAPABILITY_SCRIPT_DIALOGS, NEOASTRA_CAPABILITY_FILE_CHOOSER, NEOASTRA_CAPABILITY_HTTP_AUTHENTICATION, NEOASTRA_CAPABILITY_CLIENT_CERTIFICATES, NEOASTRA_CAPABILITY_TLS_ERROR_DECISIONS, NEOASTRA_CAPABILITY_FULLSCREEN_DECISIONS } neoastra_capability_t;
typedef enum neoastra_log_level : uint32_t { NEOASTRA_LOG_TRACE = 0, NEOASTRA_LOG_DEBUG, NEOASTRA_LOG_INFORMATION, NEOASTRA_LOG_WARNING, NEOASTRA_LOG_ERROR, NEOASTRA_LOG_CRITICAL } neoastra_log_level_t;
typedef enum neoastra_resource_kind : uint32_t { NEOASTRA_RESOURCE_OTHER = 0, NEOASTRA_RESOURCE_DOCUMENT, NEOASTRA_RESOURCE_STYLESHEET, NEOASTRA_RESOURCE_IMAGE, NEOASTRA_RESOURCE_MEDIA, NEOASTRA_RESOURCE_FONT, NEOASTRA_RESOURCE_SCRIPT, NEOASTRA_RESOURCE_XML_HTTP_REQUEST, NEOASTRA_RESOURCE_FETCH, NEOASTRA_RESOURCE_TEXT_TRACK, NEOASTRA_RESOURCE_EVENT_SOURCE, NEOASTRA_RESOURCE_WEBSOCKET, NEOASTRA_RESOURCE_MANIFEST } neoastra_resource_kind_t;
typedef enum neoastra_resource_body_kind : uint32_t { NEOASTRA_RESOURCE_BODY_EMPTY = 0, NEOASTRA_RESOURCE_BODY_BYTES = 1, NEOASTRA_RESOURCE_BODY_FILE = 2 } neoastra_resource_body_kind_t;
typedef enum neoastra_bridge_policy : uint32_t { NEOASTRA_BRIDGE_DISABLED = 0, NEOASTRA_BRIDGE_TRUSTED_ORIGINS = 1, NEOASTRA_BRIDGE_TRUST_ENTIRE_VIEW = 2 } neoastra_bridge_policy_t;

typedef uint64_t neoastra_data_kind_t;
#define NEOASTRA_DATA_COOKIES (1ull << 0)
#define NEOASTRA_DATA_CACHE (1ull << 1)
#define NEOASTRA_DATA_LOCAL_STORAGE (1ull << 2)
#define NEOASTRA_DATA_INDEXED_DB (1ull << 3)
#define NEOASTRA_DATA_SERVICE_WORKERS (1ull << 4)
#define NEOASTRA_DATA_PERMISSIONS (1ull << 5)
#define NEOASTRA_DATA_DOWNLOAD_HISTORY (1ull << 6)
#define NEOASTRA_DATA_ALL UINT64_MAX
#define NEOASTRA_PROCESS_FAILURE_KIND_MASK UINT64_C(0xffffffff)
#define NEOASTRA_PROCESS_FAILURE_CRASHED (UINT64_C(1) << 32)
#define NEOASTRA_PROCESS_FAILURE_RECREATE_VIEW (UINT64_C(1) << 33)
#define NEOASTRA_PROCESS_FAILURE_RESTART_APPLICATION (UINT64_C(1) << 34)
#define NEOASTRA_CUSTOM_SCHEME_HAS_AUTHORITY (1u << 0)
#define NEOASTRA_CUSTOM_SCHEME_SECURE (1u << 1)
#define NEOASTRA_CUSTOM_SCHEME_CORS_ENABLED (1u << 2)
#define NEOASTRA_CUSTOM_SCHEME_APPLICATION (1u << 3)
#define NEOASTRA_CUSTOM_SCHEME_SERVICE_WORKERS (1u << 4)

typedef struct neoastra_native_parent { uint32_t size; uint32_t version; neoastra_native_parent_kind_t kind; void* handle; } neoastra_native_parent_t;
typedef struct neoastra_native_handle { uint32_t size; uint32_t version; neoastra_native_handle_kind_t kind; void* value; } neoastra_native_handle_t;
typedef struct neoastra_event_header { uint32_t size; uint32_t version; neoastra_event_type_t type; uint64_t sequence; uint64_t timestamp_ns; } neoastra_event_header_t;
typedef struct neoastra_event { neoastra_event_header_t header; uint64_t object_id; neoastra_string_view_t text; neoastra_string_view_t uri; uint64_t value; int64_t native_code; neoastra_decision_t* decision; neoastra_string_view_t text2; neoastra_string_view_t text3; uint64_t value2; neoastra_rect_t bounds; neoastra_download_t* download; } neoastra_event_t;
typedef struct neoastra_capability_info { uint32_t size; uint32_t version; neoastra_support_level_t support; uint32_t capability_version; uint64_t flags; neoastra_string_view_t details; } neoastra_capability_info_t;

typedef void (NEOASTRA_CALL *neoastra_dispatch_callback_t)(void* context);
typedef void (NEOASTRA_CALL *neoastra_event_callback_t)(void* context, const neoastra_event_t* event);
typedef void (NEOASTRA_CALL *neoastra_log_callback_t)(void* context, neoastra_log_level_t level, neoastra_string_view_t category, neoastra_string_view_t message, uint64_t thread_id, uint64_t timestamp_ns, int64_t native_code, uint64_t object_id);

typedef struct neoastra_app_options { uint32_t size; uint32_t version; neoastra_string_view_t application_name; neoastra_app_shutdown_mode_t shutdown_mode; uint32_t maximum_pending_dispatches; uint32_t reserved; neoastra_log_callback_t log_callback; void* log_context; } neoastra_app_options_t;
typedef struct neoastra_environment_options { uint32_t size; uint32_t version; neoastra_string_view_t user_data_root; neoastra_string_view_t browser_runtime_path; neoastra_string_view_t browser_arguments; neoastra_string_view_t preferred_languages; uint32_t private_mode; uint32_t custom_scheme_count; const neoastra_custom_scheme_t* custom_schemes; uint32_t custom_scheme_stride; uint32_t reserved; } neoastra_environment_options_t;
typedef struct neoastra_profile_options { uint32_t size; uint32_t version; neoastra_string_view_t name; uint32_t ephemeral; uint32_t reserved; } neoastra_profile_options_t;
typedef struct neoastra_window_options { uint32_t size; uint32_t version; neoastra_string_view_t title; neoastra_rect_t bounds; neoastra_size_t minimum_size; neoastra_size_t maximum_size; neoastra_window_t* owner; neoastra_window_state_t state; uint32_t flags; neoastra_color_t background_color; } neoastra_window_options_t;
typedef struct neoastra_view_options { uint32_t size; uint32_t version; neoastra_profile_t* profile; neoastra_native_parent_t parent; neoastra_window_t* window; neoastra_rect_t bounds; uint32_t fill_parent; uint32_t maximum_message_size; uint64_t decision_timeout_ms; neoastra_decision_t* popup_request; uint32_t bridge_origin_count; neoastra_bridge_policy_t bridge_policy; const neoastra_string_view_t* bridge_origins; } neoastra_view_options_t;
typedef struct neoastra_script_options { uint32_t size; uint32_t version; neoastra_script_injection_time_t injection_time; uint32_t main_frame_only; uint32_t isolated_world; neoastra_string_view_t world_name; } neoastra_script_options_t;
typedef struct neoastra_decision_response { uint32_t size; uint32_t version; neoastra_decision_action_t action; neoastra_string_view_t text; const neoastra_string_view_t* paths; uint32_t path_count; uint32_t persist; neoastra_string_view_t secondary_text; neoastra_view_t* target_view; uint32_t selected_index; uint32_t reserved; } neoastra_decision_response_t;
typedef struct neoastra_download_info { uint32_t size; uint32_t version; uint64_t id; neoastra_download_state_t state; uint32_t can_pause; neoastra_string_view_t source_uri; neoastra_string_view_t destination_path; uint64_t bytes_received; uint64_t total_bytes; neoastra_string_view_t failure_reason; } neoastra_download_info_t;
typedef struct neoastra_runtime_info { uint32_t size; uint32_t version; neoastra_string_view_t backend_name; neoastra_string_view_t backend_version; neoastra_string_view_t browser_version; neoastra_string_view_t operating_system; neoastra_string_view_t architecture; uint64_t build_features; uint32_t debug_build; uint32_t reserved; } neoastra_runtime_info_t;
typedef struct neoastra_cookie { uint32_t size; uint32_t version; neoastra_string_view_t name; neoastra_string_view_t value; neoastra_string_view_t domain; neoastra_string_view_t path; int64_t expires_unix_ms; uint32_t flags; uint32_t same_site; } neoastra_cookie_t;
struct neoastra_resource_request { uint32_t size; uint32_t version; neoastra_string_view_t uri; neoastra_string_view_t method; neoastra_string_view_t headers; neoastra_string_view_t initiating_origin; neoastra_resource_kind_t resource_kind; uint32_t main_frame; const uint8_t* body; uint64_t body_length; };
typedef void (NEOASTRA_CALL *neoastra_context_release_callback_t)(void* context);
struct neoastra_resource_response { uint32_t size; uint32_t version; uint32_t status_code; neoastra_resource_body_kind_t body_kind; neoastra_string_view_t reason_phrase; neoastra_string_view_t headers; neoastra_string_view_t mime_type; uint64_t content_length; const uint8_t* bytes; uint64_t byte_length; neoastra_string_view_t file_path; void* release_context; neoastra_context_release_callback_t release; };
typedef neoastra_result_t (NEOASTRA_CALL *neoastra_resource_provider_callback_t)(void* context, const neoastra_resource_request_t* request, neoastra_resource_response_t* response);
struct neoastra_custom_scheme { uint32_t size; uint32_t version; neoastra_string_view_t name; uint32_t flags; uint32_t allowed_origin_count; const neoastra_string_view_t* allowed_origins; neoastra_resource_provider_callback_t resource_provider; void* resource_provider_context; neoastra_context_release_callback_t release_resource_provider_context; };

typedef void (NEOASTRA_CALL *neoastra_environment_created_callback_t)(void*, neoastra_result_t, neoastra_environment_t*, const neoastra_error_t*);
typedef void (NEOASTRA_CALL *neoastra_profile_created_callback_t)(void*, neoastra_result_t, neoastra_profile_t*, const neoastra_error_t*);
typedef void (NEOASTRA_CALL *neoastra_view_created_callback_t)(void*, neoastra_result_t, neoastra_view_t*, const neoastra_error_t*);
typedef void (NEOASTRA_CALL *neoastra_string_callback_t)(void*, neoastra_result_t, neoastra_string_view_t, const neoastra_error_t*);
typedef void (NEOASTRA_CALL *neoastra_completion_callback_t)(void*, neoastra_result_t, const neoastra_error_t*);
typedef void (NEOASTRA_CALL *neoastra_buffer_callback_t)(void*, neoastra_result_t, neoastra_buffer_t*, const neoastra_error_t*);

NEOASTRA_API uint32_t NEOASTRA_CALL neoastra_get_abi_version_major(void);
NEOASTRA_API uint32_t NEOASTRA_CALL neoastra_get_abi_version_minor(void);
NEOASTRA_API neoastra_string_view_t NEOASTRA_CALL neoastra_get_version(void);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_get_runtime_info(neoastra_runtime_info_t*, neoastra_error_t**);

#define NEOASTRA_DECLARE_LIFETIME(name) NEOASTRA_API void NEOASTRA_CALL neoastra_##name##_retain(neoastra_##name##_t*); NEOASTRA_API void NEOASTRA_CALL neoastra_##name##_release(neoastra_##name##_t*)
NEOASTRA_DECLARE_LIFETIME(app);
NEOASTRA_DECLARE_LIFETIME(environment);
NEOASTRA_DECLARE_LIFETIME(profile);
NEOASTRA_DECLARE_LIFETIME(window);
NEOASTRA_DECLARE_LIFETIME(view);
NEOASTRA_DECLARE_LIFETIME(operation);
NEOASTRA_DECLARE_LIFETIME(decision);
NEOASTRA_DECLARE_LIFETIME(download);
NEOASTRA_DECLARE_LIFETIME(error);
NEOASTRA_DECLARE_LIFETIME(buffer);
NEOASTRA_DECLARE_LIFETIME(stream);
#undef NEOASTRA_DECLARE_LIFETIME

NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_error_get_code(const neoastra_error_t*);
NEOASTRA_API int64_t NEOASTRA_CALL neoastra_error_get_native_code(const neoastra_error_t*);
NEOASTRA_API neoastra_string_view_t NEOASTRA_CALL neoastra_error_get_domain(const neoastra_error_t*);
NEOASTRA_API neoastra_string_view_t NEOASTRA_CALL neoastra_error_get_message(const neoastra_error_t*);
NEOASTRA_API const uint8_t* NEOASTRA_CALL neoastra_buffer_get_data(const neoastra_buffer_t*);
NEOASTRA_API uint64_t NEOASTRA_CALL neoastra_buffer_get_length(const neoastra_buffer_t*);
NEOASTRA_API void NEOASTRA_CALL neoastra_operation_cancel(neoastra_operation_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_decision_defer(neoastra_decision_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_decision_complete(neoastra_decision_t*, const neoastra_decision_response_t*, neoastra_error_t**);
NEOASTRA_API neoastra_decision_kind_t NEOASTRA_CALL neoastra_decision_get_kind(const neoastra_decision_t*);
NEOASTRA_API neoastra_decision_action_t NEOASTRA_CALL neoastra_decision_get_default_action(const neoastra_decision_t*);
NEOASTRA_API uint64_t NEOASTRA_CALL neoastra_decision_get_deadline_ns(const neoastra_decision_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_download_get_info(const neoastra_download_t*, neoastra_download_info_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_download_cancel(neoastra_download_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_download_pause(neoastra_download_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_download_resume(neoastra_download_t*);

NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_app_create(const neoastra_app_options_t*, neoastra_app_t**, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_app_attach(const neoastra_app_options_t*, neoastra_app_t**, neoastra_error_t**);
/* Must be called on the owning UI thread before an attached host stops pumping.
   Drains accepted dispatch work, rejects new work, and completes platform teardown. Idempotent after shutdown. */
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_app_detach(neoastra_app_t*, neoastra_error_t**);
NEOASTRA_API int32_t NEOASTRA_CALL neoastra_app_run(neoastra_app_t*);
NEOASTRA_API void NEOASTRA_CALL neoastra_app_quit(neoastra_app_t*, int32_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_app_dispatch(neoastra_app_t*, neoastra_dispatch_callback_t, void*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_app_set_event_callback(neoastra_app_t*, neoastra_event_callback_t, void*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_app_create_window(neoastra_app_t*, const neoastra_window_options_t*, neoastra_window_t**, neoastra_error_t**);
NEOASTRA_API uint64_t NEOASTRA_CALL neoastra_app_get_window_count(const neoastra_app_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_app_get_window(neoastra_app_t*, uint64_t, neoastra_window_t**);

NEOASTRA_API uint64_t NEOASTRA_CALL neoastra_window_get_id(const neoastra_window_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_get_bounds(const neoastra_window_t*, neoastra_rect_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_set_bounds(neoastra_window_t*, neoastra_rect_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_get_minimum_size(const neoastra_window_t*, neoastra_size_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_set_minimum_size(neoastra_window_t*, neoastra_size_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_get_maximum_size(const neoastra_window_t*, neoastra_size_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_set_maximum_size(neoastra_window_t*, neoastra_size_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_get_state(const neoastra_window_t*, neoastra_window_state_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_set_state(neoastra_window_t*, neoastra_window_state_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_get_attribute(const neoastra_window_t*, neoastra_window_attribute_t, uint32_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_set_attribute(neoastra_window_t*, neoastra_window_attribute_t, uint32_t);
NEOASTRA_API neoastra_string_view_t NEOASTRA_CALL neoastra_window_get_title(const neoastra_window_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_set_title(neoastra_window_t*, neoastra_string_view_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_show(neoastra_window_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_hide(neoastra_window_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_activate(neoastra_window_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_begin_drag(neoastra_window_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_begin_resize(neoastra_window_t*, neoastra_window_resize_edge_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_close(neoastra_window_t*);
/* Internal/backend teardown entry point. It bypasses close negotiation and is not exposed to renderers. */
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_force_close(neoastra_window_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_window_get_native_handle(neoastra_window_t*, neoastra_native_handle_kind_t, neoastra_native_handle_t*);

NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_environment_create_async(neoastra_app_t*, const neoastra_environment_options_t*, neoastra_environment_created_callback_t, void*, neoastra_operation_t**, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_environment_create_profile_async(neoastra_environment_t*, const neoastra_profile_options_t*, neoastra_profile_created_callback_t, void*, neoastra_operation_t**, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_environment_create_view_async(neoastra_environment_t*, const neoastra_view_options_t*, neoastra_view_created_callback_t, void*, neoastra_operation_t**, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_environment_get_capability(const neoastra_environment_t*, neoastra_capability_t, neoastra_capability_info_t*);

NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_profile_get_cookies_async(neoastra_profile_t*, neoastra_string_view_t, neoastra_buffer_callback_t, void*, neoastra_operation_t**, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_profile_set_cookie_async(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t**, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_profile_delete_cookie_async(neoastra_profile_t*, const neoastra_cookie_t*, neoastra_completion_callback_t, void*, neoastra_operation_t**, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_profile_clear_data_async(neoastra_profile_t*, neoastra_data_kind_t, int64_t, int64_t, neoastra_completion_callback_t, void*, neoastra_operation_t**, neoastra_error_t**);

NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_set_event_callback(neoastra_view_t*, neoastra_event_callback_t, void*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_set_bounds(neoastra_view_t*, neoastra_rect_t, uint32_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_navigate(neoastra_view_t*, neoastra_string_view_t, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_navigate_request(neoastra_view_t*, neoastra_string_view_t, neoastra_string_view_t, neoastra_string_view_t, const uint8_t*, uint64_t, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_load_html(neoastra_view_t*, neoastra_string_view_t, neoastra_string_view_t, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_stop(neoastra_view_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_reload(neoastra_view_t*, uint32_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_go_back(neoastra_view_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_go_forward(neoastra_view_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_evaluate_script_async(neoastra_view_t*, neoastra_string_view_t, neoastra_string_callback_t, void*, neoastra_operation_t**, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_add_script_async(neoastra_view_t*, neoastra_string_view_t, const neoastra_script_options_t*, neoastra_string_callback_t, void*, neoastra_operation_t**, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_remove_script(neoastra_view_t*, neoastra_string_view_t);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_post_message(neoastra_view_t*, neoastra_string_view_t, uint32_t, neoastra_error_t**);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_get_zoom_factor(const neoastra_view_t*, double*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_set_zoom_factor(neoastra_view_t*, double);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_view_get_native_handle(neoastra_view_t*, neoastra_native_handle_kind_t, neoastra_native_handle_t*);
NEOASTRA_API neoastra_result_t NEOASTRA_CALL neoastra_query_extension(const void*, neoastra_string_view_t, uint32_t, const void**);

#ifdef __cplusplus
}
#endif
#endif
