using System;
using System.Threading.Tasks;

using Android.App;
using Android.Content;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Small async wrappers over <see cref="AlertDialog"/> for the launcher shell.
    ///
    /// <para><c>MessageBoxUtils</c> already does this for the game-hosting path, but
    /// it is typed against <c>MessageBox.Avalonia</c>'s button and icon enums and routes
    /// through a single static activity — both wrong for a shell with several activities on
    /// the stack. These take the host explicitly and return plain results.</para>
    /// </summary>
    internal static class WpDialogs
    {
        public static Task<bool> ConfirmAsync(Activity host, string title, string message,
            string yes = "yes", string no = "no")
        {
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.RunOnUiThread(() =>
            {
                if (host.IsFinishing || host.IsDestroyed)
                {
                    source.TrySetResult(false);
                    return;
                }

                AlertDialog dialog = new AlertDialog.Builder(host)!
                    .SetTitle(title)!
                    .SetMessage(message)!
                    .SetPositiveButton(yes, (_, _) => source.TrySetResult(true))!
                    .SetNegativeButton(no, (_, _) => source.TrySetResult(false))!
                    .SetCancelable(true)!
                    .Create()!;

                // Covers back-button and tap-outside dismissals, which fire neither button.
                dialog.CancelEvent += (_, _) => source.TrySetResult(false);
                dialog.Show();
            });

            return source.Task;
        }

        /// <summary>Returns the index of the chosen item, or -1 if the sheet was dismissed.</summary>
        public static Task<int> ChooseAsync(Activity host, string title, string[] items)
        {
            var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.RunOnUiThread(() =>
            {
                if (host.IsFinishing || host.IsDestroyed)
                {
                    source.TrySetResult(-1);
                    return;
                }

                AlertDialog dialog = new AlertDialog.Builder(host)!
                    .SetTitle(title)!
                    .SetItems(items, (_, e) => source.TrySetResult(e.Which))!
                    .SetCancelable(true)!
                    .Create()!;

                dialog.CancelEvent += (_, _) => source.TrySetResult(-1);
                dialog.Show();
            });

            return source.Task;
        }

        public static void Error(Activity host, string title, string message)
        {
            host.RunOnUiThread(() =>
            {
                if (host.IsFinishing || host.IsDestroyed) return;

                new AlertDialog.Builder(host)!
                    .SetTitle(title)!
                    .SetMessage(message)!
                    .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!
                    .Show();
            });
        }
    }
}
