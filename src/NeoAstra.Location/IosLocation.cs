// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NeoAstra.Desktop;

namespace NeoAstra.Location;

// Core Location through the Objective-C runtime, as the iOS adapters of NeoAstra reach UIKit. One CLLocationManager lives on the
// main thread with a delegate of a class that this adapter defines; its callbacks answer the requests that wait for the user or
// for a fix. A request for the location asks once (requestLocation), and every request that waits takes the fix that arrives.
internal sealed partial class IosLocation : INeoLocation, INeoLocationDispatcherBinding, IAsyncDisposable
{
    private const string Framework = "/System/Library/Frameworks/CoreLocation.framework/CoreLocation";
    private const string UsageDescription = "NSLocationWhenInUseUsageDescription";
    private static readonly ConcurrentDictionary<nint, IosLocation> Owners = new();
    private static readonly object ClassLock = new();
    private static nint s_delegateClass;

    private readonly object _sync = new();
    private readonly List<TaskCompletionSource> _authorizations = [];
    private readonly List<(TaskCompletionSource<NeoDesktopResult<NeoLocationPosition>> Completion, bool High)> _positions = [];
    private NeoDispatcher? _dispatcher;
    private nint _manager;
    private nint _delegate;
    private bool _disposed;

    private IosLocation(NeoDispatcher? dispatcher) => _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "Core Location while the application is in use: the precise or approximate location that the user allows (NSLocationWhenInUseUsageDescription).");

    internal static INeoLocation Create(NeoDispatcher? dispatcher)
        => NativeLibrary.TryLoad(Framework, out _) ? new IosLocation(dispatcher) : new UnsupportedLocation("The Core Location framework is unavailable.");

    void INeoLocationDispatcherBinding.Bind(NeoDispatcher dispatcher) { ArgumentNullException.ThrowIfNull(dispatcher); _dispatcher ??= dispatcher; }

    public async ValueTask<NeoLocationAuthorization> GetAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        // Core Location asks not to read whether its service is on from the main thread, which it may block.
        var enabled = await Task.Run(ServicesEnabled, cancellationToken).ConfigureAwait(false);
        // CLAccuracyAuthorization: full 0, reduced 1.
        var (status, reduced) = await OnMainAsync(() => { var manager = Manager(); return (AuthorizationStatus(manager), Send(manager, "accuracyAuthorization") == 1); }, cancellationToken).ConfigureAwait(false);
        return new(Permission(status), status is 3 or 4 && !reduced, enabled);
    }

    public async ValueTask<NeoLocationAuthorization> RequestAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        var answer = await OnMainAsync(() =>
        {
            var manager = Manager();
            if (AuthorizationStatus(manager) != 0 || !Declared()) return null;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _authorizations.Add(completion);
            Send(manager, "requestWhenInUseAuthorization");
            return completion;
        }, cancellationToken).ConfigureAwait(false);
        if (answer is not null)
        {
            try { await answer.Task.WaitAsync(NeoLocationRequest.MaximumTimeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
            finally { lock (_sync) _authorizations.Remove(answer); }
        }
        return await GetAuthorizationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<NeoDesktopResult<NeoLocationPosition>> GetCurrentPositionAsync(NeoLocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (!await Task.Run(ServicesEnabled, cancellationToken).ConfigureAwait(false)) return NeoLocation.Failure(NeoDesktopStatus.Denied, NeoLocation.ServiceDisabledCode);
        var high = request.Accuracy == NeoLocationAccuracy.High;
        var maximumAge = request.MaximumAge;
        var start = await OnMainAsync<(NeoDesktopResult<NeoLocationPosition>? Answer, TaskCompletionSource<NeoDesktopResult<NeoLocationPosition>>? Completion)>(() =>
        {
            var manager = Manager();
            if (AuthorizationStatus(manager) is not (3 or 4)) return (NeoLocation.Failure(NeoDesktopStatus.Denied, NeoLocation.PermissionCode), null);
            if (maximumAge > TimeSpan.Zero && Send(manager, "location") is var last and not 0 && Read(last) is { } known && DateTimeOffset.UtcNow - known.Timestamp <= maximumAge)
                return (NeoDesktopResult<NeoLocationPosition>.Success(known), null);
            var completion = new TaskCompletionSource<NeoDesktopResult<NeoLocationPosition>>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool best;
            lock (_sync) { _positions.Add((completion, high)); best = _positions.Exists(static item => item.High); }
            // kCLLocationAccuracyBest and kCLLocationAccuracyHundredMeters; the most accurate request that waits decides.
            SendDouble(manager, Selector("setDesiredAccuracy:"), best ? -1.0 : 100.0);
            Send(manager, "requestLocation");
            return (null, completion);
        }, cancellationToken).ConfigureAwait(false);
        if (start.Answer is { } answer) return answer;
        var pending = start.Completion!;
        try { return await pending.Task.WaitAsync(request.Timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return NeoLocation.Failure(NeoDesktopStatus.NotFound, NeoLocation.TimeoutCode); }
        finally
        {
            bool idle;
            lock (_sync) { _positions.RemoveAll(item => item.Completion == pending); idle = _positions.Count == 0; }
            // Nobody waits any more for the fix that did not arrive in time.
            if (idle && !pending.Task.IsCompleted && _dispatcher is { } dispatcher)
                _ = dispatcher.InvokeAsync(() => { if (_manager != 0) Send(_manager, "stopUpdatingLocation"); }).AsTask().ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        if (_dispatcher is { } dispatcher)
        {
            try
            {
                if (dispatcher.CheckAccess()) Release();
                else await dispatcher.InvokeAsync(Release).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
        }
        Answer(NeoLocation.Failure(NeoDesktopStatus.Failed, null));
        TaskCompletionSource[] authorizations;
        lock (_sync) { authorizations = [.. _authorizations]; _authorizations.Clear(); }
        foreach (var completion in authorizations) completion.TrySetResult();
    }

    private void Release()
    {
        if (_manager == 0) return;
        Send(_manager, Selector("setDelegate:"), 0);
        Send(_manager, "stopUpdatingLocation");
        Send(_manager, "release");
        Owners.TryRemove(_delegate, out _);
        Send(_delegate, "release");
        _manager = 0;
        _delegate = 0;
    }

    private ValueTask<T> OnMainAsync<T>(Func<T> function, CancellationToken cancellationToken)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The iOS location must be bound to the UI dispatcher before use.");
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        return dispatcher.CheckAccess() ? ValueTask.FromResult(function()) : dispatcher.InvokeAsync(function, cancellationToken);
    }

    // The manager and its delegate are created on the main thread, whose run loop delivers the callbacks.
    private nint Manager()
    {
        if (_manager != 0) return _manager;
        var type = DelegateClass();
        _delegate = Send(Send(type, "alloc"), "init");
        Owners[_delegate] = this;
        _manager = Send(Send(Class("CLLocationManager"), "alloc"), "init");
        Send(_manager, Selector("setDelegate:"), _delegate);
        return _manager;
    }

    private void AuthorizationChanged(nint manager)
    {
        if (AuthorizationStatus(manager) == 0) return;
        TaskCompletionSource[] waiting;
        lock (_sync) { waiting = [.. _authorizations]; _authorizations.Clear(); }
        foreach (var completion in waiting) completion.TrySetResult();
    }

    private void Answer(NeoDesktopResult<NeoLocationPosition> result)
    {
        TaskCompletionSource<NeoDesktopResult<NeoLocationPosition>>[] waiting;
        lock (_sync) { waiting = [.. _positions.Select(static item => item.Completion)]; _positions.Clear(); }
        foreach (var completion in waiting) completion.TrySetResult(result);
    }

    private static NeoLocationPermission Permission(int status) => status switch
    {
        // kCLAuthorizationStatusNotDetermined, Restricted, Denied, AuthorizedAlways, AuthorizedWhenInUse.
        0 => Declared() ? NeoLocationPermission.NotRequested : NeoLocationPermission.Denied,
        1 => NeoLocationPermission.Restricted,
        3 or 4 => NeoLocationPermission.Granted,
        _ => NeoLocationPermission.Denied,
    };

    // CLAuthorizationStatus is a 32-bit value.
    private static int AuthorizationStatus(nint manager) => (int)Send(manager, "authorizationStatus");

    // Without the description in its Info.plist, iOS does not ask the user, and the application cannot read the location.
    private static bool Declared() => Send(Send(Class("NSBundle"), "mainBundle"), Selector("objectForInfoDictionaryKey:"), String(UsageDescription)) != 0;

    private static bool ServicesEnabled() => SendBool(Class("CLLocationManager"), Selector("locationServicesEnabled")) != 0;

    private static NeoLocationPosition? Read(nint location)
    {
        if (location == 0) return null;
        var coordinate = SendCoordinate(location, Selector("coordinate"));
        var horizontal = SendDouble(location, Selector("horizontalAccuracy"));
        // A negative accuracy marks coordinates that are not valid.
        if (horizontal < 0) return null;
        var vertical = SendDouble(location, Selector("verticalAccuracy"));
        var speed = SendDouble(location, Selector("speed"));
        var course = SendDouble(location, Selector("course"));
        var seconds = SendDouble(Send(location, "timestamp"), Selector("timeIntervalSince1970"));
        return new(coordinate.Latitude, coordinate.Longitude, horizontal, DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)))
        {
            Altitude = vertical >= 0 ? SendDouble(location, Selector("altitude")) : null,
            AltitudeAccuracyMeters = vertical >= 0 ? vertical : null,
            SpeedMetersPerSecond = speed >= 0 ? speed : null,
            CourseDegrees = course >= 0 ? course : null,
        };
    }

    private static unsafe nint DelegateClass()
    {
        if (s_delegateClass != 0) return s_delegateClass;
        lock (ClassLock)
        {
            if (s_delegateClass != 0) return s_delegateClass;
            var name = "NeoAstraLocationDelegate_v1"u8;
            fixed (byte* pointer = name)
            {
                var type = objc_lookUpClass(pointer);
                if (type == 0)
                {
                    type = objc_allocateClassPair(Class("NSObject"), pointer, 0);
                    if (type == 0) throw new InvalidOperationException("Unable to allocate the Core Location delegate class.");
                    Add(type, "locationManager:didUpdateLocations:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidUpdateLocations, "v@:@@"u8);
                    Add(type, "locationManager:didFailWithError:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidFail, "v@:@@"u8);
                    Add(type, "locationManagerDidChangeAuthorization:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DidChangeAuthorization, "v@:@"u8);
                    objc_registerClassPair(type);
                }
                s_delegateClass = type;
            }
            return s_delegateClass;
        }

        static void Add(nint type, string selector, nint implementation, ReadOnlySpan<byte> encoding)
        {
            fixed (byte* types = encoding)
                if (class_addMethod(type, Selector(selector), implementation, types) == 0) throw new InvalidOperationException("Unable to define a Core Location delegate method.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidUpdateLocations(nint self, nint selector, nint manager, nint locations)
    {
        try
        {
            if (!Owners.TryGetValue(self, out var owner)) return;
            var position = Read(Send(locations, "lastObject"));
            if (position is not null) owner.Answer(NeoDesktopResult<NeoLocationPosition>.Success(position));
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidFail(nint self, nint selector, nint manager, nint error)
    {
        try
        {
            if (!Owners.TryGetValue(self, out var owner)) return;
            // kCLErrorLocationUnknown: no fix now; kCLErrorDenied: the user does not allow it.
            owner.Answer(Send(error, "code") switch
            {
                0 => NeoLocation.Failure(NeoDesktopStatus.NotFound, NeoLocation.TimeoutCode),
                1 => NeoLocation.Failure(NeoDesktopStatus.Denied, NeoLocation.PermissionCode),
                _ => NeoLocation.Failure(NeoDesktopStatus.Failed, null),
            });
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidChangeAuthorization(nint self, nint selector, nint manager)
    {
        try { if (Owners.TryGetValue(self, out var owner)) owner.AuthorizationChanged(manager); }
        catch { }
    }

    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)] private struct Coordinate { public double Latitude; public double Longitude; }

    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Selector(string name);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector, nint argument);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial byte SendBool(nint receiver, nint selector);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial double SendDouble(nint receiver, nint selector);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial void SendDouble(nint receiver, nint selector, double argument);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial Coordinate SendCoordinate(nint receiver, nint selector);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static unsafe partial nint SendBytes(nint receiver, nint selector, byte* bytes);
    [LibraryImport(Runtime)] private static unsafe partial nint objc_lookUpClass(byte* name);
    [LibraryImport(Runtime)] private static unsafe partial nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [LibraryImport(Runtime)] private static partial void objc_registerClassPair(nint type);
    [LibraryImport(Runtime)] private static unsafe partial byte class_addMethod(nint type, nint selector, nint implementation, byte* types);

    private static nint Send(nint receiver, string selector) => Send(receiver, Selector(selector));

    private static unsafe nint String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        fixed (byte* pointer = bytes) return SendBytes(Class("NSString"), Selector("stringWithUTF8String:"), pointer);
    }
}
