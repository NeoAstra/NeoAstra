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
    /// <summary>Gets or sets the maximum resources owned by one view across document sessions.</summary>
    public int MaximumResourcesPerView { get; set; } = 256;
    /// <summary>Gets or sets the maximum resources owned by the application.</summary>
    public int MaximumResources { get; set; } = 4096;
    /// <summary>Gets or sets maximum declared resource bytes per document session.</summary>
    public long MaximumResourceBytesPerSession { get; set; } = 64 * 1024 * 1024;
    /// <summary>Gets or sets maximum declared resource bytes across the application.</summary>
    public long MaximumResourceBytes { get; set; } = 1024L * 1024 * 1024;
    /// <summary>Gets or sets sustained accepted invocation/subscription frames per second in one document session.</summary>
    public int RequestRatePerSecond { get; set; } = 100;
    /// <summary>Gets or sets the bounded per-session request burst.</summary>
    public int RequestRateBurst { get; set; } = 200;
    /// <summary>Gets or sets the abuse-denial count after which the offending document session is closed.</summary>
    public int AbuseClosureThreshold { get; set; } = 20;
    /// <summary>Gets or sets the named resolved security profile.</summary>
    public NeoSecurityProfile SecurityProfile { get; set; } = NeoSecurityProfile.ProductionLocalApp;
    /// <summary>Gets or sets the immutable embedded capability manifest used for diagnostics.</summary>
    public NeoCapabilityManifest? CapabilityManifest { get; set; }
    /// <summary>Gets or sets whether release configuration validation is active.</summary>
    public bool Release { get; set; } = true;
    /// <summary>Gets or sets an exact development origin.</summary>
    public Uri? DevelopmentOrigin { get; set; }
    /// <summary>Gets or sets whether a non-loopback development origin was explicitly reviewed.</summary>
    public bool AllowRemoteDevelopmentOrigin { get; set; }
    /// <summary>Gets or sets the optional authorization service used for operations that declare a permission.</summary>
    /// <remarks>Operations without a permission are application-authorized. An operation that declares a permission is denied when this is <see langword="null"/>.</remarks>
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
        if (MaximumResourcesPerView < MaximumResourcesPerSession || MaximumResourcesPerView > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumResourcesPerView));
        if (MaximumResources <= MaximumResourcesPerView || MaximumResources > 10_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumResources));
        if (MaximumResourceBytesPerSession is < 1024 or > 16L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaximumResourceBytesPerSession));
        if (MaximumResourceBytes <= MaximumResourceBytesPerSession || MaximumResourceBytes > 1024L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaximumResourceBytes));
        if (RequestRatePerSecond is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(RequestRatePerSecond));
        if (RequestRateBurst < RequestRatePerSecond || RequestRateBurst > 1_000_000) throw new ArgumentOutOfRangeException(nameof(RequestRateBurst));
        if (AbuseClosureThreshold is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(AbuseClosureThreshold));
        ArgumentNullException.ThrowIfNull(SecurityProfile);
        SecurityProfile.Validate(Release, DevelopmentOrigin, AllowRemoteDevelopmentOrigin);
        if (!SecurityProfile.BridgeEnabled && (AuthorizationService is not null || CapabilityManifest is not null)) throw new InvalidOperationException("The remote-content profile cannot enable RPC authorization or capabilities.");
        if (CapabilityManifest is not null && !ReferenceEquals(CapabilityManifest.Profile, SecurityProfile)) throw new InvalidOperationException("The capability manifest security profile does not match the RPC host profile.");
        if (AuthorizationService is NeoCapabilityAuthorizationService capabilityAuthorization && CapabilityManifest is not null && !ReferenceEquals(capabilityAuthorization.Manifest, CapabilityManifest)) throw new InvalidOperationException("The authorization service and diagnostic manifest must use the same immutable resolved manifest.");
        if (IncludeDevelopmentErrorDetails && (Release || !SecurityProfile.DetailedErrors)) throw new InvalidOperationException("Detailed RPC errors require a non-release development profile.");
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
            MaximumResourcesPerView = MaximumResourcesPerView,
            MaximumResources = MaximumResources,
            MaximumResourceBytesPerSession = MaximumResourceBytesPerSession,
            MaximumResourceBytes = MaximumResourceBytes,
            RequestRatePerSecond = RequestRatePerSecond,
            RequestRateBurst = RequestRateBurst,
            AbuseClosureThreshold = AbuseClosureThreshold,
            SecurityProfile = SecurityProfile,
            CapabilityManifest = CapabilityManifest,
            Release = Release,
            DevelopmentOrigin = DevelopmentOrigin,
            AllowRemoteDevelopmentOrigin = AllowRemoteDevelopmentOrigin,
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
    /// <summary>Gets or sets the optional permission checked before dispatch.</summary>
    /// <remarks>A <see langword="null"/> permission trusts the explicitly registered application command.</remarks>
    public string? Permission { get; set; }
    /// <summary>Gets or sets the dispatch scheduler.</summary>
    public NeoRpcDispatchMode Dispatch { get; set; }
    /// <summary>Gets or sets a command-specific timeout, or <see langword="null"/> for the host default.</summary>
    public TimeSpan? Timeout { get; set; }
    /// <summary>Gets or sets a command-specific concurrency bound.</summary>
    public int MaximumConcurrency { get; set; } = 8;

    internal NeoRpcCommandOptions CloneValidated()
    {
        if (Permission is not null && !NeoRpcValidation.IsPermission(Permission)) throw new ArgumentException("A command permission must be a bounded colon-separated ASCII identifier.", nameof(Permission));
        if (!Enum.IsDefined(Dispatch)) throw new ArgumentOutOfRangeException(nameof(Dispatch));
        if (Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MaximumConcurrency is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrency));
        return new NeoRpcCommandOptions { Permission = Permission, Dispatch = Dispatch, Timeout = Timeout, MaximumConcurrency = MaximumConcurrency };
    }
}

/// <summary>Configures one registered event.</summary>
public sealed class NeoRpcEventOptions
{
    /// <summary>Gets or sets the optional permission checked before subscription.</summary>
    /// <remarks>A <see langword="null"/> permission trusts the explicitly registered application event.</remarks>
    public string? Permission { get; set; }
    /// <summary>Gets or sets the declaration-owned bounded queue overflow behavior.</summary>
    public NeoRpcOverflowBehavior OverflowBehavior { get; set; } = NeoRpcOverflowBehavior.DropOldest;

    internal NeoRpcEventOptions CloneValidated()
    {
        if (Permission is not null && !NeoRpcValidation.IsPermission(Permission)) throw new ArgumentException("An event permission must be a bounded colon-separated ASCII identifier.", nameof(Permission));
        if (!Enum.IsDefined(OverflowBehavior)) throw new ArgumentOutOfRangeException(nameof(OverflowBehavior));
        return new NeoRpcEventOptions { Permission = Permission, OverflowBehavior = OverflowBehavior };
    }
}
