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
                    var sttDir = SettingsProvider?.Current?.Stt?.ModelPath;
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
                    var ttsModel = SettingsProvider?.Current?.Tts?.ModelPath;
                    if (!string.IsNullOrWhiteSpace(ttsModel))
                    {
                        var ttsFull = Path.IsPathRooted(ttsModel) ? ttsModel : Path.Combine(baseDir, ttsModel);
                        if (!File.Exists(ttsFull))
                        {
                            Console.WriteLine($"⚠️ TTS model file not found: {ttsFull} (Kokoro)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ TTS self-check failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SelfCheckCriticalResources failed: {ex.Message}");
            }
        }

        private void StartServices()
        {
            try
            {
                ServicesManager = new HostedServicesManager();

                // Register Vosk/ASR using settings-driven model path (per docs/settings.md)
                try
                {
                    var sttPath = SettingsProvider?.Current?.Stt?.ModelPath;
                    if (!string.IsNullOrWhiteSpace(sttPath))
                    {
                        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        var fullSttPath = Path.IsPathRooted(sttPath) ? sttPath : Path.Combine(baseDir, sttPath);
                        ServicesManager.RegisterService(new VoiceRecognizerHostedService(fullSttPath, string.Empty));
                    }
                    else
                    {
                        Console.WriteLine("⚠️ STT model path not configured in settings; skipping VoiceRecognizer service registration");
                    }
                }
                catch (Exception regEx)
                {
                    Console.WriteLine($"⚠️ Failed to register VoiceRecognizer service: {regEx.Message}");
                }

                // Start all registered services
                ServicesManager.StartAllAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Hosted services failed to start: {ex.Message}");
            }
        }

        private void SetupGlobalExceptionHandlers()
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    try { Console.WriteLine("Unhandled exception: " + e.ExceptionObject); } catch { }
                };
                DispatcherUnhandledException += (s, e) =>
                {
                    try { Console.WriteLine("Dispatcher exception: " + e.Exception?.Message); } catch { }
                    e.Handled = true;
                };
                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    try { Console.WriteLine("Unobserved task exception: " + e.Exception?.Message); } catch { }
                    e.SetObserved();
                };
            }
            catch { }
        }
    }
}
