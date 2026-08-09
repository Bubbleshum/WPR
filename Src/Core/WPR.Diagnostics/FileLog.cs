using System;
using System.IO;
using System.Text;

namespace WPR.Diagnostics;

/// <summary>
/// A thread-safe append-to-file sink — the shape the per-game wpr_game_debug.log uses.
/// Existing ad-hoc log writers migrate onto this as their consumers are touched in
/// later stages.
/// </summary>
public sealed class FileLog : IWprLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLog(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        var sb = new StringBuilder()
            .Append(DateTime.UtcNow.ToString("O"))
            .Append(" [").Append(level).Append("] ")
            .Append(message);
        if (exception is not null)
            sb.Append(Environment.NewLine).Append(exception);

        var line = sb.Append(Environment.NewLine).ToString();
        lock (_gate)
            File.AppendAllText(_path, line);
    }
}
