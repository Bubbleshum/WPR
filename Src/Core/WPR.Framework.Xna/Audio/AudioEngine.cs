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
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Audio
{
	// http://msdn.microsoft.com/en-us/library/dd940262.aspx
	public class AudioEngine : IDisposable
	{
		#region Public Constants

		public const int ContentVersion = 46;

		#endregion

		#region Public Properties

		public ReadOnlyCollection<RendererDetail> RendererDetails
		{
			get
			{
				return new ReadOnlyCollection<RendererDetail>(
					rendererDetails
				);
			}
		}

		public bool IsDisposed
		{
			get;
			private set;
		}

		#endregion

		#region Internal Variables

		internal readonly IntPtr handle;
		/* WPR 5c-3b: the 3D handle blob, the notification callback and its description all moved
		 * into the backend, which owns the FACT structs and the callback's delegate lifetime. */
		internal readonly int channels;

		/// <summary>FACT_ENGINE_LOOKAHEAD_DEFAULT — XACT\'s default engine look-ahead, in ms.</summary>
		private const int DefaultLookAheadMilliseconds = 250;

		internal readonly object gcSync = new object();

		#endregion

		#region Private Variables

		private RendererDetail[] rendererDetails;


		private class IntPtrComparer : IEqualityComparer<IntPtr>
		{
			public bool Equals(IntPtr x, IntPtr y)
			{
				return x == y;
			}

			public int GetHashCode(IntPtr obj)
			{
				return obj.GetHashCode();
			}
		}

		private static readonly IntPtrComparer comparer = new IntPtrComparer();

		// If this isn't static, destructors gets confused like idiots
		private static readonly Dictionary<IntPtr, WeakReference> xactPtrs = new Dictionary<IntPtr, WeakReference>(comparer);

		#endregion

		#region Public Static Variables

		// STOP LEAKING YOUR XACT DATA, GOOD GRIEF PEOPLE
		internal static bool ProgramExiting = false;

		#endregion

		#region Disposing Event

		public event EventHandler<EventArgs> Disposing;

		#endregion

		#region Public Constructors

		public AudioEngine(
			string settingsFile
		) : this(
			settingsFile,
			new TimeSpan(
				0, 0, 0, 0,
				DefaultLookAheadMilliseconds
			),
			null
		) {
		}

		public AudioEngine(
			string settingsFile,
			TimeSpan lookAheadTime,
			string rendererId
		) {
			if (String.IsNullOrEmpty(settingsFile))
			{
				throw new ArgumentNullException("settingsFile");
			}

			// Allocate (but don't initialize just yet!)
			handle = XnaBackend.Xact.CreateEngine();

			// Grab RendererDetails
			XactRendererInfo[] renderers = XnaBackend.Xact.GetRenderers(handle);
			if (renderers.Length == 0)
			{
				XnaBackend.Xact.ReleaseEngine(handle);
				throw new NoAudioHardwareException();
			}
			rendererDetails = new RendererDetail[renderers.Length];
			for (int i = 0; i < renderers.Length; i += 1)
			{
				rendererDetails[i] = new RendererDetail(
					renderers[i].DisplayName,
					renderers[i].RendererId
				);
			}

			// Read entire file into memory, let FACT manage the pointer
			IntPtr buffer = XnaBackend.Xact.ReadFileToPointer(settingsFile, out int bufferLength);

			/* Engine parameters, the notification callback (and its lifetime), 3D init, the final-mix
			 * format query and the three destruction-notification registrations all happen inside the
			 * backend now — they are pure FACT struct/callback plumbing.
			 */
			if (!XnaBackend.Xact.InitializeEngine(
					handle,
					buffer,
					bufferLength,
					lookAheadTime.Milliseconds,
					rendererId,
					OnXactNotification))
			{
				throw new InvalidOperationException(
					"Engine initialization failed!"
				);
			}

			channels = XnaBackend.Xact.GetFinalMixChannelCount(handle);
		}

		#endregion

		#region Destructor

		~AudioEngine()
		{
			Dispose(false);
		}

		#endregion

		#region Public Dispose Methods

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		#endregion

		#region Public Methods

		public AudioCategory GetCategory(string name)
		{
			if (String.IsNullOrEmpty(name))
			{
				throw new ArgumentNullException("name");
			}

			int category = XnaBackend.Xact.GetCategoryIndex(handle, name);

			if (category < 0)
			{
				throw new InvalidOperationException(
					"Invalid category name!"
				);
			}

			return new AudioCategory(this, category, name);
		}

		public float GetGlobalVariable(string name)
		{
			if (String.IsNullOrEmpty(name))
			{
				throw new ArgumentNullException("name");
			}

			int variable = XnaBackend.Xact.GetGlobalVariableIndex(handle, name);

			if (variable < 0)
			{
				throw new InvalidOperationException(
					"Invalid variable name!"
				);
			}

			return XnaBackend.Xact.GetGlobalVariable(handle, variable);
		}

		public void SetGlobalVariable(string name, float value)
		{
			if (String.IsNullOrEmpty(name))
			{
				throw new ArgumentNullException("name");
			}

			int variable = XnaBackend.Xact.GetGlobalVariableIndex(handle, name);

			if (variable < 0)
			{
				throw new InvalidOperationException(
					"Invalid variable name!"
				);
			}

			XnaBackend.Xact.SetGlobalVariable(handle, variable, value);
		}

		public void Update()
		{
			XnaBackend.Xact.DoWork(handle);
		}

		#endregion

		#region Protected Methods

		protected virtual void Dispose(bool disposing)
		{
			lock (gcSync)
			{
				if (!IsDisposed)
				{
					if (Disposing != null)
					{
						Disposing.Invoke(this, null);
					}

					XnaBackend.Xact.ShutDownEngine(handle);
					XnaBackend.Xact.ReleaseEngine(handle);
					rendererDetails = null;

					IsDisposed = true;
				}
			}
		}

		#endregion

		#region Internal Methods

		internal void RegisterPointer(
			IntPtr ptr,
			WeakReference reference
		) {
			lock (xactPtrs)
			{
				xactPtrs[ptr] = reference;
			}
		}

		#endregion

		#region Private Methods

		/// <summary>
		/// Called by the backend when FACT destroys a wave bank, sound bank or cue. The backend owns
		/// the native callback and decodes its union; we only map the native pointer back to the
		/// managed object through the weak registry, exactly as before.
		/// </summary>
		private static void OnXactNotification(XactNotificationKind kind, IntPtr target)
		{
			WeakReference reference;
			lock (xactPtrs)
			{
				if (xactPtrs.TryGetValue(target, out reference) && reference.IsAlive)
				{
					if (kind == XactNotificationKind.WaveBankDestroyed)
					{
						(reference.Target as WaveBank).OnWaveBankDestroyed();
					}
					else if (kind == XactNotificationKind.SoundBankDestroyed)
					{
						(reference.Target as SoundBank).OnSoundBankDestroyed();
					}
					else if (kind == XactNotificationKind.CueDestroyed)
					{
						(reference.Target as Cue).OnCueDestroyed();
					}
				}
				xactPtrs.Remove(target);
			}
		}

		#endregion
	}
}
