using System;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using WPR.Platform.Windows.Input;
using WPR.Common;

namespace WPR.Platform.Windows.Pages
{
    /// <summary>
    /// Settings page that binds the four tilt-direction keys, the sensitivity, and the in-game
    /// overlay toggle to <see cref="Configuration"/>. A small live preview at the bottom hosts
    /// a <see cref="TiltOverlay"/> control so the user can verify their bindings work without
    /// launching a game — pressing a bound key on this page tilts the dial in real time.
    /// </summary>
    public partial class ControlsPage : UserControl
    {
        public ControlsPage()
        {
            InitializeComponent();

            var leftBox     = this.Get<ComboBox>("tiltLeftKeyBox");
            var rightBox    = this.Get<ComboBox>("tiltRightKeyBox");
            var forwardBox  = this.Get<ComboBox>("tiltForwardKeyBox");
            var backwardBox = this.Get<ComboBox>("tiltBackwardKeyBox");
            var slider      = this.Get<Slider>("tiltSensitivitySlider");
            var sliderVal   = this.Get<TextBlock>("tiltSensitivityValue");
            var overlayChk  = this.Get<CheckBox>("tiltOverlayCheck");
            var simChk      = this.Get<CheckBox>("tiltSimEnabledCheck");
            var resetBtn    = this.Get<Button>("resetTiltKeysBtn");
            var livePreview = this.Get<ContentPresenter>("livePreviewHost");

            foreach (var box in new[] { leftBox, rightBox, forwardBox, backwardBox })
            {
                box.ItemsSource = KeyboardTiltBinding.CommonChoices;
            }

            var cfg = Configuration.Current!;
            leftBox.SelectedItem     = cfg.TiltKeyLeft;
            rightBox.SelectedItem    = cfg.TiltKeyRight;
            forwardBox.SelectedItem  = cfg.TiltKeyForward;
            backwardBox.SelectedItem = cfg.TiltKeyBackward;
            slider.Value             = cfg.TiltSensitivity;
            sliderVal.Text           = cfg.TiltSensitivity.ToString("F2");
            overlayChk.IsChecked     = cfg.TiltOverlayEnabled;
            simChk.IsChecked         = cfg.TiltSimulationEnabled;

            leftBox.SelectionChanged     += (_, _) => Persist(() => cfg.TiltKeyLeft     = leftBox.SelectedItem     as string ?? cfg.TiltKeyLeft);
            rightBox.SelectionChanged    += (_, _) => Persist(() => cfg.TiltKeyRight    = rightBox.SelectedItem    as string ?? cfg.TiltKeyRight);
            forwardBox.SelectionChanged  += (_, _) => Persist(() => cfg.TiltKeyForward  = forwardBox.SelectedItem  as string ?? cfg.TiltKeyForward);
            backwardBox.SelectionChanged += (_, _) => Persist(() => cfg.TiltKeyBackward = backwardBox.SelectedItem as string ?? cfg.TiltKeyBackward);

            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                cfg.TiltSensitivity = slider.Value;
                sliderVal.Text = slider.Value.ToString("F2");
                cfg.Save();
                KeyboardTiltBinding.ApplyConfigurationToHost();
            };

            overlayChk.IsCheckedChanged += (_, _) =>
            {
                cfg.TiltOverlayEnabled = overlayChk.IsChecked == true;
                cfg.Save();
            };

            simChk.IsCheckedChanged += (_, _) =>
            {
                cfg.TiltSimulationEnabled = simChk.IsChecked == true;
                cfg.Save();
                KeyboardTiltBinding.ApplyConfigurationToHost();
            };

            resetBtn.Click += (_, _) =>
            {
                cfg.TiltKeyLeft     = Configuration.DefaultTiltKeyLeft;
                cfg.TiltKeyRight    = Configuration.DefaultTiltKeyRight;
                cfg.TiltKeyForward  = Configuration.DefaultTiltKeyForward;
                cfg.TiltKeyBackward = Configuration.DefaultTiltKeyBackward;
                cfg.TiltSensitivity = Configuration.DefaultTiltSensitivity;
                cfg.Save();
                leftBox.SelectedItem     = cfg.TiltKeyLeft;
                rightBox.SelectedItem    = cfg.TiltKeyRight;
                forwardBox.SelectedItem  = cfg.TiltKeyForward;
                backwardBox.SelectedItem = cfg.TiltKeyBackward;
                slider.Value             = cfg.TiltSensitivity;
                KeyboardTiltBinding.ApplyConfigurationToHost();
            };

            // Live preview: drop a TiltOverlay into the small panel on the right. It
            // subscribes to the simulator on attach, so the dial moves in response to
            // any bound key as long as the Controls page is focused.
            livePreview.Content = new TiltOverlay
            {
                IsHitTestVisible = false,
            };

            // Forward this page's key events to the simulator so the user can test
            // bindings without launching a game. Focusable lets us actually receive
            // the events when the page is shown.
            Focusable = true;
            AttachedToVisualTree += (_, _) =>
            {
                Focus();
                KeyboardTiltBinding.ApplyConfigurationToHost();
                var top = TopLevel.GetTopLevel(this);
                if (top != null)
                {
                    top.AddHandler(InputElement.KeyDownEvent, OnTiltKeyDown, RoutingStrategies.Tunnel);
                    top.AddHandler(InputElement.KeyUpEvent,   OnTiltKeyUp,   RoutingStrategies.Tunnel);
                }
            };
            DetachedFromVisualTree += (_, _) =>
            {
                var top = TopLevel.GetTopLevel(this);
                if (top != null)
                {
                    top.RemoveHandler(InputElement.KeyDownEvent, OnTiltKeyDown);
                    top.RemoveHandler(InputElement.KeyUpEvent,   OnTiltKeyUp);
                }
                KeyboardAccelerometerHost.ResetAll();
            };
        }

        private void OnTiltKeyDown(object? sender, KeyEventArgs e)
        {
            // OS key-repeat fires KeyDown repeatedly while the key is held — fine, since
            // NotifyTiltKey is an idempotent "set this flag to true" call.
            var dir = KeyboardTiltBinding.ResolveAvaloniaKey(e.Key);
            if (dir.HasValue) KeyboardAccelerometerHost.NotifyTiltKey(dir.Value, true);
        }

        private void OnTiltKeyUp(object? sender, KeyEventArgs e)
        {
            var dir = KeyboardTiltBinding.ResolveAvaloniaKey(e.Key);
            if (dir.HasValue) KeyboardAccelerometerHost.NotifyTiltKey(dir.Value, false);
        }

        private static void Persist(Action mutate)
        {
            mutate();
            Configuration.Current?.Save();
            KeyboardTiltBinding.ApplyConfigurationToHost();
        }
    }
}
