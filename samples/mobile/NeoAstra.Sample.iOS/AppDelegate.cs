namespace NeoAstra.Samples.Mobile;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;

    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new("Default Configuration", connectingSceneSession.Role);
}

/// <summary>Shows the sample page in the view of the root view controller of the scene.</summary>
[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    private static MobileHost? host;

    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public async void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        var controller = new UIViewController();
        controller.View!.BackgroundColor = UIColor.SystemBackground;
        Window = new UIWindow(windowScene) { RootViewController = controller };
        Window.MakeKeyAndVisible();
        try
        {
            host = await MobileHost.StartAsync(NeoAstraHost.FromUIKitUIView(controller.View.Handle.Handle), Path.Combine(NSBundle.MainBundle.BundlePath, "assets"));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NeoAstra could not start: {exception}");
        }
    }
}
