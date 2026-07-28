using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using MinorShift.Emuera;

namespace Emuera.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ScreenOrientation = ScreenOrientation.FullSensor, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private SafGameDirAccessor? _safAccessor;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            var launcher = RegisterForActivityResult(
                new ActivityResultContracts.OpenDocumentTree(),
                new SafResultCallback(this));

            _safAccessor = new SafGameDirAccessor(Android.App.Application.Context, launcher);
            Android.Util.Log.Info("EmueraMaui", "MainActivity: SafGameDirAccessor initialized");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("EmueraMaui", $"MainActivity.OnCreate SAF init failed: {ex}");
        }
    }

    private sealed class SafResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        private readonly MainActivity _activity;
        public SafResultCallback(MainActivity activity) => _activity = activity;
        public void OnActivityResult(Java.Lang.Object? result)
        {
            var uri = result as Android.Net.Uri;
            _activity._safAccessor?.OnTreeResult(uri);
        }
    }
}
