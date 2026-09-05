using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Opener;
using NeoAstra.Desktop.WindowState;
using NeoAstra.Rpc;

namespace NeoAstra.Tests;

[TestClass]
public sealed class WindowsMenuTests
{
    [TestMethod]
    public async Task WindowMenuCanBeToggledThroughBrowserRpcAndClosedWhileVisible()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This regression exercises Win32 menu attachment and WebView2 resizing.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = "startup";
        var thread = new Thread(() =>
        {
            try
            {
                NeoApplication.Run(new NeoApplicationOptions { ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
                {
                    var root = Path.Combine(Path.GetTempPath(), "neoastra-menu-test-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(root);
                    try
                    {
                        await using var desktop = NeoDesktopServices.CreateSystem("neoastra.menu.test", "Menu test", "1.0", root, ["https://example.com"], [root], [root], [NeoOpenFileIntent.TextDocument], application.Dispatcher);
                        await using var plugins = new NeoPluginBuilder().AddNeoAstraDesktop(desktop).Build();
                        await plugins.StartAsync(application);
                        var window = application.CreateWindow(new NeoWindowOptions { Label = "main", Width = 1180, Height = 820 });
                        application.MainWindow = window;
                        await using var placement = new NeoWindowStateController(window, new NeoJsonWindowStateStore(Path.Combine(root, "placement")), "main");
                        window.Show();
                        stage = "browser creation";
                        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                        {
                            CustomSchemes = [NeoCustomScheme.Application("app", new MenuResourceProvider())],
                        });
                        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions
                        {
                            ViewLabel = "main", BridgePolicy = NeoBridgePolicy.TrustedOrigins, BridgeOrigins = ["app://neoastra"],
                        });
                        desktop.Menus.Commands.Register("hello", _ => ValueTask.CompletedTask);
                        var menu = new[]
                        {
                            NeoMenuItem.Command("hello", "Send managed greeting", "hello", "Ctrl+Shift+H"),
                            NeoMenuItem.Command("preview", "Open restricted preview", "hello", "Ctrl+Shift+P"),
                            NeoMenuItem.Separator("separator"),
                            NeoMenuItem.RoleItem("copy", NeoMenuRole.Copy, "Copy"),
                            NeoMenuItem.RoleItem("paste", NeoMenuRole.Paste, "Paste"),
                        };
                        var builder = new NeoRpcBuilder();
                        builder.AddCommand<bool, bool>("test.menu", async (visible, _, token) =>
                        {
                            if (visible) await desktop.Menus.SetMenuAsync("window:main", menu, token);
                            else Assert.IsTrue(await desktop.Menus.RemoveMenuAsync("window:main", token));
                            return visible;
                        }, WindowsMenuJsonContext.Default.Boolean, WindowsMenuJsonContext.Default.Boolean);
                        await using var rpc = builder.Build();
                        await using var binding = NeoRpcViewBinding.Bind(rpc, view);
                        await view.NavigateAsync(new Uri("app://neoastra/index.html"));
                        stage = "browser handshake";
                        while (await view.EvaluateScriptAsync("globalThis.menuReady === true", timeout.Token) != "true")
                            await Task.Delay(20, timeout.Token);
                        var hwnd = window.GetNativeHandle(NeoNativeHandleKind.Win32Hwnd).Value;
                        Assert.AreEqual(nint.Zero, GetMenu(hwnd));
                        for (var index = 0; index < 7; index++)
                        {
                            var visible = index % 2 == 0;
                            stage = $"menu visible={visible}, iteration={index}";
                            _ = await view.EvaluateScriptAsync($"globalThis.toggleMenu({visible.ToString().ToLowerInvariant()}); true", timeout.Token);
                            while (await view.EvaluateScriptAsync("globalThis.menuDone === true", timeout.Token) != "true")
                                await Task.Delay(20, timeout.Token);
                            Assert.AreEqual(visible ? menu.Length : 0, desktop.Menus.GetMenu("window:main").Count,
                                await view.EvaluateScriptAsync("JSON.stringify(globalThis.menuResult)", timeout.Token));
                            Assert.AreEqual(visible.ToString().ToLowerInvariant(), await view.EvaluateScriptAsync("globalThis.menuResult.value", timeout.Token));
                            Assert.AreEqual(visible, GetMenu(hwnd) != 0, "The native menu must match the acknowledged RPC state.");
                        }
                        stage = "window close";
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        window.Closed += (_, _) => closed.TrySetResult();
                        window.Close();
                        await closed.Task.WaitAsync(timeout.Token);
                    }
                    finally
                    {
                        application.ForceShutdown();
                        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
                    }
                });
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
        catch (TimeoutException) { Assert.Fail($"Native menu test hung during {stage}."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Assert.Fail($"Native menu test timed out during {stage}."); }
        catch (NeoAstraNativeLibraryException) { Assert.Inconclusive("The Windows native runtime is not staged."); }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetMenu(nint hwnd);

    private sealed class MenuResourceProvider : INeoResourceProvider
    {
        public NeoResourceResponse GetResponse(NeoResourceRequest request) => NeoResourceResponse.FromBytes("""
            <!doctype html><title>Native menu regression</title><script>
            const transport = globalThis[Symbol.for('@neoastra/client/transport/v1')];
            let nextId = 0;
            transport.setReceiveHandler(frame => {
                if (frame.kind === 'hello_ack') globalThis.menuReady = true;
                if (frame.kind === 'result') { globalThis.menuResult = frame; globalThis.menuDone = true; }
            });
            globalThis.toggleMenu = visible => {
                globalThis.menuDone = false;
                transport.send({neoastra: 1, kind: 'invoke', id: String(++nextId), command: 'test.menu', args: visible});
            };
            transport.send({neoastra:1,kind:'hello',protocol:{major:1,minor:0},features:['invoke','events'],client:{name:'menu-test',version:'1.0'}});
            </script>
            """u8.ToArray(), "text/html; charset=utf-8");
    }
}

[JsonSerializable(typeof(bool))]
internal sealed partial class WindowsMenuJsonContext : JsonSerializerContext;
