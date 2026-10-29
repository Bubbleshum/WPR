using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace WPR.Common
{
    /// <summary>
    /// The Windows Phone OS version a title was built for: its <c>WMAppManifest.xml</c>
    /// <c>AppPlatformVersion</c> (7.0, 7.1, 8.0, 8.1).
    /// </summary>
    /// <remarks>
    /// <para>This is the phone's own app-compatibility switch. Windows Phone 8 ran a 7.x title
    /// in "quirks mode": for every API whose behaviour changed in 8.0, a title that targets 7.x
    /// got the 7.x behaviour, and a title that targets 8.0 the new one ("App platform
    /// compatibility for Windows Phone 8", MSDN jj206947). WPR does the same, through
    /// <see cref="WprHostEnvironment.CurrentTarget"/>.</para>
    ///
    /// <para><b>Branch on it only where Windows Phone itself behaved differently by target
    /// version.</b> It is not a switch for a WPR bug that shows up in one family of games, and
    /// never a per-game switch (WPR has none: see .claude/rules/no-per-game-rules.md).</para>
    ///
    /// <para>Plain ints and strings only, so every shim tier can read it (it lives in
    /// <c>WPR.Common</c>, which references nothing of WPR's).</para>
    /// </remarks>
    public readonly struct WindowsPhoneTarget : IEquatable<WindowsPhoneTarget>, IComparable<WindowsPhoneTarget>
    {
        public static readonly WindowsPhoneTarget Wp70 = new WindowsPhoneTarget(7, 0);
        public static readonly WindowsPhoneTarget Wp71 = new WindowsPhoneTarget(7, 1);
        public static readonly WindowsPhoneTarget Wp80 = new WindowsPhoneTarget(8, 0);
        public static readonly WindowsPhoneTarget Wp81 = new WindowsPhoneTarget(8, 1);

        private static readonly Regex ManifestAttribute = new Regex(
            @"AppPlatformVersion\s*=\s*[""']\s*([^""']*)[""']",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public WindowsPhoneTarget(int major, int minor)
        {
            Major = major;
            Minor = minor;
        }

        public int Major { get; }
        public int Minor { get; }

        /// <summary>Built for Windows Phone 8.0 or later: no 7.x quirks apply.</summary>
        public bool IsWp8OrLater => Major >= 8;

        /// <summary>Built for Windows Phone 7.0 or 7.1: the phone ran it with the 7.x behaviour.</summary>
        public bool IsWp7 => Major < 8;

        /// <summary>
        /// What a title gets when its manifest says nothing usable: 8.0 for a native ARM package,
        /// 7.1 for a managed one. The same answer <c>PackageManifests</c> writes into a
        /// synthesised manifest, so a title reads the same version before and after install.
        /// </summary>
        public static WindowsPhoneTarget Fallback(bool native) => native ? Wp80 : Wp71;

        /// <summary>
        /// Parses an <c>AppPlatformVersion</c> value ("7.1", "8.0", "7.1.0.0", " 8.0 "). Only
        /// Windows Phone majors (7 and 8) are accepted; anything else is false.
        /// </summary>
        public static bool TryParse(string? value, out WindowsPhoneTarget target)
        {
            target = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string[] parts = value!.Trim().Split('.');
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            {
                return false;
            }

            int minor = 0;
            if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
            {
                return false;
            }

            if (major != 7 && major != 8)
            {
                return false;
            }

            target = new WindowsPhoneTarget(major, minor);
            return true;
        }

        /// <summary>The declared version in a <c>WMAppManifest.xml</c>'s text, if it has a usable one.</summary>
        public static bool TryReadManifestText(string? manifestXml, out WindowsPhoneTarget target)
        {
            target = default;
            if (string.IsNullOrEmpty(manifestXml))
            {
                return false;
            }

            Match m = ManifestAttribute.Match(manifestXml);
            return m.Success && TryParse(m.Groups[1].Value, out target);
        }

        /// <summary>
        /// The declared version in an install folder's <c>WMAppManifest.xml</c>, if it has a
        /// usable one. Never throws.
        /// </summary>
        public static bool TryReadFromInstall(string? installFolder, out WindowsPhoneTarget target)
        {
            target = default;
            if (string.IsNullOrEmpty(installFolder))
            {
                return false;
            }

            try
            {
                string manifest = Path.Combine(installFolder, "WMAppManifest.xml");
                return File.Exists(manifest) && TryReadManifestText(File.ReadAllText(manifest), out target);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The version an installed title runs under: its manifest's, else <see cref="Fallback"/>.
        /// <paramref name="declared"/> says which.
        /// </summary>
        public static WindowsPhoneTarget ReadFromInstall(string? installFolder, bool native, out bool declared)
        {
            declared = TryReadFromInstall(installFolder, out WindowsPhoneTarget target);
            return declared ? target : Fallback(native);
        }

        /// <summary>WPR Hub's platform name: "Windows Phone 7", "Windows Phone 8", "Windows Phone 8.1".</summary>
        public string? HubPlatformName => (Major, Minor) switch
        {
            (7, _) => "Windows Phone 7",
            (8, 0) => "Windows Phone 8",
            (8, 1) => "Windows Phone 8.1",
            _ => null,
        };

        public override string ToString() => Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture);

        public bool Equals(WindowsPhoneTarget other) => Major == other.Major && Minor == other.Minor;
        public override bool Equals(object? obj) => obj is WindowsPhoneTarget t && Equals(t);
        public override int GetHashCode() => (Major * 397) ^ Minor;
        public int CompareTo(WindowsPhoneTarget other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

        public static bool operator ==(WindowsPhoneTarget a, WindowsPhoneTarget b) => a.Equals(b);
        public static bool operator !=(WindowsPhoneTarget a, WindowsPhoneTarget b) => !a.Equals(b);
        public static bool operator <(WindowsPhoneTarget a, WindowsPhoneTarget b) => a.CompareTo(b) < 0;
        public static bool operator >(WindowsPhoneTarget a, WindowsPhoneTarget b) => a.CompareTo(b) > 0;
        public static bool operator <=(WindowsPhoneTarget a, WindowsPhoneTarget b) => a.CompareTo(b) <= 0;
        public static bool operator >=(WindowsPhoneTarget a, WindowsPhoneTarget b) => a.CompareTo(b) >= 0;
    }
}
