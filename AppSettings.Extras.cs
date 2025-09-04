// AppSettings.Extras.cs
using System;
using System.IO;
using System.Linq;

namespace Kinectv1
{
    // Complements AppSettings with telemetry, fusion, audio mode, model paths and summaries
    public static partial class AppSettings
    {
        // ===== Telemetry Settings =====
        public static bool LoadTelemetryEnabled()
        {
            try { return GetBool("TelemetryEnabled"); }
            catch (Exception ex) { LogSettingError("TelemetryEnabled", $"READ FAILED: {ex.Message}"); return false; }
        }
        public static void SaveTelemetryEnabled(bool enabled)
        {
            try { SetBool("TelemetryEnabled", enabled); Console.WriteLine($"Telemetry: Enabled: {enabled}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving telemetry enabled: {ex.Message}"); }
        }
        public static string LoadTelemetryFile()
        {
            try { var path = GetString("TelemetryFile"); return string.IsNullOrWhiteSpace(path) ? "logs/telemetry.ndjson" : path; }
            catch (Exception ex) { LogSettingError("TelemetryFile", $"READ FAILED: {ex.Message}"); return "logs/telemetry.ndjson"; }
        }
        public static void SaveTelemetryFile(string filePath)
        {
            try { SetString("TelemetryFile", filePath); Console.WriteLine($"Telemetry: File path: {filePath}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving telemetry file path: {ex.Message}"); }
        }
        public static int LoadTelemetrySamplingPct()
        {
            try { var pct = GetInt("TelemetrySamplingPct"); return Math.Max(0, Math.Min(100, pct)); }
            catch (Exception ex) { LogSettingError("TelemetrySamplingPct", $"READ FAILED: {ex.Message}"); return 100; }
        }
        public static void SaveTelemetrySamplingPct(int samplingPct)
        {
            try { var clamped = Math.Max(0, Math.Min(100, samplingPct)); SetInt("TelemetrySamplingPct", clamped); Console.WriteLine($"Telemetry: Sampling percentage: {clamped}%"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving telemetry sampling percentage: {ex.Message}"); }
        }
        public static void ConfigureTelemetrySettings(bool enabled = true, string filePath = "logs/telemetry.ndjson", int samplingPct = 100)
        {
            try { SaveTelemetryEnabled(enabled); SaveTelemetryFile(filePath); SaveTelemetrySamplingPct(samplingPct); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error configuring telemetry settings: {ex.Message}"); }
        }
        public static string GetTelemetrySettingsSummary()
        {
            try
            {
                var enabled = LoadTelemetryEnabled(); var filePath = LoadTelemetryFile(); var samplingPct = LoadTelemetrySamplingPct();
                return $"?? Telemetry Settings:\n" +
                       $"   Enabled: {enabled}\n" +
                       $"   File path: {filePath}\n" +
                       $"   Sampling: {samplingPct}%\n" +
                       $"   Format: NDJSON (Newline Delimited JSON)\n" +
                       $"   Console: Warnings+ and summaries only\n" +
                       $"   Rotation: ~5MB file size limit";
            }
            catch (Exception ex) { return $"ERROR: Could not load telemetry settings: {ex.Message}"; }
        }

        // ===== Identity Fusion Settings =====
        public static float LoadFusionFaceWeight()
        {
            try { var v = GetFloat("FusionFaceWeight", 0.6f); if (v <= 0.0f || v > 1.0f) { LogSettingError("FusionFaceWeight", $"OUT OF RANGE (expected 0.0-1.0, got {v})"); return 0.6f; } return v; }
            catch (Exception ex) { LogSettingError("FusionFaceWeight", $"read FAILED: {ex.Message}"); return 0.6f; }
        }
        public static void SaveFusionFaceWeight(float weight)
        {
            try { var c = Math.Max(0.0f, Math.Min(1.0f, weight)); SetFloat("FusionFaceWeight", c); Console.WriteLine($"Fusion: Face weight set to {c:F2}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving fusion face weight: {ex.Message}"); }
        }
        public static float LoadFusionVoiceWeight()
        {
            try { var v = GetFloat("FusionVoiceWeight", 0.4f); if (v <= 0.0f || v > 1.0f) { LogSettingError("FusionVoiceWeight", $"OUT OF RANGE (expected 0.0-1.0, got {v})"); return 0.4f; } return v; }
            catch (Exception ex) { LogSettingError("FusionVoiceWeight", $"read FAILED: {ex.Message}"); return 0.4f; }
        }
        public static void SaveFusionVoiceWeight(float weight)
        {
            try { var c = Math.Max(0.0f, Math.Min(1.0f, weight)); SetFloat("FusionVoiceWeight", c); Console.WriteLine($"Fusion: Voice weight set to {c:F2}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving fusion voice weight: {ex.Message}"); }
        }
        public static int LoadFusionDecayHalfLifeMs()
        {
            try { var v = GetInt("FusionDecayHalfLifeMs", 2000); if (v < 500 || v > 10000) { LogSettingError("FusionDecayHalfLifeMs", $"OUT OF RANGE (expected 500-10000ms, got {v})"); return 2000; } return v; }
            catch (Exception ex) { LogSettingError("FusionDecayHalfLifeMs", $"read FAILED: {ex.Message}"); return 2000; }
        }
        public static void SaveFusionDecayHalfLifeMs(int halfLifeMs)
        {
            try { var c = Math.Max(500, Math.Min(10000, halfLifeMs)); SetInt("FusionDecayHalfLifeMs", c); Console.WriteLine($"Fusion: Decay half-life set to {c}ms"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving fusion decay half-life: {ex.Message}"); }
        }
        public static float LoadFusionUnknownThreshold()
        {
            try { var v = GetFloat("FusionUnknownThreshold", 0.3f); if (v < 0.0f || v > 1.0f) { LogSettingError("FusionUnknownThreshold", $"OUT OF RANGE (expected 0.0-1.0, got {v})"); return 0.3f; } return v; }
            catch (Exception ex) { LogSettingError("FusionUnknownThreshold", $"read FAILED: {ex.Message}"); return 0.3f; }
        }
        public static void SaveFusionUnknownThreshold(float threshold)
        {
            try { var c = Math.Max(0.0f, Math.Min(1.0f, threshold)); SetFloat("FusionUnknownThreshold", c); Console.WriteLine($"Fusion: Unknown threshold set to {c:F2}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving fusion unknown threshold: {ex.Message}"); }
        }
        public static void ConfigureFusionSettings(float faceWeight = 0.6f, float voiceWeight = 0.4f, int halfLifeMs = 2000, float unknownThreshold = 0.3f)
        {
            try { SaveFusionFaceWeight(faceWeight); SaveFusionVoiceWeight(voiceWeight); SaveFusionDecayHalfLifeMs(halfLifeMs); SaveFusionUnknownThreshold(unknownThreshold); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error configuring fusion settings: {ex.Message}"); }
        }
        public static string GetFusionSettingsSummary()
        {
            try
            {
                var faceWeight = LoadFusionFaceWeight(); var voiceWeight = LoadFusionVoiceWeight(); var halfLifeMs = LoadFusionDecayHalfLifeMs(); var unknownThreshold = LoadFusionUnknownThreshold();
                return $"?? Identity Fusion Settings:\n" +
                       $"   Face weight: {faceWeight:F2}\n" +
                       $"   Voice weight: {voiceWeight:F2}\n" +
                       $"   Decay half-life: {halfLifeMs}ms\n" +
                       $"   Unknown threshold: {unknownThreshold:F2}\n" +
                       $"   Fusion formula: (face*{faceWeight:F1} + voice*{voiceWeight:F1}) / {faceWeight + voiceWeight:F1}\n" +
                       $"   Time decay: score *= exp(-dt/{halfLifeMs}ms)";
            }
            catch (Exception ex) { return $"ERROR: Could not load fusion settings: {ex.Message}"; }
        }

        // ===== Audio input mode =====
        public static AudioInMode LoadAudioInMode()
        {
            try
            {
                var modeStr = GetString("AudioInMode", "LocalMic");
                if (Enum.TryParse<AudioInMode>(modeStr, true, out var mode))
                {
                    string telemetryMode = mode switch
                    {
                        AudioInMode.LocalMic => "local_mic",
                        AudioInMode.DiscordVoice => "discord_voice",
                        AudioInMode.SystemLoopback => "system_loopback",
                        AudioInMode.MumbleVoice => "mumble_voice",
                        _ => "unknown"
                    };
                    Telemetry.Counter($"audio.ingest.mode.{telemetryMode}");
                    return mode;
                }
                else
                {
                    LogSettingError("AudioInMode", $"INVALID VALUE (expected LocalMic|DiscordVoice|SystemLoopback|MumbleVoice, got '{modeStr}')");
                    Telemetry.Counter("audio.ingest.mode.local_mic");
                    return AudioInMode.LocalMic;
                }
            }
            catch (Exception ex)
            {
                LogSettingError("AudioInMode", $"READ FAILED: {ex.Message}");
                Telemetry.Counter("audio.ingest.mode.local_mic");
                return AudioInMode.LocalMic;
            }
        }
        public static void SaveAudioInMode(AudioInMode mode)
        {
            try
            {
                WriteSettingRaw("AudioInMode", mode.ToString());
                Console.WriteLine($"?? Audio input mode saved: {mode}");
                string telemetryMode = mode switch
                {
                    AudioInMode.LocalMic => "local_mic",
                    AudioInMode.DiscordVoice => "discord_voice",
                    AudioInMode.SystemLoopback => "system_loopback",
                    AudioInMode.MumbleVoice => "mumble_voice",
                    _ => "unknown"
                };
                Telemetry.Counter($"audio.ingest.mode.{telemetryMode}");
            }
            catch (Exception ex) { LogSettingError("AudioInMode", $"SAVE FAILED: {ex.Message}"); }
        }

        public static void InitializeAudioDevices()
        {
            try
            {
                Console.WriteLine("?? Initializing audio devices...");
                var audioMode = LoadAudioInMode();
                var modeDescription = audioMode switch
                {
                    AudioInMode.LocalMic => "Local Microphone",
                    AudioInMode.DiscordVoice => "Discord Voice (Opus?PCM)",
                    AudioInMode.SystemLoopback => "System Loopback Capture",
                    AudioInMode.MumbleVoice => "Mumble Voice (not yet implemented)",
                    _ => "Unknown"
                };
                Console.WriteLine($"??? Audio Input Mode: {modeDescription}");
                var input = AudioDeviceManager.GetConfiguredInputDevice();
                Console.WriteLine(input != null ? $"?? Using STT input device: {input.DeviceName} (Device #{input.DeviceNumber})" : "?? STT input device: Default");
                var outputDevice = AudioDeviceManager.GetConfiguredOutputDevice();
                Console.WriteLine(outputDevice != null ? $"?? Using TTS output device: {outputDevice.DeviceName} (Device #{outputDevice.DeviceNumber})" : "?? TTS output device: Default");
                Console.WriteLine("?? Audio device initialization complete!");
            }
            catch (Exception ex) { Console.WriteLine($"? ERROR: Audio device initialization failed: {ex.Message}"); }
        }

        // ===== STT/Speaker/Face model paths =====
        public static string LoadSttModelPath()
        {
            try
            {
                var path = GetString("SttModelPath");
                if (string.IsNullOrWhiteSpace(path)) LogSettingError("SttModelPath", "EMPTY");
                else if (!Directory.Exists(path)) LogSettingError("SttModelPath", $"NOT FOUND '{path}'");
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("SttModelPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }
        public static void SaveSttModelPath(string path)
        {
            try { SetString("SttModelPath", path); Console.WriteLine($"STT: Saved model path: {path}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving STT model path: {ex.Message}"); }
        }

        public static string LoadSpeakerEmbeddingModelPath()
        {
            try
            {
                var path = GetString("SpeakerEmbeddingModelPath");
                if (string.IsNullOrWhiteSpace(path)) LogSettingError("SpeakerEmbeddingModelPath", "EMPTY");
                else if (!File.Exists(path)) LogSettingError("SpeakerEmbeddingModelPath", "NOT FOUND");
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("SpeakerEmbeddingModelPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }
        public static void SaveSpeakerEmbeddingModelPath(string path)
        {
            try { SetString("SpeakerEmbeddingModelPath", path); Console.WriteLine($"Saved speaker embedding model path: {path}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving speaker embedding model path: {ex.Message}"); }
        }

        public static string LoadArcFaceModelPath()
        {
            try
            {
                var path = GetString("ArcFaceModelPath");
                if (string.IsNullOrWhiteSpace(path)) LogSettingError("ArcFaceModelPath", "EMPTY");
                else if (!File.Exists(path)) LogSettingError("ArcFaceModelPath", "NOT FOUND");
                return path;
            }
            catch (Exception ex)
            {
                LogSettingError("ArcFaceModelPath", $"READ FAILED: {ex.Message}");
                return null;
            }
        }
        public static void SaveArcFaceModelPath(string path)
        {
            try { SetString("ArcFaceModelPath", path); Console.WriteLine($"Saved ArcFace model path: {path}"); }
            catch (Exception ex) { Console.WriteLine($"ERROR: Error saving ArcFace model path: {ex.Message}"); }
        }

        // ===== Settings summaries used by Diagnostics =====
        public static string GetTtsSettingsSummary()
        {
            try
            {
                var enabled = LoadTtsEnabled();
                var modelPath = LoadTtsModelPath();
                var speaker = LoadTtsSpeaker();
                var gpu = LoadTtsUseGpu();
                var volLocal = LoadLocalTtsVolume();
                var volDiscord = LoadDiscordTtsVolume();
                var speed = LoadTtsSpeed();
                var trimThreshold = LoadTtsTrimThreshold();
                var minClausePadding = LoadTtsMinClausePaddingMs();
                var ipaTimeout = LoadTtsIpaServiceTimeoutMs();
                var speakerMatchScore = LoadSpeakerMatchMinScore();

                return "??? TTS Settings:\n" +
                       $"   Enabled: {enabled}\n" +
                       $"   Model: {modelPath}\n" +
                       $"   Speaker: {speaker}\n" +
                       $"   Use GPU: {gpu}\n" +
                       $"   Local Volume: {volLocal:P0}\n" +
                       $"   Discord Volume: {volDiscord:P0}\n" +
                       $"   Speed: {speed:F2}\n" +
                       $"   Trim Threshold: {trimThreshold:F3}\n" +
                       $"   Min Clause Padding: {minClausePadding}ms\n" +
                       $"   IPA Service Timeout: {ipaTimeout}ms\n" +
                       $"   Speaker Match Min Score: {speakerMatchScore:F3}";
            }
            catch (Exception ex)
            {
                return $"ERROR: Could not load TTS settings: {ex.Message}";
            }
        }

        public static string GetSttSettingsSummary()
        {
            try
            {
                var input = LoadSttInputDevice();
                var model = LoadSttModelPath();
                var mode = LoadAudioInMode();
                return "?? STT Settings:\n" +
                       $"   Input Device: {input}\n" +
                       $"   Model Path: {model}\n" +
                       $"   Audio Input Mode: {mode}";
            }
            catch (Exception ex)
            {
                return $"ERROR: Could not load STT settings: {ex.Message}";
            }
        }
    }
}
