# Maggie Development Todo List
# Executed daily via cron job
# Format: [ ] Task description | Priority: High/Medium/Low | Est: Xh

## Active Tasks

### Transcription Mode (STT for Linux)
- [ ] Port Vosk STT to Linux headless mode
- [ ] Add microphone input via ALSA/PulseAudio
- [ ] Test real-time transcription accuracy
- [ ] Integrate with conversation manager
- Priority: High | Est: 4h

### WebRTC Mode Verification
- [ ] Verify WebRTC signaling server starts in headless mode
- [ ] Test audio streaming from browser to Maggie
- [ ] Test Maggie's voice output through WebRTC
- [ ] Document WebRTC setup for LAN access
- Priority: High | Est: 2h

### Discord Bot Restoration
- [ ] Add Discord.NET to headless project references
- [ ] Restore Discord voice channel integration
- [ ] Test bot commands in Discord server
- [ ] Verify TTS works in Discord voice channels
- Priority: Medium | Est: 3h

## Backlog

### Audio Improvements
- [ ] Add local audio output (not just browser)
- [ ] Support multiple audio output devices
- [ ] Add audio volume normalization

### Performance
- [ ] **Optimize for lower response latency** - Maggie is slow to respond. Check memory settings and pipeline for improvements. | Priority: High | Est: 3h
- [ ] Profile memory usage during long conversations
- [ ] Optimize vector memory search speed
- [ ] Add conversation archiving for old sessions

### Features
- [ ] Add wake word detection (local)
- [ ] Support for multiple concurrent speakers
- [ ] Integration with home automation APIs

## Completed
- [x] Fix TTS to use GPU on RTX 2080
- [x] Add LAN access to web UI
- [x] Create TTS proxy endpoints
- [x] Push linux branch to GitHub
