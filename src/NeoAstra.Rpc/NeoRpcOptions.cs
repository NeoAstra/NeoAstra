// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Rpc;

/// <summary>Configures bounded RPC dispatch and error handling.</summary>
public sealed class NeoRpcOptions
{
    /// <summary>Gets or sets the deterministic generated application contract hash advertised in invocation contexts.</summary>
    public string ContractHash { get; set; } = string.Empty;
    /// <summary>Gets or sets the maximum simultaneous invocations across all sessions.</summary>
    /// <remarks>The value must be greater than <see cref="MaximumConcurrentInvocationsPerSession"/> so admission can reserve capacity for another view.</remarks>
    public int MaximumConcurrentInvocations { get; set; } = 256;
    /// <summary>Gets or sets the maximum simultaneous invocations in one document session.</summary>
    /// <remarks>The value must be less than <see cref="MaximumConcurrentInvocations"/>.</remarks>
    public int MaximumConcurrentInvocationsPerSession { get; set; } = 32;
    /// <summary>Gets or sets the maximum retained request IDs per session, including active and completed IDs.</summary>
    public int MaximumRetainedRequestIds { get; set; } = 4096;
    /// <summary>Gets or sets the default command timeout.</summary>
    public TimeSpan InvocationTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets or sets the maximum opaque request, subscription, channel, and resource ID length.</summary>
    public int MaximumIdLength { get; set; } = 128;
    /// <summary>Gets or sets the maximum command or event wire-name length.</summary>
    public int MaximumWireNameLength { get; set; } = 192;
    /// <summary>Gets or sets the maximum inbound JSON bytes accepted by the standalone session API.</summary>
    public int MaximumFrameBytes { get; set; } = 1024 * 1024;
    /// <summary>Gets or sets the maximum JSON nesting depth.</summary>
    public int MaximumJsonDepth { get; set; } = 32;
    /// <summary>Gets or sets the maximum queued event count per subscription.</summary>
    public int MaximumQueuedEventsPerSubscription { get; set; } = 64;
    /// <summary>Gets or sets the maximum serialized bytes queued by one subscription.</summary>
    public int MaximumQueuedEventBytesPerSubscription { get; set; } = 256 * 1024;
    /// <summary>Gets or sets the maximum subscriptions in one session.</summary>
    public int MaximumSubscriptionsPerSession { get; set; } = 128;
    /// <summary>Gets or sets the maximum open channels in one session.</summary>
    public int MaximumChannelsPerSession { get; set; } = 32;
    /// <summary>Gets or sets the maximum unacknowledged channel items.</summary>
    public int MaximumUnacknowledgedChannelItems { get; set; } = 16;
    /// <summary>Gets or sets the maximum resources owned by one session.</summary>
    public int MaximumResourcesPerSession { get; set; } = 64;
    /// <summary>Gets or sets the configured authorization service.</summary>
    public INeoRpcAuthorizationService? AuthorizationService { get; set; }
    /// <summary>Gets or sets application exception mappers, evaluated in order.</summary>
    public IReadOnlyList<INeoRpcErrorMapper> ErrorMappers { get; set; } = Array.Empty<INeoRpcErrorMapper>();
    /// <summary>Gets or sets an optional bounded diagnostic sink.</summary>
    public INeoRpcDiagnosticSink? DiagnosticSink { get; set; }
    /// <summary>Gets or sets whether bounded development exception messages may be returned for unclassified failures.</summary>
    /// <remarks>This is disabled by default and must never be enabled for a release profile.</remarks>
    public bool IncludeDevelopmentErrorDetails { get; set; }

    internal NeoRpcOptions CloneValidated()
    {
        ArgumentNullException.ThrowIfNull(ContractHash);
        if (ContractHash.Length > 256 || ContractHash.Any(static character => character > 0x7f || char.IsControl(character))) throw new ArgumentException("The contract hash must be bounded ASCII.", nameof(ContractHash));
        if (MaximumConcurrentInvocations is < 2 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentInvocations));
        if (MaximumConcurrentInvocationsPerSession is < 1 or > 4096 || MaximumConcurrentInvocationsPerSession >= MaximumConcurrentInvocations)
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentInvocationsPerSession));
        if (MaximumRetainedRequestIds is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumRetainedRequestIds));
        if (InvocationTimeout <= TimeSpan.Zero || InvocationTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(InvocationTimeout));
        if (MaximumIdLength is < 16 or > 256) throw new ArgumentOutOfRangeException(nameof(MaximumIdLength));
        if (MaximumWireNameLength is < 16 or > 512) throw new ArgumentOutOfRangeException(nameof(MaximumWireNameLength));
        if (MaximumFrameBytes is < 1024 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaximumFrameBytes));
        if (MaximumJsonDepth is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(MaximumJsonDepth));
        if (MaximumQueuedEventsPerSubscription is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaximumQueuedEventsPerSubscription));
        if (MaximumQueuedEventBytesPerSubscription is < 1024 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaximumQueuedEventBytesPerSubscription));
        if (MaximumSubscriptionsPerSession is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumSubscriptionsPerSession));
        if (MaximumChannelsPerSession is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumChannelsPerSession));
        if (MaximumUnacknowledgedChannelItems is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaximumUnacknowledgedChannelItems));
        if (MaximumResourcesPerSession is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaximumResourcesPerSession));
        ArgumentNullException.ThrowIfNull(ErrorMappers);
        if (ErrorMappers.Any(static mapper => mapper is null)) throw new ArgumentException("Error mapper collections cannot contain null.", nameof(ErrorMappers));

        return new NeoRpcOptions
        {
            ContractHash = ContractHash,
            MaximumConcurrentInvocations = MaximumConcurrentInvocations,
            MaximumConcurrentInvocationsPerSession = MaximumConcurrentInvocationsPerSession,
            MaximumRetainedRequestIds = MaximumRetainedRequestIds,
            InvocationTimeout = InvocationTimeout,
            MaximumIdLength = MaximumIdLength,
            MaximumWireNameLength = MaximumWireNameLength,
            MaximumFrameBytes = MaximumFrameBytes,
            MaximumJsonDepth = MaximumJsonDepth,
            MaximumQueuedEventsPerSubscription = MaximumQueuedEventsPerSubscription,
            MaximumQueuedEventBytesPerSubscription = MaximumQueuedEventBytesPerSubscription,
            MaximumSubscriptionsPerSession = MaximumSubscriptionsPerSession,
            MaximumChannelsPerSession = MaximumChannelsPerSession,
            MaximumUnacknowledgedChannelItems = MaximumUnacknowledgedChannelItems,
            MaximumResourcesPerSession = MaximumResourcesPerSession,
            AuthorizationService = AuthorizationService,
            ErrorMappers = ErrorMappers.ToArray(),
            DiagnosticSink = DiagnosticSink,
            IncludeDevelopmentErrorDetails = IncludeDevelopmentErrorDetails,
        };
    }
}

/// <summary>Configures one registered command.</summary>
public sealed class NeoRpcCommandOptions
{
    /// <summary>Gets or sets the permission checked before dispatch.</summary>
    public string? Permission { get; set; }
    /// <summary>Gets or sets the dispatch scheduler.</summary>
    public NeoRpcDispatchMode Dispatch { get; set; }
    /// <summary>Gets or sets a command-specific timeout, or <see langword="null"/> for the host default.</summary>
    public TimeSpan? Timeout { get; set; }

    internal NeoRpcCommandOptions CloneValidated()
    {
        if (Permission is not null && !NeoRpcValidation.IsPermission(Permission)) throw new ArgumentException("The command permission is malformed.", nameof(Permission));
        if (!Enum.IsDefined(Dispatch)) throw new ArgumentOutOfRangeException(nameof(Dispatch));
        if (Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))) throw new ArgumentOutOfRangeException(nameof(Timeout));
        return new NeoRpcCommandOptions { Permission = Permission, Dispatch = Dispatch, Timeout = Timeout };
    }
}

/// <summary>Configures one registered event.</summary>
public sealed class NeoRpcEventOptions
{
    /// <summary>Gets or sets the permission checked before subscription.</summary>
    public string? Permission { get; set; }
    /// <summary>Gets or sets the declaration-owned bounded queue overflow behavior.</summary>
    public NeoRpcOverflowBehavior OverflowBehavior { get; set; } = NeoRpcOverflowBehavior.DropOldest;

    internal NeoRpcEventOptions CloneValidated()
    {
        if (Permission is not null && !NeoRpcValidation.IsPermission(Permission)) throw new ArgumentException("The event permission is malformed.", nameof(Permission));
        if (!Enum.IsDefined(OverflowBehavior)) throw new ArgumentOutOfRangeException(nameof(OverflowBehavior));
        return new NeoRpcEventOptions { Permission = Permission, OverflowBehavior = OverflowBehavior };
    }
}

internal static class NeoRpcValidation
{
    internal static bool IsWireName(string? value, int maximumLength = 192)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength || value[0] is '.' or '-' or ':') return false;
        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.' or ':')) return false;
        }
        return true;
    }

    internal static bool IsPermission(string value) => IsWireName(value, 192) && value.Contains(':', StringComparison.Ordinal);

    internal static bool IsErrorCode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        foreach (var segment in value.Split(':'))
        {
            if (segment.Length == 0 || segment[0] is < 'a' or > 'z') return false;
            if (segment.Any(static character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))) return false;
        }
        return true;
    }

    internal static bool IsSafeMessage(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);

    internal static bool IsCorrelationId(string? value) => value is null || value.Length is > 0 and <= 128 && value.All(static character => character is >= (char)0x21 and <= (char)0x7e);

    internal static void ValidateId(string value, string parameterName, int maximumLength = 128)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength || value.Any(static character => character is < (char)0x21 or > (char)0x7e))
            throw new ArgumentException("An opaque ID must be non-empty printable ASCII within the configured bound.", parameterName);
    }
}
