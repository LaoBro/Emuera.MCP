using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using SafVerification.Services;

namespace SafVerification;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.FullSensor,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            var launcher = RegisterForActivityResult(
                new ActivityResultContracts.OpenDocumentTree(),
                new SafResultCallback());

            _ = new SafService(Android.App.Application.Context, launcher!);

            Android.Util.Log.Info("SafVerification", "OnCreate: SafService OK");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("SafVerification", $"OnCreate CRASH: {ex}");
            SafService.InitError = ex.ToString();
        }
    }

    private sealed class SafResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result)
        {
            var uri = result as Android.Net.Uri;
            SafService.Current?.OnTreeResult(uri);
        }
    }
}
