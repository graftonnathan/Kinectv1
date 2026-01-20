# Audio Pipeline Simplification - Implementation Complete

## Overview

The audio pipeline has been refactored to provide a clean, unified architecture for handling multiple audio sources (Local Mic, Discord, WebRTC). This document describes the implemented solution.

## Architecture

```
???????????????????????????????????????????????????????????????????????????
?                          AUDIO SOURCES                                  ?
???????????????????????????????????????????????????????????????????????????
?  LocalMicSource ?  DiscordAudioSource ?     WebRtcAudioSource           ?
?  (NAudio)       ?  (Discord.Net)      ?     (SIPSorcery)                ?
?  16kHz mono     ?  48k stereo?16k     ?     8kHz?16kHz                  ?
???????????????????????????????????????????????????????????????????????????
         ?                   ?                         ?
         ???????????????????????????????????????????????
                             ?
                             ?
                    ??????????????????????
                    ?   AudioManager     ?
                    ?   (Singleton)      ?
                    ?                    ?
                    ? - Mode switching   ?
                    ? - Source lifecycle ?
                    ? - RMS routing      ?
                    ??????????????????????
                             ?
                             ? NormalizedAudioFrame
                    ??????????????????????
                    ?  VoiceRecognizer   ?
                    ?                    ?
                    ? - Single entry     ?
                    ? - VAD logic        ?
                    ? - Vosk STT         ?
                    ??????????????????????
                             ?
                             ?
                       OnTranscription
```

## Components

### Phase 1: NormalizedAudioFrame ?

**File**: `Services/Voice/NormalizedAudioFrame.cs`

Standard audio frame format for all sources:
- PCM16, 16kHz, mono, byte[]
- Pre-computed RMS (0-10000 scale)
- `AudioSourceType` enum (replaces string prefixes)
- Factory method for easy creation

```csharp
public readonly struct NormalizedAudioFrame
{
    public byte[] Pcm16 { get; }
    public int Length { get; }
    public float Rms { get; }
    public AudioSourceType Source { get; }
    public string SourceId { get; }
    
    public static NormalizedAudioFrame Create(byte[] pcm16, int length, AudioSourceType source, string sourceId = null);
    public static float ComputeRms(byte[] buffer, int length);
}
```

### Phase 2: IAudioSource Interface ?

**File**: `Services/Voice/IAudioSource.cs`

Unified interface for all audio sources:

```csharp
public interface IAudioSource : IDisposable
{
    AudioSourceType SourceType { get; }
    bool IsEnabled { get; }
    AudioSourceState State { get; }
    
    event Action<NormalizedAudioFrame> OnAudioFrame;
    event Action<float> OnRmsLevel;
    event Action<AudioSourceState> OnStateChanged;
    event Action<string> OnLog;
    
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    void SetEnabled(bool enabled);
}
```

### Phase 2: Source Implementations ?

| File | Source | Input | Output |
|------|--------|-------|--------|
| `LocalMicSource.cs` | NAudio WaveInEvent | 16kHz mono | NormalizedAudioFrame |
| `WebRtcAudioSource.cs` | WebRtcAudioTransport | 8kHz PCMU | NormalizedAudioFrame (upsampled) |
| `DiscordAudioSource.cs` | Discord.Net | 48kHz stereo | NormalizedAudioFrame (downsampled) |

### Phase 3: Single Entry Point ?

**File**: `Services/Voice/VoiceRecognizer.cs`

```csharp
// Preferred entry point
public static void ProcessAudio(NormalizedAudioFrame frame);

// Legacy wrapper (marked obsolete)
[Obsolete("Use ProcessAudio(NormalizedAudioFrame) instead")]
public static void ProcessExternalAudio(byte[] pcm, int length, string source = null);
```

Key improvements:
- Single `ConcurrentQueue<NormalizedAudioFrame>` for all sources
- Enum-based source detection: `frame.Source.IsExternal()`
- Consolidated RMS computation
- Cleaner VAD logic
- Added `OnWebRtcRmsLevel` event for UI meters

### Phase 4: AudioManager ?

**File**: `Services/Voice/AudioManager.cs`

Centralized audio coordination:

```csharp
public sealed class AudioManager : IDisposable
{
    public static AudioManager Instance { get; }
    
    public AudioSourceType ActiveSource { get; }
    
    public event Action<NormalizedAudioFrame> OnAudioFrame;
    public event Action<AudioSourceType, float> OnRmsLevel;
    public event Action<AudioSourceType, AudioSourceState> OnSourceStateChanged;
    
    public Task SetActiveSourceAsync(AudioSourceType sourceType);
    public Task StartAsync(CancellationToken ct = default);
    public Task StopAsync();
}
```

## Cleanup Completed ?

### Removed Legacy Code

1. **Mumble aliases removed from VoiceRecognizer**:
   - `SetMumbleInputEnabled()` - use `SetWebRtcInputEnabled()` instead
   - `IsMumbleInputEnabled()` - use `IsWebRtcInputEnabled()` instead

2. **Mumble references removed from MainWindow**:
   - `ApplyAudioMode()` now uses `SetWebRtcInputEnabled()` directly

3. **Legacy AudioUtils methods marked obsolete**:
   - `IsVoiceActive()` - prefer `NormalizedAudioFrame.Rms` with VAD
   - `ResampleMono48kTo16kFromBytes()` - removed (was for Mumble)

4. **Duplicate helper methods consolidated**:
   - `Upsample8kTo16k()` - now in `WebRtcAudioSource`
   - `ShortsToBytes()` - now in `WebRtcAudioSource`
   - `FloatsToPcm16Bytes()` - still in MainWindow (used by WebRtcSttWorker)
   - `ComputeRmsFromPcm16()` - still in MainWindow (used for WebRTC UI meter)

### VoiceRecognizer Event Model

| Event | Type | Purpose |
|-------|------|---------|
| `OnTranscription` | event | Final transcription text |
| `OnPartialTranscription` | event | In-progress transcription |
| `OnRmsLevel` | event | Local mic RMS (0-10000) |
| `OnDiscordRmsLevel` | Action | Discord RMS (backward compat) |
| `OnWebRtcRmsLevel` | event | WebRTC RMS (0-10000) |
| `OnVoiceEmbedding` | event | Speaker embedding vector |

Note: `OnDiscordRmsLevel` is a public Action (not event) for backward compatibility with `DiscordNetBotManager` which invokes it directly.

## AudioSourceType Enum

```csharp
public enum AudioSourceType
{
    LocalMic,   // Local microphone input
    Discord,    // Discord voice channel
    WebRtc,     // Browser-based WebRTC
    Preroll,    // Replayed preroll frames
    Unknown     // Unspecified source
}
```

Extension methods:
```csharp
public static bool IsExternal(this AudioSourceType source);  // Discord, WebRTC = true
public static AudioSourceType FromLegacyPrefix(string source);  // "webrtc:client" ? WebRtc
```

## Migration Guide

### Before (Legacy)
```csharp
// String-based source detection
VoiceRecognizer.ProcessExternalAudio(pcm, length, "webrtc:client-id");

// Multiple flags
VoiceRecognizer.SetMicrophoneInputEnabled(true);
VoiceRecognizer.SetDiscordInputEnabled(false);
VoiceRecognizer.SetMumbleInputEnabled(false);  // REMOVED
```

### After (New)
```csharp
// Type-safe frame creation
var frame = NormalizedAudioFrame.Create(pcm, length, AudioSourceType.WebRtc, "client-id");
VoiceRecognizer.ProcessAudio(frame);

// Centralized mode management
await AudioManager.Instance.SetActiveSourceAsync(AudioSourceType.WebRtc);
```

## File Summary

| File | Status | Purpose |
|------|--------|---------|
| `Services/Voice/NormalizedAudioFrame.cs` | ? | Standard frame type + RMS |
| `Services/Voice/IAudioSource.cs` | ? | Interface + base class |
| `Services/Voice/LocalMicSource.cs` | ? | NAudio mic wrapper |
| `Services/Voice/WebRtcAudioSource.cs` | ? | WebRTC wrapper + upsampling |
| `Services/Voice/DiscordAudioSource.cs` | ? | Discord bridge |
| `Services/Voice/AudioManager.cs` | ? | Centralized coordinator |
| `Services/Voice/VoiceRecognizer.cs` | ? | Cleaned up STT processor |
| `Services/Audio/AudioUtils.cs` | ? | Legacy methods marked obsolete |
| `Settings/UI/MainWindow.xaml.cs` | ? | Mumble refs removed |

## Benefits Achieved

| Metric | Before | After |
|--------|--------|-------|
| RMS computation locations | 3+ | 1 (`NormalizedAudioFrame.ComputeRms`) |
| Source detection method | String parsing | Enum comparison |
| Entry points to VoiceRecognizer | 3 | 1 (`ProcessAudio`) |
| Legacy Mumble references | Multiple | 0 |
| Obsolete method warnings | 0 | Clear deprecation path |

## Future Enhancements

1. **Full MainWindow Integration**: Replace remaining audio handling in MainWindow with AudioManager calls
2. **Testing**: Add unit tests for IAudioSource implementations
3. **Remove WebRtcSttWorker**: Migrate to use `WebRtcAudioSource` directly
4. **Metrics**: Add latency tracking per source
