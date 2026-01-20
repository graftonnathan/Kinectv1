# ASR VAD & Preroll Pipeline

## Architecture Overview

All audio sources now use a unified pipeline through `AudioManager` and `VoiceRecognizer`:

```
??????????????????????????????????????????????????????????????????????????
?                      IAudioSource Implementations                       ?
???????????????????????????????????????????????????????????????????????????
? LocalMicSource  ? DiscordAudioSource ? WebRtcAudioSource                ?
? 16kHz mono      ? 48k?16k resample   ? 8k?16k upsample                  ?
???????????????????????????????????????????????????????????????????????????
         ?                  ?                        ?
         ?????????????????????????????????????????????
                            ? NormalizedAudioFrame
                            ?
                   ???????????????????????
                   ?   VoiceRecognizer   ?
                   ?                     ?
                   ? ProcessAudio(frame) ?
                   ? - VAD (mic only)    ?
                   ? - Preroll buffer    ?
                   ? - Vosk STT          ?
                   ???????????????????????
                             ?
                             ?
                     OnTranscription event
```

## Components

| Component | Responsibility |
|-----------|----------------|
| `IAudioSource` | Interface for all audio sources |
| `LocalMicSource` | NAudio mic capture, 16kHz mono output |
| `DiscordAudioSource` | Bridge for Discord audio (48k?16k) |
| `WebRtcAudioSource` | WebRTC transport wrapper (8k?16k) |
| `AudioManager` | Source coordination and mode switching |
| `NormalizedAudioFrame` | Standard frame format with RMS |
| `VoiceRecognizer` | VAD, preroll, Vosk STT |
| `SpeakerEmbedder` | Voice embeddings (parallel capture) |
| `TtsPlaybackController` | Barge-in coordination |

## Audio Sources

| Source | Sample Rate | Format | Resampling |
|--------|-------------|--------|------------|
| LocalMicSource | 16kHz | PCM16 mono | None (native) |
| DiscordAudioSource | 48kHz stereo ? 16kHz mono | Via `DiscordAudioProcessor` |
| WebRtcAudioSource | 8kHz PCMU ? 16kHz mono | Linear interpolation |

## NormalizedAudioFrame

All sources output `NormalizedAudioFrame`:

```csharp
public readonly struct NormalizedAudioFrame
{
    public byte[] Pcm16 { get; }        // PCM16 little-endian mono @ 16kHz
    public int Length { get; }          // Valid bytes
    public float Rms { get; }           // Pre-computed RMS (0-10000)
    public AudioSourceType Source { get; }  // LocalMic, Discord, WebRtc
    public string SourceId { get; }     // Optional identifier
}
```

## Single Entry Point

```csharp
// Preferred entry point (type-safe)
VoiceRecognizer.ProcessAudio(NormalizedAudioFrame frame);

// Legacy wrapper (backward compatible)
VoiceRecognizer.ProcessExternalAudio(byte[] pcm, int length, string source);
```

## VAD Logic (Local Mic Only)

External sources (Discord, WebRTC) bypass VAD and feed directly to Vosk.

| Variable | Default | Meaning |
|----------|---------|---------|
| `_vadRmsThreshold` | from `Audio.VoiceThreshold` | RMS threshold (0-10000 scale) |
| `_vadDebounceMs` | 30ms | Minimum loud frames before ACTIVE |
| `_vadSilenceMs` | 800ms | Silent frames before flush |

### Frame-Based VAD

VAD uses frame counting (not wall-clock time) to handle queue backup:

```csharp
int silenceFramesNeeded = _vadSilenceMs / frameDurationMs;
int debounceFramesNeeded = _vadDebounceMs / frameDurationMs;

if (rms >= _vadRmsThreshold)
{
    _consecutiveLoudFrames++;
    if (_consecutiveLoudFrames >= debounceFramesNeeded)
        _speechActive = true;
}
else
{
    _consecutiveSilentFrames++;
    if (_consecutiveSilentFrames >= silenceFramesNeeded)
        _speechActive = false;
}
```

## Preroll

| Aspect | Value |
|--------|-------|
| Frame size | 20 ms |
| Stored frames | 30 (600 ms) |
| Purpose | Recover initial phonemes before VAD threshold crossed |

## Latency Optimizations

The following optimizations reduce end-to-end latency:

| Parameter | Old Value | New Value | Impact |
|-----------|-----------|-----------|--------|
| Frame size | 50ms | 20ms | Faster response to speech start |
| Preroll buffer | 1200ms | 600ms | Less data to replay on VAD activation |
| VAD debounce | 50ms | 30ms | Faster speech detection |
| VAD silence timeout | 1500ms | 800ms | Faster final transcription |
| Audio processor idle | Thread.Sleep(2) | SpinWait | Lower latency when queue empty |
| JSON parsing | JObject.Parse | String parsing | Reduced CPU overhead per result |

## Processing Flow

### Local Mic (with VAD)
```
NormalizedAudioFrame ? ProcessAudioInternal
  ?? RMS ? OnRmsLevel (UI meter)
  ?? UpdateVadFromRms
  ?? if !ACTIVE: StorePreRoll
  ?? if ACTIVE && wasInactive: ReplayPreRoll
  ?? if ACTIVE: FeedRecognizer + SpeakerEmbedder
  ?? if wasActive && !ACTIVE: FlushFinal
```

### External Sources (no VAD)
```
NormalizedAudioFrame ? ProcessAudioInternal
  ?? RMS ? OnDiscordRmsLevel (UI meter)
  ?? SpeakerEmbedder.AddPcm16
  ?? FeedRecognizer (immediate)
```

## Barge-In Interaction

| Barge-In Enabled | Behavior |
|------------------|----------|
| true | VAD rising edge during TTS cancels playback |
| false | Frames buffered in preroll while TTS active |

## Partial & Final Emission

| State | Action |
|-------|--------|
| Partial (ACTIVE) | `rec.PartialResult()` ? `OnPartialTranscription` |
| Final (accepted) | `rec.Result()` ? `OnTranscription` |
| Forced final | On ACTIVE?INACTIVE transition |

### External Audio Finals

For external sources, transcription emits immediately on Vosk accept:
```csharp
if (frame.Source.IsExternal())
{
    OnTranscription?.Invoke(text);  // Immediate
}
```

## Speaker Embedding Path

- All ACTIVE frames (and preroll replay) ? `SpeakerEmbedder.AddPcm16`
- Embedding emission independent of TTS state

## Threading

| Thread | Work |
|--------|------|
| UI / main | Startup, mode switching |
| WaveIn callback | Mic frames ? `ProcessAudioInternal` (direct, no queue) |
| Audio processor task | Dequeue and process external frames |
| WebRTC/Discord callbacks | Enqueue to audio processor |
| Vosk internal | Recognition JSON generation |

## Tuning Parameters

These settings can be adjusted in `settings.json` under the `asr` section:

```json
{
  "asr": {
    "vadSilenceTimeoutMs": 800,
    "vadDebounceTimeoutMs": 30,
    "bargeInEnabled": true
  },
  "audio": {
    "voiceThreshold": 0.02
  }
}
```

| Setting | Range | Default | Description |
|---------|-------|---------|-------------|
| `vadSilenceTimeoutMs` | 100-5000 | 800 | Time of silence before final emit |
| `vadDebounceTimeoutMs` | 10-2000 | 30 | Time of loud frames before activation |
| `voiceThreshold` | 0-1 | 0.02 | Normalized RMS threshold for VAD |

## API Reference

### AudioManager
```csharp
AudioManager.Instance.SetActiveSourceAsync(AudioSourceType.WebRtc);
AudioManager.Instance.ActiveSource;
AudioManager.Instance.OnAudioFrame += frame => { };
AudioManager.Instance.OnRmsLevel += (source, rms) => { };
```

### VoiceRecognizer
```csharp
VoiceRecognizer.ProcessAudio(NormalizedAudioFrame frame);
VoiceRecognizer.SetMicrophoneInputEnabled(bool enabled);
VoiceRecognizer.SetDiscordInputEnabled(bool enabled);
VoiceRecognizer.SetWebRtcInputEnabled(bool enabled);
```

### Query State
```csharp
bool ready = VoiceRecognizer.IsReady();
var (queueSize, isProcessing) = VoiceRecognizer.GetExternalAudioStats();
```

## Minimal Usage Example

```csharp
// Start with AudioManager (recommended)
await AudioManager.Instance.StartAsync();
AudioManager.Instance.OnAudioFrame += frame => 
    VoiceRecognizer.ProcessAudio(frame);

// Or direct VoiceRecognizer usage (legacy)
VoiceRecognizer.Start(modelPath);
VoiceRecognizer.OnTranscription += text => Console.WriteLine($"FINAL: {text}");
```

## Related Documentation

- [Audio Pipeline Simplification](AUDIO_PIPELINE_SIMPLIFICATION.md) - Full architecture
- [WebRTC Audio Bridge](WEBRTC_AUDIO_BRIDGE.md) - WebRTC transport
- [Discord Audio Pipeline](Discord_Audio_Pipeline_Summary.md) - Discord processing
- [TTS Preemption](TTS_PREEMPTION_AND_BARGE_IN.md) - Barge-in behavior
