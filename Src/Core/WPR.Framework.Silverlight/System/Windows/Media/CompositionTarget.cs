using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.CompositionTarget</c>. <see cref="Rendering"/> is raised
    /// once a frame by the Silverlight host (<c>SilverlightGame.Update</c>), after the storyboard
    /// clock, as Silverlight raised it before each composition. Arkadium's engine (Microsoft
    /// Mahjong, Minesweeper) is ticked from nothing else: its <c>EngineThread.RequestUpdate</c>
    /// is called only from a Rendering handler, so without it the game page draws nothing.
    /// </summary>
    public static class CompositionTarget
    {
        public static event EventHandler? Rendering;

        /// <summary>Raises <see cref="Rendering"/> with the time since the host started.</summary>
        public static void RaiseRendering(TimeSpan renderingTime)
        {
            EventHandler? handlers = Rendering;
            if (handlers is null) return;
            var args = new RenderingEventArgs(renderingTime);
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                // One faulty subscriber must not stop the others (an engine tick behind a
                // throwing animation helper), as each handler is its own frame callback.
                try { handler(null, args); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[wpr-mixed] CompositionTarget.Rendering handler threw: {ex}"); }
            }
        }

        /// <summary>
        /// Drops subscribers left behind by an exited game. A STATIC event that game code subscribes to
        /// keeps the subscriber (and therefore the game's whole collectible AssemblyLoadContext) alive
        /// forever, so the host clears it during teardown — games do not unsubscribe before exiting.
        /// </summary>
        public static void ResetForNewLaunch()
        {
            Rendering = null;
        }
    }
}
