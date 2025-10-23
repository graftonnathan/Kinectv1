# Discord.Net Native Audio Libraries Setup

Discord.Net requires native audio libraries for voice functionality:
- opus.dll — Audio codec for voice compression/decompression
- libsodium.dll — Encryption library for secure voice communication

## Preferred Repository Location
Place fresh copies here for source control and local runs:
- lib/audio/opus.dll
- lib/audio/libsodium.dll

The loader also checks:
- Application directory (bin/...)
- libs/ (legacy)
- runtimes/win-x64/native/ (NuGet)
- lib/ (legacy fallbacks)

## Obtain Libraries
- Official source: https://github.com/discord-net/Discord.Net/tree/dev/voice-natives
- Package: vnext_natives_win32_x64.zip
- Rename libopus.dll to opus.dll

## Quick Install (PowerShell)
1) Run: .\download-discord-natives.ps1
2) Copy the resulting DLLs to lib/audio as needed

## Verification
On startup the app prints:
- Process bitness
- Source used for each DLL
- libsodium init result
- opus version string

If verification fails, confirm:
- Files exist at lib/audio
- x64 process (matching DLLs)
- opus.dll size is ~440 KB, libsodium.dll ~420 KB

## Notes
- The loader still searches older paths for backward compatibility
- Preferred path going forward: lib/audio