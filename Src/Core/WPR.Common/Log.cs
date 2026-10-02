using System;

namespace WPR.Common
{
    public static class Log
    {
        private static void Write(LogCategory category, String content)
        {
            Console.WriteLine($"[{category}] {content}");
            // stdout is discarded by the desktop WinExe; the session log is what a report sends.
            SessionLog.Write(category.ToString(), content);
        }

        public static void Error(LogCategory category, String content)
        {
            Write(category, content);
        }
        public static void Warn(LogCategory category, String content)
        {
            Write(category, content);
        }
        public static void Info(LogCategory category, String content)
        {
            Write(category, content);
        }
    }
}
