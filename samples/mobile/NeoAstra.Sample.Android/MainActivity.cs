using Android.Content.PM;
using Android.Widget;

namespace NeoAstra.Samples.Mobile;

/// <summary>Shows the sample page in the content view of the activity.</summary>
[Activity(Label = "NeoAstra", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, Theme = "@android:style/Theme.DeviceDefault.Light.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity
{
    private static MobileHost? host;

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Android 15 draws an application edge to edge, and the WebView reports no safe-area insets there: the root view keeps
        // the page clear of the system bars instead.
        var root = new FrameLayout(this);
        root.SetFitsSystemWindows(true);
        SetContentView(root);
        try
        {
            host = await MobileHost.StartAsync(NeoAstraHost.FromAndroidViewGroup(root.Handle), CopyAssets());
        }
        catch (Exception exception)
        {
            Android.Util.Log.Error("NeoAstra", $"NeoAstra could not start: {exception}");
        }
    }

    // The resource provider serves files, and Android keeps the assets of an application inside its package.
    private string CopyAssets()
    {
        var target = Path.Combine(FilesDir!.AbsolutePath, "web");
        Copy("web", target);
        return target;
    }

    private void Copy(string source, string target)
    {
        var entries = Assets!.List(source) ?? [];
        if (entries.Length == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = Assets.Open(source);
            using var output = File.Create(target);
            input.CopyTo(output);
            return;
        }
        foreach (var entry in entries) Copy($"{source}/{entry}", Path.Combine(target, entry));
    }
}
