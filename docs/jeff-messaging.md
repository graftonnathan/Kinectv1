# Jeff-Maggie WebUI Messaging System

## Overview

This document explains how Jeff (the OpenClaw assistant) can send messages to Maggie that appear in her WebUI chat window with special green styling.

## Architecture

```
┌─────────┐     HTTP POST      ┌─────────────────┐     WebSocket      ┌──────────┐
│  Jeff   │ ─────────────────> │  Maggie         │ ─────────────────> │ WebUI    │
│ (API)   │  /api/chat         │  (Jeff API      │  jeff_message     │ (Browser)│
│         │  speaker: "Jeff"   │   + WebRTC      │  (instant)        │          │
│         │                    │   Server)       │                   │          │
└─────────┘                    └─────────────────┘                   └──────────┘
                                      │
                                      │ HTTP Polling (backup)
                                      │ /api/jeff/messages
                                      │ (every 2 seconds)
                                      ▼
                               ┌──────────────┐
                               │ Message Queue│
                               │ (10s retention)
                               └──────────────┘
```

## How It Works

### 1. Sending Messages

Jeff sends messages to Maggie via the `/api/chat` endpoint:

```bash
curl -X POST http://localhost:18790/api/chat \
  -H "Content-Type: application/json" \
  -d '{"speaker": "Jeff", "message": "Hello Maggie!"}'
```

### 2. Message Processing

When Maggie receives a message with `speaker: "Jeff"`:

1. **Queue for Polling**: The message is added to a queue that persists for 10 seconds
2. **WebSocket Broadcast**: The message is immediately broadcast via WebSocket to all connected WebUI clients
3. **LLM Processing**: Maggie processes the message and generates a response

### 3. WebUI Display

The WebUI receives Jeff messages in two ways:

#### Primary: WebSocket (Instant)
- Message type: `jeff_message`
- Appears immediately (no delay)
- Handled by `ws.onmessage` in client.js

#### Backup: HTTP Polling (2-second delay)
- Endpoint: `/api/jeff/messages`
- Polls every 2 seconds
- Deduplication prevents duplicate displays

### 4. Visual Styling

Jeff messages appear with:
- **Green background** (CSS class `.msg.jeff`)
- **"Jeff:" prefix** for speaker identification
- **Left-aligned** like Maggie's messages

## Implementation Details

### Backend Changes

#### JeffApiServer.cs
```csharp
// Removed duplicate queuing from HandleChat
// Messages now queued only in OnChatRequest handler
```

#### WebRtcSignalingServerHeadless.cs
Added `BroadcastJeffMessage()` method:
```csharp
public static void BroadcastJeffMessage(string message)
{
    if (_instance == null) return;
    _instance?.Broadcast(new { 
        type = "jeff_message", 
        text = message, 
        speaker = "Jeff" 
    });
}
```

#### HeadlessMaggie.cs
Hooked up immediate broadcast:
```csharp
Api.JeffApiServer.OnChatRequest += async (message) =>
{
    Console.WriteLine($"\n📨 Jeff: {message}");
    // Queue for polling backup
    Api.JeffApiServer.QueueJeffMessage(message);
    // Broadcast immediately via WebSocket
    Kinectv1.Voice.WebRtcSignalingServer.BroadcastJeffMessage(message);
    return await ProcessChatMessage("Jeff", message, true);
};
```

### Frontend Changes

#### client.js - WebSocket Handler
Added case for `jeff_message`:
```javascript
case 'jeff_message':
    if (msg.text) {
        addMsg(msg.text, 'jeff', msg.speaker || 'Jeff');
        console.log('[WS] Jeff message received:', msg.text);
    }
    break;
```

#### client.js - Deduplication
```javascript
// Track displayed Jeff message IDs to prevent duplicates
var _displayedJeffMessageIds = new Set();

// In pollJeffMessages():
if (msgId && _displayedJeffMessageIds.has(msgId)) {
    console.log('[Jeff] Skipping duplicate message:', msgId);
    return;
}
if (msgId) {
    _displayedJeffMessageIds.add(msgId);
}
```

#### index.html - CSS Styling
```css
.msg.jeff {
    align-self: flex-start;
    background: var(--green);  /* #22C55E */
    border-bottom-left-radius: 3px;
}
```

## API Endpoints

### POST /api/chat
Main chat endpoint for sending messages to Maggie.

**Request:**
```json
{
  "speaker": "Jeff",
  "message": "Your message here"
}
```

**Response:**
```json
{
  "success": true,
  "speaker": "Jeff",
  "received": "Your message here",
  "response": "Maggie's response"
}
```

### GET /api/jeff/messages
Poll for Jeff messages (backup method).

**Response:**
```json
{
  "messages": [
    {
      "Id": "uuid",
      "Text": "Message text",
      "Timestamp": 1770049506598
    }
  ],
  "count": 1
}
```

### POST /api/jeff/message
Direct endpoint for queuing Jeff messages.

**Request:**
```json
{
  "message": "Message text"
}
```

## Configuration

### Message Retention
- **Duration**: 10 seconds
- **Purpose**: Allows multiple clients to receive messages via polling
- **Location**: `JeffApiServer.cs` - `MessageRetentionMs`

### Polling Interval
- **Interval**: 2000ms (2 seconds)
- **Purpose**: Backup method when WebSocket unavailable
- **Location**: `client.js` - `JEFF_POLL_INTERVAL_MS`

### Deduplication
- **Method**: Track displayed message IDs in Set
- **Scope**: Per-client (browser session)
- **Fallback**: Timestamp + content hash for messages without IDs

## Testing

### Verify Single Message Delivery
```bash
curl -s -X POST http://localhost:18790/api/chat \
  -H "Content-Type: application/json" \
  -d '{"speaker": "Jeff", "message": "Test"}'
```

Expected: Message appears exactly once in WebUI.

### Verify Instant Delivery
1. Open WebUI
2. Send message via API
3. Message should appear immediately (before Maggie's response)

### Verify No Duplicates
Send multiple messages rapidly - each should appear exactly once.

## Troubleshooting

### Messages Not Appearing
1. Check if Maggie is running: `curl http://localhost:18790/health`
2. Verify WebSocket connection in browser console
3. Check for `[Jeff] Polling started` in console

### Duplicate Messages
1. Clear browser cache (Ctrl+F5)
2. Check `_displayedJeffMessageIds` Set in console
3. Verify only one Maggie process is running

### Delayed Messages
1. Check WebSocket connection status
2. Verify polling fallback is working: `curl http://localhost:8787/api/jeff/messages`
3. Check network latency

## Files Modified

### Backend
- `Api/JeffApiServer.cs` - Message queue management
- `HeadlessMaggie.cs` - WebSocket broadcast hookup
- `Services/Voice/WebRtcSignalingServerHeadless.cs` - Broadcast method

### Frontend
- `wwwroot/webrtc/client.js` - WebSocket handler + deduplication
- `wwwroot/webrtc/index.html` - CSS styling (pre-existing)

## Future Enhancements

- [ ] Bidirectional messaging (Maggie → Jeff)
- [ ] Message history/persistence
- [ ] Typing indicators
- [ ] Message read receipts
- [ ] Direct WebSocket connection between Jeff and Maggie

## References

- OpenClaw Gateway: http://localhost:18789
- Maggie WebRTC UI: http://localhost:8787
- Maggie API: http://localhost:18790
