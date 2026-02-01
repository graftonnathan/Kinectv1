# Headless STT Implementation Summary

## What Was Implemented

### 1. HeadlessVoiceRecognizer.cs
A simplified Vosk STT wrapper specifically for Linux headless mode:

**Features:**
- Vosk model loading and speech recognition
- NAudio WaveInEvent for microphone capture (16kHz, mono, PCM16)
- Voice Activity Detection (VAD) with configurable thresholds
- Real-time transcription with partial results
- Thread-safe audio processing queue
- Event-based architecture (OnTranscription, OnPartialTranscription, OnRmsLevel)

**Key Differences from Full VoiceRecognizer:**
- Removed Discord integration dependencies
- Removed WebRTC audio transport
- Removed speaker embedding/diarization
- Removed barge-in detection
- Simplified VAD logic
- No debug audio capture dependencies

### 2. Updated HeadlessMaggie.cs
Integrated STT into the headless entry point:

**New Features:**
- STT initialization with model path from settings
- Microphone enable/disable control
- Speech → LLM → TTS pipeline
- Console status commands
- Proper cleanup on shutdown

### 3. Updated MaggieHeadless.csproj
Added necessary dependencies:
- NAudio 2.2.1 (audio capture)
- Vosk 0.3.38 (speech recognition)

## Build Instructions

```bash
cd ~/workspace/maggie
dotnet build MaggieHeadless.csproj
```

## Running

```bash
# Set TTS service URL (optional)
export MAGGIE_TTS_URL=http://localhost:7860

# Run
dotnet run --project MaggieHeadless.csproj
```

## Requirements

1. **Vosk Model**: Download and extract to `models/vosk-model-en-us-0.22/`
   ```bash
   mkdir -p models
   cd models
   wget https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip
   unzip vosk-model-en-us-0.22.zip
   ```

2. **TTS Service** (optional): Start Qwen3-TTS service for voice output
   ```bash
   python3 tts_service/qwen_tts_service.py
   ```

3. **LMStudio**: Ensure LMStudio is running at the configured URL (default: http://100.119.229.73:1234)

## Testing Checklist

- [ ] Vosk model downloads correctly
- [ ] Microphone is detected and accessible
- [ ] ALSA/PulseAudio permissions allow recording
- [ ] Real-time transcription accuracy is acceptable
- [ ] Speech triggers LLM response
- [ ] TTS plays Maggie's response
- [ ] API endpoints work via curl/http
- [ ] Console commands (status, help, quit) work

## Known Limitations

1. **No Speaker Diarization**: Meeting transcription mode not included in this simplified version
2. **Single Audio Source**: Only microphone input (no Discord, no WebRTC audio in)
3. **No Barge-in**: Cannot interrupt TTS with new speech
4. **Console Only**: No web-based STT control yet

## Next Steps

1. Download and test with actual Vosk model
2. Verify ALSA/PulseAudio permissions on Linux
3. Test real-world transcription accuracy
4. Add meeting transcription mode (file logging) if needed
5. Add API endpoints for STT control (enable/disable, status)
6. Consider adding WebRTC audio input for browser-based voice chat

## Files Modified/Created

- **Created**: `Services/Voice/HeadlessVoiceRecognizer.cs`
- **Modified**: `HeadlessMaggie.cs`
- **Modified**: `MaggieHeadless.csproj`
- **Modified**: `TODO.md`
- **Created**: `docs/headless-stt-implementation.md` (this file)
