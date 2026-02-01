// HeadlessMaggie.cs - Console-based Maggie for Linux with Qwen3-TTS Voice
// LLM brain + Jeff API + Web Interface + Neural TTS + Vosk STT

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1;
using Kinectv1.Settings;
using Kinectv1.Llm;
using Kinectv1.Services.Transcription;

namespace Kinectv1.Headless
{
    class Program
    {
        private static bool _sttEnabled = false;
        private static bool _ttsEnabled = false;
        private static bool _transcriptionMode = false;
        private static DateTime _lastSpeechTime = DateTime.MinValue;
        private static readonly object _sttLock = new object();

        static async Task Main(string[] args)
        {
            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  🦞 Maggie AI - Voice-Enabled Headless Mode           ║");
            Console.WriteLine("║  LLM Brain + Qwen3-TTS + Web Interface + Vosk STT     ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            try
            {
                // Initialize settings
                Console.WriteLine("📋 Loading configuration...");
                
                Console.WriteLine($"   ℹ️  Settings path: {SettingsPaths.UserJsonPath}");
                Console.WriteLine($"   ℹ️  AppDataDir: {SettingsPaths.AppDataDir}");
                var settingsProvider = new SettingsService();
                var cfg = settingsProvider.Current;
                
                // Store in static App class
                App.SettingsProvider = settingsProvider;
                
                Console.WriteLine($"   ✓ Settings loaded");
                Console.WriteLine($"   ✓ LLM Provider: {cfg.Ollama.Provider}");
                Console.WriteLine($"   ✓ LMStudio URL: {cfg.Ollama.LmStudioBaseUrl}");
                Console.WriteLine($"   ✓ Memory enabled: {cfg.Ollama.MemoryEnabled}");
                Console.WriteLine();

                // Initialize Qwen3-TTS
                Console.WriteLine("🎙️  Initializing Qwen3-TTS voice...");
                var ttsUrl = Environment.GetEnvironmentVariable("MAGGIE_TTS_URL") ?? "http://localhost:7860";
                Tts.Qwen3TtsService.Initialize(ttsUrl);
                
                // Check TTS availability
                _ttsEnabled = await Tts.Qwen3TtsService.IsAvailableAsync();
                if (_ttsEnabled)
                {
                    var voices = await Tts.Qwen3TtsService.GetAvailableVoicesAsync();
                    Console.WriteLine($"   ✓ Qwen3-TTS connected: {ttsUrl}");
                    Console.WriteLine($"   ✓ Available voices: {string.Join(", ", voices)}");
                    
                    // Set default voice to Serena (warm, gentle female)
                    await Tts.Qwen3TtsService.SetVoiceAsync("Serena");
                    Console.WriteLine($"   ✓ Default voice: Serena (warm, gentle female)");
                }
                else
                {
                    Console.WriteLine($"   ⚠️  Qwen3-TTS not available at {ttsUrl}");
                    Console.WriteLine($"   ℹ️  Start TTS service: python3 tts_service/qwen_tts_service.py");
                }
                Console.WriteLine();

                // Initialize Vosk STT (Voice Recognition)
                Console.WriteLine("🎤 Initializing Vosk STT...");
                var sttModelPath = cfg.Stt?.ModelPath ?? "models/vosk-model-en-us-0.22";
                try
                {
                    // Hook up transcription event before starting
                    VoiceRecognizer.OnTranscription += OnSpeechTranscribed;
                    VoiceRecognizer.OnPartialTranscription += OnPartialTranscription;
                    VoiceRecognizer.OnRmsLevel += OnRmsLevel;
                    
                    // Start VoiceRecognizer with microphone input enabled
                    VoiceRecognizer.Start(sttModelPath);
                    VoiceRecognizer.SetMicrophoneInputEnabled(true);
                    
                    _sttEnabled = VoiceRecognizer.IsReady();
                    if (_sttEnabled)
                    {
                        Console.WriteLine($"   ✓ Vosk STT initialized");
                        Console.WriteLine($"   ✓ Model path: {sttModelPath}");
                        Console.WriteLine($"   ✓ Microphone input enabled");
                        Console.WriteLine($"   ✓ VAD Threshold: {cfg.Audio?.VoiceThreshold:F3}");
                    }
                    else
                    {
                        Console.WriteLine($"   ⚠️  Vosk STT not ready - check model path: {sttModelPath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ❌ Vosk STT initialization failed: {ex.Message}");
                    Console.WriteLine($"   ℹ️  Make sure model exists at: {sttModelPath}");
                }
                Console.WriteLine();

                // Start Jeff API for communication (with LAN access)
                Console.WriteLine("🌐 Starting Jeff API server...");
                var ip = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "localhost";
                
                Api.JeffApiServer.Start(lanAccess: true);
                Console.WriteLine($"   ✓ API listening on http://localhost:18790");
                Console.WriteLine($"   ✓ Web interface: http://{ip}:18790");
                Console.WriteLine();

                // Hook up Jeff API to Maggie's brain with TTS
                Api.JeffApiServer.OnChatRequest += async (message) =>
                {
                    Console.WriteLine($"\n📨 Jeff: {message}");
                    return await ProcessChatMessage("Jeff", message);
                };

                // Hook up sentence streaming for real-time TTS
                OllamaService.OnResponseSentenceReady += (sentence) =>
                {
                    if (_ttsEnabled && !string.IsNullOrWhiteSpace(sentence))
                    {
                        // Queue for streaming TTS
                        Tts.Qwen3TtsService.QueueSentenceForStreaming(sentence);
                    }
                };

                // Print status
                Console.WriteLine("🎯 Maggie is ready!");
                Console.WriteLine($"   🎤 STT: {(_sttEnabled ? "✓ Enabled" : "✗ Disabled")}");
                Console.WriteLine($"   🎙️  TTS: {(_ttsEnabled ? "✓ Enabled" : "✗ Disabled")}");
                Console.WriteLine("   💬 Chat via: curl -X POST http://localhost:18790/api/chat");
                Console.WriteLine("   🌐 Voice UI: http://localhost:18790/voice/  (Qwen3-TTS chat)");
                Console.WriteLine("   🌐 WebRTC:   http://localhost:18790/webrtc/ (original voice chat)");
                Console.WriteLine("   📱 LAN:      http://{0}:18790/", ip);
                Console.WriteLine();
                Console.WriteLine("Commands:");
                Console.WriteLine("   'transcription' - Toggle meeting transcription mode");
                Console.WriteLine("   'status'        - Show STT/TTS status");
                Console.WriteLine("   'quit'          - Exit");
                Console.WriteLine();
                Console.WriteLine("Press Ctrl+C to exit");
                Console.WriteLine();

                // Start console input handler for commands
                _ = Task.Run(() => HandleConsoleInputAsync());

                // Keep running
                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) => 
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                try
                {
                    await Task.Delay(-1, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown
                }

                Console.WriteLine("\n👋 Shutting down Maggie...");
                
                // Stop transcription if active
                if (_transcriptionMode)
                {
                    await Services.Transcription.TranscriptionService.Instance.StopSessionAsync();
                }
                
                // Disable microphone
                if (_sttEnabled)
                {
                    VoiceRecognizer.SetMicrophoneInputEnabled(false);
                }
                
                Api.JeffApiServer.Stop();
                Console.WriteLine("   ✓ Jeff API stopped");
                Console.WriteLine("   ✓ Goodbye!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Fatal error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// Handle speech transcription from Vosk STT
        /// </summary>
        private static void OnSpeechTranscribed(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            
            lock (_sttLock)
            {
                _lastSpeechTime = DateTime.Now;
                
                // If in transcription mode, log to file
                if (_transcriptionMode)
                {
                    Services.Transcription.TranscriptionService.Instance.AddTranscription("Speaker", text);
                    Console.WriteLine($"📝 [Transcription] {text}");
                    return;
                }
                
                // Normal chat mode - process through LLM
                Console.WriteLine($"🎤 You: {text}");
                _ = Task.Run(async () => await ProcessChatMessage("User", text));
            }
        }

        /// <summary>
        /// Handle partial transcription for real-time feedback
        /// </summary>
        private static void OnPartialTranscription(string partialText)
        {
            // Only show partials in console if they're substantial
            if (!string.IsNullOrWhiteSpace(partialText) && partialText.Length > 10)
            {
                // Console.Write($"\r🎤 ... {partialText}");
            }
        }

        /// <summary>
        /// Handle RMS level for activity indication
        /// </summary>
        private static void OnRmsLevel(float rms)
        {
            // Could be used for visual feedback or silence detection
            // For now, just track internally
        }

        /// <summary>
        /// Process a chat message through the LLM
        /// </summary>
        private static async Task<string> ProcessChatMessage(string speaker, string message)
        {
            try
            {
                var tcs = new TaskCompletionSource<string>();
                var handler = new Action<string>(response => 
                {
                    tcs.TrySetResult(response);
                });
                
                OllamaService.OnResponseReceived += handler;
                await OllamaService.DispatchAsync(speaker, message);
                
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(30000));
                OllamaService.OnResponseReceived -= handler;
                
                if (completed == tcs.Task)
                {
                    var response = await tcs.Task;
                    Console.WriteLine($"💬 Maggie: {response}");
                    
                    // Generate voice if TTS is available and not in transcription mode
                    if (_ttsEnabled && !string.IsNullOrWhiteSpace(response) && !_transcriptionMode)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Tts.Qwen3TtsService.SpeakAsync(response);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"   ⚠️  TTS error: {ex.Message}");
                            }
                        });
                    }
                    
                    return response;
                }
                return "(no response - timeout)";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error processing message: {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Handle console input for commands
        /// </summary>
        private static async Task HandleConsoleInputAsync()
        {
            while (true)
            {
                try
                {
                    var input = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(input)) continue;

                    switch (input.Trim().ToLowerInvariant())
                    {
                        case "transcription":
                        case "transcribe":
                        case "t":
                            await ToggleTranscriptionModeAsync();
                            break;

                        case "status":
                        case "s":
                            PrintStatus();
                            break;

                        case "quit":
                        case "exit":
                        case "q":
                            // Trigger shutdown via Ctrl+C simulation
                            Console.WriteLine("Shutting down...");
                            Environment.Exit(0);
                            break;

                        case "help":
                        case "h":
                        case "?":
                            PrintHelp();
                            break;

                        default:
                            // Treat as chat message
                            Console.WriteLine($"📨 Console: {input}");
                            await ProcessChatMessage("Console", input);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Console input error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Toggle transcription mode for meetings
        /// </summary>
        private static async Task ToggleTranscriptionModeAsync()
        {
            if (!_sttEnabled)
            {
                Console.WriteLine("❌ Cannot start transcription mode - STT is not enabled");
                return;
            }

            if (_transcriptionMode)
            {
                // Stop transcription
                await Services.Transcription.TranscriptionService.Instance.StopSessionAsync();
                _transcriptionMode = false;
                Console.WriteLine("📝 Transcription mode STOPPED");
                Console.WriteLine("   Transcript saved to transcriptions/ folder");
            }
            else
            {
                // Start transcription
                Services.Transcription.TranscriptionService.Instance.StartSession();
                _transcriptionMode = true;
                Console.WriteLine("📝 Transcription mode STARTED");
                Console.WriteLine("   Speaking will be logged to file (no LLM responses)");
                Console.WriteLine("   Type 'transcription' again to stop and save summary");
            }
        }

        /// <summary>
        /// Print current status
        /// </summary>
        private static void PrintStatus()
        {
            Console.WriteLine("\n📊 Maggie Status:");
            Console.WriteLine($"   🎤 STT Ready: {VoiceRecognizer.IsReady()}");
            Console.WriteLine($"   🎤 Mic Enabled: {VoiceRecognizer.IsMicrophoneInputEnabled()}");
            Console.WriteLine($"   🎙️  TTS Enabled: {_ttsEnabled}");
            Console.WriteLine($"   📝 Transcription Mode: {(_transcriptionMode ? "ACTIVE" : "inactive")}");
            Console.WriteLine($"   🧠 LLM Provider: {App.SettingsProvider?.Current?.Ollama?.Provider}");
            Console.WriteLine();
        }

        /// <summary>
        /// Print help text
        /// </summary>
        private static void PrintHelp()
        {
            Console.WriteLine("\n📖 Available Commands:");
            Console.WriteLine("   transcription, t  - Toggle meeting transcription mode");
            Console.WriteLine("   status, s         - Show current status");
            Console.WriteLine("   quit, q           - Exit Maggie");
            Console.WriteLine("   help, h, ?        - Show this help");
            Console.WriteLine("   <any text>        - Send message to Maggie");
            Console.WriteLine();
        }
    }
}
