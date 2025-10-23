# Cancellation Flow (ASR + TTS)

## Sources of Cancellation
| Source | Target | Mechanism |
|--------|--------|-----------|
| New TTS request | Previous utterance | `TtsPlaybackController` swaps CTS and calls `Cancel()` |
| User action (UI / hotkey) | Current TTS | `TtsService.CancelCurrentLocalTts()` |
| Barge?in (speech) | Current TTS | VoiceRecognizer detects VAD activation & invokes `TtsPlaybackController.CancelCurrent()` |
| App shutdown | All tasks | Global CTS + explicit cancel methods |

## Flow Diagram
```
User / System Event
   ??(speak)? StartUtterance -> alloc CTS -> run LocalSpeakAsync
   ??(new speak)? Cancel old CTS ? allocate new
   ??(barge speech)? CancelCurrent + mark grace timestamp
   ??(UI cancel)? CancelCurrentLocalTts() + mark grace
   ??(shutdown)? CancelCurrent + dispose session (optional)
```

## Grace Timestamp
Stored in `_lastCancel`; read by `RecentlyCancelled()` to modify trimming & apply small lead pad.

## Ordering Guarantees
- Controller ensures only one active CTS reference recognized as current.
- Old playback tasks allowed to complete cleanup silently after cancellation.

## Error Isolation
Cancellation triggers `OperationCanceledException` swallowed inside service path (returns `false` to caller). No error event raised for clean cancels.

## Race Protection
| Risk | Mitigation |
|------|-----------|
| Double cancel dispose | CTS captured then disposed in guarded block |
| New utterance reusing old style tensor mid-inference | Lock around `_reuseInputs` during ONNX Run |
| Late event firing after cancel | Controller suppresses duplicate OnPlaybackStop by utterance id check |

## Debug Tips
- Look for `[TTS][CANCEL]` logs (diagnostics enabled) to correlate with clipped audio.
- If cancels not respected quickly, verify CPU contention in model inference region.

