using NeoAstra;

namespace NeoAstra.Samples.Mobile.Hello;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;

    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new("Default Configuration", connectingSceneSession.Role);
}

/// <summary>Shows the application in the scene that connects.</summary>
[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("scene:willConnectToSession:options:")]
    public async void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        try
        {
            await NeoApp.AttachAsync(HelloApp.Configure);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NeoAstra could not start: {exception}");
        }
    }
}
