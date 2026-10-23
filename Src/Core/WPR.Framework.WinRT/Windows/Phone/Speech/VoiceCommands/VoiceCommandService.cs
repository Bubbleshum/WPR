using System;
using Windows.Foundation;
using WPR.WinRT;

namespace Windows.Phone.Speech.VoiceCommands
{
    /// <summary>
    /// Shim for <c>Windows.Phone.Speech.VoiceCommands.VoiceCommandService</c>: registers an app's
    /// voice command definitions (a VCD file) with the phone's speech system.
    /// </summary>
    /// <remarks>
    /// WPR has no speech recognition, so nothing can ever speak a command; installing a command set
    /// completes as the phone's does on success. Apps install their VCD at start-up and await it
    /// (YouTube, What In The World?), so a missing member here was an unhandled MissingMethodException
    /// at launch.
    /// </remarks>
    public static class VoiceCommandService
    {
        public static IAsyncAction InstallCommandSetsFromFileAsync(Uri file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            return WinRtAsync.Completed();
        }
    }
}
