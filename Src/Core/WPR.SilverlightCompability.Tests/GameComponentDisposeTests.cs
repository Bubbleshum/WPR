using Microsoft.Xna.Framework;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// A component whose own Dispose reaches its Dispose() again must not recurse: the Microsoft Ad
/// SDK's AdGameComponent does exactly that, and Game.Dispose then overflowed the stack (iRunner).
/// </summary>
public class GameComponentDisposeTests
{
    private sealed class SelfDisposing : GameComponent
    {
        public int Calls;

        public SelfDisposing() : base(null) { }

        protected override void Dispose(bool disposing)
        {
            Calls++;
            if (disposing)
            {
                Dispose();
            }

            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Dispose_reentered_from_its_own_Dispose_returns()
    {
        var component = new SelfDisposing();
        int raised = 0;
        component.Disposed += (_, _) => raised++;
        component.Dispose();
        Assert.Equal(1, component.Calls);
        Assert.Equal(1, raised);
    }
}
