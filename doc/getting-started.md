# Getting started

## Requirements

NeoAstra targets **.NET 10**. A desktop application also needs a graphical desktop and the platform's
browser runtime; an iOS or Android application (preview) builds with the .NET 11 SDK. See
[platform support and runtime dependencies](platform-support.md) for target architectures, and
[known limitations](known-limitations.md) for the differences between platforms.

## Packages

| Package | Purpose |
| --- | --- |
| `NeoAstra` | The single runtime reference for ordinary apps: hosting, RPC, desktop services, generators, and frontend build integration. |
| `NeoAstra.Core` | Low-level native window and WebView APIs for custom hosts; included by `NeoAstra`. Public types remain in the `NeoAstra` namespace. |
| `NeoAstra.Tool` | Optional `dotnet neoastra` development and delivery tooling; installed as a .NET tool, not a runtime reference. |
| `NeoAstra.Templates` | Vanilla TypeScript, React, and Vue `dotnet new` templates for the desktop, and `neoastra-mobile` for iOS and Android. |
| `NeoAstra.Location`, `NeoAstra.Push`, `NeoAstra.Passkeys`, `NeoAstra.Purchases` | Optional plugins for iOS and Android: the location of the device, push messages, passkeys, and in-app purchases. |

Follow the [create, run, develop, and publish guide](frontend-tooling-and-assets.md#consumer-path-create-run-develop-publish)
for package installation and template commands. Keep the package, tool, and template versions aligned.

## Minimal application host

Reference the `NeoAstra` package in a .NET 10 executable project. A minimal `Program.cs` looks like this:

```csharp
using System;
using NeoAstra;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        return NeoApp.Run(args, app => app.Title = "Hello, NeoAstra!");
    }
}
```

Place your web content in `frontend/index.html` beside the project file. For example:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Hello, NeoAstra!</title>
</head>
<body>
  <h1>Hello, NeoAstra!</h1>
</body>
</html>
```

Run the project with `dotnet run`. The build prepares the assets, and `NeoApp` hosts them in a native
window. This static frontend does not require Node.js or npm when using the NuGet package.

Application and browser operations must begin on the platform UI thread; Windows entry points require
the `[STAThread]` attribute shown above. `NeoApp` hosts controlled local content and blocks other
top-level navigation and unexpected new windows by default. See [capabilities and security](capabilities-and-security.md)
before exposing native operations or introducing untrusted content.

For a frontend that calls .NET services, follow [typed RPC and generated bindings](rpc-and-bindings.md).

## On a phone (preview)

The same application runs on iOS and Android. iOS and Android own the run loop of their app, so instead
of `NeoApp.Run`, the activity or the window scene calls `NeoApp.AttachAsync` with the same configuration:

```csharp
// Android: MainActivity.OnCreate. iOS: the scene delegate, once the scene connects.
await NeoApp.AttachAsync(app => app.Title = "Hello, NeoAstra!");
```

The `neoastra-mobile` template sets up one project for both platforms. See
[the simple host on iOS and Android](application-lifecycle-and-hosting.md#the-simple-host-on-ios-and-android-preview).

## Samples

- [HelloWorld](../samples/NeoAstra.Sample) — a small app with plain JavaScript and generated RPC bindings.
- [Advanced feature tour](../samples/NeoAstra.Sample.Advanced/readme.md) — React, typed RPC, streaming, lifecycle, and desktop services.
- [Core sample](../samples/NeoAstra.Core.Sample) — direct use of the low-level window and WebView APIs.
- [Mobile samples](../samples/mobile/readme.md) — a Hello application for iOS and Android, and a tour of the services and plugins on a phone.

To try HelloWorld from a source checkout, with the [build prerequisites](building.md#prerequisites)
installed, run this from the repository root:

```sh
dotnet run --project samples/NeoAstra.Sample -c Release
```

See [building and verification](building.md) for NativeAOT publishing and further checks.
