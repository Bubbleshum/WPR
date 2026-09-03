using System;

namespace WPR.Engine.Audio
{
	/// <summary>Which XACT object a destruction notification refers to.</summary>
	public enum XactNotificationKind
	{
		WaveBankDestroyed,
		SoundBankDestroyed,
		CueDestroyed,
	}

	/// <summary>XACT cue/soundbank/wavebank states, mirroring FACT's FACT_STATE_* flags.</summary>
	[Flags]
	public enum XactState
	{
		None = 0,
		Created = 1 << 0,
		Prepared = 1 << 1,
		Playing = 1 << 2,
		Stopping = 1 << 3,
		Stopped = 1 << 4,
		Paused = 1 << 5,
		InUse = 1 << 6,
		Preparing = 1 << 7,
	}

	/// <summary>How a cue/engine stop should behave.</summary>
	public enum XactStopOptions
	{
		Release,
		Immediate,
	}

	/// <summary>One audio renderer the XACT engine can target.</summary>
	public struct XactRendererInfo
	{
		public string DisplayName;
		public string RendererId;
	}

	/// <summary>
	/// The XACT (Cross-platform Audio Creation Tool) backend seam — Stage 5c-3b
	/// (Plans/STAGE5C-SCOPE.md). Backs the WPR-owned <c>AudioEngine</c>/<c>SoundBank</c>/
	/// <c>WaveBank</c>/<c>Cue</c>/<c>AudioCategory</c>, which are the XNA API over the .xgs/.xsb/.xwb
	/// projects WP7 games ship.
	///
	/// <para>Kept SEPARATE from <see cref="IAudioBackend"/> deliberately: XACT is a distinct native
	/// subsystem (FACT) with its own ~40-call surface, and most games use one or the other. Same design
	/// rule as the audio seam — primitives and opaque handles only, with every FACT struct, the 3D
	/// handle blob, and (critically) the NATIVE CALLBACK's delegate lifetime owned by the backend. That
	/// last point is the whole reason this was split out of 5c-3a: a function-pointer handed to native
	/// code must be kept alive by whoever created it, and that hazard belongs behind the seam.</para>
	/// </summary>
	public interface IXactBackend
	{
		// ---- Engine lifetime ----

		/// <summary>Creates the FACT engine; returns an opaque engine handle.</summary>
		IntPtr CreateEngine();

		/// <summary>
		/// Initialises the engine from a settings (.xgs) buffer and wires the destruction-notification
		/// callback. The backend builds the native runtime-parameters struct, owns the callback thunk
		/// for the engine's lifetime, decodes each notification and forwards it to
		/// <paramref name="onNotification"/> as (kind, native object pointer).
		/// </summary>
		/// <returns>False if the engine refused to initialise.</returns>
		bool InitializeEngine(
			IntPtr engine,
			IntPtr settingsBuffer,
			int settingsBufferLength,
			int lookAheadMilliseconds,
			string? rendererId,
			Action<XactNotificationKind, IntPtr> onNotification);

		void ShutDownEngine(IntPtr engine);
		void ReleaseEngine(IntPtr engine);
		void DoWork(IntPtr engine);

		/// <summary>Output channel count of the engine's final mix (drives 3D DSP settings).</summary>
		int GetFinalMixChannelCount(IntPtr engine);

		XactRendererInfo[] GetRenderers(IntPtr engine);

		void SetEngineVolume(IntPtr engine, IntPtr category, float volume);
		void PauseEngine(IntPtr engine, IntPtr category, bool pause);
		void StopEngine(IntPtr engine, IntPtr category, XactStopOptions options);

		// ---- Categories / global variables ----

		/// <summary>Category index, or -1 when the name is unknown.</summary>
		int GetCategoryIndex(IntPtr engine, string name);
		void SetCategoryVolume(IntPtr engine, int category, float volume);
		void PauseCategory(IntPtr engine, int category, bool pause);
		void StopCategory(IntPtr engine, int category, XactStopOptions options);

		/// <summary>Global variable index, or -1 when unknown.</summary>
		int GetGlobalVariableIndex(IntPtr engine, string name);
		float GetGlobalVariable(IntPtr engine, int index);
		void SetGlobalVariable(IntPtr engine, int index, float value);

		// ---- Banks ----

		IntPtr CreateSoundBank(IntPtr engine, IntPtr buffer, int bufferLength);
		void DestroySoundBank(IntPtr soundBank);
		XactState GetSoundBankState(IntPtr soundBank);

		IntPtr CreateInMemoryWaveBank(IntPtr engine, IntPtr buffer, int bufferLength);

		/// <summary>Creates a streaming wave bank straight off disk (the backend owns the file handle).</summary>
		IntPtr CreateStreamingWaveBank(IntPtr engine, string filePath, int offset, int packetSize, out IntPtr fileHandle);
		void CloseStreamingFile(IntPtr fileHandle);
		void DestroyWaveBank(IntPtr waveBank);
		XactState GetWaveBankState(IntPtr waveBank);

		// ---- Cues ----

		/// <summary>Cue index within a sound bank, or -1 when the name is unknown.</summary>
		int GetCueIndex(IntPtr soundBank, string name);
		IntPtr PrepareCue(IntPtr soundBank, int cueIndex);
		void PlayCueFireAndForget(IntPtr soundBank, int cueIndex);
		IntPtr PlayCue(IntPtr soundBank, int cueIndex);
		void DestroyCue(IntPtr cue);
		void PlayPreparedCue(IntPtr cue);
		void PauseCue(IntPtr cue, bool pause);
		void StopCue(IntPtr cue, XactStopOptions options);
		XactState GetCueState(IntPtr cue);

		/// <summary>Cue variable index, or -1 when unknown.</summary>
		int GetCueVariableIndex(IntPtr cue, string name);
		float GetCueVariable(IntPtr cue, int index);
		void SetCueVariable(IntPtr cue, int index, float value);

		// ---- 3D ----

		/// <summary>
		/// Runs the XACT 3D calculation and applies the result to <paramref name="cue"/>. Unlike the
		/// pure <c>Calculate3D</c> on <see cref="IAudioBackend"/>, XACT's own API couples calculate and
		/// apply (FACT3DCalculate + FACT3DApply), so this mirrors that.
		/// </summary>
		void Apply3DToCue(IntPtr engine, IntPtr cue, in Audio3DParams p);

		/// <summary>3D-positioned fire-and-forget play (FACTSoundBank_Play3D).</summary>
		void PlayCue3D(IntPtr engine, IntPtr soundBank, int cueIndex, in Audio3DParams p);

		// ---- Content loading ----
		// XACT banks are read wholesale into unmanaged memory that FACT then owns/reads. The file
		// access itself is a platform concern (it must honour the game's install layout), so it lives
		// behind the seam rather than in the framework types.

		IntPtr ReadFileToPointer(string path, out int length);
		void FreeFilePointer(IntPtr buffer);
	}
}
