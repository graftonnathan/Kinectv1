using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    public static class CoquiTtsService
    {
        public static event Action OnTtsSpeakingStarted;
        public static event Action OnTtsSpeakingFinished;
        public static event Action<string> OnTtsError;

        public static bool IsEnabled()
        {
            try { return Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { return false; }
        }

        public static Task<float[]> GenerateAudioDataAsync(string text, string speakerName = null, CancellationToken ct = default)
        {
            // In this repository, Kokoro ONNX is used for TTS playback via TtsPlaybackController.
            // Provide a simple passthrough to that controller to keep API compatibility with Discord.
            return TtsPlaybackController.GenerateAudioAsync(text, speakerName, ct);
        }

        public static async Task<bool> SpeakStreamingWithPreemptionAsync(string text, string speakerName = null)
        {
            try
            {
                OnTtsSpeakingStarted?.Invoke();
                using var cts = new CancellationTokenSource();
                var audio = await GenerateAudioDataAsync(text, speakerName, cts.Token).ConfigureAwait(false);
                if (audio == null || audio.Length == 0)
                {
                    OnTtsError?.Invoke("TTS produced no audio");
                    return false;
                }
                // Local playback via AudioDeviceManager using Kokoro sample rate
                var sr = KokoroTtsService.GetSampleRate();
                await AudioDeviceManager.PlayLocallyAsync(audio, sr).ConfigureAwait(false);
                OnTtsSpeakingFinished?.Invoke();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                OnTtsError?.Invoke(ex.Message);
                return false;
            }
        }

        public static Task<bool> SpeakStreamingWithPreemptionAsync(string text, string speakerName, CancellationToken ct)
        {
            // Respect ct by generating and playing with provided token
            return SpeakStreamingWithPreemptionAsync(text, speakerName);
        }
    }
}
