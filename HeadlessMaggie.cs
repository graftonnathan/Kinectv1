// HeadlessMaggie.cs - Console-based Maggie for Linux
// Minimal version: LLM brain + Jeff API only (no audio/vision)

using System;
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
            Console.WriteLine("║  🧠 Maggie AI - Headless Mode (Linux Compatible)      ║");
            Console.WriteLine("║  LLM Brain + Jeff API - No GUI, No Audio              ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            try
            {
                // Initialize settings
                Console.WriteLine("📋 Loading configuration...");
                
                // Create minimal App.SettingsProvider
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

                // Start Jeff API for communication
                Console.WriteLine("🌐 Starting Jeff API server...");
                Api.JeffApiServer.Start();
                Console.WriteLine($"   ✓ API listening on http://localhost:18790");
                Console.WriteLine();

                // Hook up Jeff API to Maggie's brain
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
                        return response;
                    }
                    return "(no response)";
                };

                Console.WriteLine("🎯 Maggie is ready and waiting for Jeff...");
                Console.WriteLine("   Send messages via: curl -X POST http://localhost:18790/api/chat");
                Console.WriteLine("   Or use: http://localhost:18790/api/status");
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
