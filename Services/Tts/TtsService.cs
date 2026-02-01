// TtsService.cs - Backward-compatible wrapper for Qwen3-TTS
// This maintains compatibility with existing code that calls TtsService

using System;
using System.Threading.Tasks;

namespace Kinectv1.Tts
{
    /// <summary>
    /// Backward-compatible TTS service wrapper.
    /// Delegates to Qwen3TtsService for actual voice generation.
    /// </summary>
    public static class TtsService
    {
        // Mirror events from Qwen3TtsService for compatibility
        public static event Action OnTtsSpeakingStarted
        {
            add => Qwen3TtsService.OnTtsSpeakingStarted += value;
            remove => Qwen3TtsService.OnTtsSpeakingStarted -= value;
        }
        
        public static event Action OnTtsSpeakingFinished
        {
            add => Qwen3TtsService.OnTtsSpeakingFinished += value;
            remove => Qwen3TtsService.OnTtsSpeakingFinished -= value;
        }
        
        public static event Action<string> OnTtsError
        {
            add => Qwen3TtsService.OnTtsError += value;
            remove => Qwen3TtsService.OnTtsError -= value;
        }
        
        public static event Action<byte[], int> OnTtsAudioChunk
        {
            add => Qwen3TtsService.OnTtsAudioChunk += value;
            remove => Qwen3TtsService.OnTtsAudioChunk -= value;
        }

        /// <summary>
        /// Queue a sentence for streaming TTS playback.
        /// </summary>
        public static void QueueSentenceForStreaming(string text, string voice = null)
        {
            // Delegate to Qwen3-TTS
            Qwen3TtsService.QueueSentenceForStreaming(text, voice);
        }

        /// <summary>
        /// Check if TTS is enabled (always true if Qwen3-TTS is available).
        /// </summary>
        public static bool IsEnabled() => true;

        /// <summary>
        /// Get sample rate (Qwen3-TTS uses 22050 or 24000 depending on model).
        /// </summary>
        public static int GetSampleRate() => 22050;

        /// <summary>
        /// Generate speech and play it.
        /// </summary>
        public static Task SpeakAsync(string text, string speaker = null)
        {
            return Qwen3TtsService.SpeakAsync(text, speaker);
        }
    }
}
