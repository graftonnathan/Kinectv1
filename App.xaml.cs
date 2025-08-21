using System;
using System.Threading.Tasks;
using System.Windows;

namespace Kinectv1
{
    public partial class App : Application
    {
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

                // IMPORTANT: Start STT (Vosk) BEFORE creating MainWindow (which initializes TTS)
                try
                {
                    var voiceModelPath = AppSettings.LoadSttModelPath();
                    if (!string.IsNullOrWhiteSpace(voiceModelPath) && System.IO.Directory.Exists(voiceModelPath))
                    {
                        VoiceRecognizer.Start(voiceModelPath, "john");
                    }
                }
                catch (Exception sttEx)
                {
                    Console.WriteLine($"❌ Early STT init failed: {sttEx.Message}");
                }

                // Create and show main window
                var win = new MainWindow();
                win.Show();
                win.Activate();
                
                // Start remaining background services
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
            // Start services in background
            Task.Run(() =>
            {
                try
                {
                    // STT: Only start if not already started during early init
                    var voiceModelPath = AppSettings.LoadSttModelPath();
                    if (!VoiceRecognizer.IsReady() && !string.IsNullOrWhiteSpace(voiceModelPath) && System.IO.Directory.Exists(voiceModelPath))
                    {
                        VoiceRecognizer.Start(voiceModelPath, "john");
                    }

                    // DEFER TTS initialization to first use (Kokoro adapter loads on demand)
                    Console.WriteLine("🔊 TTS will initialize on first use (deferred)");

                    // Start Enhanced Kinect Face Tracker (shows ALL faces with tracking IDs)
                    EnhancedKinectFaceTracker.Start();
                    
                    // Initialize Ollama service based on settings
                    var ollamaEnabled = AppSettings.LoadOllamaEnabled();
                    OllamaService.SetEnabled(ollamaEnabled);
                    Console.WriteLine($"Ollama service initialized (enabled: {ollamaEnabled})");
                    
                    Console.WriteLine("All services initialized successfully with enhanced face tracking and Ollama integration");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Service initialization error: {ex.Message}");
                }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Console.WriteLine("🔻 Application exiting - stopping services...");

                // Stop voice recognizer first to prevent further use of Vosk native resources
                try { VoiceRecognizer.Stop(); } catch (Exception ex) { Console.WriteLine($"VoiceRecognizer.Stop error: {ex.Message}"); }

                // Dispose all shared Vosk models after recognizers are stopped
                try { VoskModelManager.DisposeAllModels(); } catch (Exception ex) { Console.WriteLine($"DisposeAllModels error: {ex.Message}"); }

                // Stop Kinect trackers
                try { EnhancedKinectFaceTracker.Stop(); } catch { try { KinectFaceTracker.Stop(); } catch (Exception ex) { Console.WriteLine($"Kinect stop error: {ex.Message}"); } }

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
