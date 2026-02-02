# Discord Bot Configuration for Maggie

To restore Maggie's Discord integration, follow these steps:

1. Create a new Discord bot at https://discord.com/developers/applications
2. Copy the bot token
3. Update the settings file at `Settings/default.json`:
   - Set `"enabled": true` in the `"discord"` section
   - Add your bot token to `"token": "YOUR_BOT_TOKEN_HERE"`
   - Optionally adjust other settings like prefix or autoJoinVoice

Example configuration:
```json
"discord": {
  "enabled": true,
  "prefix": "!",
  "autoJoinVoice": false,
  "token": "YOUR_BOT_TOKEN_HERE"
}
```

4. Restart Maggie to apply the changes