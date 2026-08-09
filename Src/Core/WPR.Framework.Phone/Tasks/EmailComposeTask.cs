namespace Microsoft.Phone.Tasks
{
    public class EmailComposeTask
    {
        public EmailComposeTask()
        {
        }

        public string? To { get; set; }
        public string? Cc { get; set; }
        public string? Bcc { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }

        public void Show()
        {
        }
    }
}
