using NeoAstra.Push;

namespace NeoAstra.Samples.Mobile;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;

    // iOS gives the device token of APNs to the application delegate, which NeoAstra does not own.
    public override void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken) => NeoApplePush.DidRegister(deviceToken.ToArray());

    public override void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error) => NeoApplePush.DidFailToRegister(error.LocalizedDescription);

    // A silent message (content-available) comes here, not to the notification center.
    public override void DidReceiveRemoteNotification(UIApplication application, NSDictionary userInfo, Action<UIBackgroundFetchResult> completionHandler) =>
        NeoApplePush.DidReceiveRemoteNotification(userInfo.Handle.Handle, () => completionHandler(UIBackgroundFetchResult.NewData));

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
        // A scene that the user opened by tapping a notification has it in its options.
        NeoApplePush.DidLaunch(connectionOptions.NotificationResponse?.Handle.Handle ?? 0);
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
