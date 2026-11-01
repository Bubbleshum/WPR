using Microsoft.Phone.Shell;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>
    /// An app that ends itself (Application.Terminate()) gets no Deactivated or Closing, as on the
    /// phone; one the player leaves gets both. GTA: San Andreas froze on quitting because its
    /// Closing handler called into a native component that was still inside the quit.
    /// </summary>
    public class TerminateLifecycleTests
    {
        private static (int Deactivated, int Closing) Exit(bool terminated)
        {
            var service = new PhoneApplicationService();
            int deactivated = 0, closing = 0;
            service.Deactivated += (s, e) => deactivated++;
            service.Closing += (s, e) => closing++;
            service.HandleApplicationExit(terminated);
            return (deactivated, closing);
        }

        [Fact]
        public void LeftByThePlayer_RaisesDeactivatedAndClosing()
        {
            Assert.Equal((1, 1), Exit(terminated: false));
        }

        [Fact]
        public void EndedByTheApp_RaisesNeither()
        {
            Assert.Equal((0, 0), Exit(terminated: true));
        }
    }
}
