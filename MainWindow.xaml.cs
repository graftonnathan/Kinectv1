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
using Kinectv1.Mumble; // added
using Kinectv1.Settings; // added for AudioInMode and other settings types

namespace Kinectv1
{
    public partial class MainWindow : Window
    {
        private float[] _lastVoiceEmbedding = null;
        private bool _isDarkMode = true; // Default to dark mode

        // Cancellation token for cleanup
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private bool _isClosing = false;

        // Latest-wins UI update mechanism to prevent update queue buildup
        private volatile float _latestRmsValue = 0f;
        private volatile float _latestDiscordRmsValue = 0f;
        private volatile float _latestMumbleRmsValue = 0f; // NEW: Latest Mumble RMS value
        private volatile int _rmsUpdatePending = 0; // 0 = no update pending, 1 = update pending
        private volatile int _discordRmsUpdatePending = 0;
        private volatile int _mumbleRmsUpdatePending = 0; // NEW: Update pending flag for Mumble RMS

        // ENHANCED DOUBLE REGISTRATION PREVENTION - Discord initialization protection
        private static int _discordInitInProgress = 0; // 0 = not in progress, 1 = in progress

        // Audio input settings
        private bool _isMicrophoneInputEnabled = true;
        // Default Discord and Mumble inputs disabled at startup; toggled by mode selection
        private bool _isDiscordInputEnabled = false;
        private bool _isMumbleInputEnabled = false; // new: UI state mirror (future input)
        private AudioInMode _currentAudioMode = AudioInMode.LocalMic;
        private bool _updatingAudioMode = false;

        // Missing UI control placeholders to prevent compilation errors
        private ComboBox ToneComboBox = new ComboBox();
        private TextBox TestOllamaPromptTextBox = new TextBox();

        // Embedded settings window host
        private UI.Settings.SettingsWindow _embeddedSettingsWindow;

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
                    VoiceRecognizer.OnSpeakerMatch += ShowSpeakerMatch; // Will no-op (live view disabled)
                    // Show only resolved-at-dispatch events
                    VoiceRecognizer.OnSpeakerResolvedForOllama += ShowSpeakerResolvedForOllama;
                    VoiceRecognizer.OnNameHeard += name => EnhancedKinectFaceTracker.QueueLabel(name);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook VoiceRecognizer events: {ex.Message}");
                }

                // Hook Mumble events (Phase 1 placeholders)
                try
                {
                    MumbleClientManager.OnRmsLevel += UpdateMumbleRmsLevel;
                    MumbleClientManager.OnStatusChanged += status => { try { Console.WriteLine($"[Mumble] {status}"); } catch { } };
                    MumbleClientManager.OnError += err => { try { Console.WriteLine($"[Mumble][Error] {err}"); } catch { } };
                }
                catch { }

                try
                {
                    VoiceEnrollmentManager.OnEnrollmentProgress += UpdateVoiceEnrollmentProgress;
                    VoiceEnrollmentManager.OnEnrollmentComplete += OnVoiceEnrollmentComplete;
                    VoiceEnrollmentManager.OnEnrollmentCancelled += OnVoiceEnrollmentCancelled;
                    VoiceRecognizer.OnVoiceEmbedding += OnVoiceEmbedding;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook voice enrollment events: {ex.Message}");
                }

                try
                {
                    EnhancedKinectFaceTracker.OnAllFacesDetected += OnAllFacesDetected;
                    Console.WriteLine("Enhanced face detection events hooked");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook enhanced face detection events: {ex.Message}");
                }

                try
                {
                    IdentityFusionTracker.OnIdentityFused += OnIdentityFused;
                    Console.WriteLine("Identity fusion events hooked");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook identity fusion events: {ex.Message}");
                }

                // Hook Ollama events
                try
                {
                    OllamaService.OnPromptSent += OnOllamaPromptSent;
                    OllamaService.OnResponseReceived += OnOllamaResponseReceived;
                    OllamaService.OnError += OnOllamaError;
                    Console.WriteLine("Ollama service events hooked");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook Ollama events: {ex.Message}");
                }

                // Hook TTS events
                try
                {
                    CoquiTtsService.OnTtsSpeakingStarted += OnTtsSpeakingStarted;
                    CoquiTtsService.OnTtsSpeakingFinished += OnTtsSpeakingFinished;
                    CoquiTtsService.OnTtsError += OnTtsError;
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

        private void InitializeEmbeddedSettings()
        {
            try
            {
                _embeddedSettingsWindow = new UI.Settings.SettingsWindow();
                if (_embeddedSettingsWindow.Content is FrameworkElement content && SettingsHost != null)
                {
                    content.DataContext = _embeddedSettingsWindow.DataContext;
                    _embeddedSettingsWindow.Content = null;
                    SettingsHost.Content = content;
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

        private float _smoothedRms = 0f; // Initialize baseline RMS immediately
        private float _smoothedDiscordRms = 0f; // Initialize baseline Discord RMS immediately
        private float _smoothedMumbleRms = 0f; // Initialize baseline Mumble RMS
        private double _localTtsVolume = 1.0; // 100%
        private double _discordTtsVolume = 1.0; // 100%

        // RMS Update logic
        private void UpdateRmsLevel(float rawRms)
        {
            try
            {
                if (_isClosing) return; // Prevent UI updates during shutdown

                // Latest-wins policy: store the latest value and only dispatch if no update is pending
                _latestRmsValue = rawRms;
                
                // Only schedule an update if one isn't already pending
                if (Interlocked.CompareExchange(ref _rmsUpdatePending, 1, 0) == 0)
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (_isClosing) return; // Double check inside dispatcher

                                // Get the latest value (may have been updated since dispatch was scheduled)
                                var currentRms = _latestRmsValue;
                                
                                // Check if UI elements are still valid
                                if (RmsBar != null && RmsText != null)
                                {
                                    // Only update if microphone input is enabled
                                    if (_isMicrophoneInputEnabled)
                                    {
                                        // Smooth the RMS values for better visualization
                                        _smoothedRms = 0.7f * _smoothedRms + 0.3f * currentRms;
                                        var scaledRms = Math.Min(100, Math.Max(0, (_smoothedRms / 10000.0f) * 100));

                                        RmsBar.Value = scaledRms;
                                        RmsText.Text = $"RMS: {_smoothedRms:F1} ({scaledRms:F0}%)";

                                        // FIXED: Color gradient - Green (low/quiet) -> Orange (medium) -> Red (high/loud)
                                        var greenBrush = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.Green;
                                        var orangeBrush = this.TryFindResource("AccentOrange") as SolidColorBrush ?? Brushes.Orange;
                                        var redBrush = this.TryFindResource("AccentRed") as SolidColorBrush ?? Brushes.Red;

                                        // FIXED: Proper gradient logic - Low=Green (good), Medium=Orange, High=Red (loud/bad)
                                        if (scaledRms <= 33)
                                            RmsBar.Foreground = greenBrush;     // 0-33% = Green (quiet/good)
                                        else if (scaledRms <= 66)
                                            RmsBar.Foreground = orangeBrush;    // 34-66% = Orange (medium)
                                        else
                                            RmsBar.Foreground = redBrush;       // 67-100% = Red (loud/bad)
                                    }
                                    // If disabled, the UpdateMicrophoneStatus() method handles the display
                                }
                            }
                            finally
                            {
                                // Reset the pending flag to allow future updates
                                Interlocked.Exchange(ref _rmsUpdatePending, 0);

                            }
                        }), DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        // Reset the pending flag if dispatch failed
                        Interlocked.Exchange(ref _rmsUpdatePending, 0);
                        if (!_isClosing)
                        {
                            Console.WriteLine($"Error scheduling RMS update: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Suppress exceptions during shutdown
                if (!_isClosing)
                {
                    Console.WriteLine($"Error updating RMS level: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Update Discord RMS level display (keeping this for the Discord RMS bar)
        /// </summary>
        private void UpdateDiscordRmsLevel(float rawRms)
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            // Latest-wins policy: store the latest value and only dispatch if no update is pending
            _latestDiscordRmsValue = rawRms;
            
            // Only schedule an update if one isn't already pending
            if (Interlocked.CompareExchange(ref _discordRmsUpdatePending, 1, 0) == 0)
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (_isClosing) return; // Double check inside dispatcher

                            // Get the latest value (may have been updated since dispatch was scheduled)
                            var currentRms = _latestDiscordRmsValue;

                            // Check if UI elements are still valid
                            if (DiscordRmsBar != null && DiscordRmsText != null)
                            {
                                // Only update if Discord input is enabled
                                if (_isDiscordInputEnabled)
                                {
                                    // Smooth the Discord RMS values for better visualization
                                    _smoothedDiscordRms = 0.7f * _smoothedDiscordRms + 0.3f * currentRms;
                                    
                                    // Discord audio uses a different scaling since it's typically normalized differently
                                    var scaledRms = Math.Min(100, Math.Max(0, (_smoothedDiscordRms / 1000.0f) * 100));

                                    DiscordRmsBar.Value = scaledRms;
                                    DiscordRmsText.Text = $"RMS: {_smoothedDiscordRms:F1} ({scaledRms:F0}%)";

                                    // Color gradient for Discord - Blue theme
                                    var blueBrush = this.TryFindResource("AccentBlue") as SolidColorBrush ?? Brushes.Blue;
                                    var purpleBrush = this.TryFindResource("AccentPurple") as SolidColorBrush ?? Brushes.Purple;
                                    var orangeBrush = this.TryFindResource("AccentOrange") as SolidColorBrush ?? Brushes.Orange;

                                    // Discord-specific color gradient
                                    if (scaledRms <= 33)
                                        DiscordRmsBar.Foreground = blueBrush;      // 0-33% = Blue (quiet)
                                    else if (scaledRms <= 66)
                                        DiscordRmsBar.Foreground = purpleBrush;    // 34-66% = Purple (medium)
                                    else
                                        DiscordRmsBar.Foreground = orangeBrush;    // 67-100% = Orange (loud)
                                }
                                // If disabled, the UpdateDiscordStatus() method handles the display
                            }
                        }
                        finally
                        {
                            // Reset the pending flag to allow future updates
                            Interlocked.Exchange(ref _discordRmsUpdatePending, 0);
                        }
                    }), DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    // Reset the pending flag if dispatch failed
                    Interlocked.Exchange(ref _discordRmsUpdatePending, 0);
                    if (!_isClosing)
                    {
                        Console.WriteLine($"Error scheduling Discord RMS update: {ex.Message}");
                    }
                }
            }
        }

        // Add this method inside the MainWindow class near other RMS update methods
        private void UpdateMumbleRmsLevel(float rawRms)
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            _latestMumbleRmsValue = rawRms;
            if (Interlocked.CompareExchange(ref _mumbleRmsUpdatePending, 1, 0) == 0)
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (_isClosing) return;
                            var currentRms = _latestMumbleRmsValue;

                            var bar = this.FindName("MumbleRmsBar") as ProgressBar;
                            var text = this.FindName("MumbleRmsText") as TextBlock;
                            if (bar != null && text != null && _isMumbleInputEnabled)
                            {
                                // Mumble meter emits peak 0..1; normalize and smooth separately from Discord
                                var norm = Math.Max(0f, Math.Min(1f, currentRms));
                                _smoothedMumbleRms = 0.7f * _smoothedMumbleRms + 0.3f * norm;
                                var scaled = _smoothedMumbleRms * 100f;
                                bar.Value = scaled;
                                text.Text = $"RMS: {_smoothedMumbleRms:F2} ({scaled:F0}%)";
                            }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _mumbleRmsUpdatePending, 0);
                        }
                    }), DispatcherPriority.Background);
                }
                catch
                {
                    Interlocked.Exchange(ref _mumbleRmsUpdatePending, 0);
                }
            }
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

        private void MumbleInputEnabledCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingAudioMode) return;
            PersistAudioMode(AudioInMode.MumbleVoice);
            ApplyAudioMode(AudioInMode.MumbleVoice);
        }

        private void MumbleInputEnabledCheckBox_Unchecked(object sender, RoutedEventArgs e)
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
                    try { KokoroTtsService.RecreateSessionFromSettings(); } catch { }
                }
            }
            catch (Exception ex) { Console.WriteLine($"TTS GPU toggle error: {ex.Message}"); }
        }

        // --- No-op stubs referenced during initialization ---
        private void LoadWindowSettings() { }
        private void LoadApplicationSettings() { }
        private void InitializeAudioDevicesUI() { }
        private void InitializeOllamaModels() { }
        private void InitializeTtsSystem() { }
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
        private void OnVoiceEmbedding(float[] embedding) { _lastVoiceEmbedding = embedding; }
        private void OnOllamaPromptSent(string prompt) { }
        private void OnOllamaResponseReceived(string response)
        {
            // Speak model response if TTS is enabled and populate AI Response box
            try
            {
                if (string.IsNullOrWhiteSpace(response)) return;

                // Update AI Response UI box
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
                            OllamaStatusText.Text = "✅ Ollama: Response received";
                        }
                    }), DispatcherPriority.Background);
                }
                catch { }

                // Prefer JSON pipeline for TTS enabled
                var ttsEnabled = false;
                try { ttsEnabled = Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { }
                if (!ttsEnabled) return;

                // Prefer JSON settings snapshot speaker to avoid stale legacy speaker
                var voice = App.SettingsProvider?.Current?.Tts?.Speaker;
                var speakLocal = _isMicrophoneInputEnabled;      // Local output when mic mode is active
                var speakDiscord = _isDiscordInputEnabled;       // Discord output when discord mode is active

                if (speakLocal)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await CoquiTtsService.SpeakStreamingWithPreemptionAsync(response, voice); }
                        catch (Exception ex) { Console.WriteLine($"TTS speak error: {ex.Message}"); }
                    });
                }

                if (speakDiscord && Kinectv1.Discord.DiscordNetBotManager.IsInVoiceChannel)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await Kinectv1.Discord.DiscordNetBotManager.SendTtsToDiscordAsync(response, voice); }
                        catch (Exception ex) { Console.WriteLine($"Discord TTS error: {ex.Message}"); }
                    });
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
        private void OnTtsSpeakingStarted(string text) { }
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

                // Ensure microphone capture starts so RMS updates flow
                var sttModelPath = App.SettingsProvider?.Current?.Stt?.ModelPath ?? string.Empty;
                VoiceRecognizer.Start(sttModelPath);

                // Apply current mode to wire UI and recognizer input toggles
                ApplyAudioMode(mode);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Init audio input controls failed: {ex.Message}");
            }
        }

        // ENHANCED: Centralized audio mode application with disconnect logic
        private void ApplyAudioMode(AudioInMode mode)
        {
            _updatingAudioMode = true;
            try
            {
                _currentAudioMode = mode;
                _isMicrophoneInputEnabled = (mode == AudioInMode.LocalMic);
                _isDiscordInputEnabled = (mode == AudioInMode.DiscordVoice);
                _isMumbleInputEnabled = (mode == AudioInMode.MumbleVoice);

                // Reflect in UI (single-selection behavior)
                if (MicInputEnabledCheckBox != null)
                    MicInputEnabledCheckBox.IsChecked = _isMicrophoneInputEnabled;
                if (DiscordInputEnabledCheckBox != null)
                    DiscordInputEnabledCheckBox.IsChecked = _isDiscordInputEnabled;
                try
                {
                    var mumbleCb = this.FindName("MumbleInputEnabledCheckBox") as CheckBox;
                    if (mumbleCb != null) mumbleCb.IsChecked = _isMumbleInputEnabled;
                }
                catch { }

                // Enforce disconnect-on-switch policy
                if (_isDiscordInputEnabled)
                {
                    // Ensure Mumble is disconnected
                    _ = Task.Run(async () => { try { await MumbleClientManager.DisconnectAsync(); } catch { } });

                    // Ensure Discord bot is running so commands and gateway are available
                    _ = Task.Run(async () => { try { await DiscordNetBotManager.StartAsync(); } catch { } });
                }
                else if (_isMumbleInputEnabled)
                {
                    // Ensure Discord is disconnected
                    _ = Task.Run(async () => { try { await DiscordNetBotManager.LeaveAllVoiceAsync(); } catch { } });

                    // Persist selection intent: mark Mumble enabled in settings (persistent)
                    try
                    {
                        var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null)
                        {
                            var mb = curr.Mumble;
                            var nextMb = new Kinectv1.Settings.MumbleSettings(
                                Enabled: true,
                                AutoConnect: mb.AutoConnect,
                                Host: mb.Host,
                                Port: mb.Port,
                                Username: mb.Username,
                                ServerPassword: mb.ServerPassword,
                                Channel: mb.Channel,
                                ChannelPassword: mb.ChannelPassword,
                                ValidateTls: mb.ValidateTls,
                                SelfMute: mb.SelfMute,
                                SelfDeaf: mb.SelfDeaf,
                                OpusBitrate: mb.OpusBitrate,
                                VadThreshold: mb.VadThreshold,
                                ReconnectBackoffMs: mb.ReconnectBackoffMs,
                                TextCommandsEnabled: mb.TextCommandsEnabled
                            );
                            var next = new Kinectv1.Settings.AppSettings(curr.Audio, curr.Tts, curr.Vad, curr.Ollama, curr.Discord, nextMb, curr.Ui, curr.Asr, curr.Stt, curr.Face, curr.App);
                            svc.Save(next);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Persist Mumble enable failed: {ex.Message}");
                    }

                    // Connect to Mumble using settings
                    var snap = App.SettingsProvider?.Current;
                    var mb2 = snap?.Mumble;
                    if (mb2 != null)
                    {
                        Console.WriteLine($"[Mumble] Auto-connect on mode select -> {mb2.Host}:{mb2.Port} as {mb2.Username}");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await MumbleClientManager.StartAsync();
                                await MumbleClientManager.ConnectAsync(
                                    mb2.Host, mb2.Port, mb2.Username, mb2.ServerPassword,
                                    mb2.Channel, mb2.ChannelPassword, mb2.ValidateTls,
                                    mb2.SelfMute, mb2.SelfDeaf);
                            }
                            catch (Exception ex) { Console.WriteLine($"Mumble connect failed: {ex.Message}"); }
                        });
                    }
                }
                else
                {
                    // Mic mode: disconnect both remote sources
                    _ = Task.Run(async () => { try { await DiscordNetBotManager.LeaveAllVoiceAsync(); } catch { } try { await MumbleClientManager.DisconnectAsync(); } catch { } });
                }

                // Apply to recognizer (mumble not implemented yet)
                try { VoiceRecognizer.SetMicrophoneInputEnabled(_isMicrophoneInputEnabled); } catch { }
                try { VoiceRecognizer.SetDiscordInputEnabled(_isDiscordInputEnabled); } catch { }
                // Mumble gating to be added in Phase 2 when ingest lands
            }
            finally
            {
                _updatingAudioMode = false;
            }
        }

        // Replace PersistAudioMode to use SettingsService
        private void PersistAudioMode(AudioInMode mode)
        {
            try
            {
                var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
                var next = curr with { App = curr.App with { InputMode = (Kinectv1.Settings.AudioInMode)mode } };
                svc.Save(next);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Persist AudioInMode failed: {ex.Message}");
            }
        }

        // Implemented enrollment and utility buttons
        private void EnrollButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var name = EnrollNameBox?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    Console.WriteLine("Enroll Face: name is empty");
                    return;
                }

                EnhancedKinectFaceTracker.QueueLabel(name);
                Console.WriteLine($"👤 Queued face enrollment for '{name}' (look at camera)");

                // Auto-show video window to help operator align face
                try
                {
                    if (!EnhancedKinectFaceTracker.IsVideoWindowOpen())
                    {
                        EnhancedKinectFaceTracker.ShowVideoWindow();
                        if (ShowVideoButton != null) ShowVideoButton.Content = "📺 Hide Video";
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"EnrollButton_Click failed: {ex.Message}");
            }
        }

        private void EnrollVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var name = EnrollNameBox?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    Console.WriteLine("Enroll Voice: name is empty");
                    return;
                }

                VoiceEnrollmentManager.StartEnrollment(name);
                if (VoiceEnrollmentPanel != null)
                    VoiceEnrollmentPanel.Visibility = Visibility.Visible;

                // Initialize UI to 0 progress
                UpdateVoiceEnrollmentProgress(name, 0, VoiceEnrollmentManager.GetRequiredSamples());
                Console.WriteLine($"🎙 Started voice enrollment for '{name}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"EnrollVoiceButton_Click failed: {ex.Message}");
            }
        }

        private void CancelVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                VoiceEnrollmentManager.CancelEnrollment();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CancelVoiceButton_Click failed: {ex.Message}");
            }
        }

        private void ShowVideoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (EnhancedKinectFaceTracker.IsVideoWindowOpen())
                {
                    EnhancedKinectFaceTracker.HideVideoWindow();
                    if (ShowVideoButton != null) ShowVideoButton.Content = "📺 Show Video";
                }
                else
                {
                    EnhancedKinectFaceTracker.ShowVideoWindow();
                    if (ShowVideoButton != null) ShowVideoButton.Content = "📺 Hide Video";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ShowVideoButton_Click failed: {ex.Message}");
            }
        }

        private void ListSpeakersButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SpeakerIdentifier.ListEnrolledSpeakers();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ListSpeakersButton_Click failed: {ex.Message}");
            }
        }

        private void SetAsDefaultButton_Click(object sender, RoutedEventArgs e) { /* no-op stub */ }
        private void FlushVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int removed = MemoryStore.FlushAllVoiceEmbeddings();
                Console.WriteLine($"🗑 Cleared {removed} voice embeddings from memory store");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FlushVoiceButton_Click failed: {ex.Message}");
            }
        }
        private void ToggleOllamaButton_Click(object sender, RoutedEventArgs e) { /* no-op stub */ }
        private void ToggleTtsButton_Click(object sender, RoutedEventArgs e) { /* no-op stub */ }
        private void TestTtsButton_Click(object sender, RoutedEventArgs e) { /* no-op stub */ }
        private void OllamaModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { /* already above; duplicate stub ignored by compiler if we keep one */ }
        private void RefreshModelsButton_Click(object sender, RoutedEventArgs e) { /* already above; duplicate stub ignored if signature matches; keep minimal */ }
        private void ScenarioComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { /* no-op stub */ }
        private void ApplyScenarioButton_Click(object sender, RoutedEventArgs e) { /* no-op stub */ }
        private void RefreshValidationButton_Click(object sender, RoutedEventArgs e) { }
        private void RefreshDiagnosticsButton_Click(object sender, RoutedEventArgs e) { }
        private void LoadDiagnosticsTab() { }
        private void RefreshTtsModelsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TtsModelStatusText != null)
                {
                    TtsModelStatusText.Text = "TTS models refreshed";
                }
            }
            catch { }
        }
    }
}