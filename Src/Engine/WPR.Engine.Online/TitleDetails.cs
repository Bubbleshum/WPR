using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>
    /// What an installed package says about its game, offered to WPR Hub so its title pages fill in
    /// without an admin typing them: the name, publisher, developer, platform and the icon.
    /// </summary>
    /// <param name="TitleId">The product id, braced or not.</param>
    /// <param name="IconFile">An absolute path to the installed icon, read when the offer is sent.</param>
    public sealed record TitleDetails(
        string TitleId,
        string? Name,
        string? Publisher,
        string? Developer,
        string? Platform,
        string? IconFile)
    {
        private static readonly Regex PlatformVersion = new(@"AppPlatformVersion\s*=\s*""(\d+)\.(\d+)", RegexOptions.CultureInvariant);

        /// <summary>
        /// The hub's platform name for an install folder, from its <c>WMAppManifest.xml</c>'s
        /// <c>AppPlatformVersion</c> ("7.1" is Windows Phone 7, "8.0" Windows Phone 8). Null when
        /// the manifest is missing or says nothing usable. Never throws.
        /// </summary>
        public static string? ReadPlatform(string? installFolder)
        {
            if (string.IsNullOrEmpty(installFolder)) return null;
            try
            {
                string manifest = Path.Combine(installFolder, "WMAppManifest.xml");
                if (!File.Exists(manifest)) return null;
                Match m = PlatformVersion.Match(File.ReadAllText(manifest));
                if (!m.Success) return null;
                return (m.Groups[1].Value, m.Groups[2].Value) switch
                {
                    ("7", _) => "Windows Phone 7",
                    ("8", "0") => "Windows Phone 8",
                    ("8", "1") => "Windows Phone 8.1",
                    _ => null,
                };
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Offers installed games' details to the hub, which keeps only what it does not have yet.
    /// </summary>
    /// <remarks>
    /// <para>The install only queues (<see cref="Offer"/>); <see cref="OnlineBackend.FlushAsync"/>
    /// sends, so an install never waits on, or fails over, the network. Same consent as an install
    /// receipt: nothing is queued or sent with server logging off.</para>
    /// </remarks>
    public interface ITitleDetailsSync
    {
        /// <summary>Queue one game's details. Cheap, never throws, never touches the network.</summary>
        void Offer(TitleDetails details);

        /// <summary>Send what is queued, plus installed games never offered (a backfill). Never throws.</summary>
        Task FlushAsync();
    }
}
