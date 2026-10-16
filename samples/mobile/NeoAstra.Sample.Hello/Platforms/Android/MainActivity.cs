using Android.Content.PM;
using NeoAstra;

namespace NeoAstra.Samples.Mobile.Hello;

/// <summary>Shows the application in the activity, which a window of NeoAstra fills.</summary>
[Activity(Label = "NeoAstra Hello", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, Theme = "@android:style/Theme.DeviceDefault.Light.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity
{
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            await NeoApp.AttachAsync(HelloApp.Configure);
        }
        catch (Exception exception)
        {
            Android.Util.Log.Error("NeoAstra", $"NeoAstra could not start: {exception}");
        }
    }
}
