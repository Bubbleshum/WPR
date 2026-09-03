using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WPR.Input.Keyboard;
using WPR.Platform.Windows.Input;

namespace WPR.Platform.Windows.Views
{
    /// <summary>
    /// Per-game key-to-touch binding editor.
    ///
    /// <para><b>Per game, because the data is.</b> A gesture's coordinates only mean anything
    /// against one title's layout, and <c>input-bindings.json</c> lives in that game's install
    /// folder and is removed with it. The global Controls page keeps the tilt keys and the Back
    /// key, which genuinely are game-independent.</para>
    ///
    /// <para>Edits are held in memory and written on Save, so Cancel really cancels — the file is
    /// not touched until then. A running game would not see the change anyway: bindings load in
    /// <c>PrepareForLaunch</c>, so they apply from the next launch.</para>
    /// </summary>
    public partial class GameControlsDialog : Window
    {
        private readonly ObservableCollection<KeyboardTouchBinding> _bindings = new();
        private string? _installFolder;

        private Point? _drawnStart;
        private Point? _drawnEnd;

        public GameControlsDialog()
        {
            InitializeComponent();

            var pad = this.Get<PhoneGesturePad>("pad");
            var keyBox = this.Get<ComboBox>("keyBox");
            var list = this.Get<ListBox>("bindingList");
            var addBtn = this.Get<Button>("addBtn");
            var removeBtn = this.Get<Button>("removeBtn");
            var landscape = this.Get<CheckBox>("landscapeCheck");

            // Same curated key list the tilt pickers use — names that spell identically in the
            // Avalonia and XNA key enums, so a binding resolves under either host.
            keyBox.ItemsSource = KeyboardKeyChoices.Common;
            list.ItemsSource = _bindings;

            landscape.IsCheckedChanged += (_, _) => pad.IsLandscape = landscape.IsChecked == true;

            pad.GestureDrawn += (_, e) =>
            {
                _drawnStart = e.Start;
                _drawnEnd = e.End;
                this.Get<TextBlock>("drawnText").Text = e.IsSwipe
                    ? $"swipe ({e.Start.X:F0},{e.Start.Y:F0}) -> ({e.End!.Value.X:F0},{e.End.Value.Y:F0})"
                    : $"tap ({e.Start.X:F0},{e.Start.Y:F0})";
                UpdateAddEnabled();
            };

            keyBox.SelectionChanged += (_, _) => UpdateAddEnabled();

            list.SelectionChanged += (_, _) =>
            {
                removeBtn.IsEnabled = list.SelectedItem is KeyboardTouchBinding;
                if (list.SelectedItem is KeyboardTouchBinding b)
                {
                    // Load the selected binding back onto the pad so it can be redrawn in place.
                    keyBox.SelectedItem = KeyboardKeyChoices.Common
                        .FirstOrDefault(c => string.Equals(c, b.Key, StringComparison.OrdinalIgnoreCase));
                    this.Get<NumericUpDown>("durationBox").Value = b.DurationMs;
                    bool swipe = b.Kind == KeyboardTouchGestureKind.Swipe;
                    pad.SetGesture(b.StartX, b.StartY, swipe ? b.EndX : (double?)null, swipe ? b.EndY : (double?)null);
                    _drawnStart = new Point(b.StartX, b.StartY);
                    _drawnEnd = swipe ? new Point(b.EndX, b.EndY) : (Point?)null;
                    UpdateAddEnabled();
                }
            };

            addBtn.Click += (_, _) =>
            {
                if (_drawnStart == null || keyBox.SelectedItem is not string key) return;

                // One binding per key: re-adding an existing key replaces it rather than creating a
                // second entry that could never fire (Resolve takes the first match).
                KeyboardTouchBinding? existing = _bindings
                    .FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase));
                if (existing != null) _bindings.Remove(existing);

                Point s = _drawnStart.Value;
                _bindings.Add(new KeyboardTouchBinding
                {
                    Key = key,
                    Kind = _drawnEnd != null ? KeyboardTouchGestureKind.Swipe : KeyboardTouchGestureKind.Tap,
                    StartX = (float)s.X,
                    StartY = (float)s.Y,
                    EndX = (float)(_drawnEnd?.X ?? s.X),
                    EndY = (float)(_drawnEnd?.Y ?? s.Y),
                    DurationMs = (int)(this.Get<NumericUpDown>("durationBox").Value ?? 120),
                });
            };

            removeBtn.Click += (_, _) =>
            {
                if (list.SelectedItem is KeyboardTouchBinding b) _bindings.Remove(b);
            };

            this.Get<Button>("cancelBtn").Click += (_, _) => Close();
            this.Get<Button>("saveBtn").Click += (_, _) => Save();
        }

        private void UpdateAddEnabled() =>
            this.Get<Button>("addBtn").IsEnabled =
                _drawnStart != null && this.Get<ComboBox>("keyBox").SelectedItem is string;

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>Opens the editor on one installed game.</summary>
        public void Load(string gameName, string installFolder)
        {
            _installFolder = installFolder;
            this.Get<TextBlock>("gameNameText").Text = gameName;

            _bindings.Clear();
            foreach (KeyboardTouchBinding b in KeyboardTouchBindingFile.Read(installFolder))
            {
                _bindings.Add(b);
            }
        }

        private void Save()
        {
            if (_installFolder == null) { Close(); return; }

            try
            {
                KeyboardTouchBindingFile.Write(_installFolder, _bindings.ToList());
                Close();
            }
            catch (Exception ex)
            {
                // Keep the dialog open so the edits are not lost to a failed write.
                this.Get<TextBlock>("statusText").Text = "Save failed: " + ex.Message;
            }
        }
    }
}
