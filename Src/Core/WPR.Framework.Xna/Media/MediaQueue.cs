#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using System;
using System.Collections.Generic;
#endregion

namespace Microsoft.Xna.Framework.Media
{
	/// <remarks>
	/// <para><b>Every access to the song list is synchronised, because WP7 titles drive
	/// <see cref="MediaPlayer"/> from worker threads.</b> XNA's <c>MediaPlayer</c> is a static over
	/// a device service, and games treat it as callable from anywhere — Carcassonne's
	/// <c>MusicPlayer.PlayNextSong</c> queues <c>MediaPlayer.Stop()</c> followed by
	/// <c>MediaPlayer.Play(...)</c> onto a <c>ThreadPool</c> work item, from several call sites, so
	/// two of them can be inside the queue at once. Upstream FNA synchronises none of it and this
	/// list is an ordinary <c>List&lt;Song&gt;</c>, so a concurrent <c>Clear</c> turned every
	/// check-then-act in <c>MediaPlayer</c> into an <c>ArgumentOutOfRangeException</c> — thrown on a
	/// thread-pool thread, therefore <b>unhandled and fatal</b>. Carcassonne died before its first
	/// frame.</para>
	///
	/// <para><b>Reads answer null rather than throwing when the index no longer addresses a
	/// song.</b> That is not merely defensive: <c>MediaPlayer.NextSong</c> already tests its result
	/// for null and could never observe it, because the indexer threw first — and on an empty queue
	/// its own <c>Clamp(..., Count - 1)</c> produces -1, so <c>MoveNext()</c> on an empty queue was
	/// a crash on ONE thread, no race required. The null path those callers already carry is now
	/// reachable.</para>
	///
	/// <para><b>The public surface is unchanged</b>, including the indexer, which still throws for a
	/// genuinely bad index — that is the caller's bug and XNA's contract. What changed is that it
	/// cannot observe a torn list. The atomic <c>FirstOrNull</c>/<c>AtOrNull</c>/<c>Snapshot</c>
	/// helpers below exist so <c>MediaPlayer</c> never has to do two calls where it means one.</para>
	///
	/// <para><b>No lock is held across a callback</b>, here or in <c>MediaPlayer</c>: every section
	/// below is a list read or write with nothing reentrant in it. Serialising <c>MediaPlayer</c>'s
	/// operations as a whole would mean holding a lock across <c>IMediaBackend.PlaySong</c> and the
	/// game's own <c>ActiveSongChanged</c> handler, which is the deadlock
	/// <c>KeyboardAccelerometerHost.OnTick</c> is careful to avoid. The residue is that two
	/// concurrent <c>Play</c> calls can still end on the wrong song — XNA promises no better — but
	/// neither can take the process down.</para>
	/// </remarks>
	public sealed class MediaQueue
	{
		#region Public Properties

		public Song ActiveSong
		{
			get
			{
				lock (gate)
				{
					// Upper bound included deliberately: ActiveSongIndex is publicly settable and
					// survives a Clear, so "index valid when written, list since emptied" is an
					// ordinary state rather than a misuse.
					if (activeSongIndex < 0 || activeSongIndex >= songs.Count)
					{
						return null;
					}

					return songs[activeSongIndex];
				}
			}
		}

		public int ActiveSongIndex
		{
			get { lock (gate) { return activeSongIndex; } }
			set { lock (gate) { activeSongIndex = value; } }
		}

		public int Count
		{
			get { lock (gate) { return songs.Count; } }
		}

		public Song this[int index]
		{
			get { lock (gate) { return songs[index]; } }
		}

		#endregion

		#region Private Variables

		private readonly List<Song> songs = new List<Song>();

		private readonly object gate = new object();

		private int activeSongIndex = -1;

		#endregion

		#region Internal Constructor

		internal MediaQueue()
		{
		}

		#endregion

		#region Internal Methods

		internal void Add(Song song)
		{
			lock (gate)
			{
				songs.Add(song);
			}
		}

		internal void Clear()
		{
			lock (gate)
			{
				songs.Clear();
			}
		}

		/// <summary>The first queued song, or null when the queue is empty.</summary>
		internal Song FirstOrNull()
		{
			lock (gate)
			{
				return songs.Count > 0 ? songs[0] : null;
			}
		}

		/// <summary>The song at <paramref name="index"/>, or null when it addresses nothing.</summary>
		internal Song AtOrNull(int index)
		{
			lock (gate)
			{
				return (index >= 0 && index < songs.Count) ? songs[index] : null;
			}
		}

		/// <summary>
		/// A copy of the queue, for callers that iterate it. Indexing in a <c>for</c> loop over a
		/// separately-read <c>Count</c> is the shape that throws here.
		/// </summary>
		internal Song[] Snapshot()
		{
			lock (gate)
			{
				return songs.ToArray();
			}
		}

		#endregion

	}
}
