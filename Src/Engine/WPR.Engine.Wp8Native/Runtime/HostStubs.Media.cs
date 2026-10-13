namespace WPR.Wp8Native
{
    /// <summary>
    /// Registration of the media imports: MFPlat (<see cref="MediaFoundation"/>), the media engine
    /// class factory (<see cref="MediaEngine"/>), PhoneAudioSes (<see cref="PhoneAudio"/>), the
    /// XAudio2_8 ordinals past XAudio2Create (<see cref="X3DAudioLibrary"/>), and dxgi's
    /// CreateDXGIFactory/CreateDXGIFactory1.
    /// </summary>
    public sealed partial class HostStubs
    {
        private MediaCom? _mediaCom;
        private MediaEngine? _mediaEngine;
        private PhoneAudio? _phoneAudio;
        private MediaFoundation? _mediaFoundation;

        /// <summary>
        /// Decodes the compressed media the media engine is given (MP3 and the like). Set by the
        /// host from the platform's decoder; null leaves only uncompressed WAVE playable.
        /// </summary>
        public IMediaDecoder? MediaDecoder { get; set; }

        /// <summary>Media calls answered with a documented failure because nothing behind them exists here.</summary>
        public IReadOnlyList<string> MediaUnimplemented => _mediaCom?.Unimplemented ?? [];

        /// <summary>Media engine events queued to games, in order.</summary>
        public IReadOnlyList<string> MediaEngineEvents => _mediaEngine?.EventsFired ?? [];

        /// <summary>One frame of the media clocks: WASAPI padding and events, media engine time and end.</summary>
        public void TickMedia(double frameSeconds = 1.0 / 60)
        {
            _phoneAudio?.Tick(frameSeconds);
            _mediaEngine?.Tick(frameSeconds);
        }

        private void RegisterMediaImports(string imageDirectory)
        {
            _mediaCom = new MediaCom(_emulator, _frame);
            _mediaFoundation = new MediaFoundation(_mediaCom, imageDirectory) { HostStreams = new MediaStreams(this) };
            _mediaFoundation.RegisterInto(_handlers);
            _mediaEngine = new MediaEngine(_mediaCom, imageDirectory, () => XAudio2.Output, () => MediaDecoder, _mediaFoundation);
            _phoneAudio = new PhoneAudio(_mediaCom, () => XAudio2.Output, SignalEvent);
            _phoneAudio.RegisterInto(_handlers);
            new X3DAudioLibrary(_mediaCom).RegisterInto(_handlers);
            XAudio2.FrameTick += () => TickMedia();

            // HRESULT CreateDXGIFactory1(REFIID, void** ppFactory), and the older CreateDXGIFactory.
            _handlers["CreateDXGIFactory1"] = Direct3D.CreateDxgiFactory;
            _handlers["CreateDXGIFactory"] = Direct3D.CreateDxgiFactory;

            // CoCreateInstanceFromApp: the media engine class factory here, every other class
            // through whatever the COM stubs answer.
            _handlers["CoCreateInstanceFromApp"] = () =>
            {
                if (!_mediaEngine.TryCoCreateInstanceFromApp())
                {
                    ComStubs["CoCreateInstanceFromApp"]();
                }
            };
        }

        /// <summary>SetEvent on a handle from host code, through the same handler the guest uses.</summary>
        private bool SignalEvent(long handle)
        {
            if (!_handlers.TryGetValue("SetEvent", out Action? setEvent))
            {
                return false;
            }

            int r0 = UnicornEngine.Const.Arm.UC_ARM_REG_R0;
            long saved = _emulator.ReadRegister(r0);
            _emulator.WriteRegister(r0, handle);
            setEvent();
            bool ok = (_emulator.ReadRegister(r0) & 0xFFFFFFFF) != 0;
            _emulator.WriteRegister(r0, saved);
            return ok;
        }
    }
}
