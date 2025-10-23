# Hosted Services Manager Implementation (Updated)

## Overview
Centralizes lifecycle management of long?running features. For TTS, the previous layered Coqui/Kokoro wrappers have been unified behind `TtsService` + `TtsPlaybackController` (no background hosted loop required).

## Key Components

### IHostedService (if retained)
Used for services that truly need background loops (e.g., Discord bot). `TtsService` is on?demand and typically not registered as a hosted service anymore.

### HostedServicesManager
- Starts/stops real hosted services (Discord, optional capture, etc.)
- Provides shared cancellation token

### Current Service Set
| Service | Hosted | Notes |
|---------|--------|-------|
| VoiceRecognizer | Yes | Manages mic + external queue loop |
| DiscordNetBotManager | Yes | Voice + command handling |
| TtsService | No | Lazy init on first use; preemption via controller |

## TTS Integration Changes
- Removed `CoquiTtsHostedService` layer
- `TtsPlaybackController.StartUtterance` governs concurrency (single active utterance)
- Cancellation (barge?in / UI) calls `CancelCurrent()` directly – no hosted teardown needed

## Shutdown Considerations
Include early call to `TtsPlaybackController.CancelCurrent()` before disposing audio devices (see clean exit doc). No special StopAsync required for `TtsService`.

## Benefits
- Fewer background threads
- Deterministic TTS cancellation semantics
- Reduced complexity vs legacy wrapper stack