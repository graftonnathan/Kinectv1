using System;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
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

        // Minimal JSON settings provider (defaults + user overlay)
        public static SettingsService SettingsProvider { get; private set; }

        public App()
        {
            // Wire up global exception handlers early
            SetupGlobalExceptionHandlers();
            
            // Don't initialize here - move to OnStartup to ensure proper console allocation
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static void EnsureNativeDllSearchPaths()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var teamtalkDir = System.IO.Path.Combine(baseDir, "lib", "teamtalk");
                if (System.IO.Directory.Exists(teamtalkDir))
                {
                    SetDllDirectory(teamtalkDir);
                }
            }
            catch { }
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

            // Ensure native DLL search paths are configured before any P/Invoke loads
            EnsureNativeDllSearchPaths();

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

                // Create and show main window
                var win = new MainWindow();
                win.Show();
                win.Activate();
                
                // Start background services (direct start; hosted services manager removed)
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
                Console.WriteLine($"Self-check failed: {ex.Message}");
            }
        }

        private void StartServices()
        {
            // Placeholder for direct-start services if needed
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
