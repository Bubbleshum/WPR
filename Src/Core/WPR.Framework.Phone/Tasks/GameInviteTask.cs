namespace Microsoft.Phone.Tasks
{
    /// <summary>
    /// Shim for the WP8 <c>Microsoft.Phone.Tasks.GameInviteTask</c>. On the phone it opened the
    /// Xbox LIVE screen for inviting friends to the multiplayer session named by
    /// <see cref="SessionId"/>, and raised <c>Completed</c> when the player left it. There is no
    /// such screen here, so <see cref="Show"/> answers at once with the result of a player who
    /// backed out of it.
    ///
    /// Asphalt 7: Heat builds one in its page constructor, so the type has to resolve before
    /// anything in the game can start.
    /// </summary>
    public sealed class GameInviteTask : ChooserBase<TaskEventArgs>
    {
        /// <summary>The multiplayer session the invitations are for.</summary>
        public string? SessionId { get; set; }

        /// <summary>Opens the invitation screen; here, answers that the player backed out.</summary>
        public override void Show()
        {
            RaiseCompleted(new TaskEventArgs(TaskResult.Cancel));
        }
    }
}
