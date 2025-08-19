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

            base.OnStartup(e);

            try
            {
                // Initialize application-wide settings early
                Console.WriteLine("⚙️ Initializing app settings...");
                AppSettings.InitializeSettingsOnStartup();
                Console.WriteLine("✅ App settings initialized successfully");
                
                // Test conversation manager functionality
                Console.WriteLine("🧪 Testing conversation manager on startup...");
                OllamaService.TestConversationSaving();
                Console.WriteLine("✅ Conversation manager test completed");
                
                // NOTE: Removed hanging DiagnoseConversationHistory() call from startup
                // Use DiagnoseConversationHistoryAsync() manually when needed
                Console.WriteLine("🔍 Conversation diagnostic available via DiagnoseConversationHistoryAsync()");
                
                // Load settings from Settings.settings on startup
                AppSettings.InitializeSettingsOnStartup();
                
                // Initialize core components
                MemoryStore.Init();
                
                // Create and show main window
                var win = new MainWindow();
                win.Show();
                win.Activate();
                
                // Start background services
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
                    // Load speaker model
                    var embModel = AppSettings.LoadSpeakerEmbeddingModelPath();
                    if (!string.IsNullOrWhiteSpace(embModel) && System.IO.File.Exists(embModel))
                    {
                        SpeakerEmbedder.Load(embModel);
                    }

                    // Start voice recognition
                    var voiceModelPath = AppSettings.LoadSttModelPath();
                    if (!string.IsNullOrWhiteSpace(voiceModelPath) && System.IO.Directory.Exists(voiceModelPath))
                    {
                        VoiceRecognizer.Start(voiceModelPath, "john");
                    }

                    // Initialize TTS system for testing
                    try
                    {
                        var ttsModelPath = AppSettings.LoadTtsModelPath();
                        var cmudictPath = AppSettings.LoadTtsCmudictPath();
                        var symbolsPath = AppSettings.LoadTtsSymbolsPath();
                        var vocoderPath = AppSettings.LoadTtsVocoderModelPath(); // Not used yet by CoquiTtsService but stored for future
                        
                        if (System.IO.File.Exists(ttsModelPath) && System.IO.File.Exists(cmudictPath) && System.IO.File.Exists(symbolsPath))
                        {
                            Console.WriteLine("🎤 Initializing TTS system...");
                            if (CoquiTtsService.Initialize(ttsModelPath, cmudictPath, symbolsPath))
                            {
                                Console.WriteLine("✅ TTS system initialized successfully");
                                
                                // Test tokenization on startup to verify it's working
                                CoquiTtsService.TestTokenization();
                            }
                            else
                            {
                                Console.WriteLine("❌ TTS system initialization failed");
                            }
                        }
                        else
                        {
                            Console.WriteLine("⚠️ TTS model files not found - TTS features disabled");
                            Console.WriteLine($"   Expected: {ttsModelPath}");
                            Console.WriteLine($"   Expected: {cmudictPath}");
                            Console.WriteLine($"   Expected: {symbolsPath}");
                        }
                    }
                    catch (Exception ttsEx)
                    {
                        Console.WriteLine($"❌ TTS initialization error: {ttsEx.Message}");
                    }

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
                // Save all settings before exiting
                AppSettings.SaveAllSettings();
                
                EnhancedKinectFaceTracker.Stop();
                VoiceRecognizer.Stop();
                OllamaService.Dispose();
                MemoryStore.Save();
                
                // Dispose all Vosk models to prevent memory leaks
                VoskModelManager.DisposeAllModels();
                
                ConsoleManager.HideConsole();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Shutdown error: {ex.Message}");
            }
            
            base.OnExit(e);
        }
    }
}
