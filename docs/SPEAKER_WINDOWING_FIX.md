# Speaker Recognition Windowing Fix - Implementation Notes

## Summary
Fixed speaker recognition ingestion issue where embedder often returned 'Unknown' scores despite audio presence. The issue was caused by improper windowing and lack of silence gating.

## Root Cause
- Original implementation consumed entire 1-second audio windows after processing
- No rolling window with proper 0.5s hop interval as required by the speaker embedder model
- Missing RMS normalization and peak clamping
- No silence gating to skip inference on low-quality audio
- Hardcoded speaker match threshold of 0.4 instead of configurable 0.6

## Solution Overview

### 1. Rolling Window Implementation (`RingBuffer` class)
- **Location**: `SpeakerEmbedder.cs`
- **Purpose**: Manages exactly 16,000 samples (1 second at 16kHz) in circular buffer
- **Key Features**:
  - Continuous addition of audio samples without consuming buffer
  - Extract full 1-second windows on demand
  - Calculate RMS for silence gating
  - Thread-safe operations

### 2. Windowing with 0.5s Hop (`ProcessSpeakerEmbeddingWithRollingWindow`)
- **Location**: `VoiceProcessor.cs`
- **Purpose**: Generate speaker embeddings every 500ms instead of consuming full windows
- **Key Features**:
  - Time-based inference control (0.5s hop)
  - Silence gating with -45 dBFS threshold
  - Minimum 20% voiced samples requirement
  - Proper error handling and logging

### 3. Audio Normalization (`NormalizeAndClampAudio`)
- **Location**: `SpeakerEmbedder.cs` 
- **Purpose**: Prepare audio for speaker embedder model
- **Key Features**:
  - Target RMS normalization to 0.1
  - Peak clamping to [-1.0, 1.0] range
  - Handles low-amplitude signals gracefully

### 4. Configurable Speaker Threshold
- **Location**: `AppSettings.cs`, `SpeakerIdentifier.cs`
- **Purpose**: Make speaker matching threshold configurable
- **Changes**:
  - Added `LoadSpeakerMatchMinScore()` with 0.6 default
  - Updated `SpeakerIdentifier.Identify()` to use settings
  - Updated `GetDefaultThreshold()` to return settings value

### 5. Telemetry Integration
- **Counters**: `speaker.infer` - tracks inference calls
- **Gauges**: `speaker.score` - tracks speaker match scores
- **Purpose**: Monitor speaker recognition performance

## Key Technical Parameters

| Parameter | Value | Purpose |
|-----------|-------|---------|
| Window Size | 16,000 samples | 1 second at 16kHz as required by embedder |
| Hop Interval | 500ms (8,000 samples) | Generate embeddings every 0.5 seconds |
| Silence Threshold | -45 dBFS | Skip inference on very quiet audio |
| Voiced Ratio | 20% minimum | Require meaningful speech content |
| Target RMS | 0.1 | Normalize audio amplitude for embedder |
| Peak Clamp | [-1.0, 1.0] | Prevent clipping in normalized audio |
| Match Threshold | 0.6 (configurable) | Increased from 0.4 for better precision |

## Files Modified

1. **`SpeakerEmbedder.cs`**
   - Added `RingBuffer` class for rolling windows
   - Added `NormalizeAndClampAudio()` method
   - Enhanced `Embed()` with normalization and telemetry

2. **`VoiceProcessor.cs`**
   - Added `_speakerBuffer` RingBuffer instance
   - Added `ProcessSpeakerEmbeddingWithRollingWindow()` method
   - Added `CountVoicedSamples()` helper method
   - Updated `ProcessAudio()` to use rolling window

3. **`SpeakerIdentifier.cs`**
   - Updated `Identify()` to use configurable threshold
   - Updated `GetDefaultThreshold()` to read from settings

4. **`AppSettings.cs`**
   - Added `LoadSpeakerMatchMinScore()` method
   - Added `SaveSpeakerMatchMinScore()` method
   - Updated STT settings summary

## Testing

Created test files in `tools/` directory:
- `SpeakerWindowingTest.cs` - Unit tests for RingBuffer and silence gating
- `ManualIntegrationTest.cs` - Integration test for full pipeline

## Expected Results

1. **Stable Speaker Scores**: Non-zero scores for known voices consistently
2. **Proper 'Unknown' Behavior**: Only during actual silence, not audio gaps
3. **Reduced Flapping**: Identity fusion stable across short speech gaps
4. **Better Performance**: Regular inference every 0.5s instead of sporadic windows
5. **Quality Control**: Skip inference on poor quality audio automatically

## Configuration

New setting in app configuration:
```xml
<setting name="SpeakerMatchMinScore" serializeAs="String">
    <value>0.6</value>
</setting>
```

## Monitoring

Track these telemetry metrics:
- `counter.speaker.infer` - Should increase regularly during speech
- `gauge.speaker.score` - Should show consistent non-zero values for known speakers