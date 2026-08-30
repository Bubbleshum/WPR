using System;

using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The WP-styled progress sheet used for copying, installing, re-patching and
    /// preparing a game. Replaces <c>ProgressDialog</c>, which is deprecated and paints
    /// a Material spinner that looks nothing like the rest of the shell.
    ///
    /// <para>Every mutator marshals to the UI thread itself, because the installer reports
    /// progress from a worker.</para>
    /// </summary>
    internal sealed class WpProgressDialog
    {
        private readonly Activity _Host;
        private readonly AlertDialog _Dialog;
        private readonly TextView _Stage;
        private readonly ProgressBar _Bar;

        private WpProgressDialog(Activity host, AlertDialog dialog, TextView stage, ProgressBar bar)
        {
            _Host = host;
            _Dialog = dialog;
            _Stage = stage;
            _Bar = bar;
        }

        public static WpProgressDialog Show(Activity host, string title, string stage, bool indeterminate)
        {
            View view = host.LayoutInflater.Inflate(Resource.Layout.dialog_install, null)!;

            TextView titleView = view.FindViewById<TextView>(Resource.Id.installTitle)!;
            TextView stageView = view.FindViewById<TextView>(Resource.Id.installStage)!;
            ProgressBar bar = view.FindViewById<ProgressBar>(Resource.Id.installProgress)!;

            titleView.Text = title;
            stageView.Text = stage;
            bar.Indeterminate = indeterminate;
            WpTheme.ApplyProgress(bar);
            bar.IndeterminateTintList = global::Android.Content.Res.ColorStateList.ValueOf(WpTheme.Accent);

            AlertDialog dialog = new AlertDialog.Builder(host)!
                .SetView(view)!
                .SetCancelable(false)!
                .Create()!;

            // The dialog frame would otherwise draw a rounded Material card behind our flat
            // WP panel, leaving a grey halo around the corners.
            dialog.Window?.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
            dialog.Show();

            return new WpProgressDialog(host, dialog, stageView, bar);
        }

        public void SetStage(string stage) => _Host.RunOnUiThread(() =>
        {
            if (_Dialog.IsShowing) _Stage.Text = stage;
        });

        /// <summary>Switch to determinate and set the percentage in one call.</summary>
        public void SetProgress(int percent) => _Host.RunOnUiThread(() =>
        {
            if (!_Dialog.IsShowing) return;
            if (_Bar.Indeterminate) _Bar.Indeterminate = false;
            _Bar.Progress = Math.Max(0, Math.Min(100, percent));
        });

        public void Dismiss() => _Host.RunOnUiThread(() =>
        {
            try { if (_Dialog.IsShowing) _Dialog.Dismiss(); }
            catch (Exception) { /* activity already gone; nothing to dismiss */ }
        });
    }
}
