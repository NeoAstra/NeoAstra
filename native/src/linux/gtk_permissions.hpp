#pragma once

#include "neoastra.h"

#include <glib-object.h>
#include <webkit/webkit.h>

// The kind of a WebKitGTK permission request that is not a capture (whose kinds depend on the devices of the request), from
// its type alone. WebKitGTK 2.42 asks for the clipboard (navigator.clipboard.readText and read, document.execCommand("paste"))
// with a request of its own; its type is found by name, so that the library still loads with a WebKitGTK that does not have
// it, and a type that is not registered yet is no such request.
inline neoastra_permission_kind_t neo_gtk_permission_kind(GType type) noexcept {
    if (g_type_is_a(type, WEBKIT_TYPE_GEOLOCATION_PERMISSION_REQUEST)) return NEOASTRA_PERMISSION_GEOLOCATION;
    if (g_type_is_a(type, WEBKIT_TYPE_NOTIFICATION_PERMISSION_REQUEST)) return NEOASTRA_PERMISSION_NOTIFICATIONS;
    const GType clipboard = g_type_from_name("WebKitClipboardPermissionRequest");
    if (clipboard != 0 && g_type_is_a(type, clipboard)) return NEOASTRA_PERMISSION_CLIPBOARD_READ;
    return NEOASTRA_PERMISSION_UNKNOWN;
}
