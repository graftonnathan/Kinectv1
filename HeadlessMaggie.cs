// HeadlessMaggie.cs - Console-based Maggie for Linux with Qwen3-TTS Voice
// LLM brain + Jeff API + Web Interface + Neural TTS

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Settings;
using Kinectv1.Llm;
using Kinectv1.Voice;
using Kinectv1.Headless;

namespace Kinectv1.Headless
{
    class Program
    {
        private static WebRtcSignalingServer _webRtcServer;
        private static bool _webRtcEnabled = false;
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

                // Initialize Voice Recognition (STT)
                Console.WriteLine("🎤 Initializing voice recognition (Vosk STT)...");
                var modelPath = Environment.GetEnvironmentVariable("MAGGIE_VOSK_MODEL") 
                    ?? cfg.Stt?.ModelPath 
                    ?? "vosk-model-small-en-us-0.15";
                HeadlessVoiceRecognizer.Instance.Start(modelPath);
                
                if (HeadlessVoiceRecognizer.Instance.IsReady)
                {
                    // Hook up speech transcription to Maggie's brain
                    HeadlessVoiceRecognizer.Instance.OnTranscription += (text) =>
                    {
                        Console.WriteLine($"\n🎤 Heard: \"{text}\"");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ProcessChatMessage("You", text, ttsAvailable);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"   ⚠️  Speech processing error: {ex.Message}");
                            }
                        });
                    };
                    
                    HeadlessVoiceRecognizer.Instance.SetMicrophoneEnabled(true);
                    Console.WriteLine($"   ✓ Voice recognition ready (model: {modelPath})");
                    Console.WriteLine($"   ✓ Microphone: enabled");
                }
                else
                {
                    Console.WriteLine($"   ⚠️  Voice recognition not available");
                    Console.WriteLine($"   ℹ️  Download model: wget https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip");
                    Console.WriteLine($"   ℹ️  Set path: export MAGGIE_VOSK_MODEL=/path/to/model");
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

                // Start WebRTC Signaling Server
                Console.WriteLine("📡 Starting WebRTC signaling server...");
                var webRtcPort = cfg.WebRtc?.Port ?? 8787;
                try
                {
                    _webRtcServer = new WebRtcSignalingServer(webRtcPort);
                    _webRtcServer.OnLog += (msg) => Console.WriteLine($"   [WebRTC] {msg}");
                    
                    // Hook up WebRTC text input to Maggie's brain
                    _webRtcServer.OnWebTextInput += (speaker, text) =>
                    {
                        Console.WriteLine($"\n🌐 WebRTC [{speaker}]: {text}");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ProcessChatMessage(speaker, text, ttsAvailable);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"   ⚠️  WebRTC message error: {ex.Message}");
                            }
                        });
                    };

                    // Hook up WebRTC audio input to STT pipeline
                    _webRtcServer.OnWebAudioReceived += (frame) =>
                    {
                        if (HeadlessVoiceRecognizer.Instance.IsReady)
                        {
                            // Convert short[] PCM to byte[] for Vosk
                            var pcmBytes = new byte[frame.Pcm16.Length * 2];
                            System.Buffer.BlockCopy(frame.Pcm16, 0, pcmBytes, 0, pcmBytes.Length);
                            HeadlessVoiceRecognizer.Instance.ProcessAudio(pcmBytes, pcmBytes.Length, Kinectv1.Voice.AudioSourceType.WebRtc, frame.SourceId);
                        }
                    };

                    await _webRtcServer.StartAsync(System.Threading.CancellationToken.None);
                    _webRtcEnabled = true;
                    Console.WriteLine($"   ✓ WebRTC signaling on http://localhost:{webRtcPort}");
                    Console.WriteLine($"   ✓ WebRTC LAN access: http://{ip}:{webRtcPort}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️  WebRTC server failed to start: {ex.Message}");
                    Console.WriteLine($"   ℹ️  This is non-critical; other features will work");
                }
                Console.WriteLine();

                // Hook up Jeff API to Maggie's brain with TTS
                Api.JeffApiServer.OnChatRequest += async (message) =>
                {
                    Console.WriteLine($"\n📨 Jeff: {message}");
                    return await ProcessChatMessage("Jeff", message, ttsAvailable);
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

                // Hook up TTS audio to WebRTC clients
                if (_webRtcEnabled)
                {
                    Tts.Qwen3TtsService.OnTtsAudioChunk += (pcmData, sampleRate) =>
                    {
                        _webRtcServer?.BroadcastTtsAudio(pcmData, sampleRate);
                    };
                    Tts.Qwen3TtsService.OnTtsSpeakingFinished += () =>
                    {
                        _webRtcServer?.BroadcastTtsStop();
                    };
                }

                // Start Discord Bot (if enabled)
                Console.WriteLine();
                Console.WriteLine("💬 Starting Discord bot...");
                bool discordEnabled = false;
                try
                {
                    var discordSettings = cfg.Discord;
                    if (discordSettings?.Enabled == true && !string.IsNullOrEmpty(discordSettings.Token))
                    {
                        // Hook up TTS audio to Discord voice
                        Tts.Qwen3TtsService.OnTtsAudioChunk += (pcmData, sampleRate) =>
                        {
                            var pcmStream = Discord.DiscordNetBotManagerHeadless.GetPcmStream();
                            if (pcmStream != null && pcmData != null && pcmData.Length > 0)
                            {
                                try
                                {
                                    // Convert sample rate if needed (Discord expects 48kHz)
                                    if (sampleRate == 48000)
                                    {
                                        pcmStream.Write(pcmData, 0, pcmData.Length);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"   [Discord] TTS audio error: {ex.Message}");
                                }
                            }
                        };

                        // Hook up Discord messages to Maggie's brain
                        Discord.DiscordNetBotManagerHeadless.OnMessageReceived += (speaker, text) =>
                        {
                            Console.WriteLine($"\n💬 Discord [{speaker}]: {text}");
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var response = await ProcessChatMessage(speaker, text, ttsAvailable);
                                    // Send response back to Discord text channel
                                    await Discord.DiscordNetBotManagerHeadless.SendMessageAsync(response);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"   ⚠️  Discord message error: {ex.Message}");
                                }
                            });
                        };

                        discordEnabled = await Discord.DiscordNetBotManagerHeadless.StartAsync();
                        if (discordEnabled)
                        {
                            Console.WriteLine($"   ✓ Discord bot connected");
                            Console.WriteLine($"   ℹ️  Commands: !join <channel>, !leave, !status");
                        }
                    }
                    else
                    {
                        Console.WriteLine("   ℹ️  Discord bot disabled (set token in Settings/default.json to enable)");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️  Discord bot failed to start: {ex.Message}");
                    Console.WriteLine($"   ℹ️  This is non-critical; other features will work");
                }

                Console.WriteLine();
                Console.WriteLine("🎯 Maggie is ready!");
                Console.WriteLine($"   🎤 STT:      {(HeadlessVoiceRecognizer.Instance.IsReady ? "✓ Listening" : "✗ No model")}");
                Console.WriteLine($"   📡 WebRTC:   {(_webRtcEnabled ? $"✓ Port {webRtcPort}" : "✗ Disabled")}");
                Console.WriteLine($"   💬 Discord:  {(discordEnabled ? "✓ Connected" : "✗ Disabled")}");
                Console.WriteLine($"   🎤 STT:      {(HeadlessVoiceRecognizer.Instance.IsReady ? "✓ Listening" : "✗ No model")}");
                Console.WriteLine($"   📡 WebRTC:   {(_webRtcEnabled ? $"✓ Port {webRtcPort}" : "✗ Disabled")}");
                Console.WriteLine("   💬 Chat via: curl -X POST http://localhost:18790/api/chat");
                Console.WriteLine("   🌐 Voice UI: http://localhost:18790/voice/  (Qwen3-TTS chat)");
                if (_webRtcEnabled)
                {
                    Console.WriteLine($"   🌐 WebRTC:   http://{ip}:{webRtcPort}/ (browser voice chat)");
                }
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
                
                // Flush pending history writes before stopping
                try
                {
                    await OllamaService.FlushHistoryAsync();
                    Console.WriteLine("   ✓ Conversation history flushed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️  History flush error: {ex.Message}");
                }
                
                // Stop WebRTC server
                if (_webRtcServer != null)
                {
                    try
                    {
                        await _webRtcServer.StopAsync();
                        Console.WriteLine("   ✓ WebRTC server stopped");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ⚠️  WebRTC stop error: {ex.Message}");
                    }
                }
                
                // Stop voice recognizer
                if (HeadlessVoiceRecognizer.Instance.IsReady)
                {
                    try
                    {
                        HeadlessVoiceRecognizer.Instance.Stop();
                        Console.WriteLine("   ✓ Voice recognizer stopped");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   ⚠️  Voice recognizer stop error: {ex.Message}");
                    }
                }
                
                // Stop Discord bot
                try
                {
                    await Discord.DiscordNetBotManagerHeadless.ShutdownAsync();
                    Console.WriteLine("   ✓ Discord bot stopped");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️  Discord stop error: {ex.Message}");
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
        /// Process a chat message through the LLM and optionally speak the response.
        /// Optimized: Uses CancellationToken instead of Task.WhenAny for better performance.
        /// </summary>
        private static async Task<string> ProcessChatMessage(string speaker, string message, bool ttsAvailable)
        {
            // OPTIMIZATION: Use CancellationTokenSource instead of Task.WhenAny
            // Task.WhenAny creates an extra task allocation; CTS is more efficient
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tcs = new TaskCompletionSource<string>();
            
            // Link the timeout CTS to our TCS
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                var handler = new Action<string>(response => 
                {
                    tcs.TrySetResult(response);
                });
                
                try
                {
                    OllamaService.OnResponseReceived += handler;
                    await OllamaService.DispatchAsync(speaker, message);
                    
                    string response;
                    try
                    {
                        response = await tcs.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        return "(no response - timeout)";
                    }
                    
                    Console.WriteLine($"💬 Maggie: {response}");
                    
                    // Broadcast to WebRTC clients if available
                    if (_webRtcEnabled)
                    {
                        _webRtcServer?.BroadcastResponse(response);
                    }
                    
                    // Generate voice if TTS is available (fire-and-forget)
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
                finally
                {
                    OllamaService.OnResponseReceived -= handler;
                }
            }
        }
    }
}
