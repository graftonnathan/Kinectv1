# Settings User Guide

This guide explains how to configure the Kinectv1 application settings for optimal performance in your environment.

## Overview

Kinectv1 uses a comprehensive settings system to control all aspects of the application, from speech recognition to Discord bot integration. Settings are stored in the `Settings.settings` file and can be configured through the application interface or modified directly in the configuration files.

## Application Scenarios

The application provides three preset scenarios to quickly configure settings for different use cases:

### Local Scenario
- **Purpose**: Local-only TTS and voice processing without external integrations
- **Best for**: Personal use, development, or isolated testing
- **Key features**: Local speech recognition and text-to-speech only

### Discord Scenario  
- **Purpose**: Discord bot integration with voice commands
- **Best for**: Discord servers with voice interaction capabilities
- **Key features**: Discord bot enabled, voice channel integration

### Kiosk Scenario
- **Purpose**: Public kiosk mode with restricted settings for privacy
- **Best for**: Public installations, demonstrations
- **Key features**: Ollama disabled, telemetry disabled for privacy

## Core Settings Categories

### 🔊 Text-to-Speech (TTS) Settings

Controls the speech synthesis pipeline that converts text to audible speech.

| Setting | Description | Default | Notes |
|---------|-------------|---------|-------|
| **TtsEnabled** | Enable/disable TTS pipeline | `True` | Master switch for speech output |
| **TtsUseGpu** | Use GPU acceleration for TTS | `True` | Requires compatible GPU |
| **TtsModelPath** | Path to TTS model file | `models\tts\kokoro\onnx\model.onnx` | ONNX model file location |
| **TtsModelFolder** | TTS model folder | `models\tts\kokoro` | Contains supporting model files |
| **TtsSpeaker** | Voice speaker/style | `af_aoede` | Determines voice characteristics |
| **LocalTtsVolume** | Local audio output volume | `1.0` | Range: 0.0-1.0 |
| **DiscordTtsVolume** | Discord voice channel volume | `1.0` | Range: 0.0-1.0 |

**Configuration Example:**
```
TTS enabled: Yes
GPU acceleration: Yes  
Model: models\tts\kokoro\onnx\model.onnx
Speaker: af_aoede
Local Volume: 100%
Discord Volume: 100%
```

### 🎙️ Speech-to-Text (STT) Settings

Controls the speech recognition pipeline that converts audio to text.

| Setting | Description | Default | Notes |
|---------|-------------|---------|-------|
| **SttModelPath** | Vosk model directory | `models\vosk-model-en-us-0.22` | Contains Vosk language model |
| **SttInputDevice** | Microphone device | `Default` | Name of audio input device |

**Configuration Example:**
```
STT Model: models\vosk-model-en-us-0.22
Input Device: Default (system default microphone)
```

### 🎤 Voice Recognition Settings

Fine-tune voice activity detection and speech quality thresholds.

| Setting | Description | Default | Range | Notes |
|---------|-------------|---------|-------|-------|
| **VoiceThreshold** | Basic voice detection threshold | `0.2` | 0.0-1.0 | Lower = more sensitive |
| **VoiceActivityThreshold** | Microphone VAD threshold | `300` | 0-1000+ | Audio level for voice detection |
| **DiscordVoiceActivityThreshold** | Discord VAD threshold | `25` | 0-100+ | Discord voice activity detection |
| **VoiceConfidenceThreshold** | Minimum quality threshold | `0.7` | 0.0-1.0 | Speech recognition confidence |
| **VoiceHighConfidenceThreshold** | High quality threshold | `0.9` | 0.0-1.0 | Premium quality speech |
| **VoiceConfidenceBufferSize** | Confidence buffer size | `7` | 1-20 | Smoothing window size |
| **VoiceConfidenceLoggingEnabled** | Log confidence scores | `True` | - | Debug voice recognition |
| **VadSilenceTimeoutMs** | Silence timeout (finalization) | `1000ms` | 500-5000ms | Delay before finalizing speech |
| **VadDebounceTimeoutMs** | Debounce timeout | `500ms` | 100-2000ms | Prevent double-finalization |
| **BargeInEnabled** | Allow ASR to interrupt TTS | `False` | - | Interrupt speech output |

**Tuning Guidelines:**
- **VoiceActivityThreshold**: Start with 300, increase if picking up background noise
- **VoiceConfidenceThreshold**: 0.7 is good for most users, lower for accented speech  
- **BargeInEnabled**: Enable for interactive conversations, disable for announcements

### 🎧 Audio Device Settings

Configure specific audio devices for input and output.

| Setting | Description | Default | Notes |
|---------|-------------|---------|-------|
| **SttInputDevice** | Microphone device name | `Default` | Use system default or specific device |
| **TtsOutputDevice** | Speaker device name | `Default` | Use system default or specific device |

**Device Configuration:**
- Use `"Default"` for system default devices
- Use exact device names for specific hardware (e.g., `"USB Microphone"`)
- Available devices are logged at startup
- Device changes require application restart

### 🤖 Discord Bot Settings

Configure Discord integration for voice commands and bot functionality.

| Setting | Description | Default | Notes |
|---------|-------------|---------|-------|
| **DiscordBotEnabled** | Enable Discord bot | `True` | Master switch for Discord features |
| **DiscordBotToken** | Bot authentication token | *(required)* | From Discord Developer Portal |
| **DiscordBotPrefix** | Command prefix | `!` | Character(s) to trigger commands |
| **DiscordAutoJoinVoice** | Auto-join voice channels | `False` | Automatically connect to voice |

**Setup Steps:**
1. Create Discord application at https://discord.com/developers/applications
2. Create bot and copy token to `DiscordBotToken`
3. Invite bot to server with appropriate permissions
4. Enable `DiscordBotEnabled` and configure prefix

### 🧠 Ollama AI Settings

Configure the Ollama AI integration for conversational responses.

| Setting | Description | Default | Notes |
|---------|-------------|---------|-------|
| **OllamaEnabled** | Enable Ollama AI responses | `True` | Master switch for AI features |
| **OllamaModel** | AI model name | `gemma3:4b` | Must be installed in Ollama |
| **OllamaMemoryEnabled** | Conversation memory | `True` | Remember past conversations |
| **OllamaMaxMessagesPerSpeaker** | Message history limit | `20` | Per-speaker message limit |
| **OllamaMaxSystemMessages** | System message limit | `3` | System prompt message limit |
| **OllamaConversationTimeoutMinutes** | Conversation timeout | `30` | Minutes before conversation reset |
| **ConversationHistoryPath** | History storage path | `history` | Directory for conversation files |
| **SystemPromptPath** | System prompt file | `prompts\system.txt` | AI behavior instructions |

**AI Model Setup:**
1. Install Ollama: https://ollama.ai/
2. Pull desired model: `ollama pull gemma3:4b`
3. Configure `OllamaModel` to match installed model
4. Customize system prompt in `prompts\system.txt`

### 👤 Face Recognition Settings

Configure face detection and recognition thresholds.

| Setting | Description | Default | Range | Notes |
|---------|-------------|---------|-------|-------|
| **FaceThreshold** | Face detection confidence | `0.45` | 0.0-1.0 | Lower = more detections |
| **SpeakerEmbeddingModelPath** | Face embedding model | `models\pyannote_embedding.onnx` | - | ONNX model for face recognition |

### 🎨 User Interface Settings

Control application appearance and window behavior.

| Setting | Description | Default | Notes |
|---------|-------------|---------|-------|
| **DarkMode** | Use dark theme | `True` | Visual appearance |
| **WindowWidth** | Default window width | `900` | Pixels |
| **WindowHeight** | Default window height | `900` | Pixels |
| **WindowLeft** | Window X position | `100` | Screen coordinates |
| **WindowTop** | Window Y position | `100` | Screen coordinates |
| **WindowState** | Window state | `Normal` | Normal/Maximized/Minimized |

### 📊 Telemetry Settings

Configure data collection and performance monitoring.

| Setting | Description | Default | Notes |
|---------|-------------|---------|-------|
| **TelemetryEnabled** | Enable telemetry collection | `True` | Performance and usage data |
| **TelemetryFile** | Telemetry output file | `telemetry.json` | Data storage location |

**Privacy Note**: Telemetry data is stored locally and not transmitted externally. Disable in Kiosk scenario for maximum privacy.

### 🔀 Identity Fusion Settings

Configure speaker identification and voice recognition fusion.

| Setting | Description | Default | Notes |
|---------|-------------|---------|-------|
| **FusionEnabled** | Enable identity fusion | `True` | Combine face and voice recognition |
| **FusionConfidenceThreshold** | Fusion confidence threshold | `0.8` | Identity matching confidence |

### 📁 System Settings

Core application configuration settings.

| Setting | Description | Default | Notes |
|---------|-------------|---------|-------|
| **KinectMode** | Kinect operation mode | `Native` | Hardware interface mode |

## Configuration Validation

The application includes built-in validation that checks your configuration at startup:

### ✅ Successful Configuration
When all settings are properly configured, you'll see:
```
✅ All configuration checks passed
```

### ⚠️ Warning Messages
Configuration warnings indicate potential issues:
- `⚠️ STT: Vosk model directory not found` - Check SttModelPath
- `⚠️ Discord: Bot token appears invalid` - Verify DiscordBotToken
- `⚠️ Audio: STT input device not configured` - Check SttInputDevice

### ❌ Error Messages  
Configuration errors prevent proper operation:
- `❌ TTS: Model file not found` - Check TtsModelPath and TtsModelFolder
- `❌ Discord: Bot token not configured` - Set DiscordBotToken
- `❌ Ollama: No model selected` - Configure OllamaModel

## Troubleshooting

### Audio Issues
1. **No voice detection**: Lower VoiceActivityThreshold, check SttInputDevice
2. **Background noise pickup**: Raise VoiceActivityThreshold  
3. **No TTS output**: Check TtsEnabled, TtsOutputDevice, and volume settings
4. **Poor recognition quality**: Adjust VoiceConfidenceThreshold, check microphone

### Discord Issues  
1. **Bot not responding**: Verify DiscordBotToken and server permissions
2. **Commands not working**: Check DiscordBotPrefix and command syntax
3. **Voice channel issues**: Enable DiscordAutoJoinVoice, check voice permissions

### AI Issues
1. **No AI responses**: Check OllamaEnabled and OllamaModel installation
2. **Poor AI quality**: Try different model with `ollama pull <model>`
3. **Memory issues**: Adjust OllamaMaxMessagesPerSpeaker

### Performance Issues
1. **Slow TTS**: Disable TtsUseGpu if GPU acceleration unavailable
2. **High CPU usage**: Reduce VoiceConfidenceBufferSize  
3. **Memory usage**: Disable OllamaMemoryEnabled or reduce message limits

## Configuration Files

### Settings.settings
The main configuration file containing all user settings. This is an XML file automatically managed by the application.

### App.config
Application-level configuration containing system defaults and infrastructure settings.

### Model Files
- TTS models in `models/tts/`
- STT models in `models/vosk-*`  
- Face recognition models in `models/`

### Prompt Files
- System prompts in `prompts/system.txt`
- Custom prompts can be added to `prompts/` directory

For more specific technical details about adding new settings, see the [Developer Notes](dev-notes.md).