using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WPR.Online.Hub.Client;

/// <summary>
/// Builds diagnostic reports and scrubs personal data from them before the
/// user sees the preview. The server scrubs again as a backstop, but the
/// point is that nothing personal leaves the machine in the first place.
/// </summary>
public static partial class Diagnostics
{
    public const int MaxLogTailChars = 60_000;

    /// <summary>Stable random ID for this install. Not derived from hardware.</summary>
    public static string GetOrCreateInstallId(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var id = File.ReadAllText(filePath).Trim();
                if (Guid.TryParse(id, out _)) return id;
            }
            var fresh = Guid.NewGuid().ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            File.WriteAllText(filePath, fresh);
            return fresh;
        }
        catch (IOException)
        {
            return Guid.NewGuid().ToString();
        }
    }

    public static DiagnosticReport FromException(
        Exception ex,
        string wprVersion,
        string? titleId = null,
        string? titleName = null,
        string? xapSha256 = null,
        long? xapSize = null,
        string? runtimePath = null,
        string? graphicsBackend = null,
        string? gpu = null,
        string? logTail = null,
        string? installId = null,
        bool serverLogging = false)
    {
        var root = ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerException! : ex;

        return new DiagnosticReport
        {
            Kind = "crash",
            InstallId = installId,
            WprVersion = wprVersion,
            Platform = OperatingSystem.IsAndroid() ? "android" : "windows",
            OsVersion = Environment.OSVersion.Version.ToString(),
            Arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            TitleId = titleId is null ? null : TitleIds.Normalize(titleId),
            TitleName = titleName,
            XapHash = xapSha256?.ToLowerInvariant(),
            XapSize = xapSize,
            RuntimePath = runtimePath,
            GraphicsBackend = graphicsBackend,
            Gpu = Scrub(gpu),
            ExceptionType = root.GetType().FullName,
            ExceptionMessage = Scrub(root.Message),
            StackTrace = Scrub(FullStackTrace(root)),
            LogTail = Scrub(Tail(logTail, MaxLogTailChars)),
            ServerLogging = serverLogging,
        };
    }

    /// <summary>Exactly what will be sent, for the consent dialog. Show this, not a summary.</summary>
    public static string Preview(DiagnosticReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions(HubJson.Options) { WriteIndented = true });

    /// <summary>Remove user names, home paths, emails and IPs.</summary>
    public static string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var s = text;

        // The actual user name and profile folder, wherever they appear.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && home.Length > 3)
            s = s.Replace(home, "<home>", StringComparison.OrdinalIgnoreCase);

        var user = Environment.UserName;
        if (!string.IsNullOrEmpty(user) && user.Length >= 3)
            s = Regex.Replace(s, $@"(?<=[\\/]){Regex.Escape(user)}(?=[\\/]|$)", "<user>", RegexOptions.IgnoreCase);

        var machine = Environment.MachineName;
        if (!string.IsNullOrEmpty(machine) && machine.Length >= 3)
            s = Regex.Replace(s, $@"\b{Regex.Escape(machine)}\b", "<host>", RegexOptions.IgnoreCase);

        s = WindowsProfile().Replace(s, "$1<user>");
        s = UnixHome().Replace(s, "$1<user>");
        s = Email().Replace(s, "<email>");
        s = IPv4().Replace(s, "<ip>");
        return s;
    }

    private static string FullStackTrace(Exception ex)
    {
        // Innermost first, which is where the bug usually is and what the
        // server fingerprints on.
        var chain = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException) chain.Add(e);
        chain.Reverse();

        var sb = new StringBuilder();
        for (var i = 0; i < chain.Count; i++)
        {
            if (i > 0) sb.AppendLine($"--- wrapped by {chain[i].GetType().FullName}: {chain[i].Message}");
            sb.AppendLine(chain[i].StackTrace);
        }
        return sb.ToString();
    }

    private static string? Tail(string? s, int max) => s is null || s.Length <= max ? s : s[^max..];

    [GeneratedRegex(@"([A-Za-z]:[\\/](?:Users|Documents and Settings)[\\/])[^\\/\r\n""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsProfile();

    [GeneratedRegex(@"(/home/|/Users/)[^/\s""']+")]
    private static partial Regex UnixHome();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex IPv4();
}
