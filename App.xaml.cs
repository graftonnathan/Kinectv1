using System;
using System.Threading.Tasks;
using System.Windows;
using System.Threading;

namespace Kinectv1
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Add global exception handling first
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            base.OnStartup(e);

            try
            {
                // 1) Console - keep this minimal
                ConsoleManager.AllocConsole();
                Console.WriteLine("🟢 App.OnStartup");

                // 2) Skip MemoryStore.Init() for now - defer it
                Console.WriteLine("⏩ Deferring MemoryStore initialization");

                // 3) Create and show main window IMMEDIATELY - no heavy operations
                var win = new MainWindow();
                win.Show();
                win.Activate();
                win.Topmost = true;
                win.Focus();
                
                // Remove topmost after delay
                Task.Delay(100).ContinueWith(_ => 
                {
                    win.Dispatcher.Invoke(() => win.Topmost = false);
                });
                
                Console.WriteLine("✅ MainWindow shown and activated");
                Console.WriteLine("🟢 App startup complete - UI ready IMMEDIATELY");
                Console.WriteLine("🔄 ALL initialization deferred to background");

                // 4) COMPLETELY defer ALL initialization - start after UI is rendered
                StartDeferredInitialization();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] Startup error: {ex}");
                try
                {
                    MessageBox.Show($"Application failed to start:\n{ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch
                {
                    Console.WriteLine("[FATAL] Could not show error MessageBox");
                }
                Shutdown();
            }
        }

        private void StartDeferredInitialization()
        {
            // Wait for UI to be fully rendered, then start ALL initialization
            Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                // Small delay to ensure UI is fully rendered
                await Task.Delay(500);
                
                Console.WriteLine("🚀 UI fully rendered - starting deferred initialization");
                Console.WriteLine("📱 Application is fully responsive - services loading in background");
                
                // Now start background services in completely isolated threads
                StartBackgroundServicesCompleteyIsolated();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void StartBackgroundServicesCompleteyIsolated()
        {
            // Use separate background threads with maximum isolation
            
            // 1) MemoryStore initialization - now in background
            new Thread(() =>
            {
                try
                {
                    Console.WriteLine("🔄 [MEM-Thread] Initializing MemoryStore...");
                    MemoryStore.Init();
                    Console.WriteLine("✅ [MEM-Thread] MemoryStore initialized");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ [MEM-Thread] MemoryStore.Init failed: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = "MemoryStore-Init"
            }.Start();

            // 2) Speaker model loading
            new Thread(() =>
            {
                try
                {
                    // Wait a bit to let MemoryStore initialize first
                    Thread.Sleep(1000);
                    
                    Console.WriteLine("🔄 [SPEAKER-Thread] Starting SpeakerEmbedder initialization...");
                    
                    var embModel = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "models", "pyannote_embedding.onnx");
                    
                    if (System.IO.File.Exists(embModel))
                    {
                        Console.WriteLine("📁 [SPEAKER-Thread] SpeakerEmbedder model found, loading...");
                        SpeakerEmbedder.Load(embModel);
                        Console.WriteLine("💻 [SPEAKER-Thread] SpeakerEmbedder loaded with CPU");
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ [SPEAKER-Thread] Speaker model not found: {embModel}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ [SPEAKER-Thread] SpeakerEmbedder failed: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = "SpeakerEmbedder-Init"
            }.Start();

            // 3) Kinect initialization
            new Thread(() =>
            {
                try
                {
                    // Wait a bit more for MemoryStore
                    Thread.Sleep(2000);
                    
                    Console.WriteLine("🔄 [KINECT-Thread] Starting Kinect initialization...");
                    KinectFaceTracker.Start();
                    Console.WriteLine("✅ [KINECT-Thread] Kinect initialization completed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ [KINECT-Thread] Kinect initialization failed: {ex.Message}");
                    Console.WriteLine("💡 Face recognition will not be available");
                }
            })
            {
                IsBackground = true,
                Name = "Kinect-Init"
            }.Start();

            // 4) Voice recognition initialization
            new Thread(() =>
            {
                try
                {
                    // Wait for MemoryStore to be ready
                    Thread.Sleep(3000);
                    
                    Console.WriteLine("🔄 [VOICE-Thread] Starting VoiceRecognizer initialization...");
                    
                    var voiceModelPath = "models/vosk-model-small-en-us-0.15";
                    if (System.IO.Directory.Exists(voiceModelPath))
                    {
                        Console.WriteLine("📁 [VOICE-Thread] Voice model found, loading...");
                        VoiceRecognizer.Start(voiceModelPath, "john");
                        Console.WriteLine("✅ [VOICE-Thread] VoiceRecognizer started");
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ [VOICE-Thread] Voice model not found: {voiceModelPath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ [VOICE-Thread] VoiceRecognizer failed: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = "VoiceRecognizer-Init"
            }.Start();

            // 5) Status monitoring thread
            new Thread(() =>
            {
                try
                {
                    // Wait for other services to have time to initialize
                    Thread.Sleep(10000); // 10 seconds
                    
                    Console.WriteLine("📊 [STATUS-Thread] === INITIALIZATION STATUS CHECK ===");
                    
                    try
                    {
                        var kinectStatus = KinectFaceTracker.GetSystemStatus();
                        Console.WriteLine($"🎯 [STATUS-Thread] Kinect Status: {kinectStatus}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ [STATUS-Thread] Could not get Kinect status: {ex.Message}");
                    }
                    
                    Console.WriteLine("🎤 [STATUS-Thread] Voice Recognition: Check microphone levels in UI");
                    Console.WriteLine("📊 [STATUS-Thread] === STATUS CHECK COMPLETE ===");
                    Console.WriteLine("💡 [STATUS-Thread] If any services failed, you can still use the application");
                    Console.WriteLine("📺 [STATUS-Thread] Try opening the video feed to test functionality");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ [STATUS-Thread] Status check failed: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = "Status-Monitor"
            }.Start();

            Console.WriteLine("🎭 All background services started in isolated threads");
            Console.WriteLine("🎯 Each service runs completely independently");
            Console.WriteLine("⚡ UI thread is completely free and responsive");
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine($"[FATAL] Unhandled exception: {e.ExceptionObject}");
            try
            {
                MessageBox.Show($"Unhandled exception:\n{e.ExceptionObject}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                Console.WriteLine("[FATAL] Could not show MessageBox for unhandled exception");
            }
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Console.WriteLine($"[FATAL] Dispatcher exception: {e.Exception}");
            try
            {
                MessageBox.Show($"UI thread exception:\n{e.Exception.Message}", "UI Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                Console.WriteLine("[FATAL] Could not show MessageBox for dispatcher exception");
            }
            e.Handled = true; // Prevent crash
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Console.WriteLine($"[ERROR] Unobserved task exception: {e.Exception}");
            e.SetObserved(); // Prevent process termination
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Console.WriteLine("🔄 Application shutting down...");
                
                // Quick shutdown - don't wait for background threads
                try
                {
                    KinectFaceTracker.Stop();
                    Console.WriteLine("✅ Kinect stopped");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Error stopping Kinect: {ex.Message}");
                }

                try
                {
                    VoiceRecognizer.Stop();
                    Console.WriteLine("✅ VoiceRecognizer stopped");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Error stopping VoiceRecognizer: {ex.Message}");
                }

                try
                {
                    MemoryStore.Save();
                    Console.WriteLine("✅ MemoryStore saved");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Error saving MemoryStore: {ex.Message}");
                }

                try
                {
                    ConsoleManager.FreeConsole();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Error freeing console: {ex.Message}");
                }
                
                Console.WriteLine("🔄 Application shutdown complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Exit cleanup failed: {ex}");
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }
}
