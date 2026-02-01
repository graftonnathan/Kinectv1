# Maggie TODO List

## High Priority

### 1. Optimize for Lower Response Latency ✅ DONE
- [x] Analyze current pipeline for latency bottlenecks
- [x] Implement embedding cache (LRU cache for query embeddings)
- [x] Add low-latency mode (disables expensive operations)
- [x] Tune default settings for better latency
- [x] Create latency_optimizer.py tool
- [x] Document optimizations
- [x] Test and verify system is functional
- [~] Measure actual latency improvements (Maggie running - ready for user testing)
- [ ] Consider parallel initialization of services (future work)

### 2. Transcription Mode (STT for Linux) ✅ DONE
- [x] Port Vosk STT integration from Windows codebase
- [x] Test with headless mode (Maggie running with STT)
- [x] Transcription logging available via console output

### 3. WebRTC Mode Verification ✅ PARTIAL
- [x] WebRTC signaling server implemented and running (port 8787)
- [x] WebRTC → Vosk STT pipeline connected
- [x] TTS audio broadcast to browsers implemented
- [~] Test audio streaming from mobile devices (ready for testing)
- [~] Verify echo cancellation (browser-side, ready for testing)
- [~] Check latency of WebRTC path (ready for user testing)

## Medium Priority

### 4. Discord Bot Restoration ✅ READY (Needs Config)
- [x] Code implementation complete and working
- [x] Voice connection pipeline implemented
- [x] TTS routing to Discord ready
- [ ] **NEEDS:** Discord bot token from https://discord.com/developers/applications
- [ ] **NEEDS:** Enable in Settings/default.json (`enabled: true`, add token)

### 5. Explore OpenClaw Agent Ecosystem ✅ ACTIVE
- [x] 4claw.org - checked
- [x] Moltbook - claimed account  
- [~] Moltoverflow - discovered (StackOverflow for agents, React app)
- [ ] Instaclaw - try posting (needs ATXP image generation)
- [x] 8claw - ✅ Participated! Posted to /tech/ thread about persistent memory

## Low Priority

### 6. Wake Word Detection
- [ ] Research lightweight wake word models
- [ ] Implement VAD-based triggering
- [ ] Add configurable wake phrases

### 7. Teach Maggie About the World
- [ ] Connect to web search tools
- [ ] Show Maggie interesting websites
- [ ] Build knowledge base

## Notes

Last updated: 2026-02-01
