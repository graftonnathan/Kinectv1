using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Speech.Synthesis;
using System.Speech.AudioFormat;

namespace Kinectv1
{
    public static class TtsPlaybackController
    {
        private static readonly object _lock = new object();
        private static SpeechSynthesizer _synth;
        private static int _sampleRate = 22050;
        private static bool _useGpu = false; // placeholder flag to satisfy UI

        private static void EnsureSynth()
        {
            if (_synth != null) return;
            lock (_lock)
            {
                if (_synth == null)
                {
                    _synth = new SpeechSynthesizer();
                    try
                    {
                        var voice = Kinectv1.App.SettingsProvider?.Current?.Tts?.Speaker;
                        if (!string.IsNullOrWhiteSpace(voice))
                        {
                            _synth.SelectVoice(voice);
                        }
                    }
                    catch { }
                }
            }
        }

        public static bool RecreateSessionFromSettings()
        {
            lock (_lock)
            {
                try
                {
                    _synth?.Dispose();
                }
                catch { }
                _synth = null;
                EnsureSynth();
                try
                {
                    var exec = Kinectv1.App.SettingsProvider?.Current?.Tts?.Execution;
                    _useGpu = string.Equals(exec?.ToString(), "GPU", StringComparison.OrdinalIgnoreCase);
                }
                catch { _useGpu = false; }
                return true;
            }
        }

        public static bool IsUsingGpu() => _useGpu;
        public static int GetSampleRate() => _sampleRate;

        public static async Task<float[]> GenerateAudioAsync(string text, string speakerName = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();
            EnsureSynth();

            return await Task.Run(() =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(speakerName))
                    {
                        try { _synth.SelectVoice(speakerName); } catch { }
                    }

                    using var ms = new MemoryStream();
                    var fmt = new SpeechAudioFormatInfo(_sampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
                    _synth.SetOutputToWaveStream(ms);
                    _synth.Speak(new Prompt(text));
                    _synth.SetOutputToNull();
                    ms.Position = 0;

                    using var rdr = new WaveFileReader(ms);
                    ISampleProvider sampleProvider = rdr.ToSampleProvider();

                    // Read all samples into buffer
                    var buffer = new float[rdr.SampleCount];
                    int read;
                    int offset = 0;
                    var temp = new float[4096];
                    while ((read = sampleProvider.Read(temp, 0, temp.Length)) > 0)
                    {
                        Array.Copy(temp, 0, buffer, offset, read);
                        offset += read;
                        if (ct.IsCancellationRequested) break;
                    }

                    if (offset < buffer.Length)
                    {
                        Array.Resize(ref buffer, offset);
                    }

                    return buffer;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TtsPlaybackController] Generation failed: {ex.Message}");
                    return Array.Empty<float>();
                }
            }, ct).ConfigureAwait(false);
        }
    }
}
