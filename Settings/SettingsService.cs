// Settings/SettingsService.cs
using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Threading;
using System.ComponentModel.DataAnnotations;
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

        // Schema hygiene: bump when migrations are added
        private const int CurrentSchemaVersion = 1;

        private AppSettings _current;
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);
        private readonly string _userPathOverride;

        public event EventHandler<AppSettings> Changed;

        public AppSettings Current { get { return _current; } }

        public SettingsService(string userJsonPathOverride = null)
        {
            _userPathOverride = userJsonPathOverride;
            _current = LoadOrInitialize();
        }

        public AppSettings Reload()
        {
            _mutex.Wait();
            try
            {
                _current = LoadComposite();
            }
            finally { _mutex.Release(); }
            Changed?.Invoke(this, _current);
            return _current;
        }

        // Save a full snapshot
        public void Save(AppSettings next)
        {
            Validate(next);
            var target = GetUserJsonPath();
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            // Compute overrides-only JSON against effective defaults (defaults + machine)
            var overrides = GetOverridesJson(next) ?? new JObject();
            overrides["schemaVersion"] = CurrentSchemaVersion;

            var tmp = target + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            using (var jtw = new JsonTextWriter(sw) { Formatting = Formatting.Indented })
            {
                var ser = JsonSerializer.Create(JsonSettings);
                ser.Serialize(jtw, overrides);
            }

            var bak = target + ".bak";
            if (File.Exists(target))
                File.Replace(tmp, target, bak);
            else
            {
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(tmp, target);
                File.Copy(target, bak, overwrite: true);
            }

            _mutex.Wait();
            try { _current = next; }
            finally { _mutex.Release(); }
            Changed?.Invoke(this, _current);
        }

        // Async-friendly wrapper to match docs contract
        public System.Threading.Tasks.Task SaveAsync(AppSettings next)
        {
            Save(next);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        // ----- externals -----
        public AppSettings GetDefaults()
        {
            var json = ReadEmbeddedDefaultJson();
            var obj = JsonConvert.DeserializeObject<AppSettings>(json, JsonSettings)
                      ?? throw new InvalidDataException("default.json is invalid");
            Validate(obj);
            return obj;
        }

        public AppSettings GetDefaultsEffective()
        {
            var eff = OverlayBySection(ReadDefaultsAsJObject(), ReadOptionalJsonAsJObject(SettingsPaths.MachineDefaultsPath));
            var obj = eff.ToObject<AppSettings>(JsonSerializer.Create(JsonSettings))
                      ?? throw new InvalidDataException("effective defaults invalid");
            Validate(obj);
            return obj;
        }

        public static void ValidateOrThrow(AppSettings s) => Validate(s);

        public void ResetToDefaults()
        {
            var path = GetUserJsonPath();
            if (File.Exists(path)) File.Delete(path);
            Reload();
        }

        // Diagnostics: build effective overlay JSON (defaults -> machine -> user -> secrets)
        public JObject GetEffectiveJson()
        {
            var defaults = ReadDefaultsAsJObject();
            var machine = ReadOptionalJsonAsJObject(SettingsPaths.MachineDefaultsPath);
            var user = ReadOptionalJsonAsJObject(GetUserJsonPath());
            var secrets = ReadOptionalJsonAsJObject(SettingsPaths.SecretsPath);

            // user-only migration in memory; no persistence here
            var userVer = GetSchemaVersion(user);
            if (userVer < CurrentSchemaVersion)
            {
                if (MigrateUserOverrides(user, userVer) && user != null)
                    user["schemaVersion"] = CurrentSchemaVersion;
            }

            return OverlayBySection(defaults, machine, user, secrets);
        }

        // Optional QA: compute overrides-only JSON for cleaner diffs
        public JObject GetOverridesJson(AppSettings candidate)
        {
            var ser = JsonSerializer.Create(JsonSettings);
            var effDefaults = OverlayBySection(ReadDefaultsAsJObject(), ReadOptionalJsonAsJObject(SettingsPaths.MachineDefaultsPath));
            var jCandidate = JObject.FromObject(candidate, ser);
            jCandidate["schemaVersion"] = CurrentSchemaVersion;
            return PruneToOverrides(effDefaults, jCandidate);
        }

        // ----- internals -----
        private AppSettings LoadOrInitialize()
        {
            var target = GetUserJsonPath();
            if (!File.Exists(target))
            {
                // First run: create empty overrides file with only schemaVersion
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var tmp = target + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                using (var jtw = new JsonTextWriter(sw) { Formatting = Formatting.Indented })
                {
                    var empty = new JObject
                    {
                        ["schemaVersion"] = CurrentSchemaVersion
                    };
                    var ser = JsonSerializer.Create(JsonSettings);
                    ser.Serialize(jtw, empty);
                }
                var bak = target + ".bak";
                File.Move(tmp, target);
                File.Copy(target, bak, overwrite: true);

                // Load the composite effective settings (defaults + machine + empty user + secrets)
                return LoadComposite();
            }
            return LoadComposite();
        }

        // Overlays: defaults <- machine <- user <- secrets
        private AppSettings LoadComposite()
        {
            var defaults = ReadDefaultsAsJObject();
            var machine = ReadOptionalJsonAsJObject(SettingsPaths.MachineDefaultsPath);
            var user = ReadOptionalJsonAsJObject(GetUserJsonPath());
            var secrets = ReadOptionalJsonAsJObject(SettingsPaths.SecretsPath);

            // schemaVersion migration on user overrides only (no mutations to defaults or secrets)
            var userVer = GetSchemaVersion(user);
            if (userVer < CurrentSchemaVersion)
            {
                if (MigrateUserOverrides(user, userVer))
                {
                    if (user != null) user["schemaVersion"] = CurrentSchemaVersion;
                }
                else if (user != null)
                {
                    user["schemaVersion"] = CurrentSchemaVersion;
                }
            }

            var effective = OverlayBySection(defaults, machine, user, secrets);

            var result = effective.ToObject<AppSettings>(JsonSerializer.Create(JsonSettings))
                         ?? throw new InvalidDataException("Effective settings invalid");
            Validate(result);
            return result;
        }

        // Helpers for overlays + schema
        private static int GetSchemaVersion(JObject root)
        {
            try { return root?[(string)"schemaVersion"]?.Value<int?>() ?? 0; } catch { return 0; }
        }

        private static bool MigrateUserOverrides(JObject user, int fromVersion)
        {
            if (user == null) return false;
            bool changed = false;
            // Example: future migrations mutate only 'user' object
            // if (fromVersion < 2) { /* transform keys/values */ changed = true; fromVersion = 2; }
            return changed;
        }

        // Compute overrides-only JSON (remove values equal to effective defaults)
        private static JObject PruneToOverrides(JObject effectiveDefaults, JObject candidate)
        {
            if (candidate == null) return null;
            if (effectiveDefaults == null) return (JObject)candidate.DeepClone();

            JObject Prune(JObject defObj, JObject candObj)
            {
                var pruned = new JObject();
                foreach (var prop in candObj.Properties())
                {
                    var name = prop.Name;
                    var candVal = prop.Value;
                    var defVal = defObj[name];

                    if (candVal is JObject candChild && defVal is JObject defChild)
                    {
                        var inner = Prune(defChild as JObject, candChild);
                        if (inner.HasValues)
                            pruned[name] = inner;
                    }
                    else
                    {
                        if (defVal == null || !JToken.DeepEquals(defVal, candVal))
                            pruned[name] = candVal.DeepClone();
                    }
                }
                return pruned;
            }

            return Prune(effectiveDefaults, candidate);
        }

        private static JObject ReadDefaultsAsJObject()
        {
            var json = ReadEmbeddedDefaultJsonStatic();
            return JObject.Parse(json);
        }

        private static JObject OverlayBySection(params JObject[] layers)
        {
            var dst = new JObject();
            foreach (var layer in layers)
            {
                if (layer == null) continue;
                foreach (var p in layer.Properties())
                {
                    dst[p.Name] = p.Value.DeepClone();
                }
            }
            return dst;
        }

        private static JObject ReadOptionalJsonAsJObject(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var txt = File.ReadAllText(path, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(txt) ? null : JObject.Parse(txt);
        }

        private static string ReadEmbeddedDefaultJsonStatic()
        {
            var asm = typeof(SettingsService).Assembly;
            var resName = SettingsPaths.DefaultResourceName;
            using var s = asm.GetManifestResourceStream(resName) ?? throw new FileNotFoundException("default.json resource missing");
            using var sr = new StreamReader(s, Encoding.UTF8, true);
            return sr.ReadToEnd();
        }

        private string ReadEmbeddedDefaultJson()
        {
            return ReadEmbeddedDefaultJsonStatic();
        }

        private AppSettings ReadDefaultsAsAppSettings()
        {
            var json = ReadEmbeddedDefaultJson();
            var obj = JsonConvert.DeserializeObject<AppSettings>(json, JsonSettings)
                      ?? throw new InvalidDataException("default.json is invalid");
            return obj;
        }

        private string GetUserJsonPath() => _userPathOverride ?? SettingsPaths.UserJsonPath;

        private static void Validate(AppSettings s)
        {
            // 0) DataAnnotations
            var ctx = new ValidationContext(s);
            var results = new System.Collections.Generic.List<ValidationResult>();
            if (!Validator.TryValidateObject(s, ctx, results, validateAllProperties: true))
            {
                var msg = string.Join("; ", results.ConvertAll(r => r.ErrorMessage));
                throw new InvalidDataException("Settings annotations failed: " + msg);
            }

            // 1) Existing custom rules
            if (s == null || s.Audio == null || s.Tts == null || s.Vad == null)
                throw new InvalidDataException("Settings missing required sections (audio/tts/vad)");

            if (s.Audio.VoiceThreshold < 0.0 || s.Audio.VoiceThreshold > 1.0)
                throw new InvalidDataException("audio.voiceThreshold out of range [0,1]");
            if (s.Audio.VadThreshold < 0 || s.Audio.BufferSize <= 0)
                throw new InvalidDataException("audio vadThreshold>=0 and bufferSize>0 required");
            if (s.Audio.SpeakerMatchMinScore < 0.0 || s.Audio.SpeakerMatchMinScore > 1.0)
                throw new InvalidDataException("audio.speakerMatchMinScore must be 0..1");

            if (s.Tts.Enabled)
            {
                if (string.IsNullOrWhiteSpace(s.Tts.ModelFolder)) throw new InvalidDataException("tts.modelFolder required when tts.enabled");
                if (string.IsNullOrWhiteSpace(s.Tts.ModelPath)) throw new InvalidDataException("tts.modelPath required when tts.enabled");
                // VocoderPath is optional for some models; do not enforce
            }

            if (s.Ollama == null)
                throw new InvalidDataException("ollama section missing");
            if (string.IsNullOrWhiteSpace(s.Ollama.Provider))
                throw new InvalidDataException("ollama.provider required (Ollama|LMStudio)");
            var prov = s.Ollama.Provider.Trim();
            if (!string.Equals(prov, "Ollama", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(prov, "LMStudio", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ollama.provider must be 'Ollama' or 'LMStudio'");

            if (s.Ollama.Enabled)
            {
                if (string.IsNullOrWhiteSpace(s.Ollama.Model))
                    throw new InvalidDataException("ollama.model required when ollama.enabled");
            }
            if (s.Ollama.MaxMessagesPerSpeaker < 0 || s.Ollama.MaxSystemMessages < 0)
                throw new InvalidDataException("ollama max message counts must be >= 0");
            if (s.Ollama.ConversationTimeoutMinutes < 0)
                throw new InvalidDataException("ollama.conversationTimeoutMinutes must be >= 0");

            if (s.Discord == null)
                throw new InvalidDataException("discord section missing");
            if (s.Discord.Enabled)
            {
                if (string.IsNullOrWhiteSpace(s.Discord.Prefix))
                    throw new InvalidDataException("discord.prefix required when discord.enabled");
                if (string.IsNullOrWhiteSpace(s.Discord.Token) || s.Discord.Token.Length < 24)
                    throw new InvalidDataException("discord.token appears invalid or missing when discord.enabled");
            }

            if (s.Mumble == null)
                throw new InvalidDataException("mumble section missing");
            if (s.Mumble.Enabled)
            {
                if (string.IsNullOrWhiteSpace(s.Mumble.Host))
                    throw new InvalidDataException("mumble.host required when mumble.enabled");
                if (s.Mumble.Port <= 0 || s.Mumble.Port > 65535)
                    throw new InvalidDataException("mumble.port must be 1..65535");
                if (string.IsNullOrWhiteSpace(s.Mumble.Username))
                    throw new InvalidDataException("mumble.username required when mumble.enabled");
                if (s.Mumble.OpusBitrate < 6000 || s.Mumble.OpusBitrate > 96000)
                    throw new InvalidDataException("mumble.opusBitrate must be 6000..96000");
                if (s.Mumble.VadThreshold < 1 || s.Mumble.VadThreshold > 10000)
                    throw new InvalidDataException("mumble.vadThreshold must be 1..10000");
                if (s.Mumble.ReconnectBackoffMs < 0 || s.Mumble.ReconnectBackoffMs > 60000)
                    throw new InvalidDataException("mumble.reconnectBackOffMs must be 0..60000");
            }
        }
    }
}
