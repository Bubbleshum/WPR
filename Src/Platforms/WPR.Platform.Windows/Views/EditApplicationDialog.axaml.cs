using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WPR.Models;

namespace WPR.Platform.Windows.Views
{
    /// <summary>
    /// Modal editor for the user-displayed metadata fields of an installed
    /// <see cref="Application"/> — Name, Description, Author, Publisher,
    /// Version. Returns the edited values on Save, or null on Cancel / close.
    /// </summary>
    public partial class EditApplicationDialog : Window
    {
        private TaskCompletionSource<EditApplicationResult?>? _Tcs;

        public EditApplicationDialog()
        {
            InitializeComponent();

            this.Get<Button>("saveButton").Click += (_, __) =>
            {
                _Tcs?.TrySetResult(new EditApplicationResult(
                    Name: this.Get<TextBox>("nameTextBox").Text ?? string.Empty,
                    Description: this.Get<TextBox>("descriptionTextBox").Text ?? string.Empty,
                    Author: this.Get<TextBox>("authorTextBox").Text ?? string.Empty,
                    Publisher: this.Get<TextBox>("publisherTextBox").Text ?? string.Empty,
                    Version: this.Get<TextBox>("versionTextBox").Text ?? string.Empty));
                Close();
            };

            this.Get<Button>("cancelButton").Click += (_, __) =>
            {
                _Tcs?.TrySetResult(null);
                Close();
            };

            // Closing via the X / Esc resolves the same as Cancel so the awaiter
            // never sees a hung Task — closure happens after the button handler
            // here too, but TrySetResult is a no-op the second time.
            this.Closed += (_, __) => _Tcs?.TrySetResult(null);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Pre-populate the inputs from an existing Application row. Called by
        /// the host before <see cref="ShowDialogAsync"/>.
        /// </summary>
        public void SetInitialValues(Application app)
        {
            this.Get<TextBox>("nameTextBox").Text = app.Name ?? string.Empty;
            this.Get<TextBox>("descriptionTextBox").Text = app.Description ?? string.Empty;
            this.Get<TextBox>("authorTextBox").Text = app.Author ?? string.Empty;
            this.Get<TextBox>("publisherTextBox").Text = app.Publisher ?? string.Empty;
            this.Get<TextBox>("versionTextBox").Text = app.Version ?? string.Empty;
        }

        public Task<EditApplicationResult?> ShowDialogAsync(Window owner)
        {
            _Tcs = new TaskCompletionSource<EditApplicationResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = ShowDialog(owner);
            return _Tcs.Task;
        }
    }

    public record EditApplicationResult(
        string Name,
        string Description,
        string Author,
        string Publisher,
        string Version);
}
