# Conditional TTS Output Routing

## Overview

The TTS pipeline conditionally outputs audio based on which input source is active. This provides users with precise control over where their AI responses are heard.

## Audio Input Architecture

The application uses a unified `AudioManager` to coordinate audio sources:

```
???????????????????????????????????????????????????????????????????????????
?                        AudioManager (Singleton)                         ?
???????????????????????????????????????????????????????????????????????????
?  LocalMicSource ?  DiscordAudioSource ?     WebRtcAudioSource           ?
?  ?? NAudio      ?  ?? Discord.Net     ?     ?? SIPSorcery               ?
???????????????????????????????????????????????????????????????????????????
         ?                   ?                         ?
         ???????????????????????????????????????????????
                             ? NormalizedAudioFrame
                             ?
                     VoiceRecognizer ? Vosk STT ? LLM
```

## Audio Input Modes

The application supports three mutually exclusive audio input modes:

| Mode | Source Class | Description |
|------|--------------|-------------|
| **Local Mic** | `LocalMicSource` | Uses local microphone, TTS plays on local speakers |
| **Discord** | `DiscordAudioSource` | Uses Discord voice channel audio, TTS plays in Discord |
| **WebRTC** | `WebRtcAudioSource` | Uses browser-based LAN audio bridge, TTS plays on local speakers |

## Routing Logic

```csharp
// AudioManager handles mode switching
await AudioManager.Instance.SetActiveSourceAsync(AudioSourceType.WebRtc);

// TTS routing based on active source
var activeSource = AudioManager.Instance.ActiveSource;
var speakLocal = activeSource == AudioSourceType.LocalMic || activeSource == AudioSourceType.WebRtc;
var speakDiscord = activeSource == AudioSourceType.Discord && DiscordNetBotManager.IsInVoiceChannel;

if (speakLocal)
{
    TtsService.QueueSentenceForStreaming(sentence, voice);
}

if (speakDiscord)
{
    await DiscordNetBotManager.SendTtsToDiscordAsync(sentence, voice);
}
```

## Output Scenarios

### ?? Local Microphone Mode
- **Input**: `LocalMicSource` via NAudio (16kHz mono)
- **Output**: Local speakers via `TtsPlaybackController`
- **Use case**: Solo development, testing, local usage

### ?? Discord Mode  
- **Input**: `DiscordAudioSource` (48kHz stereo ? 16kHz mono)
- **Output**: Discord voice channel via `DiscordNetBotManager`
- **Use case**: Gaming, streaming, Discord-based interaction

### ?? WebRTC Mode
- **Input**: `WebRtcAudioSource` (8kHz PCMU ? 16kHz mono)
- **Output**: Local speakers (WebRTC TTS return path is future enhancement)
- **Use case**: Mobile device input, cross-device interaction

## Technical Implementation

### AudioManager Integration

```csharp
// Mode switching via AudioManager
await AudioManager.Instance.SetActiveSourceAsync(AudioSourceType.WebRtc);

// Query current state
var activeSource = AudioManager.Instance.ActiveSource;
var webRtcSource = AudioManager.Instance.WebRtcSource;
var discordSource = AudioManager.Instance.DiscordSource;
```

### State Tracking (Legacy - still supported)
```csharp
private bool _isMicrophoneInputEnabled = true;
private bool _isDiscordInputEnabled = false;
private bool _isWebRtcInputEnabled = false;
private AudioInMode _currentAudioMode = AudioInMode.LocalMic;
```

### Mode Switching
When switching modes, the `AudioManager`:
1. Disables previous source
2. Stops previous source (except Discord which is externally managed)
3. Enables and starts the new source
4. Updates `VoiceRecognizer` flags for compatibility

```csharp
public async Task SetActiveSourceAsync(AudioSourceType sourceType)
{
    _activeSource = sourceType;
    
    // Disable other sources
    foreach (var source in _sources.Values.Where(s => s.SourceType != sourceType))
        source.SetEnabled(false);
    
    // Enable and start new source
    var newSource = _sources[sourceType];
    newSource.SetEnabled(true);
    await newSource.StartAsync();
    
    // Sync with VoiceRecognizer
    VoiceRecognizer.SetMicrophoneInputEnabled(sourceType == AudioSourceType.LocalMic);
    VoiceRecognizer.SetDiscordInputEnabled(sourceType == AudioSourceType.Discord);
    VoiceRecognizer.SetWebRtcInputEnabled(sourceType == AudioSourceType.WebRtc);
}
```

### Persistence
Mode selection is persisted to settings:
```json
{
  "app": {
    "inputMode": "webrtc"  // "localmic" | "discordvoice" | "webrtc"
  }
}
```

## Playback Pipeline (Controller Integration)

Local playback is mediated by `TtsPlaybackController`:

- Guarantees a single active utterance
- New requests preempt the previous one atomically
- External cancels (UI / barge-in) call `CancelCurrent()`
- `TtsService.SpeakWithPreemptionAsync` is the canonical entry point

### Barge-In Interaction

`AsrSettings.BargeInEnabled` determines ASR/TTS interplay:

| Setting | Behavior |
|---------|----------|
| `true` | VAD activation during TTS cancels current utterance (barge-in) |
| `false` | ASR suppressed while TTS plays; frames buffered as preroll |

## Console Output Examples

### Local Microphone Mode
```
[AudioManager] Switching to LocalMic
[LocalMic] Started on device Microphone (USB Audio)
[TTS Stream] Sentence ready (42 chars), local=True, discord=False
```

### Discord Mode
```
[AudioManager] Switching to Discord
[DiscordSource] Started (waiting for audio from Discord bot)
[TTS Stream] Sentence ready (42 chars), local=False, discord=True
```

### WebRTC Mode
```
[AudioManager] Switching to WebRtc
[WebRtcSource] Started. Join URL: http://192.168.1.34:8787/
[WebRTC] State changed: Listening
[TTS Stream] Sentence ready (42 chars), local=True, discord=False
```

## Error Handling

- Missing voice/style ? raises `OnTtsError` and aborts
- Model/session init failure ? disables synthesis until `RecreateSessionFromSettings()` succeeds
- Discord not connected ? Discord TTS silently skipped
- WebRTC not connected ? Falls back to local speakers

## Related Documentation

- [Audio Pipeline Simplification](AUDIO_PIPELINE_SIMPLIFICATION.md) - Full architecture details
- [WebRTC Audio Bridge](WEBRTC_AUDIO_BRIDGE.md) - WebRTC transport details
- [Discord Audio Pipeline](Discord_Audio_Pipeline_Summary.md) - Discord voice processing
- [TTS Preemption](TTS_PREEMPTION_AND_BARGE_IN.md) - Barge-in behavior
- [Settings](settings.md) - Configuration options