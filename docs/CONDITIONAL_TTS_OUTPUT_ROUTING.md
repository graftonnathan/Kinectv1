# ??? Conditional TTS Output Routing - IMPLEMENTED

## ?? **Feature Overview**
The TTS pipeline now conditionally outputs audio based on which input sources are enabled in the MainWindow UI. This provides users with precise control over where their AI responses are heard.

## ?? **Implementation Details**

### **UI Controls**
- **?? Microphone Checkbox**: Controls local system audio output
- **?? Discord Checkbox**: Controls Discord voice channel output

### **Routing Logic**
```csharp
// Only play locally if microphone input is enabled
if (_isMicrophoneInputEnabled)
{
    Console.WriteLine($"?? Playing TTS locally (microphone input enabled)");
    localTtsTask = CoquiTtsService.SpeakAsync(response, currentSpeakerRefId);
}
else
{
    Console.WriteLine($"?? Skipping local TTS (microphone input disabled)");
}

// Only send to Discord if Discord input is enabled AND bot is connected
if (_isDiscordInputEnabled && DiscordNetBotManager.IsRunning && DiscordNetBotManager.IsInVoiceChannel)
{
    Console.WriteLine($"?? Sending TTS to Discord voice channel (Discord input enabled)");
    discordTtsTask = DiscordNetBotManager.SendTtsToDiscordAsync(response, currentSpeakerRefId);
}
else if (!_isDiscordInputEnabled)
{
    Console.WriteLine($"?? Skipping Discord TTS (Discord input disabled)");
}
```

## ?? **Output Scenarios**

### **? Both Enabled (Default)**
- **Microphone**: ? Enabled
- **Discord**: ? Enabled
- **Result**: TTS plays on both system speakers AND Discord voice chat
- **Log**: `? TTS completed for both local and Discord output`

### **?? Microphone Only**
- **Microphone**: ? Enabled
- **Discord**: ? Disabled
- **Result**: TTS plays only on system speakers
- **Log**: `? TTS completed for local output only`

### **?? Discord Only**
- **Microphone**: ? Disabled
- **Discord**: ? Enabled
- **Result**: TTS plays only in Discord voice chat
- **Log**: `? TTS completed for Discord output only`

### **?? Both Disabled**
- **Microphone**: ? Disabled
- **Discord**: ? Disabled
- **Result**: No TTS output (silent AI responses)
- **Log**: `? TTS completed for no output (both inputs disabled)`

## ?? **User Experience**

### **Typical Usage Patterns**

#### **?? Solo Development/Testing**
- ? Enable Microphone only
- ? Disable Discord
- **Benefit**: AI responses only heard locally, no Discord spam

#### **?? Discord Gaming/Streaming**
- ? Disable Microphone
- ? Enable Discord only
- **Benefit**: AI responses only heard by Discord audience, no local echo

#### **??? Content Creation**
- ? Enable both
- **Benefit**: Creator hears responses locally AND audience hears in Discord

#### **?? Silent Mode**
- ? Disable both
- **Benefit**: AI processes voice but provides no audio feedback (text-only)

## ?? **Technical Benefits**

### **1. No Audio Conflicts**
- Prevents local/Discord audio echo issues
- Users can choose single output source

### **2. Bandwidth Optimization**
- Discord TTS only when needed
- Reduces unnecessary audio processing

### **3. Context-Aware Operation**
- Respects user's current activity context
- Flexible for different usage scenarios

### **4. Clear User Feedback**
- Console logs show exactly which outputs are active
- Users understand where audio will play

## ?? **Implementation Details**

### **State Tracking**
```csharp
private bool _isMicrophoneInputEnabled = true;
private bool _isDiscordInputEnabled = true;
```

### **UI Event Handlers**
- `MicInputEnabledCheckBox_Checked/Unchecked`
- `DiscordInputEnabledCheckBox_Checked/Unchecked`
- Real-time state updates with VoiceRecognizer

### **Conditional Output Logic**
- Integrated into `OnOllamaResponseReceived` method
- Uses existing TTS infrastructure
- Maintains async/await patterns for performance

## ?? **Console Output Examples**

### **Both Enabled:**
```
?? Speaking Ollama response: 'Hello there! It's lovely to meet you.'
?? Using selected TTS speaker: 314
?? Playing TTS locally (microphone input enabled)
?? Sending TTS to Discord voice channel (Discord input enabled)
? TTS completed for both local and Discord output
```

### **Microphone Only:**
```
?? Speaking Ollama response: 'Hello there! It's lovely to meet you.'
?? Using selected TTS speaker: 314
?? Playing TTS locally (microphone input enabled)
?? Skipping Discord TTS (Discord input disabled)
? TTS completed for local output only
```

### **Discord Only:**
```
?? Speaking Ollama response: 'Hello there! It's lovely to meet you.'
?? Using selected TTS speaker: 314
?? Skipping local TTS (microphone input disabled)
?? Sending TTS to Discord voice channel (Discord input enabled)
? TTS completed for Discord output only
```

## ? **Feature Complete**

The conditional TTS output routing is now fully implemented and provides users with:

1. **??? Precise Control**: Choose exactly where AI responses are heard
2. **?? Flexible Usage**: Adapt to different scenarios (solo work, Discord chat, content creation)
3. **?? Clear Feedback**: Console logs show exactly what's happening
4. **? Efficient Operation**: Only processes audio for enabled outputs
5. **?? User-Friendly**: Simple checkbox interface for immediate control

Users can now customize their AI voice experience based on their current context and needs! ??

---

# Conditional TTS Output Routing

This document describes how TTS chooses an output route (local speakers, Discord, muted) based on current configuration and runtime state.

## Core Decision Factors

1. TTS enabled (`TtsSettings.Enabled`)
2. Local vs Discord volume > 0
3. Active Discord voice connection (for Discord path)
4. Application scenario (Local / Hybrid)
5. Muting or focus rules

## Updated Playback Pipeline (Controller Integration)

Local playback is now mediated by `TtsPlaybackController`:

- Guarantees a single active utterance.
- New requests preempt the previous one atomically.
- External cancels (UI / barge?in) call `CancelCurrent()`.
- `TtsService.SpeakWithPreemptionAsync` is the canonical entry point.

### Barge?In Interaction

`AsrSettings.BargeInEnabled` determines ASR/TTS interplay:

| Setting | Behavior |
|---------|----------|
| true | VAD activation during TTS cancels current utterance (barge?in) |
| false | ASR suppressed while TTS plays; frames buffered as preroll but not recognized |

A cancellation marks a short grace window used to relax leading trim on the next utterance to avoid clipped starts.

## Routing Outcomes

| Condition | Route |
|-----------|-------|
| Local volume > 0, no Discord session | Local speakers only |
| Local volume == 0, Discord connected, Discord volume > 0 | Discord only |
| Both volumes > 0 and Discord connected | Dual: local + Discord (if implemented) |
| All volumes 0 or disabled | No playback (generation may still occur) |

## Error Handling

- Missing voice/style -> raises `OnTtsError` and aborts.
- Model/session init failure -> disables further synthesis until `RecreateSessionFromSettings()` succeeds.

## Extensibility

Future routing conditions (e.g., Mumble, WebRTC) should plug in after the controller so that preemption rules remain consistent across outputs.