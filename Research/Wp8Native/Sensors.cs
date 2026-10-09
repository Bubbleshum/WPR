namespace WPR.Wp8Native
{
    /// <summary>
    /// The device's motion sensors as the host offers them. Null members mean "this device has
    /// none", which is what a game is told.
    /// </summary>
    /// <remarks>
    /// The probe runs without one, and every sensor is then absent. WPR's game host fills it from
    /// <c>WPR.Engine.Sensors.SensorBackend</c>. Called on the emulator thread; must not throw.
    /// </remarks>
    public interface ISensorHost
    {
        /// <summary>Rotation rate in radians per second, WP device frame.</summary>
        IHostSensor? Gyroscope { get; }

        /// <summary>Acceleration in g, WP device frame.</summary>
        IHostSensor? Accelerometer { get; }
    }

    /// <summary>One three-axis sensor. Start/Stop are counted by the host, once each per game here.</summary>
    public interface IHostSensor
    {
        void Start();

        void Stop();

        /// <summary>The latest sample.</summary>
        (double X, double Y, double Z) Read();
    }

    /// <summary>
    /// <c>Windows.Devices.Sensors.Gyrometer</c> and <c>Accelerometer</c>.
    /// </summary>
    /// <remarks>
    /// <para>Both were improvised stand-ins until now: <c>GetDefault</c> answered a placeholder
    /// object, so a game believed it had the sensor and read whatever the stand-in left in its
    /// out-parameters. Modern Combat 4 asks for the gyrometer for its gyroscope aiming.</para>
    /// <para>Layouts from <c>Windows.Devices.winmd</c> (declaration order is vtable order, after
    /// IInspectable's six). The two classes differ only in units, the reading's property names and
    /// the accelerometer's extra <c>Shaken</c> event:</para>
    /// <code>
    /// IxxxStatics   6 GetDefault(Ixxx**)
    /// Ixxx          6 GetCurrentReading(IxxxReading**)   7 get_MinimumReportInterval(UINT32*)
    ///               8 put_ReportInterval(UINT32)         9 get_ReportInterval(UINT32*)
    ///              10 add_ReadingChanged(handler, token*) 11 remove_ReadingChanged(token)
    ///              12 add_Shaken  13 remove_Shaken        (accelerometer only)
    /// IxxxReading   6 get_Timestamp(DateTime*)  7-9 get_X/Y/Z(DOUBLE*)
    /// IxxxReadingChangedEventArgs  6 get_Reading(IxxxReading**)
    /// </code>
    /// <para><c>ReadingChanged</c> is raised from the frame loop, at most once a frame and no faster
    /// than the game's report interval, and only when the sample has changed: the game's handler
    /// runs on its own thread between frames, as input does.</para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        /// <summary>What stands behind the sensors. Read when a game first asks for one.</summary>
        public ISensorHost? Sensors { get; set; }

        /// <summary>One frame at 60 Hz, the fastest any sensor reports here.</summary>
        private const uint MinimumReportIntervalMs = 16;

        private readonly List<WinRtSensor> _sensors = new();
        private int _sensorFrame = -1;

        /// <summary>What happened to each sensor, for the log.</summary>
        public List<string> SensorLog { get; } = new();

        private void RegisterSensors()
        {
            Register(new WinRtSensor(
                "Gyrometer",
                host => host.Gyroscope,
                // WinRT speaks degrees per second; the host gives radians.
                scale: 180.0 / Math.PI,
                hasShaken: false));
            Register(new WinRtSensor(
                "Accelerometer",
                host => host.Accelerometer,
                scale: 1.0,
                hasShaken: true));

            void Register(WinRtSensor sensor)
            {
                _sensors.Add(sensor);
                _factories["Windows.Devices.Sensors." + sensor.Name] = CreateDiscoveryObject(
                    $"I{sensor.Name}Statics",
                    slotCount: InspectableSlots + 1,
                    known: new Dictionary<int, (string, Action)>
                    {
                        [InspectableSlots + 0] = ("GetDefault", () =>
                        {
                            long instance = SensorInstance(sensor);
                            if (Arg(1) != 0)
                            {
                                _emulator.WriteUInt32(Arg(1), (uint)instance);
                            }

                            Return(HResultOk);
                        }),
                    });
            }
        }

        /// <summary>The sensor's object, or 0 when the device has none (WinRT's nullptr).</summary>
        private long SensorInstance(WinRtSensor sensor)
        {
            if (sensor.Instance != 0 || sensor.Absent)
            {
                return sensor.Instance;
            }

            sensor.Device = Sensors is { } host ? sensor.Pick(host) : null;
            if (sensor.Device is null)
            {
                sensor.Absent = true;
                SensorLog.Add($"{sensor.Name}.GetDefault: none on this device");
                return 0;
            }

            SensorLog.Add($"{sensor.Name}.GetDefault: present");
            sensor.Reading = CreateDiscoveryObject(
                $"I{sensor.Name}Reading",
                slotCount: InspectableSlots + 4,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_Timestamp", () =>
                    {
                        if (Arg(1) != 0)
                        {
                            _emulator.WriteUInt64(Arg(1), (ulong)sensor.Timestamp);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 1] = ("get_X", () =>
                    {
                        sensor.ValuesRead++;
                        WriteDoubleOut(sensor.Value.X * sensor.Scale);
                    }),
                    [InspectableSlots + 2] = ("get_Y", () => WriteDoubleOut(sensor.Value.Y * sensor.Scale)),
                    [InspectableSlots + 3] = ("get_Z", () => WriteDoubleOut(sensor.Value.Z * sensor.Scale)),
                });
            sensor.Args = CreateDiscoveryObject(
                $"I{sensor.Name}ReadingChangedEventArgs",
                slotCount: InspectableSlots + 1,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_Reading", () =>
                    {
                        if (Arg(1) != 0)
                        {
                            _emulator.WriteUInt32(Arg(1), (uint)sensor.Reading);
                        }

                        Return(HResultOk);
                    }),
                });

            var slots = new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("GetCurrentReading", () =>
                {
                    sensor.Polls++;
                    StartSensor(sensor);
                    Sample(sensor);
                    if (Arg(1) != 0)
                    {
                        _emulator.WriteUInt32(Arg(1), (uint)sensor.Reading);
                    }

                    Return(HResultOk);
                }),
                [InspectableSlots + 1] = ("get_MinimumReportInterval", () => ReturnUInt32(MinimumReportIntervalMs)),
                [InspectableSlots + 2] = ("put_ReportInterval", () =>
                {
                    // 0 means "the default", as on WinRT.
                    uint interval = (uint)Arg(1);
                    sensor.ReportIntervalMs = interval == 0 ? MinimumReportIntervalMs : Math.Max(interval, MinimumReportIntervalMs);
                    Return(HResultOk);
                }),
                [InspectableSlots + 3] = ("get_ReportInterval", () => ReturnUInt32(sensor.ReportIntervalMs)),
                [InspectableSlots + 4] = ("add_ReadingChanged", () =>
                {
                    long handler = Arg(1);
                    long token = ++sensor.NextToken;
                    if (Arg(2) != 0)
                    {
                        _emulator.WriteUInt64(Arg(2), (ulong)token);
                    }

                    sensor.Handlers.Add((token, handler));
                    StartSensor(sensor);
                    SensorLog.Add($"{sensor.Name}.ReadingChanged subscribed, handler 0x{handler:X8}");

                    // Ours to keep: the caller releases its reference once the add returns.
                    AddRefThenReturn(handler, HResultOk);
                }),
                [InspectableSlots + 5] = ("remove_ReadingChanged", () =>
                {
                    // An EventRegistrationToken is 64-bit, so after `this` in r0 it is aligned
                    // into r2:r3; r1 is padding.
                    RemoveHandler(sensor, Arg(2));
                    Return(HResultOk);
                }),
            };

            if (sensor.HasShaken)
            {
                slots[InspectableSlots + 6] = ("add_Shaken", () =>
                {
                    // Accepted and never raised: nothing here detects a shake.
                    if (Arg(2) != 0)
                    {
                        _emulator.WriteUInt64(Arg(2), 0);
                    }

                    Return(HResultOk);
                });
                slots[InspectableSlots + 7] = ("remove_Shaken", () => Return(HResultOk));
            }

            sensor.Instance = CreateDiscoveryObject($"I{sensor.Name}", slotCount: InspectableSlots + 8, known: slots);
            return sensor.Instance;
        }

        private void RemoveHandler(WinRtSensor sensor, long token)
        {
            int index = sensor.Handlers.FindIndex(h => h.Token == token);
            if (index < 0)
            {
                return;
            }

            sensor.Handlers.RemoveAt(index);
            SensorLog.Add($"{sensor.Name}.ReadingChanged unsubscribed");
        }

        private void StartSensor(WinRtSensor sensor)
        {
            if (sensor.Started || sensor.Device is null)
            {
                return;
            }

            sensor.Started = true;
            try
            {
                sensor.Device.Start();
            }
            catch (Exception)
            {
            }
        }

        private static void Sample(WinRtSensor sensor)
        {
            try
            {
                sensor.Value = sensor.Device!.Read();
            }
            catch (Exception)
            {
            }

            sensor.Timestamp = DateTime.UtcNow.ToFileTimeUtc();
        }

        /// <summary>Stops every sensor a game started. The host calls it when the game ends.</summary>
        public void StopSensors()
        {
            foreach (WinRtSensor sensor in _sensors)
            {
                if (!sensor.Started)
                {
                    continue;
                }

                sensor.Started = false;
                SensorLog.Add($"{sensor.Name}: stopped after {sensor.Polls} GetCurrentReading, {sensor.ValuesRead} readings read, {sensor.Raised} ReadingChanged raised");
                try
                {
                    sensor.Device?.Stop();
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Raises <c>ReadingChanged</c> for each sensor with a new sample due, or returns false if
        /// there is none this frame. As with <see cref="DeliverBack"/>, true means it has taken
        /// over the return path and will run <paramref name="continueWith"/> itself.
        /// </summary>
        private bool DeliverSensors(Action continueWith)
        {
            if (_sensorFrame == ProcessEventsCalls)
            {
                return false;
            }

            _sensorFrame = ProcessEventsCalls;

            var due = new List<(WinRtSensor Sensor, long Handler)>();
            foreach (WinRtSensor sensor in _sensors)
            {
                if (sensor.Handlers.Count == 0 || sensor.Device is null)
                {
                    continue;
                }

                int frames = (int)Math.Max(1, sensor.ReportIntervalMs / MinimumReportIntervalMs);
                if (ProcessEventsCalls - sensor.LastRaisedFrame < frames)
                {
                    continue;
                }

                (double X, double Y, double Z) previous = sensor.Value;
                Sample(sensor);
                if (sensor.LastRaisedFrame >= 0 && sensor.Value == previous)
                {
                    continue;
                }

                sensor.LastRaisedFrame = ProcessEventsCalls;
                sensor.Raised++;
                foreach ((long _, long handler) in sensor.Handlers)
                {
                    due.Add((sensor, handler));
                }
            }

            if (due.Count == 0)
            {
                return false;
            }

            RaiseNext(0);
            return true;

            void RaiseNext(int index)
            {
                while (index < due.Count)
                {
                    (WinRtSensor sensor, long handler) = due[index];
                    long invoke = _emulator.ReadUInt32(_emulator.ReadUInt32(handler, 0) + (SlotDelegateInvoke * 4), 0);
                    if (_emulator.IsExecutableCode(invoke))
                    {
                        int next = index + 1;
                        _emulator.CallEmulated(
                            $"{sensor.Name}::ReadingChanged",
                            invoke,
                            [handler, sensor.Instance, sensor.Args],
                            onReturn: () => RaiseNext(next),
                            recycle: true);
                        return;
                    }

                    index++;
                }

                // The handlers' answers are not the caller's: put back the S_OK it was given.
                Return(HResultOk);
                continueWith();
            }
        }

        private void WriteDoubleOut(double value)
        {
            if (Arg(1) != 0)
            {
                _emulator.WriteUInt64(Arg(1), BitConverter.DoubleToUInt64Bits(value));
            }

            Return(HResultOk);
        }

        private sealed class WinRtSensor(string name, Func<ISensorHost, IHostSensor?> pick, double scale, bool hasShaken)
        {
            public string Name { get; } = name;

            public Func<ISensorHost, IHostSensor?> Pick { get; } = pick;

            public double Scale { get; } = scale;

            public bool HasShaken { get; } = hasShaken;

            public IHostSensor? Device { get; set; }

            public bool Absent { get; set; }

            public bool Started { get; set; }

            public long Instance { get; set; }

            public long Reading { get; set; }

            public long Args { get; set; }

            public (double X, double Y, double Z) Value { get; set; }

            public long Timestamp { get; set; }

            public uint ReportIntervalMs { get; set; } = MinimumReportIntervalMs;

            public int LastRaisedFrame { get; set; } = -1;

            public long NextToken { get; set; }

            public int Polls { get; set; }

            public int ValuesRead { get; set; }

            public int Raised { get; set; }

            public List<(long Token, long Handler)> Handlers { get; } = new();
        }
    }
}
