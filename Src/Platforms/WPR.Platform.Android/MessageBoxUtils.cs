using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Android.App;
using Android.Widget;

namespace WPR.Platform.Android
{
    public static class MessageBoxUtils
    {
        public static Activity MainActivity { get; set; }

        /// <summary>
        /// The presenter behind a game's Silverlight <c>MessageBox.Show</c>: an AlertDialog with one
        /// button per label, completing with the index pressed or -1 when it was dismissed (the
        /// hardware Back button, a tap outside). The shim maps that to a <c>MessageBoxResult</c>.
        /// </summary>
        /// <remarks>
        /// The game thread (SDL's, in the <c>:game</c> process) waits on this task, so every way the
        /// dialog can go away completes it: a button, cancel (Back, outside tap) and a plain dismiss
        /// (the activity finishing under it). The first label (the affirmative: ok, yes) is the
        /// positive button and the second the negative one, so the layout follows Android's own
        /// convention (affirmative on the right) rather than WP7's (ok on the left).
        /// Captions are the WP7 lower-case labels the shim passes in, as WP7 drew them.
        /// </remarks>
        public static Task<int> ShowGameMessageBoxAsync(string caption, string text, IReadOnlyList<string> labels)
        {
            var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Activity? activity = MainActivity;
            if (activity == null || activity.IsFinishing)
            {
                source.TrySetResult(-1);
                return source.Task;
            }

            activity.RunOnUiThread(() =>
            {
                try
                {
                    var builder = new AlertDialog.Builder(activity)!
                        .SetMessage(text)!
                        .SetCancelable(true)!;
                    if (!string.IsNullOrEmpty(caption))
                    {
                        builder = builder.SetTitle(caption)!;
                    }

                    // AlertDialog has three slots: positive (right), negative, neutral (left).
                    if (labels.Count > 0)
                        builder = builder.SetPositiveButton(labels[0], (_, _) => source.TrySetResult(0))!;
                    if (labels.Count > 1)
                        builder = builder.SetNegativeButton(labels[1], (_, _) => source.TrySetResult(1))!;
                    if (labels.Count > 2)
                        builder = builder.SetNeutralButton(labels[2], (_, _) => source.TrySetResult(2))!;

                    builder = builder
                        .SetOnCancelListener(new DialogListener(() => source.TrySetResult(-1)))!
                        .SetOnDismissListener(new DialogListener(() => source.TrySetResult(-1)))!;

                    var dialog = builder.Create()!;
                    dialog.SetCanceledOnTouchOutside(true);
                    dialog.Show();
                }
                catch (Exception ex)
                {
                    Common.Log.Error(Common.LogCategory.AppList, "Game message box failed: " + ex);
                    source.TrySetResult(-1);
                }
            });

            return source.Task;
        }

        private sealed class DialogListener : Java.Lang.Object,
            global::Android.Content.IDialogInterfaceOnCancelListener,
            global::Android.Content.IDialogInterfaceOnDismissListener
        {
            private readonly Action _action;
            public DialogListener(Action action) { _action = action; }
            public void OnCancel(global::Android.Content.IDialogInterface? dialog) => _action();
            public void OnDismiss(global::Android.Content.IDialogInterface? dialog) => _action();
        }

        /// <summary>
        /// Shows an error dialog whose body is a read-only, selectable, multi-line TextBox so
        /// the user can copy the text. A "Copy" button copies the full body to the clipboard.
        /// On Android this is a plain AlertDialog with one OK button (no copy button).
        /// </summary>
        public static Task ShowSelectableErrorAsync(string title, string body)
        {
            return ShowGameMessageBoxAsync(title, body, new[] { "OK" });
        }

        public static Task<string> GetInputResult(string title, string description, string defaultText, bool isModal = true, bool dispatchMain = false)
        {
            TaskCompletionSource<string> source = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            MainActivity.RunOnUiThread(() =>
            {
                EditText editField = new EditText(MainActivity);
                editField.SetText(defaultText, TextView.BufferType.Editable);

                AlertDialog.Builder builder = new AlertDialog.Builder(MainActivity)!
                    .SetTitle(title)!
                    .SetMessage(description)!
                    .SetView(editField)!
                    .SetPositiveButton(global::Android.Resource.String.Ok, (dialog, which) =>
                    {
                        source.TrySetResult(editField.Text!);
                        (dialog as AlertDialog)!.Dismiss();
                    })!
                    .SetNegativeButton(global::Android.Resource.String.Cancel, (dialog, which) =>
                    {
                        source.TrySetResult(defaultText);
                        (dialog as AlertDialog)!.Dismiss();
                    })!;

                // Back, a tap outside and the activity going away close the dialog without a button;
                // each must still complete the task, or the caller waits for ever.
                builder = builder
                    .SetOnCancelListener(new DialogListener(() => source.TrySetResult(defaultText)))!
                    .SetOnDismissListener(new DialogListener(() => source.TrySetResult(defaultText)))!;

                builder.Create()!.Show();
            });

            return source.Task;
        }
    }
}
