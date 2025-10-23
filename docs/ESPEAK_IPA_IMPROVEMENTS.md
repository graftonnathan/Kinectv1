# eSpeak IPA Path (Current Simplified Implementation)

## Current Approach
`TtsService` performs a single one‑shot `espeak-ng` process invocation per segment when generating IPA:
```csharp
var psi = new ProcessStartInfo {
  FileName = espeakPath,
  Arguments = "--ipa -q -v en-us \"" + sanitized + "\"",
  RedirectStandardOutput = true,
  RedirectStandardError = true,
  UseShellExecute = false,
  CreateNoWindow = true
};
```
- Timeout enforced via `IpaOneShotTimeoutMs` (default 1500 ms)
- No persistent worker / queue / retry layers
- Output sanitized and normalized (whitespace collapse, '/' removal)

## Sanitization & Normalization
Text is pre‑processed to normalize smart quotes and collapse control whitespace. IPA output then:
- Replaces `/` with space
- Collapses repeated whitespace

## Failure Behavior
If the process times out or returns empty IPA:
- Segment synthesis aborts with an error event (`OnTtsError("IPA generation failed")`)
- No fallback grapheme mapping presently (future extension point)

## Performance
- Cold start cost per utterance segment (process launch) – acceptable for moderate usage patterns
- No caching layer; benefit: deterministic, low memory; cost: repeated identical phrases incur repeated cost

## Barge‑In Grace Window Interaction
After a barge‑in cancellation, the next utterance applies a “grace” audio pad (relaxed trimming) but IPA generation path remains unchanged (no special handling required).

## Potential Future Enhancements
| Enhancement | Benefit |
|-------------|---------|
| Persistent espeak worker | Remove process spawn overhead |
| IPA result cache (LRU) | Reduce duplicate phrase latency |
| Phoneme fallback (rule G2P) | Provide robustness when espeak absent |
| Multi‑language voice selection | Extend beyond hardcoded `en-us` |

Current minimalist design trades absolute speed for simplicity and reliability while integrating cleanly with the new preemptive playback controller.