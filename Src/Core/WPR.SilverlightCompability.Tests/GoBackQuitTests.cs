using System;
using WPR.WindowsCompability;
using WPR.SilverlightCompability.Threading;
using Microsoft.Phone.Controls;
using WPR.Xna.Rhi;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>
    /// GoBack() off an app's only page, left unhandled, closes the app as the phone did: Fruit Ninja
    /// 2013 and Jetpack Joyride quit that way from a dispatcher post after their "Exit" box.
    /// </summary>
    [Collection("Dispatcher")]
    public class GoBackQuitTests
    {
        private static void RunPosted(Action posted, bool appHandles)
        {
            Application.ResetCurrent();
            Application app = Application.Current;
            app.UnhandledException += (s, e) => e.Handled = appHandles;
            Dispatcher.AttachToCurrentThread();
            try
            {
                Dispatcher.Shared.BeginInvoke(posted);
                Dispatcher.PumpQueued();
            }
            finally { Dispatcher.Detach(); }
        }

        [Fact]
        public void GoBackWithNoEntry_FromAPost_LeftUnhandled_Terminates()
        {
            try
            {
                RunPosted(() => new PhoneApplicationFrame().GoBack(), appHandles: false);
                Assert.True(GameUnhandledException.TerminationRequested);
            }
            finally { Application.ResetCurrent(); }
        }

        [Fact]
        public void GoBackWithNoEntry_Handled_KeepsRunning()
        {
            try
            {
                RunPosted(() => new PhoneApplicationFrame().GoBack(), appHandles: true);
                Assert.False(GameUnhandledException.TerminationRequested);
            }
            finally { Application.ResetCurrent(); }
        }

        [Fact]
        public void AnOrdinaryBclException_FromAPost_StillDoesNotTerminate()
        {
            try
            {
                RunPosted(() => throw new InvalidOperationException("a bug"), appHandles: false);
                Assert.False(GameUnhandledException.TerminationRequested);
            }
            finally { Application.ResetCurrent(); }
        }

        [Fact]
        public void GoBackWithNoEntry_ThrowsThePhonesType()
        {
            var thrown = Assert.Throws<InvalidOperationException>(() => new PhoneApplicationFrame().GoBack());
            Assert.True(Frame.IsGoBackWithNoEntry(thrown));
        }
    }
}
