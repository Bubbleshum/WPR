using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Android.App;
using Android.Widget;

using MessageBox.Avalonia;

namespace WPR.Platform.Android
{
    public static class MessageBoxUtils
    {
        public static Activity MainActivity { get; set; }

        public static Task<MessageBox.Avalonia.Enums.ButtonResult> GetMessageDialogResult(string title,
            string text, MessageBox.Avalonia.Enums.ButtonEnum buttons = MessageBox.Avalonia.Enums.ButtonEnum.Ok,
            MessageBox.Avalonia.Enums.Icon icon = MessageBox.Avalonia.Enums.Icon.Info, IEnumerable<string> ?buttonTexts = null,
            bool modalOnWindow = true, bool dispatchMain = false)
        {
            TaskCompletionSource<MessageBox.Avalonia.Enums.ButtonResult> source = new TaskCompletionSource<MessageBox.Avalonia.Enums.ButtonResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            MainActivity.RunOnUiThread(() =>
            {
                AlertDialog.Builder builder = new AlertDialog.Builder(MainActivity)!
                    .SetTitle(title)!
                    .SetMessage(text)!;

                switch (buttons)
                {
                    case MessageBox.Avalonia.Enums.ButtonEnum.Ok:
                        if (buttonTexts != null)
                        {
                            var enumerable = buttonTexts.GetEnumerator();
                            enumerable.MoveNext();

                            builder = builder.SetPositiveButton(enumerable.Current, (dialog, which) =>
                            {
                                Common.Log.Error(Common.LogCategory.AppList, "OK reported");
                                source.SetResult(MessageBox.Avalonia.Enums.ButtonResult.Ok);
                                (dialog as AlertDialog)!.Dismiss();
                            })!;
                        }
                        else
                        {
                            builder = builder.SetPositiveButton(global::Android.Resource.String.Ok, (dialog, which) =>
                            {
                                Common.Log.Error(Common.LogCategory.AppList, "OK reported");
                                source.SetResult(MessageBox.Avalonia.Enums.ButtonResult.Ok);
                                (dialog as AlertDialog)!.Dismiss();
                            })!;
                        }

                        break;

                    case MessageBox.Avalonia.Enums.ButtonEnum.YesNo:
                        if (buttonTexts != null)
                        {
                            var enumerable = buttonTexts.GetEnumerator();
                            enumerable.MoveNext();

                            builder = builder.SetNegativeButton(enumerable.Current, (dialog, which) =>
                                {
                                    source.SetResult(MessageBox.Avalonia.Enums.ButtonResult.No);
                                    (dialog as AlertDialog)!.Dismiss();
                                })!;

                            enumerable.MoveNext();

                            builder = builder
                                .SetPositiveButton(enumerable.Current, (dialog, which) =>
                                {
                                    source.SetResult(MessageBox.Avalonia.Enums.ButtonResult.Yes);
                                    (dialog as AlertDialog)!.Dismiss();
                                })!;
                            
                        } else
                        {
                            builder = builder
                                .SetPositiveButton(global::Android.Resource.String.Yes, (dialog, which) =>
                                {
                                    source.SetResult(MessageBox.Avalonia.Enums.ButtonResult.Yes);
                                    (dialog as AlertDialog)!.Dismiss();
                                })!
                                .SetNegativeButton(global::Android.Resource.String.No, (dialog, which) =>
                                {
                                    source.SetResult(MessageBox.Avalonia.Enums.ButtonResult.No);
                                    (dialog as AlertDialog)!.Dismiss();
                                })!;
                        }
                        break;

                }

                switch (icon)
                {
                    case MessageBox.Avalonia.Enums.Icon.Warning:
                        builder = builder.SetIcon(global::Android.Resource.Drawable.IcDialogAlert)!;
                        break;

                    case MessageBox.Avalonia.Enums.Icon.Info:
                        builder = builder.SetIcon(global::Android.Resource.Drawable.IcDialogInfo)!;
                        break;

                    default:
                        break;
                }

                builder.Create()!.Show();
            });

            return source.Task;
        }

        /// <summary>
        /// Shows an error dialog whose body is a read-only, selectable, multi-line TextBox so
        /// the user can copy the text. A "Copy" button copies the full body to the clipboard.
        /// Resizable. Falls back to <see cref="GetMessageDialogResult"/> on Android.
        /// </summary>
        public static Task ShowSelectableErrorAsync(string title, string body)
        {
            return GetMessageDialogResult(
                title: title,
                text: body,
                buttons: MessageBox.Avalonia.Enums.ButtonEnum.Ok,
                icon: MessageBox.Avalonia.Enums.Icon.Error);
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
                        source.SetResult(editField.Text!);
                        (dialog as AlertDialog)!.Dismiss();
                    })!
                    .SetNegativeButton(global::Android.Resource.String.Cancel, (dialog, which) =>
                    {
                        source.SetResult(defaultText);
                        (dialog as AlertDialog)!.Dismiss();
                    })!;

                builder.Create()!.Show();
            });

            return source.Task;
        }
    }
}
