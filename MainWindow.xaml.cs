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

namespace Kinectv1
{
    public partial class MainWindow : Window
    {
        private float[] _lastVoiceEmbedding = null;
        private float _currentThreshold = 0.40f; // Default threshold
        private bool _isDarkMode = true; // Default to dark mode

        // Cancellation token for cleanup
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private bool _isClosing = false;

        // Latest-wins UI update mechanism to prevent update queue buildup
        private volatile float _latestRmsValue = 0f;
        private volatile float _latestDiscordRmsValue = 0f;
        private volatile int _rmsUpdatePending = 0; // 0 = no update pending, 1 = update pending
        private volatile int _discordRmsUpdatePending = 0;

        // ENHANCED DOUBLE REGISTRATION PREVENTION - Discord initialization protection
        private static int _discordInitInProgress = 0; // 0 = not in progress, 1 = in progress

        // Audio input settings
        private bool _isMicrophoneInputEnabled = true;
        private bool _isDiscordInputEnabled = true;

        // Missing UI control placeholders to prevent compilation errors
        private ComboBox ToneComboBox = new ComboBox();
        private TextBox TestOllamaPromptTextBox = new TextBox();

        public MainWindow()
        {
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
                            Console.WriteLine("🧪 Testing Hosted Services Manager (Ctrl+H pressed)");
                            Task.Run(async () =>
                            {
                                await HostedServicesTest.RunAllTests();
                            });
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
                            Console.WriteLine("🧪 Testing Shutdown Lifecycle (Ctrl+S pressed)");
                            Task.Run(async () =>
                            {
                                await ShutdownLifecycleTest.RunAllTests();
                            });
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
                    VoiceRecognizer.OnSpeakerMatch += ShowSpeakerMatch;
                    VoiceRecognizer.OnNameHeard += name => EnhancedKinectFaceTracker.QueueLabel(name);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook VoiceRecognizer events: {ex.Message}");
                }

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

                // Load and apply saved settings
                LoadApplicationSettings();

                // Initialize audio input control states
                InitializeAudioInputControls();

                // Initialize audio devices UI
                InitializeAudioDevicesUI();

                // Update threshold display
                UpdateThresholdDisplay();

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

                // Initialize diagnostics tab
                LoadDiagnosticsTab();
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
        }

        private void LoadThemeSettings()
        {
            try
            {
                _isDarkMode = AppSettings.LoadDarkMode();
                ApplyTheme(_isDarkMode);
                Console.WriteLine($"🎨 Theme loaded: {(_isDarkMode ? "Dark" : "Light")} mode");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load theme settings: {ex.Message}");
                // Default to dark mode if loading fails
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
                    ThresholdTextBox.Foreground = Brushes.White;
                    TtsTestTextBox.Foreground = Brushes.White;
                    EnrollNameBox.CaretBrush = Brushes.White;
                    ThresholdTextBox.CaretBrush = Brushes.White;
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
                    ThresholdTextBox.Foreground = Brushes.Black;
                    TtsTestTextBox.Foreground = Brushes.Black;
                    EnrollNameBox.CaretBrush = Brushes.Black;
                    ThresholdTextBox.CaretBrush = Brushes.Black;
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
                AppSettings.SaveDarkMode(_isDarkMode);

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

            try
            {
                // Log fusion updates to console for debugging
                Console.WriteLine($"🔀 Identity Fusion TrackingID {trackingId}: {fusedName} (score={fusedScore:F3})");

                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return; // Double check inside dispatcher

                    // Update speaker label with fused identity (replaces ShowSpeakerMatch)
                    if (SpeakerLabel != null)
                    {
                        var content = $"{fusedName} ({fusedScore:F2}) [Fused]";
                        SpeakerLabel.Content = content;

                        // Color coding based on fused confidence using theme-aware colors
                        Brush backgroundBrush;

                        if (fusedName == "Unknown" || fusedScore < 0.3f)
                            backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(101, 68, 68) : Color.FromRgb(255, 192, 192));
                        else if (fusedScore < 0.5f)
                            backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(102, 85, 68) : Color.FromRgb(255, 255, 128));
                        else if (fusedScore < 0.7f)
                            backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(68, 85, 102) : Color.FromRgb(192, 224, 255));
                        else
                            backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(68, 102, 68) : Color.FromRgb(192, 255, 192));

                        SpeakerLabel.Background = backgroundBrush;
                    }
                });
            }
            catch (Exception ex)
            {
                // Suppress exceptions during shutdown
                if (!_isClosing)
                {
                    Console.WriteLine($"Error updating fused identity: {ex.Message}");
                }
            }
        }

        private void UpdateThresholdDisplay()
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return; // Double check inside dispatcher

                    try
                    {
                        var currentThreshold = SpeakerIdentifier.GetDefaultThreshold();
                        if (ThresholdTextBox != null)
                        {
                            ThresholdTextBox.Text = currentThreshold.ToString("0.00");
                            _currentThreshold = currentThreshold;
                        }
                    }
                    catch (Exception ex)
                    {
                        // SpeakerIdentifier might not be initialized yet - use default
                        if (ThresholdTextBox != null)
                        {
                            ThresholdTextBox.Text = _currentThreshold.ToString("0.00");
                        }
                        Console.WriteLine($"UpdateThresholdDisplay warning: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($"UpdateThresholdDisplay failed: {ex.Message}");
                }
            }
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

        // Volume control fields
        private double _localTtsVolume = 1.0; // 100%
        private double _discordTtsVolume = 1.0; // 100%

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

        // Note: UpdateDiscordRmsLevel method removed since we no longer have separate Discord RMS events
        // Discord audio processing is now handled entirely in DiscordVoiceBot.cs with professional pipeline

        private void ShowSpeakerMatch(string speakerName, float confidence)
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return; // Double check inside dispatcher

                    // Check if UI element is still valid
                    if (SpeakerLabel != null)
                    {
                        var content = $"{speakerName} ({confidence:F2})";
                        SpeakerLabel.Content = content;

                        // Color coding based on confidence using theme-aware colors
                        Brush backgroundBrush;

                        if (speakerName == "Unknown" || confidence < 0.3f)
                            backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(101, 68, 68) : Color.FromRgb(255, 192, 192));
                        else if (confidence < 0.5f)
                            backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(102, 85, 68) : Color.FromRgb(255, 255, 128));
                        else if (confidence < 0.7f)
                            backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(68, 85, 102) : Color.FromRgb(192, 224, 255));
                        else
                            backgroundBrush = new SolidColorBrush(_isDarkMode ? Color.FromRgb(68, 102, 68) : Color.FromRgb(192, 255, 192));

                        SpeakerLabel.Background = backgroundBrush;
                    }
                });
            }
            catch (Exception ex)
            {
                // Suppress exceptions during shutdown
                if (!_isClosing)
                {
                    Console.WriteLine($"Error updating speaker match: {ex.Message}");
                }
            }
        }

        // Button Event Handlers
        private void EnrollButton_Click(object sender, RoutedEventArgs e)
        {
            var name = EnrollNameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Please enter a name before enrolling a face.", "Name Required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                EnrollNameBox.Focus();
                return;
            }

            try
            {
                EnhancedKinectFaceTracker.QueueLabel(name);
                Console.WriteLine($"👤 Face enrollment initiated for '{name}'");
                MessageBox.Show($"Face enrollment started for '{name}'.\nLook at the camera and wait for capture.",
                    "Face Enrollment", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Face enrollment failed: {ex.Message}");
                MessageBox.Show($"Face enrollment failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void EnrollVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            var name = EnrollNameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Please enter a name before enrolling voice.", "Name Required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                EnrollNameBox.Focus();
                return;
            }

            try
            {
                VoiceEnrollmentManager.StartEnrollment(name);
                VoiceEnrollmentPanel.Visibility = Visibility.Visible;
                EnrollVoiceButton.IsEnabled = false;
                Console.WriteLine($"🎙️ Voice enrollment started for '{name}' - 10 samples required");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Voice enrollment failed: {ex.Message}");
                MessageBox.Show($"Voice enrollment failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                VoiceEnrollmentManager.CancelEnrollment();
                VoiceEnrollmentPanel.Visibility = Visibility.Collapsed;
                EnrollVoiceButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Voice enrollment cancellation failed: {ex.Message}");
            }
        }

        private void ShowVideoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnhancedKinectFaceTracker.ShowVideoWindow();
                Console.WriteLine("📹 Video window requested");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to show video window: {ex.Message}");
                MessageBox.Show($"Failed to show video window: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ListSpeakersButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SpeakerIdentifier.ListEnrolledSpeakers();
                Console.WriteLine("🔊 Listed enrolled speakers");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to list speakers: {ex.Message}");
            }
        }

        private void SetAsDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (float.TryParse(ThresholdTextBox.Text, out float threshold))
                {
                    SpeakerIdentifier.SetDefaultThreshold(threshold);
                    _currentThreshold = threshold;

                    // Save the threshold to AppSettings
                    AppSettings.SaveVoiceThreshold(threshold);

                    Console.WriteLine($"🎛️ Voice threshold set to {threshold:F2}");
                    MessageBox.Show($"Voice threshold set to {threshold:F2}", "Threshold Updated",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Please enter a valid threshold value (e.g., 0.40)", "Invalid Value",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    UpdateThresholdDisplay();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set threshold: {ex.Message}");
                MessageBox.Show($"Failed to set threshold: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FlushVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("Are you sure you want to clear all voice data?\nThis action cannot be undone.",
                    "Confirm Clear Voice Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var deletedCount = MemoryStore.FlushAllVoiceEmbeddings();
                    Console.WriteLine($"🗑️ Cleared {deletedCount} voice embeddings from all speakers");
                    MessageBox.Show($"Cleared {deletedCount} voice embeddings from all speakers.", "Voice Data Cleared",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clear voice data: {ex.Message}");
                MessageBox.Show($"Failed to clear voice data: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleOllamaButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var isEnabled = OllamaService.IsEnabled();
                if (isEnabled)
                {
                    // SetEnabled will automatically save the state
                    OllamaService.SetEnabled(false);
                    ToggleOllamaButton.Content = "Enable Ollama";
                    ToggleOllamaButton.Background = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.LightGreen;
                    OllamaStatusText.Text = "🤖 Ollama: Disabled";
                    Console.WriteLine("🤖 Ollama service disabled");
                }
                else
                {
                    // SetEnabled will automatically save the state
                    OllamaService.SetEnabled(true);
                    ToggleOllamaButton.Content = "Disable Ollama";
                    ToggleOllamaButton.Background = this.TryFindResource("AccentRed") as SolidColorBrush ?? Brushes.IndianRed;
                    OllamaStatusText.Text = "🤖 Ollama: Connecting...";
                    Console.WriteLine("🤖 Ollama service enabled - refreshing models...");

                    // Auto-refresh models when Ollama is enabled
                    RefreshOllamaModels();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to toggle Ollama: {ex.Message}");
                MessageBox.Show($"Failed to toggle Ollama: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleTtsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var isEnabled = CoquiTtsService.IsEnabled();
                if (isEnabled)
                {
                    CoquiTtsService.SetEnabled(false);
                    ToggleTtsButton.Content = "Enable TTS";
                    ToggleTtsButton.Background = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.LightGreen;
                    TtsStatusText.Text = "🎤 TTS: Disabled";
                    Console.WriteLine("🎤 TTS service disabled");
                }
                else
                {
                    CoquiTtsService.SetEnabled(true);
                    ToggleTtsButton.Content = "Disable TTS";
                    ToggleTtsButton.Background = this.TryFindResource("AccentRed") as SolidColorBrush ?? Brushes.IndianRed;
                    TtsStatusText.Text = "🎤 TTS: Enabled";
                    Console.WriteLine("🎤 TTS service enabled");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to toggle TTS: {ex.Message}");
                MessageBox.Show($"Failed to toggle TTS: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void TestTtsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return; // Prevent new operations during shutdown

            try
            {
                var testText = TtsTestTextBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(testText))
                {
                    testText = "Hello, this is a test of the text to speech system.";
                    TtsTestTextBox.Text = testText;
                }

                // Get the REF ID from the selected speaker (what the model expects)
                string currentSpeakerRefId = GetCurrentTtsSpeakerRefId();

                // Get speaker info for logging
                var selectedItem = TtsSpeakerComboBox.SelectedItem as ComboBoxItem;
                string speakerDisplayText = selectedItem?.Content?.ToString() ?? "Unknown Speaker";

                Console.WriteLine($"🎤 Testing TTS with speaker '{speakerDisplayText}' (REF ID: {currentSpeakerRefId})");
                Console.WriteLine($"🎤 Text: '{testText}'");

                var success = await CoquiTtsService.SpeakAsync(testText, currentSpeakerRefId);

                if (_isClosing) return; // Check again after async operation

                if (!success)
                {
                    MessageBox.Show("TTS test failed. Check console for details.", "TTS Test Failed",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    Console.WriteLine($"✅ TTS test successful with: {speakerDisplayText} (REF ID: {currentSpeakerRefId})");

                    // Show quick confirmation in status
                    var originalText = TtsStatusText.Text;
                    TtsStatusText.Text = $"🎤 TTS: Test successful ({currentSpeakerRefId})";

                    // Reset status after 3 seconds WITHOUT using cancellation token
                    // Use a background task that checks for shutdown instead
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Wait in smaller intervals and check for shutdown
                            for (int i = 0; i < 30; i++) // 30 * 100ms = 3000ms
                            {
                                if (_isClosing) return; // Exit early if closing
                                await Task.Delay(100); // Small delay without cancellation token
                            }

                            if (!_isClosing)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (!_isClosing && TtsStatusText.Text.Contains("Test successful"))
                                    {
                                        TtsStatusText.Text = originalText;
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            // Ignore exceptions in background status reset
                            if (!_isClosing)
                            {
                                Console.WriteLine($"Background status reset error: {ex.Message}");
                            }
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected during shutdown
                Console.WriteLine("🎤 TTS test canceled due to application shutdown");
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($"❌ TTS test failed: {ex.Message}");
                    MessageBox.Show($"TTS test failed: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Load window settings
        /// </summary>
        private void LoadWindowSettings()
        {
            try
            {
                var (width, height, left, top, state) = AppSettings.LoadWindowSettings();

                if (width > 0 && height > 0)
                {
                    this.Width = width;
                    this.Height = height;
                }

                if (!double.IsNaN(left) && !double.IsNaN(top))
                {
                    this.Left = left;
                    this.Top = top;
                }

                if (state == "Maximized")
                    this.WindowState = WindowState.Maximized;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load window settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Load application settings
        /// </summary>
        private void LoadApplicationSettings()
        {
            try
            {
                var threshold = AppSettings.LoadVoiceThreshold();
                _currentThreshold = threshold;
                ThresholdTextBox.Text = threshold.ToString("0.00");
                SpeakerIdentifier.SetDefaultThreshold(threshold);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load application settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize Ollama models dropdown
        /// </summary>
        private void InitializeOllamaModels()
        {
            try
            {
                var defaultModels = new[] { "gemma3:4b", "llama3.2", "gemma2", "phi3", "mistral", "llama3.1", "codellama" };

                // UPDATED: Load the saved model FIRST before populating dropdown
                var savedModel = AppSettings.LoadOllamaModel();

                OllamaModelComboBox.Items.Clear();

                // If we have a saved model that's not in the default list, add it first
                if (!string.IsNullOrEmpty(savedModel) && !defaultModels.Contains(savedModel))
                {
                    OllamaModelComboBox.Items.Add(savedModel);
                    Console.WriteLine($"🤖 Added saved custom model: {savedModel}");
                }

                // Add all default models
                foreach (var model in defaultModels)
                {
                    OllamaModelComboBox.Items.Add(model);
                }

                // Now select the saved model (it will be at index 0 if it was custom, or found in the list)
                bool modelFound = false;
                if (!string.IsNullOrEmpty(savedModel))
                {
                    for (int i = 0; i < OllamaModelComboBox.Items.Count; i++)
                    {
                        if (OllamaModelComboBox.Items[i].ToString() == savedModel)
                        {
                            OllamaModelComboBox.SelectedIndex = i;
                            modelFound = true;
                            Console.WriteLine($"🤖 Restored saved model selection: {savedModel} at index {i}");
                            break;
                        }
                    }
                }

                // If no saved model or model not found, select first item
                if (!modelFound && OllamaModelComboBox.Items.Count > 0)
                {
                    OllamaModelComboBox.SelectedIndex = 0;
                    Console.WriteLine($"🤖 Selected default model: {OllamaModelComboBox.Items[0]}");
                }

                // UPDATED: Load and apply saved enabled state
                var savedEnabled = AppSettings.LoadOllamaEnabled();
                if (savedEnabled)
                {
                    ToggleOllamaButton.Content = "Disable Ollama";
                    ToggleOllamaButton.Background = this.TryFindResource("AccentRed") as SolidColorBrush ?? Brushes.IndianRed;
                    OllamaStatusText.Text = "🤖 Ollama: Enabled";
                    Console.WriteLine($"🤖 Restored Ollama enabled state: {savedEnabled}");
                }
                else
                {
                    ToggleOllamaButton.Content = "Enable Ollama";
                    ToggleOllamaButton.Background = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.LightGreen;
                    OllamaStatusText.Text = "🤖 Ollama: Disabled";
                    Console.WriteLine($"🤖 Restored Ollama enabled state: {savedEnabled}");
                }

                Console.WriteLine($"🤖 Initialized with {OllamaModelComboBox.Items.Count} models, selected: {OllamaModelComboBox.SelectedItem}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize Ollama models: {ex.Message}");
            }
        }

        /// <summary>
        /// Voice enrollment event handlers
        /// </summary>
        private void UpdateVoiceEnrollmentProgress(string name, int current, int total)
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return; // Double check inside dispatcher

                    // Check if UI elements are still valid
                    if (VoiceEnrollProgress != null && VoiceProgressText != null &&
                        VoiceProgressPercent != null && VoiceEnrollStatusText != null)
                    {
                        VoiceEnrollProgress.Value = current;
                        VoiceProgressText.Text = $"{current}/{total}";
                        var percentage = (float)current / total * 100;
                        VoiceProgressPercent.Text = $"({percentage:F0}%)";
                        VoiceEnrollStatusText.Text = $"Enrolling '{name}' - Sample {current} captured";
                    }
                });
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($"Error updating voice enrollment progress: {ex.Message}");
                }
            }
        }

        private void OnVoiceEnrollmentComplete(string name)
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return; // Double check inside dispatcher

                    // Check if UI elements are still valid
                    if (VoiceEnrollmentPanel != null && EnrollVoiceButton != null &&
                        VoiceEnrollProgress != null && VoiceProgressText != null &&
                        VoiceProgressPercent != null && VoiceEnrollStatusText != null)
                    {
                        VoiceEnrollmentPanel.Visibility = Visibility.Collapsed;
                        EnrollVoiceButton.IsEnabled = true;
                        VoiceEnrollProgress.Value = 0;
                        VoiceProgressText.Text = "0/10";
                        VoiceProgressPercent.Text = "(0%)";
                        VoiceEnrollStatusText.Text = "Ready for voice enrollment";

                        MessageBox.Show($"Voice enrollment completed for '{name}'!", "Enrollment Complete",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                });
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($"Error handling voice enrollment completion: {ex.Message}");
                }
            }
        }

        private void OnVoiceEnrollmentCancelled(string name)
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return; // Double check inside dispatcher

                    // Check if UI elements are still valid
                    if (VoiceEnrollmentPanel != null && EnrollVoiceButton != null &&
                        VoiceEnrollProgress != null && VoiceProgressText != null &&
                        VoiceProgressPercent != null && VoiceEnrollStatusText != null)
                    {
                        VoiceEnrollmentPanel.Visibility = Visibility.Collapsed;
                        EnrollVoiceButton.IsEnabled = true;
                        VoiceEnrollProgress.Value = 0;
                        VoiceProgressText.Text = "0/10";
                        VoiceProgressPercent.Text = "(0%)";
                        VoiceEnrollStatusText.Text = "Ready for voice enrollment";
                    }
                });
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($"Error handling voice enrollment cancellation: {ex.Message}");
                }
            }
        }

        private void OnVoiceEmbedding(float[] embedding)
        {
            _lastVoiceEmbedding = embedding;

            if (VoiceEnrollmentManager.IsEnrolling)
            {
                VoiceEnrollmentManager.ProcessVoiceSample(embedding);
            }
        }

        /// <summary>
        /// Ollama event handlers
        /// </summary>
        private void OnOllamaPromptSent(string prompt)
        {
            Dispatcher.Invoke(() =>
            {
                OllamaStatusText.Text = "🤖 Ollama: Processing...";
            });
        }

        private void OnOllamaResponseReceived(string response)
        {
            if (_isClosing) return; // Prevent operations during shutdown

            Dispatcher.Invoke(() =>
            {
                OllamaResponseBox.Text = response;
                OllamaStatusText.Text = "🤖 Ollama: Ready";

                // ENHANCED: Conditional TTS output based on enabled input sources
                if (CoquiTtsService.IsEnabled() && !string.IsNullOrWhiteSpace(response) && !_isClosing)
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            // Get the currently selected TTS speaker
                            string currentSpeakerRefId = null;
                            Dispatcher.Invoke(() =>
                            {
                                currentSpeakerRefId = GetCurrentTtsSpeakerRefId();
                            });

                            Console.WriteLine($"🎤 Speaking Ollama response: '{response}'");
                            Console.WriteLine($"🎤 Using selected TTS speaker: {currentSpeakerRefId}");

                            // CONDITIONAL OUTPUT: Based on which input sources are enabled
                            Task<bool> localTtsTask = Task.FromResult(true);
                            Task<bool> discordTtsTask = Task.FromResult(false);

                            // Only play locally if microphone input is enabled
                            if (_isMicrophoneInputEnabled)
                            {
                                Console.WriteLine($"🔊 Playing TTS locally (microphone input enabled)");
                                localTtsTask = CoquiTtsService.SpeakAsync(response, currentSpeakerRefId);
                            }
                            else
                            {
                                Console.WriteLine($"🔇 Skipping local TTS (microphone input disabled)");
                            }
                            

                            // Only send to Discord if Discord input is enabled AND bot is connected
                            if (_isDiscordInputEnabled && DiscordNetBotManager.IsRunning && DiscordNetBotManager.IsInVoiceChannel)
                            {
                                Console.WriteLine($"🤖 Sending TTS to Discord voice channel (Discord input enabled)");
                                discordTtsTask = DiscordNetBotManager.SendTtsToDiscordAsync(response, currentSpeakerRefId);
                            }
                            else if (!_isDiscordInputEnabled)
                            {
                                Console.WriteLine($"🔇 Skipping Discord TTS (Discord input disabled)");
                            }
                            else
                            {
                                Console.WriteLine($"🔇 Skipping Discord TTS (bot not connected to voice)");
                            }
                            

                            // Wait for enabled outputs to complete
                            await Task.WhenAll(localTtsTask, discordTtsTask);

                            var localOk = await localTtsTask;
                            var discordOk = await discordTtsTask;
                            
                            var outputSummary = "";
                            if (_isMicrophoneInputEnabled && _isDiscordInputEnabled)
                                outputSummary = "both local and Discord output";
                            else if (_isMicrophoneInputEnabled)
                                outputSummary = "local output only";
                            else if (_isDiscordInputEnabled)
                                outputSummary = "Discord output only";
                            else
                                outputSummary = "no output (both inputs disabled)";
                                
                            Console.WriteLine($"✅ TTS completed for {outputSummary}");

                            if (!discordOk && _isDiscordInputEnabled)
                            {
                                Console.WriteLine("❗ Discord TTS failed or did not play. Check voice connection and native libs (opus/libsodium).");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancellation is expected during shutdown
                            Console.WriteLine("🎤 Ollama TTS canceled due to application shutdown");
                        }
                        catch (Exception ex)
                        {
                            if (!_isClosing)
                            {
                                Console.WriteLine($"Error speaking Ollama response: {ex.Message}");
                            }
                        }
                    }, _cancellationTokenSource.Token);
                }
            });
        }

        private void OnOllamaError(string error)
        {
            Dispatcher.Invoke(() =>
            {
                OllamaResponseBox.Text = $"Error: {error}";
                OllamaStatusText.Text = "🤖 Olloma: Error";
            });
        }

        /// <summary>
        /// TTS event handlers
        /// </summary>
        private void OnTtsSpeakingStarted(string text)
        {
            Dispatcher.Invoke(() =>
            {
                TtsStatusText.Text = "🎤 TTS: Speaking...";
                Console.WriteLine($"🎤 TTS started speaking: '{text}'");
            });
        }

        private void OnTtsSpeakingFinished()
        {
            Dispatcher.Invoke(() =>
            {
                TtsStatusText.Text = "🎤 TTS: Ready";
                Console.WriteLine("🎤 TTS finished speaking");
            });
        }

        private void OnTtsError(string error)
        {
            Dispatcher.Invoke(() =>
            {
                TtsStatusText.Text = $"🎤 TTS: Error - {error}";
                Console.WriteLine($"TTS Error: {error}");
                
                // Show centralized error if it contains AppError format
                ShowAppError(error);
            });
        }

        /// <summary>
        /// Centralized error display system for AppError instances
        /// </summary>
        private void ShowAppError(string errorMessage)
        {
            try
            {
                // Simple detection of AppError format (starts with emoji)
                if (errorMessage.StartsWith("⚙️") || errorMessage.StartsWith("📁") || 
                    errorMessage.StartsWith("🎙️") || errorMessage.StartsWith("🌐") ||
                    errorMessage.StartsWith("🖥️") || errorMessage.StartsWith("🗣️") ||
                    errorMessage.StartsWith("🎤") || errorMessage.StartsWith("🤖") ||
                    errorMessage.StartsWith("❓") || errorMessage.StartsWith("❌"))
                {
                    // Display in console for now - could be enhanced with UI toast/banner
                    Console.WriteLine($"🔔 AppError: {errorMessage}");
                    
                    // Optional: Flash window title to indicate error
                    var originalTitle = this.Title;
                    this.Title = $"⚠️ Error - {originalTitle}";
                    
                    // Reset title after 3 seconds
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    timer.Tick += (s, e) =>
                    {
                        this.Title = originalTitle;
                        timer.Stop();
                    };
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ShowAppError: {ex.Message}");
            }
        }

        /// <summary>
        /// Discord bot event handlers
        /// </summary>
        private void OnDiscordBotStatusChanged(string status)
        {
            if (_isClosing) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return;

                    // Update Discord status in UI if we have Discord controls
                    Console.WriteLine($"🤖 Discord Bot Status: {status}");

                    // You can add Discord status UI elements here
                    // For now, just log the status changes
                });
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($"Error handling Discord bot status change: {ex.Message}");
                }
            }
        }

        private void OnDiscordVoiceMessageReceived(string speaker, string message)
        {
            if (_isClosing) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return;

                    // Log Discord voice messages to console only - don't update transcription display
                    Console.WriteLine($"🗣️ Discord Voice: [{speaker}] {message}");

                    // DO NOT update TranscriptionLabel here - let the normal STT flow handle transcription display
                    // The transcription display should only show actual transcribed text from UpdateTranscription()
                    
                    // You can add Discord-specific message handling here if needed (like logging, statistics, etc.)
                    // but don't interfere with the main transcription display
                });
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($"Error handling Discord voice message: {ex.Message}");
                }
            }
        }

        private void OnDiscordBotError(string error)
        {
            if (_isClosing) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return;

                    Console.WriteLine($"❌ Discord Bot Error: {error}");

                    // Show centralized error display
                    ShowAppError(error);

                    // Legacy specific handling for backwards compatibility
                    if (error.Contains("token") || error.Contains("authentication"))
                    {
                        // Critical authentication error - might want to show user notification
                        Console.WriteLine("🔑 Discord authentication error - check bot token");
                    }
                });
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($"Error handling Discord bot error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Refresh Ollama models
        /// </summary>
        private async void RefreshOllamaModels()
        {
            try
            {
                OllamaStatusText.Text = "🤖 Ollama: Loading models...";
                OllamaModelComboBox.IsEnabled = false;

                var connectionTest = await OllamaService.TestConnectionAsync();

                if (connectionTest)
                {
                    var models = await OllamaService.GetAvailableModelsAsync();

                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            // Store the current selection to preserve it
                            var currentSelection = OllamaModelComboBox.SelectedItem?.ToString();

                            OllamaModelComboBox.Items.Clear();

                            if (models != null && models.Length > 0)
                            {
                                foreach (var model in models)
                                {
                                    OllamaModelComboBox.Items.Add(model);
                                }

                                // Try to restore the previous selection
                                bool selectionRestored = false;
                                if (!string.IsNullOrEmpty(currentSelection))
                                {
                                    for (int i = 0; i < OllamaModelComboBox.Items.Count; i++)
                                    {
                                        if (OllamaModelComboBox.Items[i].ToString() == currentSelection)
                                        {
                                            OllamaModelComboBox.SelectedIndex = i;
                                            selectionRestored = true;
                                            Console.WriteLine($"🤖 Restored previous selection: {currentSelection} at index {i}");
                                            break;
                                        }
                                    }
                                }

                                // If previous selection wasn't found, try to select the saved model from settings
                                if (!selectionRestored)
                                {
                                    var savedModel = AppSettings.LoadOllamaModel();
                                    if (!string.IsNullOrEmpty(savedModel))
                                    {
                                        for (int i = 0; i < OllamaModelComboBox.Items.Count; i++)
                                        {
                                            if (OllamaModelComboBox.Items[i].ToString() == savedModel)
                                            {
                                                OllamaModelComboBox.SelectedIndex = i;
                                                selectionRestored = true;
                                                Console.WriteLine($"🤖 Restored saved model from settings: {savedModel}");
                                                break;
                                            }
                                        }
                                    }
                                }

                                // If still no selection, select the first item
                                if (!selectionRestored && OllamaModelComboBox.Items.Count > 0)
                                {
                                    OllamaModelComboBox.SelectedIndex = 0;
                                    Console.WriteLine($"🤖 Selected first available model: {OllamaModelComboBox.Items[0]}");
                                }


                                OllamaStatusText.Text = $"🤖 Ollama: Ready ({models.Length} models)";
                                Console.WriteLine($"🤖 Refreshed Ollama models: {string.Join(", ", models)}");
                            }
                            else
                            {
                                InitializeOllamaModels();
                                OllamaStatusText.Text = "🤖 Ollama: Ready (using defaults)";
                            }
                        }
                        catch (Exception uiEx)
                        {
                            Console.WriteLine($"Error updating UI with models: {uiEx.Message}");
                            OllamaStatusText.Text = "🤖 Ollama: Error loading models";
                        }
                        finally
                        {
                            OllamaModelComboBox.IsEnabled = true;
                        }
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        InitializeOllamaModels();
                        OllamaStatusText.Text = "🤖 Ollama: Connection failed (using defaults)";
                        OllamaModelComboBox.IsEnabled = true;
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing Ollama models: {ex.Message}");

                Dispatcher.Invoke(() =>
                {
                    InitializeOllamaModels();
                    OllamaStatusText.Text = "🤖 Ollama: Error (using defaults)";
                    OllamaModelComboBox.IsEnabled = true;
                });
            }
        }

        // XAML event handlers (stubs/minimal implementations)
        private void OllamaModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var model = OllamaModelComboBox.SelectedItem?.ToString();
                if (!string.IsNullOrWhiteSpace(model))
                {
                    AppSettings.SaveOllamaModel(model);
                    OllamaStatusText.Text = $"🤖 Ollama model: {model}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OllamaModelComboBox_SelectionChanged: {ex.Message}");
            }
        }

        private void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshOllamaModels();
        }

        private void ScenarioComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var item = ScenarioComboBox.SelectedItem as ComboBoxItem;
                var tag = item?.Tag?.ToString() ?? string.Empty;
                string desc = tag switch
                {
                    "Local" => "Local - Processes audio locally without Discord integration.",
                    "Discord" => "Discord - Bot integration enabled; can join voice channels.",
                    "Kiosk" => "Kiosk - Public-facing mode with simplified UI.",
                    _ => "Select a scenario to see its description."
                };
                ScenarioDescriptionText.Text = desc;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ScenarioComboBox_SelectionChanged: {ex.Message}");
            }
        }

        private void ApplyScenarioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var item = ScenarioComboBox.SelectedItem as ComboBoxItem;
                var tag = item?.Tag?.ToString() ?? "";
                Console.WriteLine($"Applying scenario: {tag}");
                DiagnosticsStatusText.Text = $"Applied scenario: {tag}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error applying scenario: {ex.Message}");
            }
        }

        private void RefreshValidationButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DiagnosticsStatusText.Text = "Validation refreshed.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing validation: {ex.Message}");
            }
        }

        private void RefreshDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DiagnosticsReportTextBox.Text = "Diagnostics report not implemented.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing diagnostics: {ex.Message}");
            }
        }

        private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(DiagnosticsReportTextBox.Text ?? string.Empty);
                DiagnosticsStatusText.Text = "Diagnostics copied to clipboard.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error copying diagnostics: {ex.Message}");
            }
        }
        // Stub methods to satisfy XAML handlers and initialization calls
        private void InitializeAudioInputControls() { }
        private void InitializeAudioDevicesUI() { }
        private void InitializeTtsSystem() { }
        private void InitializeDiscordBot() { }
        private void InitializeVolumeControls() { }
        private void InitializeIdentityFusionCleanup() { }
        private void LoadDiagnosticsTab() { }
        private string GetCurrentTtsSpeakerRefId() { return AppSettings.LoadTtsSpeaker(); }

        // XAML event handler stubs
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e) { _isClosing = true; }
        private void MicInputEnabledCheckBox_Checked(object sender, RoutedEventArgs e) { _isMicrophoneInputEnabled = true; }
        private void MicInputEnabledCheckBox_Unchecked(object sender, RoutedEventArgs e) { _isMicrophoneInputEnabled = false; }
        private void DiscordInputEnabledCheckBox_Checked(object sender, RoutedEventArgs e) { _isDiscordInputEnabled = true; }
        private void DiscordInputEnabledCheckBox_Unchecked(object sender, RoutedEventArgs e) { _isDiscordInputEnabled = false; }
        private void TtsModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void TtsSpeakerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void TtsGpuToggleButton_Click(object sender, RoutedEventArgs e) { }
        private void RefreshTtsModelsButton_Click(object sender, RoutedEventArgs e) { }
    }
}