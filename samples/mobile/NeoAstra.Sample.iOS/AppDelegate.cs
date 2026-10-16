namespace NeoAstra.Samples.Mobile;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;

    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new("Default Configuration", connectingSceneSession.Role);
}

/// <summary>Shows the sample page in a window of NeoAstra, which takes the scene that connects.</summary>
[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("scene:willConnectToSession:options:")]
    public async void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene) return;
        try
        {
            await MobileHost.ShowAsync(Path.Combine(NSBundle.MainBundle.BundlePath, "assets"));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NeoAstra could not start: {exception}");
        }
    }
}
