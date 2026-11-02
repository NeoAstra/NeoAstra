#pragma once

#include <gio/gio.h>
#include <glib.h>

// What the choice of the renderer of GTK depends on, read once GTK has opened its display.
struct neo_gtk_renderer_facts {
    const char* gsk_renderer{};      // GSK_RENDERER, or null when it is not set.
    unsigned gtk_minor_version{};
    bool gtk_initialized_before{};   // By the application that hosts NeoAstra, which has chosen the renderer of its windows.
    bool wayland{};
    bool nvidia_driver_loaded{};
};

// GTK 4.16 and later draw windows with Vulkan on Wayland, and with GL on X11. The Vulkan renderer refuses the DMA-BUF
// formats of the NVIDIA driver (its compressed modifiers) in which WebKitGTK hands each frame of a page: GTK then reads
// the whole frame back to memory and uploads it again, on its main thread, at every frame. GL imports those frames as
// they are. A renderer that GSK_RENDERER names, even "vulkan", stays the one of the user; an empty one names none and
// GTK ignores it. Returns the renderer to name in GSK_RENDERER, or null to leave the variable alone.
inline const char* neo_gtk_renderer_override(const neo_gtk_renderer_facts& facts) noexcept {
    if ((facts.gsk_renderer != nullptr && *facts.gsk_renderer != '\0') || facts.gtk_initialized_before || !facts.wayland ||
        !facts.nvidia_driver_loaded || facts.gtk_minor_version < 16)
        return nullptr;
    // Up to GTK 4.18, ngl names the GL renderer, and gl the old one or a warning. From GTK 4.20 on, gl names it and ngl
    // draws a warning. 4.19 is a development series.
    return facts.gtk_minor_version < 20 ? "ngl" : "gl";
}

// The kernel modules of the NVIDIA driver, proprietary or open, are loaded as soon as it drives a GPU, of a laptop
// with two GPUs too. Nouveau loads neither.
inline bool neo_nvidia_driver_loaded() noexcept {
    return g_file_test("/sys/module/nvidia_drm", G_FILE_TEST_IS_DIR) || g_file_test("/proc/driver/nvidia/version", G_FILE_TEST_EXISTS);
}

// An application that opens an address gets the environment of this process, without the renderer that NeoAstra chose
// for its own windows: the same name means another renderer, or a warning, in another version of GTK. Null when the
// variable is not NeoAstra's. The caller releases the context.
inline GAppLaunchContext* neo_external_launch_context(bool renderer_set_by_neoastra) noexcept {
    if (!renderer_set_by_neoastra) return nullptr;
    auto* context = g_app_launch_context_new();
    g_app_launch_context_unsetenv(context, "GSK_RENDERER");
    return context;
}
