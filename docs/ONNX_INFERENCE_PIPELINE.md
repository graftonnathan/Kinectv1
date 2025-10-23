# ONNX Inference Pipeline (TTS)

## Overview
Single ONNX model (Kokoro variant) executed per utterance (or segment) with minimal allocations and shared session reuse.

## Stages
| Stage | Method | Notes |
|-------|--------|------|
| IPA generation | `GetIpa` | External `espeak-ng` process (one-shot) |
| Token mapping | `MapIpaToIds` | Char ? vocab id; adds BOS/EOS padding slots |
| Style vector selection | `LoadStyle` | Voice `.bin` file slice chosen by inner token count |
| Tensor prep | Reuse style/speed tensors | Speed scalar + 256-d style vector reused |
| Inference | `_session.Run(_reuseInputs)` | Locked on `_reuseInputs` list |
| Postprocessing | Trimming + volume scaling | Grace mode may adjust behavior |

## Reused Objects
| Object | Lifetime | Purpose |
|--------|----------|---------|
| `_session` | App run | Heavy model weights loaded once |
| `_reuseStyleTensor` | App run | Avoid reallocation of style buffer |
| `_reuseSpeedTensor` | App run | Scalar speed container |
| `_reuseInputs` | Per-call cleared | Prevent list reallocation |

## Style Vector File
Voice `.bin` expected layout:
```
N * 256 float32 values (concatenated style embeddings)
```
Index selected:
```
idx = clamp(innerTokenCount, 0, N-1)
```

## Speed Control
`_speed` loaded from settings ( > 0 ) else defaults to 1.0. Applied as a single float tensor `speed` at inference.

## Locking
| Lock | Protects |
|------|----------|
| `_lock` | Session (init / recreate) |
| `_reuseInputs` | Shared tensors & input list during Run |

## Failure Points & Handling
| Point | Error Action |
|-------|-------------|
| IPA null/empty | Emit error; return empty audio |
| Token list empty | Emit error; return empty audio |
| Style load fail | Emit error; return empty audio |
| Inference exception | Emit error; utterance abort |

## Memory Efficiency
- Primary per?segment allocation: input ID tensor + output float array
- Style copy (256 floats) unavoidable for each inference (safe to micro-opt later)

## Potential Enhancements
| Area | Idea |
|------|------|
| Streaming synthesis | Partial vocoder frames playback during model run |
| Voice adaptation | Cache style slices based on token length buckets |
| Multi-speaker batching | Collate short requests when queue reintroduced |

## Minimal Sample
```csharp
var audio = await TtsService.GenerateAudioDataAsync("Sample.");
if(audio.Length > 0) { /* playback / send */ }
```
