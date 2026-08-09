using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.IsolatedStorage;
using System.Runtime.Serialization;
using WPR.Common;

namespace WPR.WindowsCompability
{
    // projection: System......IsolatedStorageSettings
    public class IsolatedStorageSettings2 //RnD : static
    {
        private static IsolatedStorageSettings2 _ApplicationSettings;
        private const string LocalSettingsName = "__LocalSettings";

        private IsolatedStorageFile _Holder;
        // Pre-initialise so static accessors (TryGetValue, indexer, Contains, Remove, Add,
        // Save) are NRE-safe even when the patcher routes a call here before the user code
        // has triggered the ApplicationSettings getter. The .ctor overwrites this with the
        // on-disk dictionary; until then, reads return defaults and writes accumulate.
        private static Dictionary<string, object> _Settings = new Dictionary<string, object>();

        public IsolatedStorageSettings2()// RnD: static
        {
        }

        internal IsolatedStorageSettings2(IsolatedStorageFile file)
        {
            _Holder = file;
            if (!file.FileExists(LocalSettingsName))
            {
                _Settings = new Dictionary<string, object>();
            }
            else
            {
                using (IsolatedStorageFileStream fs = file.OpenFile(
                    LocalSettingsName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (StreamReader sr = new StreamReader(fs))
                    {
                        DataContractSerializer reader = new DataContractSerializer(
                            typeof(Dictionary<string, object>));
                        try
                        {
                            _Settings = (reader.ReadObject(fs) as Dictionary<string, object>)!;
                        }
                        catch (Exception ex)
                        {
                            // Recoverable: settings file was truncated (typically a prior run
                            // killed mid-Save). Start fresh; Save() now writes atomically so
                            // this should not recur on clean shutdowns.
                            Log.Warn(LogCategory.Common,
                                $"Isolated settings unreadable, resetting to empty: {ex.Message}");
                            _Settings = new Dictionary<string, object>();
                        }

                        if (_Settings == null)
                        {
                            _Settings = new Dictionary<string, object>();
                        }
                    }
                }
            }
        }

        ~IsolatedStorageSettings2()
        {
            Save();
        }

        public void Save()
        {
            // Force the singleton path: the patcher rewrites IsolatedStorageSettings.Save
            // call sites without guaranteeing the instance was previously materialised, so
            // _Holder can be null here. Drive the ApplicationSettings getter to ensure
            // there's a backing IsolatedStorageFile to write to.
            IsolatedStorageFile? holder = _Holder ?? ApplicationSettings._Holder;
            if (holder == null) return;

            // Collect the runtime types of stored values so DataContractSerializer can
            // write entries whose declared slot type is `object` but whose actual value
            // is something like `Dictionary<int,int>`. Without knownTypes, WriteObject
            // throws SerializationException — and because WP7 games typically call Save()
            // from inside Game.Initialize, that throw propagates up to FNA's init-time
            // catch and leaves the game with half-initialized statics. Symptom: the
            // SDL window opens but Draw NREs every frame (Fling black-screen-on-launch).
            HashSet<Type> knownTypes = new HashSet<Type>();
            foreach (object? value in _Settings.Values)
            {
                if (value != null)
                {
                    knownTypes.Add(value.GetType());
                }
            }

            try
            {
                using (IsolatedStorageFileStream storage = holder.CreateFile(LocalSettingsName))
                {
                    DataContractSerializer serializer = new DataContractSerializer(
                        typeof(Dictionary<string, object>), knownTypes);
                    serializer.WriteObject(storage, _Settings);
                }
            }
            catch (Exception ex)
            {
                // Don't let a serialization failure escape — that's what trapped Fling.
                // The settings live in-memory regardless; losing the on-disk copy this
                // launch is far better than a black screen.
                Log.Warn(LogCategory.Common,
                    $"IsolatedStorageSettings.Save failed ({_Settings.Count} entries, " +
                    $"knownTypes=[{string.Join(",", knownTypes)}]): {ex.Message}");
            }
        }


        // RnD: static 
        public static IsolatedStorageSettings2 ApplicationSettings
        {
            get
            {
                if (_ApplicationSettings == null)
                {
                    _ApplicationSettings = new
                        IsolatedStorageSettings2(
                            IsolatedStorageFile.GetUserStoreForApplication());
                }

                return _ApplicationSettings;
            }
        }

        public object this[string key]
        {
            get
            {
                // MUST throw on a missing key — do NOT "soften" this to return null.
                // Both contracts we implement require the throw: Silverlight's
                // IsolatedStorageSettings indexer throws KeyNotFoundException, and so does
                // IDictionary<string, object>. WP7 SDKs are written against that and catch it
                // to substitute a default. Returning null instead turns their catch into an
                // unboxing NRE for value-type settings:
                //
                //   try   { val = (T) _isolatedStore[key]; }          // (int) null -> NRE
                //   catch (KeyNotFoundException) { val = defaultValue; }   // never reached
                //
                // That is exactly how FlurryWP7SDK.Common.PersistedSettingUtils.GetValueOrDefault<T>
                // reads its never-written "Flurry_ReportDelay" (int) setting. The NRE escaped on
                // Flurry's raw reporting Thread (no try/catch in ReportManager.SendReportsWithDelay),
                // i.e. an UNHANDLED exception that killed the process on every launch of every
                // Flurry-bundled title (Bug Village and 4 others). Fixed 2026-08-07.
                return _Settings[key];
            }

            set
            {
                _Settings[key] = value;
            }
        }

        public object? this[object key]
        {
            get
            {
                string? keyString = key as string;
                if (keyString == null)
                {
                    return null;
                }
                if (!_Settings.ContainsKey(keyString))
                {
                    return null;
                }

                return _Settings[keyString];
            }
            set
            {
                string? keyString = key as string;
                if (keyString == null)
                {
                    return;
                }

                // IDictionary's indexer setter ADDS the key when absent (only the getter is
                // lookup-only). This used to bail out on a missing key, silently dropping the
                // write, so a game that persisted a NEW setting through the non-generic
                // indexer could never save it. Fixed 2026-08-07 alongside the getter above.
                _Settings[keyString] = value!;
            }
        }

        //RnD: static
        // [MaybeNullWhen(false)]
        public static bool TryGetValue(string key, out object value)
        {
            //value = true;
            return _Settings.TryGetValue(key, out value);
        }

        // Generic overload mirroring Silverlight's instance
        // IsolatedStorageSettings.TryGetValue<T>(string, out T). Krome Game Room
        // titles (e.g. Asteroids Deluxe) read settings via
        // applicationSettings.TryGetValue<float>/<bool>(key, out value); without
        // this overload the patched call site throws MissingMethodException.
        public bool TryGetValue<T>(string key, out T value)
        {
            if (_Settings != null && _Settings.TryGetValue(key, out var raw))
            {
                if (raw is T typed)
                {
                    value = typed;
                    return true;
                }

                try
                {
                    value = (T)Convert.ChangeType(raw, typeof(T));
                    return true;
                }
                catch
                {
                    // Stored value isn't convertible to T — treat as absent.
                }
            }

            value = default!;
            return false;
        }

        public bool Contains(string key) => _Settings != null && _Settings.ContainsKey(key);

        public bool Remove(string key) => _Settings != null && _Settings.Remove(key);

        public void Add(string key, object value) => _Settings[key] = value;

        public void Clear() => _Settings?.Clear();

        public int Count => _Settings?.Count ?? 0;

        public ICollection<string> Keys => _Settings?.Keys ?? (ICollection<string>)Array.Empty<string>();

        public ICollection<object> Values => _Settings?.Values ?? (ICollection<object>)Array.Empty<object>();

        //RnD : static
        //public IsolatedStorageSettings2 get_ApplicationSettings()
        //{
            //byte[] result = System.Security.Cryptography
            //   .ProtectedData.Unprotect(byteArrayOfOriginalData, 
            //   additionalEntropyOrSalt, 
            //   DataProtectionScope.CurrentUser);
            //return default;
        //}
    }
}
