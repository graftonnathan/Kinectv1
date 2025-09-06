# Audio Device Configuration

This document explains how to configure microphone input devices for STT (Speech-to-Text) and audio output devices for TTS (Text-to-Speech) in the Kinectv1 application.

## Overview

The application now supports selecting specific audio devices for:
- **STT Input**: Microphone/recording device for speech recognition
- **TTS Output**: Speaker/headphone device for text-to-speech playback

## Classes Added

### AudioDeviceManager
- Enumerates available input and output devices
- Tests device availability and functionality
- Manages device selection and configuration

### AudioInputHelper
- Creates configured WaveInEvent instances for STT
- Tests STT input device functionality
- Provides device information and logging

## Settings

Two new settings have been added to `Settings.settings`:

- **SttInputDevice** (string): Name of microphone device for STT input
- **TtsOutputDevice** (string): Name of speaker device for TTS output

Default value for both: `"Default"` (uses system default device)

## Usage Examples

### View Available Devices
```csharp
// Show all available devices and current configuration
AppSettings.ShowAudioDeviceDemo();

// Log all devices to console
AudioDeviceManager.LogAllDevices();
```

### Configure STT Input Device
```csharp
// Set specific microphone by name
AppSettings.SaveSttInputDevice("USB Microphone");

// Use system default
AppSettings.SaveSttInputDevice("Default");

// Get available input devices
var inputDevices = AudioDeviceManager.GetInputDevices();
foreach (var device in inputDevices)
{
    Console.WriteLine($"[{device.DeviceNumber}] {device.DeviceName}");
}
```

### Configure TTS Output Device
```csharp
// Set specific speaker/headphones by name
AppSettings.SaveTtsOutputDevice("Speakers (High Definition Audio)");

// Use system default
AppSettings.SaveTtsOutputDevice("Default");

// Get available output devices
var outputDevices = AudioDeviceManager.GetOutputDevices();
foreach (var device in outputDevices)
{
    Console.WriteLine($"[{device.DeviceNumber}] {device.DeviceName}");
}
```

### Configure Both Devices
```csharp
// Configure both STT and TTS devices at once
AppSettings.ConfigureAudioDeviceSettings(
    sttInputDevice: "USB Microphone",
    ttsOutputDevice: "Bluetooth Headphones"
);
```

### Using Configured Devices in Code

#### For STT Input (Speech Recognition)
```csharp
// Create WaveInEvent with configured input device
using (var waveIn = AudioInputHelper.CreateConfiguredWaveIn())
{
    waveIn.DataAvailable += (s, e) => { /* Process audio data */ };
    waveIn.StartRecording();
    // ... recording logic
}
```

#### For TTS Output (Text-to-Speech)
The `CoquiTtsService` automatically uses the configured output device:
```csharp
// TTS will automatically use the configured output device
await CoquiTtsService.SpeakAsync("Hello, this will play on the configured speakers!");
```

## Device Information

### AudioInputDevice Properties
- `DeviceNumber`: NAudio device index
- `DeviceName`: Display name for the device
- `ProductName`: Hardware product name
- `Channels`: Number of audio channels supported
- `Capabilities`: NAudio WaveInCapabilities object
- `IsDefault`: Whether this is the system default device

### AudioOutputDevice Properties
- `DeviceNumber`: NAudio device index
- `DeviceName`: Display name for the device
- `ProductName`: Hardware product name
- `Channels`: Number of audio channels supported
- `Capabilities`: NAudio WaveOutCapabilities object
- `IsDefault`: Whether this is the system default device

## Testing Device Functionality

```csharp
// Test if a specific input device works
bool inputWorks = AudioDeviceManager.TestInputDevice(deviceNumber);

// Test if a specific output device works
bool outputWorks = AudioDeviceManager.TestOutputDevice(deviceNumber);

// Test currently configured devices
bool sttDeviceWorks = AudioInputHelper.TestConfiguredInputDevice();
```

## Startup Initialization

The audio device configuration is automatically initialized at startup:

```csharp
// Called automatically in AppSettings.InitializeSettingsOnStartup()
AppSettings.InitializeAudioDevices();
```

This will:
1. Enumerate all available devices
2. Log device information to console
3. Verify configured devices are available
4. Show current configuration

## Error Handling

The system gracefully handles common issues:
- **Device not found**: Falls back to system default
- **Device unavailable**: Uses fallback device with warning
- **Permission issues**: Logs error and continues with default
- **Hardware disconnected**: Automatically switches to available device

## Integration with Existing Code

The new audio device system integrates seamlessly with existing components:

- **VoiceProcessor**: Use `AudioInputHelper.CreateConfiguredWaveIn()` for STT input
- **CoquiTtsService**: Automatically uses configured TTS output device
- **AppSettings**: All audio device settings persist in user settings file

## UI Integration (Future)

The system is designed to support dropdown controls in the UI:

```csharp
// Get device names for dropdowns
var inputDeviceNames = AudioDeviceManager.GetInputDeviceNames();
var outputDeviceNames = AudioDeviceManager.GetOutputDeviceNames();

// Convert selection back to device numbers
var inputDeviceNumber = AudioDeviceManager.GetInputDeviceNumberByName(selectedName);
var outputDeviceNumber = AudioDeviceManager.GetOutputDeviceNumberByName(selectedName);
```

## Configuration Files

### Settings.settings
```xml
<Setting Name="SttInputDevice" Type="System.String" Scope="User">
  <Value Profile="(Default)">Default</Value>
</Setting>
<Setting Name="TtsOutputDevice" Type="System.String" Scope="User">
  <Value Profile="(Default)">Default</Value>
</Setting>
```

### AppSettings Methods
- `LoadSttInputDevice()` / `SaveSttInputDevice(string)`
- `LoadTtsOutputDevice()` / `SaveTtsOutputDevice(string)`
- `ConfigureAudioDeviceSettings(string, string)`
- `GetAudioDeviceSettingsSummary()`

This system provides flexible audio device management while maintaining backward compatibility with existing code.