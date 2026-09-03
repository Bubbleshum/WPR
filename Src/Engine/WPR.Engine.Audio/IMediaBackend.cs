using System;

namespace WPR.Engine.Audio
{
	/// <summary>Chroma subsampling of a decoded video's YUV planes. WPR-owned mirror of Theora's
	/// <c>th_pixel_fmt</c> — the framework layer needs it only to size the U/V planes relative to Y,
	/// so the backend maps its decoder's format onto these three cases.</summary>
	public enum VideoPixelFormat
	{
		/// <summary>The decoder reported a format WPR does not know how to plane-split.</summary>
		Unknown = 0,
		/// <summary>4:2:0 — U/V are half width AND half height.</summary>
		Yuv420,
		/// <summary>4:2:2 — U/V are half width, full height.</summary>
		Yuv422,
		/// <summary>4:4:4 — U/V are full width and height.</summary>
		Yuv444,
	}

	/// <summary>
	/// The XNA <c>Media</c> subsystem's backend seam — Stage 5c-3c (Plans/STAGE5C-SCOPE.md). Two
	/// regions: <b>song playback</b> (backs <c>MediaPlayer</c>/<c>Song</c>) and <b>video decode</b>
	/// (backs <c>VideoPlayer</c>/<c>Video</c>).
	///
	/// <para><b>Why one interface for both.</b> The other seams split by native facility
	/// (<see cref="IAudioBackend"/> = FAudio SFX, <see cref="IXactBackend"/> = FACT), and under the
	/// FNA backend these two regions are indeed different libraries (FAudio's <c>XNA_*</c> song
	/// player vs. Theorafile). They are nonetheless one seam because they are one XNA subsystem with
	/// one lifetime: both are per-title file playback, both are registered and torn down together,
	/// and a non-FNA backend generally implements them with a single facility (on Windows,
	/// Media Foundation plays both). XACT is different — it is genuinely optional (most titles ship
	/// no banks) and has its own engine handle — which is why it kept a slot of its own.</para>
	///
	/// <para><b>Shape.</b> Same principle as the audio seam: primitives and WPR-owned enums, not the
	/// C ABI. <c>tf_*</c>'s <c>int</c> booleans become <see cref="bool"/>, <c>th_pixel_fmt</c> becomes
	/// <see cref="VideoPixelFormat"/>, and the decoder handle stays an opaque <see cref="IntPtr"/>
	/// that only the backend interprets. The YUV/audio sample buffers stay raw <see cref="IntPtr"/>s
	/// because <c>VideoPlayer</c> owns them (an <c>AllocHGlobal</c> plane buffer and a pinned float
	/// array) and hands the same pointer to the decoder every frame — copying them across the seam
	/// would be per-frame waste for no abstraction gain.</para>
	/// </summary>
	public interface IMediaBackend
	{
		// ---- Song playback (MediaPlayer / Song) ----

		/// <summary>Brings up the song player. Called lazily on the first <c>MediaPlayer</c> play, and
		/// paired with <see cref="SongQuit"/> in the host's teardown ladder (ADR Risk #1 — skipping it
		/// leaves the next launch's <c>MediaPlayer</c> believing it is already initialised).</summary>
		void SongInit();

		/// <summary>Tears the song player down and releases its device.</summary>
		void SongQuit();

		/// <summary>Starts streaming a song file from disk. Returns its duration in <b>seconds</b>.</summary>
		float PlaySong(string fileName);

		void PauseSong();
		void ResumeSong();
		void StopSong();

		/// <summary>Sets the song mix volume, 0..1. <c>MediaPlayer</c> passes 0 for muted.</summary>
		void SetSongVolume(float volume);

		/// <summary>True once the current song has run to its end. Polled once per
		/// <c>FrameworkDispatcher.Update</c>, which is how <c>MediaPlayer</c> advances its queue.</summary>
		bool GetSongEnded();

		/// <summary>Turns the visualization tap on or off. Off by default; enabling it costs the
		/// decoder an FFT per block, so <c>MediaPlayer.IsVisualizationEnabled</c> gates it.</summary>
		void EnableSongVisualization(bool enable);

		bool IsSongVisualizationEnabled();

		/// <summary>Fills the caller's frequency and sample buffers from the visualization tap.
		/// Both arrays are <c>count</c> long and owned by <c>VisualizationData</c>.</summary>
		void GetSongVisualizationData(float[] frequencies, float[] samples, int count);

		// ---- Video decode (VideoPlayer / Video) ----

		/// <summary>
		/// Opens a video file and returns an opaque decoder handle. Returns the handle even when the
		/// file could not be opened (the caller then sees no video and no audio stream) — this
		/// deliberately preserves the existing behaviour, where <c>Video</c>'s ctor ignores the open
		/// result and lets the subsequent info query decide.
		/// </summary>
		IntPtr OpenVideo(string fileName);

		/// <summary>Closes the decoder and frees its handle allocation. Zeroes the caller's handle.
		/// Reached from <c>~Video()</c>, so it must tolerate being called on the finalizer thread.</summary>
		void CloseVideo(ref IntPtr video);

		/// <summary>Reads the video stream's dimensions, frame rate and chroma format.</summary>
		void GetVideoInfo(IntPtr video, out int width, out int height, out double framesPerSecond, out VideoPixelFormat format);

		/// <summary>Reads the video's audio-track channel count and sample rate.</summary>
		void GetVideoAudioInfo(IntPtr video, out int channels, out int sampleRate);

		bool HasVideoStream(IntPtr video);
		bool HasAudioStream(IntPtr video);

		/// <summary>Selects one of a multi-track file's audio/video tracks. No-ops on single-track files.</summary>
		void SetAudioTrack(IntPtr video, int track);
		void SetVideoTrack(IntPtr video, int track);

		/// <summary>True once the decoder has run past the last packet.</summary>
		bool IsEndOfVideo(IntPtr video);

		/// <summary>Seeks the decoder back to the start. Used both to loop and to leave a stopped
		/// player in a replayable state.</summary>
		void ResetVideo(IntPtr video);

		/// <summary>Decodes up to <paramref name="frameCount"/> frames into the caller's YUV plane
		/// buffer (Y, then U, then V, tightly packed). Returns true when a new frame landed in the
		/// buffer — false means the decoder had nothing ready and the caller should keep the old frame.</summary>
		bool ReadVideoFrames(IntPtr video, IntPtr yuvBuffer, int frameCount);

		/// <summary>Decodes interleaved float audio into the caller's buffer. Returns the number of
		/// samples written (0 at end of stream).</summary>
		int ReadVideoAudio(IntPtr video, IntPtr buffer, int length);
	}
}
