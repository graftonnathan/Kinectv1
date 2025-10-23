# Discord Voice Audio & TTS Pipeline (Inbound Processing + Outbound Streaming)

## ✅ Current Status
A fully integrated, production‑grade Discord audio subsystem is in place providing:
- Inbound voice capture (other users in the channel) → conditioned → routed into ASR (Vosk/recognizer).
- Outbound TTS synthesis → queued, preemptible streaming into the same Discord voice channel.
- Robust lifecycle management (voice joins, retries, gateway resets, orderly shutdown, single‑instance protection per token).

This document supersedes earlier incremental notes and removes legacy "next steps" guidance that is now obsolete.

---
## 🎧 Inbound Voice Processing (Summary)
Input (Discord Opus decoded PCM from Discord.Net): 48 kHz, 16‑bit, stereo, ~20 ms frames (3840 bytes per read).
Pipeline (implemented in `DiscordAudioProcessor` and invoked via `DiscordNetBotManager.ProcessVoiceData`):
1. Convert s16 → float.
2. Stereo mixdown (L+R)/2.
3. High‑pass filter @ ~80 Hz (rumble/DC removal).
4. RMS measurement & adaptive gain smoothing.
5. Loudness normalization (target ≈ -21.5 dBFS) with gradual gain converge.
6. Soft limiter (≈ -3 dBFS ceiling) for peak protection.
7. Decimation 48 kHz → 16 kHz (3:1) for recognizer (20 ms => 320 samples mono s16).
8. Forward conditioned chunk(s) to ASR if recognizer ready and Discord input enabled.
Telemetry: Per‑chunk raw RMS forwarded to UI via `VoiceRecognizer.OnDiscordRmsLevel`.

---
## 🔊 Outbound Discord TTS Streaming
Outbound synthesis & streaming is handled entirely inside `DiscordNetBotManager` using a **bounded, preemptible queue** plus a **generation guard**.

### High‑Level Flow
```
(UI / Command / Internal Event)
          │
          ▼
SendTtsToDiscordAsync(text, speaker)
          │  (validates: text, TtsService enabled, voice connected)
          ▼
Bounded Channel<TtsJob> (capacity = 1, DropOldest)
          │  (enqueue increments _ttsGeneration, cancels prior active CTS)
          ▼
Background Worker (single reader) ↔ _ttsWorkerRunning flag
          │
          ▼
TtsService.GenerateAudioDataAsync → float[] @ 24 kHz mono
          │
          ▼
Clamp & volume scale (currently fixed 1.0, headroom enforced 0.98)
          │
          ▼
Float → PCM16 (24 kHz mono) -> MemoryStream -> RawSourceWaveStream
          │
          ▼
MediaFoundationResampler → 48 kHz, 16‑bit, stereo
          │
          ▼
20 ms pacing + frame assembly (3840 bytes per Discord frame)
          │
          ▼
Persistent AudioOutStream (CreatePCMStream, 96 kbps, buffer 200 ms)
          │
          ▼
Write/Flush, SetSpeaking(true) before first frame
          │
          ▼
Idle Hold (250 ms) then SetSpeaking(false)
```

### Key Structures / Fields
| Element | Purpose |
|---------|---------|
| `Channel<TtsJob> _ttsChannel` | Capacity 1, `BoundedChannelFullMode.DropOldest` ensures newest request wins under burst load. |
| `int _ttsGeneration` | Monotonic counter; each enqueue increments and tags job. Used to invalidate superseded jobs mid‑stream. |
| `CancellationTokenSource _currentTtsCts` | Swapped atomically per new job; previous CTS cancelled to preempt. |
| `volatile bool _ttsWorkerRunning` | Prevents launching multiple workers; worker restarts automatically when new items appear after drain. |
| `_speakingHoldCts` | Manages post‑playback idle window before clearing Discord speaking state. |
| `AudioOutStream _discordPcmStream` | Reused output stream (PCM) reducing allocation / handshake cost between utterances. |

### Preemption & Cancellation Rules
A job aborts (returns `false`) if any of:
1. Voice connection lost (`_currentAudioClient == null` or not `Connected`).
2. Synthesis cancelled (`job.Cts` triggered) or produced no samples.
3. `_ttsGeneration` changed (newer job enqueued) – checked before SetSpeaking and inside write loop on every frame.
4. External shutdown / gateway reset path calls `CancelCurrentTts()`.

### Frame Assembly & Timing
- Resampler outputs arbitrary chunk sizes; bytes are buffered into a 3840‑byte carry buffer (20 ms @ 48k stereo 16‑bit).
- A `Stopwatch` enforces near‑real‑time pacing: before each frame write, if we are early vs `frames * 20 ms`, a short delay aligns timing.
- Residual partial frame at end is zero‑padded then written (prevents truncation artifacts).
- Flush attempted after final frame; errors ignored (best‑effort, avoids abort on non‑fatal stream issues).

### Speaking State Handling
- `SetSpeakingAsync(true)` issued after confirming job not superseded; 60 ms settle delay allows Discord gateway state propagation before first audio frame.
- After streaming completes (success, cancellation, or supersede), `SpeakingIdleHoldAsync` starts a 250 ms hold. If no newer generation replaced it during that window, `SetSpeakingAsync(false)` is sent.
- Hold prevents rapid on/off flicker and gives downstream clients smoother presence indicators.

### Return Semantics (`SendTtsToDiscordAsync` Task<bool>)
`true` → At least one frame written and job remained current through completion.
`false` → Any preemption, cancellation, connection loss, or synthesis failure condition.

### Resource Lifecycle & Shutdown Integration
| Event | Action |
|-------|--------|
| Gateway disconnect (`Client_Disconnected`) | Cancels active TTS, closes voice resources, restarts gateway (auto reset). |
| Explicit shutdown (`ShutdownAsync` / `FullShutdownAsync`) | Cancels TTS, completes channel writer (best‑effort), disposes stream. |
| Voice channel leave (`OnVoiceChannelLeft`) | Disposes output stream and input capture streams; future TTS calls short‑circuit. |
| Preemptive enqueue | Cancels previous `_currentTtsCts` and increments generation strictly before worker picks up new job. |

### Interaction With Conditional Routing
Conditional routing logic (documented in `CONDITIONAL_TTS_OUTPUT_ROUTING.md`) only invokes `SendTtsToDiscordAsync` when:
- Discord routing enabled by UI state (Discord input checkbox ON).
- Bot is connected to a voice channel (`IsInVoiceChannel`).
- Global TTS service enabled (`TtsService.IsEnabled()`).
Local playback path (speaker audio) is handled separately by `TtsPlaybackController`; Discord path is independent and non‑blocking.

### Reliability / Safety Considerations
- Bounded channel + DropOldest prevents unbounded memory growth if many messages arrive during synthesis.
- Generation guard avoids race where a superseded job partially emits late frames after a newer request.
- Persistent `AudioOutStream` reduces churn vs recreating streams per utterance (minor latency & GC win).
- Internal try/catch blocks intentionally isolate non‑critical failures (flush, speaking state) from aborting the entire system.

### Potential Future Enhancements
1. Dynamic volume scaling preference (user adjustable Discord output gain).
2. Direct Opus encoding path (bypassing PCM stream) for bandwidth/CPU efficiency.
3. Parallel warm synthesis of next utterance (speculative) if queue gets multi‑slot design.
4. Real‑time underrun detection / frame drift metrics logging.
5. Adaptive pacing using high‑resolution timer instead of millisecond Task.Delay.
6. Optional post‑processing (noise gate, compression) before stereo upmix.

---
## 📂 Key Implementation Files (Updated)
| File | Responsibility |
|------|----------------|
| `DiscordNetBotManager.cs` | Lifecycle, voice connection mgmt, inbound stream hook, outbound preemptible TTS streaming. |
| `DiscordAudioProcessor.cs` | Inbound PCM conditioning (48k stereo → 16k mono with gain, filtering, limiting). |
| `VoiceRecognizer.cs` | ASR integration, readiness checks, RMS event surface. |
| `CONDITIONAL_TTS_OUTPUT_ROUTING.md` | Higher‑level routing policy (local vs Discord vs silent). |
| 'DISCORD_TTS_PIPELINE_SUMMARY.md' | This document (summary of current implemented state). |

---
## 🧪 Testing the Pipelines
### Inbound (Recognition) Quick Test
Use existing `!testaudio` command (see `DiscordNetVoiceCommands.cs`) – generates synthetic 48 kHz stereo frame and pushes through processing path.

### Outbound (TTS) Quick Test
`!testtts your text here` → Enqueues TTS job; console will show any `[Discord][TTS]` job errors. Observe Discord client speaking indicator and resulting audio.

### Manual Stress / Edge Cases
| Scenario | Expected Behavior |
|----------|------------------|
| Rapid consecutive `!testtts` | Earlier utterances preempted (only latest plays). |
| Disconnect mid‑utterance | Job returns false; speaking indicator cleared after reconnection logic runs. |
| Gateway hard reset (4006) | Force close voice pipes, clear streams, auto restart, subsequent TTS requires rejoin. |
| Long text synthesis cancel | New enqueue triggers cancellation; partial frames stop promptly. |

---
## 📊 Operational Signals
| Metric / Log | Meaning |
|--------------|---------|
| `[Discord][TTS] job error:` | Non‑fatal exception in current job (result => false). |
| Console join debug `[JoinDBG]` | Voice session acquisition / retry diagnostic path. |
| Speaking flicker absent | Idle hold functioning correctly. |
| RMS level updates in UI | Inbound pipeline delivering normalized levels. |

---
## 🔐 Single Instance Protection
Startup enforces per‑token global mutex (`Global\\DiscordBot_<SHA256(token)>`) preventing multiple processes from causing recurrent 4006 (session invalidation) storms.

---
## ♻️ Summary
You now have a unified Discord audio subsystem:
- Robust inbound conditioning producing ASR‑optimized 16 kHz mono.
- Efficient, cancellable outbound TTS with deterministic preemption.
- Clean lifecycle & recovery on disconnects / restarts.
- Separation of concerns (recognition, playback routing, synthesis, Discord transport).

This document reflects the **current implemented state**; prior provisional "next steps" have been removed as they are fulfilled.