using System.Collections.Generic;

namespace WPR.Engine.Notifications
{
    /// <summary>
    /// </summary>
    public class Notification
    {
        public Notification()
        {
            Buttons = new List<(string Title, string ActionId)>();
        }

        public string? Title { get; set; }

        public string? Body { get; set; }

        public string? ImagePath { get; set; }

        //RnD
        public string? BodyImagePath { get; set; }

        public string? BodyImageAltText { get; set; }

        /// <summary>
        /// Optional small attribution text rendered at the bottom of the toast, used for
        /// branding (e.g. "Windows Phone Reimplementation"). Platform-styled — Windows
        /// renders it in a smaller, dimmer font separated from the body.
        /// </summary>
        public string? AttributionText { get; set; }

        // NOTE: This only works on packaged app (Android or WinRT)
        // The sound name needs to be in resource folder
        public string? SoundUri { get; set; }

        public List<(string Title, string ActionId)> Buttons { get; }
    }
}