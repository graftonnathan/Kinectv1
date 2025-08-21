using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Text.RegularExpressions;

namespace Kinectv1
{
    /// <summary>
    /// CoquiTtsService adapter now routed to Kokoro-82M ONNX (no Python sidecar)
    /// Keeps public API stable for callers.
    /// </summary>
    public static class CoquiTtsService
    {
        private static bool _isInitialized = false;
        private static readonly object _lockObject = new object();
        private static string _currentSpeaker = null;

        // Events for UI integration
        public static event Action<string> OnTtsSpeakingStarted;
        public static event Action OnTtsSpeakingFinished;
        public static event Action<string> OnTtsError;

        /// <summary>
        /// Initialize the TTS service
        /// </summary>
        public static bool Initialize(string ttsModelPath = null, string cmudictPath = null, string symbolsPath = null)
        {
            lock (_lockObject)
            {
                if (_isInitialized) return true;
                var ok = KokoroTtsService.Initialize();
                _isInitialized = ok;
                return ok;
            }
        }

        /// <summary>
        /// Convert text to speech and play it
        /// </summary>
        public static async Task<bool> SpeakAsync(string text, string speakerName = null)
        {
            try
            {
                if (!IsEnabled() || string.IsNullOrWhiteSpace(text)) return false;

                OnTtsSpeakingStarted?.Invoke(text);

                var voiceKey = speakerName ?? _currentSpeaker ?? AppSettings.LoadTtsSpeaker();

                // Generate audio using KokoroTtsService
                var audio = await Task.Run(() => KokoroTtsService.GenerateAudio(text, voiceKey));
                
                if (audio == null || audio.Length == 0) { OnTtsError?.Invoke("Kokoro returned empty audio"); return false; }

                // Play the generated audio at native sample rate
                await AudioDeviceManager.PlayLocallyAsync(audio, KokoroTtsService.GetSampleRate());

                OnTtsSpeakingFinished?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                OnTtsError?.Invoke(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Generate TTS audio data without playing it (for Discord transmission)
        /// </summary>
        public static async Task<float[]> GenerateAudioDataAsync(string text, string speakerName = null)
        {
            using (var scope = Telemetry.LatencyScope("coqui_tts_generate"))
            {
                if (!IsEnabled() || string.IsNullOrWhiteSpace(text)) 
                {
                    Telemetry.Counter("coqui_tts.disabled_or_empty");
                    return Array.Empty<float>();
                }

                Telemetry.Counter("coqui_tts.generate_requests");
                var voiceKey = speakerName ?? _currentSpeaker ?? AppSettings.LoadTtsSpeaker();
                return await Task.Run(() => KokoroTtsService.GenerateAudio(text, voiceKey));
            }
        }

        /// <summary>
        /// Check if TTS is enabled
        /// </summary>
        public static bool IsEnabled() => AppSettings.LoadTtsEnabled() && Initialize();

        /// <summary>
        /// Enable or disable TTS
        /// </summary>
        public static void SetEnabled(bool enabled) => AppSettings.SaveTtsEnabled(enabled);

        /// <summary>
        /// Set current speaker
        /// </summary>
        public static void SetSpeaker(string speaker) { _currentSpeaker = speaker; AppSettings.SaveTtsSpeaker(speaker); }

        /// <summary>
        /// Get current model name
        /// </summary>
        public static string GetCurrentModel() => "kokoro-82M";

        /// <summary>
        /// Get current execution mode (GPU or CPU)
        /// </summary>
        public static string GetExecutionMode() => KokoroTtsService.IsUsingGpu() ? "GPU" : "CPU";

        /// <summary>
        /// Check if GPU mode is active
        /// </summary>
        public static bool IsUsingGpu() => KokoroTtsService.IsUsingGpu();

        /// <summary>
        /// Switch between CPU and GPU execution modes
        /// </summary>
        public static bool SwitchExecutionMode(bool useGpu)
        {
            try
            {
                AppSettings.SaveTtsUseGpu(useGpu);
                return KokoroTtsService.RecreateSessionFromSettings();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Shutdown TTS service
        /// </summary>
        public static void Shutdown() { /* nothing to do for kokoro adapter */ }

        /// <summary>
        /// Stop current audio playback if playing
        /// </summary>
        public static void StopCurrentPlayback() { /* playback handled by AudioDeviceManager */ }

        /// <summary>
        /// Check if TTS is currently playing audio
        /// </summary>
        public static bool IsCurrentlyPlaying() => false;

        /// <summary>
        /// Get current local TTS volume (0.0 to 1.0)
        /// </summary>
        public static double GetLocalVolume() => AppSettings.LoadLocalTtsVolume();

        /// <summary>
        /// Set local TTS volume (0.0 to 1.0)
        /// </summary>
        public static void SetLocalVolume(double volume) => AppSettings.SaveLocalTtsVolume(volume);

        /// <summary>
        /// Get current Discord TTS volume (0.0 to 1.0)
        /// </summary>
        public static double GetDiscordVolume() => AppSettings.LoadDiscordTtsVolume();

        /// <summary>
        /// Set Discord TTS volume (0.0 to 1.0)
        /// </summary>
        public static void SetDiscordVolume(double volume) => AppSettings.SaveDiscordTtsVolume(volume);

        /// <summary>
        /// Get available TTS models
        /// </summary>
        public static string[] GetAvailableModels() => new[] { "kokoro-82M" };

        /// <summary>
        /// Switch to a different TTS model
        /// </summary>
        public static bool SwitchModel(string modelName) => true;

        /// <summary>
        /// Get a random speaker from VCTK dataset
        /// </summary>
        public static int GetRandomSpeaker(string accent = "any") => 0;

        /// <summary>
        /// Comprehensive TTS diagnostic method to identify why local TTS isn't working
        /// </summary>
        public static void DiagnoseTtsIssues() { /* Kokoro diagnostics can be added here */ }

        /// <summary>
        /// Test tokenization with a simple phrase to verify token ranges
        /// </summary>
        public static void TestTokenization() { }

        /// <summary>
        /// Tokenize text using proper punctuation handling based on symbols.txt vocabulary
        /// </summary>
        public static long[] Tokenize(string text) => Array.Empty<long>();

        /// <summary>
        /// Convert text to speech and play it with reduced latency by streaming segments
        /// </summary>
        public static async Task<bool> SpeakStreamingAsync(string text, string speakerName = null)
        {
            try
            {
                if (!IsEnabled() || string.IsNullOrWhiteSpace(text)) return false;
                OnTtsSpeakingStarted?.Invoke(text);

                var voiceKey = speakerName ?? _currentSpeaker ?? AppSettings.LoadTtsSpeaker();

                foreach (var segment in KokoroTtsService.GenerateAudioSegments(text, voiceKey))
                {
                    if (segment == null || segment.Length == 0) continue;
                    await AudioDeviceManager.PlayLocallyAsync(segment, KokoroTtsService.GetSampleRate());
                }

                OnTtsSpeakingFinished?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                OnTtsError?.Invoke(ex.Message);
                return false;
            }
        }
    }
}