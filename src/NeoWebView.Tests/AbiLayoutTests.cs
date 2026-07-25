// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;
using NeoWebView.Interop.Generated;

namespace NeoWebView.Tests;

[TestClass]
public sealed class AbiLayoutTests
{
    [TestMethod]
    public void GeneratedStructuresMatchNativeAbi17Layout()
    {
        Assert.AreEqual(8, IntPtr.Size, "ABI 1.7 is validated for the current 64-bit primary targets.");

        AssertLayout<NativeMethods.neo_webview_string_view>(16, (nameof(NativeMethods.neo_webview_string_view.length), 8));
        AssertLayout<NativeMethods.neo_webview_struct_header>(8, (nameof(NativeMethods.neo_webview_struct_header.version), 4));
        AssertLayout<NativeMethods.neo_webview_point>(8, (nameof(NativeMethods.neo_webview_point.y), 4));
        AssertLayout<NativeMethods.neo_webview_size>(8, (nameof(NativeMethods.neo_webview_size.height), 4));
        AssertLayout<NativeMethods.neo_webview_rect>(16, (nameof(NativeMethods.neo_webview_rect.width), 8));
        AssertLayout<NativeMethods.neo_webview_color>(4, (nameof(NativeMethods.neo_webview_color.alpha), 3));
        AssertLayout<NativeMethods.neo_webview_native_parent>(24, (nameof(NativeMethods.neo_webview_native_parent.handle), 16));
        AssertLayout<NativeMethods.neo_webview_native_handle>(24, (nameof(NativeMethods.neo_webview_native_handle.value), 16));
        AssertLayout<NativeMethods.neo_webview_event_header>(32, (nameof(NativeMethods.neo_webview_event_header.sequence), 16));
        AssertLayout<NativeMethods.neo_webview_event>(160, (nameof(NativeMethods.neo_webview_event.download), 152));
        AssertLayout<NativeMethods.neo_webview_capability_info>(40, (nameof(NativeMethods.neo_webview_capability_info.details), 24));
        AssertLayout<NativeMethods.neo_webview_app_options>(56, (nameof(NativeMethods.neo_webview_app_options.log_callback), 40));
        AssertLayout<NativeMethods.neo_webview_environment_options>(96, (nameof(NativeMethods.neo_webview_environment_options.custom_scheme_stride), 88));
        AssertLayout<NativeMethods.neo_webview_profile_options>(32, (nameof(NativeMethods.neo_webview_profile_options.ephemeral), 24));
        AssertLayout<NativeMethods.neo_webview_window_options>(80, (nameof(NativeMethods.neo_webview_window_options.background_color), 72));
        AssertLayout<NativeMethods.neo_webview_view_options>(104, (nameof(NativeMethods.neo_webview_view_options.bridge_origins), 96));
        AssertLayout<NativeMethods.neo_webview_script_options>(40, (nameof(NativeMethods.neo_webview_script_options.world_name), 24));
        AssertLayout<NativeMethods.neo_webview_decision_response>(80, (nameof(NativeMethods.neo_webview_decision_response.target_view), 64));
        AssertLayout<NativeMethods.neo_webview_download_info>(88, (nameof(NativeMethods.neo_webview_download_info.failure_reason), 72));
        AssertLayout<NativeMethods.neo_webview_runtime_info>(104, (nameof(NativeMethods.neo_webview_runtime_info.build_features), 88));
        AssertLayout<NativeMethods.neo_webview_cookie>(88, (nameof(NativeMethods.neo_webview_cookie.expires_unix_ms), 72));
        AssertLayout<NativeMethods.neo_webview_resource_request>(96, (nameof(NativeMethods.neo_webview_resource_request.body_length), 88));
        AssertLayout<NativeMethods.neo_webview_resource_response>(120, (nameof(NativeMethods.neo_webview_resource_response.release), 112));
        AssertLayout<NativeMethods.neo_webview_custom_scheme>(64, (nameof(NativeMethods.neo_webview_custom_scheme.resource_provider), 40));
    }

    [TestMethod]
    public void GeneratedEnumsMatchNativeAbi17ValuesAndStorage()
    {
        AssertEnum<int, NativeMethods.neo_webview_result>(-14, 0);
        AssertEnum<uint, NativeMethods.neo_webview_support_level>(0, 3);
        AssertEnum<uint, NativeMethods.neo_webview_app_shutdown_mode>(0, 2);
        AssertEnum<uint, NativeMethods.neo_webview_native_parent_kind>(0, 3);
        AssertEnum<uint, NativeMethods.neo_webview_native_handle_kind>(0, 9);
        AssertEnum<uint, NativeMethods.neo_webview_window_state>(0, 3);
        AssertEnum<uint, NativeMethods.neo_webview_option_state>(0, 2);
        AssertEnum<uint, NativeMethods.neo_webview_script_injection_time>(0, 1);
        AssertEnum<uint, NativeMethods.neo_webview_decision_action>(0, 6);
        AssertEnum<uint, NativeMethods.neo_webview_decision_kind>(0, 10);
        AssertEnum<uint, NativeMethods.neo_webview_script_dialog_kind>(0, 3);
        AssertEnum<uint, NativeMethods.neo_webview_download_state>(0, 4);
        AssertEnum<uint, NativeMethods.neo_webview_permission_kind>(0, 12);
        AssertEnum<uint, NativeMethods.neo_webview_process_failure_kind>(0, 3);
        AssertEnum<uint, NativeMethods.neo_webview_event_type>(0, 33);
        AssertEnum<uint, NativeMethods.neo_webview_capability>(0, 32);
        AssertEnum<uint, NativeMethods.neo_webview_log_level>(0, 5);
        AssertEnum<uint, NativeMethods.neo_webview_resource_kind>(0, 12);
        AssertEnum<uint, NativeMethods.neo_webview_resource_body_kind>(0, 2);
    }

    [TestMethod]
    public void PublicManagedEnumsRemainEquivalentToGeneratedInterop()
    {
        AssertEquivalent<NeoErrorCode, NativeMethods.neo_webview_result>();
        AssertEquivalent<NeoSupportLevel, NativeMethods.neo_webview_support_level>();
        AssertEquivalent<NeoApplicationShutdownMode, NativeMethods.neo_webview_app_shutdown_mode>();
        AssertEquivalent<NeoWindowState, NativeMethods.neo_webview_window_state>();
        AssertEquivalent<NeoOptionState, NativeMethods.neo_webview_option_state>();
        AssertEquivalent<NeoDecisionAction, NativeMethods.neo_webview_decision_action>();
        AssertEquivalent<NeoScriptDialogKind, NativeMethods.neo_webview_script_dialog_kind>();
        AssertEquivalent<NeoDownloadState, NativeMethods.neo_webview_download_state>();
        AssertEquivalent<NeoPermissionKind, NativeMethods.neo_webview_permission_kind>();
        AssertEquivalent<NeoProcessFailureKind, NativeMethods.neo_webview_process_failure_kind>();
        AssertEquivalent<NeoCapability, NativeMethods.neo_webview_capability>();
        AssertEquivalent<NeoLogLevel, NativeMethods.neo_webview_log_level>();
        AssertEquivalent<NeoResourceKind, NativeMethods.neo_webview_resource_kind>();

        var nativeHandles = Values<NativeMethods.neo_webview_native_handle_kind>().Skip(1).ToArray();
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
