// VoiceProcessor.cs
using System;
using System.Collections.Generic;
using Vosk;

public class VoiceProcessor
{
    private readonly VoskRecognizer _recognizer;
    private readonly Action<string> _onTranscription;
    private readonly Action<string, float> _onSpeakerMatch;
    private readonly Action<float> _onRmsLevel;
    private readonly Action<float[]> _onVoiceEmbedding;
    private readonly string _triggerName;
    private DateTime _lastVoiceTime = DateTime.UtcNow;
    private TimeSpan _silenceTimeout = TimeSpan.FromMilliseconds(700);
    private readonly List<float> _pcmBuffer = new();

    public VoiceProcessor(VoskRecognizer recognizer, Action<string> onTranscription, Action<string, float> onSpeakerMatch, string triggerName, Action<float> onRmsLevel = null, Action<float[]> onVoiceEmbedding = null)
    {
        _recognizer = recognizer;
        _onTranscription = onTranscription;
        _onSpeakerMatch = onSpeakerMatch;
        _onRmsLevel = onRmsLevel;
        _onVoiceEmbedding = onVoiceEmbedding;
        _triggerName = triggerName;
    }

    public void ProcessAudio(byte[] buffer, int bytesRecorded)
    {
        try
        {
            // Calculate RMS for GUI visualization
            float rms = AudioUtils.CalculateRms(buffer, bytesRecorded);
            _onRmsLevel?.Invoke(rms);

            if (!AudioUtils.IsVoiceActive(buffer, bytesRecorded))
            {
                if (DateTime.UtcNow - _lastVoiceTime > _silenceTimeout)
                {
                    var flush = _recognizer.FinalResult();
                    var final = JsonUtils.Extract(flush);
                    _onTranscription?.Invoke(final);
                    TriggerHandler.TryTrigger(final, _triggerName);
                    _lastVoiceTime = DateTime.UtcNow;
                }
                return;
            }

            // Only add to buffer when voice is active
            float[] floatPcm = AudioUtils.ConvertToFloatPcm(buffer, bytesRecorded);
            _pcmBuffer.AddRange(floatPcm);
            _lastVoiceTime = DateTime.UtcNow;

            if (_recognizer.AcceptWaveform(buffer, bytesRecorded))
            {
                var json = _recognizer.Result();
                var final = JsonUtils.Extract(json);
                _onTranscription?.Invoke(final);
                TriggerHandler.TryTrigger(final, _triggerName);
            }
            else
            {
                var partialJson = _recognizer.PartialResult();
                var partial = JsonUtils.Extract(partialJson);
                _onTranscription?.Invoke(partial);
                TriggerHandler.TryTrigger(partial, _triggerName);
            }

            // Process voice embeddings when we have enough audio (1 second)
            if (_pcmBuffer.Count >= 16000)
            {
                float[] oneSecond = _pcmBuffer.GetRange(0, 16000).ToArray();
                _pcmBuffer.RemoveRange(0, 16000);

                // Generate speaker embedding
                var emb = SpeakerEmbedder.Embed(oneSecond);

                // Pass embedding to debugging callback
                _onVoiceEmbedding?.Invoke(emb);

                // Handle voice enrollment if active
                if (VoiceEnrollmentManager.IsEnrolling)
                {
                    VoiceEnrollmentManager.ProcessVoiceSample(emb);
                }
                else
                {
                    // Normal speaker identification
                    var match = SpeakerIdentifier.Identify(emb);
                    if (match != null)
                        _onSpeakerMatch?.Invoke(match.Value.name, match.Value.score);
                    else
                        _onSpeakerMatch?.Invoke("Unknown", 0f);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VoiceProcessor.ProcessAudio failed: {ex.Message}");
        }
    }
}
