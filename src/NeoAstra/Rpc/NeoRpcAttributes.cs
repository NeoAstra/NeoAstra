// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoAstra.Rpc;

/// <summary>Marks an explicitly exported RPC service and assigns its stable wire name.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NeoRpcServiceAttribute : Attribute
{
    /// <summary>Initializes a service declaration.</summary>
    /// <param name="name">The stable service wire name.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public NeoRpcServiceAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A service wire name is required.", nameof(name));
        Name = name;
    }

    /// <summary>Gets the stable service wire name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the positive service contract version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the default service lifetime used by generated factory registration.</summary>
    public NeoRpcServiceLifetime Lifetime { get; set; } = NeoRpcServiceLifetime.ApplicationSingleton;
}

/// <summary>Marks an explicitly exported RPC method and assigns its stable wire name.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NeoRpcMethodAttribute : Attribute
{
    /// <summary>Initializes a method declaration.</summary>
    /// <param name="name">The stable method wire name.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public NeoRpcMethodAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A method wire name is required.", nameof(name));
        Name = name;
    }

    /// <summary>Gets the stable method wire name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the permission checked before dispatch, or <see langword="null"/> to trust the explicitly registered application command.</summary>
    public string? Permission { get; set; }

    /// <summary>Gets or sets the scheduler used to invoke the method.</summary>
    public NeoRpcDispatchMode Dispatch { get; set; }

    /// <summary>Gets or sets the command timeout in milliseconds; zero uses the host default.</summary>
    public int TimeoutMilliseconds { get; set; }
}

/// <summary>Marks a declaration used to generate a typed event contract.</summary>
[AttributeUsage(AttributeTargets.Event | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class NeoRpcEventAttribute : Attribute
{
    /// <summary>Initializes an event declaration.</summary>
    /// <param name="name">The stable event wire name.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public NeoRpcEventAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An event wire name is required.", nameof(name));
        Name = name;
    }

    /// <summary>Gets the stable event wire name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the permission checked before subscription, or <see langword="null"/> to trust the explicitly registered application event.</summary>
    public string? Permission { get; set; }

    /// <summary>Gets or sets the bounded-queue overflow behavior fixed by the declaration.</summary>
    public NeoRpcOverflowBehavior OverflowBehavior { get; set; } = NeoRpcOverflowBehavior.DropOldest;
}

/// <summary>Marks an explicit discriminated-union root.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class NeoRpcUnionAttribute : Attribute
{
    /// <summary>Initializes a union declaration.</summary>
    /// <param name="discriminator">The stable JSON discriminator property.</param>
    /// <exception cref="ArgumentException"><paramref name="discriminator"/> is empty.</exception>
    public NeoRpcUnionAttribute(string discriminator)
    {
        if (string.IsNullOrWhiteSpace(discriminator)) throw new ArgumentException("A union discriminator is required.", nameof(discriminator));
        Discriminator = discriminator;
    }

    /// <summary>Gets the JSON discriminator property.</summary>
    public string Discriminator { get; }
}

/// <summary>Specifies that a 64-bit integer uses the explicit decimal-string RPC wire policy.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NeoRpcInt64Attribute : Attribute
{
    /// <summary>Initializes a 64-bit integer policy.</summary>
    /// <param name="policy">The explicit wire policy.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is undefined.</exception>
    public NeoRpcInt64Attribute(NeoRpcInt64Policy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        Policy = policy;
    }

    /// <summary>Gets the explicit wire policy.</summary>
    public NeoRpcInt64Policy Policy { get; }

}

/// <summary>Serializes signed RPC 64-bit integers as canonical invariant decimal strings.</summary>
public sealed class NeoRpcInt64JsonConverter : JsonConverter<long>
{
    /// <inheritdoc />
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("RPC Int64 values must be decimal strings.");
        var text = reader.GetString()!;
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value.ToString(CultureInfo.InvariantCulture) != text)
            throw new JsonException("The RPC Int64 decimal string is not canonical.");
        return value;
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Serializes unsigned RPC 64-bit integers as canonical invariant decimal strings.</summary>
public sealed class NeoRpcUInt64JsonConverter : JsonConverter<ulong>
{
    /// <inheritdoc />
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("RPC UInt64 values must be decimal strings.");
        var text = reader.GetString()!;
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value.ToString(CultureInfo.InvariantCulture) != text)
            throw new JsonException("The RPC UInt64 decimal string is not canonical.");
        return value;
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Serializes nullable signed RPC 64-bit integers as canonical invariant decimal strings or JSON null.</summary>
public sealed class NeoRpcNullableInt64JsonConverter : JsonConverter<long?>
{
    /// <inheritdoc />
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("RPC nullable Int64 values must be decimal strings or null.");
        var text = reader.GetString()!;
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value.ToString(CultureInfo.InvariantCulture) != text)
            throw new JsonException("The RPC nullable Int64 decimal string is not canonical.");
        return value;
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>Serializes nullable unsigned RPC 64-bit integers as canonical invariant decimal strings or JSON null.</summary>
public sealed class NeoRpcNullableUInt64JsonConverter : JsonConverter<ulong?>
{
    /// <inheritdoc />
    public override ulong? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("RPC nullable UInt64 values must be decimal strings or null.");
        var text = reader.GetString()!;
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value.ToString(CultureInfo.InvariantCulture) != text)
            throw new JsonException("The RPC nullable UInt64 decimal string is not canonical.");
        return value;
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ulong? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>Identifies the application source-generated JSON context used by generated RPC dispatch.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class NeoRpcJsonContextAttribute : Attribute
{
    /// <summary>Initializes a serializer-context declaration.</summary>
    /// <param name="contextType">A <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> partial type with RPC DTO metadata.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contextType"/> is <see langword="null"/>.</exception>
    public NeoRpcJsonContextAttribute(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        ContextType = contextType;
    }

    /// <summary>Gets the source-generated serializer context type.</summary>
    public Type ContextType { get; }
}

/// <summary>Defines an RPC service instance lifetime.</summary>
public enum NeoRpcServiceLifetime
{
    /// <summary>One instance is retained by the application RPC host.</summary>
    ApplicationSingleton,
    /// <summary>One instance is retained for each view.</summary>
    PerView,
    /// <summary>One instance is retained for each document session.</summary>
    PerDocumentSession,
    /// <summary>A new instance is created and disposed for each invocation.</summary>
    PerInvocation,
}

/// <summary>Defines where application command code executes.</summary>
public enum NeoRpcDispatchMode
{
    /// <summary>Dispatch without entering the NeoAstra UI dispatcher.</summary>
    Background,
    /// <summary>Dispatch through the originating view's UI dispatcher.</summary>
    UiThread,
}

/// <summary>Defines bounded event queue overflow behavior.</summary>
public enum NeoRpcOverflowBehavior
{
    /// <summary>Discard the oldest queued event.</summary>
    DropOldest,
    /// <summary>Discard the new event.</summary>
    DropNewest,
    /// <summary>Keep only the most recent queued event.</summary>
    Coalesce,
    /// <summary>Fail and remove the overflowing subscription.</summary>
    Fail,
}

/// <summary>Defines the JavaScript representation of signed and unsigned 64-bit integers.</summary>
public enum NeoRpcInt64Policy
{
    /// <summary>Encode the integer as a decimal JSON string.</summary>
    String,
}
