# Hosted Services Manager Implementation

## Overview

This implementation centralizes the management of long-running services in the Kinectv1 application under a single `HostedServicesManager` class with shared cancellation token support and consistent start/stop lifecycle management.

## Key Components

### 1. IHostedService Interface
- Provides consistent contract for all services with `StartAsync` and `StopAsync` methods
- Ensures all services support cancellation tokens
- Includes service name and running status properties

### 2. HostedServicesManager
- Centralized manager for all hosted services
- Provides shared `CancellationToken` for coordinated shutdown
- Implements idempotent `StartAllAsync`/`StopAllAsync` with interlocked guards
- Handles service registration, startup, shutdown, and error reporting
- Supports concurrent service operations with proper timeout handling

### 3. Service Wrappers
Individual hosted service wrappers for existing services:
- `VoiceRecognizerHostedService` - Wraps VoiceRecognizer static methods
- `DiscordBotHostedService` - Wraps DiscordNetBotManager 
- `DiscordSystemAudioCaptureHostedService` - Wraps DiscordSystemAudioCapture
- `CoquiTtsHostedService` - Wraps CoquiTtsService
- `OllamaHostedService` - Wraps OllamaService

## Key Features

### Thread Safety
- All service operations use atomic `Interlocked` operations to prevent race conditions
- Idempotent start/stop operations - safe to call multiple times
- Proper locking around shared state

### Cancellation Token Propagation
- Shared cancellation token created by `HostedServicesManager`
- Propagated to all services during startup
- Services can create linked cancellation tokens for their internal operations
- Coordinated shutdown when main cancellation token is cancelled

### Error Handling
- Individual service failures don't prevent other services from starting/stopping
- Comprehensive error reporting through events
- Timeout handling for startup and shutdown operations
- Graceful degradation on service failures

### Integration Points

#### App.xaml.cs
- Creates global `HostedServicesManager` instance
- Registers all services during startup
- Handles centralized shutdown in `OnExit`

#### MainWindow_Closing
- Uses centralized shutdown through `HostedServicesManager`
- Maintains blocking pattern required for exit handlers
- Proper timeout handling (35 seconds) with fallback

## Testing

### HostedServicesTest
- Comprehensive test suite for service lifecycle
- Tests idempotent operations
- Verifies cancellation token propagation
- Uses `TestHostedService` for dependency-free testing

### Manual Testing
- Ctrl+H keyboard shortcut in MainWindow to run tests
- Ctrl+F for existing identity fusion tests (unchanged)

## Usage Example

```csharp
// Create and configure hosted services manager
var servicesManager = new HostedServicesManager();

// Register services
servicesManager.RegisterService(new VoiceRecognizerHostedService(modelPath));
servicesManager.RegisterService(new DiscordBotHostedService());
servicesManager.RegisterService(new CoquiTtsHostedService());

// Start all services
await servicesManager.StartAllAsync(TimeSpan.FromMinutes(1));

// Get shared cancellation token for other operations
var sharedToken = servicesManager.ServiceCancellationToken;

// Stop all services
await servicesManager.StopAllAsync(TimeSpan.FromSeconds(30));
```

## Benefits

1. **Centralized Lifecycle Management** - All services managed through single point
2. **Consistent Threading Model** - Standardized async start/stop with cancellation
3. **Improved Shutdown Reliability** - Coordinated shutdown prevents hanging processes
4. **Better Error Handling** - Individual service failures don't crash application
5. **Easier Testing** - Isolated service testing through common interface
6. **Future Extensibility** - Easy to add new services following the same pattern