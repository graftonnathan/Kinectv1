using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kinectv1.Discord;
using Kinectv1.Settings;
using Kinectv1.Tts;
using Kinectv1.Voice;

namespace Kinectv1
{
    public partial class MainWindow : Window
    {
        private float[] _lastVoiceEmbedding = null;
        private bool _isDarkMode = true; // Default to dark mode

        // Cancellation token for cleanup
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private bool _isClosing = false;

        // Latest RMS values (pull-model)
        private volatile float _latestRmsValue = 0f;
        private volatile float _latestDiscordRmsValue = 0f;
        private volatile float _latestWebRtcRmsValue = 0f;

        // ENHANCED DOUBLE REGISTRATION PREVENTION - Discord initialization protection
        private static int _discordInitInProgress = 0; // 0 = not in progress, 1 = in progress

        // Audio input settings
        private bool _isMicrophoneInputEnabled = true;
        private bool _isDiscordInputEnabled = false;
        private bool _isWebRtcInputEnabled = false;
        private AudioInMode _currentAudioMode = AudioInMode.LocalMic;
        private bool _updatingAudioMode = false;

        // Missing UI control placeholders to prevent compilation errors
        private ComboBox ToneComboBox = new ComboBox();
        private TextBox TestOllamaPromptTextBox = new TextBox();

        // Embedded settings window host
        private UI.Settings.SettingsWindow _embeddedSettingsWindow;

        // --- RMS Visualization Optimizations (Phase 2) ---
        private DispatcherTimer _rmsUiTimer; // single timer drives all RMS UI updates
        private SolidColorBrush _rmsGreenBrush, _rmsOrangeBrush, _rmsRedBrush;
        private SolidColorBrush _discordLowBrush, _discordMidBrush, _discordHighBrush;
        private double _lastMicPct = -1, _lastDiscordPct = -1, _lastWebRtcPct = -1;
        private int _lastMicBucket = -1, _lastDiscordBucket = -1, _lastWebRtcBucket = -1;
        private float _smoothedRms = 0f; // baseline RMS (mic)
        private float _smoothedDiscordRms = 0f; // baseline discord
        private float _smoothedWebRtcRms = 0f; // baseline WebRTC
        // NEW: track last RMS update times to allow decay when capture pauses
        private DateTime _lastMicRmsTime = DateTime.MinValue;
        private DateTime _lastDiscordRmsTime = DateTime.MinValue;
        private DateTime _lastWebRtcRmsTime = DateTime.MinValue;

        private double _localTtsVolume = 1.0; // 100%
        private double _discordTtsVolume = 1.0; // 100%

        // Track when streaming TTS was recently active to prevent fallback double-play after barge-in
        private DateTime _lastStreamingTtsActive = DateTime.MinValue;

        // WebRTC service and audio queue for STT
        private WebRtcAudioTransport _webRtcTransport;
        private DroppingAudioQueue<AudioFrame> _webRtcSttQueue;
        private CancellationTokenSource _webRtcCts;
        private Task _webRtcSttTask;

        public MainWindow()
        {
            // Guard UI event handlers from firing during initialization (e.g., CheckBox.Checked)
            _updatingAudioMode = true;
            try
            {
                InitializeComponent();

                // Load and apply saved window settings
                LoadWindowSettings();

                // Load and apply theme settings
                LoadThemeSettings();

                // Set window properties for better focus behavior
                this.ShowInTaskbar = true;
                this.Title = "Kinect Face & Voice Recognition";

                // Add keyboard shortcuts for testing
                this.KeyDown += (sender, e) =>
                {
                    if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        try
                        {
                            Console.WriteLine("🧪 Testing Identity Fusion System (Ctrl+F pressed)");
                            IdentityFusionTracker.TestFusion();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error testing fusion: {ex.Message}");
                        }
                    }
                    else if (e.Key == Key.H && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        try
                        {
                            Console.WriteLine("🧪 Hosted services quick-check (Ctrl+H pressed)");
                            // Placeholder to avoid missing test harness type in release builds
                            Task.Run(async () => { await Task.Delay(10); Console.WriteLine("Hosted services check placeholder."); });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error testing hosted services: {ex.Message}");
                        }
                    }
                    else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        try
                        {
                            Console.WriteLine("🧪 Shutdown lifecycle quick-check (Ctrl+S pressed)");
                            // Placeholder to avoid missing test harness type in release builds
                            Task.Run(async () => { await Task.Delay(10); Console.WriteLine("Shutdown lifecycle check placeholder."); });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error testing shutdown lifecycle: {ex.Message}");
                        }
                    }
                };

                // Hook GUI events
                try
                {
                    VoiceRecognizer.OnTranscription += UpdateTranscription;
                    VoiceRecognizer.OnRmsLevel += UpdateRmsLevel;
                    VoiceRecognizer.OnDiscordRmsLevel += UpdateDiscordRmsLevel; // NEW: Discord RMS event
                    // Mumble RMS is driven directly from MumbleClientManager.OnRmsLevel
                    VoiceRecognizer.OnSpeakerMatch += ShowSpeakerMatch; // Will no-op (live view disabled)
                    // Show only resolved-at-dispatch events
                    VoiceRecognizer.OnSpeakerResolvedForOllama += ShowSpeakerResolvedForOllama;
                    VoiceRecognizer.OnNameHeard += name => EnhancedKinectFaceTracker.QueueLabel(name);
                    VoiceRecognizer.OnVoiceEmbedding += OnVoiceEmbedding; // capture embeddings

                    // Ensure final-only UI and LLM dispatch wiring
                    WireTranscriptionEvents();
                    WireDispatchPipeline();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook VoiceRecognizer events: {ex.Message}");
                }

                // Hook TTS events
                try
                {
                    TtsService.OnTtsSpeakingStarted += OnTtsSpeakingStarted;
                    TtsService.OnTtsSpeakingFinished += OnTtsSpeakingFinished;
                    TtsService.OnTtsError += OnTtsError;
                    Console.WriteLine("TTS service events hooked");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook TTS events: {ex.Message}");
                }

                // Hook Discord bot events
                try
                {
                    DiscordNetBotManager.OnBotStatusChanged += OnDiscordBotStatusChanged;
                    DiscordNetBotManager.OnVoiceMessageReceived += OnDiscordVoiceMessageReceived;
                    DiscordNetBotManager.OnErrorOccurred += OnDiscordBotError;
                    Console.WriteLine("Discord bot events hooked");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook Discord bot events: {ex.Message}");
                }

                // Start enhanced face tracker (required for enroll face/video)
                try
                {
                    EnhancedKinectFaceTracker.Start();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to start face tracker: {ex.Message}");
                }

                // Load and apply saved settings
                LoadApplicationSettings();

                // Initialize audio input control states
                InitializeAudioInputControls();

                // Initialize audio devices UI
                InitializeAudioDevicesUI();

                // Initialize Ollama model dropdown
                InitializeOllamaModels();

                // Initialize TTS system (this will handle speaker dropdown population and loading)
                InitializeTtsSystem();

                // Initialize Discord bot if enabled
                InitializeDiscordBot();

                // Initialize volume controls
                InitializeVolumeControls();

                // Initialize identity fusion cleanup timer
                InitializeIdentityFusionCleanup();

                // Initialize embedded settings into the Settings tab
                InitializeEmbeddedSettings();

                // NEW: Initialize optimized RMS visualization pull model
                InitializeRmsVisualizer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MainWindow initialization failed: {ex.Message}");
                this.Title = "Kinect Face & Voice Recognition - Error";

                try
                {
                    MessageBox.Show($"MainWindow initialization error:\n{ex.Message}\n\nSome features may not work properly.",
                        "Initialization Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                    Console.WriteLine("Could not show initialization warning");
                }
            }
            finally
            {
                // Allow user-driven mode changes after initial wiring
                _updatingAudioMode = false;
            }
        }

        private void InitializeRmsVisualizer()
        {
            try
            {
                // Cache theme brushes once (fallback to defaults if missing)
                _rmsGreenBrush = (TryFindResource("AccentGreen") as SolidColorBrush) ?? new SolidColorBrush(Colors.Green);
                _rmsOrangeBrush = (TryFindResource("AccentOrange") as SolidColorBrush) ?? new SolidColorBrush(Colors.Orange);
                _rmsRedBrush = (TryFindResource("AccentRed") as SolidColorBrush) ?? new SolidColorBrush(Colors.Red);

                _discordLowBrush = (TryFindResource("AccentBlue") as SolidColorBrush) ?? new SolidColorBrush(Colors.SteelBlue);
                _discordMidBrush = (TryFindResource("AccentPurple") as SolidColorBrush) ?? new SolidColorBrush(Colors.MediumPurple);
                _discordHighBrush = (TryFindResource("AccentOrange") as SolidColorBrush) ?? new SolidColorBrush(Colors.Orange);

                _rmsUiTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS
                };
                _rmsUiTimer.Tick += (s, e) => RmsUiTick();
                _rmsUiTimer.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitializeRmsVisualizer failed: {ex.Message}");
            }
        }

        private void RmsUiTick()
        {
            if (_isClosing) return;
            var now = DateTime.UtcNow;

            // MIC (Local)
            if (_isMicrophoneInputEnabled && RmsBar != null && RmsText != null)
            {
                bool stale = (now - _lastMicRmsTime).TotalMilliseconds > 200; // >200ms no new frames
                var input = stale ? 0f : _latestRmsValue; // raw scale 0..10000*

                if (input > _smoothedRms)
                    _smoothedRms = _smoothedRms * 0.4f + input * 0.6f; // attack
                else
                    _smoothedRms = _smoothedRms * 0.85f + input * 0.15f; // decay

                // Accelerated decay when stale (capture paused / silence)
                if (stale && input == 0f)
                {
                    _smoothedRms *= 0.80f; // speed up fall
                    if (_smoothedRms < 5f) _smoothedRms = 0f; // snap to absolute zero near floor
                }

                var pct = Math.Max(0.0, Math.Min(100.0, (_smoothedRms / 10000.0) * 100.0));

                // Always update if stale & decreasing even if change < 0.5 to avoid plateau perception
                bool forceUpdate = stale && pct < _lastMicPct;
                if (forceUpdate || Math.Abs(pct - _lastMicPct) >= 0.5)
                {
                    RmsBar.Value = pct;
                    _lastMicPct = pct;
                    int pctIntNow = (int)pct;
                    RmsText.Text = $"RMS: {_smoothedRms:F1} ({pctIntNow}%)";
                }
                else if (RmsText.Text.Length == 0)
                {
                    RmsText.Text = $"RMS: {_smoothedRms:F1} ({(int)pct}%)";
                }

                int bucket = (pct <= 33) ? 0 : (pct <= 66 ? 1 : 2);
                if (bucket != _lastMicBucket)
                {
                    _lastMicBucket = bucket;
                    RmsBar.Foreground = bucket == 0 ? _rmsGreenBrush : bucket == 1 ? _rmsOrangeBrush : _rmsRedBrush;
                }
            }

            // DISCORD (now unified scaling 0..10000 like mic)
            if (_isDiscordInputEnabled && DiscordRmsBar != null && DiscordRmsText != null)
            {
                bool staleD = (now - _lastDiscordRmsTime).TotalMilliseconds > 200; // align with mic stale window
                var input = staleD ? 0f : _latestDiscordRmsValue; // raw scale 0..10000*

                if (input > _smoothedDiscordRms)
                    _smoothedDiscordRms = _smoothedDiscordRms * 0.4f + input * 0.6f; // same attack
                else
                    _smoothedDiscordRms = _smoothedDiscordRms * 0.85f + input * 0.15f; // same decay

                if (staleD && input == 0f)
                {
                    _smoothedDiscordRms *= 0.80f;
                    if (_smoothedDiscordRms < 5f) _smoothedDiscordRms = 0f; // same floor snap as mic
                }

                var pct = Math.Max(0.0, Math.Min(100.0, (_smoothedDiscordRms / 10000.0) * 100.0));
                bool forceUpdate = staleD && pct < _lastDiscordPct;
                if (forceUpdate || Math.Abs(pct - _lastDiscordPct) >= 0.5)
                {
                    DiscordRmsBar.Value = pct;
                    _lastDiscordPct = pct;
                    DiscordRmsText.Text = $"RMS: {_smoothedDiscordRms:F1} ({(int)pct}%)";
                }
                else if (DiscordRmsText.Text.Length == 0)
                {
                    DiscordRmsText.Text = $"RMS: {_smoothedDiscordRms:F1} ({(int)pct}%)";
                }

                int bucket = (pct <= 33) ? 0 : (pct <= 66 ? 1 : 2);
                if (bucket != _lastDiscordBucket)
                {
                    _lastDiscordBucket = bucket;
                    DiscordRmsBar.Foreground = bucket == 0 ? _discordLowBrush : bucket == 1 ? _discordMidBrush : _discordHighBrush;
                }
            }

            // WEBRTC
            try
            {
                var bar = this.FindName("WebRtcRmsBar") as ProgressBar;
                var text = this.FindName("WebRtcRmsText") as TextBlock;
                // Show when WebRTC input mode is selected
                bool showWebRtc = _isWebRtcInputEnabled;
                if (showWebRtc && bar != null && text != null)
                {
                    bool staleW = (now - _lastWebRtcRmsTime).TotalMilliseconds > 250;
                    var input = staleW ? 0f : _latestWebRtcRmsValue; // raw scale 0..10000 (same as mic)

                    if (input > _smoothedWebRtcRms)
                        _smoothedWebRtcRms = _smoothedWebRtcRms * 0.4f + input * 0.6f;
                    else
                        _smoothedWebRtcRms = _smoothedWebRtcRms * 0.85f + input * 0.15f;

                    if (staleW && input == 0f)
                    {
                        _smoothedWebRtcRms *= 0.80f;
                        if (_smoothedWebRtcRms < 5f) _smoothedWebRtcRms = 0f;
                    }

                    var pct = Math.Max(0.0, Math.Min(100.0, (_smoothedWebRtcRms / 10000.0) * 100.0));
                    bool forceUpdate = staleW && pct < _lastWebRtcPct;
                    if (forceUpdate || Math.Abs(pct - _lastWebRtcPct) >= 0.5)
                    {
                        bar.Value = pct;
                        _lastWebRtcPct = pct;
                        text.Text = $"RMS: {_smoothedWebRtcRms:F1} ({(int)pct}%)";
                    }
                    else if (text.Text.Length == 0)
                    {
                        text.Text = $"RMS: {_smoothedWebRtcRms:F1} ({(int)pct}%)";
                    }

                    int bucket = (pct <= 33) ? 0 : (pct <= 66 ? 1 : 2);
                    if (bucket != _lastWebRtcBucket)
                    {
                        _lastWebRtcBucket = bucket;
                        bar.Foreground = bucket == 0 ? _rmsGreenBrush : bucket == 1 ? _rmsOrangeBrush : _rmsRedBrush;
                    }
                }
            }
            catch { }
        }

        private void InitializeEmbeddedSettings()
        {
            try
            {
                _embeddedSettingsWindow = new UI.Settings.SettingsWindow();
                // Expose to embedded child editors so their buttons can route to the host
                UI.Settings.SettingsWindow.CurrentEmbedded = _embeddedSettingsWindow;

                if (_embeddedSettingsWindow.Content is FrameworkElement content && SettingsHost != null)
                {
                    // Use the SettingsWindow's ViewModel as DataContext for embedded content
                    content.DataContext = _embeddedSettingsWindow.DataContext;
                    _embeddedSettingsWindow.Content = null; // detach visual tree from Window
                    SettingsHost.Content = content;

                    // Explicitly populate from current snapshot since Window.Loaded will not fire when embedded
                    _embeddedSettingsWindow.PopulateEditorFromCurrentSnapshot();

                    // Keep UI in sync if settings change elsewhere and reload STT model from settings
                    try
                    {
                        var svc = Kinectv1.App.SettingsProvider;
                        if (svc != null)
                        {
                            svc.Changed += (s, snap) =>
                            {
                                // Reload Vosk model per settings path
                                try { VoiceRecognizer.ReloadFromSettings(); } catch { }

                                // Refresh the embedded editor view
                                try { Dispatcher.BeginInvoke(new Action(() => _embeddedSettingsWindow.PopulateEditorFromCurrentSnapshot()), DispatcherPriority.Background); } catch { }
                            };
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Settings embed init failed: {ex.Message}");
            }
        }

        private void LoadThemeSettings()
        {
            try
            {
                var dark = Kinectv1.App.SettingsProvider?.Current?.Ui?.DarkMode ?? true;
                _isDarkMode = dark;
                ApplyTheme(_isDarkMode);
                Console.WriteLine($"🎨 Theme loaded: {(_isDarkMode ? "Dark" : "Light")} mode");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load theme settings: {ex.Message}");
                _isDarkMode = true;
                ApplyTheme(true);
            }
        }

        private void ApplyTheme(bool isDarkMode)
        {
            try
            {
                if (isDarkMode)
                {
                    // Dark theme colors
                    this.Resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                    this.Resources["SurfaceBackground"] = new SolidColorBrush(Color.FromRgb(45, 45, 48));
                    this.Resources["SurfaceBackgroundLight"] = new SolidColorBrush(Color.FromRgb(63, 63, 70));
                    this.Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(70, 70, 71));
                    this.Resources["TextPrimary"] = new SolidColorBrush(Colors.White);
                    this.Resources["TextSecondary"] = new SolidColorBrush(Color.FromRgb(224, 224, 224));
                    this.Resources["TextMuted"] = new SolidColorBrush(Color.FromRgb(176, 176, 176));

                    // Update TextBox text colors directly
                    EnrollNameBox.Foreground = Brushes.White;
                    TtsTestTextBox.Foreground = Brushes.White;
                    EnrollNameBox.CaretBrush = Brushes.White;
                    TtsTestTextBox.CaretBrush = Brushes.White;

                    Console.WriteLine("🌙 Dark theme applied");
                }
                else
                {
                    // Light theme colors
                    this.Resources["WindowBackground"] = new SolidColorBrush(Colors.White);
                    this.Resources["SurfaceBackground"] = new SolidColorBrush(Color.FromRgb(248, 248, 248));
                    this.Resources["SurfaceBackgroundLight"] = new SolidColorBrush(Color.FromRgb(232, 232, 232));
                    this.Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(208, 208, 208));
                    this.Resources["TextPrimary"] = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                    this.Resources["TextSecondary"] = new SolidColorBrush(Color.FromRgb(64, 64, 64));
                    this.Resources["TextMuted"] = new SolidColorBrush(Color.FromRgb(96, 96, 96));

                    // Update TextBox text colors directly
                    EnrollNameBox.Foreground = Brushes.Black;
                    TtsTestTextBox.Foreground = Brushes.Black;
                    EnrollNameBox.CaretBrush = Brushes.Black;
                    TtsTestTextBox.CaretBrush = Brushes.Black;

                    Console.WriteLine("☀️ Light theme applied");
                }

                // Accent colors remain the same for both themes
                this.Resources["AccentBlue"] = new SolidColorBrush(Color.FromRgb(0, 120, 212));
                this.Resources["AccentGreen"] = new SolidColorBrush(Color.FromRgb(16, 124, 16));
                this.Resources["AccentOrange"] = new SolidColorBrush(Color.FromRgb(255, 140, 0));
                this.Resources["AccentRed"] = new SolidColorBrush(Color.FromRgb(231, 76, 60));
                this.Resources["AccentPurple"] = new SolidColorBrush(Color.FromRgb(139, 92, 246));

                // Update theme toggle button text
                if (ThemeToggleButton != null)
                {
                    ThemeToggleButton.Content = isDarkMode ? "☀️ Light Mode" : "🌙 Dark Mode";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to apply theme: {ex.Message}");
            }
        }

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isDarkMode = !_isDarkMode;
                ApplyTheme(_isDarkMode);

                // Persist via JSON settings
                var svc = Kinectv1.App.SettingsProvider; var curr = svc?.Current;
                if (svc != null && curr != null)
                {
                    var next = curr with { Ui = new Kinectv1.Settings.UiSettings(_isDarkMode) };
                    svc.Save(next);
                }

                Console.WriteLine($"🎨 Theme switched to: {(_isDarkMode ? "Dark" : "Light")} mode");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to toggle theme: {ex.Message}");
            }
        }

        private void OnFaceDetected(int left, int top, int width, int height, string name, float confidence)
        {
            // Handle face detection events for main window integration
        }

        // NEW: Handle all detected faces with tracking information
        private void OnAllFacesDetected(List<EnhancedKinectFaceTracker.FaceTrackingInfo> faces)
        {
            // This receives ALL detected faces with their tracking IDs, recognition status, and emotions
            foreach (var face in faces)
            {
                string status = face.IsRecognized ? $"Recognized: {face.Name} ({face.Confidence:F2})" : "Unknown";
                string emotionInfo = face.Emotion.PrimaryEmotion != "Neutral" ? $" | Emotion: {face.Emotion}" : "";

                // Only log significant changes or new faces to reduce console spam
                if (face.IsRecognized && face.Confidence > 0.5f && face.Emotion.PrimaryEmotion != "Neutral")
                {
                    Console.WriteLine($"Face TrackingID {face.TrackingId}: {status}{emotionInfo} at ({face.Left},{face.Top}) {face.Width}x{face.Height}");
                }
            }
        }

        // NEW: Handle identity fusion updates - replaces ad-hoc speaker fallback
        private void OnIdentityFused(ulong trackingId, string fusedName, float fusedScore)
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            // Live speaker/fusion view disabled – we only show dispatched speaker
            // Keep debug log for diagnostics, but do not update SpeakerLabel here
            try
            {
                Console.WriteLine($"🔀 Identity Fusion TrackingID {trackingId}: {fusedName} (score={fusedScore:F3})");
            }
            catch { }
            return;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            this.Activate();
            this.Focus();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
        }

        // Enhanced transcription display with debugging info
        private void UpdateTranscription(string text)
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return; // Double check inside dispatcher

                    // Check if UI element is still valid
                    if (TranscriptionLabel != null)
                    {
                        // Update the main transcription label
                        TranscriptionLabel.Content = text;
                    }
                });
            }
            catch (Exception ex)
            {
                // Suppress exceptions during shutdown
                if (!_isClosing)
                {
                    Console.WriteLine($"❌ Error updating transcription: {ex.Message}");
                }
            }
        }

        // RMS Update logic (EVENT THREAD): now only stores latest value; UI refresh handled by timer
        private void UpdateRmsLevel(float rawRms)
        {
            if (_isClosing) return;
            _latestRmsValue = rawRms;
            _lastMicRmsTime = DateTime.UtcNow; // NEW
        }

        private void UpdateDiscordRmsLevel(float rawRms)
        {
            if (_isClosing) return;
            _latestDiscordRmsValue = rawRms;
            _lastDiscordRmsTime = DateTime.UtcNow; // NEW
        }

        // Button Event Handlers and settings tab handler
        private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // No longer used; settings are embedded in a tab.
            try
            {
                var wnd = new UI.Settings.SettingsWindow();
                wnd.Owner = this;
                wnd.Show();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to open settings window: {ex.Message}");
            }
        }

        // Add back missing XAML handlers referenced by MainWindow.xaml
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            try { _ = StopWebRtcAsync(); } catch { }
            try { _rmsUiTimer?.Stop(); _rmsUiTimer = null; } catch { }
            try { Application.Current.Shutdown(); } catch { }
        }

        private void MicInputEnabledCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingAudioMode) return;
            PersistAudioMode(AudioInMode.LocalMic);
            ApplyAudioMode(AudioInMode.LocalMic);
        }

        private void MicInputEnabledCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_updatingAudioMode) return;
            // Prevent invalid "none selected"; revert to current mode
            ApplyAudioMode(_currentAudioMode);
        }

        private void DiscordInputEnabledCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingAudioMode) return;
            PersistAudioMode(AudioInMode.DiscordVoice);
            ApplyAudioMode(AudioInMode.DiscordVoice);
        }

        private void DiscordInputEnabledCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_updatingAudioMode) return;
            // Prevent invalid "none selected"; revert to current mode
            ApplyAudioMode(_currentAudioMode);
        }

        private void WebRtcInputEnabledCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingAudioMode) return;
            PersistAudioMode(AudioInMode.WebRtcVoice);
            ApplyAudioMode(AudioInMode.WebRtcVoice);
        }

        private void WebRtcInputEnabledCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_updatingAudioMode) return;
            // Prevent none-selected; revert to persisted/current mode
            ApplyAudioMode(_currentAudioMode);
        }

        private void TtsModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Persist selected TTS model path if items contain paths; no-op if bound differently
            try
            {
                var sel = TtsModelComboBox.SelectedItem?.ToString();
                if (!string.IsNullOrWhiteSpace(sel))
                {
                    var svc = App.SettingsProvider; var curr = svc?.Current;
                    if (svc != null && curr != null)
                    {
                        var next = curr with { Tts = curr.Tts with { ModelPath = sel } };
                        svc.Save(next);
                    }
                    TtsModelStatusText.Text = $"Model selected: {sel}";
                }
            }
            catch (Exception ex) { Console.WriteLine($"TTS model selection error: {ex.Message}"); }
        }

        private void TtsSpeakerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var selItem = TtsSpeakerComboBox.SelectedItem as ComboBoxItem;
                var speaker = selItem?.Content?.ToString() ?? selItem?.Tag?.ToString();
                if (!string.IsNullOrWhiteSpace(speaker))
                {
                    var svc = App.SettingsProvider; var curr = svc?.Current;
                    if (svc != null && curr != null)
                    {
                        var next = curr with { Tts = curr.Tts with { Speaker = speaker } };
                        svc.Save(next);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"TTS speaker selection error: {ex.Message}"); }
        }

        private void TtsGpuToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var svc = App.SettingsProvider; var curr = svc?.Current;
                if (svc != null && curr != null)
                {
                    var newExec = (curr.Tts.Execution == Kinectv1.Settings.TtsExecution.GPU) ? Kinectv1.Settings.TtsExecution.CPU : Kinectv1.Settings.TtsExecution.GPU;
                    var next = curr with { Tts = curr.Tts with { Execution = newExec } };
                    svc.Save(next);
                    TtsGpuToggleButton.Content = (newExec == Kinectv1.Settings.TtsExecution.GPU) ? "⚡ GPU" : "⚙ CPU";
                    // Recreate session to apply
                    try { TtsService.RecreateSessionFromSettings(); } catch { }
                }
            }
            catch (Exception ex) { Console.WriteLine($"TTS GPU toggle error: {ex.Message}"); }
        }

        // --- No-op stubs referenced during initialization ---
        private void LoadWindowSettings() { }
        private void LoadApplicationSettings() { }
        private void InitializeAudioDevicesUI() { }
        private void InitializeOllamaModels() { }
        private void InitializeTtsSystem()
        {
            // Pre-warm TTS session in background to avoid cold-start latency
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500); // Brief delay to let other init complete first
                    TtsService.Prewarm();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"TTS prewarm error: {ex.Message}");
                }
            });
        }
        private void InitializeDiscordBot()
        {
            try
            {
                // Start Discord bot at app startup when enabled and token is present
                var discord = App.SettingsProvider?.Current?.Discord;
                if (discord != null && discord.Enabled && !string.IsNullOrWhiteSpace(discord.Token))
                {
                    // Avoid duplicate startups with atomic guard inside manager
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var ok = await DiscordNetBotManager.StartAsync();
                            if (!ok)
                            {
                                Console.WriteLine("Discord bot failed to start. Check token and network.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Discord init error: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"InitializeDiscordBot failed: {ex.Message}");
            }
        }
        private void InitializeVolumeControls() { }
        private void InitializeIdentityFusionCleanup() { }

        // Implement ShowSpeakerMatch to update UI on voice matches
        private void ShowSpeakerMatch(string speakerName, float confidence)
        {
            // Live voice view disabled – only show dispatched speaker resolution
            try
            {
                Console.WriteLine($"[Live voice match suppressed] {speakerName} ({confidence:F2})");
            }
            catch { }
            return;
        }

        // Show speaker resolved at dispatch time (what is actually sent to Ollama)
        private void ShowSpeakerResolvedForOllama(string speakerName, float confidence, string method)
        {
            try
            {
                if (_isClosing) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosing) return;
                    if (SpeakerLabel == null) return;

                    var display = string.IsNullOrWhiteSpace(speakerName) ? "UnknownSpeaker" : speakerName;
                    var score = Math.Max(0f, Math.Min(1f, confidence));

                    SpeakerLabel.Content = $"{display} ({score:F2}) [Dispatched]";

                    // Theme-aware background based on confidence
                    Brush backgroundBrush;
                    if (display == "UnknownSpeaker" || score < 0.3f)
                        backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(101, 68, 68) : Color.FromRgb(255, 192, 192));
                    else if (score < 0.5f)
                        backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(102, 85, 68) : Color.FromRgb(255, 255, 128));
                    else if (score < 0.7f)
                        backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(68, 85, 102) : Color.FromRgb(192, 224, 255));
                    else
                        backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(68, 102, 68) : Color.FromRgb(192, 255, 192));

                    SpeakerLabel.Background = backgroundBrush;
                    if (OllamaStatusText != null)
                    {
                        OllamaStatusText.Text = $"Dispatching as: {display} ({score:F2}) via {method}";
                    }
                }), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ShowSpeakerResolvedForOllama failed: {ex.Message}");
            }
        }

        // Stubs for features referenced earlier to fix missing symbol errors
        private void UpdateVoiceEnrollmentProgress(string name, int current, int total)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (VoiceEnrollmentPanel == null) return;
                    VoiceEnrollmentPanel.Visibility = Visibility.Visible;
                    if (VoiceEnrollStatusText != null)
                        VoiceEnrollStatusText.Text = $"Enrolling '{name}'...";
                    if (VoiceEnrollProgress != null)
                    {
                        VoiceEnrollProgress.Maximum = total;
                        VoiceEnrollProgress.Value = current;
                    }
                    if (VoiceProgressText != null)
                        VoiceProgressText.Text = $"{current}/{total}";
                    if (VoiceProgressPercent != null)
                    {
                        var pct = total > 0 ? (int)(current * 100.0 / total) : 0;
                        VoiceProgressPercent.Text = $"({pct}%)";
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Voice enrollment progress update failed: {ex.Message}");
            }
        }
        private void OnVoiceEnrollmentComplete(string name)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (VoiceEnrollStatusText != null)
                        VoiceEnrollStatusText.Text = $"✅ Voice enrollment complete for '{name}'";
                    if (VoiceEnrollmentPanel != null)
                        VoiceEnrollmentPanel.Visibility = Visibility.Collapsed;
                });
                Console.WriteLine($"🎤 Voice enrollment finished for {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Voice enrollment complete handler failed: {ex.Message}");
            }
        }
        private void OnVoiceEnrollmentCancelled(string name)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (VoiceEnrollStatusText != null)
                        VoiceEnrollStatusText.Text = $"✖ Enrollment cancelled for '{name}'";
                    if (VoiceEnrollmentPanel != null)
                        VoiceEnrollmentPanel.Visibility = Visibility.Collapsed;
                });
                Console.WriteLine($"Voice enrollment cancelled for {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Voice enrollment cancel handler failed: {ex.Message}");
            }
        }
        private void OnVoiceEmbedding(float[] embedding) { _lastVoiceEmbedding = embedding; try { if (VoiceEnrollmentManager.IsEnrolling) VoiceEnrollmentManager.ProcessVoiceSample(embedding); } catch { } }
        private void OnOllamaPromptSent(string prompt) { }
        
        /// <summary>
        /// Handle streaming chunks from LLM for real-time UI updates.
        /// </summary>
        private void OnOllamaResponseChunk(string chunk)
        {
            if (_isClosing || string.IsNullOrEmpty(chunk)) return;
            
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosing) return;
                    if (OllamaResponseBox != null)
                    {
                        OllamaResponseBox.Text += chunk;
                        OllamaResponseBox.ScrollToEnd();
                    }
                }), DispatcherPriority.Background);
            }
            catch { }
        }

        /// <summary>
        /// Handle complete sentences from LLM stream for immediate TTS.
        /// This is the key to reducing latency - TTS starts while LLM is still generating.
        /// </summary>
        private void OnOllamaResponseSentenceReady(string sentence)
        {
            if (_isClosing || string.IsNullOrWhiteSpace(sentence)) return;

            // Mark that we're actively streaming TTS
            _lastStreamingTtsActive = DateTime.UtcNow;

            try
            {
                var ttsEnabled = false;
                try { ttsEnabled = Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { }
                if (!ttsEnabled) return;

                var voice = App.SettingsProvider?.Current?.Tts?.Speaker;
                
                // Determine routing based on active input mode
                var speakLocal = _isMicrophoneInputEnabled; // Local mic mode = local speakers
                var speakDiscord = _isDiscordInputEnabled && Kinectv1.Discord.DiscordNetBotManager.IsInVoiceChannel;
                var speakWebRtc = _isWebRtcInputEnabled; // Remove direct connection check

                Console.WriteLine($"[TTS Stream] Sentence ready ({sentence.Length} chars), local={speakLocal}, discord={speakDiscord}, webRtc={speakWebRtc}");

                if (speakLocal)
                {
                    TtsService.QueueSentenceForStreaming(sentence, voice);
                }

                if (speakDiscord)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await Kinectv1.Discord.DiscordNetBotManager.SendTtsToDiscordAsync(sentence, voice); }
                        catch (Exception ex) { Console.WriteLine($"Discord streaming TTS error: {ex.Message}"); }
                    });
                }
                
                if (speakWebRtc)
                {
                    // Remove WebRTC send call for now; direct connection not available
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OnOllamaResponseSentenceReady error: {ex.Message}");
            }
        }

        private void OnOllamaResponseReceived(string response)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(response)) return;
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_isClosing) return;
                        if (OllamaResponseBox != null)
                        {
                            OllamaResponseBox.Text = response;
                            OllamaResponseBox.ScrollToEnd();
                        }
                        if (OllamaStatusText != null)
                        {
                            OllamaStatusText.Text = "✅ Ollama: Response complete";
                        }
                    }), DispatcherPriority.Background);
                }
                catch { }

                // TTS is handled via OnOllamaResponseSentenceReady for streaming.
                // DO NOT fall back to full-response TTS - this causes the "repeat" bug.
                // If streaming was used, sentences were already queued.
                // If streaming was cancelled (barge-in), we don't want to play the old response.
                
                // Only use fallback if streaming was NEVER active for this response
                // (e.g., non-streaming mode or immediate error)
                var timeSinceStreaming = (DateTime.UtcNow - _lastStreamingTtsActive).TotalMilliseconds;
                bool streamingWasUsed = timeSinceStreaming < 30000; // 30 second window - if any streaming happened recently
                
                if (streamingWasUsed)
                {
                    // Streaming was used - don't play full response again
                    Console.WriteLine($"[TTS] Skipping full response fallback - streaming was used ({timeSinceStreaming:F0}ms ago)");
                    return;
                }
                
                // Streaming was never used (non-streaming path or very old response)
                if (!TtsService.IsStreamingPlaybackActive)
                {
                    var ttsEnabled = false;
                    try { ttsEnabled = Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { }
                    if (!ttsEnabled) return;

                    var voice = App.SettingsProvider?.Current?.Tts?.Speaker;
                    
                    // Determine routing based on active input mode
                    var speakLocal = _isMicrophoneInputEnabled; // Local mic mode = local speakers
                    var speakDiscord = _isDiscordInputEnabled && Kinectv1.Discord.DiscordNetBotManager.IsInVoiceChannel;
                    var speakWebRtc = _isWebRtcInputEnabled; // Remove direct connection check

                    Console.WriteLine($"[TTS] Using full response (streaming not used), local={speakLocal}, discord={speakDiscord}, webRtc={speakWebRtc}");

                    if (speakLocal)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await TtsService.SpeakWithPreemptionAsync(response, voice); } catch (Exception ex) { Console.WriteLine($"TTS speak error: {ex.Message}"); }
                        });
                    }

                    if (speakDiscord)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await Kinectv1.Discord.DiscordNetBotManager.SendTtsToDiscordAsync(response, voice); } catch (Exception ex) { Console.WriteLine($"Discord TTS error: {ex.Message}"); }
                        });
                    }
                    
                    if (speakWebRtc)
                    {
                        // Remove WebRTC send call for now; direct connection not available
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OnOllamaResponseReceived error: {ex.Message}");
            }
        }
        private void OnOllamaError(string error)
        {
            try
            {
                Console.WriteLine(string.IsNullOrWhiteSpace(error) ? "Ollama error" : error);
            }
            catch { }
        }
        private void OnTtsSpeakingStarted() { }
        private void OnTtsSpeakingFinished() { }
        private void OnTtsError(string error) { }
        private void OnDiscordBotStatusChanged(string status) { }
        private void OnDiscordVoiceMessageReceived(string speaker, String message) { }
        private void OnDiscordBotError(string error) { }
        private void InitializeAudioInputControls()
        {
            try
            {
                var modeJson = App.SettingsProvider?.Current?.App?.InputMode;
                var mode = modeJson.HasValue ? (AudioInMode)modeJson.Value : AudioInMode.LocalMic;

                var sttModelPath = App.SettingsProvider?.Current?.Stt?.ModelPath ?? string.Empty;
                VoiceRecognizer.Start(sttModelPath);

                EnsureWebRtcWiring();

                ApplyAudioMode(mode);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Init audio input controls failed: {ex.Message}");
            }
        }

        private void EnsureWebRtcWiring()
        {
            if (_webRtcTransport != null) return;

            var port = App.SettingsProvider?.Current?.WebRtc?.Port ?? 8787;
            _webRtcTransport = new WebRtcAudioTransport(port);
            _webRtcSttQueue = new DroppingAudioQueue<AudioFrame>(capacity: 100);

            _webRtcTransport.OnLog += s => { try { Console.WriteLine(s); } catch { } };
            _webRtcTransport.OnInboundAudio += OnWebRtcInboundAudio;
            _webRtcTransport.OnStateChanged += state =>
            {
                Console.WriteLine($"[WebRTC] State changed: {state}");
                if (state == VoiceTransportState.Connected)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            var text = this.FindName("WebRtcRmsText") as TextBlock;
                            if (text != null) text.Text = "Connected";
                        }
                        catch { }
                    }));
                }
            };
        }

        private void OnWebRtcInboundAudio(AudioFrame frame)
        {
            if (_isClosing) return;

            // Update UI meter
            try
            {
                float rms = ComputeRmsFromPcm16(frame.Pcm16);
                _latestWebRtcRmsValue = rms;
                _lastWebRtcRmsTime = DateTime.UtcNow;
            }
            catch { }

            // Only feed STT when WebRTC mode is active
            if (!_isWebRtcInputEnabled) return;

            _webRtcSttQueue?.Enqueue(frame);
        }

        private static float ComputeRmsFromPcm16(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return 0f;
            double sumSq = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                double n = pcm[i] / 32768.0;
                sumSq += n * n;
            }
            return (float)(Math.Sqrt(sumSq / pcm.Length) * 10000.0);
        }

        private void EnsureWebRtcStartedFromSettings()
        {
            try
            {
                var cfg = App.SettingsProvider?.Current?.WebRtc;
                if (cfg == null || !cfg.Enabled) return;

                if (_webRtcCts != null && !_webRtcCts.IsCancellationRequested) return;

                EnsureWebRtcWiring();

                _webRtcCts = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _webRtcTransport.StartAsync(_webRtcCts.Token);
                        Console.WriteLine($"[WebRTC] Started. Join URL: {_webRtcTransport.GetJoinUrl()}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WebRTC] StartAsync failed: {ex.Message}");
                        try { await StopWebRtcAsync(); } catch { }
                    }
                });

                _webRtcSttTask = Task.Run(() => WebRtcSttWorker(_webRtcCts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WebRTC] start failed: " + ex.Message);
            }
        }

        private async Task StopWebRtcAsync()
        {
            try
            {
                var cts = _webRtcCts;
                _webRtcCts = null;
                if (cts != null)
                {
                    try { cts.Cancel(); } catch { }
                    try { cts.Dispose(); } catch { }
                }

                var t = _webRtcSttTask;
                _webRtcSttTask = null;
                if (t != null) { try { await Task.WhenAny(t, Task.Delay(1000)); } catch { } }

                if (_webRtcTransport != null)
                {
                    await _webRtcTransport.StopAsync();
                }
            }
            catch { }
        }

        private void WebRtcSttWorker(CancellationToken ct)
        {
            int frames = 0;
            DateTime lastLog = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                if (_webRtcSttQueue == null || !_webRtcSttQueue.TryDequeue(out var frame))
                {
                    Thread.Sleep(2);
                    continue;
                }

                try
                {
                    frames++;

                    var pcm = frame.Pcm16;
                    var sr = frame.SampleRate;
                    var ch = frame.Channels;

                    // Resample to 16k mono for Vosk
                    byte[] pcm16k;
                    if (sr == 16000 && ch == 1)
                    {
                        pcm16k = ShortsToBytes(pcm);
                    }
                    else if (sr == 8000 && ch == 1)
                    {
                        // Upsample 8k to 16k (simple interpolation)
                        var upsampled = Upsample8kTo16k(pcm);
                        pcm16k = ShortsToBytes(upsampled);
                    }
                    else
                    {
                        // Convert to float and resample
                        var floats = new float[pcm.Length];
                        for (int i = 0; i < pcm.Length; i++) floats[i] = pcm[i] / 32768.0f;

                        float[] res;
                        if (sr == 48000)
                        {
                            if (ch == 1) res = AudioUtils.ResampleMono48kTo16k(floats, floats.Length, "webrtc");
                            else res = AudioUtils.ResampleStereo48kTo16kMono(floats, floats.Length, "webrtc");
                        }
                        else
                        {
                            res = Array.Empty<float>();
                        }

                        pcm16k = FloatsToPcm16Bytes(res);
                    }

                    if (pcm16k != null && pcm16k.Length > 0)
                    {
                        VoiceRecognizer.ProcessExternalAudio(pcm16k, pcm16k.Length, $"webrtc:{frame.SourceId}");
                    }
                }
                catch { }

                var now = DateTime.UtcNow;
                if ((now - lastLog).TotalSeconds >= 1)
                {
                    try
                    {
                        var (depth, dropped, totalDropped, depthMs) = _webRtcSttQueue?.GetStats(16000, 320) ?? (0, 0, 0, 0);
                        Console.WriteLine($"[WebRTC][stt] fps={frames} q={depth} ({depthMs:F0}ms) dropped={dropped}");
                    }
                    catch { }
                    frames = 0;
                    lastLog = now;
                }
            }
        }

        private static short[] Upsample8kTo16k(short[] input)
        {
            if (input == null || input.Length == 0) return Array.Empty<short>();
            var output = new short[input.Length * 2];
            for (int i = 0; i < input.Length; i++)
            {
                output[i * 2] = input[i];
                if (i < input.Length - 1)
                    output[i * 2 + 1] = (short)((input[i] + input[i + 1]) / 2);
                else
                    output[i * 2 + 1] = input[i];
            }
            return output;
        }

        private static byte[] ShortsToBytes(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return Array.Empty<byte>();
            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] FloatsToPcm16Bytes(float[] floats)
        {
            if (floats == null || floats.Length == 0) return Array.Empty<byte>();
            var bytes = new byte[floats.Length * 2];
            for (int i = 0; i < floats.Length; i++)
            {
                float f = floats[i];
                if (f > 1f) f = 1f;
                if (f < -1f) f = -1f;
                short s = (short)(f * 32767.0f);
                bytes[i * 2] = (byte)(s & 0xFF);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return bytes;
        }

        private void ApplyAudioMode(AudioInMode mode)
        {
            _updatingAudioMode = true;
            try
            {
                _currentAudioMode = mode;
                _isMicrophoneInputEnabled = (mode == AudioInMode.LocalMic);
                _isDiscordInputEnabled = (mode == AudioInMode.DiscordVoice);
                _isWebRtcInputEnabled = (mode == AudioInMode.WebRtcVoice);

                // Reflect in UI (single-selection behavior)
                if (MicInputEnabledCheckBox != null)
                    MicInputEnabledCheckBox.IsChecked = _isMicrophoneInputEnabled;
                if (DiscordInputEnabledCheckBox != null)
                    DiscordInputEnabledCheckBox.IsChecked = _isDiscordInputEnabled;
                try
                {
                    var webRtcCb = this.FindName("WebRtcInputEnabledCheckBox") as CheckBox;
                    if (webRtcCb != null) webRtcCb.IsChecked = _isWebRtcInputEnabled;
                }
                catch { }

                // Enforce disconnect-on-switch policy
                if (_isDiscordInputEnabled)
                {
                    // HARD DISABLE mic capture in Discord mode to prevent local barge-in triggers
                    try { VoiceRecognizer.SetMicrophoneInputEnabled(false); } catch { }
                    try { VoiceRecognizer.SetMumbleInputEnabled(false); } catch { }
                    try { TtsService.CancelCurrentLocalTts(); } catch { }
                    _ = StopWebRtcAsync();
                    Console.WriteLine("[AudioMode] Mic forcibly disabled (Discord mode)");
                }
                else if (_isWebRtcInputEnabled)
                {
                    // Disconnect Discord
                    _ = Task.Run(async () => { try { await DiscordNetBotManager.LeaveAllVoiceAsync(); } catch { } });

                    // Disable mic and discord input
                    try { VoiceRecognizer.SetMicrophoneInputEnabled(false); } catch { }
                    try { VoiceRecognizer.SetDiscordInputEnabled(false); } catch { }
                    try { VoiceRecognizer.SetMumbleInputEnabled(true); } catch { }

                    // Enable WebRTC in settings
                    try
                    {
                        var svc = App.SettingsProvider; var curr = svc?.Current;
                        if (svc != null && curr != null)
                        {
                            var nextWebRtc = new WebRtcSettings(Enabled: true, Port: curr.WebRtc.Port);
                            var next = curr with { WebRtc = nextWebRtc };
                            svc.Save(next);
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Persist WebRTC enable failed: {ex.Message}"); }

                    // Start WebRTC
                    Console.WriteLine("[WebRTC] Auto-start on mode select");
                    EnsureWebRtcStartedFromSettings();
                }
                else
                {
                    // Mic mode: disconnect remote sources
                    _ = Task.Run(async () => { try { await DiscordNetBotManager.LeaveAllVoiceAsync(); } catch { } });
                    _ = StopWebRtcAsync();
                }

                // Apply to recognizer
                if (!_isDiscordInputEnabled && !_isWebRtcInputEnabled)
                {
                    try { VoiceRecognizer.SetMicrophoneInputEnabled(_isMicrophoneInputEnabled); } catch { }
                }
                try { VoiceRecognizer.SetDiscordInputEnabled(_isDiscordInputEnabled); } catch { }
            }
            finally
            {
                _updatingAudioMode = false;
            }
        }

        // Text input to AI handlers
        private void TextInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(TextInputBox?.Text))
            {
                SendTextToAi();
                e.Handled = true;
            }
        }

        private void SendTextButton_Click(object sender, RoutedEventArgs e)
        {
            SendTextToAi();
        }

        private void SendTextToAi()
        {
            try
            {
                var text = TextInputBox?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return;

                // Clear input box immediately
                if (TextInputBox != null)
                    TextInputBox.Text = string.Empty;

                // Barge-in: Cancel any ongoing TTS playback
                try
                {
                    var bargeInEnabled = App.SettingsProvider?.Current?.Asr?.BargeInEnabled ?? true;
                    if (bargeInEnabled && TtsPlaybackController.HasActiveUtterance())
                    {
                        Console.WriteLine("[TextInput] Barge-in: Cancelling current TTS");
                        TtsPlaybackController.CancelCurrent();
                        try { Discord.DiscordNetBotManager.CancelCurrentTts(); } catch { }
                        try { Tts.TtsService.MarkExternalCancel(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TextInput] Barge-in cancel error: {ex.Message}");
                }

                // Determine speaker
                string speaker = "User";
                try
                {
                    var cfg = App.SettingsProvider?.Current?.Ollama;
                    if (cfg != null && cfg.ForceSpeakerOverrideEnabled && !string.IsNullOrWhiteSpace(cfg.ForcedSpeakerId))
                    {
                        speaker = cfg.ForcedSpeakerId.Trim();
                    }
                }
                catch { }

                // Update UI to show we're processing
                if (OllamaStatusText != null)
                    OllamaStatusText.Text = "🤖 Ollama: Processing...";

                // Show the dispatched speaker
                try { ShowSpeakerResolvedForOllama(speaker, 1.0f, "text"); } catch { }

                // Fire-and-forget on background thread to prevent UI freeze
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await OllamaService.DispatchAsync(speaker, text);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"SendTextToAi dispatch error: {ex.Message}");
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (OllamaStatusText != null)
                                OllamaStatusText.Text = $"🤖 Ollama: Error - {ex.Message}";
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SendTextToAi error: {ex.Message}");
                if (OllamaStatusText != null)
                    OllamaStatusText.Text = $"🤖 Ollama: Error - {ex.Message}";
            }
        }

        // Hook to final text only; ignore partials in UI
        private void WireTranscriptionEvents()
        {
            try
            {
                VoiceRecognizer.OnTranscription -= OnFinalTranscription;
                VoiceRecognizer.OnPartialTranscription -= OnPartialTranscription;
            }
            catch { }

            VoiceRecognizer.OnTranscription += OnFinalTranscription;
            VoiceRecognizer.OnPartialTranscription += OnPartialTranscription; // no-op handler
            VoiceRecognizer.OnSpeakerResolvedForOllama -= ShowSpeakerResolvedForOllama;
            VoiceRecognizer.OnSpeakerResolvedForOllama += ShowSpeakerResolvedForOllama;
        }

        private void OnPartialTranscription(string text)
        {
            // Intentionally ignore to avoid "as spoken" UI updates
        }

        private void OnFinalTranscription(string text)
        {
            if (_isClosing) return;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return;
                    if (TranscriptionLabel != null)
                        TranscriptionLabel.Content = text;
                });
            }
            catch { }
        }

        // Call this after InitializeComponent in ctor
        private void WireDispatchPipeline()
        {
            try
            {
                VoiceRecognizer.OnTranscription -= HandleFinalToLlm;
                VoiceRecognizer.OnVoiceEmbedding -= OnVoiceEmbedding;
            }
            catch { }
            VoiceRecognizer.OnTranscription += HandleFinalToLlm;
            VoiceRecognizer.OnVoiceEmbedding += OnVoiceEmbedding;
        }

        private void HandleFinalToLlm(string text)
        {
            // Fire-and-forget on background thread to prevent UI freeze
            _ = Task.Run(async () =>
            {
                try
                {
                    string speaker = "UnknownSpeaker";
                    float score = 0f;
                    if (_lastVoiceEmbedding != null && _lastVoiceEmbedding.Length > 0)
                    {
                        var pair = SpeakerIdentifier.IdentifyFromEmbedding(_lastVoiceEmbedding);
                        speaker = pair.name; score = pair.score;
                    }
                    else
                    {
                        var hint = SpeakerIdentifier.Identify();
                        speaker = hint.name; score = hint.score;
                    }

                    // Apply forced speaker override if enabled
                    try
                    {
                        var cfg = App.SettingsProvider?.Current?.Ollama;
                        if (cfg != null && cfg.ForceSpeakerOverrideEnabled && !string.IsNullOrWhiteSpace(cfg.ForcedSpeakerId))
                        {
                            speaker = cfg.ForcedSpeakerId.Trim();
                            // Indicate override in UI (dispatch to UI thread)
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try { ShowSpeakerResolvedForOllama(speaker, 1.0f, "override"); } catch { }
                            }));
                        }
                        else
                        {
                            // Reflect resolved speaker in UI normally (dispatch to UI thread)
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try { ShowSpeakerResolvedForOllama(speaker, score, "voice"); } catch { }
                            }));
                        }
                    }
                    catch { }

                    await OllamaService.DispatchAsync(speaker, text);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Dispatch pipeline error: {ex.Message}");
                }
            });
        }

        private void PersistAudioMode(AudioInMode mode)
        {
            try
            {
                var svc = App.SettingsProvider;
                var cur = svc?.Current;
                if (svc == null || cur == null) return;

                var updated = cur with { App = cur.App with { InputMode = mode } };
                SettingsService.ValidateOrThrow(updated);
                svc.Save(updated);
            }
            catch { }
        }

        // XAML click handlers that are referenced in MainWindow.xaml
        private void EnrollButton_Click(object sender, RoutedEventArgs e) { }
        private void EnrollVoiceButton_Click(object sender, RoutedEventArgs e) { }
        private void CancelVoiceButton_Click(object sender, RoutedEventArgs e) { }
        private void ShowVideoButton_Click(object sender, RoutedEventArgs e) { }
        private void ListSpeakersButton_Click(object sender, RoutedEventArgs e) { }
        private void FlushVoiceButton_Click(object sender, RoutedEventArgs e) { }
        private void ToggleOllamaButton_Click(object sender, RoutedEventArgs e) { }
        private void OllamaModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void RefreshModelsButton_Click(object sender, RoutedEventArgs e) { }
        private void ToggleTtsButton_Click(object sender, RoutedEventArgs e) { }
        private void TestTtsButton_Click(object sender, RoutedEventArgs e) { }
        private void RefreshTtsModelsButton_Click(object sender, RoutedEventArgs e) { }
    }
}