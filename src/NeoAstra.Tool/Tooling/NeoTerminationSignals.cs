// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.InteropServices;

namespace NeoAstra.Tooling;

/// <summary>
/// Ends a session on SIGTERM and SIGHUP the way Ctrl+C does on macOS and Linux: the signal cancels the session instead of
/// ending the process, so that the session tears down its process trees and removes what it set up on a device. Agents, IDEs,
/// and job runners stop a process with SIGTERM; a terminal that closes sends SIGHUP. Windows maps those signals to closing
/// the console and shutting down, which end the process whatever the handler does, so the tool registers nothing there and
/// Ctrl+C stays the way to end a session.
/// </summary>
internal static class NeoTerminationSignals
{
    internal static IDisposable Register(CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return OperatingSystem.IsWindows() ? new Registrations([]) : new Registrations([Handle(PosixSignal.SIGTERM, source), Handle(PosixSignal.SIGHUP, source)]);
    }

    private static PosixSignalRegistration Handle(PosixSignal signal, CancellationTokenSource source) => PosixSignalRegistration.Create(signal, context =>
    {
        context.Cancel = true;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    });

    private sealed class Registrations(PosixSignalRegistration[] registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations) registration.Dispose();
        }
    }
}
