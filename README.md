# Kinect Face & Voice Recognition

A WPF application that combines Kinect face tracking with voice recognition, LLM integration, and text-to-speech for interactive AI experiences.

## Features

- **Face Recognition** - Real-time face detection and identification using Kinect + ArcFace
- **Voice Recognition** - Speech-to-text using Vosk with VAD (Voice Activity Detection)
- **LLM Integration** - Conversation with Ollama/LM Studio language models
- **Text-to-Speech** - Neural TTS using Kokoro ONNX model
- **Discord Integration** - Voice chat support for Discord servers
- **WebRTC Audio Bridge** - Browser-based audio input from mobile devices

## Requirements

- Windows 10/11 (x64)
- .NET 8.0 Runtime
- Kinect v1 sensor (optional, for face tracking)
- NVIDIA GPU with CUDA support (recommended for TTS)

## Audio Input Modes

| Mode | Description | Use Case |
|------|-------------|----------|
| **Local Mic** | Direct microphone input | Local development, solo use |
| **Discord** | Discord voice channel | Gaming, streaming, group chat |
| **WebRTC** | Browser-based LAN audio | Mobile device input |

### WebRTC Audio Bridge

The WebRTC mode allows capturing audio from any device with a browser:

1. Select "WebRTC" in the Audio Input panel
2. Open the displayed URL on your phone/tablet
3. Tap "Join Voice" and grant microphone access
4. Speak - audio streams to the app for recognition

See [WebRTC Audio Bridge](docs/WEBRTC_AUDIO_BRIDGE.md) for details.

## Project Structure

```
Kinectv1/
??? Services/
?   ??? Audio/          # Audio device management, utilities
?   ??? Discord/        # Discord bot and voice processing
?   ??? FaceTracking/   # Kinect face detection, identity fusion
?   ??? Llm/            # LLM clients (Ollama, LM Studio)
?   ??? Speaker/        # Speaker identification, embeddings
?   ??? Tts/            # Text-to-speech service
?   ??? Utilities/      # Console manager, helpers
?   ??? Voice/          # Voice recognition, WebRTC transport
??? Settings/
?   ??? UI/             # Settings windows and views
?   ??? *.cs            # Settings models and service
??? Discord/            # Discord.Net bot integration
??? docs/               # Documentation
??? models/             # ML models (Vosk, ArcFace, TTS)
??? prompts/            # System prompts for LLM
??? lib/                # Native libraries
```

## Configuration

Settings are stored in `%APPDATA%/Kinectv1/settings.json`. Key sections:

```json
{
  "audio": {
    "voiceThreshold": 0.01,
    "bufferSize": 4096
  },
  "tts": {
    "enabled": true,
    "execution": "GPU",
    "modelPath": "models/tts/model.onnx"
  },
  "stt": {
    "modelPath": "models/vosk-model-en-us-0.22"
  },
  "webRtc": {
    "enabled": true,
    "port": 8787
  },
  "app": {
    "inputMode": "localmic"
  }
}
```

See [Settings Documentation](docs/settings.md) for full reference.

## Documentation

| Document | Description |
|----------|-------------|
| [Settings](docs/settings.md) | Configuration reference |
| [WebRTC Audio Bridge](docs/WEBRTC_AUDIO_BRIDGE.md) | Browser-based audio input |
| [ASR VAD & Preroll](docs/ASR_VAD_AND_PREROLL.md) | Voice activity detection |
| [Discord Audio Pipeline](docs/Discord_Audio_Pipeline_Summary.md) | Discord voice processing |
| [TTS Pipeline](docs/ONNX_INFERENCE_PIPELINE.md) | Text-to-speech details |
| [Conditional Routing](docs/CONDITIONAL_TTS_OUTPUT_ROUTING.md) | Audio output routing |
| [Vector Memory](docs/VECTOR_MEMORY.md) | Conversation memory system |

## Building

```powershell
dotnet build -c Release
```

### Native Dependencies

The following native libraries are required:
- `opus.dll`, `libsodium.dll` - Discord voice
- `onnxruntime*.dll` - ONNX inference
- CUDA/cuDNN DLLs - GPU acceleration (optional)

Run `download-discord-natives.ps1` to fetch Discord voice libraries.

## License

See LICENSE file for details.
