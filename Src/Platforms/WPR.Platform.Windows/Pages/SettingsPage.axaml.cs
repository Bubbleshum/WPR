using WPR.Shell;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using WPR.Common;

using ReactiveUI;
using System;
using System.IO;
using System.Linq;

using Microsoft.Xna.Framework.GamerServices;
using MessageBox.Avalonia;

namespace WPR.Platform.Windows.Pages
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();

            TextBox gamerTagTextBox = this.Get<TextBox>("gamerTagTextBox");
            if (Configuration.Current.GamerTag != null)
            {
                gamerTagTextBox.Text = Configuration.Current.GamerTag;
            }

            gamerTagTextBox.WhenAnyValue(x => x.Text).Subscribe(text =>
            {
                if (Gamer.SignedInGamers.Count != 0)
                {
                    Gamer.SignedInGamers[0].Gamertag = text;
                }

                Configuration.Current.GamerTag = text;
                Configuration.Current.Save();
            });

            WireGamerPicturePicker();
            WireHighlightColorPicker();

            TextBox pathTextBox = this.Get<TextBox>("dataStoragePathText");
            pathTextBox.Text = Configuration.Current.DataStorePath;

            Button pathChangeBtn = this.Get<Button>("dataStoragePathBrowse");
            pathChangeBtn.Click += async (obj, args) =>
            {
                string? resultFolder = await new OpenFolderDialog()
                {
                    Directory = Configuration.Current.DataStorePath
                }.ShowAsync(GetWindow());

                if (resultFolder != null)
                {
                    pathTextBox.Text = resultFolder;
                    Configuration.Current.DataStorePath = resultFolder;
                    Configuration.Current.Save();

                    var msgBox = MessageBoxManager.GetMessageBoxStandardWindow(
                        title: WPR.Shell.Resources.SuccessfullyChanged,
                        text: WPR.Shell.Resources.SuccessfullyChangedDataPathMsg,
                        icon: MessageBox.Avalonia.Enums.Icon.Success,
                        windowStartupLocation: WindowStartupLocation.CenterScreen);

                    await msgBox.ShowDialog(GetWindow());
                }
            };

            this.Get<Button>("restoreDefaultStoragePathBtn").Click += async (obj, args) =>
            {
                Configuration.Current.RestoreDefaultDataStoragePath();
                pathTextBox.Text = Configuration.Current.DataStorePath;

                Configuration.Current.Save();

                var msgBox = MessageBoxManager.GetMessageBoxStandardWindow(
                    title: WPR.Shell.Resources.SuccessfullyChanged,
                    text: WPR.Shell.Resources.SuccessfullyChangedDataPathMsg,
                    icon: MessageBox.Avalonia.Enums.Icon.Success,
                    windowStartupLocation: WindowStartupLocation.CenterScreen);

                await msgBox.ShowDialog(GetWindow());
            };

            TextBox libraryPathTextBox = this.Get<TextBox>("gameLibraryPathText");
            libraryPathTextBox.Text = Configuration.Current.GameLibraryPath ?? "";

            Button libraryPathBrowseBtn = this.Get<Button>("gameLibraryPathBrowse");
            libraryPathBrowseBtn.Click += async (obj, args) =>
            {
                string? resultFolder = await new OpenFolderDialog()
                {
                    Directory = Configuration.Current.GameLibraryPath ?? Configuration.Current.DataStorePath
                }.ShowAsync(GetWindow());

                if (resultFolder != null)
                {
                    libraryPathTextBox.Text = resultFolder;
                    Configuration.Current.GameLibraryPath = resultFolder;
                    Configuration.Current.Save();
                }
            };

            this.Get<Button>("clearGameLibraryPathBtn").Click += (obj, args) =>
            {
                libraryPathTextBox.Text = "";
                Configuration.Current.GameLibraryPath = null;
                Configuration.Current.Save();
            };
        }

        /// <summary>
        /// Populate the highlight-color combo with the WP7 accent palette,
        /// seed selection from <see cref="Configuration.AccentColor"/>, and on
        /// change persist the chosen hex back to configuration plus update the
        /// preview swatch. The actual phone theme reads this on next game launch
        /// (in <c>WPR.WindowsCompability.Application</c>'s ctor); we don't apply
        /// it live to running games because <c>PhoneTheme</c>-keyed brushes get
        /// captured into individual element <c>Style</c>s during XAML load.
        /// </summary>
        private void WireHighlightColorPicker()
        {
            var combo = this.Get<ComboBox>("highlightColorBox");
            var swatch = this.Get<Border>("highlightColorSwatch");
            combo.ItemsSource = WP7AccentColors.Presets;

            string? saved = Configuration.Current.AccentColor;
            WP7AccentColor initial = WP7AccentColors.Presets
                .FirstOrDefault(c => string.Equals(c.Hex, saved, StringComparison.OrdinalIgnoreCase))
                ?? WP7AccentColors.Default;
            combo.SelectedItem = initial;
            swatch.Background = initial.Brush;

            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not WP7AccentColor pick) return;
                Configuration.Current.AccentColor = pick.Hex;
                Configuration.Current.Save();
                swatch.Background = pick.Brush;
            };
        }

        /// <summary>
        /// Wires the gamer picture row: shows a preview of the currently-configured file
        /// (or a placeholder), lets the user Browse to a new image, and Clear to remove the
        /// selection. The chosen path is stored in <see cref="Configuration.GamerPicturePath"/>
        /// as an absolute path and consumed by
        /// <c>Microsoft.Xna.Framework.GamerServices.GamerProfile.GetGamerPicture()</c> at
        /// each game's profile-fetch time, so changes here take effect on the next game launch.
        /// </summary>
        private void WireGamerPicturePicker()
        {
            Image preview = this.Get<Image>("gamerPictureImage");
            TextBlock pathText = this.Get<TextBlock>("gamerPicturePathText");
            Button browseBtn = this.Get<Button>("gamerPictureBrowseBtn");
            Button clearBtn = this.Get<Button>("gamerPictureClearBtn");
            StackPanel defaultsPanel = this.Get<StackPanel>("gamerPictureDefaultsPanel");

            BuildDefaultsStrip(defaultsPanel, preview, pathText);
            RefreshGamerPictureUi(preview, pathText);

            browseBtn.Click += async (_, _) =>
            {
                var dlg = new OpenFileDialog
                {
                    AllowMultiple = false,
                    Filters = new System.Collections.Generic.List<FileDialogFilter>
                    {
                        new FileDialogFilter { Name = "Images", Extensions = { "png", "jpg", "jpeg", "bmp", "gif" } },
                        new FileDialogFilter { Name = "All files", Extensions = { "*" } }
                    }
                };
                string[]? picked = await dlg.ShowAsync(GetWindow());
                if (picked == null || picked.Length == 0) return;
                Configuration.Current.GamerPicturePath = picked[0];
                Configuration.Current.Save();
                RefreshGamerPictureUi(preview, pathText);
            };

            clearBtn.Click += (_, _) =>
            {
                Configuration.Current.GamerPicturePath = null;
                Configuration.Current.Save();
                RefreshGamerPictureUi(preview, pathText);
            };
        }

        /// <summary>
        /// Populate the horizontal thumbnail strip with one button per bundled default
        /// from <see cref="GamerPictureDefaults"/>. Clicking a thumbnail writes its
        /// <c>default:&lt;id&gt;</c> token to config and refreshes the preview.
        /// </summary>
        private static void BuildDefaultsStrip(StackPanel panel, Image preview, TextBlock pathText)
        {
            foreach (string id in GamerPictureDefaults.Ids)
            {
                Bitmap? thumb = null;
                using (Stream? s = GamerPictureDefaults.Open(id))
                {
                    if (s != null) thumb = new Bitmap(s);
                }
                if (thumb == null) continue;

                var img = new Image { Source = thumb, Stretch = Avalonia.Media.Stretch.UniformToFill, Width = 36, Height = 36 };
                var btn = new Button
                {
                    Padding = new Avalonia.Thickness(2),
                    Background = Avalonia.Media.Brushes.Transparent,
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7FFFFFFF")),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(3),
                    Content = img,
                    Tag = id
                };
                btn.Click += (_, _) =>
                {
                    Configuration.Current.GamerPicturePath = GamerPictureDefaults.ToConfigValue(id);
                    Configuration.Current.Save();
                    RefreshGamerPictureUi(preview, pathText);
                };
                panel.Children.Add(btn);
            }
        }

        private static void RefreshGamerPictureUi(Image preview, TextBlock pathText)
        {
            string? configured = Configuration.Current.GamerPicturePath;

            if (GamerPictureDefaults.IsDefault(configured))
            {
                string id = GamerPictureDefaults.ExtractId(configured)!;
                using Stream? s = GamerPictureDefaults.Open(id);
                if (s != null)
                {
                    preview.Source = new Bitmap(s);
                    pathText.Text = id;
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            {
                try
                {
                    preview.Source = new Bitmap(configured);
                    pathText.Text = configured;
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.Common, $"Settings: failed to load gamer-picture preview {configured}: {ex.Message}");
                }
            }

            preview.Source = null;
            pathText.Text = WPR.Shell.Resources.GamerPictureNone;
        }

        Window GetWindow() => VisualRoot as Window ?? throw new NullReferenceException("Invalid Owner");
    }
}
