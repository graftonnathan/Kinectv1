# TTS Preemption & Barge?In

## Overview
Local TTS playback is governed by a lightweight controller + synthesis service:
- `TtsService` – text normalization, IPA generation, tokenization, ONNX inference, trimming.
- `TtsPlaybackController` – single active utterance, atomic preemption, cancellation events.
- `VoiceRecognizer` – VAD + preroll; optionally cancels active TTS (barge?in) or suppresses ASR.

## Lifecycle (One Utterance)
```
Call SpeakWithPreemptionAsync(text)
  ?? TtsPlaybackController.StartUtterance()
       1. Cancel previous CTS (if any) + wait brief for disposal
       2. Allocate new CancellationTokenSource
       3. Fire OnPlaybackStart (UI hook)
       4. Invoke LocalSpeakAsync(text, speaker, ct)
            a. GenerateAudioDataAsync -> GenerateAudioInternal
            b. Style + speed tensors populated
            c. ONNX inference produces raw waveform
            d. Optional trimming (leading/trailing) unless grace window
            e. Local gain (volume scaling) applied
            f. AudioDeviceManager.PlayLocallyAsync
       5. On completion / cancellation: fire OnPlaybackStop
```

## Preemption Rules
| Scenario | Result |
|----------|--------|
| New request while prior playing | Prior CTS canceled immediately; new starts after synthesis completes |
| New request while prior still synthesizing | Prior CTS canceled; ongoing generation abandoned ASAP |
| External cancel (`CancelCurrentLocalTts`) | Same as preemption; sets grace timestamp |
| App settings disable TTS mid?utterance | Current completes if not canceled; new requests rejected |

## Grace Window
After any external / barge?in cancel a short grace period (~600 ms) is active:
- Leading trimming disabled (or lightened) to avoid clipped initial phonemes.
- A minimal padding (?15 ms) may be inserted at start.

## Barge?In Logic
| `AsrSettings.BargeInEnabled` | Behavior |
|------------------------------|----------|
| true | First VAD activation (INACTIVE?ACTIVE) during TTS calls `TtsPlaybackController.CancelCurrent()` + marks external cancel time |
| false | Recognition suppressed while TTS plays (frames stored in preroll only) |

## Events
| Event | Raised By | Purpose |
|-------|-----------|---------|
| `TtsService.OnTtsSpeakingStarted` | After synthesis begins | UI state / LED |
| `TtsService.OnTtsSpeakingFinished` | After playback completes (not on cancel) | UI reset |
| `TtsService.OnTtsError(string)` | Any unrecoverable step failure | Surface diagnostics |
| `TtsPlaybackController.OnPlaybackStart` | Just before task scheduled | Global playback indicators |
| `TtsPlaybackController.OnPlaybackStop` | After utterance finalizes | Cleanup / auto?resume ASR |

## Cancellation Sources
| Source | Method | Notes |
|--------|--------|-------|
| Preemption (next utterance) | Controller swap CTS | Controlled internally |
| UI / external | `CancelCurrentLocalTts()` | Marks grace timestamp |
| Barge?in (speech) | VoiceRecognizer VAD rising edge | Conditional on setting |
| App Shutdown | Global CTS or explicit cancel | Should happen early in exit sequence |

## Error Conditions & Recovery
| Failure | Effect | Recovery Path |
|---------|--------|---------------|
| Model file missing | Initialization fails | `RecreateSessionFromSettings()` after correcting path |
| IPA timeout / null | Segment returns empty; error event | Caller gets `false`; next utterance allowed |
| Style vector fail | Abort segment | Ensure voice style file exists (.bin) |
| ONNX inference exception | Abort utterance; event fired | Session may need recreation |

## Threading / Locks
| Lock | Protects | Notes |
|------|----------|-------|
| `_speakLock` | Active CTS swap | VERY small critical section |
| `_lock` | Session init / recreation | Avoids double model load |
| `_reuseInputs` | Shared tensor list + style/speed tensors | Inference hot path serialization |

## Performance Characteristics
- Single ONNX session reused (no re-load per utterance).
- Style & speed tensors reused; only input ID tensor allocated per call.
- Preemption discards unneeded synthesis early reducing wasted CPU.

## Extensibility Points
| Need | Suggested Hook |
|------|----------------|
| Streaming partial playback | Replace `LocalSpeakAsync` with segment iterator + interleave playback |
| Multi?output (Discord + local) | Wrap controller callback to branch before playback |
| Dynamic prosody | Extend style tensor mapping or add additional conditioning input |

## Minimal Example
```csharp
await TtsService.SpeakWithPreemptionAsync("Hello there.");
// Start speaking again quickly – first call cancels
await TtsService.SpeakWithPreemptionAsync("New thought...");
```

## Checklist (Debugging)
- Is `TtsService.IsEnabled()` true?
- Does `_modelPath` exist?
- Voice name in settings matches a `.bin` file?
- IPA command present (`espeak-ng.exe` found)?
- Any recent `CANCEL` log preceding clipped audio? (Grace mode expected)
