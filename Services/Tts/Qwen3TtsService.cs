// Qwen3TtsService.cs - Qwen3-TTS integration for Maggie
// Connects to Python TTS service for high-quality neural speech

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Tts
{
    /// <summary>
    /// Qwen3-TTS service client for high-quality neural text-to-speech.
    /// Connects to a Python-based Qwen3-TTS service via HTTP API.
    /// </summary>
    public static class Qwen3TtsService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static string _serviceUrl = "http://localhost:7860";
        private static string _defaultSpeaker = "Serena";  // Warm, gentle female voice
        private static bool _initialized = false;

        // Events for audio streaming
        public static event Action OnTtsSpeakingStarted;
        public static event Action OnTtsSpeakingFinished;
        public static event Action<string> OnTtsError;
        public static event Action<byte[], int> OnTtsAudioChunk; // PCM data, sample rate

        /// <summary>
        /// Initialize the Qwen3-TTS service client.
        /// </summary>
        public static void Initialize(string serviceUrl = null)
        {
            if (!string.IsNullOrWhiteSpace(serviceUrl))
                _serviceUrl = serviceUrl.TrimEnd('/');
            
            _initialized = true;
            Console.WriteLine($"[Qwen3TTS] Initialized with service at {_serviceUrl}");
            Console.WriteLine($"[Qwen3TTS] Default voice: {_defaultSpeaker}");
        }

        /// <summary>
        /// Check if the TTS service is available.
        /// </summary>
        public static async Task<bool> IsAvailableAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_serviceUrl}/health");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var json = JObject.Parse(content);
                    return json["model_loaded"]?.Value<bool>() ?? false;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Get list of available voices.
        /// </summary>
        public static async Task<string[]> GetAvailableVoicesAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_serviceUrl}/voices");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var voices = JsonConvert.DeserializeObject<VoiceInfo[]>(content);
                    return voices?.Select(v => v.name).ToArray() ?? new[] { _defaultSpeaker };
                }
            }
            catch { }
            return new[] { _defaultSpeaker };
        }

        /// <summary>
        /// Set the default voice for Maggie.
        /// </summary>
        public static async Task<bool> SetVoiceAsync(string speaker)
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_serviceUrl}/set_voice?speaker={speaker}", null);
                if (response.IsSuccessStatusCode)
                {
                    _defaultSpeaker = speaker;
                    Console.WriteLine($"[Qwen3TTS] Voice changed to: {speaker}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Qwen3TTS] Failed to change voice: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Generate speech from text and return audio data.
        /// </summary>
        public static async Task<byte[]> GenerateSpeechAsync(string text, string speaker = null, string instruct = null)
        {
            if (!_initialized)
                Initialize();

            if (string.IsNullOrWhiteSpace(text))
                return null;

            try
            {
                var request = new
                {
                    text = text,
                    speaker = speaker ?? _defaultSpeaker,
                    language = "English",
                    instruct = instruct ?? "",
                    speed = 1.0
                };

                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync($"{_serviceUrl}/tts", content);
                
                if (response.IsSuccessStatusCode)
                {
                    var audioData = await response.Content.ReadAsByteArrayAsync();
                    return audioData;
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[Qwen3TTS] TTS request failed: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Qwen3TTS] Generate speech error: {ex.Message}");
                OnTtsError?.Invoke(ex.Message);
            }
            
            return null;
        }

        /// <summary>
        /// Speak text with Qwen3-TTS (non-streaming, plays when complete).
        /// </summary>
        public static async Task SpeakAsync(string text, string speaker = null, CancellationToken ct = default)
        {
            if (!_initialized)
                Initialize();

            OnTtsSpeakingStarted?.Invoke();

            try
            {
                var audioData = await GenerateSpeechAsync(text, speaker);
                
                if (audioData != null && audioData.Length > 0)
                {
                    // Parse WAV header and extract PCM data
                    var (pcmData, sampleRate) = ParseWav(audioData);
                    
                    if (pcmData != null)
                    {
                        // Fire audio chunk event for playback
                        OnTtsAudioChunk?.Invoke(pcmData, sampleRate);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Qwen3TTS] Speak error: {ex.Message}");
                OnTtsError?.Invoke(ex.Message);
            }
            finally
            {
                OnTtsSpeakingFinished?.Invoke();
            }
        }

        /// <summary>
        /// Queue a sentence for streaming TTS playback.
        /// This matches the interface of the existing TtsService.
        /// </summary>
        public static void QueueSentenceForStreaming(string text, string speaker = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SpeakAsync(text, speaker);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Qwen3TTS] Streaming queue error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Parse WAV file bytes to extract PCM data and sample rate.
        /// </summary>
        private static (byte[] pcmData, int sampleRate) ParseWav(byte[] wavData)
        {
            try
            {
                using var ms = new MemoryStream(wavData);
                using var reader = new BinaryReader(ms);
                
                // Read WAV header
                var riff = reader.ReadBytes(4);
                if (System.Text.Encoding.ASCII.GetString(riff) != "RIFF")
                    return (null, 0);
                
                reader.ReadInt32(); // File size
                reader.ReadBytes(4); // WAVE
                
                // Find fmt chunk
                while (ms.Position < ms.Length)
                {
                    var chunkId = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                    var chunkSize = reader.ReadInt32();
                    
                    if (chunkId == "fmt ")
                    {
                        reader.ReadInt16(); // Audio format
                        reader.ReadInt16(); // Num channels
                        var sampleRate = reader.ReadInt32();
                        reader.ReadInt32(); // Byte rate
                        reader.ReadInt16(); // Block align
                        reader.ReadInt16(); // Bits per sample
                        
                        // Skip remaining fmt chunk data
                        if (chunkSize > 16)
                            reader.ReadBytes(chunkSize - 16);
                        
                        // Find data chunk
                        while (ms.Position < ms.Length)
                        {
                            var dataId = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                            var dataSize = reader.ReadInt32();
                            
                            if (dataId == "data")
                            {
                                var pcmData = reader.ReadBytes(dataSize);
                                return (pcmData, sampleRate);
                            }
                            else
                            {
                                reader.ReadBytes(dataSize);
                            }
                        }
                    }
                    else
                    {
                        reader.ReadBytes(chunkSize);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Qwen3TTS] WAV parse error: {ex.Message}");
            }
            
            return (null, 0);
        }

        private class VoiceInfo
        {
            public string name { get; set; }
            public string description { get; set; }
            public string language { get; set; }
        }
    }
}
