// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;
using NeoAstra;

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
