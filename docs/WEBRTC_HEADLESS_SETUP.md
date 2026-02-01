# WebRTC Setup for Maggie Linux Headless

## Overview

WebRTC mode allows you to use your mobile device's browser as a microphone and speaker for Maggie. This is useful for:
- Voice input from anywhere on your LAN
- Hearing Maggie's responses through your phone's speaker
- Chat interface for text-based interaction

## How It Works

1. **WebRTC Signaling Server** - Runs on Maggie (port 8787 by default)
2. **Browser Client** - Connects from your phone/tablet
3. **Audio Streaming** - Browser captures microphone and streams to Maggie
4. **TTS Playback** - Maggie's voice responses stream back to the browser

## Starting WebRTC in Headless Mode

### Prerequisites
- Maggie headless built successfully
- TTS service running (optional, for voice output)
- Vosk STT model (for speech recognition)

### Configuration

WebRTC settings are in your `settings.json`:

```json
{
  "webRtc": {
    "enabled": true,
    "port": 8787,
    "httpsEnabled": false,
    "httpsPort": 8788
  }
}
```

### Starting Maggie

```bash
cd ~/workspace/maggie
export PATH="$HOME/.dotnet:$PATH"
dotnet run --project MaggieHeadless.csproj
```

You should see output like:
```
📡 Starting WebRTC signaling server...
   [WebRTC] HTTP server started on port 8787
   [WebRTC] WebRTC URL: http://192.168.1.34:8787/
   ✓ WebRTC signaling on http://localhost:8787
   ✓ WebRTC LAN access: http://192.168.1.34:8787
```

## Connecting from Your Phone

### 1. Find Maggie's IP Address

Look for the LAN access URL in the startup output:
```
✓ WebRTC LAN access: http://192.168.1.34:8787
```

### 2. Open Browser on Phone

- Open Safari (iOS) or Chrome (Android)
- Navigate to `http://192.168.1.34:8787`

### 3. Grant Permissions

- Allow microphone access when prompted
- For iOS: Ensure Safari has microphone permission in Settings

### 4. Use WebRTC Mode

- Select "WebRTC" mode from the 3-position switch
- Tap the microphone button to start speaking
- Maggie will respond through your phone's speaker

## Troubleshooting

### Connection Failed

1. **Check firewall** - Port 8787 must be open:
   ```bash
   sudo ufw allow 8787/tcp
   sudo ufw allow 8787/udp
   ```

2. **Same network** - Phone and Maggie must be on same LAN

3. **Check IP address** - Verify Maggie's IP hasn't changed:
   ```bash
   ip addr show
   ```

### No Audio Received

1. **Check browser console** for errors (use remote debugging)
2. **Verify microphone permissions** in browser settings
3. **Try HTTPS** on iOS (some versions require HTTPS for getUserMedia)

### iOS Safari Issues

iOS Safari may require HTTPS for microphone access. Enable HTTPS in settings:
```json
{
  "webRtc": {
    "httpsEnabled": true,
    "httpsPort": 8788
  }
}
```

Note: Self-signed certificates will show a security warning.

### STT Not Working

1. **Verify Vosk model** is downloaded:
   ```bash
   ls models/vosk-model-en-us-0.22/
   ```

2. **Check audio is flowing** - Look for `[WebRTC][Audio]` messages in console

## Network Architecture

```
┌─────────────────┐      HTTP/WebSocket      ┌─────────────────┐
│  Mobile Browser │ ◄──────────────────────► │  Maggie Server  │
│                 │      Port 8787           │  (WebRTC)       │
│  • Microphone   │                          │                 │
│  • Speaker      │      Audio (WebSocket)   │  • Vosk STT     │
│  • Chat UI      │ ◄──────────────────────► │  • Qwen3-TTS    │
└─────────────────┘                          └─────────────────┘
```

## API Endpoints

- `GET /` - WebRTC client UI (HTML)
- `GET /client.js` - Client JavaScript
- `GET /api/status` - Current mode and settings
- `POST /api/mode` - Change audio input mode
- `WS /ws` - WebSocket for audio streaming and chat

## Security Notes

- **LAN-only**: No STUN/TURN servers configured; works only on local network
- **No encryption**: Audio streams are unencrypted (suitable for home LAN)
- **No authentication**: Anyone on the network can connect

For production/internet use, add:
- HTTPS/WSS for secure transport
- Authentication mechanism
- TURN servers for NAT traversal

## Status Check

Run the `status` command in Maggie console:
```
> status

📊 Maggie Status:
   🎤 STT Ready: True
   🎤 Mic Enabled: True
   🎙️  TTS Enabled: True
   📡 WebRTC Enabled: True (port 8787)
   🧠 LLM Provider: LMStudio
```

## Next Steps

- Test audio streaming from browser
- Test Maggie's voice output through WebRTC
- Verify transcription accuracy with mobile microphone
