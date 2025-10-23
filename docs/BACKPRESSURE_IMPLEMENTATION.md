# Backpressure Resilience Implementation

This document summarizes backpressure controls for audio and LLM‑adjacent pipelines.

## VoiceRecognizer Audio Pipelines
- External audio queue (Discord/system) bounded (50 items)
- Per-user Discord queues bounded (25 items each)
- Drop policy: oldest first
- Metrics: total external drops, per‑user drops
- Preroll buffer (10 * 50ms frames) retained outside bounded queues

### Barge‑In Interaction
When `Asr.BargeInEnabled` is false, incoming frames during local TTS playback are stored only in preroll (not fed to recognizer) and do not contribute to queue pressure. When true, first VAD activation cancels TTS (via controller) and normal processing resumes immediately.

## Local TTS Playback
Previously a multi‑item TTS job queue existed. Now local synthesis/playback uses `TtsPlaybackController`:
- Single active utterance (implicit queue length = 1)
- New request immediately cancels prior (no accumulation)
- No backpressure counters required (work is discarded early)
- Cancellation marks grace window for trimming logic (prevents clipped starts)

## Discord TTS (If Enabled)
If remote / Discord TTS queuing persists, retain bounded channel with drop‑oldest policy (see actual implementation in Discord manager). Otherwise, documentation of multi‑slot queue is legacy.

## Configuration Constants (Representative)
```csharp
const int MAX_EXTERNAL_QUEUE_SIZE = 50;
const int MAX_PER_USER_QUEUE_SIZE = 25;
// Local TTS: implicit single slot via controller
```

## Telemetry
Include queue depth + drop counters plus (optionally) active TTS flag for correlation.

## Expected Behavior
| Scenario | Result |
|----------|--------|
| Sustained audio burst | Oldest frames dropped, recent preserved |
| Rapid successive TTS calls | Only last utterance synthesized/played |
| Barge‑in disabled, user speaks during TTS | Speech buffered in preroll; recognized after playback |
| Barge‑in enabled, user speaks | Active TTS cancelled instantly |

## Benefits
- Memory bounded
- Latency predictable
- Cancellation prevents wasted synthesis
- Simple mental model (no deep queue tuning for TTS)