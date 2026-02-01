# Discord Bot Setup Guide for Maggie

## Quick Summary
✅ **Implementation Status:** Complete and ready  
⏳ **Your Action Needed:** Create bot + add token to settings

---

## Step 1: Create a Discord Application

1. Go to: https://discord.com/developers/applications
2. Click **"New Application"**
3. Name it (e.g., "Maggie Bot")
4. Navigate to **Bot** section in left sidebar
5. Click **"Add Bot"** → **"Yes, do it!"**
6. Under **Token**, click **"Reset Token"** → **"Copy"**
   - ⚠️ **SAVE THIS TOKEN SECURELY** - you can only see it once!

### Required Bot Permissions

Under **Privileged Gateway Intents**, enable:
- ✅ **SERVER MEMBERS INTENT**
- ✅ **MESSAGE CONTENT INTENT**
- ✅ **PRESENCE INTENT**

Under **OAuth2 → URL Generator**:
- Select scope: **bot**
- Bot permissions:
  - **Send Messages**
  - **Connect** (voice)
  - **Speak** (voice)
  - **Use Voice Activity**
  - **Read Message History**

Copy the generated URL and open it to invite Maggie to your Discord server.

---

## Step 2: Configure Maggie

Edit `~/workspace/maggie/Settings/default.json`:

```json
"discord": {
  "enabled": true,
  "prefix": "!",
  "autoJoinVoice": false,
  "token": "YOUR_BOT_TOKEN_HERE"
}
```

Or let me apply this change when you have the token.

---

## Step 3: Test the Bot

Start Maggie with Discord enabled and try these commands in Discord:

| Command | Description |
|---------|-------------|
| `!help` | Show available commands |
| `!join` | Join your voice channel |
| `!leave` | Leave voice channel |
| `!testtts Hello world` | Test TTS in Discord |
| `!testaudio` | Test audio pipeline |

---

## Features Available

Once configured, Maggie can:
- ✅ Join/leave voice channels on command
- ✅ Stream TTS responses to Discord voice
- ✅ Receive voice input from Discord users (Vosk STT)
- ✅ Respond to text commands
- ✅ Auto-reconnect on disconnections
- ✅ Preemptible TTS queue (new requests cancel old ones)

---

## Security Notes

- Keep your bot token **private** - never commit it to git
- The token grants full bot control
- Maggie has single-instance protection (prevents token conflicts)

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Bot doesn't appear online | Check token is correct, restart Maggie |
| Can't hear TTS | Verify Discord output device in Windows |
| Voice recognition not working | Check Vosk model path in settings |
| 4006/session errors | Bot will auto-retry; check network |

---

*Generated: 2026-02-01 by Jeff (cron task)*
