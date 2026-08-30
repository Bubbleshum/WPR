using System;
using System.Runtime.Serialization;

namespace Microsoft.Xna.Framework.GamerServices
{
    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.GamerServices.NetworkNotAvailableException</c>.
    ///
    /// Derives from <see cref="NetworkException"/> to match the upstream hierarchy: games
    /// routinely catch the base and expect this to be caught with it, so getting the base class
    /// wrong would silently change which handler runs. Kinectimals names it in
    /// FrontEnd.Leaderboard.GetLeaderboard/Update. See <see cref="NetworkException"/> for why an
    /// unresolvable catch type is fatal rather than inert.
    /// </summary>
    [Serializable]
    public class NetworkNotAvailableException : NetworkException
    {
        public NetworkNotAvailableException()
        {
        }

        public NetworkNotAvailableException(string message)
            : base(message)
        {
        }

        protected NetworkNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        public NetworkNotAvailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
