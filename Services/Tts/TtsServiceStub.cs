// TtsServiceStub.cs - Minimal stub for headless operation
// Replaces full TTS service when running without audio

namespace Kinectv1.Tts
{
    /// <summary>
    /// Stub TTS service for headless mode.
    /// All methods are no-ops since there's no audio output.
    /// </summary>
    public static class TtsService
    {
        public static void QueueSentenceForStreaming(string text, string voice = null)
        {
            // No-op in headless mode
            System.Console.WriteLine($"[TTS would say]: {text}");
        }
    }
}
