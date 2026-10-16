// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NeoAstra.Desktop;

namespace NeoAstra.Passkeys;

// Authentication Services through the Objective-C runtime, as the iOS adapters of NeoAstra reach UIKit. A request builds the
// registration or assertion request of the platform provider for the relying party of the options, and an authorization
// controller shows it on the key window; a delegate of a class that this adapter defines answers the request and anchors the
// sheet. The answer goes back as the WebAuthn JSON that a server verifies.
internal sealed partial class IosPasskeys : INeoPasskeys, INeoPasskeysDispatcherBinding
{
    private const string Framework = "/System/Library/Frameworks/AuthenticationServices.framework/AuthenticationServices";
    private static readonly ConcurrentDictionary<nint, Pending> Requests = new();
    private static readonly object ClassLock = new();
    private static nint s_delegateClass;
    private NeoDispatcher? _dispatcher;

    private IosPasskeys(NeoDispatcher? dispatcher) => _dispatcher = dispatcher;

    public NeoCapabilityInfo Support { get; } = new(NeoSupportLevel.Native, 1, 0,
        "The platform passkeys of Authentication Services in the password manager of the user (associated domains entitlement with webcredentials).");

    internal static INeoPasskeys Create(NeoDispatcher? dispatcher)
        => NativeLibrary.TryLoad(Framework, out _) ? new IosPasskeys(dispatcher) : new UnsupportedPasskeys("The Authentication Services framework is unavailable.");

    void INeoPasskeysDispatcherBinding.Bind(NeoDispatcher dispatcher) { ArgumentNullException.ThrowIfNull(dispatcher); _dispatcher ??= dispatcher; }

    public ValueTask<NeoDesktopResult<string>> CreateAsync(string options, CancellationToken cancellationToken = default)
    {
        var creation = NeoPasskeys.ParseCreation(options);
        return RunAsync(() => Registration(creation), creation: true, cancellationToken);
    }

    public ValueTask<NeoDesktopResult<string>> GetAsync(string options, CancellationToken cancellationToken = default)
    {
        var request = NeoPasskeys.ParseRequest(options);
        return RunAsync(() => Assertion(request), creation: false, cancellationToken);
    }

    private async ValueTask<NeoDesktopResult<string>> RunAsync(Func<nint> build, bool creation, CancellationToken cancellationToken)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The iOS passkeys must be bound to the UI dispatcher before use.");
        var pending = new Pending(creation);
        await Invoke(dispatcher, () => Start(pending, build()), cancellationToken).ConfigureAwait(false);
        try { return await pending.Completion.Task.WaitAsync(PasskeysRendererRegistration.Timeout, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // Nobody waits any more: take the sheet away.
            _ = Invoke(dispatcher, () => { if (pending.Controller != 0) Send(pending.Controller, "cancel"); }, CancellationToken.None).AsTask().ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            if (exception is OperationCanceledException) throw;
            return NeoPasskeys.Failure(NeoDesktopStatus.Canceled, null);
        }
    }

    private static void Start(Pending pending, nint request)
    {
        var type = DelegateClass();
        var target = Send(Send(type, "alloc"), "init");
        pending.Delegate = target;
        Requests[target] = pending;
        var controller = Send(Send(Class("ASAuthorizationController"), "alloc"), Selector("initWithAuthorizationRequests:"), Send(Class("NSArray"), Selector("arrayWithObject:"), request));
        pending.Controller = controller;
        Send(controller, Selector("setDelegate:"), target);
        Send(controller, Selector("setPresentationContextProvider:"), target);
        Send(controller, "performRequests");
    }

    // The registration request of the platform provider for the relying party, the user, and the options of the server.
    private static nint Registration(NeoPasskeyCreation options)
    {
        var provider = Provider(options.RelyingParty);
        try
        {
            var request = Send(provider, Selector("createCredentialRegistrationRequestWithChallenge:name:userID:"), Data(options.Challenge), String(options.UserName), Data(options.UserId));
            Send(request, Selector("setDisplayName:"), String(options.DisplayName));
            Send(request, Selector("setUserVerificationPreference:"), String(options.UserVerification));
            Send(request, Selector("setAttestationPreference:"), String(options.Attestation));
            if (options.Excluded.Count > 0 && Responds(request, "setExcludedCredentials:")) Send(request, Selector("setExcludedCredentials:"), Descriptors(options.Excluded));
            return request;
        }
        finally { Send(provider, "release"); }
    }

    private static nint Assertion(NeoPasskeyRequest options)
    {
        var provider = Provider(options.RelyingParty);
        try
        {
            var request = Send(provider, Selector("createCredentialAssertionRequestWithChallenge:"), Data(options.Challenge));
            Send(request, Selector("setUserVerificationPreference:"), String(options.UserVerification));
            if (options.Allowed.Count > 0) Send(request, Selector("setAllowedCredentials:"), Descriptors(options.Allowed));
            return request;
        }
        finally { Send(provider, "release"); }
    }

    private static nint Provider(string relyingParty) => Send(Send(Class("ASAuthorizationPlatformPublicKeyCredentialProvider"), "alloc"), Selector("initWithRelyingPartyIdentifier:"), String(relyingParty));

    private static nint Descriptors(IReadOnlyList<byte[]> ids)
    {
        var array = Send(Class("NSMutableArray"), "array");
        foreach (var id in ids)
        {
            var descriptor = Send(Send(Class("ASAuthorizationPlatformPublicKeyCredentialDescriptor"), "alloc"), Selector("initWithCredentialID:"), Data(id));
            Send(array, Selector("addObject:"), descriptor);
            Send(descriptor, "release");
        }
        return array;
    }

    private static void Finish(nint target, NeoDesktopResult<string> result)
    {
        if (!Requests.TryRemove(target, out var pending)) return;
        pending.Completion.TrySetResult(result);
        if (pending.Controller != 0) { Send(pending.Controller, "release"); pending.Controller = 0; }
        Send(target, "release");
    }

    // The WebAuthn JSON of the credential, with the base64url values that a server verifies.
    private static string Response(nint credential, bool creation)
    {
        var id = NeoPasskeys.Base64Url(Bytes(Send(credential, "credentialID")));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("rawId", id);
            writer.WriteString("type", "public-key");
            writer.WriteString("authenticatorAttachment", "platform");
            writer.WriteStartObject("response");
            writer.WriteString("clientDataJSON", NeoPasskeys.Base64Url(Bytes(Send(credential, "rawClientDataJSON"))));
            if (creation)
            {
                writer.WriteString("attestationObject", NeoPasskeys.Base64Url(Bytes(Send(credential, "rawAttestationObject"))));
                writer.WriteStartArray("transports");
                writer.WriteStringValue("internal");
                writer.WriteStringValue("hybrid");
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteString("authenticatorData", NeoPasskeys.Base64Url(Bytes(Send(credential, "rawAuthenticatorData"))));
                writer.WriteString("signature", NeoPasskeys.Base64Url(Bytes(Send(credential, "signature"))));
                writer.WriteString("userHandle", NeoPasskeys.Base64Url(Bytes(Send(credential, "userID"))));
            }
            writer.WriteEndObject();
            writer.WriteStartObject("clientExtensionResults");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ASAuthorizationErrorCanceled 1001, Failed 1004 (also for a relying party that the application is not associated with),
    // MatchedExcludedCredential 1006.
    private static NeoDesktopResult<string> Error(nint error, bool creation)
    {
        var code = (long)Send(error, "code");
        var description = Text(Send(error, "localizedDescription")) ?? string.Empty;
        if (code == 1001) return NeoPasskeys.Failure(NeoDesktopStatus.Canceled, null);
        if (code == 1006 && creation) return NeoPasskeys.Failure(NeoDesktopStatus.Conflict, NeoPasskeys.ExcludedCode);
        if (description.Contains("not associated", StringComparison.OrdinalIgnoreCase)) return NeoPasskeys.Failure(NeoDesktopStatus.Denied, NeoPasskeys.NotAssociatedCode);
        return NeoPasskeys.Failure(NeoDesktopStatus.Failed, null);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidComplete(nint self, nint selector, nint controller, nint authorization)
    {
        try
        {
            if (!Requests.TryGetValue(self, out var pending)) return;
            var credential = Send(authorization, "credential");
            Finish(self, credential == 0 ? NeoPasskeys.Failure(NeoDesktopStatus.Failed, null) : NeoDesktopResult<string>.Success(Response(credential, pending.Creation)));
        }
        catch { Finish(self, NeoPasskeys.Failure(NeoDesktopStatus.Failed, null)); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidFail(nint self, nint selector, nint controller, nint error)
    {
        try { if (Requests.TryGetValue(self, out var pending)) Finish(self, Error(error, pending.Creation)); }
        catch { Finish(self, NeoPasskeys.Failure(NeoDesktopStatus.Failed, null)); }
    }

    // The sheet shows on the key window of the scene that the user sees.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Anchor(nint self, nint selector, nint controller)
    {
        try
        {
            var scenes = Send(Send(Send(Class("UIApplication"), "sharedApplication"), "connectedScenes"), "allObjects");
            var count = (long)Send(scenes, "count");
            nint fallback = 0;
            for (var index = 0L; index < count; index++)
            {
                var scene = Send(scenes, Selector("objectAtIndex:"), (nint)index);
                if (!Responds(scene, "keyWindow")) continue;
                var window = Send(scene, "keyWindow");
                if (window != 0 && (long)Send(scene, "activationState") == 0) return window;
                if (window != 0) fallback = window;
            }
            return fallback;
        }
        catch { return 0; }
    }

    private static unsafe nint DelegateClass()
    {
        if (s_delegateClass != 0) return s_delegateClass;
        lock (ClassLock)
        {
            if (s_delegateClass != 0) return s_delegateClass;
            var name = "NeoAstraPasskeysDelegate_v1"u8;
            fixed (byte* pointer = name)
            {
                var type = objc_lookUpClass(pointer);
                if (type == 0)
                {
                    type = objc_allocateClassPair(Class("NSObject"), pointer, 0);
                    if (type == 0) throw new InvalidOperationException("Unable to allocate the Authentication Services delegate class.");
                    Add(type, "authorizationController:didCompleteWithAuthorization:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidComplete, "v@:@@"u8);
                    Add(type, "authorizationController:didCompleteWithError:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidFail, "v@:@@"u8);
                    Add(type, "presentationAnchorForAuthorizationController:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&Anchor, "@@:@"u8);
                    objc_registerClassPair(type);
                }
                s_delegateClass = type;
            }
            return s_delegateClass;
        }

        static void Add(nint type, string selector, nint implementation, ReadOnlySpan<byte> encoding)
        {
            fixed (byte* types = encoding)
                if (class_addMethod(type, Selector(selector), implementation, types) == 0) throw new InvalidOperationException("Unable to define an Authentication Services delegate method.");
        }
    }

    private static ValueTask Invoke(NeoDispatcher dispatcher, Action action, CancellationToken cancellationToken)
    {
        if (!dispatcher.CheckAccess()) return dispatcher.InvokeAsync(action, cancellationToken);
        action();
        return ValueTask.CompletedTask;
    }

    private static bool Responds(nint target, string selector) => (Send(target, Selector("respondsToSelector:"), Selector(selector)) & 0xFF) != 0;

    private static unsafe nint Data(byte[] value) { fixed (byte* pointer = value) return SendData(Class("NSData"), Selector("dataWithBytes:length:"), pointer, (nuint)value.Length); }

    private static unsafe byte[] Bytes(nint data)
    {
        if (data == 0) return [];
        var length = (long)Send(data, "length");
        return length <= 0 ? [] : new ReadOnlySpan<byte>((void*)Send(data, "bytes"), (int)length).ToArray();
    }

    private static unsafe nint String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        fixed (byte* pointer = bytes) return SendBytes(Class("NSString"), Selector("stringWithUTF8String:"), pointer);
    }

    private static string? Text(nint value) => value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, "UTF8String"));

    private sealed class Pending(bool creation)
    {
        internal bool Creation { get; } = creation;
        internal TaskCompletionSource<NeoDesktopResult<string>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal nint Controller { get; set; }
        internal nint Delegate { get; set; }
    }

    private const string Runtime = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Selector(string name);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector, nint argument);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static partial nint Send(nint receiver, nint selector, nint first, nint second, nint third);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static unsafe partial nint SendData(nint receiver, nint selector, byte* bytes, nuint length);
    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")] private static unsafe partial nint SendBytes(nint receiver, nint selector, byte* bytes);
    [LibraryImport(Runtime)] private static unsafe partial nint objc_lookUpClass(byte* name);
    [LibraryImport(Runtime)] private static unsafe partial nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [LibraryImport(Runtime)] private static partial void objc_registerClassPair(nint type);
    [LibraryImport(Runtime)] private static unsafe partial byte class_addMethod(nint type, nint selector, nint implementation, byte* types);

    private static nint Send(nint receiver, string selector) => Send(receiver, Selector(selector));
}
