// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using NeoWebView.Interop;
using NeoWebView.Interop.Generated;

namespace NeoWebView;

/// <summary>Represents a tracked native download.</summary>
public sealed class NeoDownload : IDisposable
{
    private readonly SafeDownloadHandle _handle;
    private int _disposed;

    internal NeoDownload(nint handle)
    {
        NativeMethods.neo_webview_download_retain(new(handle));
        _handle = new SafeDownloadHandle(handle);
        Refresh();
    }

    /// <summary>Gets the stable download identifier.</summary>
    public ulong Id { get; private set; }
    /// <summary>Gets the current lifecycle state.</summary>
    public NeoDownloadState State { get; private set; }
    /// <summary>Gets whether the backend supports pausing this download.</summary>
    public bool CanPause { get; private set; }
    /// <summary>Gets the source URI, when valid and available.</summary>
    public Uri? Source { get; private set; }
    /// <summary>Gets the selected destination path, when available.</summary>
    public string? DestinationPath { get; private set; }
    /// <summary>Gets the number of received bytes.</summary>
    public ulong BytesReceived { get; private set; }
    /// <summary>Gets the total byte count, or <see langword="null"/> when unknown.</summary>
    public ulong? TotalBytes { get; private set; }
    /// <summary>Gets the terminal failure description, when available.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Refreshes this object's snapshot from the native download.</summary>
    public unsafe void Refresh()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var raw = new NativeMethods.neo_webview_download_info
        {
            size = (uint)sizeof(NativeMethods.neo_webview_download_info),
            version = 1,
        };
        var info = new NativeMethods.neo_webview_download_info_t(raw);
        NativeError.ThrowIfFailed(NativeMethods.neo_webview_download_get_info(new(_handle.DangerousGetHandle()), &info), default, "get download information");
        raw = info.Value;
        Id = raw.id;
        State = (NeoDownloadState)raw.state.Value;
        CanPause = raw.can_pause != 0;
        var source = Utf8String.Decode(raw.source_uri);
        Source = Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri : null;
        var destination = Utf8String.Decode(raw.destination_path);
        DestinationPath = string.IsNullOrEmpty(destination) ? null : destination;
        BytesReceived = raw.bytes_received;
        TotalBytes = raw.total_bytes == ulong.MaxValue ? null : raw.total_bytes;
        var failure = Utf8String.Decode(raw.failure_reason);
        FailureReason = string.IsNullOrEmpty(failure) ? null : failure;
    }

    /// <summary>Cancels the download.</summary>
    public void Cancel()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        NativeError.ThrowIfFailed(NativeMethods.neo_webview_download_cancel(new(_handle.DangerousGetHandle())), default, "cancel download");
    }

    /// <summary>Pauses the download when <see cref="CanPause"/> is <see langword="true"/>.</summary>
    /// <exception cref="NotSupportedException">The active backend cannot pause this download.</exception>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        NativeError.ThrowIfFailed(NativeMethods.neo_webview_download_pause(new(_handle.DangerousGetHandle())), default, "pause download");
    }

    /// <summary>Resumes a paused download when <see cref="CanPause"/> is <see langword="true"/>.</summary>
    /// <exception cref="NotSupportedException">The active backend cannot resume this download.</exception>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        NativeError.ThrowIfFailed(NativeMethods.neo_webview_download_resume(new(_handle.DangerousGetHandle())), default, "resume download");
    }

    /// <summary>Releases this managed reference to the native download.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _handle.Dispose();
    }
}

/// <summary>Provides a tracked download lifecycle snapshot.</summary>
/// <param name="download">The tracked download.</param>
public sealed class NeoDownloadEventArgs(NeoDownload download) : EventArgs
{
    /// <summary>Gets the tracked download.</summary>
    public NeoDownload Download { get; } = download;
}
