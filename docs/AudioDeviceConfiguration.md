# Audio Device Configuration

This document explains how to configure microphone input devices for STT (Speech-to-Text) and audio output devices for TTS (Text-to-Speech) in the Kinectv1 application.

## Overview

The application supports selecting specific audio devices for:
- **STT Input**: Microphone/recording device for speech recognition
- **TTS Output**: Speaker/headphone device for text-to-speech playback

Local TTS playback is now routed through `TtsService` + `TtsPlaybackController` (single active utterance with preemption / barge?in support).

## Classes

### AudioDeviceManager
- Enumerates available input and output devices
- Tests device availability and functionality
- Manages device selection and configuration

## Settings

Settings are persisted in `AppSettings` (see `settings.md`).

Relevant fields:
- `Stt.InputDevice`
- `Tts.OutputDevice`
- `Tts.LocalVolume` (scaling applied inside `TtsService` before playback)
- `Asr.BargeInEnabled` (governs whether live speech cancels current TTS)

Default for devices: `Default` (system default).

## Usage Examples

### View Available Devices
```csharp
AudioDeviceManager.LogAllDevices();
```

### Configure STT Input Device
```csharp
AppSettings.SaveSttInputDevice("USB Microphone");
```

### Configure TTS Output Device
```csharp
AppSettings.SaveTtsOutputDevice("Speakers (High Definition Audio)");
```

### Using Configured Devices

#### STT Input
```csharp
var inputDevice = AudioDeviceManager.GetConfiguredInputDevice();
using (var waveIn = new WaveInEvent
{
    WaveFormat = new WaveFormat(16000, 16, 1),
    DeviceNumber = inputDevice?.DeviceNumber ?? 0
})
{
    waveIn.DataAvailable += (s,e)=>{ /* process */ };
    waveIn.StartRecording();
}
```

#### TTS Output (Local)
```csharp
await TtsService.SpeakWithPreemptionAsync("Hello world");
```
The controller will preempt any prior utterance and apply volume scaling.

## Device Info Objects
(unchanged)

## Error Handling
- Missing / unavailable device -> fallback to system default with warning
- Playback cancellation triggered by barge?in uses `TtsPlaybackController.CancelCurrent()`

## Notes
- No separate Coqui/Kokoro service layer; unified `TtsService` manages model + playback.
- Barge?in: when enabled, VAD activation cancels current audio; otherwise STT is suppressed during playback.
- `AudioInputHelper` was removed; use `AudioDeviceManager` with `WaveInEvent` directly for STT input setup.
