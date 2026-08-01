// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using NeoAstra.Interop.Generated;

namespace NeoAstra.Tests;

[TestClass]
public sealed class AbiLayoutTests
{
    [TestMethod]
    public void GeneratedStructuresMatchNativeAbi18Layout()
    {
        Assert.AreEqual(8, IntPtr.Size, "ABI 1.0 is validated for the current 64-bit primary targets.");

        AssertLayout<NativeMethods.neoastra_string_view>(16, (nameof(NativeMethods.neoastra_string_view.length), 8));
        AssertLayout<NativeMethods.neoastra_struct_header>(8, (nameof(NativeMethods.neoastra_struct_header.version), 4));
        AssertLayout<NativeMethods.neoastra_point>(8, (nameof(NativeMethods.neoastra_point.y), 4));
        AssertLayout<NativeMethods.neoastra_size>(8, (nameof(NativeMethods.neoastra_size.height), 4));
        AssertLayout<NativeMethods.neoastra_rect>(16, (nameof(NativeMethods.neoastra_rect.width), 8));
        AssertLayout<NativeMethods.neoastra_color>(4, (nameof(NativeMethods.neoastra_color.alpha), 3));
        AssertLayout<NativeMethods.neoastra_native_parent>(24, (nameof(NativeMethods.neoastra_native_parent.handle), 16));
        AssertLayout<NativeMethods.neoastra_native_handle>(24, (nameof(NativeMethods.neoastra_native_handle.value), 16));
        AssertLayout<NativeMethods.neoastra_event_header>(32, (nameof(NativeMethods.neoastra_event_header.sequence), 16));
        AssertLayout<NativeMethods.neoastra_event>(160, (nameof(NativeMethods.neoastra_event.download), 152));
        AssertLayout<NativeMethods.neoastra_capability_info>(40, (nameof(NativeMethods.neoastra_capability_info.details), 24));
        AssertLayout<NativeMethods.neoastra_app_options>(56, (nameof(NativeMethods.neoastra_app_options.log_callback), 40));
        AssertLayout<NativeMethods.neoastra_environment_options>(96, (nameof(NativeMethods.neoastra_environment_options.custom_scheme_stride), 88));
        AssertLayout<NativeMethods.neoastra_profile_options>(32, (nameof(NativeMethods.neoastra_profile_options.ephemeral), 24));
        AssertLayout<NativeMethods.neoastra_window_options>(80, (nameof(NativeMethods.neoastra_window_options.background_color), 72));
        AssertLayout<NativeMethods.neoastra_view_options>(104,
            (nameof(NativeMethods.neoastra_view_options.bridge_policy), 92),
            (nameof(NativeMethods.neoastra_view_options.bridge_origins), 96));
        AssertLayout<NativeMethods.neoastra_script_options>(40, (nameof(NativeMethods.neoastra_script_options.world_name), 24));
        AssertLayout<NativeMethods.neoastra_decision_response>(80, (nameof(NativeMethods.neoastra_decision_response.target_view), 64));
        AssertLayout<NativeMethods.neoastra_download_info>(88, (nameof(NativeMethods.neoastra_download_info.failure_reason), 72));
        AssertLayout<NativeMethods.neoastra_runtime_info>(104, (nameof(NativeMethods.neoastra_runtime_info.build_features), 88));
        AssertLayout<NativeMethods.neoastra_cookie>(88, (nameof(NativeMethods.neoastra_cookie.expires_unix_ms), 72));
        AssertLayout<NativeMethods.neoastra_resource_request>(96, (nameof(NativeMethods.neoastra_resource_request.body_length), 88));
        AssertLayout<NativeMethods.neoastra_resource_response>(120, (nameof(NativeMethods.neoastra_resource_response.release), 112));
        AssertLayout<NativeMethods.neoastra_custom_scheme>(64, (nameof(NativeMethods.neoastra_custom_scheme.resource_provider), 40));
    }

    [TestMethod]
    public void GeneratedEnumsMatchNativeAbi19ValuesAndStorage()
    {
        AssertEnum<int, NativeMethods.neoastra_result>(-14, 0);
        AssertEnum<uint, NativeMethods.neoastra_support_level>(0, 3);
        AssertEnum<uint, NativeMethods.neoastra_app_shutdown_mode>(0, 2);
        AssertEnum<uint, NativeMethods.neoastra_native_parent_kind>(0, 3);
        AssertEnum<uint, NativeMethods.neoastra_native_handle_kind>(0, 9);
        AssertEnum<uint, NativeMethods.neoastra_window_state>(0, 3);
        AssertEnum<uint, NativeMethods.neoastra_window_attribute>(0, 3);
        AssertEnum<uint, NativeMethods.neoastra_window_resize_edge>(0, 7);
        AssertEnum<uint, NativeMethods.neoastra_window_close_reason>(0, 5);
        AssertEnum<uint, NativeMethods.neoastra_option_state>(0, 2);
        AssertEnum<uint, NativeMethods.neoastra_script_injection_time>(0, 1);
        AssertEnum<uint, NativeMethods.neoastra_decision_action>(0, 6);
        AssertEnum<uint, NativeMethods.neoastra_decision_kind>(0, 12);
        AssertEnum<uint, NativeMethods.neoastra_script_dialog_kind>(0, 3);
        AssertEnum<uint, NativeMethods.neoastra_download_state>(0, 4);
        AssertEnum<uint, NativeMethods.neoastra_permission_kind>(0, 12);
        AssertEnum<uint, NativeMethods.neoastra_process_failure_kind>(0, 3);
        AssertEnum<uint, NativeMethods.neoastra_event_type>(0, 37);
        AssertEnum<uint, NativeMethods.neoastra_capability>(0, 32);
        AssertEnum<uint, NativeMethods.neoastra_log_level>(0, 5);
        AssertEnum<uint, NativeMethods.neoastra_resource_kind>(0, 12);
        AssertEnum<uint, NativeMethods.neoastra_resource_body_kind>(0, 2);
        AssertEnum<uint, NativeMethods.neoastra_bridge_policy>(0, 2);
    }

    [TestMethod]
    public void PublicManagedEnumsRemainEquivalentToGeneratedInterop()
    {
        AssertEquivalent<NeoErrorCode, NativeMethods.neoastra_result>();
        AssertEquivalent<NeoSupportLevel, NativeMethods.neoastra_support_level>();
        AssertEquivalent<NeoApplicationShutdownMode, NativeMethods.neoastra_app_shutdown_mode>();
        AssertEquivalent<NeoWindowState, NativeMethods.neoastra_window_state>();
        AssertEquivalent<NeoWindowResizeEdge, NativeMethods.neoastra_window_resize_edge>();
        AssertEquivalent<NeoWindowCloseReason, NativeMethods.neoastra_window_close_reason>();
        AssertEquivalent<NeoOptionState, NativeMethods.neoastra_option_state>();
        AssertEquivalent<NeoBridgePolicy, NativeMethods.neoastra_bridge_policy>();
        AssertEquivalent<NeoDecisionAction, NativeMethods.neoastra_decision_action>();
        AssertEquivalent<NeoScriptDialogKind, NativeMethods.neoastra_script_dialog_kind>();
        AssertEquivalent<NeoDownloadState, NativeMethods.neoastra_download_state>();
        AssertEquivalent<NeoPermissionKind, NativeMethods.neoastra_permission_kind>();
        AssertEquivalent<NeoProcessFailureKind, NativeMethods.neoastra_process_failure_kind>();
        AssertEquivalent<NeoCapability, NativeMethods.neoastra_capability>();
        AssertEquivalent<NeoLogLevel, NativeMethods.neoastra_log_level>();
        AssertEquivalent<NeoResourceKind, NativeMethods.neoastra_resource_kind>();

        var nativeHandles = Values<NativeMethods.neoastra_native_handle_kind>().Skip(1).ToArray();
        CollectionAssert.AreEqual(Values<NeoNativeHandleKind>(), nativeHandles);
    }

    private static void AssertLayout<T>(int expectedSize, params (string Field, int Offset)[] fields)
        where T : struct
    {
        Assert.AreEqual(expectedSize, Marshal.SizeOf<T>(), typeof(T).Name);
        foreach (var (field, offset) in fields)
        {
            Assert.AreEqual((nint)offset, Marshal.OffsetOf<T>(field), $"{typeof(T).Name}.{field}");
        }
    }

    private static void AssertEnum<TStorage, TEnum>(long first, long last)
        where TStorage : struct
        where TEnum : struct, Enum
    {
        Assert.AreEqual(typeof(TStorage), Enum.GetUnderlyingType(typeof(TEnum)), typeof(TEnum).Name);
        CollectionAssert.AreEqual(Enumerable.Range(checked((int)first), checked((int)(last - first + 1))).Select(static value => (long)value).ToArray(), Values<TEnum>());
    }

    private static void AssertEquivalent<TManaged, TNative>()
        where TManaged : struct, Enum
        where TNative : struct, Enum
        => CollectionAssert.AreEqual(Values<TManaged>(), Values<TNative>(), typeof(TManaged).Name);

    private static long[] Values<TEnum>() where TEnum : struct, Enum
        => Enum.GetValues<TEnum>().Select(static value => Convert.ToInt64(value)).Order().ToArray();
}
