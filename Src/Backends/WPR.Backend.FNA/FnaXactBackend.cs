using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using WPR.Xna.Rhi;
// FAudio/FACT bindings live in the GLOBAL namespace (see FnaAudioBackend).

namespace WPR.Backend.FNA
{
	/// <summary>
	/// FACT implementation of <see cref="IXactBackend"/> — Stage 5c-3b (Plans/STAGE5C-SCOPE.md).
	///
	/// <para>Owns the whole FACT native surface on behalf of the WPR-owned XACT types: the runtime
	/// parameters, notification descriptions, streaming parameters, renderer details, the 3D handle
	/// blob — and, most importantly, <b>the notification callback's delegate lifetime</b>. A function
	/// pointer handed to native code must be kept alive by its creator or the thunk is collected and
	/// the next callback jumps into freed memory; keeping that responsibility here (rather than in the
	/// framework types) is exactly why XACT was split into its own sub-stage.</para>
	///
	/// <para>The 3D handle is per-engine, so it is kept in a small map keyed by engine handle rather
	/// than as a single field — a process can in principle create more than one AudioEngine.</para>
	/// </summary>
	public sealed class FnaXactBackend : IXactBackend
	{
		/// <summary>Per-engine state the framework must not see: the 3D handle blob and the pinned
		/// notification callback. Held for as long as the engine lives.</summary>
		private sealed class EngineState
		{
			public byte[]? Handle3D;
			public FAudio.FACTNotificationCallback? Callback;   // keeps the native thunk alive
			public Action<XactNotificationKind, IntPtr>? OnNotification;
			public int Channels = 1;
		}

		private static readonly object _gate = new object();
		private static readonly Dictionary<IntPtr, EngineState> _engines = new Dictionary<IntPtr, EngineState>();

		private static EngineState? Find(IntPtr engine)
		{
			lock (_gate)
			{
				return _engines.TryGetValue(engine, out EngineState? s) ? s : null;
			}
		}

		// ---- Engine lifetime ----

		public IntPtr CreateEngine()
		{
			FAudio.FACTCreateEngine(0, out IntPtr engine);
			return engine;
		}

		public unsafe bool InitializeEngine(
			IntPtr engine,
			IntPtr settingsBuffer,
			int settingsBufferLength,
			int lookAheadMilliseconds,
			string? rendererId,
			Action<XactNotificationKind, IntPtr> onNotification)
		{
			var state = new EngineState { OnNotification = onNotification };

			// The callback is stored in EngineState BEFORE it is handed to native code, and that
			// object stays referenced by _engines for the engine's lifetime — this is the delegate
			// lifetime guarantee described in the class docs.
			state.Callback = notification => DispatchNotification(engine, notification);

			FAudio.FACTRuntimeParameters settings = new FAudio.FACTRuntimeParameters
			{
				pGlobalSettingsBuffer = settingsBuffer,
				globalSettingsBufferSize = (uint) settingsBufferLength,
				globalSettingsFlags = FAudio.FACT_FLAG_MANAGEDATA,
				fnNotificationCallback = Marshal.GetFunctionPointerForDelegate(state.Callback),
				lookAheadTime = (uint) lookAheadMilliseconds,
			};
			if (!string.IsNullOrEmpty(rendererId))
			{
				// FIXME: wchar_t? -flibit  (carried over from FNA verbatim)
				settings.pRendererID = Marshal.StringToHGlobalAuto(rendererId);
			}

			lock (_gate)
			{
				_engines[engine] = state;
			}

			uint result = FAudio.FACTAudioEngine_Initialize(engine, ref settings);

			if (settings.pRendererID != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(settings.pRendererID);
			}

			if (result != 0)
			{
				lock (_gate) { _engines.Remove(engine); }
				return false;
			}

			// 3D + final mix format
			state.Handle3D = new byte[FAudio.F3DAUDIO_HANDLE_BYTESIZE];
			FAudio.FACT3DInitialize(engine, state.Handle3D);
			FAudio.FACTAudioEngine_GetFinalMixFormat(engine, out FAudio.FAudioWaveFormatExtensible mixFormat);
			state.Channels = mixFormat.Format.nChannels;

			// Every XACT object destruction has to come back through us.
			RegisterNotification(engine, FAudio.FACTNOTIFICATIONTYPE_WAVEBANKDESTROYED);
			RegisterNotification(engine, FAudio.FACTNOTIFICATIONTYPE_SOUNDBANKDESTROYED);
			RegisterNotification(engine, FAudio.FACTNOTIFICATIONTYPE_CUEDESTROYED);
			return true;
		}

		private static void RegisterNotification(IntPtr engine, byte type)
		{
			FAudio.FACTNotificationDescription desc = new FAudio.FACTNotificationDescription
			{
				flags = FAudio.FACT_FLAG_NOTIFICATION_PERSIST,
				type = type,
			};
			FAudio.FACTAudioEngine_RegisterNotification(engine, ref desc);
		}

		/// <summary>Decodes the native notification union and forwards it as (kind, object pointer).
		/// The union access stays here because the struct is FACT's, not ours.</summary>
		private static unsafe void DispatchNotification(IntPtr engine, IntPtr notification)
		{
			try
			{
				EngineState? state = Find(engine);
				Action<XactNotificationKind, IntPtr>? handler = state?.OnNotification;
				if (handler == null)
				{
					return;
				}

				FAudio.FACTNotification* n = (FAudio.FACTNotification*) notification;
				if (n->type == FAudio.FACTNOTIFICATIONTYPE_WAVEBANKDESTROYED)
				{
					handler(XactNotificationKind.WaveBankDestroyed, n->anon.waveBank.pWaveBank);
				}
				else if (n->type == FAudio.FACTNOTIFICATIONTYPE_SOUNDBANKDESTROYED)
				{
					handler(XactNotificationKind.SoundBankDestroyed, n->anon.soundBank.pSoundBank);
				}
				else if (n->type == FAudio.FACTNOTIFICATIONTYPE_CUEDESTROYED)
				{
					handler(XactNotificationKind.CueDestroyed, n->anon.cue.pCue);
				}
			}
			catch
			{
				// This runs on a native callback: an escaping exception would cross the native
				// boundary and take the process down. Never let that happen.
			}
		}

		public void ShutDownEngine(IntPtr engine) => FAudio.FACTAudioEngine_ShutDown(engine);

		public void ReleaseEngine(IntPtr engine)
		{
			FAudio.FACTAudioEngine_Release(engine);
			lock (_gate) { _engines.Remove(engine); }   // releases the callback + 3D handle
		}

		public void DoWork(IntPtr engine) => FAudio.FACTAudioEngine_DoWork(engine);

		public int GetFinalMixChannelCount(IntPtr engine) => Find(engine)?.Channels ?? 1;

		public unsafe XactRendererInfo[] GetRenderers(IntPtr engine)
		{
			FAudio.FACTAudioEngine_GetRendererCount(engine, out ushort count);
			var result = new XactRendererInfo[count];
			for (ushort i = 0; i < count; i += 1)
			{
				FAudio.FACTAudioEngine_GetRendererDetails(engine, i, out FAudio.FACTRendererDetails details);
				result[i] = new XactRendererInfo
				{
					DisplayName = ReadUnicode(details.displayName),
					RendererId = ReadUnicode(details.rendererID),
				};
			}
			return result;
		}

		private static unsafe string ReadUnicode(short* utf16)
		{
			// FACTRendererDetails' fields are fixed-size UTF-16 buffers.
			byte[] bytes = new byte[0xFF];
			Marshal.Copy((IntPtr) utf16, bytes, 0, bytes.Length);
			return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
		}

		public void SetEngineVolume(IntPtr engine, IntPtr category, float volume) =>
			FAudio.FACTAudioEngine_SetVolume(engine, (ushort) category, volume);

		public void PauseEngine(IntPtr engine, IntPtr category, bool pause) =>
			FAudio.FACTAudioEngine_Pause(engine, (ushort) category, pause ? 1 : 0);

		public void StopEngine(IntPtr engine, IntPtr category, XactStopOptions options) =>
			FAudio.FACTAudioEngine_Stop(engine, (ushort) category, StopFlags(options));

		private static uint StopFlags(XactStopOptions options) =>
			options == XactStopOptions.Immediate
				? FAudio.FACT_FLAG_STOP_IMMEDIATE
				: FAudio.FACT_FLAG_STOP_RELEASE;

		// ---- Categories / global variables ----

		public int GetCategoryIndex(IntPtr engine, string name)
		{
			ushort index = FAudio.FACTAudioEngine_GetCategory(engine, name);
			return index == FAudio.FACTCATEGORY_INVALID ? -1 : index;
		}

		public void SetCategoryVolume(IntPtr engine, int category, float volume) =>
			FAudio.FACTAudioEngine_SetVolume(engine, (ushort) category, volume);

		public void PauseCategory(IntPtr engine, int category, bool pause) =>
			FAudio.FACTAudioEngine_Pause(engine, (ushort) category, pause ? 1 : 0);

		public void StopCategory(IntPtr engine, int category, XactStopOptions options) =>
			FAudio.FACTAudioEngine_Stop(engine, (ushort) category, StopFlags(options));

		public int GetGlobalVariableIndex(IntPtr engine, string name)
		{
			ushort index = FAudio.FACTAudioEngine_GetGlobalVariableIndex(engine, name);
			return index == FAudio.FACTVARIABLEINDEX_INVALID ? -1 : index;
		}

		public float GetGlobalVariable(IntPtr engine, int index)
		{
			FAudio.FACTAudioEngine_GetGlobalVariable(engine, (ushort) index, out float value);
			return value;
		}

		public void SetGlobalVariable(IntPtr engine, int index, float value) =>
			FAudio.FACTAudioEngine_SetGlobalVariable(engine, (ushort) index, value);

		// ---- Banks ----

		public IntPtr CreateSoundBank(IntPtr engine, IntPtr buffer, int bufferLength)
		{
			FAudio.FACTAudioEngine_CreateSoundBank(
				engine, buffer, (uint) bufferLength, 0, 0, out IntPtr soundBank);
			return soundBank;
		}

		public void DestroySoundBank(IntPtr soundBank) => FAudio.FACTSoundBank_Destroy(soundBank);

		public XactState GetSoundBankState(IntPtr soundBank)
		{
			FAudio.FACTSoundBank_GetState(soundBank, out uint state);
			return ToXactState(state);
		}

		public IntPtr CreateInMemoryWaveBank(IntPtr engine, IntPtr buffer, int bufferLength)
		{
			FAudio.FACTAudioEngine_CreateInMemoryWaveBank(
				engine, buffer, (uint) bufferLength, 0, 0, out IntPtr waveBank);
			return waveBank;
		}

		public IntPtr CreateStreamingWaveBank(
			IntPtr engine, string filePath, int offset, int packetSize, out IntPtr fileHandle)
		{
			// Separator normalisation + rooting against the title location, as FNA's WaveBank used to
			// do inline. Platform concerns, so they belong here rather than in the framework type.
			string safeName = MonoGame.Utilities.FileHelpers.NormalizeFilePathSeparators(filePath);
			if (!System.IO.Path.IsPathRooted(safeName))
			{
				safeName = System.IO.Path.Combine(Microsoft.Xna.Framework.TitleLocation.Path, safeName);
			}

			fileHandle = FAudio.FAudio_fopen(safeName);
			FAudio.FACTStreamingParameters settings = new FAudio.FACTStreamingParameters
			{
				file = fileHandle,
				offset = (uint) offset,
				packetSize = (ushort) packetSize,
			};
			FAudio.FACTAudioEngine_CreateStreamingWaveBank(engine, ref settings, out IntPtr waveBank);
			return waveBank;
		}

		public void CloseStreamingFile(IntPtr fileHandle)
		{
			if (fileHandle != IntPtr.Zero)
			{
				FAudio.FAudio_close(fileHandle);
			}
		}

		public void DestroyWaveBank(IntPtr waveBank) => FAudio.FACTWaveBank_Destroy(waveBank);

		public XactState GetWaveBankState(IntPtr waveBank)
		{
			FAudio.FACTWaveBank_GetState(waveBank, out uint state);
			return ToXactState(state);
		}

		// ---- Cues ----

		public int GetCueIndex(IntPtr soundBank, string name)
		{
			ushort index = FAudio.FACTSoundBank_GetCueIndex(soundBank, name);
			return index == FAudio.FACTINDEX_INVALID ? -1 : index;
		}

		public IntPtr PrepareCue(IntPtr soundBank, int cueIndex)
		{
			FAudio.FACTSoundBank_Prepare(soundBank, (ushort) cueIndex, 0, 0, out IntPtr cue);
			return cue;
		}

		public void PlayCueFireAndForget(IntPtr soundBank, int cueIndex) =>
			FAudio.FACTSoundBank_Play(soundBank, (ushort) cueIndex, 0, 0, IntPtr.Zero);

		public IntPtr PlayCue(IntPtr soundBank, int cueIndex)
		{
			FAudio.FACTSoundBank_Play(soundBank, (ushort) cueIndex, 0, 0, out IntPtr cue);
			return cue;
		}

		public void DestroyCue(IntPtr cue) => FAudio.FACTCue_Destroy(cue);
		public void PlayPreparedCue(IntPtr cue) => FAudio.FACTCue_Play(cue);
		public void PauseCue(IntPtr cue, bool pause) => FAudio.FACTCue_Pause(cue, pause ? 1 : 0);
		public void StopCue(IntPtr cue, XactStopOptions options) => FAudio.FACTCue_Stop(cue, StopFlags(options));

		public XactState GetCueState(IntPtr cue)
		{
			FAudio.FACTCue_GetState(cue, out uint state);
			return ToXactState(state);
		}

		public int GetCueVariableIndex(IntPtr cue, string name)
		{
			ushort index = FAudio.FACTCue_GetVariableIndex(cue, name);
			return index == FAudio.FACTVARIABLEINDEX_INVALID ? -1 : index;
		}

		public float GetCueVariable(IntPtr cue, int index)
		{
			FAudio.FACTCue_GetVariable(cue, (ushort) index, out float value);
			return value;
		}

		public void SetCueVariable(IntPtr cue, int index, float value) =>
			FAudio.FACTCue_SetVariable(cue, (ushort) index, value);

		// ---- 3D ----

		public void Apply3DToCue(IntPtr engine, IntPtr cue, in Audio3DParams p)
		{
			EngineState? state = Find(engine);
			if (state?.Handle3D == null)
			{
				return;
			}

			FAudio.F3DAUDIO_DSP_SETTINGS dsp = Calculate(state, in p, out GCHandle pin);
			try
			{
				FAudio.FACT3DApply(ref dsp, cue);
			}
			finally
			{
				pin.Free();
			}
		}

		public void PlayCue3D(IntPtr engine, IntPtr soundBank, int cueIndex, in Audio3DParams p)
		{
			EngineState? state = Find(engine);
			if (state?.Handle3D == null)
			{
				return;
			}

			FAudio.F3DAUDIO_DSP_SETTINGS dsp = Calculate(state, in p, out GCHandle pin);
			try
			{
				FAudio.FACTSoundBank_Play3D(soundBank, (ushort) cueIndex, 0, 0, ref dsp, IntPtr.Zero);
			}
			finally
			{
				pin.Free();
			}
		}

		/// <summary>Shared FACT3DCalculate. Builds the native listener/emitter from plain XNA-space
		/// values — including the right-handed→left-handed Z flip and the fixed emitter defaults that
		/// used to live in AudioEmitter (and, until 5c-3b, in FNA's temporary WprXact3D helper).</summary>
		private static FAudio.F3DAUDIO_DSP_SETTINGS Calculate(
			EngineState state, in Audio3DParams p, out GCHandle coefficientPin)
		{
			FAudio.F3DAUDIO_LISTENER listener = new FAudio.F3DAUDIO_LISTENER
			{
				OrientFront = ToF3D(p.ListenerForward),
				OrientTop = ToF3D(p.ListenerUp),
				Position = ToF3D(p.ListenerPosition),
				Velocity = ToF3D(p.ListenerVelocity),
			};
			FAudio.F3DAUDIO_EMITTER emitter = new FAudio.F3DAUDIO_EMITTER
			{
				ChannelCount = (uint) p.SourceChannels,
				CurveDistanceScaler = p.CurveDistanceScaler,
				DopplerScaler = p.EmitterDopplerScale,
				OrientFront = ToF3D(p.EmitterForward),
				OrientTop = ToF3D(p.EmitterUp),
				Position = ToF3D(p.EmitterPosition),
				Velocity = ToF3D(p.EmitterVelocity),
				ChannelRadius = 1.0f,
				pChannelAzimuths = StereoAzimuthPtr,
			};

			float[] coefficients = new float[p.SourceChannels * p.DestinationChannels];
			coefficientPin = GCHandle.Alloc(coefficients, GCHandleType.Pinned);
			FAudio.F3DAUDIO_DSP_SETTINGS dsp = new FAudio.F3DAUDIO_DSP_SETTINGS
			{
				SrcChannelCount = (uint) p.SourceChannels,
				DstChannelCount = (uint) p.DestinationChannels,
				pMatrixCoefficients = coefficientPin.AddrOfPinnedObject(),
			};
			FAudio.FACT3DCalculate(state.Handle3D!, ref listener, ref emitter, ref dsp);
			return dsp;
		}

		private static FAudio.F3DAUDIO_VECTOR ToF3D(Vector3 v) =>
			new FAudio.F3DAUDIO_VECTOR { x = v.X, y = v.Y, z = -v.Z };

		private static readonly float[] StereoAzimuth = new float[] { 0.0f, 0.0f };
		private static readonly GCHandle StereoAzimuthHandle =
			GCHandle.Alloc(StereoAzimuth, GCHandleType.Pinned);
		private static IntPtr StereoAzimuthPtr => StereoAzimuthHandle.AddrOfPinnedObject();

		// ---- Content loading ----

		/// <summary>
		/// Slurps a bank file into unmanaged memory. This was <c>TitleContainer.ReadToPointer</c>, an
		/// internal FNA helper with exactly one caller (this method) — 5c-4 moved TitleContainer up into
		/// WPR.Framework.Xna, and its body was the type's last tie to <c>FNAPlatform</c>, so it came down
		/// here instead. Same rooting rule as <see cref="CreateStreamingWaveBank"/> above, and the same
		/// FileNotFoundException on a miss.
		/// </summary>
		public IntPtr ReadFileToPointer(string path, out int length)
		{
			string safeName = MonoGame.Utilities.FileHelpers.NormalizeFilePathSeparators(path);
			if (!System.IO.Path.IsPathRooted(safeName))
			{
				safeName = System.IO.Path.Combine(Microsoft.Xna.Framework.TitleLocation.Path, safeName);
			}
			if (!System.IO.File.Exists(safeName))
			{
				throw new System.IO.FileNotFoundException(safeName);
			}

			IntPtr buffer = Microsoft.Xna.Framework.FNAPlatform.ReadFileToPointer(safeName, out IntPtr len);
			length = (int) len;
			return buffer;
		}

		public void FreeFilePointer(IntPtr buffer) =>
			Microsoft.Xna.Framework.FNAPlatform.FreeFilePointer(buffer);

		// ---- State mapping ----

		private static XactState ToXactState(uint state)
		{
			XactState result = XactState.None;
			if ((state & FAudio.FACT_STATE_CREATED) != 0) result |= XactState.Created;
			if ((state & FAudio.FACT_STATE_PREPARED) != 0) result |= XactState.Prepared;
			if ((state & FAudio.FACT_STATE_PLAYING) != 0) result |= XactState.Playing;
			if ((state & FAudio.FACT_STATE_STOPPING) != 0) result |= XactState.Stopping;
			if ((state & FAudio.FACT_STATE_STOPPED) != 0) result |= XactState.Stopped;
			if ((state & FAudio.FACT_STATE_PAUSED) != 0) result |= XactState.Paused;
			if ((state & FAudio.FACT_STATE_INUSE) != 0) result |= XactState.InUse;
			if ((state & FAudio.FACT_STATE_PREPARING) != 0) result |= XactState.Preparing;
			return result;
		}
	}
}
