using System;

using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Widget;

namespace WPR.Platform.Android.Native
{
    [Activity(
        Label = "about",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.AboutActivity")]
    public class AboutActivity : Activity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.activity_about);
            WpTheme.ApplySystemBars(this);

            FindViewById<TextView>(Resource.Id.appTitle)!.SetTextColor(WpTheme.Accent);

            // Read the version off the installed package rather than hardcoding it, so the
            // page cannot drift from what the csproj actually shipped.
            string version = "unknown";
            try
            {
                PackageInfo? info = PackageManager?.GetPackageInfo(PackageName!, 0);
                if (!string.IsNullOrEmpty(info?.VersionName)) version = info!.VersionName!;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("WPR", $"Could not read package version: {ex.Message}");
            }

            string release = Build.VERSION.Release ?? "?";
            int api = (int)Build.VERSION.SdkInt;
            string model = Build.Model ?? "device";

            FindViewById<TextView>(Resource.Id.aboutVersion)!.Text = $"WPR {version}";
            FindViewById<TextView>(Resource.Id.aboutBuild)!.Text =
                $"DEVELOPER EDITION  ·  android {release} (API {api})  ·  {model}";
        }
    }
}
