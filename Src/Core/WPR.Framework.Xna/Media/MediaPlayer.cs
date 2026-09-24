#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using WPR.Engine.Audio;
using System;
using System.Diagnostics;
using System.IO;

using WPR.Xna.Rhi;
#endregion

namespace Microsoft.Xna.Framework.Media
{
	public static class MediaPlayer
	{
		#region Public Static Properties

		public static bool GameHasControl
		{
			get
			{
				/* This is based on whether or not the player is playing custom
				 * music, rather than yours.
				 * -flibit
				 */
				return true;
			}
		}

		public static bool IsMuted
		{
			get
			{
				return INTERNAL_isMuted;
			}

			set
			{
				INTERNAL_isMuted = value;
				AudioBackendRegistry.Media.SetSongVolume(
					INTERNAL_isMuted ?
						0.0f :
						INTERNAL_volume
				);
			}
		}

		public static bool IsRepeating
		{
			get;
			set;
		}

		public static bool IsShuffled
		{
			get;
			set;
		}

		public static TimeSpan PlayPosition
		{
			get
			{
				return timer.Elapsed;
			}
		}

		public static MediaQueue Queue 
		{
			get;
			private set;
		}

		public static MediaState State
		{
			get
			{
				return INTERNAL_state;
			}

			private set
			{
				if (INTERNAL_state != value)
				{
					INTERNAL_state = value;
					INTERNAL_mediaStateChanged = true;
				}
			}
		}

		public static float Volume
		{
			get
			{
				return INTERNAL_volume;
			}
			set
			{
				INTERNAL_volume = MathHelper.Clamp(
					value,
					0.0f,
					1.0f
				);
				AudioBackendRegistry.Media.SetSongVolume(
					IsMuted ? 0.0f : INTERNAL_volume
				);
			}
		}

		public static bool IsVisualizationEnabled
		{
			get
			{
				return AudioBackendRegistry.Media.IsSongVisualizationEnabled();
			}
			set
			{
				AudioBackendRegistry.Media.EnableSongVisualization(value);
			}
		}

		#endregion

		#region Public Static Variables

		public static event EventHandler<EventArgs> ActiveSongChanged;
		public static event EventHandler<EventArgs> MediaStateChanged;

		#endregion

		#region Private Static Variables

		/* 5c-3c: these two dirty flags used to live on FNA's FrameworkDispatcher, which set them
		 * to false after raising the corresponding event. They move HERE with MediaPlayer for the
		 * same reason DynamicSoundEffectInstance's stream list did in 5c-3a: the dispatcher stays
		 * in FNA (it also pumps TouchPanel, which has not moved), and moved code may not reference
		 * FNA — so the state inverts onto the owner and the dispatcher calls in via PumpUpdate().
		 * Named INTERNAL_* rather than the dispatcher's bare names because MediaPlayer already has
		 * PUBLIC EVENTS called ActiveSongChanged / MediaStateChanged. */
		private static bool INTERNAL_activeSongChanged = false;
		private static bool INTERNAL_mediaStateChanged = false;

		private static bool INTERNAL_isMuted = false;
		private static MediaState INTERNAL_state = MediaState.Stopped;
		private static float INTERNAL_volume = 1.0f;

		private static bool initialized = false;

		/* Need to hold onto this to keep track of how many songs
		 * have played when in shuffle mode.
		 */
		private static int numSongsInQueuePlayed = 0;

		/* FIXME: Ideally we'd be using the stream offset to track position,
		 * but usually you end up with a bit of stairstepping...
		 *
		 * For now, just use a timer. It's not 100% accurate, but it'll at
		 * least be consistent.
		 * -flibit
		 */
		private static Stopwatch timer = new Stopwatch();

		private static readonly Random random = new Random();

		#endregion

		#region Static Constructor

		static MediaPlayer()
		{
			Queue = new MediaQueue();
		}

		#endregion

		#region Public Static Methods

		public static void MoveNext()
		{
			NextSong(1);
		}

		public static void MovePrevious()
		{
			NextSong(-1);
		}

		public static void Pause()
		{
			if (State != MediaState.Playing || Queue.ActiveSong == null)
			{
				return;
			}

			lock (songGate)
			{
				AudioBackendRegistry.Media.PauseSong();
				timer.Stop();

				State = MediaState.Paused;
			}
		}

		/// <summary>
		/// The Play method clears the current playback queue and queues the specified song
		/// for playback. Playback starts immediately at the beginning of the song.
		/// </summary>
		public static void Play(Song song)
		{
			// One atomic read, not Count-then-index: a worker thread calling Stop()/Play() on the
			// same queue clears it between the two, and this method is reached from a ThreadPool
			// work item in real titles. See the remarks on MediaQueue.
			Song previousSong = Queue.FirstOrNull();

			Queue.Clear();
			numSongsInQueuePlayed = 0;
			LoadSong(song);
			Queue.ActiveSongIndex = 0;

			PlaySong(song);

			if (previousSong != song)
			{
				INTERNAL_activeSongChanged = true;
			}
		}

		public static void Play(SongCollection songs)
		{
			Play(songs, 0);
		}

		public static void Play(SongCollection songs, int index)
		{
			Queue.Clear();
			numSongsInQueuePlayed = 0;

			foreach (Song song in songs)
			{
				LoadSong(song);
			}

			Queue.ActiveSongIndex = index;

			PlaySong(Queue.ActiveSong);
		}

		public static void Resume()
		{
			if (State != MediaState.Paused)
			{
				return;
			}

			lock (songGate)
			{
				AudioBackendRegistry.Media.ResumeSong();
				timer.Start();
				State = MediaState.Playing;
			}
		}

		public static void Stop()
		{
			if (State == MediaState.Stopped)
			{
				return;
			}

			lock (songGate)
			{
				AudioBackendRegistry.Media.StopSong();
				timer.Stop();
				timer.Reset();
			}

			foreach (Song queued in Queue.Snapshot())
			{
				queued.PlayCount = 0;
			}

			State = MediaState.Stopped;
		}

		public static void GetVisualizationData(VisualizationData data)
		{
			AudioBackendRegistry.Media.GetSongVisualizationData(
				data.freq,
				data.samp,
				VisualizationData.Size
			);
		}

		#endregion

		#region Internal Static Methods

		internal static void Update()
		{
			if (	Queue == null ||
				Queue.ActiveSong == null ||
				State != MediaState.Playing ||
				!AudioBackendRegistry.Media.GetSongEnded()	)
			{
				// Nothing to do... yet...
				return;
			}

			numSongsInQueuePlayed += 1;

			if (numSongsInQueuePlayed >= Queue.Count)
			{
				numSongsInQueuePlayed = 0;
				if (!IsRepeating)
				{
					Stop();

					INTERNAL_activeSongChanged = true;

					return;
				}
			}

			MoveNext();
		}

		/// <summary>
		/// One tick of the media pump, called by FNA's <c>FrameworkDispatcher.Update</c> (reachable
		/// through this assembly's <c>InternalsVisibleTo(FNA)</c>). Advances the queue, then raises
		/// whichever events were flagged since the last tick — the exact sequence the dispatcher used
		/// to run inline, kept in one place now that the flags live here.
		/// </summary>
		internal static void PumpUpdate()
		{
			Update();
			if (INTERNAL_activeSongChanged)
			{
				OnActiveSongChanged();
				INTERNAL_activeSongChanged = false;
			}
			if (INTERNAL_mediaStateChanged)
			{
				OnMediaStateChanged();
				INTERNAL_mediaStateChanged = false;
			}
		}

		internal static void DisposeIfNecessary()
		{
			if (initialized)
			{
				AudioBackendRegistry.Media.SongQuit();
				initialized = false;
			}
		}

		internal static void OnActiveSongChanged()
		{
			if (ActiveSongChanged != null)
			{
				ActiveSongChanged(null, EventArgs.Empty);
			}
		}

		internal static void OnMediaStateChanged()
		{
			if (MediaStateChanged != null)
			{
				MediaStateChanged(null, EventArgs.Empty);
			}
		}

		#endregion

		#region Private Static Methods

		private static void LoadSong(Song song)
		{
			/* Believe it or not, XNA duplicates the Song object
			 * and then assigns a bunch of stuff to it at Play time.
			 * -flibit
			 */
			Queue.Add(new Song(song.handle, song.Name));
		}

		private static void NextSong(int direction)
		{
			Stop();
			if (IsRepeating && Queue.ActiveSongIndex >= Queue.Count - 1)
			{
				Queue.ActiveSongIndex = 0;

				/* Setting direction to 0 will force the first song
				 * in the queue to be played.
				 * if we're on "shuffle", then it'll pick a random one
				 * anyway, regardless of the "direction".
				 */
				direction = 0;
			}

			if (IsShuffled)
			{
				Queue.ActiveSongIndex = random.Next(Queue.Count);
			}
			else
			{
				Queue.ActiveSongIndex = (int) MathHelper.Clamp(
					Queue.ActiveSongIndex + direction,
					0,
					Queue.Count - 1
				);
			}

			// AtOrNull, so the null branch below is actually reachable. On an empty queue the
			// Clamp above yields -1 and the shuffle branch yields random.Next(0) == 0, so the
			// plain indexer threw here on a single thread — MoveNext() with nothing queued.
			Song nextSong = Queue.AtOrNull(Queue.ActiveSongIndex);
			if (nextSong != null)
			{
				PlaySong(nextSong);
			}

			INTERNAL_activeSongChanged = true;
		}

		private static bool IsSupportedSongPath(string path)
		{
			/* The song backend decodes Ogg Vorbis only (FNA's is stb_vorbis). WP7 titles
			 * ship .wma; AudioCompabilityConverter transcodes those to Ogg at install
			 * time but writes the result back under the original .wma filename (the
			 * .xnb/SongReader path is unchanged), so the extension lies. Sniff the
			 * actual container ("OggS" magic) rather than trusting the extension. */
			string ext = Path.GetExtension(path);
			if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			try
			{
				using (FileStream fs = File.OpenRead(path))
				{
					byte[] magic = new byte[4];
					return fs.Read(magic, 0, 4) == 4
						&& magic[0] == (byte) 'O'
						&& magic[1] == (byte) 'g'
						&& magic[2] == (byte) 'g'
						&& magic[3] == (byte) 'S';
				}
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Serialises every call into the media backend's SONG api.
		/// </summary>
		/// <remarks>
		/// <para>FAudio's song player is a set of file-static variables — <c>songAudio</c>,
		/// <c>songMaster</c> and a single decode cache in <c>XNA_Song.c</c> — with no locking of its
		/// own. Two threads inside <c>XNA_PlaySong</c> corrupt it and the process dies with an
		/// <c>AccessViolationException</c> that no managed frame can catch.</para>
		///
		/// <para>That is reachable from ordinary game code: WP7 titles start music from worker
		/// threads, and Carcassonne queues a <c>MediaPlayer.Stop()</c>+<c>Play()</c> pair onto a
		/// <c>ThreadPool</c> work item from several call sites at once.</para>
		///
		/// <para><b>Nothing raises a game event under this lock.</b> The <c>State</c> setter only
		/// flags <c>INTERNAL_mediaStateChanged</c>; the events themselves are raised from
		/// <c>PumpUpdate</c> on the game thread, outside it. Keep it that way — a game's
		/// <c>MediaStateChanged</c> handler commonly calls straight back into <c>Play</c>.</para>
		/// </remarks>
		private static readonly object songGate = new object();

		private static void PlaySong(Song song)
		{
			if (song == null)
			{
				// Reachable now that the queue answers null for an index that addresses nothing
				// (Play(SongCollection, index) hands us Queue.ActiveSong directly). Nothing to
				// start, and the previous behaviour here was an NRE.
				return;
			}

			lock (songGate)
			{
			if (!initialized)
			{
				/* Double-checked under the SAME gate SoundEffect.Device() uses. Both sides end in
				 * FAudioCreate (XNA_SongInit calls it into its own file-static), and concurrent
				 * SDL audio init is an AccessViolationException on a thread with no managed
				 * handler — process gone, no [wpr-fce], nothing in the log.
				 *
				 * A plain bool could not have held: PlaySong is reached from ThreadPool work items
				 * in real titles (Carcassonne queues Stop()+Play() from several call sites), so two
				 * workers both read false and both initialised.
				 */
				lock (Audio.SoundEffect.createLock)
				{
					if (!initialized)
					{
						AudioBackendRegistry.Media.SongInit();
						initialized = true;
					}
				}
			}

			if (!IsSupportedSongPath(song.handle))
			{
				Debug.WriteLine(
					"[WPR] Skipping unsupported MediaPlayer song: " + song.handle
				);
				song.Duration = TimeSpan.Zero;
				timer.Reset();
				State = MediaState.Playing;
				return;
			}

			song.Duration = TimeSpan.FromSeconds(AudioBackendRegistry.Media.PlaySong(song.handle));
			timer.Start();
			State = MediaState.Playing;
			}
		}

		#endregion

	}
}
