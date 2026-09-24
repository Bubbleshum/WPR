using System;
using System.Windows;
using System.Threading.Tasks;

namespace WPR.WindowsCompability
{
    public static class MessageBox
    {
        public static Func<string, string, MessageBoxButton, Task<MessageBoxResult>> ShowSimpleImpl;

        public static MessageBoxResult Show(string title, string caption, MessageBoxButton buttons)
        {
            return ShowSimpleImpl(title, caption, buttons).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Silverlight's one-argument overload: message only, an OK button, no caption.
        /// </summary>
        /// <remarks>
        /// A separate method rather than an optional parameter, because a default argument is
        /// baked into the CALLER at compile time — a game's IL names <c>Show(string)</c> and
        /// resolves against that exact signature, so an optional-parameter version would still
        /// be a MissingMethodException. Flight Control Rocket is the case that found this.
        /// </remarks>
        public static MessageBoxResult Show(string message)
        {
            return Show(message, string.Empty, MessageBoxButton.OK);
        }
    }

}
