// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace NeoAstra;

/// <summary>Builds the scripts that call the automation agent in a page and reads what the agent answers.</summary>
internal static class NeoAutomationAgent
{
    /// <summary>The name under which the agent registers itself on the window of a document.</summary>
    internal const string GlobalName = "__neoastraAutomation";

    /// <summary>What a calling script returns when its document has no agent yet.</summary>
    internal const string Missing = "neoastra-automation-missing";

    private static readonly Lazy<string> SourceText = new(LoadSource);

    /// <summary>Gets the source of the agent, which installs itself once in a document.</summary>
    internal static string Source => SourceText.Value;

    // A calling script names itself, so that the agent can tell its frames from the ones of the page in a stack trace.
    private const string CallSource = "\n//# sourceURL=neoastra-automation-call.js";

    internal static string Call(string operation, string argumentsJson)
        => $"(function(){{var a=window.{GlobalName};return a?a.call(\"{operation}\",{argumentsJson}):\"{Missing}\"}})(){CallSource}";

    internal static string Start(long job, string operation, string argumentsJson)
        => $"(function(){{var a=window.{GlobalName};return a?a.start({job},\"{operation}\",{argumentsJson}):\"{Missing}\"}})(){CallSource}";

    internal static string Poll(long job)
        => $"(function(){{var a=window.{GlobalName};return a?a.poll({job}):\"{Missing}\"}})(){CallSource}";

    /// <summary>Builds the script that runs a caller's function. The function is part of the script, not a string in it.</summary>
    /// <remarks>The script is named apart from the agent's own, so that the frames of the function stay in its stack traces.</remarks>
    internal static string Evaluate(long job, string functionSource, string uidsJson)
        => $"(function(){{var a=window.{GlobalName};if(!a)return \"{Missing}\";return a.evaluate({job},function(){{return (\n{functionSource}\n)}},{uidsJson})}})()\n//# sourceURL=neoastra-evaluate-script.js";

    /// <summary>Writes a JSON value as text that is also a JavaScript expression.</summary>
    /// <param name="write">Writes the value.</param>
    /// <param name="readable">
    /// Whether the text is for a reader, and keeps characters such as quotes and angle brackets as they are, rather than
    /// for a script, where everything outside plain ASCII is escaped.
    /// </param>
    internal static string Json(Action<Utf8JsonWriter> write, bool readable = false)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var options = readable ? new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping } : default;
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string EmptyObject => "{}";

    /// <summary>Reads the JSON text of a JavaScript string that the engine returned as a JSON string literal.</summary>
    /// <returns>The string, or <see langword="null"/> when the engine returned something else.</returns>
    internal static string? ReadString(string? engineResult)
    {
        if (string.IsNullOrEmpty(engineResult) || engineResult[0] != '"') return null;
        try
        {
            using var document = JsonDocument.Parse(engineResult);
            return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string LoadSource()
    {
        using var stream = typeof(NeoAutomationAgent).Assembly.GetManifestResourceStream("NeoAstra.Automation.automation-agent.js")
            ?? throw new InvalidOperationException("The NeoAstra automation agent resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>An answer of the automation agent.</summary>
internal readonly struct NeoAutomationAgentResult
{
    private NeoAutomationAgentResult(bool isPending, bool isOk, JsonElement value, string? errorCode, string? errorMessage, string? errorUid, string? url, string? urlBefore, int? pollAfter = null, bool isLeaving = false)
    {
        IsPending = isPending;
        PollAfter = pollAfter;
        IsLeaving = isLeaving;
        IsOk = isOk;
        Value = value;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        ErrorUid = errorUid;
        Url = url;
        UrlBefore = urlBefore;
    }

    /// <summary>Gets whether a started operation has not completed yet.</summary>
    internal bool IsPending { get; }

    /// <summary>Gets the milliseconds before which a pending operation cannot complete, when the agent knows.</summary>
    internal int? PollAfter { get; }

    internal bool IsOk { get; }

    /// <summary>Gets the value of a successful operation. It does not depend on the parsed document.</summary>
    internal JsonElement Value { get; }

    internal string? ErrorCode { get; }

    internal string? ErrorMessage { get; }

    /// <summary>Gets the element identifier that an error is about, when the agent names one.</summary>
    internal string? ErrorUid { get; }

    /// <summary>Gets the address of the document when the operation completed.</summary>
    internal string? Url { get; }

    /// <summary>Gets the address of the document when the operation began.</summary>
    internal string? UrlBefore { get; }

    /// <summary>Gets whether the operation sent the page to another document, as far as the page can tell.</summary>
    internal bool IsLeaving { get; }

    internal static NeoAutomationAgentResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The automation agent returned something other than an object.");
        if (root.TryGetProperty("pending", out var pending) && pending.ValueKind == JsonValueKind.True)
        {
            int? again = root.TryGetProperty("again", out var againValue) && againValue.TryGetInt32(out var milliseconds) ? milliseconds : null;
            return new NeoAutomationAgentResult(true, false, default, null, null, null, null, null, again);
        }

        var url = root.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
        var before = root.TryGetProperty("before", out var beforeValue) ? beforeValue.GetString() : null;
        var leaving = root.TryGetProperty("leaving", out var leavingValue) && leavingValue.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            var value = root.TryGetProperty("value", out var valueElement) ? valueElement.Clone() : default;
            return new NeoAutomationAgentResult(false, true, value, null, null, null, url, before, isLeaving: leaving);
        }

        string? code = null, message = null, uid = null;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = error.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
            message = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            uid = error.TryGetProperty("uid", out var uidValue) ? uidValue.GetString() : null;
            if (error.TryGetProperty("name", out var nameValue) && nameValue.GetString() is { Length: > 0 } name && name != "Error" && message is not null && code == "error")
            {
                message = name + ": " + message;
            }
        }

        return new NeoAutomationAgentResult(false, false, default, code ?? "error", message ?? "The operation failed in the page.", uid, url, before, isLeaving: leaving);
    }

    internal NeoAutomationException ToException() => new(ErrorCode ?? "error", ErrorMessage ?? "The operation failed in the page.");
}
