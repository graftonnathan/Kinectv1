// Settings/SettingsService.cs
using System;
using System.IO;
using System.Text;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Settings
{
    public sealed class SettingsService
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Converters = { new StringEnumConverter() }
        };

        private AppSettings _current;
        private readonly object _gate = new object();
        private readonly string _userPathOverride;

        public AppSettings Current { get { lock (_gate) return _current; } }

        public SettingsService(string userJsonPathOverride = null)
        {
            _userPathOverride = userJsonPathOverride;
            _current = LoadOrInitialize();
        }

        public AppSettings Reload()
        {
            lock (_gate)
            {
                _current = LoadComposite();
                return _current;
            }
        }

        public void Save(Func<AppSettings, AppSettings> update)
        {
            AppSettings next;
            lock (_gate) next = update(_current);

            Validate(next);

            var target = GetUserJsonPath();
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            var tmp = target + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            using (var jtw = new JsonTextWriter(sw) { Formatting = Formatting.Indented })
            {
                var ser = JsonSerializer.Create(JsonSettings);
                ser.Serialize(jtw, next);
            }

            var bak = target + ".bak";
            if (File.Exists(target))
            {
                File.Replace(tmp, target, bak);
            }
            else
            {
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(tmp, target);
                File.Copy(target, bak, overwrite: true);
            }

            lock (_gate) _current = next;
        }

        // ----- externals -----
        public AppSettings GetDefaults()
        {
            var json = ReadEmbeddedDefaultJson();
            var obj = JsonConvert.DeserializeObject<AppSettings>(json, JsonSettings);
            if (obj == null) throw new InvalidDataException("default.json is invalid");
            Validate(obj);
            return obj;
        }

        public static void ValidateOrThrow(AppSettings s) => Validate(s);

        // ----- internals -----
        private AppSettings LoadOrInitialize()
        {
            var target = GetUserJsonPath();
            if (!File.Exists(target))
            {
                var defaults = ReadDefaultsAsAppSettings();
                Validate(defaults);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var tmp = target + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(defaults, JsonSettings), new UTF8Encoding(false));
                var bak = target + ".bak";
                File.Move(tmp, target);
                File.Copy(target, bak, overwrite: true);
                return defaults;
            }
            return LoadComposite();
        }

        private AppSettings LoadComposite()
        {
            var defaultsJson = ReadEmbeddedDefaultJson();
            var composite = JObject.Parse(defaultsJson);

            JObject userObj = null;
            if (File.Exists(GetUserJsonPath()))
            {
                var userJson = File.ReadAllText(GetUserJsonPath(), Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(userJson))
                {
                    userObj = JObject.Parse(userJson);
                    DeepMerge(composite, userObj);
                }
            }

            bool changed = false;
            changed |= NormalizeSectionCasing(composite);

            var defaultsObj = JObject.Parse(defaultsJson);
            changed |= BackfillTts(composite, defaultsObj);

            if (changed)
            {
                PersistNormalizedUserJson(composite);
            }

            var result = composite.ToObject<AppSettings>(JsonSerializer.Create(JsonSettings));
            if (result == null) throw new InvalidDataException("Merged settings invalid");
            Validate(result);
            return result;
        }

        private void PersistNormalizedUserJson(JObject normalized)
        {
            try
            {
                var target = GetUserJsonPath();
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var tmp = target + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                using (var jtw = new JsonTextWriter(sw) { Formatting = Formatting.Indented })
                {
                    var ser = JsonSerializer.Create(JsonSettings);
                    ser.Serialize(jtw, normalized);
                }
                var bak = target + ".bak";
                if (File.Exists(target))
                {
                    File.Replace(tmp, target, bak);
                }
                else
                {
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(tmp, target);
                    File.Copy(target, bak, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Settings migration write failed: {ex.Message}");
            }
        }

        private static bool NormalizeSectionCasing(JObject root)
        {
            bool changed = false;
            if (root == null) return changed;

            void MergeInto(string lowerName, string upperName)
            {
                var upper = root[upperName] as JObject;
                if (upper == null) return;
                var lower = root[lowerName] as JObject;
                if (lower == null)
                {
                    lower = new JObject();
                    root[lowerName] = lower;
                }
                foreach (var p in upper.Properties())
                {
                    var name = p.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    var lc = char.ToLowerInvariant(name[0]) + name.Substring(1);
                    lower[lc] = p.Value.DeepClone();
                }
                root.Remove(upperName);
                changed = true;
            }

            MergeInto("audio", "Audio");
            MergeInto("tts", "Tts");
            MergeInto("vad", "Vad");
            return changed;
        }

        private static bool BackfillTts(JObject composite, JObject defaults)
        {
            bool changed = false;
            var tts = composite?["tts"] as JObject;
            var dTts = defaults?["tts"] as JObject;
            if (tts == null || dTts == null) return changed;

            void Ensure(string name, Func<JToken, bool> invalid)
            {
                var token = tts[name];
                if (token == null || invalid(token))
                {
                    tts[name] = dTts[name]?.DeepClone();
                    changed = true;
                }
            }

            bool IsNumberInvalidInRange(JToken tok, double min, double max)
            {
                if (tok == null || tok.Type == JTokenType.Null) return true;
                double v;
                try { v = tok.Value<double>(); } catch { return true; }
                return v < min || v > max;
            }

            Ensure("outputDevice", tok => tok.Type != JTokenType.String || string.IsNullOrWhiteSpace(tok.Value<string>()));
            Ensure("localVolume", tok => IsNumberInvalidInRange(tok, 0.0, 1.0));
            Ensure("discordVolume", tok => IsNumberInvalidInRange(tok, 0.0, 1.0));
            Ensure("speed", tok => IsNumberInvalidInRange(tok, 0.5, 2.0));
            Ensure("trimThreshold", tok => IsNumberInvalidInRange(tok, 0.0005, 0.05));
            Ensure("trimLeaveMs", tok => IsNumberInvalidInRange(tok, 0, 100));
            Ensure("trimMaxMs", tok => IsNumberInvalidInRange(tok, 50, 3000));
            Ensure("minClausePaddingMs", tok => IsNumberInvalidInRange(tok, 0, 200));
            Ensure("ipaServiceTimeoutMs", tok => IsNumberInvalidInRange(tok, 200, 5000));
            Ensure("ipaOneShotTimeoutMs", tok => IsNumberInvalidInRange(tok, 200, 5000));

            return changed;
        }

        private static void DeepMerge(JObject dst, JObject src)
        {
            foreach (var p in src.Properties())
            {
                if (p.Value is JObject srcObj)
                {
                    if (dst[p.Name] is JObject dstObj)
                        DeepMerge(dstObj, srcObj);
                    else
                        dst[p.Name] = srcObj.DeepClone();
                }
                else
                {
                    dst[p.Name] = p.Value.DeepClone();
                }
            }
        }

        private string ReadEmbeddedDefaultJson()
        {
            var asm = typeof(SettingsService).Assembly;
            var resName = SettingsPaths.DefaultResourceName;
            using var s = asm.GetManifestResourceStream(resName) ?? throw new FileNotFoundException("default.json resource missing");
            using var sr = new StreamReader(s, Encoding.UTF8, true);
            return sr.ReadToEnd();
        }

        private AppSettings ReadDefaultsAsAppSettings()
        {
            var json = ReadEmbeddedDefaultJson();
            var obj = JsonConvert.DeserializeObject<AppSettings>(json, JsonSettings);
            if (obj == null) throw new InvalidDataException("default.json is invalid");
            return obj;
        }

        private string GetUserJsonPath() => _userPathOverride ?? SettingsPaths.UserJsonPath;

        private static void Validate(AppSettings s)
        {
            if (s == null || s.Audio == null || s.Tts == null || s.Vad == null)
                throw new InvalidDataException("Settings missing required sections (audio/tts/vad)");

            if (s.Audio.VoiceThreshold < 0.0 || s.Audio.VoiceThreshold > 1.0)
                throw new InvalidDataException("audio.voiceThreshold out of range [0,1]");
            if (s.Audio.VadThreshold < 0 || s.Audio.BufferSize <= 0)
                throw new InvalidDataException("audio vadThreshold>=0 and bufferSize>0 required");

            // TTS basics
            if (s.Tts.Enabled)
            {
                if (string.IsNullOrWhiteSpace(s.Tts.ModelFolder)) throw new InvalidDataException("tts.modelFolder required when tts.enabled");
                if (string.IsNullOrWhiteSpace(s.Tts.ModelPath)) throw new InvalidDataException("tts.modelPath required when tts.enabled");
                if (string.IsNullOrWhiteSpace(s.Tts.VocoderPath)) throw new InvalidDataException("tts.vocoderPath required when tts.enabled");
            }

            // TTS extended fields validation (ranges)
            if (s.Tts.LocalVolume < 0.0 || s.Tts.LocalVolume > 1.0)
                throw new InvalidDataException("tts.localVolume must be 0..1");
            if (s.Tts.DiscordVolume < 0.0 || s.Tts.DiscordVolume > 1.0)
                throw new InvalidDataException("tts.discordVolume must be 0..1");
            if (s.Tts.Speed < 0.5f || s.Tts.Speed > 2.0f)
                throw new InvalidDataException("tts.speed must be 0.5..2.0");
            if (s.Tts.TrimThreshold < 0.0005 || s.Tts.TrimThreshold > 0.05)
                throw new InvalidDataException("tts.trimThreshold must be 0.0005..0.05");
            if (s.Tts.TrimLeaveMs < 0 || s.Tts.TrimLeaveMs > 100)
                throw new InvalidDataException("tts.trimLeaveMs must be 0..100");
            if (s.Tts.TrimMaxMs < 50 || s.Tts.TrimMaxMs > 3000)
                throw new InvalidDataException("tts.trimMaxMs must be 50..3000");
            if (s.Tts.MinClausePaddingMs < 0 || s.Tts.MinClausePaddingMs > 200)
                throw new InvalidDataException("tts.minClausePaddingMs must be 0..200");
            if (s.Tts.IpaServiceTimeoutMs < 200 || s.Tts.IpaServiceTimeoutMs > 5000)
                throw new InvalidDataException("tts.ipaServiceTimeoutMs must be 200..5000");
            if (s.Tts.IpaOneShotTimeoutMs < 200 || s.Tts.IpaOneShotTimeoutMs > 5000)
                throw new InvalidDataException("tts.ipaOneShotTimeoutMs must be 200..5000");

            if (s.Vad.Threshold < 0) throw new InvalidDataException("vad.threshold must be >= 0");
        }
    }
}
