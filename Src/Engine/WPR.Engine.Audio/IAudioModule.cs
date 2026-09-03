#nullable enable
namespace WPR.Engine.Audio

{
	/// <summary>
	/// One pluggable audio implementation — the unit a host or platform head registers with
	/// <see cref="AudioBackendRegistry"/>.
	///
	/// <para><b>Why a module rather than three separate registrations.</b> The audio subsystem is
	/// three seams (<see cref="IAudioBackend"/> sound effects, <see cref="IXactBackend"/> XACT,
	/// <see cref="IMediaBackend"/> songs + video) that a real implementation fills as a set: FAudio
	/// fills all three, Android's platform player fills only part of one. Registering the set as a
	/// named unit is what lets a head say "use the platform song player" without also having to know
	/// what fills the other two, and what lets the composition be logged as one line
	/// (<c>sound=FAudio xact=FAudio media=AndroidMediaPlayer</c>) when a game misbehaves.</para>
	///
	/// <para><b>Each factory takes the module below it in the stack.</b> That is the whole plugging
	/// mechanism: modules compose as a chain, later registrations layering over earlier ones.
	/// A module that does not implement a seam returns <paramref name="next"/> unchanged (which
	/// <see cref="AudioModule"/> does for you); a module that implements it fully ignores
	/// <paramref name="next"/>; and a module that implements only <em>part</em> of a seam keeps
	/// <paramref name="next"/> and delegates the rest to it. The third case is not hypothetical —
	/// it is exactly Android, which replaces song playback and forwards video to Theorafile — and
	/// handing the delegate in rather than letting the module <c>new</c> it is what keeps
	/// <c>WPR.Audio.AndroidMediaPlayer</c> free of any reference to <c>WPR.Audio.FAudio</c>.</para>
	///
	/// <para>Instances are created <b>per game launch</b>, because that is the lifetime the
	/// <see cref="XnaBackend"/> slots have — a backend registry must not outlive a run (ADR Risk #1).
	/// The module object itself is process-lifetime and must therefore hold no per-game state.</para>
	/// </summary>
	public interface IAudioModule
	{
		/// <summary>Short stable identifier, e.g. <c>FAudio</c>. Used to de-duplicate registrations
		/// (a head that re-runs its composition root replaces rather than accumulates) and to name
		/// the module in the composition log.</summary>
		string Name { get; }

		/// <summary>
		/// Whether this module can serve the current process. Checked once per composition, before
		/// any factory runs; a module that answers false is skipped entirely and the stack below it
		/// serves instead. For implementations whose availability is genuinely dynamic — a platform
		/// facility that may be absent on some devices — this is the place to say so, rather than
		/// throwing out of a factory.
		/// </summary>
		bool IsAvailable { get; }

		/// <summary>Builds this module's sound-effect backend, or returns <paramref name="next"/>
		/// (possibly null) to leave the seam to the module below.</summary>
		IAudioBackend? CreateAudio(IAudioBackend? next);

		/// <summary>Builds this module's XACT backend, or returns <paramref name="next"/>.</summary>
		IXactBackend? CreateXact(IXactBackend? next);

		/// <summary>Builds this module's media backend, or returns <paramref name="next"/>. A module
		/// covering only songs keeps <paramref name="next"/> and forwards the video half to it.</summary>
		IMediaBackend? CreateMedia(IMediaBackend? next);
	}

	/// <summary>
	/// Convenience base for <see cref="IAudioModule"/>: every seam defaults to "not mine", so a
	/// module overrides only the factories it actually implements. Prefer deriving from this over
	/// implementing the interface directly — a seam added to <see cref="IAudioModule"/> later then
	/// does not break existing modules.
	/// </summary>
	public abstract class AudioModule : IAudioModule
	{
		public abstract string Name { get; }

		public virtual bool IsAvailable => true;

		public virtual IAudioBackend? CreateAudio(IAudioBackend? next) => next;

		public virtual IXactBackend? CreateXact(IXactBackend? next) => next;

		public virtual IMediaBackend? CreateMedia(IMediaBackend? next) => next;

		public override string ToString() => Name;
	}
}
