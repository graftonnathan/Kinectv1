# WebRTC LAN Audio Bridge

## Overview

The WebRTC Audio Bridge provides a browser-based interface for interacting with the Voice AI application from mobile devices (iOS/Android). It supports:

- **Voice input** - Capture microphone audio and stream to the app for speech recognition
- **Text input** - Type messages directly to the LLM from the web interface
- **Mode control** - Switch between Mic/Discord/WebRTC modes from the web UI
- **Chat interface** - View transcriptions and AI responses in a mobile-friendly chat UI

When **WebRTC mode** (`AudioInMode.WebRtcVoice`) is active, the system is intended to be **WebUI-routed**:
- STT input should come from the WebUI stream (phone mic) only.
- The desktop app should not ingest local microphone frames for STT.
- The app should not ingest system loopback audio (speaker mix) for STT.

## Architecture

```
?????????????????????     WebSocket/RTP      ???????????????????????
?  Mobile Browser   ? ?????????????????????  ?   WPF Application   ?
?  (Safari/Chrome)  ?                        ?                     ?
?                   ? ?????????????????????  ?   WebRTC Transport  ?
?  Microphone       ?     TTS Audio (PCMU)   ?   + Signaling       ?
?  Text Input       ?                        ?   + Chat Relay      ?
?  Mode Selector    ?                        ?                     ?
?????????????????????                        ???????????????????????
                                                      ?
                                                      ?
                                             ???????????????????????
                                             ?   VoiceRecognizer   ?
                                             ?   (Vosk STT)        ?
                                             ???????????????????????
                                                      ?
                                                      ?
                                             ???????????????????????
                                             ?   OllamaService     ?
                                             ?   (LLM Dispatch)    ?
                                             ???????????????????????
```

Note: browser audio is delivered to the app via `WebRtcAudioTransport` and then fed into `VoiceRecognizer` as `AudioSourceType.WebRtc`.

## Web Client Features

### Chat Interface
- Mobile-optimized dark theme UI
- Real-time message display (user messages and AI responses)
- Streaming response display with typing indicator
- Auto-scroll to latest messages

### Mode Selector (3-Position Switch)
- **?? Mic** - Local microphone input (green)
- **?? Discord** - Discord voice channel (blue)  
- **?? WebRTC** - Browser-based audio (purple)

Mode changes sync bidirectionally:
- Change mode in web UI ? desktop app updates
- Change mode in desktop app ? web UI updates

### Voice Input
- Tap voice button to start/stop streaming
- Real-time audio level meter
- Voice button only enabled in WebRTC mode
- Automatic codec negotiation (prefers PCMU)

#### iOS: TTS self-hearing suppression
On iOS (especially speakerphone), the phone mic can re-capture the AI TTS playback. The WebUI applies a short temporary mic-suppress window after receiving TTS audio chunks.

- Client setting: `TTS_MIC_SUPPRESS_MS` (in `wwwroot/webrtc/client.js`).
- This was increased to reduce iOS Safari feedback loops when louder server-side RMS normalization is enabled.

### Text Input
- Type messages directly to the LLM
- Works in any mode (not just WebRTC)
- Messages appear in both web UI and desktop UI

## Components

### WebRtcSignalingServer
- **Location**: `Services/Voice/WebRtcSignalingServer.cs`
- **Port**: Configurable via `webRtc.port` setting (default: 8787)
- **Purpose**: HTTP server hosting the web client and WebSocket signaling
- **Endpoints**:
  - `/` or `/index.html` - Web client UI
  - `/client.js` - WebRTC client JavaScript
  - `/api/mode` - POST to change audio input mode
  - `/api/status` - GET current mode
  - WebSocket upgrade - Signaling channel + chat relay

### WebRtcAudioTransport
- **Location**: `Services/Voice/WebRtcAudioTransport.cs`
- **Codec**: PCMU (G.711 ?-law) at 8kHz
- **Capabilities**:
  - Inbound: Receives audio from browser microphone
  - Outbound: Sends TTS audio back to browser (future)
- **Events**:
  - `OnInboundAudio` - Fired for each received audio frame
  - `OnStateChanged` - Connection state updates
  - `OnLog` - Diagnostic logging

### Static Events (for MainWindow integration)
- `OnModeChangeRequested` - Fired when web UI changes mode
- `OnWebTextInput` - Fired when text is sent from web UI
- `BroadcastModeChange(int mode)` - Broadcast mode changes to all web clients

## Audio Pipeline

### Inbound (Browser ? App)
1. **Browser captures** microphone audio via `getUserMedia()`
2. **WebRTC encodes** to PCMU (G.711 ?-law, 8kHz mono)
3. **RTP packets** sent over UDP to WPF application
4. **WebRtcAudioTransport** decodes ?-law to PCM16
5. **MainWindow** upsamples 8kHz ? 16kHz for Vosk
6. **WebRTC RMS normalization (AGC)**: the app applies smooth RMS normalization to WebRTC audio before feeding Vosk to improve recognition on quiet mobile mics
7. **VoiceRecognizer** processes via `ProcessAudio()`
8. **Vosk STT** produces transcription
9. **OnTranscription** event triggers LLM dispatch

### Isolation in WebRTC mode
When `AudioInMode.WebRtcVoice` is active:
- Local mic capture is disabled in `VoiceRecognizer`.
- Discord voice input is disabled.
- System loopback capture is blocked (see `DiscordSystemAudioCapture`) to prevent desktop audio/mic sidetone from contaminating STT.

### Text Input (Browser ? App)
1. **Browser sends** `{ type: "text", text: "..." }` via WebSocket
2. **Server fires** `OnWebTextInput` event
3. **MainWindow** updates UI and sends to LLM
4. **Server broadcasts** transcription to all web clients
5. **LLM response** streams back via `response_chunk` messages

### Outbound (App ? Browser) - Future
1. TTS generates PCM16 at 16kHz
2. Downsample to 8kHz
3. Encode to PCMU
4. Send via RTP to browser
5. Browser plays via `<audio>` element

## Configuration

### Settings (`AppSettings`)
```json
{
  "webRtc": {
    "enabled": true,
    "port": 8787
  }
}
```

### Audio Input Mode
Set `app.inputMode` to `"webrtc"` to enable WebRTC audio processing:
```json
{
  "app": {
    "inputMode": "webrtc"
  }
}
```

Note: `inputMode` values map to `AudioInMode` enum:
- `0` = LocalMic
- `1` = DiscordVoice
- `2` = SystemLoopback
- `3` = WebRtcVoice

## Usage

### Connecting from Mobile
1. Ensure WebRTC is enabled in settings
2. Note the displayed URL in WebRTC Settings (e.g., `http://192.168.1.34:8787/`)
3. Open URL in mobile browser (Safari on iOS, Chrome on Android)
4. Web UI loads with chat interface and mode selector
5. To use voice: select WebRTC mode, tap voice button, grant mic permissions

### Text Chat (Any Mode)
1. Open the web UI URL
2. Type a message in the input box
3. Press Enter or tap Send
4. Message appears in web UI and desktop UI
5. AI response streams back to both interfaces

### Voice Chat (WebRTC Mode Only)
1. Select "WebRTC" in the mode selector
2. Tap the green microphone button
3. Grant microphone permissions when prompted
4. Button turns red when recording
5. Audio level meter shows microphone input
6. Speak - audio streams to app for recognition

## Troubleshooting

### No Audio Received
1. **Check firewall**: Port 8787 must be open for TCP (HTTP) and UDP (RTP)
2. **Same network**: Phone and PC must be on same LAN subnet
3. **HTTPS on iOS**: Safari may require HTTPS for `getUserMedia()` on non-localhost
4. **Run as admin**: HTTP listener may need admin rights for `http://+:8787/`

### Connection Failed
1. Check console for `[WebRTC]` and `[Signaling]` log messages
2. Verify WebSocket connection in browser dev tools
3. Check ICE candidate exchange in logs

### Mode Not Syncing
1. Check console for `[WebRTC] Broadcasting mode change: X to Y clients`
2. If `Y = 0`, no clients are connected
3. Hard refresh the web UI to reconnect WebSocket

### Codec Issues
- Verify `PT=0` (PCMU) in audio packet logs
- Check SDP negotiation in logs shows PCMU codec

## Comparison: WebRTC vs Legacy

| Feature | WebRTC | Mumble (Removed) | TeamTalk (Removed) |
|---------|--------|------------------|-------------------|
| Client | Any browser | Mumble client | TeamTalk client |
| Setup | Zero install | Client install | Client install |
| Mobile | ? Native support | ?? Limited | ?? Limited |
| Codec | PCMU/PCMA | Opus | Speex/Opus |
| Latency | Low (~50ms) | Low | Medium |
| Complexity | Simple | Complex | Complex |
| Text Chat | ? Built-in | ? | ? |
| Mode Control | ? Built-in | ? | ? |

## Files

| File | Purpose |
|------|---------|
| `Services/Voice/WebRtcAudioTransport.cs` | WebRTC peer connection and audio handling |
| `Services/Voice/WebRtcSignalingServer.cs` | HTTP server, web client, WebSocket signaling, chat relay |
| `Services/Voice/IVoiceTransport.cs` | Transport interface |
| `Services/Voice/DroppingAudioQueue.cs` | Bounded audio frame queue |
| `Settings/UI/WebRtcSettingsView.xaml` | Settings UI panel |
| `wwwroot/webrtc/index.html` | Web client HTML/CSS |
| `wwwroot/webrtc/client.js` | Web client JavaScript |

## Security Considerations

- **LAN-only**: No STUN/TURN servers configured; works only on local network
- **No encryption**: Audio streams are unencrypted (suitable for home LAN)
- **No authentication**: Anyone on the network can connect
- For production/internet use, add TURN servers and DTLS-SRTP

## Future Enhancements

1. **TTS return path**: Send synthesized speech back to mobile browser
2. **Opus codec**: Add Opus support for better quality/compression
3. **Multiple clients**: Support concurrent mobile connections with different speakers
4. **HTTPS/WSS**: Secure transport for broader browser compatibility
5. **Push notifications**: Notify mobile when AI responds
