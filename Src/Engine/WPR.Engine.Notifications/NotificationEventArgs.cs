namespace WPR.Engine.Notifications
{
    /// <summary>
    /// </summary>
    public class NotificationEventArgs
    {
        public NotificationEventArgs(Notification notification)
        {
            Notification = notification;
        }

        /// <summary>
        /// </summary>
        public Notification Notification { get; }
    }
}