# ?? TTS Local Audio Troubleshooting Guide

## ? **Problem**
When the microphone radio button is enabled, local TTS audio output is not playing through system speakers.

## ?? **Diagnostic Tools Added**

### **1. Debug TTS Button**
- **Location**: Control buttons row (?? Debug TTS)
- **Function**: Runs comprehensive TTS diagnostics and offers auto-fix
- **Usage**: Click the button when TTS audio isn't working

### **2. Comprehensive Diagnostic Method**
```csharp
CoquiTtsService.DiagnoseTtsIssues()
```
**Checks:**
- TTS service state (initialized, enabled, model loaded)
- Audio configuration (volume, output device)
- Component status (CMU dictionary, symbols, model)
- Current playback state
- Basic functionality tests

### **3. Conditional TTS Routing Test**
```csharp
TestConditionalTtsRouting()
```
**Verifies:**
- Microphone input checkbox state
- TTS service enabled status
- Volume settings
- Audio routing logic
- UI state consistency

### **4. Auto-Fix Method**
```csharp
FixCommonTtsIssues()
```
**Automatically fixes:**
- Enables TTS service if disabled
- Enables microphone input if disabled
- Increases volume if too low (< 10%)
- Re-initializes TTS model if needed

## ??? **Manual Diagnostic Steps**

### **Step 1: Check Basic Requirements**
1. ? **TTS Enabled**: Button should show "Disable TTS" (red background)
2. ? **Microphone Input**: ?? Mic checkbox should be checked
3. ? **Volume**: Local volume slider should be > 10%
4. ? **Model Loaded**: TTS status should show "Enabled"

### **Step 2: Test TTS Directly**
```csharp
// In console or debug window
CoquiTtsService.SpeakAsync("Hello world", "225");
```

### **Step 3: Check Audio Device**
1. Verify system volume is up
2. Test other audio applications work
3. Check Windows Sound settings
4. Try different output device if available

### **Step 4: Verify Conditional Logic**
When microphone checkbox is enabled and AI responds:
```
Expected Console Output:
?? Playing TTS locally (microphone input enabled)
?? TTS started speaking: '[response text]'
```

## ?? **Common Issues & Solutions**

### **Issue 1: TTS Service Not Enabled**
**Symptoms**: TTS button shows "Enable TTS"
**Solution**: Click "Enable TTS" button or use auto-fix

### **Issue 2: Microphone Input Disabled**
**Symptoms**: Console shows "Skipping local TTS (microphone input disabled)"
**Solution**: Check the ?? Mic checkbox

### **Issue 3: Volume Too Low**
**Symptoms**: TTS generates audio but you can't hear it
**Solution**: Increase local volume slider or use auto-fix

### **Issue 4: Audio Device Issues**
**Symptoms**: Audio generated but no sound output
**Solutions**:
- Check system volume
- Try different output device
- Restart audio drivers
- Check Windows Sound settings

### **Issue 5: Model Not Loaded**
**Symptoms**: TTS shows "Model load failed" or errors in console
**Solutions**:
- Verify model files exist in `models/tts/`
- Try different TTS model
- Check GPU/CPU settings
- Use auto-fix to reinitialize

## ?? **Console Debugging**

### **Successful TTS Flow:**
```
?? Speaking Ollama response: 'Hello there!'
?? Using selected TTS speaker: 225
?? Playing TTS locally (microphone input enabled)
?? Starting TTS audio playback - 44100 samples
? TTS playback started successfully! State: Playing, Volume: 80%
? TTS completed for local output only
```

### **Failed TTS Flow Examples:**
```
? TTS not enabled
? Skipping local TTS (microphone input disabled)
? Failed to generate audio
? Audio initialization failed
```

## ?? **Quick Troubleshooting Checklist**

1. **Click ?? Debug TTS button** ? Check console output
2. **Accept auto-fix** if offered
3. **Test with TTS Test button** in TTS section
4. **Check system audio** (try YouTube, music, etc.)
5. **Verify microphone checkbox** is enabled
6. **Check volume sliders** (both local and system)

## ?? **Pro Tips**

### **For Developers:**
- Use `CoquiTtsService.DiagnoseTtsIssues()` for detailed technical info
- Monitor console output during AI responses
- Test direct TTS calls for isolation

### **For Users:**
- Use the Debug TTS button first
- Always accept auto-fix suggestions
- Check system volume and audio settings
- Try the TTS Test button to isolate issues

### **Audio Troubleshooting:**
- Different audio devices may behave differently
- Windows audio drivers can cause issues
- Some USB audio devices need special handling
- Virtual audio devices (VoiceMeeter, etc.) may interfere

The diagnostic tools will help identify exactly where the TTS pipeline is failing and provide specific solutions for each issue! ??