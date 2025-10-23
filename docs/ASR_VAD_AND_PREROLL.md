# ASR VAD & Preroll Pipeline

## Components
| Component | Responsibility |
|-----------|----------------|
| `VoiceRecognizer` | Mic capture, external (Discord) audio ingestion, VAD state, preroll management, feeding Vosk recognizer |
| `VoskRecognizer` | Offline speech recognition (partial + final JSON results) |
| `SpeakerEmbedder` | Voice embeddings (parallel capture) |
| `TtsPlaybackController` | Used indirectly for barge?in logic (cancel on speech) |

## Capture Flow (Microphone)
```
WaveInEvent (50ms mono 16k PCM) ? ProcessFrame
  RMS ? VAD update
  if INACTIVE: store frame in circular preroll (up to 500ms)
  if ACTIVATED: replay preroll ? feed frames to Vosk + speaker embedder
  if transition ACTIVE?INACTIVE with silence timeout: flush final
```

## External / Discord Audio Path
- Arrives as queued PCM (already resampled + conditioned elsewhere)
- Processed only when Discord input enabled
- Shares same VAD logic and final/partial emission

## VAD Logic
| Variable | Meaning |
|----------|---------|
| `_vadRmsThreshold` | Dynamic RMS threshold (`Audio.VoiceThreshold` mapped to 0..10000 scale) |
| `_vadDebounceMs` | Minimum sustained time above threshold to enter ACTIVE |
| `_vadSilenceMs` | Time below threshold to exit ACTIVE & flush final |

Activation condition:
```
rms >= threshold continuously for >= debounce window
```
Deactivation:
```
(now - _lastAbove) >= _vadSilenceMs
```

## Preroll
| Aspect | Value |
|--------|-------|
| Frame size | 50 ms |
| Stored frames | 10 (500 ms) |
| Purpose | Recover initial phonemes that occur before VAD crosses threshold |

## Partial & Final Emission
| State | Action |
|-------|--------|
| Partial (ACTIVE) | Periodic `rec.PartialResult()` decoded ? `OnPartialTranscription` |
| Final (waveform accepted) | `rec.Result()` (accepted flag) ? `OnTranscription` |
| Forced final | On ACTIVE?INACTIVE or manual flush after silence |

## Barge?In Interaction
| Barge?In Enabled | Behavior |
|------------------|----------|
| true | VAD rising edge during local TTS cancels playback (controller cancel + grace timestamp) |
| false | Frames buffered in preroll only while TTS active; no recognition fed to Vosk |

## Speaker Embedding Path
- All ACTIVE (and preroll replay) frames passed to `SpeakerEmbedder.AddPcm16`
- Embedding emission independent of TTS state (barge?in does not pause buffering)

## Threading
| Thread | Work |
|--------|------|
| UI / main | Startup / enabling devices |
| WaveIn callback | Mic frames ? `ProcessFrame` (fast operations only) |
| External queue worker | Dequeues external frames every few ms |
| Vosk internal | Recognition JSON generation |

No blocking operations permitted inside real-time callback (no awaited tasks).

## Failure / Edge Handling
| Case | Handling |
|------|----------|
| Recognizer not loaded | Frames ignored until model ready |
| RMS spikes while mic disabled | Logged once every 100 frames (diagnostic) |
| Long silence while ACTIVE | Silence timeout triggers flush + reset |

## Optimization Points
- Potential: adaptive threshold using noise floor tracking
- Potential: dynamic preroll length based on average attack time
- Potential: streaming partial token diffing (currently whole-partial string)

## Minimal Usage Example
```csharp
VoiceRecognizer.Start(modelPath); // loads model + begins mic (if enabled)
VoiceRecognizer.OnTranscription += text => Console.WriteLine($"FINAL: {text}");
VoiceRecognizer.OnPartialTranscription += part => Console.WriteLine($"PART: {part}");
```
