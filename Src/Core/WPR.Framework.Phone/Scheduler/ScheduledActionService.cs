using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Phone.Scheduler
{
    public class ScheduledActionService
    {
        private static Dictionary<string, ScheduledAction> Actions;

        static ScheduledActionService()
        {
            Actions = new Dictionary<string, ScheduledAction>();
        }

        public static ScheduledAction? Find(string name)
        {
            return Actions.ContainsKey(name) ? Actions[name] : null;
        }

        public static void Add(ScheduledAction action)
        {
            if (Actions.ContainsKey(action.Name!))
            {
                throw new ArgumentException($"The task with the name: {action.Name} has already been scheduled!");
            }

            Actions.Add(action.Name!, action);
        }

        /// <summary>
        /// Unschedule a named action. Real WP7 throws
        /// <see cref="InvalidOperationException"/> when the name isn't scheduled, but games
        /// routinely call Remove unguarded to clear a stale agent before re-adding it —
        /// Kinectimals does exactly that in Main.MainGame.RemoveAgent — so removing a name that
        /// was never added is treated as success. Throwing here would turn a defensive cleanup
        /// call into a crash.
        /// </summary>
        public static void Remove(string name)
        {
            Actions.Remove(name);
        }
    }
}
