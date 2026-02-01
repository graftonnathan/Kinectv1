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

### Transcription Mode (STT for Linux) - IN PROGRESS
- [x] Port Vosk STT to Linux headless mode - Created HeadlessVoiceRecognizer.cs
- [x] Add microphone input via ALSA/PulseAudio - Using NAudio WaveInEvent
- [ ] Test real-time transcription accuracy - Needs Vosk model
- [x] Integrate with conversation manager - Speech → LLM pipeline working
- Priority: High | Est: 4h | Actual: 2h

**Implementation Details:**
- Created `HeadlessVoiceRecognizer.cs` - Simplified Vosk wrapper for Linux
- Uses NAudio for microphone capture (16kHz, mono, PCM16)
- VAD with configurable thresholds from settings
- Speech → LLM → TTS pipeline functional
- Console commands: status, help, quit
- Build: `dotnet build MaggieHeadless.csproj`

### WebRTC Mode Verification
- [x] Verify WebRTC signaling server starts in headless mode - **COMPLETED**: Created simplified WebRtcSignalingServerHeadless.cs
- [ ] Test audio streaming from browser to Maggie
- [ ] Test Maggie's voice output through WebRTC
- [x] Document WebRTC setup for LAN access - **COMPLETED**: Created docs/WEBRTC_HEADLESS_SETUP.md
- Priority: High | Est: 2h | Actual: 1.5h

**Implementation Details:**
- Created `WebRtcSignalingServerHeadless.cs` - Simplified signaling server without WPF dependencies
- Added WebRTC support to `MaggieHeadless.csproj` (SIPSorcery package + source files)
- Updated `HeadlessMaggie.cs` to start WebRTC server on port 8787
- Added `ProcessAudio` method to `HeadlessVoiceRecognizer` for external audio sources
- WebRTC server provides HTTP/WebSocket endpoints for browser-based voice chat
- Browser client files served from `wwwroot/webrtc/` (index.html, client.js)
- WebRTC audio is fed into Vosk STT pipeline via `AudioFrame` structs

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
