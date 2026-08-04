// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NeoAstra.Desktop.Notifications;

/// <summary>Modern UserNotifications presenter with custom-dismiss action routing.</summary>
internal sealed unsafe partial class MacNotifications : INeoNotifications, INeoApplicationBoundDesktopService, IAsyncDisposable
{
    private static readonly object ClassLock = new();
    private static readonly ConcurrentDictionary<nint, MacNotifications> Owners = new();
    private static nint s_delegateClass, s_stackBlock;
    private static BlockDescriptor* s_blockDescriptor;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, nint> _categories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShowState> _pendingShows = new(StringComparer.Ordinal);
    private readonly HashSet<SettingsState> _pendingSettings = [];
    private readonly HashSet<AuthorizationState> _pendingAuthorizations = [];
    private NeoDispatcher? _dispatcher;
    private NeoApplication? _application;
    private nint _center, _delegate;
    private long _generation;
    private bool _disposed;
    private readonly string _nativePrefix;

    internal MacNotifications() : this("neoastra.application") { }
    internal MacNotifications(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        _nativePrefix = "neoastra." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(applicationId)))[..24].ToLowerInvariant();
    }

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0, "Modern UNUserNotificationCenter authorization status and first-display request, identifier replacement, action/default activation, custom dismissal, explicit pending/delivered removal, generation routing, and deterministic delegate/category teardown.");
    public event EventHandler<NeoNotificationActivation>? Activated;

    void INeoApplicationBoundDesktopService.BindApplication(NeoApplication application) { ArgumentNullException.ThrowIfNull(application); if (_application is not null && !ReferenceEquals(_application, application)) throw new InvalidOperationException("The notification presenter is already bound to an application."); _application = application; _dispatcher = application.Dispatcher; }

    public ValueTask<NeoNotificationPermissionStatus> GetPermissionStatusAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The macOS notification presenter must be bound to the UI dispatcher before use.");
        return dispatcher.CheckAccess() ? new(MacNotificationTasks.WaitAsync(StartSettingsQuery(), TimeSpan.FromSeconds(10), cancellationToken)) : new(MacNotificationTasks.DispatchAndWaitAsync(dispatcher.InvokeAsync(StartSettingsQuery, cancellationToken), TimeSpan.FromSeconds(10), cancellationToken));
    }

    public ValueTask<NeoDesktopStatus> ShowAsync(NeoNotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate(); var dispatcher = _dispatcher ?? throw new InvalidOperationException("The macOS notification presenter must be bound to the UI dispatcher before use.");
        return new(MacNotificationTasks.ShowWithAuthorizationAsync(dispatcher, StartSettingsQuery, StartAuthorizationRequest, () => StartShow(request), cancellationToken));
    }

    public ValueTask<NeoDesktopStatus> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        NeoNotificationRequest.ValidateId(id, nameof(id)); var dispatcher = _dispatcher ?? throw new InvalidOperationException("The macOS notification presenter must be bound to the UI dispatcher before use.");
        return dispatcher.CheckAccess() ? ValueTask.FromResult(RemoveOnDispatcher(id)) : dispatcher.InvokeAsync(() => RemoveOnDispatcher(id), cancellationToken);
    }

    public ValueTask DisposeAsync() { var dispatcher = _dispatcher; if (dispatcher is not null && !dispatcher.CheckAccess()) return dispatcher.InvokeAsync(DisposeOnDispatcher); DisposeOnDispatcher(); return ValueTask.CompletedTask; }

    private Task<NeoNotificationPermissionStatus> StartSettingsQuery()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); EnsureCenter();if(_pendingSettings.Count>=256)return Task.FromResult(NeoNotificationPermissionStatus.Unknown); var state = new SettingsState(this); _pendingSettings.Add(state); var handle = GCHandle.Alloc(state); try { var block = CreateBlock((nint)(delegate* unmanaged[Cdecl]<Block*, nint, void>)&SettingsCompleted, GCHandle.ToIntPtr(handle)); Native.SendBlock(_center, Native.GetSelector("getNotificationSettingsWithCompletionHandler:"), &block); return state.Completion.Task; } catch { _pendingSettings.Remove(state); handle.Free(); throw; }
    }

    private Task<NeoNotificationPermissionStatus> StartAuthorizationRequest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); EnsureCenter(); if (_pendingAuthorizations.Count >= 256) return Task.FromResult(NeoNotificationPermissionStatus.Unknown); var state = new AuthorizationState(this); _pendingAuthorizations.Add(state); var handle = GCHandle.Alloc(state);
        try { var block = CreateBlock((nint)(delegate* unmanaged[Cdecl]<Block*, byte, nint, void>)&AuthorizationCompleted, GCHandle.ToIntPtr(handle)); Native.SendAuthorization(_center, Native.GetSelector("requestAuthorizationWithOptions:completionHandler:"), 0x6, &block); return state.Completion.Task; }
        catch { _pendingAuthorizations.Remove(state); handle.Free(); throw; }
    }

    internal static NeoDesktopStatus PermissionFailureStatus(NeoNotificationPermissionStatus status) => status switch
    {
        NeoNotificationPermissionStatus.Denied => NeoDesktopStatus.Denied,
        NeoNotificationPermissionStatus.Unsupported => NeoDesktopStatus.Unsupported,
        _ => NeoDesktopStatus.Failed,
    };

    private Task<NeoDesktopStatus> StartShow(NeoNotificationRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); EnsureCenter(); if (!_entries.ContainsKey(request.Id) && _entries.Count >= 256) return Task.FromResult(NeoDesktopStatus.LimitExceeded); if (!_pending.Add(request.Id)) return Task.FromResult(NeoDesktopStatus.Conflict); var generation = checked(++_generation); var categoryId = _nativePrefix + ".category." + request.Id; nint category = 0, content = 0, nativeRequest = 0, previousCategory = 0; Entry? previousEntry = null; var categoryRetained = false; var installed = false;
        try
        {
            category = CreateCategory(categoryId, request.Actions); content = Allocate("UNMutableNotificationContent");
            SetString(content, "setTitle:", request.Title); SetString(content, "setBody:", request.Body); SetString(content, "setCategoryIdentifier:", categoryId);
            using var generationValue = NativeString.Create(generation.ToString(CultureInfo.InvariantCulture)); using var generationKey = NativeString.Create("neoastraGeneration"); var userInfo = Native.Send2(Native.GetClass("NSDictionary"), Native.GetSelector("dictionaryWithObject:forKey:"), generationValue.Value, generationKey.Value); Native.SendVoidArg(content, Native.GetSelector("setUserInfo:"), userInfo);
            using var identifier = NativeString.Create(NativeId(request.Id)); nativeRequest = Native.Send3(Native.GetClass("UNNotificationRequest"), Native.GetSelector("requestWithIdentifier:content:trigger:"), identifier.Value, content, 0); if (nativeRequest == 0) throw new InvalidOperationException("Unable to allocate a notification request.");
            Native.SendVoid(category, Native.GetSelector("retain")); categoryRetained = true; _categories.TryGetValue(request.Id, out previousCategory); _entries.TryGetValue(request.Id, out previousEntry); _categories[request.Id] = category; installed = true; RebuildCategories();
            var snapshot = request with { Actions = Array.AsReadOnly(request.Actions.ToArray()) }; _entries[request.Id] = new(generation, snapshot);
            var state = new ShowState(this, request.Id, generation, previousEntry, previousCategory); _pendingShows[request.Id] = state; var handle = GCHandle.Alloc(state); var block = CreateBlock((nint)(delegate* unmanaged[Cdecl]<Block*, nint, void>)&ShowCompleted, GCHandle.ToIntPtr(handle)); Native.SendRequest(_center, Native.GetSelector("addNotificationRequest:withCompletionHandler:"), nativeRequest, &block); return state.Completion.Task;
        }
        catch
        {
            _pending.Remove(request.Id); _pendingShows.Remove(request.Id);
            if (installed)
            {
                _categories.Remove(request.Id); _entries.Remove(request.Id);
                if (categoryRetained) { Native.SendVoid(category, Native.GetSelector("release")); categoryRetained = false; }
                if (previousEntry is not null) { _entries[request.Id] = previousEntry; _categories[request.Id] = previousCategory; }
                try { RebuildCategories(); } catch { }
            }
            else if (categoryRetained) Native.SendVoid(category, Native.GetSelector("release"));
            throw;
        }
        finally { if (content != 0) Native.SendVoid(content, Native.GetSelector("release")); }
    }

    private NeoDesktopStatus RemoveOnDispatcher(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); EnsureCenter(); if (_pending.Contains(id)) return NeoDesktopStatus.Conflict; if (!_entries.Remove(id)) return NeoDesktopStatus.NotFound; RemoveNative(id); if (_categories.Remove(id, out var category)) Native.SendVoid(category, Native.GetSelector("release")); RebuildCategories(); return NeoDesktopStatus.Success;
    }

    private void RemoveNative(string id)
    {
        using var value = NativeString.Create(NativeId(id)); var values = stackalloc nint[1] { value.Value }; var array = Native.SendPointerCount(Native.GetClass("NSArray"), Native.GetSelector("arrayWithObjects:count:"), values, 1); Native.SendVoidArg(_center, Native.GetSelector("removePendingNotificationRequestsWithIdentifiers:"), array); Native.SendVoidArg(_center, Native.GetSelector("removeDeliveredNotificationsWithIdentifiers:"), array);
    }

    private nint CreateCategory(string id, IReadOnlyList<NeoNotificationAction> actions)
    {
        var nativeActions = stackalloc nint[actions.Count]; var count = 0;
        try
        {
            foreach (var action in actions) { using var identifier = NativeString.Create(action.Id); using var title = NativeString.Create(action.Title); var value = Native.Send3(Native.GetClass("UNNotificationAction"), Native.GetSelector("actionWithIdentifier:title:options:"), identifier.Value, title.Value, 0); if (value == 0) throw new InvalidOperationException("Unable to allocate a notification action."); Native.SendVoid(value, Native.GetSelector("retain")); nativeActions[count++] = value; }
            var array = Native.SendPointerCount(Native.GetClass("NSArray"), Native.GetSelector("arrayWithObjects:count:"), nativeActions, (nuint)count); var empty = Native.Send(Native.GetClass("NSArray"), Native.GetSelector("array")); using var category = NativeString.Create(id); var result = Native.Send4(Native.GetClass("UNNotificationCategory"), Native.GetSelector("categoryWithIdentifier:actions:intentIdentifiers:options:"), category.Value, array, empty, 1); return result != 0 ? result : throw new InvalidOperationException("Unable to allocate a notification category.");
        }
        finally { for (var index = 0; index < count; index++) Native.SendVoid(nativeActions[index], Native.GetSelector("release")); }
    }

    private void RebuildCategories()
    {
        // UNUserNotificationCenter categories are process-global. Rebuild the union so one
        // application-bound presenter cannot erase categories owned by another presenter.
        lock (ClassLock)
        {
            var values = Owners.Values.SelectMany(static owner => owner._categories.Values).Distinct().ToArray();
            fixed (nint* pointer = values) { var array = Native.SendPointerCount(Native.GetClass("NSArray"), Native.GetSelector("arrayWithObjects:count:"), pointer, (nuint)values.Length); var set = Native.SendArg(Native.GetClass("NSSet"), Native.GetSelector("setWithArray:"), array); Native.SendVoidArg(_center, Native.GetSelector("setNotificationCategories:"), set); }
        }
    }

    private void CompleteShow(ShowState state, bool success)
    {
        void Complete()
        {
            if (_disposed || !_entries.TryGetValue(state.Id, out var entry) || entry.Generation != state.Generation) { state.Completion.TrySetResult(NeoDesktopStatus.Failed); return; }
            _pending.Remove(state.Id);
            _pendingShows.Remove(state.Id);
            if (success) { if (state.PreviousCategory != 0) Native.SendVoid(state.PreviousCategory, Native.GetSelector("release")); state.Completion.TrySetResult(NeoDesktopStatus.Success); return; }
            if (_categories.Remove(state.Id, out var currentCategory)) Native.SendVoid(currentCategory, Native.GetSelector("release"));
            if (state.PreviousEntry is not null) { _entries[state.Id] = state.PreviousEntry; _categories[state.Id] = state.PreviousCategory; } else _entries.Remove(state.Id);
            RebuildCategories(); state.Completion.TrySetResult(NeoDesktopStatus.Failed);
        }
        var dispatcher = _dispatcher; if (dispatcher is not null && !dispatcher.CheckAccess()) { try { _ = dispatcher.InvokeAsync(Complete); } catch { state.Completion.TrySetResult(NeoDesktopStatus.Failed); } } else Complete();
    }

    private void Response(string id, long generation, string actionId)
    {
        var prefix = _nativePrefix + "."; if (!id.StartsWith(prefix, StringComparison.Ordinal)) return; id = id[prefix.Length..];
        if (_disposed || !_entries.TryGetValue(id, out var entry) || entry.Generation != generation) return; var dismissed = actionId == "com.apple.UNNotificationDismissActionIdentifier"; string? action = null;
        if (!dismissed && actionId != "com.apple.UNNotificationDefaultActionIdentifier") { if (!entry.Request.Actions.Any(value => value.Id == actionId)) return; action = actionId; }
        _entries.Remove(id); if (_categories.Remove(id, out var category)) Native.SendVoid(category, Native.GetSelector("release")); RebuildCategories();
        var activation = new NeoNotificationActivation(id, action, entry.Request.ActivationData, dismissed); var accepted = _application?.QueueLaunchEvent(new NeoLaunchEvent(NeoLaunchReason.Extension, metadata: new Dictionary<string, string> { ["plugin"] = "neoastra.desktop.notifications", ["notification"] = id, ["action"] = action ?? string.Empty, ["dismissed"] = dismissed ? "true" : "false" })) ?? true; if (accepted) try { Activated?.Invoke(this, activation); } catch { }
    }

    private void CompleteSettings(SettingsState state, NeoNotificationPermissionStatus status)
    {
        void Complete() { if (!_pendingSettings.Remove(state) || _disposed) state.Completion.TrySetResult(NeoNotificationPermissionStatus.Unknown); else state.Completion.TrySetResult(status); }
        var dispatcher = _dispatcher; if (dispatcher is not null && !dispatcher.CheckAccess()) { try { _ = dispatcher.InvokeAsync(Complete); } catch { state.Completion.TrySetResult(NeoNotificationPermissionStatus.Unknown); } } else Complete();
    }

    private void CompleteAuthorization(AuthorizationState state, NeoNotificationPermissionStatus status)
    {
        void Complete() { if (!_pendingAuthorizations.Remove(state) || _disposed) state.Completion.TrySetResult(NeoNotificationPermissionStatus.Unknown); else state.Completion.TrySetResult(status); }
        var dispatcher = _dispatcher; if (dispatcher is not null && !dispatcher.CheckAccess()) { try { _ = dispatcher.InvokeAsync(Complete); } catch { state.Completion.TrySetResult(NeoNotificationPermissionStatus.Unknown); } } else Complete();
    }

    private void EnsureCenter()
    {
        if (_center != 0) return; EnsureDelegateClass(); EnsureBlockRuntime(); _center = Native.Send(Native.GetClass("UNUserNotificationCenter"), Native.GetSelector("currentNotificationCenter")); _delegate = Native.Send(Native.Send(s_delegateClass, Native.GetSelector("alloc")), Native.GetSelector("init")); if (_center == 0 || _delegate == 0 || !Owners.TryAdd(_delegate, this)) throw new PlatformNotSupportedException("UNUserNotificationCenter is unavailable."); Native.SendVoidArg(_center, Native.GetSelector("setDelegate:"), _delegate);
    }

    private static void EnsureDelegateClass()
    {
        if (s_delegateClass != 0) return; lock (ClassLock) { if (s_delegateClass != 0) return; var name = "NeoAstraUNDelegate_v1"u8; fixed (byte* pointer = name) { var value = Native.objc_lookUpClass(pointer); if (value == 0) { value = Native.objc_allocateClassPair(Native.GetClass("NSObject"), pointer, 0); Add(value, "userNotificationCenter:willPresentNotification:withCompletionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&WillPresent, "v@:@@@?"u8); Add(value, "userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidReceive, "v@:@@@?"u8); Native.objc_registerClassPair(value); } s_delegateClass = value; } }
        static void Add(nint type, string selector, nint callback, ReadOnlySpan<byte> encoding) { fixed (byte* pointer = encoding) if (!Native.class_addMethod(type, Native.GetSelector(selector), callback, pointer)) throw new InvalidOperationException("Unable to define a notification delegate callback."); }
    }
    private static void EnsureBlockRuntime() { if (s_stackBlock != 0) return; s_stackBlock = Native.dlsym(-2, "_NSConcreteStackBlock"); if (s_stackBlock == 0) throw new PlatformNotSupportedException("The Objective-C block runtime is unavailable."); s_blockDescriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor)); *s_blockDescriptor = new() { Size = (nuint)sizeof(Block) }; }
    private static Block CreateBlock(nint invoke, nint context) => new() { Isa = s_stackBlock, Invoke = invoke, Descriptor = s_blockDescriptor, Context = context };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void WillPresent(nint self, nint selector, nint center, nint notification, nint completion) { try { if (completion != 0) ((delegate* unmanaged[Cdecl]<nint, nuint, void>)((Block*)completion)->Invoke)((nint)completion, 0x1A); } catch { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void DidReceive(nint self, nint selector, nint center, nint response, nint completion)
    {
        try
        {
            if (Owners.ContainsKey(self)) { var notification = Native.Send(response, Native.GetSelector("notification")); var request = Native.Send(notification, Native.GetSelector("request")); var id = ReadString(Native.Send(request, Native.GetSelector("identifier"))); var action = ReadString(Native.Send(response, Native.GetSelector("actionIdentifier"))); var content = Native.Send(request, Native.GetSelector("content")); var info = Native.Send(content, Native.GetSelector("userInfo")); using var key = NativeString.Create("neoastraGeneration"); var generationText = ReadString(Native.SendArg(info, Native.GetSelector("objectForKey:"), key.Value)); if (id is not null && action is not null && long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)) foreach (var owner in Owners.Values.Distinct()) { var dispatcher = owner._dispatcher; if (dispatcher is not null) { try { _ = dispatcher.InvokeAsync(() => owner.Response(id, generation, action)); } catch { } } } }
        }
        catch { }
        finally { try { if (completion != 0) ((delegate* unmanaged[Cdecl]<nint, void>)((Block*)completion)->Invoke)((nint)completion); } catch { } }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void SettingsCompleted(Block* block, nint settings) { var handle = GCHandle.FromIntPtr(block->Context); try { var status = (long)Native.Send(settings, Native.GetSelector("authorizationStatus")); if (handle.Target is SettingsState state) state.Owner.CompleteSettings(state, status switch { 0 => NeoNotificationPermissionStatus.NotRequested, 1 => NeoNotificationPermissionStatus.Denied, 2 or 3 or 4 => NeoNotificationPermissionStatus.Granted, _ => NeoNotificationPermissionStatus.Unknown }); } catch (Exception exception) { if (handle.Target is SettingsState state) state.Completion.TrySetException(exception); } finally { handle.Free(); } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void AuthorizationCompleted(Block* block, byte granted, nint error) { var handle = GCHandle.FromIntPtr(block->Context); try { if (handle.Target is AuthorizationState state) state.Owner.CompleteAuthorization(state, error != 0 ? NeoNotificationPermissionStatus.Unknown : granted != 0 ? NeoNotificationPermissionStatus.Granted : NeoNotificationPermissionStatus.Denied); } catch (Exception exception) { if (handle.Target is AuthorizationState state) state.Completion.TrySetException(exception); } finally { handle.Free(); } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void ShowCompleted(Block* block, nint error) { var handle = GCHandle.FromIntPtr(block->Context); try { if (handle.Target is ShowState state) state.Owner.CompleteShow(state, error == 0); } catch (Exception exception) { if (handle.Target is ShowState state) state.Completion.TrySetException(exception); } finally { handle.Free(); } }

    private void DisposeOnDispatcher() { if (_disposed) return; _disposed = true; Activated = null; if (_center != 0) foreach (var id in _entries.Keys) RemoveNative(id); _entries.Clear(); _pending.Clear(); foreach (var state in _pendingShows.Values) { if (state.PreviousCategory != 0) Native.SendVoid(state.PreviousCategory, Native.GetSelector("release")); state.Completion.TrySetResult(NeoDesktopStatus.Canceled); } _pendingShows.Clear(); foreach (var state in _pendingSettings) state.Completion.TrySetResult(NeoNotificationPermissionStatus.Unknown); _pendingSettings.Clear(); foreach (var state in _pendingAuthorizations) state.Completion.TrySetResult(NeoNotificationPermissionStatus.Unknown); _pendingAuthorizations.Clear(); foreach (var value in _categories.Values) Native.SendVoid(value, Native.GetSelector("release")); _categories.Clear(); if (_center != 0) RebuildCategories(); if (_delegate != 0) { Owners.TryRemove(_delegate, out _); if (_center != 0) Native.SendVoidArg(_center, Native.GetSelector("setDelegate:"), Owners.Keys.FirstOrDefault()); Native.SendVoid(_delegate, Native.GetSelector("release")); } _delegate = 0; _center = 0; }
    private string NativeId(string id) => _nativePrefix + "." + id;
    private static nint Allocate(string className) { var result = Native.Send(Native.Send(Native.GetClass(className), Native.GetSelector("alloc")), Native.GetSelector("init")); return result != 0 ? result : throw new InvalidOperationException($"Unable to allocate {className}."); }
    private static void SetString(nint target, string selector, string value) { using var text = NativeString.Create(value); Native.SendVoidArg(target, Native.GetSelector(selector), text.Value); }
    private static string? ReadString(nint value) { if (value == 0) return null; var pointer = Native.Send(value, Native.GetSelector("UTF8String")); return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer); }

    private sealed record Entry(long Generation, NeoNotificationRequest Request);
    private sealed class SettingsState(MacNotifications owner) { internal MacNotifications Owner { get; } = owner; internal TaskCompletionSource<NeoNotificationPermissionStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class AuthorizationState(MacNotifications owner) { internal MacNotifications Owner { get; } = owner; internal TaskCompletionSource<NeoNotificationPermissionStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class ShowState(MacNotifications owner, string id, long generation, Entry? previousEntry, nint previousCategory) { internal MacNotifications Owner { get; } = owner; internal string Id { get; } = id; internal long Generation { get; } = generation; internal Entry? PreviousEntry { get; } = previousEntry; internal nint PreviousCategory { get; } = previousCategory; internal TaskCompletionSource<NeoDesktopStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    [StructLayout(LayoutKind.Sequential)] private struct Block { internal nint Isa; internal int Flags, Reserved; internal nint Invoke; internal BlockDescriptor* Descriptor; internal nint Context; }
    [StructLayout(LayoutKind.Sequential)] private struct BlockDescriptor { internal nuint Reserved, Size; }
    private readonly struct NativeString(nint value) : IDisposable { internal nint Value { get; } = value; internal static NativeString Create(string value) { var bytes = Encoding.UTF8.GetBytes(value + '\0'); fixed (byte* pointer = bytes) { var result = Native.SendUtf8(Native.GetClass("NSString"), Native.GetSelector("stringWithUTF8String:"), pointer); Native.SendVoid(result, Native.GetSelector("retain")); return new(result); } } public void Dispose() { if (Value != 0) Native.SendVoid(Value, Native.GetSelector("release")); } }

    private static partial class Native
    {
        [LibraryImport("/usr/lib/libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint dlsym(nint handle, string symbol);
        [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_lookUpClass(byte* name); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_allocateClassPair(nint superclass, byte* name, nuint extra); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial void objc_registerClassPair(nint value); [LibraryImport("/usr/lib/libobjc.A.dylib")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool class_addMethod(nint type, nint selector, nint callback, byte* encoding); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint objc_getClass(byte* name); [LibraryImport("/usr/lib/libobjc.A.dylib")] internal static partial nint sel_registerName(byte* name);
        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send(nint target, nint selector); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendArg(nint target, nint selector, nint value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send2(nint target, nint selector, nint first, nint second); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send3(nint target, nint selector, nint first, nint second, nint third); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint Send4(nint target, nint selector, nint first, nint second, nint third, nuint fourth); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendPointerCount(nint target, nint selector, nint* values, nuint count); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial nint SendUtf8(nint target, nint selector, byte* value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoid(nint target, nint selector); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendVoidArg(nint target, nint selector, nint value); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendBlock(nint target, nint selector, Block* block); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendAuthorization(nint target, nint selector, nuint options, Block* block); [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] internal static partial void SendRequest(nint target, nint selector, nint request, Block* block);
        internal static nint GetClass(string name) { var bytes = Encoding.UTF8.GetBytes(name + '\0'); fixed (byte* pointer = bytes) return objc_getClass(pointer); } internal static nint GetSelector(string name) { var bytes = Encoding.UTF8.GetBytes(name + '\0'); fixed (byte* pointer = bytes) return sel_registerName(pointer); }
    }
}

internal static class MacNotificationTasks
{
    internal static async Task<NeoDesktopStatus> ShowWithAuthorizationAsync(NeoDispatcher dispatcher, Func<Task<NeoNotificationPermissionStatus>> query, Func<Task<NeoNotificationPermissionStatus>> authorize, Func<Task<NeoDesktopStatus>> show, CancellationToken cancellationToken)
    {
        var permission = dispatcher.CheckAccess()
            ? await WaitAsync(query(), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)
            : await DispatchAndWaitAsync(dispatcher.InvokeAsync(query, cancellationToken), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (permission == NeoNotificationPermissionStatus.NotRequested)
        {
            permission = dispatcher.CheckAccess()
                ? await WaitAsync(authorize(), TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false)
                : await DispatchAndWaitAsync(dispatcher.InvokeAsync(authorize, cancellationToken), TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }
        if (permission != NeoNotificationPermissionStatus.Granted) return MacNotifications.PermissionFailureStatus(permission);
        return dispatcher.CheckAccess()
            ? await WaitAsync(show(), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false)
            : await DispatchAndWaitAsync(dispatcher.InvokeAsync(show, cancellationToken), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> DispatchAndWaitAsync<T>(ValueTask<Task<T>> dispatch, TimeSpan timeout, CancellationToken cancellationToken)
        => await (await dispatch.ConfigureAwait(false)).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

    internal static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
        => await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
}
