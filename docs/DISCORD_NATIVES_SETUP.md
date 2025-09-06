# Discord.Net Native Audio Libraries Setup

## Problem ? RESOLVED
Discord.Net requires native audio libraries for voice functionality:
- **opus.dll** - Audio codec for voice compression/decompression (~441KB)
- **libsodium.dll** - Encryption library for secure voice communication (~421KB)

## Previous Error ? FIXED
```
System.DllNotFoundException: Unable to load DLL 'opus': The specified module could not be found.
TaskCanceledException: A task was canceled.
Kinect initialization timeout
```

## Solution ? COMPLETED + ENHANCED

### ?? **Fresh Installation + Voice Handshake Enhancement**
The issue was resolved by:
1. **Fresh Native Libraries** from official Discord.Net ZIP package
2. **Enhanced Voice Handshake** with proper connection management
3. **Native Verification** using DLL imports for early error detection

**Source:** https://github.com/discord-net/Discord.Net/tree/dev/voice-natives  
**Package:** vnext_natives_win32_x64.zip  
**Critical Fix:** `libopus.dll` ? `opus.dll` (Discord.Net expects 'opus.dll')

### ?? **Enhanced Voice Handshake Features**

#### **Native Library Verification**
```csharp
[DllImport("opus")] private static extern IntPtr opus_get_version_string();
[DllImport("libsodium")] private static extern int sodium_init();
```
- ? Verifies libraries can actually be loaded before connection attempts
- ? Shows opus version and libsodium initialization status
- ? Catches bitness mismatches (x64 vs x86)

#### **One Voice Per Guild Management**
```csharp
private static readonly ConcurrentDictionary<ulong, IAudioClient> _audioClients = new();
```
- ? Ensures only one voice connection per Discord guild
- ? Properly cleans up existing connections before creating new ones
- ? Prevents connection conflicts and resource leaks

#### **Enhanced Connection Process**
```csharp
GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates;
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
```
- ? Uses proper gateway intents for voice functionality
- ? Implements 20-second timeout with smart retry logic
- ? Progressive backoff: 3 attempts with detailed logging

### Current Installation Status
```
? libs/opus.dll (441,856 bytes) - Fresh from ZIP, renamed for compatibility
? libs/libsodium.dll (421,376 bytes) - Fresh from ZIP
? bin/Debug/net481/opus.dll - Installed
? bin/Debug/net481/libsodium.dll - Installed
? bin/x64/Debug/net481/* - Installed (x64 support)
? Native verification: PASSED
? Voice handshake: Enhanced with connection management
```

### ?? **Enhanced Commands**

#### **!testvoice** - Comprehensive Testing
```
?? Testing Discord.Net voice system...
?? Process bitness: x64
?? libsodium init: 0 ? SUCCESS
?? opus version: libopus 1.3.1 ? SUCCESS
?? Native voice libraries verification PASSED
```

#### **!reload** - Smart Library Management
```
?? Reloading Discord.Net native libraries...
?? Verifying reloaded libraries...
? Native libraries reloaded and verified successfully!
```

#### **!status** - Enhanced Diagnostics
```
?? Native Libraries:
Libraries Loaded: ? Yes
opus.dll: ? Available
libsodium.dll: ? Available
Process: x64 ?
Sodium Init: ? OK
Opus Version: ? libopus 1.3.1

?? Voice Connections:
Active Connections: 1
Guild 123456789: Connected
```

### Automatic Installation Script
```powershell
.\download-discord-natives.ps1
```

**Enhanced features:**
1. Downloads official ZIP from Discord.Net repository
2. Extracts and renames `libopus.dll` ? `opus.dll`
3. Installs to multiple output directories for compatibility
4. Removes old corrupted files
5. Verifies file integrity (sizes: opus=441KB, libsodium=421KB)
6. Supports x64 architecture

### Manual Installation (Alternative)
If the script fails:
1. Download: https://github.com/discord-net/Discord.Net/raw/dev/voice-natives/vnext_natives_win32_x64.zip
2. Extract `libopus.dll` and `libsodium.dll`
3. **CRITICAL:** Rename `libopus.dll` to `opus.dll`
4. Place in directories:
   - `libs/opus.dll` ?
   - `libs/libsodium.dll` ?
   - `bin/Debug/net481/opus.dll` ?
   - `bin/Debug/net481/libsodium.dll` ?
   - `bin/x64/Debug/net481/opus.dll` ?
   - `bin/x64/Debug/net481/libsodium.dll` ?

### Voice Connection Process
```
?? Discord.Net Join command executing...
?? Verifying native voice dependencies...
?? Process bitness: x64
?? libsodium init: 0 ? SUCCESS
?? opus version: libopus 1.3.1 ? SUCCESS
?? Native voice libraries verification PASSED
?? Cleaning up existing voice connection for guild...
?? Bot permissions - Connect: True, Speak: True, UseVAD: True
?? Connecting to Discord voice with 20s timeout...
? Voice connection successful on attempt 1!
?? Audio client state: Connected
?? Voice Connected
? Voice connection verified and stable
```

### Enhanced Loader Verification
The `DiscordNativeLoader` now:
1. **Checks multiple locations** (Application, libs, runtimes, legacy)
2. **Verifies file integrity** (size checks)
3. **Tests actual functionality** (DLL imports)
4. **Provides detailed diagnostics** (loading sources, verification status)

### Testing Process
1. **Start application** ? Native libraries verified automatically
2. **Use `!testvoice`** ? Comprehensive voice system test
3. **Use `!join <channel>`** ? Enhanced connection with handshake
4. **Monitor console** ? Detailed connection diagnostics
5. **Use `!status`** ? Real-time system status

### Expected Results

#### **Before Enhancement:**
```
? Kinect initialization timeout
? System.Threading.Tasks.TaskCanceledException
? Connection timed out after 10 seconds
? A task was canceled
```

#### **After Enhancement:**
```
? Native voice libraries verification PASSED
? Voice connection successful on attempt 1!
? Voice connection verified and stable
? Audio connection is stable and ready
```

### Technical Details
- **File Sources:** Official Discord.Net vnext natives package
- **Platform:** Windows x64
- **File Integrity:** Verified (opus: 441,856 bytes, libsodium: 421,376 bytes)
- **Compatibility:** .NET Framework 4.8.1, x64 platform target
- **Loader:** Enhanced with native verification and connection management
- **Voice Handshake:** One client per guild with proper cleanup
- **Gateway Intents:** Guilds + GuildVoiceStates for voice functionality

### Troubleshooting
If you still encounter issues:
1. **Check console** for native verification results
2. **Use `!testvoice`** for comprehensive diagnostics
3. **Verify bitness** (must be x64 for Discord voice)
4. **Check file sizes** (opus=441KB, libsodium=421KB)
5. **Use `!reload`** to refresh libraries without restart
6. **Run as Administrator** if permissions issues occur

### Alternative: System Audio Capture
If native voice still doesn't work, system audio capture mode bypasses Discord voice gateway entirely.