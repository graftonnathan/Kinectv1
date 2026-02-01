# Maggie Development Todo List
# Auto-execution: Jeff will start working on high priority tasks when Nathan is idle >15 mins
# Format: [ ] Task description | Priority: High/Medium/Low | Est: Xh

## Auto-Execution Rules
- When Nathan is idle for >15 minutes, Jeff spawns an isolated agent
- Agent reads this TODO.md and picks highest priority incomplete task
- Agent works autonomously for up to 30 minutes
- Agent reports progress back to main session
- If questions arise, agent asks before proceeding

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
- [x] **Optimize for lower response latency** - COMPLETED: Disabled query rewrite (500ms-2s savings), increased chunk size 150→500, reduced SEARCH_TOP_K 20→10, tuned memory settings. See `docs/latency-optimization-report.md` | Priority: High | Est: 3h | Actual: 2h
- [ ] Profile memory usage during long conversations

### Voice Design
- [ ] **Fix pure voice design** - Currently falling back to base voice because 0.6B CustomVoice model doesn't support generate_voice_design(). Need to either switch to VoiceDesign model, load both models, or find alternative approach | Priority: High | Est: 2h
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
