using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Audio source for local microphone input via NAudio.
    /// Outputs 16kHz mono PCM16 frames.
    /// </summary>
    public sealed class LocalMicSource : AudioSourceBase
    {
        private WaveInEvent _waveIn;
        private readonly object _lock = new object();
        private const int SampleRate = 16000;
        private const int BufferMs = 50;

        public override AudioSourceType SourceType => AudioSourceType.LocalMic;

        public override async Task StartAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_state != AudioSourceState.Stopped) return;
                SetState(AudioSourceState.Starting);
            }

            try
            {
                var inputDevice = AudioDeviceManager.GetConfiguredInputDevice();
                
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = inputDevice?.DeviceNumber ?? 0,
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = BufferMs
                };

                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.RecordingStopped += OnRecordingStopped;
                _waveIn.StartRecording();

                _isEnabled = true;
                SetState(AudioSourceState.Running);
                Log($"[LocalMic] Started on device {inputDevice?.DeviceName ?? "default"}");
            }
            catch (Exception ex)
            {
                Log($"[LocalMic] Start failed: {ex.Message}");
                SetState(AudioSourceState.Error);
                throw;
            }

            await Task.CompletedTask;
        }

        public override async Task StopAsync()
        {
            WaveInEvent waveIn;
            lock (_lock)
            {
                if (_state == AudioSourceState.Stopped) return;
                waveIn = _waveIn;
                _waveIn = null;
                SetState(AudioSourceState.Stopped);
            }

            _isEnabled = false;

            if (waveIn != null)
            {
                try
                {
                    waveIn.DataAvailable -= OnDataAvailable;
                    waveIn.RecordingStopped -= OnRecordingStopped;
                    waveIn.StopRecording();
                    waveIn.Dispose();
                }
                catch { }
            }

            Log("[LocalMic] Stopped");
            await Task.CompletedTask;
        }

        public override void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (!_isEnabled || e.BytesRecorded <= 0) return;

            try
            {
                // Create normalized frame with RMS computation
                var frame = NormalizedAudioFrame.Create(
                    e.Buffer,
                    e.BytesRecorded,
                    AudioSourceType.LocalMic
                );

                // Emit RMS for UI meter
                EmitRms(frame.Rms);

                // Emit frame for processing
                EmitFrame(frame);
            }
            catch (Exception ex)
            {
                Log($"[LocalMic] Frame processing error: {ex.Message}");
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                Log($"[LocalMic] Recording stopped with error: {e.Exception.Message}");
                SetState(AudioSourceState.Error);
            }
        }
    }
}
