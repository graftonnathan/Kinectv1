// VoiceRecognizer.cs

using System;
using System.IO;
using NAudio.Wave;
using Vosk;

public static class VoiceRecognizer
{
    private static Model _model;
    private static VoskRecognizer _recognizer;
    private static WaveInEvent _waveIn;
    private static VoiceProcessor _voiceProcessor;

    public static Action<float>? OnRmsLevel;
    public static Action<string>? OnTranscription;
    public static Action<string>? OnNameHeard;
    public static Action<string, float>? OnSpeakerMatch;
    public static Action<float[]>? OnVoiceEmbedding; // Added for debugging

    public static void Start(string modelPath, string triggerName = "john")
    {
        try
        {
            Vosk.Vosk.SetLogLevel(0);

            if (!Directory.Exists(modelPath))
            {
                Console.WriteLine($"[Vosk] Model path not found: {modelPath}");
                return;
            }

            _model = new Model(modelPath);
            _recognizer = new VoskRecognizer(_model, 16000.0f);
            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(16000, 1)
            };

            _voiceProcessor = new VoiceProcessor(
                _recognizer,
                transcription =>
                {
                    OnTranscription?.Invoke(transcription);
                },
                (speaker, score) =>
                {
                    Console.WriteLine($"🎤 Matched speaker: {speaker} (score={score:F3})");
                    OnSpeakerMatch?.Invoke(speaker, score);
                },
                triggerName,
                rms =>
                {
                    // Pass real RMS values to GUI
                    OnRmsLevel?.Invoke(rms);
                },
                embedding =>
                {
                    // Pass voice embeddings for debugging
                    OnVoiceEmbedding?.Invoke(embedding);
                }
            );

            _waveIn.DataAvailable += (s, a) =>
            {
                _voiceProcessor.ProcessAudio(a.Buffer, a.BytesRecorded);
            };

            _waveIn.StartRecording();
            Console.WriteLine("[Vosk] Voice recognizer started with RMS visualization and debugging.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VoiceRecognizer.Start failed: {ex.Message}");
        }
    }

    public static void Stop()
    {
        try
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _recognizer?.Dispose();
            _model?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VoiceRecognizer.Stop failed: {ex.Message}");
        }
    }
}
