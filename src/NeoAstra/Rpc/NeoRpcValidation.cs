// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

#if NEOASTRA_TOOL
namespace NeoAstra.Tool.Shared.Rpc;
#else
namespace NeoAstra.Rpc;
#endif

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

    internal static bool IsPermission(string value)
    {
        if (!IsWireName(value, 192)) return false;
        var segments = value.Split(':');
        return segments.Length >= 2 && segments.All(static segment => IsWireName(segment, 128));
    }

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
