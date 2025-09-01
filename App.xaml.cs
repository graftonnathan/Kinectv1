using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kinectv1.Discord;
using System.IO;
using Kinectv1.Settings;
using System.Text; // Ensure we can set Console encodings
using System.Runtime.InteropServices; // For SetConsoleCP

namespace Kinectv1
{
    public partial class App : Application
    {
        // P/Invoke to force UTF-8 in classic console hosts
        [DllImport("kernel32.dll")] private static extern bool SetConsoleOutputCP(uint wCodePageID);
        [DllImport("kernel32.dll")] private static extern bool SetConsoleCP(uint wCodePageID);

        private static void EnsureUtf8Console()
        {
            try
            {
                // Set Windows code pages first, then .NET encodings
                SetConsoleCP(65001);
                SetConsoleOutputCP(65001);
                Console.InputEncoding = Encoding.UTF8;
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
            catch { /* Non-fatal if console host does not support */ }
        }

        // Global hosted services manager instance
        public static HostedServicesManager ServicesManager { get; private set; }

        // Minimal JSON settings provider (defaults + user overlay)
        public static SettingsService SettingsProvider { get; private set; }

        public App()
        {
            // Wire up global exception handlers early
            SetupGlobalExceptionHandlers();
            
            // Don't initialize here - move to OnStartup to ensure proper console allocation
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize console for essential output first
            try
            {
                ConsoleManager.ShowConsole();
                EnsureUtf8Console(); // Force UTF-8 so emoji/icons render correctly
                Console.WriteLine("🚀 Kinect Face & Voice Recognition - Starting...");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Console initialization failed: {ex.Message}");
            }

            // Reduce native OpenMP duplicate runtime crashes when mixing libraries
            try { Environment.SetEnvironmentVariable("KMP_DUPLICATE_LIB_OK", "TRUE", EnvironmentVariableTarget.Process); } catch { }

            base.OnStartup(e);

            try
            {
                // Initialize new JSON settings (defaults embedded + user overlay)
                try
                {
                    SettingsProvider = new SettingsService();
                    var cfg = SettingsProvider.Current;
                    Console.WriteLine($"⚙️ JSON settings loaded: audio.voiceThreshold={cfg.Audio.VoiceThreshold:F2}, tts.enabled={cfg.Tts.Enabled}, tts.exec={cfg.Tts.Execution}");
                }
                catch (Exception sx)
                {
                    Console.WriteLine($"❌ JSON settings failed to load: {sx.Message}");
                    throw;
                }

                // Validate and clamp all legacy settings as early as possible
                Console.WriteLine("⚙️ Validating app settings...");
                AppSettings.ValidateAll();

                // Initialize application-wide legacy settings and print summaries
                Console.WriteLine("⚙️ Initializing app settings...");
                AppSettings.InitializeSettingsOnStartup();
                Console.WriteLine("✅ App settings initialized successfully");

                // SAFELY force CUDA device to 0 without flushing entire ProgramValueList
                try { AppSettings.SaveTtsGpuDeviceId(0); } catch { }

                // Ensure telemetry is enabled so program status is logged to the configured file
                if (!AppSettings.LoadTelemetryEnabled())
                {
                    AppSettings.SaveTelemetryEnabled(true);
                }

                // Perform lightweight startup self-checks for critical resources
                SelfCheckCriticalResources();

                // Initialize telemetry system
                Console.WriteLine("📊 Initializing telemetry...");
                Telemetry.RefreshSettings();
                Telemetry.Event("app.startup", new { version = "1.0", timestamp = DateTime.UtcNow });
                Console.WriteLine("✅ Telemetry initialized successfully");

                // Create and show main window
                var win = new MainWindow();
                win.Show();
                win.Activate();
                
                // Start background services through hosted services manager
                StartServices();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Startup error: {ex.Message}");
                MessageBox.Show($"Application failed to start:\n{ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void SelfCheckCriticalResources()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                // STT (Vosk) model directory presence
                try
                {
                    var sttDir = AppSettings.LoadSttModelPath();
                    if (!string.IsNullOrWhiteSpace(sttDir))
                    {
                        var sttFull = Path.IsPathRooted(sttDir) ? sttDir : Path.Combine(baseDir, sttDir);
                        if (!Directory.Exists(sttFull))
                        {
                            Console.WriteLine($"⚠️ STT model directory not found: {sttFull} (Vosk)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ STT self-check failed: {ex.Message}");
                }

                // TTS (Kokoro) ONNX model presence
                try
                {
                    var ttsModel = AppSettings.LoadTtsModelPath();
                    if (!string.IsNullOrWhiteSpace(ttsModel))
                    {
                        var ttsFull = Path.IsPathRooted(ttsModel) ? ttsModel : Path.Combine(baseDir, ttsModel);
                        if (!File.Exists(ttsFull))
                        {
                            Console.WriteLine($"⚠️ TTS model not found: {ttsFull} (Kokoro ONNX)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ TTS self-check failed: {ex.Message}");
                }

                // Face (ArcFace) ONNX model presence
                try
                {
                    var arc = AppSettings.LoadArcFaceModelPath();
                    if (!string.IsNullOrWhiteSpace(arc))
                    {
                        var arcFull = Path.IsPathRooted(arc) ? arc : Path.Combine(baseDir, arc);
                        if (!File.Exists(arcFull))
                        {
                            Console.WriteLine($"⚠️ Face model not found: {arcFull} (ArcFace ONNX)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Face model self-check failed: {ex.Message}");
                }

                // Audio device sanity: ensure configured output exists else fall back to Default
                try
                {
                    var configuredOutput = AppSettings.LoadTtsOutputDevice();
                    if (!string.IsNullOrWhiteSpace(configuredOutput) && !string.Equals(configuredOutput, "Default", StringComparison.OrdinalIgnoreCase))
                    {
                        var exists = false;
                        try
                        {
                            var outputs = AudioDeviceManager.GetOutputDevices();
                            exists = outputs?.Exists(d => string.Equals(d.DeviceName, configuredOutput, StringComparison.OrdinalIgnoreCase)) == true;
                        }
                        catch { /* device enumeration failure should not crash app */ }

                        if (!exists)
                        {
                            Console.WriteLine($"🔊 TTS output device '{configuredOutput}' not found. Falling back to Default.");
                            AppSettings.SaveTtsOutputDevice("Default");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Audio output self-check failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Startup self-check encountered an error: {ex.Message}");
            }
        }

        private void StartServices()
        {
            // Initialize the hosted services manager
            ServicesManager = new HostedServicesManager();
            
            // Register event handlers for service monitoring
            ServicesManager.OnServiceError += (serviceName, ex) => 
            {
                Console.WriteLine($"❌ Service error in {serviceName}: {ex.Message}");
            };
            
            ServicesManager.OnStatusChanged += (status) => 
            {
                Console.WriteLine($"🔧 Services status: {status}");
            };

            // Start services in background
            Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine("🔧 Registering hosted services...");

                    // Register all services with the hosted services manager
                    var voiceModelPath = AppSettings.LoadSttModelPath();
                    if (!string.IsNullOrWhiteSpace(voiceModelPath) && System.IO.Directory.Exists(voiceModelPath))
                    {
                        var voiceService = new VoiceRecognizerHostedService(voiceModelPath, "john");
                        ServicesManager.RegisterService(voiceService);
                    }

                    // Register TTS service (use JSON settings for enablement)
                    if (SettingsProvider?.Current.Tts.Enabled == true)
                    {
                        var ttsService = new CoquiTtsHostedService();
                        ServicesManager.RegisterService(ttsService);
                    }
                    else
                    {
                        Console.WriteLine("🔇 TTS disabled in JSON settings; TTS service not registered.");
                    }

                    // Register Discord services if enabled and token configured
                    if (AppSettings.LoadDiscordBotEnabled())
                    {
                        var discordToken = AppSettings.LoadDiscordBotToken();
                        if (!string.IsNullOrWhiteSpace(discordToken))
                        {
                            var discordBotService = new DiscordBotHostedService();
                            ServicesManager.RegisterService(discordBotService);

                            // Conditionally register Discord audio services based on AudioInMode
                            var audioMode = AppSettings.LoadAudioInMode();
                            Console.WriteLine($"🎧 Audio input mode: {audioMode}");
                            
                            if (audioMode == AudioInMode.SystemLoopback)
                            {
                                var discordAudioService = new DiscordSystemAudioCaptureHostedService();
                                ServicesManager.RegisterService(discordAudioService);
                                Console.WriteLine("🔊 Discord system audio capture service registered (SystemLoopback mode)");
                            }
                            else if (audioMode == AudioInMode.DiscordVoice)
                            {
                                Console.WriteLine("🎤 Discord voice receiver will be used (DiscordVoice mode)");
                                // Voice receiver is managed by DiscordNetBotManager, not as a separate service
                            }
                            else
                            {
                                Console.WriteLine("🎤 Local microphone input will be used (LocalMic mode)");
                            }
                        }
                        else
                        {
                            Console.WriteLine("⚠️ Discord bot is enabled but no token is configured; skipping Discord services registration.");
                        }
                    }

                    

                    Console.WriteLine("🚀 Starting all hosted services...");
                    
                    // Start all services with centralized management
                    await ServicesManager.StartAllAsync(TimeSpan.FromSeconds(60));

                    // Start Enhanced Kinect Face Tracker (not converted to hosted service yet)
                    EnhancedKinectFaceTracker.Start();
                    
                    Console.WriteLine("✅ All services initialized successfully with centralized management");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Service initialization error: {ex.Message}");
                }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Console.WriteLine("🔻 Application exiting - stopping services...");
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Stop any active TTS playback first to release audio devices and cancel loops
                try { TtsPlaybackController.CancelCurrent(); } catch { }

                // Stop hosted services manager first (centralized shutdown)
                if (ServicesManager != null)
                {
                    try 
                    { 
                        // Await StopAllAsync with a 3s timeout
                        var stopTask = ServicesManager.StopAllAsync(TimeSpan.FromSeconds(3));
                        stopTask.Wait(3500);
                        Console.WriteLine("✅ Hosted services stop requested");
                    } 
                    catch (Exception ex) 
                    { 
                        Console.WriteLine($"⚠️ Hosted services stop error: {ex.Message}"); 
                    }
                }

                // Leave all Discord voice channels and close gateway with watchdogs
                try 
                { 
                    // Prefer async cleanup with watchdogs
                    var leaveTask = DiscordNetBotManager.LeaveAllVoiceAsync();
                    leaveTask.Wait(2500);

                    var closeTask = DiscordNetBotManager.CloseGatewayAsync();
                    closeTask.Wait(2500);
                } 
                catch (Exception ex) 
                { 
                    Console.WriteLine($"⚠️ Discord final shutdown error: {ex.Message}");
                }

                // Stop remaining services not yet converted to hosted services
                try { EnhancedKinectFaceTracker.Stop(); } catch { try { KinectFaceTracker.Stop(); } catch (Exception ex) { Console.WriteLine($"Kinect stop error: {ex.Message}"); } }

                // Dispose all shared Vosk models after recognizers are stopped
                try { VoskModelManager.DisposeAllModels(); } catch (Exception ex) { Console.WriteLine($"DisposeAllModels error: {ex.Message}"); }
                
                stopwatch.Stop();
                Telemetry.Timer("app.exit", stopwatch.ElapsedMilliseconds);
                Console.WriteLine($"📊 App.OnExit completed in {stopwatch.ElapsedMilliseconds}ms");

                // Hide console AFTER all Console.WriteLine calls to avoid invalid handle errors
                try { ConsoleManager.HideConsole(); } catch { }
            }
            finally
            {
                base.OnExit(e);
                // Hard fail-safe to guarantee process termination if any foreground threads remain
                try { Environment.Exit(0); } catch { }
            }
        }

        /// <summary>
        /// Set up global exception handlers for unhandled exceptions
        /// </summary>
        private void SetupGlobalExceptionHandlers()
        {
            // Handle unhandled exceptions from all threads
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            
            // Handle unhandled exceptions from the WPF UI thread
            this.DispatcherUnhandledException += OnDispatcherUnhandledException;
            
            // Handle unhandled exceptions from tasks
            TaskScheduler.UnobservedTaskException += OnTaskUnobservedException;
        }

        /// <summary>
        /// Handle unhandled exceptions from AppDomain (non-UI threads)
        /// </summary>
        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            var context = GatherApplicationContext();
            
            var crashData = new
            {
                source = "AppDomain",
                isTerminating = e.IsTerminating,
                exception = new
                {
                    type = exception?.GetType().FullName ?? "Unknown",
                    message = exception?.Message ?? "Unknown exception",
                    stackTrace = exception?.StackTrace
                },
                context = context
            };

            // Log to telemetry system
            Telemetry.Event("app.crash", crashData, TelemetryLevel.Error);
            
            // Also log to Debug output
            System.Diagnostics.Debug.WriteLine($"FATAL CRASH: {exception?.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {exception?.StackTrace}");
            
            // Console output for immediate visibility
            Console.WriteLine($"❌ FATAL CRASH: {exception?.Message}");
            Console.WriteLine($"Context: {context}");
        }

        /// <summary>
        /// Handle unhandled exceptions from WPF Dispatcher (UI thread)
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var exception = e.Exception;
            var context = GatherApplicationContext();
            
            var crashData = new
            {
                source = "WPF Dispatcher",
                exception = new
                {
                    type = exception.GetType().FullName,
                    message = exception.Message,
                    stackTrace = exception.StackTrace
                },
                context = context
            };

            // Log to telemetry system
            Telemetry.Event("app.crash", crashData, TelemetryLevel.Error);
            
            // Also log to Debug output  
            System.Diagnostics.Debug.WriteLine($"WPF UI CRASH: {exception.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {exception.StackTrace}");
            
            // Console output for immediate visibility
            Console.WriteLine($"❌ WPF UI CRASH: {exception.Message}");
            Console.WriteLine($"Context: {context}");
            
            // Mark as handled to prevent immediate crash
            e.Handled = true;
        }

        /// <summary>
        /// Handle unhandled exceptions from Tasks
        /// </summary>
        private void OnTaskUnobservedException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            var exception = e.Exception?.GetBaseException() ?? e.Exception;
            var context = GatherApplicationContext();
            
            var crashData = new
            {
                source = "Task Scheduler",
                exception = new
                {
                    type = exception?.GetType().FullName ?? "Unknown",
                    message = exception?.Message ?? "Unknown task exception",
                    stackTrace = exception?.StackTrace
                },
                context = context
            };

            // Log to telemetry system
            Telemetry.Event("app.crash", crashData, TelemetryLevel.Error);
            
            // Also log to Debug output
            System.Diagnostics.Debug.WriteLine($"TASK CRASH: {exception?.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {exception?.StackTrace}");
            
            // Console output for immediate visibility
            Console.WriteLine($"❌ TASK CRASH: {exception?.Message}");
            Console.WriteLine($"Context: {context}");
            
            // Mark as observed to prevent app termination
            e.SetObserved();
        }

        /// <summary>
        /// Gather rich application context for crash reports
        /// </summary>
        private object GatherApplicationContext()
        {
            try
            {
                // Get audio device information
                string currentInputDevice = "Unknown";
                string currentOutputDevice = "Unknown";
                try
                {
                    var inputDevice = AudioDeviceManager.GetConfiguredInputDevice();
                    currentInputDevice = inputDevice?.DeviceName ?? "Default";
                    
                    var outputDevice = AudioDeviceManager.GetConfiguredOutputDevice();
                    currentOutputDevice = outputDevice?.DeviceName ?? "Default";
                }
                catch (Exception ex)
                {
                    currentInputDevice = $"Error: {ex.Message}";
                    currentOutputDevice = $"Error: {ex.Message}";
                }

                // Get model paths
                string sttModelPath = "Unknown";
                string ttsModelPath = "Unknown";
                bool ttsUseGpu = false;
                try
                {
                    sttModelPath = AppSettings.LoadSttModelPath() ?? "Not configured";
                    ttsModelPath = AppSettings.LoadTtsModelPath() ?? "Not configured";
                    ttsUseGpu = AppSettings.LoadTtsUseGpu();
                }
                catch (Exception ex)
                {
                    sttModelPath = $"Error: {ex.Message}";
                    ttsModelPath = $"Error: {ex.Message}";
                }

                return new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    audioDevices = new
                    {
                        inputDevice = currentInputDevice,
                        outputDevice = currentOutputDevice
                    },
                    models = new
                    {
                        sttModelPath = sttModelPath,
                        ttsModelPath = ttsModelPath,
                        ttsExecutionMode = ttsUseGpu ? "GPU" : "CPU"
                    },
                    services = new
                    {
                        hostedServicesRunning = ServicesManager?.IsStarted ?? false
                    }
                };
            }
            catch (Exception contextEx)
            {
                return new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    contextError = $"Failed to gather context: {contextEx.Message}"
                };
            }
        }
    }
}
