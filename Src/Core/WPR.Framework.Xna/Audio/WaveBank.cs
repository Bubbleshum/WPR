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
using System.IO;
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Audio
{
	// http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.audio.wavebank.aspx
	public class WaveBank : IDisposable
	{
		#region Public Properties

		public bool IsDisposed
		{
			get;
			private set;
		}

		public bool IsPrepared
		{
			get
			{
				uint state;
				state = (uint) XnaBackend.Xact.GetWaveBankState(handle);
				return (state & (uint) XactState.Prepared) != 0;
			}
		}

		public bool IsInUse
		{
			get
			{
				uint state;
				state = (uint) XnaBackend.Xact.GetWaveBankState(handle);
				return (state & (uint) XactState.InUse) != 0;
			}
		}

		#endregion

		#region Private Variables

		private IntPtr handle;

		private AudioEngine engine;
		private WeakReference selfReference;

		private IntPtr bankData;
		private IntPtr bankDataLen; // Non-zero for in-memory WaveBanks

		#endregion

		#region Disposing Event

		public event EventHandler<EventArgs> Disposing;

		#endregion

		#region Public Constructors

		public WaveBank(
			AudioEngine audioEngine,
			string nonStreamingWaveBankFilename
		) {
			if (audioEngine == null)
			{
				throw new ArgumentNullException("audioEngine");
			}
			if (String.IsNullOrEmpty(nonStreamingWaveBankFilename))
			{
				throw new ArgumentNullException("nonStreamingWaveBankFilename");
			}

			bankData = XnaBackend.Xact.ReadFileToPointer(
				nonStreamingWaveBankFilename,
				out int bankLength
			);
			bankDataLen = (IntPtr) bankLength;

			handle = XnaBackend.Xact.CreateInMemoryWaveBank(
				audioEngine.handle,
				bankData,
				bankLength
			);

			engine = audioEngine;
			selfReference = new WeakReference(this, true);
			engine.RegisterPointer(handle, selfReference);
			IsDisposed = false;
		}

		public WaveBank(
			AudioEngine audioEngine,
			string streamingWaveBankFilename,
			int offset,
			short packetsize
		) {
			if (audioEngine == null)
			{
				throw new ArgumentNullException("audioEngine");
			}
			if (String.IsNullOrEmpty(streamingWaveBankFilename))
			{
				throw new ArgumentNullException("streamingWaveBankFilename");
			}

			/* Path normalisation (separator fixups + rooting against the title location) moved into
			 * the backend along with the native fopen: both are platform concerns, and doing them
			 * here would drag FNA's TitleLocation/FileHelpers back into this assembly.
			 */
			handle = XnaBackend.Xact.CreateStreamingWaveBank(
				audioEngine.handle,
				streamingWaveBankFilename,
				offset,
				packetsize,
				out bankData
			);

			engine = audioEngine;
			selfReference = new WeakReference(this, true);
			engine.RegisterPointer(handle, selfReference);
			IsDisposed = false;
		}

		#endregion

		#region Destructor

		~WaveBank()
		{
			if (AudioEngine.ProgramExiting)
			{
				return;
			}

			if (!IsDisposed && IsInUse)
			{
				// STOP LEAKING YOUR BANKS, ARGH
				GC.ReRegisterForFinalize(this);
				return;
			}
			Dispose(false);
		}

		#endregion

		#region Public Dispose Method

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		#endregion

		#region Protected Dispose Method

		protected virtual void Dispose(bool disposing)
		{
			lock (engine.gcSync)
			{
				if (!IsDisposed)
				{
					if (Disposing != null)
					{
						Disposing.Invoke(this, null);
					}

					// If this is disposed, stop leaking memory!
					if (!engine.IsDisposed)
					{
						XnaBackend.Xact.DestroyWaveBank(handle);
					}
					OnWaveBankDestroyed();
				}
			}
		}

		#endregion

		#region Internal Methods

		internal void OnWaveBankDestroyed()
		{
			IsDisposed = true;
			if (bankData != IntPtr.Zero)
			{
				if (bankDataLen != IntPtr.Zero)
				{
					XnaBackend.Xact.FreeFilePointer(bankData);
					bankDataLen = IntPtr.Zero;
				}
				else
				{
					XnaBackend.Xact.CloseStreamingFile(bankData);
				}
				bankData = IntPtr.Zero;
			}
			handle = IntPtr.Zero;
			selfReference = null;
		}

		#endregion
	}
}
