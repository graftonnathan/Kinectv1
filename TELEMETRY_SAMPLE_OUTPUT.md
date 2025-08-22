# Telemetry Implementation - Sample Output

This demonstrates the telemetry output format and key metrics tracked:

## Console Output (Single-line JSON)
```json
{"ts":"2024-08-21T22:45:00.123Z","lvl":"info","name":"app.startup","data":{"version":"1.0","timestamp":"2024-08-21T22:45:00.123Z"},"threadId":1}
{"ts":"2024-08-21T22:45:01.456Z","lvl":"info","name":"discord_audio_process.completed","data":{"duration_ms":12.34},"threadId":3}
{"ts":"2024-08-21T22:45:02.789Z","lvl":"info","name":"tts_generate.completed","data":{"duration_ms":234.56},"threadId":5}
{"ts":"2024-08-21T22:45:05.000Z","lvl":"info","name":"health.snapshot","data":{"chunks_processed":150,"drop_count":0,"clip_count":2,"asr_final_ms":45.6,"asr_partial_count":89,"tts_generate_ms":189.3,"tts_segments":23,"onnx_sessions_created":2,"cuda_ep_fallbacks":0,"microphone_enabled":true,"discord_enabled":true,"processing_active":false,"model_loaded":true},"threadId":1}
```

## Key Metrics Tracked

### Discord Audio Processing
- `discord_audio.chunks_processed` - Total audio chunks processed
- `discord_audio.null_input` - Count of null/empty input chunks
- `discord_audio.oversized_chunks` - Count of chunks exceeding buffer size
- `discord_audio.clip_count` - Audio clipping events
- `discord_audio.rms_avg` - Average RMS levels
- `discord_audio.hp_filter_ms` - High-pass filter timing
- `discord_audio.normalize_ms` - Normalization timing
- `discord_audio.resample_ms` - Resampling timing

### ASR (Voice Processing)
- `asr.partial_results` - Partial transcription results
- `asr.final_results` - Final transcription results
- `asr.debounce_skips` - VAD debounce preventions
- `asr.final_flushes` - Forced final result flushes
- `asr_partial.total_ms` - Partial processing timing
- `asr_final.total_ms` - Final processing timing

### TTS Processing
- `tts.generate_requests` - TTS generation requests
- `tts.initialization_failures` - TTS init failures
- `tts.empty_text` - Empty text requests
- `tts.segments_processed` - Text segments processed
- `tts.gpu_fallbacks` - GPU to CPU fallbacks
- `tts.generation_failures` - Generation failures
- `tts_generate.total_ms` - Generation timing

### ONNX Session Management
- `onnx.session_create_requests` - Session creation requests
- `onnx.sessions_created` - Successfully created sessions
- `onnx.cuda_ep_success` - CUDA EP successful loads
- `onnx.cuda_ep_unavailable` - CUDA EP unavailable
- `onnx.cpu_fallbacks` - Fallbacks to CPU execution
- `onnx_session_create.total_ms` - Session creation timing

## Health Snapshots (Every 5 seconds)
Health snapshots provide aggregated metrics for monitoring system performance and detecting regressions.

## File Rotation
- Files rotate automatically at ~5MB
- Rotated files are timestamped: `telemetry_20240821_224500.ndjson`
- Current active file: `logs/telemetry.ndjson`

## Configuration
```
📊 Telemetry Settings:
   Enabled: false (default for safety)
   File path: logs\telemetry.ndjson
   Sampling: 100%
   Format: NDJSON (Newline Delimited JSON)
   Console: Warnings+ and summaries only
   Rotation: ~5MB file size limit
```

To enable telemetry:
```csharp
AppSettings.ConfigureTelemetrySettings(true, "logs/telemetry.ndjson", 100);
Telemetry.RefreshSettings();
```