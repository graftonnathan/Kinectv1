# Audio Trimming & Grace Mode

## Goals
- Remove leading / trailing silence without chopping initial phonemes
- Guarantee smooth handoff after barge?in (avoid clipped restarts)

## Trimming Strategy
| Phase | Function | Parameters |
|-------|----------|------------|
| Leading | `TrimLeading` | threshold (`TrimThreshold`), max scan window (`TrimMaxMs`, capped 200ms) |
| Trailing | `TrimTrailing` | threshold, leave tail window (`TrimLeaveMs`), max trim (`TrimMaxMs`) |

Both operate on float waveform (PCM decoded) in sample domain.

### Leading Trim
Linear scan up to max samples:
```
while abs(sample) <= thr AND scanned < maxMsWindow ? advance
copy remainder
```
Skips if threshold <= 0 or array empty.

### Trailing Trim
Reverse scan (bounded by max trim window). On first magnitude > thr, keeps additional `leaveMs` tail then truncates.

## Grace Mode (Barge?In Follow-Up)
Triggered if `RecentlyCancelled()` (cancel timestamp within 600 ms):
- Leading trim bypassed (or extremely conservative)
- 15 ms of silence padding inserted at start to restore natural attack envelope
- Purpose: The previous utterance ended abruptly; avoid immediate consonant loss

## Parameter Sources
| Setting Field | Description |
|---------------|-------------|
| `Tts.TrimThreshold` | Amplitude threshold (linear float) |
| `Tts.TrimLeaveMs` | Tail to retain after signal resumes |
| `Tts.TrimMaxMs` | Max scan / trim window |
| `Tts.MinClausePaddingMs` | Inter / post segment silence injection |

## Segment Padding
If multi?segment synthesis (punctuation based splitting), padding inserted only BETWEEN segments (not after last). If a single short clause, optional end padding still applied.

## Failure Safety
If trimming logic determines nothing can be removed (indices unchanged) original buffer returned (no copies created unless needed).

## Future Enhancements
| Idea | Benefit |
|------|--------|
| Energy-based attack detection | More robust to low?level fricatives |
| ML-based pause classifier | Smarter multi?clause pacing |
| Loudness-normalized trim threshold | Adapt across style vectors |

## Debug Checklist
- Audio clipped early? Confirm not in grace mode just before.
- Long leading silence? Increase `TrimThreshold` or `TrimMaxMs`.
- Soft endings cut short? Increase `TrimLeaveMs`.

