// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using NeoAstra;

if (args.Length == 5 && args[0] == "--race-session")
{
    // A process started from another terminal or from the desktop is in another Unix session. The runtime reads
    // its session when it starts, so the race itself runs in a child of the new session.
    if (Native.SetSessionId() < 0) return 12;
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add(typeof(Native).Assembly.Location);
    start.ArgumentList.Add("--race");
    foreach (var argument in args.AsSpan(1)) start.ArgumentList.Add(argument);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the race in the new session.");
    await child.WaitForExitAsync();
    return child.ExitCode;
}

if (args.Length == 5 && args[0] == "--race")
{
    while (!File.Exists(args[2])) await Task.Delay(10);
    var application = (NeoApplication)RuntimeHelpers.GetUninitializedObject(typeof(NeoApplication));
    try
    {
        await using var instance = await NeoSingleInstance.AcquireAsync(application,
            new NeoSingleInstanceOptions { ApplicationId = args[1], AcknowledgementTimeout = TimeSpan.FromSeconds(2) },
            new NeoLaunchEvent(NeoLaunchReason.SecondInstance));
        if (!instance.IsPrimary) return 11;
        File.WriteAllText(args[3], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Task.Delay(int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
        return 10;
    }
    catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException)
    {
        return 11;
    }
}

if (args.Length is not (2 or 3)) return 2;
var secondaryApplication = (NeoApplication)RuntimeHelpers.GetUninitializedObject(typeof(NeoApplication));
var launch = new NeoLaunchEvent(NeoLaunchReason.SecondInstance, files: [Path.GetFullPath(args[1])]);
try
{
    await using var secondaryInstance = await NeoSingleInstance.AcquireAsync(secondaryApplication,
        new NeoSingleInstanceOptions
        {
            ApplicationId = args[0],
            AcknowledgementTimeout = args.Length == 3 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5),
            HungPrimaryPolicy = args.Length == 3 ? NeoSingleInstanceHungPrimaryPolicy.Retry : NeoSingleInstanceHungPrimaryPolicy.Fail,
        }, launch);
    return secondaryInstance.IsPrimary ? 3 : 0;
}
catch (TimeoutException) { return 5; }
catch (InvalidOperationException) { return 4; }

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setsid")]
    internal static extern int SetSessionId();
}
