# WebRTC LAN Audio Bridge

## Overview

The WebRTC Audio Bridge provides a browser-based interface for capturing audio from mobile devices (iOS/Android) and streaming it to the Kinect application for speech recognition. This replaces the legacy Mumble and TeamTalk integrations.

## Architecture

```
???????????????????     WebSocket/RTP      ???????????????????????
?  Mobile Browser ? ?????????????????????? ?   WPF Application   ?
?  (Safari/Chrome)?                        ?                     ?
?                 ? ?????????????????????? ?   WebRTC Transport  ?
?  Microphone     ?     TTS Audio (PCMU)   ?   + Signaling       ?
???????????????????                        ???????????????????????
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

## Components

### WebRtcSignalingServer
- **Location**: `Services/Voice/WebRtcSignalingServer.cs`
- **Port**: Configurable via `webRtc.port` setting (default: 8787)
- **Purpose**: HTTP server hosting the web client and WebSocket signaling
- **Endpoints**:
  - `/` or `/index.html` - Web client UI
  - `/client.js` - WebRTC client JavaScript
  - WebSocket upgrade - Signaling channel

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

## Audio Pipeline

### Inbound (Browser ? App)
1. **Browser captures** microphone audio via `getUserMedia()`
2. **WebRTC encodes** to PCMU (G.711 ?-law, 8kHz mono)
3. **RTP packets** sent over UDP to WPF application
4. **WebRtcAudioTransport** decodes ?-law to PCM16
5. **MainWindow** upsamples 8kHz ? 16kHz for Vosk
6. **VoiceRecognizer** processes via `ProcessExternalAudio()`
7. **Vosk STT** produces transcription
8. **OnTranscription** event triggers LLM dispatch

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

## Usage

### Connecting from Mobile
1. Select "WebRTC" checkbox in the Audio Input panel
2. Note the displayed URL (e.g., `http://192.168.1.34:8787/`)
3. Open URL in mobile browser (Safari on iOS, Chrome on Android)
4. Tap "Join Voice" button
5. Grant microphone permissions when prompted
6. Speak - audio streams to app for recognition

### Web Client Features
- Real-time RMS meter showing microphone level
- Connection status indicator
- Automatic codec negotiation (prefers PCMU)
- No STUN/TURN servers needed (LAN-only)

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

### Codec Issues
- Verify `PT=0` (PCMU) in audio packet logs
- Check SDP negotiation in logs shows PCMU codec

## Comparison: WebRTC vs Legacy

| Feature | WebRTC | Mumble (Removed) | TeamTalk (Removed) |
|---------|--------|------------------|-------------------|
| Client | Any browser | Mumble client | TeamTalk client |
| Setup | Zero install | Client install | Client install |
| Mobile | ? Native support | ? Limited | ? Limited |
| Codec | PCMU/PCMA | Opus | Speex/Opus |
| Latency | Low (~50ms) | Low | Medium |
| Complexity | Simple | Complex | Complex |

## Files

| File | Purpose |
|------|---------|
| `Services/Voice/WebRtcAudioTransport.cs` | WebRTC peer connection and audio handling |
| `Services/Voice/WebRtcSignalingServer.cs` | HTTP server, web client, WebSocket signaling |
| `Services/Voice/IVoiceTransport.cs` | Transport interface |
| `Services/Voice/DroppingAudioQueue.cs` | Bounded audio frame queue |
| `Settings/UI/WebRtcSettingsView.xaml` | Settings UI panel |

## Security Considerations

- **LAN-only**: No STUN/TURN servers configured; works only on local network
- **No encryption**: Audio streams are unencrypted (suitable for home LAN)
- **No authentication**: Anyone on the network can connect
- For production/internet use, add TURN servers and DTLS-SRTP

## Future Enhancements

1. **TTS return path**: Send synthesized speech back to mobile browser
2. **Opus codec**: Add Opus support for better quality/compression
3. **Multiple clients**: Support concurrent mobile connections
4. **HTTPS/WSS**: Secure transport for broader browser compatibility
