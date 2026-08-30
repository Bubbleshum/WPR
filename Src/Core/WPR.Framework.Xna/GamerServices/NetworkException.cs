using System;
using System.Runtime.Serialization;

namespace Microsoft.Xna.Framework.GamerServices
{
    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.GamerServices.NetworkException</c>.
    ///
    /// WPR never actually reaches Xbox LIVE, so this is never thrown by the shims — but games
    /// name it in <c>catch</c> clauses around their LIVE calls, and the JIT resolves a handler's
    /// catch type when it compiles the method. A missing type therefore throws TypeLoadException
    /// on entry to an otherwise-harmless method. Star Wars: The Battle for Hoth hits this in five
    /// CXBoxLiveManager methods (GamerProfileCallback, GetLeaderboardReader,
    /// LeaderboardReaderCallback, PageDownCallback, PageUpCallback), which is why its menus
    /// accept no input.
    /// </summary>
    [Serializable]
    public class NetworkException : Exception
    {
        public NetworkException()
        {
        }

        public NetworkException(string message)
            : base(message)
        {
        }

        protected NetworkException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        public NetworkException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
