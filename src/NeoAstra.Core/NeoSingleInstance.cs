// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace NeoAstra;

/// <summary>Policy used when an existing single-instance primary is unreachable.</summary>
public enum NeoSingleInstanceHungPrimaryPolicy
{
    /// <summary>Fail without starting a competing normal instance.</summary>
    Fail,
    /// <summary>Retry until the configured acknowledgement timeout expires, then fail.</summary>
    Retry,
}

/// <summary>Configures secure local-user second-launch routing.</summary>
public sealed class NeoSingleInstanceOptions
{
    /// <summary>Gets or sets the explicit stable application identifier.</summary>
    public required string ApplicationId { get; set; }

    /// <summary>Gets or sets the maximum versioned envelope size.</summary>
    public int MaximumEnvelopeBytes { get; set; } = 256 * 1024;

    /// <summary>Gets or sets the bounded acknowledgement timeout.</summary>
    public TimeSpan AcknowledgementTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the hung-primary policy.</summary>
    public NeoSingleInstanceHungPrimaryPolicy HungPrimaryPolicy { get; set; } = NeoSingleInstanceHungPrimaryPolicy.Fail;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationId) || ApplicationId.Length > 192 || ApplicationId.Any(char.IsControl))
            throw new ArgumentException("A bounded explicit application identifier without controls is required.", nameof(ApplicationId));
        if (MaximumEnvelopeBytes is < 1024 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumEnvelopeBytes), "Envelope size must be between 1 KiB and 1 MiB.");
        if (AcknowledgementTimeout <= TimeSpan.Zero || AcknowledgementTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(AcknowledgementTimeout), "Acknowledgement timeout must be positive and no more than one minute.");
        if (!Enum.IsDefined(HungPrimaryPolicy)) throw new ArgumentOutOfRangeException(nameof(HungPrimaryPolicy));
    }
}

/// <summary>Owns a local-user single-instance lock and bounded launch-routing endpoint.</summary>
public sealed partial class NeoSingleInstance : IAsyncDisposable
{
    private readonly NeoApplication _application;
    private readonly NeoSingleInstanceOptions _options;
    private readonly PrimaryLock? _primaryLock;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task? _server;
    private readonly Dictionary<Guid, bool> _replay = [];
    private readonly Queue<Guid> _replayOrder = [];
    private int _disposed;

    private NeoSingleInstance(NeoApplication application, NeoSingleInstanceOptions options, string endpoint, PrimaryLock? primaryLock, bool primary)
    {
        _application = application;
        _options = options;
        EndpointName = endpoint;
        _primaryLock = primaryLock;
        IsPrimary = primary;
        if (primary) _server = RunServerAsync();
    }

    /// <summary>Gets whether this process owns the primary lock.</summary>
    public bool IsPrimary { get; }

    /// <summary>Gets the opaque local endpoint name. It contains no application arguments or secrets.</summary>
    public string EndpointName { get; }

    /// <summary>Acquires the user/session-scoped lock or routes one launch to the existing primary.</summary>
    /// <param name="application">The application that queues primary launch delivery.</param>
    /// <param name="options">Single-instance security and timeout options.</param>
    /// <param name="secondaryLaunch">The launch envelope sent only when another primary owns the lock.</param>
    /// <param name="cancellationToken">Cancels acquisition and routing.</param>
    /// <returns>An owned primary or acknowledged secondary result.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An option or launch value is invalid or exceeds a configured bound.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A timeout or size bound is outside its supported range.</exception>
    /// <exception cref="InvalidOperationException">The primary is hung, rejects the envelope, or its queue is full.</exception>
    /// <exception cref="TimeoutException">The bounded acknowledgement deadline expires.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static async ValueTask<NeoSingleInstance> AcquireAsync(NeoApplication application, NeoSingleInstanceOptions options,
        NeoLaunchEvent secondaryLaunch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secondaryLaunch);
        if (secondaryLaunch.Reason != NeoLaunchReason.SecondInstance)
            throw new ArgumentException("Single-instance routing requires the SecondInstance launch reason.", nameof(secondaryLaunch));
        options.Validate();
        var snapshot = new NeoSingleInstanceOptions
        {
            ApplicationId = options.ApplicationId,
            MaximumEnvelopeBytes = options.MaximumEnvelopeBytes,
            AcknowledgementTimeout = options.AcknowledgementTimeout,
            HungPrimaryPolicy = options.HungPrimaryPolicy,
        };
        var endpoint = CreateEndpoint(snapshot.ApplicationId);
        var primaryLock = PrimaryLock.TryAcquire("neoastra-lock-" + endpoint);
        if (primaryLock is not null) return new NeoSingleInstance(application, snapshot, endpoint, primaryLock, true);
        var secondary = new NeoSingleInstance(application, snapshot, endpoint, null, false);
        try
        {
            await secondary.RouteAsync(secondaryLaunch, cancellationToken).ConfigureAwait(false);
            return secondary;
        }
        catch
        {
            await secondary.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Routes another bounded launch envelope to the primary.</summary>
    /// <param name="launchEvent">Validated launch data. Environment and capability data cannot be represented.</param>
    /// <param name="cancellationToken">Cancels routing.</param>
    /// <exception cref="InvalidOperationException">This instance is primary or the primary rejects the event.</exception>
    /// <exception cref="ArgumentException">The launch reason is not <see cref="NeoLaunchReason.SecondInstance"/> or the envelope exceeds the configured byte limit.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="launchEvent"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    /// <exception cref="TimeoutException">The primary is unreachable before the deadline.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public async ValueTask RouteAsync(NeoLaunchEvent launchEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchEvent);
        if (launchEvent.Reason != NeoLaunchReason.SecondInstance)
            throw new ArgumentException("Single-instance routing requires the SecondInstance launch reason.", nameof(launchEvent));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsPrimary) throw new InvalidOperationException("A primary does not route to itself.");
        var requestId = Guid.NewGuid();
        var payload = WriteEnvelope(requestId, launchEvent);
        if (payload.Length > _options.MaximumEnvelopeBytes) throw new ArgumentException("The launch envelope exceeds the configured byte limit.", nameof(launchEvent));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.AcknowledgementTimeout);
        Exception? last = null;
        do
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", EndpointName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await pipe.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await pipe.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                var acknowledgement = new byte[1];
                await pipe.ReadExactlyAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
                if (acknowledgement[0] != 1) throw new PrimaryRejectedException();
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The primary did not acknowledge the launch before the configured deadline.", last);
            }
            catch (PrimaryRejectedException)
            {
                throw new InvalidOperationException("The primary rejected the launch envelope.");
            }
            catch (Exception exception) when (_options.HungPrimaryPolicy == NeoSingleInstanceHungPrimaryPolicy.Retry && !deadline.IsCancellationRequested)
            {
                last = exception;
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        } while (_options.HungPrimaryPolicy == NeoSingleInstanceHungPrimaryPolicy.Retry);
        throw new InvalidOperationException("The existing primary is unreachable; a competing normal instance was not started.", last);
    }

    /// <summary>Stops routing and releases the primary lock exactly once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        if (_server is not null)
        {
            try { await _server.ConfigureAwait(false); }
            catch (Exception) { /* Teardown must still release the process lock after a server failure. */ }
        }
        _stopping.Dispose();
        _primaryLock?.Dispose();
    }

    private async Task RunServerAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(EndpointName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    _options.MaximumEnvelopeBytes + 4, _options.MaximumEnvelopeBytes + 4);
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
                using var peerDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                peerDeadline.CancelAfter(_options.AcknowledgementTimeout);
                var peerToken = peerDeadline.Token;
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, peerToken).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > _options.MaximumEnvelopeBytes)
                {
                    await pipe.WriteAsync(new byte[] { 0 }, peerToken).ConfigureAwait(false);
                    continue;
                }
                var payload = new byte[length];
                await pipe.ReadExactlyAsync(payload, peerToken).ConfigureAwait(false);
                var accepted = false;
                if (TryReadEnvelope(payload, out var requestId, out var launchEvent))
                {
                    if (!_replay.TryGetValue(requestId, out accepted))
                    {
                        try { accepted = _application.QueueLaunchEvent(launchEvent!); }
                        catch { accepted = false; }
                        _replay.Add(requestId, accepted);
                        _replayOrder.Enqueue(requestId);
                        if (_replayOrder.Count > 1024) _replay.Remove(_replayOrder.Dequeue());
                    }
                }
                await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, peerToken).ConfigureAwait(false);
                await pipe.FlushAsync(peerToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* Stop or discard a peer that exceeded its bounded request deadline. */ }
            catch (IOException) { /* Malformed/disconnected peers cannot terminate the primary. */ }
            catch (JsonException) { }
            catch (Exception exception)
            {
                _application.ReportLifecycleFailure("application.single-instance", exception, 0);
                break;
            }
        }
    }

    internal static string CreateEndpoint(string applicationId)
    {
        var userScope = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : GetUnixUserId().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var session = OperatingSystem.IsWindows() ? Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "user";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(applicationId + "\n" + userScope + "\n" + session));
        return "neoastra-" + Convert.ToHexStringLower(bytes.AsSpan(0, 20));
    }

    internal static byte[] WriteEnvelope(Guid requestId, NeoLaunchEvent value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("requestId", requestId.ToString("N")); writer.WriteString("reason", value.Reason.ToString());
            writer.WriteStartArray("arguments"); foreach (var item in value.Arguments) writer.WriteStringValue(item); writer.WriteEndArray();
            if (value.WorkingDirectory is not null) writer.WriteString("workingDirectory", value.WorkingDirectory);
            writer.WriteStartArray("files"); foreach (var item in value.Files) writer.WriteStringValue(item); writer.WriteEndArray();
            writer.WriteStartArray("urls"); foreach (var item in value.Urls) writer.WriteStringValue(item.AbsoluteUri); writer.WriteEndArray();
            writer.WriteStartObject("metadata"); foreach (var item in value.Metadata) writer.WriteString(item.Key, item.Value); writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    internal static bool TryReadEnvelope(ReadOnlySpan<byte> payload, out NeoLaunchEvent? launchEvent)
        => TryReadEnvelope(payload, out _, out launchEvent);

    private static bool TryReadEnvelope(ReadOnlySpan<byte> payload, out Guid requestId, out NeoLaunchEvent? launchEvent)
    {
        requestId = default;
        launchEvent = null;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("version" or "requestId" or "reason" or "arguments" or "workingDirectory" or "files" or "urls" or "metadata")) return false;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1) return false;
            if (!root.TryGetProperty("requestId", out var request) || request.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(request.GetString(), "N", out requestId)) return false;
            if (!root.TryGetProperty("reason", out var reasonValue) || reasonValue.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<NeoLaunchReason>(reasonValue.GetString(), false, out var reason) || reason != NeoLaunchReason.SecondInstance) return false;
            if (!TryReadStrings(root, "arguments", out var arguments) || !TryReadStrings(root, "files", out var files) || !TryReadStrings(root, "urls", out var uriStrings)) return false;
            var urls = uriStrings.Select(static value => new Uri(value, UriKind.Absolute)).ToArray();
            string? workingDirectory = null;
            if (root.TryGetProperty("workingDirectory", out var working))
            {
                if (working.ValueKind != JsonValueKind.String) return false;
                workingDirectory = working.GetString();
            }
            if (!root.TryGetProperty("metadata", out var metadataValue) || metadataValue.ValueKind != JsonValueKind.Object) return false;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in metadataValue.EnumerateObject())
                if (property.Value.ValueKind != JsonValueKind.String || !metadata.TryAdd(property.Name, property.Value.GetString()!)) return false;
            launchEvent = new NeoLaunchEvent(reason, arguments, workingDirectory, files, urls, metadata);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadStrings(JsonElement root, string name, out string[] values)
    {
        values = [];
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return false;
        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            result.Add(item.GetString()!);
        }
        values = result.ToArray();
        return true;
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUnixUserId();

    private sealed class PrimaryRejectedException : Exception { }

    /// <summary>Keeps acquisition and release on the same thread because named mutex ownership is thread-affine.</summary>
    private sealed class PrimaryLock : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private bool _owns;
        private int _disposed;

        private PrimaryLock(string name)
        {
            _thread = new Thread(() => Run(name)) { IsBackground = true, Name = "NeoAstra primary lock" };
            _thread.Start();
        }

        public static PrimaryLock? TryAcquire(string name)
        {
            var value = new PrimaryLock(name);
            value._acquired.Wait();
            if (value._owns) return value;
            value.Dispose();
            return null;
        }

        private void Run(string name)
        {
            try
            {
                using var mutex = new Mutex(false, name);
                try { _owns = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { _owns = true; }
                finally { _acquired.Set(); }
                if (!_owns) return;
                _release.Wait();
                mutex.ReleaseMutex();
            }
            catch
            {
                _owns = false;
                _acquired.Set();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _release.Set();
            _thread.Join();
            _acquired.Dispose();
            _release.Dispose();
        }
    }
}
