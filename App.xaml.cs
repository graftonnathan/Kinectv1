using System;
using System.Threading.Tasks;
using System.Windows;

namespace Kinectv1
{
    public partial class App : Application
    {
        // Global hosted services manager instance
        public static HostedServicesManager ServicesManager { get; private set; }

        public App()
        {
            // Don't initialize here - move to OnStartup to ensure proper console allocation
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize console for essential output first
            try
            {
                ConsoleManager.ShowConsole();
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
                // Initialize application-wide settings early
                Console.WriteLine("⚙️ Initializing app settings...");
                AppSettings.InitializeSettingsOnStartup();
                Console.WriteLine("✅ App settings initialized successfully");

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

                    // Register TTS service
                    var ttsService = new CoquiTtsHostedService();
                    ServicesManager.RegisterService(ttsService);

                    // Register Discord services if enabled
                    if (AppSettings.LoadDiscordBotEnabled())
                    {
                        var discordBotService = new DiscordBotHostedService();
                        ServicesManager.RegisterService(discordBotService);

                        var discordAudioService = new DiscordSystemAudioCaptureHostedService();
                        ServicesManager.RegisterService(discordAudioService);
                    }

                    // Register Ollama service
                    var ollamaService = new OllamaHostedService();
                    ServicesManager.RegisterService(ollamaService);

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

                // Stop hosted services manager first (centralized shutdown)
                if (ServicesManager != null)
                {
                    try 
                    { 
                        var stopTask = ServicesManager.StopAllAsync(TimeSpan.FromSeconds(30));
                        stopTask.Wait(35000); // Wait with timeout
                        Console.WriteLine("✅ Hosted services stopped");
                    } 
                    catch (Exception ex) 
                    { 
                        Console.WriteLine($"⚠️ Hosted services stop error: {ex.Message}"); 
                    }
                }

                // Stop remaining services not yet converted to hosted services
                try { EnhancedKinectFaceTracker.Stop(); } catch { try { KinectFaceTracker.Stop(); } catch (Exception ex) { Console.WriteLine($"Kinect stop error: {ex.Message}"); } }

                // Dispose all shared Vosk models after recognizers are stopped
                try { VoskModelManager.DisposeAllModels(); } catch (Exception ex) { Console.WriteLine($"DisposeAllModels error: {ex.Message}"); }

                // Hide console
                try { ConsoleManager.HideConsole(); } catch { }
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }
}
