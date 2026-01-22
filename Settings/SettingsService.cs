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
            Converters = { new StringEnumConverter() },
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
            {
                NamingStrategy = new Newtonsoft.Json.Serialization.DefaultNamingStrategy()
            }
        };

        // Schema hygiene: bump when migrations are added
        private const int CurrentSchemaVersion = 2; // Bumped for teamtalk->webrtc migration

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
            // Strip legacy keys from defaults/user overlays before binding (e.g. audio.vadThreshold, top-level vad)
            PruneLegacyKeys(defaults);
            var machine = ReadOptionalJsonAsJObject(SettingsPaths.MachineDefaultsPath); PruneLegacyKeys(machine);
            var user = ReadOptionalJsonAsJObject(GetUserJsonPath()); PruneLegacyKeys(user);
            var secrets = ReadOptionalJsonAsJObject(SettingsPaths.SecretsPath); PruneLegacyKeys(secrets);

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

        private static void PruneLegacyKeys(JObject root)
        {
            if (root == null) return;
            try
            {
                // Remove obsolete top-level 'vad'
                root.Remove("vad");
                var audio = root["audio"] as JObject;
                audio?.Remove("vadThreshold");

                // Normalize legacy input mode values 'mumble' and 'teamtalk' -> 'webrtc'
                var app = root["app"] as JObject ?? root["App"] as JObject;
                if (app != null)
                {
                    var imTok = app["inputMode"] ?? app["InputMode"];
                    var im = imTok?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(im))
                    {
                        var trimmed = im.Trim().ToLowerInvariant();
                        if (trimmed == "mumble" || trimmed == "teamtalk")
                        {
                            if (app["inputMode"] != null) app["inputMode"] = "webrtc";
                            else if (app["InputMode"] != null) app["InputMode"] = "webrtc";
                            else app["inputMode"] = "webrtc";
                        }
                    }
                }

                // Remove obsolete teamTalk section if present
                root.Remove("teamTalk");
                root.Remove("TeamTalk");
            }
            catch { }
        }

        // Helpers for overlays + schema
        private static int GetSchemaVersion(JObject root)
        {
            try { return root?["schemaVersion"]?.Value<int?>() ?? 0; } catch { return 0; }
        }

        private static bool MigrateUserOverrides(JObject user, int fromVersion)
        {
            if (user == null) return false;
            bool changed = false;

            // Accept legacy input mode values "mumble" and "teamtalk" and migrate to "webrtc"
            try
            {
                var app = user["app"] as JObject ?? user["App"] as JObject;
                if (app != null)
                {
                    var imTok = app["inputMode"] ?? app["InputMode"];
                    var im = imTok?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(im))
                    {
                        var trimmed = im.Trim().ToLowerInvariant();
                        if (trimmed == "mumble" || trimmed == "teamtalk")
                        {
                            if (app["inputMode"] != null) app["inputMode"] = "webrtc";
                            else if (app["InputMode"] != null) app["InputMode"] = "webrtc";
                            else app["inputMode"] = "webrtc";
                            changed = true;
                        }
                    }
                }
            }
            catch { }

            // Remove obsolete teamTalk section
            try
            {
                if (user.ContainsKey("teamTalk") || user.ContainsKey("TeamTalk"))
                {
                    user.Remove("teamTalk");
                    user.Remove("TeamTalk");
                    changed = true;
                }
            }
            catch { }

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
                        var inner = Prune(defChild, candChild);
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
            if (s == null || s.Audio == null || s.Tts == null)
                throw new InvalidDataException("Settings missing required sections (audio/tts)");

            if (s.Audio.VoiceThreshold < 0.0 || s.Audio.VoiceThreshold > 1.0)
                throw new InvalidDataException("audio.voiceThreshold out of range [0,1]");
            if (s.Audio.BufferSize <= 0)
                throw new InvalidDataException("audio.bufferSize must be >0");
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
            if (s.Ollama.ConversationMaxTokens < 0)
                throw new InvalidDataException("ollama.conversationMaxTokens must be >= 0");

            if (s.Discord == null)
                throw new InvalidDataException("discord section missing");
            if (s.Discord.Enabled)
            {
                if (string.IsNullOrWhiteSpace(s.Discord.Prefix))
                    throw new InvalidDataException("discord.prefix required when discord.enabled");
                if (string.IsNullOrWhiteSpace(s.Discord.Token) || s.Discord.Token.Length < 24)
                    throw new InvalidDataException("discord.token appears invalid or missing when discord.enabled");
            }

            if (s.WebRtc == null)
                throw new InvalidDataException("webrtc section missing");
            if (s.WebRtc.Enabled)
            {
                if (s.WebRtc.Port <= 0 || s.WebRtc.Port > 65535)
                    throw new InvalidDataException("webrtc.port must be 1..65535");
            }
        }
    }
}
