// HeadlessMaggie.cs - Console-based Maggie for Linux with Qwen3-TTS Voice
// LLM brain + Jeff API + Web Interface + Neural TTS

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Settings;
using Kinectv1.Llm;

namespace Kinectv1.Headless
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  🦞 Maggie AI - Voice-Enabled Headless Mode           ║");
            Console.WriteLine("║  LLM Brain + Qwen3-TTS + Web Interface                ║");
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
                var ttsAvailable = await Tts.Qwen3TtsService.IsAvailableAsync();
                if (ttsAvailable)
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
                    
                    var tcs = new TaskCompletionSource<string>();
                    var handler = new Action<string>(response => 
                    {
                        tcs.TrySetResult(response);
                    });
                    
                    OllamaService.OnResponseReceived += handler;
                    await OllamaService.DispatchAsync("Jeff", message);
                    
                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(30000));
                    OllamaService.OnResponseReceived -= handler;
                    
                    if (completed == tcs.Task)
                    {
                        var response = await tcs.Task;
                        Console.WriteLine($"💬 Maggie: {response}");
                        
                        // Generate voice if TTS is available
                        if (ttsAvailable && !string.IsNullOrWhiteSpace(response))
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
                    return "(no response)";
                };

                // Hook up sentence streaming for real-time TTS
                OllamaService.OnResponseSentenceReady += (sentence) =>
                {
                    if (ttsAvailable && !string.IsNullOrWhiteSpace(sentence))
                    {
                        // Queue for streaming TTS
                        Tts.Qwen3TtsService.QueueSentenceForStreaming(sentence);
                    }
                };

                Console.WriteLine("🎯 Maggie is ready!");
                Console.WriteLine("   💬 Chat via: curl -X POST http://localhost:18790/api/chat");
                Console.WriteLine("   🌐 Voice UI: http://localhost:18790/voice/  (Qwen3-TTS chat)");
                Console.WriteLine("   🌐 WebRTC:   http://localhost:18790/webrtc/ (original voice chat)");
                Console.WriteLine("   📱 LAN:      http://{0}:18790/", ip);
                Console.WriteLine();
                Console.WriteLine("Press Ctrl+C to exit");
                Console.WriteLine();

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
    }
}
