using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Xna.Framework.GamerServices
{
    public static class GamerServicesDispatcher
    {

        public static event EventHandler<EventArgs> InstallingTitleUpdate;

        /// <summary>
        /// Drops subscribers left behind by an exited game. A field-like STATIC event keeps game
        /// handlers — and so the game's whole collectible AssemblyLoadContext — alive forever, because
        /// games do not unsubscribe before exiting. Called by the host during teardown.
        /// </summary>
        public static void ResetForNewLaunch()
        {
            InstallingTitleUpdate = null;
        }


        public static void Initialize(IServiceProvider serviceProvider)
        {
        }

        public static void Update()
        {
        }

        public static bool IsInitialized => true;

        public static IntPtr WindowHandle { get; set; }
    }
}
