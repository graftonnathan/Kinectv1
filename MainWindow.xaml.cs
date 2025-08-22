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

        // ENHANCED DOUBLE REGISTRATION PREVENTION - Discord initialization protection
        private static int _discordInitInProgress = 0; // 0 = not in progress, 1 = in progress

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
        private bool _isMicrophoneInputEnabled = true;
        private bool _isDiscordInputEnabled = true;

        // Volume control fields
        private double _localTtsVolume = 1.0; // 100%
        private double _discordTtsVolume = 1.0; // 100%

        private void UpdateRmsLevel(float rawRms)
        {
            if (_isClosing) return; // Prevent UI updates during shutdown

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return; // Double check inside dispatcher

                    // Check if UI elements are still valid
                    if (RmsBar != null && RmsText != null)
                    {
                        // Only update if microphone input is enabled
                        if (_isMicrophoneInputEnabled)
                        {
                            // Smooth the RMS values for better visualization
                            _smoothedRms = 0.7f * _smoothedRms + 0.3f * rawRms;
                            var scaledRms = Math.Min(100, Math.Max(0, (_smoothedRms / 10000.0f) * 100));

                            RmsBar.Value = scaledRms;
                            RmsText.Text = $"RMS: {_smoothedRms:F1} ({scaledRms:F0}%)";

                            // Emit telemetry gauge for mic RMS
                            Telemetry.Gauge("gauge.audio.mic.rms", _smoothedRms);

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
                });
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

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return; // Double check inside dispatcher

                    // Check if UI elements are still valid
                    if (DiscordRmsBar != null && DiscordRmsText != null)
                    {
                        // Only update if Discord input is enabled
                        if (_isDiscordInputEnabled)
                        {
                            // Smooth the Discord RMS values for better visualization
                            _smoothedDiscordRms = 0.7f * _smoothedDiscordRms + 0.3f * rawRms;
                            
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
                });
            }
            catch (Exception ex)
            {
                // Suppress exceptions during shutdown
                if (!_isClosing)
                {
                    Console.WriteLine($"Error updating Discord RMS level: {ex.Message}");
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
                                            Console.WriteLine($"🤖 Restored previous selection: {currentSelection}");
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

        /// <summary>
        /// TTS model selection changed event handler
        /// </summary>
        private void TtsModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (TtsModelComboBox.SelectedItem != null)
                {
                    var selectedModel = TtsModelComboBox.SelectedItem.ToString();
                    
                    // Save the model path
                    var modelPath = $"models\\tts\\{selectedModel}";
                    AppSettings.SaveTtsModelPath(modelPath);
                    
                    Console.WriteLine($"🎤 TTS model changed to: {selectedModel}");
                    
                    // Update status
                    TtsModelStatusText.Text = $"Selected TTS model: {selectedModel}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error changing TTS model: {ex.Message}");
                MessageBox.Show($"Error changing TTS model: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// TTS speaker selection changed event handler
        /// </summary>
        private void TtsSpeakerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (TtsSpeakerComboBox.SelectedItem is ComboBoxItem comboItem && comboItem.Tag != null)
                {
                    var speakerRefId = comboItem.Tag.ToString();
                    var speakerDisplayText = comboItem.Content.ToString();
                    
                    // Save the speaker immediately
                    AppSettings.SaveTtsSpeaker(speakerRefId);
                    
                    Console.WriteLine($"🎤 TTS speaker changed to: {speakerDisplayText} (REF ID: {speakerRefId})");
                    Console.WriteLine($"💾 TTS speaker saved automatically: {speakerRefId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error changing TTS speaker: {ex.Message}");
            }
        }

        /// <summary>
        /// TTS GPU toggle button click event handler
        /// </summary>
        private void TtsGpuToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var currentGpuSetting = AppSettings.LoadTtsUseGpu();
                var newGpuSetting = !currentGpuSetting;
                
                // Save the new setting
                AppSettings.SaveTtsUseGpu(newGpuSetting);
                
                // Update button appearance
                TtsGpuToggleButton.Content = newGpuSetting ? "🚀 GPU" : "💻 CPU";
                TtsGpuToggleButton.Background = newGpuSetting
                    ? this.TryFindResource("AccentPurple") as SolidColorBrush ?? Brushes.Purple
                    : this.TryFindResource("AccentBlue") as SolidColorBrush ?? Brushes.Blue;
                
                Console.WriteLine($"🎤 TTS GPU setting changed to: {(newGpuSetting ? "GPU" : "CPU")}");

                // Show info about when the change takes effect
                var statusMessage = newGpuSetting 
                    ? "GPU acceleration enabled (takes effect on next model load)"
                    : "CPU processing enabled (takes effect on next model load)";
                
                MessageBox.Show(statusMessage, "TTS Processing Mode Changed",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error toggling TTS GPU setting: {ex.Message}");
                MessageBox.Show($"Error toggling TTS GPU setting: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Refresh TTS models button click event handler
        /// </summary>
        private void RefreshTtsModelsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Console.WriteLine("🔄 Refreshing TTS models...");
                
                // Re-initialize the TTS system to pick up new models
                Task.Run(() =>
                {
                    try
                    {
                        var availableModels = CoquiTtsService.GetAvailableModels();
                        
                        if (!_isClosing)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (_isClosing) return;
                                
                                var currentSelection = TtsModelComboBox.SelectedItem?.ToString();
                                

                                TtsModelComboBox.Items.Clear();
                                
                                if (availableModels.Length > 0)
                                {
                                    foreach (var model in availableModels)
                                    {
                                        TtsModelComboBox.Items.Add(model);
                                    }
                                    
                                    // Try to restore previous selection
                                    var selectionRestored = false;
                                    if (!string.IsNullOrEmpty(currentSelection))
                                    {
                                        for (int i = 0; i < TtsModelComboBox.Items.Count; i++)
                                        {
                                            if (TtsModelComboBox.Items[i].ToString() == currentSelection)
                                            {
                                                TtsModelComboBox.SelectedIndex = i;
                                                selectionRestored = true;
                                                break;
                                            }
                                        }
                                    }
                                    
                                    if (!selectionRestored && TtsModelComboBox.Items.Count > 0)
                                    {
                                        TtsModelComboBox.SelectedIndex = 0;
                                    }
                                    
                                    TtsModelStatusText.Text = $"Found {availableModels.Length} models";
                                    Console.WriteLine($"✅ Refreshed TTS models: found {availableModels.Length} models");
                                }
                                else
                                {
                                    TtsModelComboBox.Items.Add("No models found");
                                    TtsModelComboBox.SelectedIndex = 0;
                                    TtsModelStatusText.Text = "No models found - place .onnx files in models/tts/";
                                    Console.WriteLine("❌ No TTS models found after refresh");
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_isClosing)
                        {
                            Console.WriteLine($"Error during TTS model refresh: {ex.Message}");
                            Dispatcher.Invoke(() =>
                            {
                                TtsModelStatusText.Text = "Error refreshing models";
                            });
                        }
                    }
                }, _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing TTS models: {ex.Message}");
                MessageBox.Show($"Error refreshing TTS models: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Add missing stub methods to fix build errors
        private void InitializeAudioInputControls() 
        {
            try
            {
                // Initialize audio input state based on saved AudioInMode setting
                var audioMode = AppSettings.LoadAudioInMode();
                
                // Set Discord input enabled if mode is DiscordVoice
                _isDiscordInputEnabled = (audioMode == AudioInMode.DiscordVoice);
                VoiceRecognizer.SetDiscordInputEnabled(_isDiscordInputEnabled);
                
                // Set microphone input enabled (for now, always enabled in LocalMic mode)
                _isMicrophoneInputEnabled = VoiceRecognizer.IsMicrophoneInputEnabled();

                // Update UI checkboxes to match states
                if (MicInputEnabledCheckBox != null)
                {
                    MicInputEnabledCheckBox.IsChecked = _isMicrophoneInputEnabled;
                }

                if (DiscordInputEnabledCheckBox != null)
                {
                    DiscordInputEnabledCheckBox.IsChecked = _isDiscordInputEnabled;
                }

                Console.WriteLine($"🎛️ Audio input controls initialized:");
                Console.WriteLine($"   Audio Input Mode: {audioMode}");
                Console.WriteLine($"   Microphone: {(_isMicrophoneInputEnabled ? "Enabled" : "Disabled")}");
                Console.WriteLine($"   Discord Input: {(_isDiscordInputEnabled ? "Enabled" : "Disabled")}");

                // Update status displays
                UpdateMicrophoneStatus();
                UpdateDiscordStatus();

                // Initialize RMS meters with baseline 0 for immediate display
                InitializeRmsBaseline();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize audio input controls: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize RMS meters with baseline 0 for immediate display
        /// </summary>
        private void InitializeRmsBaseline()
        {
            try
            {
                // Initialize baseline RMS immediately to render the meter
                if (RmsBar != null && RmsText != null)
                {
                    RmsBar.Value = 0;
                    RmsText.Text = "RMS: 0.0 (0%)";
                    
                    // Set initial color to green (quiet/good)
                    var greenBrush = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.Green;
                    RmsBar.Foreground = greenBrush;
                }

                if (DiscordRmsBar != null && DiscordRmsText != null)
                {
                    DiscordRmsBar.Value = 0;
                    DiscordRmsText.Text = "RMS: 0.0 (0%)";
                    
                    // Set initial color to green (quiet/good)
                    var greenBrush = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.Green;
                    DiscordRmsBar.Foreground = greenBrush;
                }

                // Emit initial telemetry gauge
                Telemetry.Gauge("gauge.audio.mic.rms", 0);

                Console.WriteLine("🎵 RMS baseline initialized to 0 for immediate meter display");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize RMS baseline: {ex.Message}");
            }
        }

        private void InitializeTtsSystem() 
        {
            try
            {
                Console.WriteLine("🎤 Initializing TTS system...");

                // Load TTS models
                var availableModels = CoquiTtsService.GetAvailableModels();

                TtsModelComboBox.Items.Clear();
                if (availableModels.Length > 0)
                {
                    foreach (var model in availableModels)
                    {
                        TtsModelComboBox.Items.Add(model);
                    }

                    var savedModelPath = AppSettings.LoadTtsModelPath();
                    var savedModelName = !string.IsNullOrEmpty(savedModelPath) ? Path.GetFileName(savedModelPath) : null;

                    var selectedIndex = -1;
                    if (!string.IsNullOrEmpty(savedModelName))
                    {
                        for (int i = 0; i < TtsModelComboBox.Items.Count; i++)
                        {
                            if (TtsModelComboBox.Items[i].ToString() == savedModelName)
                            {
                                selectedIndex = i;
                                break;
                            }
                        }
                    }

                    TtsModelComboBox.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                    TtsModelStatusText.Text = $"Found {availableModels.Length} models";
                    Console.WriteLine($"🎤 Found {availableModels.Length} TTS models, selected: {TtsModelComboBox.SelectedItem}");
                }
                else
                {
                    TtsModelComboBox.Items.Add("No models found");
                    TtsModelComboBox.SelectedIndex = 0;
                    TtsModelStatusText.Text = "No models found - place .onnx files in models/tts/";
                    Console.WriteLine("No TTS models found in models/tts directory");
                }

                // IMPORTANT: Load the saved speaker BEFORE populating dropdown
                var savedSpeaker = AppSettings.LoadTtsSpeaker();
                Console.WriteLine($"🎤 Attempting to load saved TTS speaker: '{savedSpeaker}'");

                // Load speakers from SpeakerList.txt (but don't auto-select yet)
                PopulateTtsSpeakerDropdownWithoutSelection();

                // Show speaker statistics
                ShowSpeakerStatistics();

                // NOW apply the saved speaker selection
                if (!string.IsNullOrEmpty(savedSpeaker))
                {
                    Console.WriteLine($"🎤 Loading saved TTS speaker: {savedSpeaker}");
                    bool speakerFound = SelectTtsSpeaker(savedSpeaker);

                    if (!speakerFound)
                    {
                        Console.WriteLine($"⚠️ Saved TTS speaker '{savedSpeaker}' not found in dropdown, using first available speaker");
                        if (TtsSpeakerComboBox.Items.Count > 0)
                        {
                            TtsSpeakerComboBox.SelectedIndex = 0;
                            var fallbackSpeaker = GetCurrentTtsSpeakerRefId();

                            // Immediately save the fallback speaker
                            AppSettings.SaveTtsSpeaker(fallbackSpeaker);
                            Console.WriteLine($"💾 Saved fallback speaker immediately: {fallbackSpeaker}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"✅ Successfully restored saved TTS speaker: {savedSpeaker}");
                        Console.WriteLine($"💾 Speaker selection automatically saved");
                    }
                }
                else
                {
                    Console.WriteLine($"🎤 No saved speaker found, using default");
                    // Select first speaker by default if no saved speaker
                    if (TtsSpeakerComboBox.Items.Count > 0)
                    {
                        TtsSpeakerComboBox.SelectedIndex = 0;
                        var defaultSpeaker = GetCurrentTtsSpeakerRefId();

                        // Immediately save the default speaker
                        AppSettings.SaveTtsSpeaker(defaultSpeaker);
                        Console.WriteLine($"💾 Saved default speaker immediately: {defaultSpeaker}");
                    }
                }

                var selectedModelName = TtsModelComboBox.SelectedItem?.ToString();

                if (!string.IsNullOrEmpty(selectedModelName) && selectedModelName != "No models found")
                {
                    Console.WriteLine($"🎤 Attempting to initialize TTS with model: {selectedModelName}");

                    Task.Run(() =>
                    {
                        try
                        {
                            var success = CoquiTtsService.SwitchModel(selectedModelName);

                            if (!_isClosing)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (success)
                                    {
                                        // Update button to reflect new mode
                                        ToggleTtsButton.Content = "Disable TTS";
                                        ToggleTtsButton.Background = this.TryFindResource("AccentRed") as SolidColorBrush ?? Brushes.IndianRed;
                                        TtsStatusText.Text = $"🎤 TTS: Enabled ({(AppSettings.LoadTtsUseGpu() ? "GPU" : "CPU")})";
                                        TtsModelStatusText.Text = $"{selectedModelName} loaded";

                                        Console.WriteLine($"🎤 TTS system initialized successfully with model: {selectedModelName}");

                                        // Log final speaker selection for verification
                                        var finalSpeaker = GetCurrentTtsSpeakerRefId();
                                        Console.WriteLine($"🎯 Final TTS speaker selection: {finalSpeaker}");
                                        Console.WriteLine($"💾 Speaker persistence: IMMEDIATE (saved on selection, not on exit)");
                                    }
                                    else
                                    {
                                        ToggleTtsButton.Content = "Enable TTS";
                                        ToggleTtsButton.Background = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.LightGreen;
                                        TtsStatusText.Text = "🎤 TTS: Model load failed";
                                        TtsModelStatusText.Text = $"Failed to load {selectedModelName}";

                                        Console.WriteLine($"TTS system initialization failed for model: {selectedModelName}");
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            if (!_isClosing)
                            {
                                Console.WriteLine($"Error during TTS initialization: {ex.Message}");
                                Dispatcher.Invoke(() =>
                                {
                                    ToggleTtsButton.Content = "Enable TTS";
                                    ToggleTtsButton.Background = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.LightGreen;
                                    TtsStatusText.Text = "🎤 TTS: Initialization error";
                                    TtsModelStatusText.Text = $"Error loading {selectedModelName}";

                                    var defaultGpu = AppSettings.LoadTtsUseGpu();
                                    TtsGpuToggleButton.Content = defaultGpu ? "🚀 GPU" : "💻 CPU";
                                    TtsGpuToggleButton.Background = defaultGpu
                                        ? this.TryFindResource("AccentPurple") as SolidColorBrush ?? Brushes.Purple
                                        : this.TryFindResource("AccentBlue") as SolidColorBrush ?? Brushes.Blue;
                                });
                            }
                        }
                    }, _cancellationTokenSource.Token);

                    ToggleTtsButton.Content = "Enable TTS";
                    ToggleTtsButton.Background = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.LightGreen;
                    TtsStatusText.Text = "🎤 TTS: Loading...";
                    TtsModelStatusText.Text = $"🔄 Loading {selectedModelName}...";

                    var defaultGpuPref = AppSettings.LoadTtsUseGpu();
                    TtsGpuToggleButton.Content = defaultGpuPref ? "🚀 GPU" : "💻 CPU";
                    TtsGpuToggleButton.Background = defaultGpuPref
                        ? this.TryFindResource("AccentPurple") as SolidColorBrush ?? Brushes.Purple
                        : this.TryFindResource("AccentBlue") as SolidColorBrush ?? Brushes.Blue;
                }
                else
                {
                    ToggleTtsButton.Content = "Enable TTS";
                    ToggleTtsButton.Background = this.TryFindResource("AccentGreen") as SolidColorBrush ?? Brushes.LightGreen;
                    TtsStatusText.Text = availableModels.Length > 0 ? "🎤 TTS: Ready" : "🎤 TTS: No models found";
                    Console.WriteLine($"No TTS model selected for initialization");

                    var defaultGpuPref = AppSettings.LoadTtsUseGpu();
                    TtsGpuToggleButton.Content = defaultGpuPref ? "🚀 GPU" : "💻 CPU";
                    TtsGpuToggleButton.Background = defaultGpuPref
                        ? this.TryFindResource("AccentPurple") as SolidColorBrush ?? Brushes.Purple
                        : this.TryFindResource("AccentBlue") as SolidColorBrush ?? Brushes.Blue;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize TTS system: {ex.Message}");
                TtsStatusText.Text = "🎤 TTS: Initialization error";
            }
        }

        /// <summary>
        /// Initialize Discord bot with enhanced double registration prevention
        /// </summary>
        private void InitializeDiscordBot() 
        {
            // ATOMIC CHECK: Prevent multiple initialization attempts
            if (Interlocked.CompareExchange(ref _discordInitInProgress, 1, 0) != 0)
            {
                Console.WriteLine("🔄 === InitializeDiscordBot already in progress - EARLY RETURN ===");
                return;
            }

            try
            {
                Console.WriteLine($"🔍 === MainWindow.InitializeDiscordBot() ENTRY (Thread-Safe) ===");
                Console.WriteLine($"🔍 Thread ID: {Thread.CurrentThread.ManagedThreadId}");
                
                // Check if Discord bot is enabled in settings
                if (!AppSettings.LoadDiscordBotEnabled())
                {
                    Console.WriteLine("🔒 Discord bot is disabled in settings - skipping initialization");
                    return;
                }

                Console.WriteLine("🚀 Discord bot is enabled - starting initialization...");
                Console.WriteLine($"🔍 BotManager.IsRunning: {DiscordNetBotManager.IsRunning}");

                // GUARD: Don't start if already running
                if (DiscordNetBotManager.IsRunning)
                {
                    Console.WriteLine("✅ Discord bot is already running - SKIPPING INITIALIZATION");
                    return;
                }

                // Start Discord bot in background to avoid blocking UI - SINGLE TASK ONLY
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Console.WriteLine($"🔍 === Background Task Starting DiscordNetBotManager.StartAsync() ===");
                        Console.WriteLine($"🔍 Background Thread ID: {Thread.CurrentThread.ManagedThreadId}");
                        
                        // Test configuration first
                        bool configValid = await DiscordNetBotManager.TestConfigurationAsync();

                        if (!configValid)
                        {
                            Console.WriteLine("❌ Discord bot configuration is invalid - not starting");
                            return;
                        }

                        // Initialize and start the bot - StartAsync has its own protection
                        bool success = await DiscordNetBotManager.StartAsync();

                        if (success)
                        {
                            Console.WriteLine("✅ Discord bot started successfully with MainWindow");

                            // Show usage instructions in console
                            Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    Console.WriteLine("\n🎮 Discord Bot Commands Available:");
                                    var prefix = AppSettings.LoadDiscordBotPrefix();
                                    Console.WriteLine($"  {prefix}help          - Show help message");
                                    Console.WriteLine($"  {prefix}status        - Show bot status");
                                    Console.WriteLine($"  {prefix}join [channel] - Join voice channel");
                                    Console.WriteLine($"  {prefix}leave         - Leave voice channel");
                                    Console.WriteLine($"  {prefix}speak <text>  - Speak text using TTS");
                                    Console.WriteLine($"  {prefix}clearsession  - Clear Discord voice session");
                                    Console.WriteLine($"  {prefix}joinforce [channel] - Force join with fresh session");
                                    Console.WriteLine("\n💡 Invite the bot to your Discord server to start using it!");
                                }
                                catch (Exception dispatchEx)
                                {
                                    Console.WriteLine($"❌ Error in dispatcher invoke: {dispatchEx.Message}");
                                }
                            });
                        }
                        else
                        {
                            Console.WriteLine("❌ Discord bot failed to start");
                        }
                        
                        Console.WriteLine($"🔍 === Background Task Completed ===");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Discord bot initialization error: {ex.Message}");
                        Console.WriteLine($"📍 Stack trace: {ex.StackTrace}");
                    }
                    finally
                    {
                        // Always reset the initialization flag when task completes
                        Interlocked.Exchange(ref _discordInitInProgress, 0);
                    }
                }, _cancellationTokenSource.Token);
                
                Console.WriteLine($"🔍 === MainWindow.InitializeDiscordBot() EXIT (Background Task Started) ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to initialize Discord bot: {ex.Message}");
                Console.WriteLine($"📍 Stack trace: {ex.StackTrace}");
                
                // Reset flag on error
                Interlocked.Exchange(ref _discordInitInProgress, 0);
            }
        }

        private string GetCurrentTtsSpeakerRefId() 
        {
            try
            {
                if (TtsSpeakerComboBox?.SelectedItem is ComboBoxItem comboItem && comboItem.Tag != null)
                {
                    return comboItem.Tag.ToString();
                }

                // Fallback to first speaker if none selected
                if (TtsSpeakerComboBox.Items.Count > 0 && TtsSpeakerComboBox.Items[0] is ComboBoxItem firstItem)
                {
                    return firstItem.Tag?.ToString() ?? "em_alex"; // Default to Kokoro default voice
                }

                return "em_alex"; // Ultimate fallback
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting current TTS speaker: {ex.Message}");
                return "em_alex"; // Fallback to Kokoro default voice
            }
        }

        /// <summary>
        /// Enhanced clean shutdown handler - Uses centralized HostedServicesManager for coordinated shutdown
        /// Implements the 3-step clean exit pattern:
        /// 1. Cancel background tasks
        /// 2. Stop all hosted services through centralized manager
        /// 3. Save essential settings and cleanup
        /// </summary>
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e) 
        {
            _isClosing = true; // Set flag to prevent new operations
            
            Console.WriteLine("🔴 === APPLICATION SHUTDOWN INITIATED ===");
            Console.WriteLine("🔴 Using centralized hosted services manager for clean shutdown...");

            try
            {
                // STEP 1: Cancel all background tasks FIRST
                try
                {
                    Console.WriteLine("🔴 STEP 1: Canceling background tasks...");
                    _cancellationTokenSource?.Cancel();
                    Console.WriteLine("✅ Background tasks canceled");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Error canceling background tasks: {ex.Message}");
                }

                // STEP 2: Stop all hosted services through centralized manager
                try
                {
                    if (App.ServicesManager != null && App.ServicesManager.IsStarted)
                    {
                        Console.WriteLine("🔴 STEP 2: Stopping all hosted services through centralized manager...");
                        Console.WriteLine("🔴 Performing blocking shutdown (exit handlers require synchronous completion)");
                        
                        // Use blocking Wait() pattern as recommended for exit handlers
                        var stopTask = App.ServicesManager.StopAllAsync(TimeSpan.FromSeconds(30));
                        
                        // Block until shutdown completes (with timeout for safety)
                        Console.WriteLine("🔴 Blocking on hosted services shutdown task...");
                        bool completedInTime = stopTask.Wait(35000); // 35 second timeout
                        
                        if (completedInTime)
                        {
                            Console.WriteLine("✅ All hosted services shut down successfully within timeout");
                        }
                        else
                        {
                            Console.WriteLine("⚠️ Hosted services shutdown timed out after 35 seconds");
                            // Continue with shutdown anyway - don't block application exit indefinitely
                        }
                    }
                    else
                    {
                        Console.WriteLine("🔴 STEP 2: Hosted services manager not started - skipping centralized shutdown");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error during hosted services shutdown: {ex.Message}");
                    // Don't let service shutdown errors prevent application exit
                }

                // STEP 3: Reset atomic flags to ensure clean state
                try
                {
                    Console.WriteLine("🔴 STEP 3: Resetting atomic flags...");
                    Interlocked.Exchange(ref _discordInitInProgress, 0);
                    Console.WriteLine("✅ Atomic flags reset");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Error resetting atomic flags: {ex.Message}");
                }

                // STEP 4: Save essential settings (keep this minimal for speed)
                try
                {
                    Console.WriteLine("🔴 STEP 4: Saving essential settings...");
                    var windowState = this.WindowState == WindowState.Maximized ? "Maximized" : "Normal";
                    AppSettings.SaveWindowSettings(this.Width, this.Height, this.Left, this.Top, windowState);
                    AppSettings.SaveVoiceThreshold(_currentThreshold);
                    AppSettings.SaveDarkMode(_isDarkMode);
                    Console.WriteLine("✅ Essential settings saved");
                }
                catch (Exception saveEx)
                {
                    Console.WriteLine($"⚠️ Error saving essential settings: {saveEx.Message}");
                }

                Console.WriteLine("🔴 === CLEAN SHUTDOWN COMPLETED ===");
                Console.WriteLine("🔴 All services stopped cleanly through centralized management");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Critical error during application shutdown: {ex.Message}");
                Console.WriteLine($"📍 Stack trace: {ex.StackTrace}");
                // Don't prevent application exit even if there are errors
            }
            finally
            {
                // STEP 5: Final cleanup (always execute)
                try
                {
                    Console.WriteLine("🔴 FINAL: Disposing cancellation token...");
                    _cancellationTokenSource?.Dispose();
                    Console.WriteLine("✅ Cancellation token disposed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Error disposing cancellation token: {ex.Message}");
                }

                Console.WriteLine("🔴 === APPLICATION EXIT READY ===");
                Console.WriteLine("🔴 Centralized shutdown pattern completed - application can now exit safely");
            }
        }

        private void MicInputEnabledCheckBox_Checked(object sender, RoutedEventArgs e) 
        {
            try
            {
                _isMicrophoneInputEnabled = true;
                VoiceRecognizer.SetMicrophoneInputEnabled(true);
                Console.WriteLine("🎤 Microphone input enabled via UI");

                // Update RMS display to show it's active
                UpdateMicrophoneStatus();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enabling microphone input: {ex.Message}");
            }
        }

        private void MicInputEnabledCheckBox_Unchecked(object sender, RoutedEventArgs e) 
        {
            try
            {
                _isMicrophoneInputEnabled = false;
                VoiceRecognizer.SetMicrophoneInputEnabled(false);
                Console.WriteLine("🎤 Microphone input disabled via UI");

                // Clear microphone RMS display
                UpdateMicrophoneStatus();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disabling microphone input: {ex.Message}");
            }
        }

        private void DiscordInputEnabledCheckBox_Checked(object sender, RoutedEventArgs e) 
        {
            try
            {
                // Set audio input mode to Discord voice channel
                AppSettings.SaveAudioInMode(AudioInMode.DiscordVoice);
                _isDiscordInputEnabled = true;
                VoiceRecognizer.SetDiscordInputEnabled(true);
                Console.WriteLine("🤖 Discord voice input enabled via UI (AudioInMode=DiscordVoice)");

                // Update RMS display to show it's active
                UpdateDiscordStatus();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enabling Discord input: {ex.Message}");
            }
        }

        private void DiscordInputEnabledCheckBox_Unchecked(object sender, RoutedEventArgs e) 
        {
            try
            {
                // Set audio input mode back to local microphone
                AppSettings.SaveAudioInMode(AudioInMode.LocalMic);
                _isDiscordInputEnabled = false;
                VoiceRecognizer.SetDiscordInputEnabled(false);
                Console.WriteLine("🤖 Discord input disabled via UI (AudioInMode=LocalMic)");

                // Clear Discord RMS display
                UpdateDiscordStatus();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disabling Discord input: {ex.Message}");
            }
        }

        private void OllamaModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) 
        {
            try
            {
                if (OllamaModelComboBox.SelectedItem != null)
                {
                    var selectedModel = OllamaModelComboBox.SelectedItem.ToString();
                    
                    // Save the selected model immediately using the correct method
                    OllamaService.SetDefaultModel(selectedModel);
                    
                    Console.WriteLine($"🤖 Ollama model changed to: {selectedModel}");
                    
                    // Update status
                    OllamaStatusText.Text = $"🤖 Ollama: Model set to {selectedModel}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error changing Ollama model: {ex.Message}");
                MessageBox.Show($"Error changing Ollama model: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshModelsButton_Click(object sender, RoutedEventArgs e) 
        {
            try
            {
                Console.WriteLine("🔄 Refreshing Ollama models...");
                RefreshOllamaModels();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing Ollama models: {ex.Message}");
                MessageBox.Show($"Error refreshing Ollama models: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Update microphone status display
        /// </summary>
        private void UpdateMicrophoneStatus()
        {
            if (_isClosing) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return;

                    if (RmsBar != null && RmsText != null)
                    {
                        if (!_isMicrophoneInputEnabled)
                        {
                            // Clear RMS display when disabled
                            RmsBar.Value = 0;
                            RmsText.Text = "DISABLED";
                            RmsBar.Foreground = this.TryFindResource("BorderBrush") as SolidColorBrush ?? Brushes.Gray;
                        }
                        // If enabled, the UpdateRmsLevel method handles the display
                    }
                });
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($"Error updating microphone status: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Update Discord status display
        /// </summary>
        private void UpdateDiscordStatus()
        {
            if (_isClosing) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isClosing) return;

                    if (DiscordRmsBar != null && DiscordRmsText != null)
                    {
                        if (!_isDiscordInputEnabled)
                        {
                            // Clear RMS display when disabled
                            DiscordRmsBar.Value = 0;
                            DiscordRmsText.Text = "DISABLED";
                            DiscordRmsBar.Foreground = this.TryFindResource("BorderBrush") as SolidColorBrush ?? Brushes.Gray;
                        }
                        // If enabled, the UpdateDiscordRmsLevel method handles the display
                    }
                });
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    Console.WriteLine($" Error updating Discord status: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Show speaker statistics in console (for debugging)
        /// </summary>
        private void ShowSpeakerStatistics()
        {
            try
            {
                // Ensure Kokoro is initialized so voices are loaded
                KokoroTtsService.Initialize();
                var voices = KokoroTtsService.GetVoices()?.ToList() ?? new List<string>();
                if (voices.Count == 0)
                {
                    return; // No voices, skip statistics
                }

                Console.WriteLine($"📋 {voices.Count} TTS voices loaded (Kokoro)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error loading speaker statistics: {ex.Message}");
            }
        }

        /// <summary>
        /// Populate TTS speaker dropdown with Kokoro voices (without auto-selecting)
        /// </summary>
        private void PopulateTtsSpeakerDropdownWithoutSelection()
        {
            try
            {
                TtsSpeakerComboBox.Items.Clear();

                // Ensure Kokoro voices are available
                KokoroTtsService.Initialize();
                var voices = KokoroTtsService.GetVoices()?.ToList();

                if (voices == null || voices.Count == 0)
                {
                    // Add a default item indicating no voices are available
                    TtsSpeakerComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = "No voices available",
                        Tag = "em_alex" // Default Kokoro voice key
                    });
                }
                else
                {
                    foreach (var voiceKey in voices)
                    {
                        // Display a friendly name (replace underscores with spaces)
                        var display = voiceKey.Replace('_', ' ');
                        var item = new ComboBoxItem
                        {
                            Content = display,
                            Tag = voiceKey,
                            ToolTip = voiceKey
                        };
                        TtsSpeakerComboBox.Items.Add(item);
                    }

                    Console.WriteLine($"✅ Populated {voices.Count} Kokoro voices");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to populate TTS speaker dropdown: {ex.Message}");

                // Emergency fallback: add default item
                TtsSpeakerComboBox.Items.Clear();
                TtsSpeakerComboBox.Items.Add(new ComboBoxItem
                {
                    Content = "No voices available",
                    Tag = "em_alex"
                });
            }
        }

        /// <summary>
        /// Select TTS speaker in dropdown and save immediately
        /// </summary>
        private bool SelectTtsSpeaker(string speakerValue)
        {
            try
            {
                for (int i = 0; i < TtsSpeakerComboBox.Items.Count; i++)
                {
                    if (TtsSpeakerComboBox.Items[i] is ComboBoxItem item &&
                        item.Tag?.ToString() == speakerValue)
                    {
                        TtsSpeakerComboBox.SelectedIndex = i;

                        // The SelectionChanged event will handle the saving automatically
                        Console.WriteLine($"🎤 Selected TTS speaker: {speakerValue} ({item.Content})");
                        Console.WriteLine($"💾 Speaker will be saved automatically via SelectionChanged event");
                        return true; // Found and selected
                    }
                }

                // Speaker not found in existing dropdown, add it as custom
                var customItem = new ComboBoxItem
                {
                    Content = $"Custom: {speakerValue}",
                    Tag = speakerValue
                };
                TtsSpeakerComboBox.Items.Add(customItem);
                TtsSpeakerComboBox.SelectedIndex = TtsSpeakerComboBox.Items.Count - 1;

                // The SelectionChanged event will handle the saving automatically
                Console.WriteLine($"🎤 Added and selected custom TTS speaker: {speakerValue}");
                Console.WriteLine($"💾 Custom speaker will be saved automatically via SelectionChanged event");
                return true; // Added and selected
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to select TTS speaker '{speakerValue}': {ex.Message}");

                if (TtsSpeakerComboBox.Items.Count > 0)
                {
                    TtsSpeakerComboBox.SelectedIndex = 0;
                    Console.WriteLine($"🔄 Fallback to first speaker (will auto-save)");
                }

                return false; // Failed to select
            }
        }

        /// <summary>
        /// Handle local volume slider changes
        /// </summary>
        private void LocalVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                _localTtsVolume = e.NewValue / 100.0; // Convert percentage to 0.0-1.0 range
                
                var localVolumeLabel = this.FindName("LocalVolumeLabel") as TextBlock;
                if (localVolumeLabel != null)
                {
                    localVolumeLabel.Text = $"{(int)e.NewValue}%";
                }
                
                // Save the setting
                AppSettings.SaveLocalTtsVolume(_localTtsVolume);
                
                Console.WriteLine($"🔊 Local TTS volume set to: {(int)e.NewValue}% ({_localTtsVolume:F2})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating local TTS volume: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle Discord volume slider changes  
        /// </summary>
        private void DiscordVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                _discordTtsVolume = e.NewValue / 100.0; // Convert percentage to 0.0-1.0 range
                
                var discordVolumeLabel = this.FindName("DiscordVolumeLabel") as TextBlock;
                if (discordVolumeLabel != null)
                {
                    discordVolumeLabel.Text = $"{(int)e.NewValue}%";
                }
                
                // Save the setting
                AppSettings.SaveDiscordTtsVolume(_discordTtsVolume);
                
                Console.WriteLine($"🤖 Discord TTS volume set to: {(int)e.NewValue}% ({_discordTtsVolume:F2})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating Discord TTS volume: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize volume controls with saved settings
        /// </summary>
        private void InitializeVolumeControls()
        {
            try
            {
                // Load saved volume levels
                _localTtsVolume = AppSettings.LoadLocalTtsVolume();
                _discordTtsVolume = AppSettings.LoadDiscordTtsVolume();
                
                // Set slider values (convert 0.0-1.0 to 0-100 percentage)
                var localVolumeSlider = this.FindName("LocalVolumeSlider") as Slider;
                var localVolumeLabel = this.FindName("LocalVolumeLabel") as TextBlock;
                if (localVolumeSlider != null)
                {
                    localVolumeSlider.Value = _localTtsVolume * 100.0;
                    if (localVolumeLabel != null)
                    {
                        localVolumeLabel.Text = $"{(int)(_localTtsVolume * 100)}%";
                    }
                }
                
                var discordVolumeSlider = this.FindName("DiscordVolumeSlider") as Slider;
                var discordVolumeLabel = this.FindName("DiscordVolumeLabel") as TextBlock;
                if (discordVolumeSlider != null)
                {
                    discordVolumeSlider.Value = _discordTtsVolume * 100.0;
                    if (discordVolumeLabel != null)
                    {
                        discordVolumeLabel.Text = $"{(int)(_discordTtsVolume * 100)}%";
                    }
                }
                
                Console.WriteLine($"🔊 Volume controls initialized:");
                Console.WriteLine($"   Local TTS: {_localTtsVolume * 100:F0}%");
                Console.WriteLine($"   Discord TTS: {_discordTtsVolume * 100:F0}%");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing volume controls: {ex.Message}");
                // Set defaults if initialization fails
                _localTtsVolume = 1.0;
                _discordTtsVolume = 1.0;
            }
        }

        /// <summary>
        /// Initialize identity fusion cleanup timer
        /// </summary>
        private void InitializeIdentityFusionCleanup()
        {
            try
            {
                // Set up periodic cleanup timer for old identity entries
                var cleanupTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(10) // Clean up every 10 seconds
                };
                
                cleanupTimer.Tick += (sender, e) =>
                {
                    try
                    {
                        IdentityFusionTracker.CleanupOldEntries();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error during identity fusion cleanup: {ex.Message}");
                    }
                };
                
                cleanupTimer.Start();
                
                // Set up periodic status logging timer
                var statusTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(15) // Log status every 15 seconds
                };
                
                statusTimer.Tick += (sender, e) =>
                {
                    try
                    {
                        var status = IdentityFusionTracker.GetFusionStatus();
                        if (!status.Contains("No active identities"))
                        {
                            Console.WriteLine("--- Identity Fusion Status ---");
                            Console.WriteLine(status);
                            Console.WriteLine("-----------------------------");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error during identity fusion status logging: {ex.Message}");
                    }
                };
                
                statusTimer.Start();
                
                Console.WriteLine("🔀 Identity fusion cleanup timer initialized (10s interval)");
                Console.WriteLine("🔀 Identity fusion status logging initialized (15s interval)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing identity fusion cleanup: {ex.Message}");
            }
        }

        #region Diagnostics Tab Event Handlers

        /// <summary>
        /// Load diagnostics tab with current scenario and validation
        /// </summary>
        public void LoadDiagnosticsTab()
        {
            try
            {
                // Load current scenario into combo box
                var currentScenario = AppSettings.LoadAppScenario();
                
                // Find and select the matching combo box item
                foreach (ComboBoxItem item in ScenarioComboBox.Items)
                {
                    if (item.Tag.ToString() == currentScenario.ToString())
                    {
                        ScenarioComboBox.SelectedItem = item;
                        break;
                    }
                }

                // Update scenario description
                UpdateScenarioDescription(currentScenario);

                // Load validation results
                RefreshValidationResults();

                // Load full diagnostics report
                RefreshDiagnosticsReport();

                DiagnosticsStatusText.Text = $"Diagnostics loaded - Current scenario: {currentScenario}";
            }
            catch (Exception ex)
            {
                DiagnosticsStatusText.Text = $"Error loading diagnostics: {ex.Message}";
                Console.WriteLine($"Error loading diagnostics tab: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle scenario combo box selection change
        /// </summary>
        private void ScenarioComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (ScenarioComboBox.SelectedItem is ComboBoxItem selectedItem)
                {
                    var scenarioStr = selectedItem.Tag.ToString();
                    if (Enum.TryParse<AppScenario>(scenarioStr, out var scenario))
                    {
                        // Save the selected scenario
                        AppSettings.SaveAppScenario(scenario);

                        // Update description
                        UpdateScenarioDescription(scenario);

                        // Refresh validation since scenario changed
                        RefreshValidationResults();
                        RefreshDiagnosticsReport();

                        DiagnosticsStatusText.Text = $"Scenario changed to: {scenario}";
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsStatusText.Text = $"Error changing scenario: {ex.Message}";
                Console.WriteLine($"Error in scenario selection: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply scenario defaults button click
        /// </summary>
        private void ApplyScenarioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ScenarioComboBox.SelectedItem is ComboBoxItem selectedItem)
                {
                    var scenarioStr = selectedItem.Tag.ToString();
                    if (Enum.TryParse<AppScenario>(scenarioStr, out var scenario))
                    {
                        // Apply scenario defaults
                        AppSettings.ApplyScenarioDefaults(scenario);

                        // Refresh validation and diagnostics to reflect changes
                        RefreshValidationResults();
                        RefreshDiagnosticsReport();

                        DiagnosticsStatusText.Text = $"Applied {scenario} scenario defaults successfully";
                        
                        // Show confirmation message
                        MessageBox.Show($"Applied {scenario} scenario defaults successfully!\n\nPlease check the validation results for any remaining issues.", 
                                      "Scenario Defaults Applied", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsStatusText.Text = $"Error applying scenario defaults: {ex.Message}";
                MessageBox.Show($"Error applying scenario defaults: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Console.WriteLine($"Error applying scenario defaults: {ex.Message}");
            }
        }

        /// <summary>
        /// Refresh validation results button click
        /// </summary>
        private void RefreshValidationButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshValidationResults();
        }

        /// <summary>
        /// Refresh diagnostics report button click
        /// </summary>
        private void RefreshDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshDiagnosticsReport();
        }

        /// <summary>
        /// Copy diagnostics report to clipboard
        /// </summary>
        private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(DiagnosticsReportTextBox.Text))
                {
                    Clipboard.SetText(DiagnosticsReportTextBox.Text);
                    DiagnosticsStatusText.Text = "Diagnostics report copied to clipboard";
                }
                else
                {
                    DiagnosticsStatusText.Text = "No diagnostics report to copy";
                }
            }
            catch (Exception ex)
            {
                DiagnosticsStatusText.Text = $"Error copying to clipboard: {ex.Message}";
                Console.WriteLine($"Error copying diagnostics to clipboard: {ex.Message}");
            }
        }

        /// <summary>
        /// Update scenario description text
        /// </summary>
        private void UpdateScenarioDescription(AppScenario scenario)
        {
            try
            {
                switch (scenario)
                {
                    case AppScenario.Local:
                        ScenarioDescriptionText.Text = "Local scenario: Optimized for local TTS and voice processing. Discord bot disabled, moderate confidence thresholds, CPU processing for stability.";
                        break;
                    case AppScenario.Discord:
                        ScenarioDescriptionText.Text = "Discord scenario: Full bot integration enabled with voice commands. Higher confidence thresholds, optimized for Discord voice activity detection.";
                        break;
                    case AppScenario.Kiosk:
                        ScenarioDescriptionText.Text = "Kiosk scenario: Public-facing mode with AI disabled for privacy. High accuracy thresholds, stable CPU processing, telemetry disabled.";
                        break;
                    default:
                        ScenarioDescriptionText.Text = "Select a scenario to see its description.";
                        break;
                }
            }
            catch (Exception ex)
            {
                ScenarioDescriptionText.Text = $"Error updating description: {ex.Message}";
            }
        }

        /// <summary>
        /// Refresh validation results
        /// </summary>
        private void RefreshValidationResults()
        {
            try
            {
                var validationIssues = AppSettings.Validate();
                ValidationResultsList.ItemsSource = validationIssues;
                
                var issueCount = validationIssues.Count(issue => issue.StartsWith("❌") || issue.StartsWith("⚠️"));
                if (issueCount == 0)
                {
                    DiagnosticsStatusText.Text = "✅ All validation checks passed";
                }
                else
                {
                    DiagnosticsStatusText.Text = $"Found {issueCount} configuration issues";
                }
            }
            catch (Exception ex)
            {
                ValidationResultsList.ItemsSource = new[] { $"❌ Validation failed: {ex.Message}" };
                DiagnosticsStatusText.Text = $"Error during validation: {ex.Message}";
                Console.WriteLine($"Error refreshing validation results: {ex.Message}");
            }
        }

        /// <summary>
        /// Refresh full diagnostics report
        /// </summary>
        private void RefreshDiagnosticsReport()
        {
            try
            {
                var report = AppSettings.GetDiagnosticsReport();
                DiagnosticsReportTextBox.Text = report;
            }
            catch (Exception ex)
            {
                DiagnosticsReportTextBox.Text = $"❌ ERROR: Could not generate diagnostics report: {ex.Message}";
                Console.WriteLine($"Error refreshing diagnostics report: {ex.Message}");
            }
        }

        #endregion

    }
}