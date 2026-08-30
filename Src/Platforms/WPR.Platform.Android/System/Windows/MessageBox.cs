// ""

namespace System.Windows;

internal class MessageBox
{
    internal static Func<string, string, string, Threading.Tasks.Task<string>> ShowSimpleImpl;
}