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
        private bool _isDarkMode = true;

        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private bool _isClosing = false;

        // Latest RMS values (pull-model)
        private volatile float _latestRmsValue = 0f;
        private volatile float _latestDiscordRmsValue = 0f;
        private volatile float _latestWebRtcRmsValue = 0f;

        // Discord initialization protection
        private static int _discordInitInProgress = 0;

        // Audio input settings
        private bool _isMicrophoneInputEnabled = true;
        private bool _isDiscordInputEnabled = false;
        private bool _isWebRtcInputEnabled = false;
        private AudioInMode _currentAudioMode = AudioInMode.LocalMic;
        private bool _updatingAudioMode = false;

        // UI control placeholders (moved to Settings tab or removed)
        private ComboBox ToneComboBox = new ComboBox();
        private TextBox TestOllamaPromptTextBox = new TextBox();
        private TextBox TtsTestTextBox = new TextBox();
        private ComboBox TtsModelComboBox = new ComboBox();
        private ComboBox TtsSpeakerComboBox = new ComboBox();
        private TextBlock TtsModelStatusText = new TextBlock();
        private Button TtsGpuToggleButton = new Button();
        
        // Removed UI elements (combined into single RMS meter)
        private ProgressBar DiscordRmsBar = new ProgressBar();
        private TextBlock DiscordRmsText = new TextBlock();
        private ProgressBar WebRtcRmsBar = new ProgressBar();
        private TextBlock WebRtcRmsText = new TextBlock();

        // Embedded settings window host
        private UI.Settings.SettingsWindow _embeddedSettingsWindow;

        // RMS Visualization
        private DispatcherTimer _rmsUiTimer;
        private SolidColorBrush _rmsGreenBrush, _rmsOrangeBrush, _rmsRedBrush;
        private SolidColorBrush _discordLowBrush, _discordMidBrush, _discordHighBrush;
        private double _lastMicPct = -1, _lastDiscordPct = -1, _lastWebRtcPct = -1;
        private int _lastMicBucket = -1, _lastDiscordBucket = -1, _lastWebRtcBucket = -1;
        private float _smoothedRms = 0f;
        private float _smoothedDiscordRms = 0f;
        private float _smoothedWebRtcRms = 0f;
        private DateTime _lastMicRmsTime = DateTime.MinValue;
        private DateTime _lastDiscordRmsTime = DateTime.MinValue;
        private DateTime _lastWebRtcRmsTime = DateTime.MinValue;

        private double _localTtsVolume = 1.0;
        private double _discordTtsVolume = 1.0;

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
                this.Title = "Voice Recognition & AI Assistant";

                // Hook GUI events
                try
                {
                    VoiceRecognizer.OnTranscription += UpdateTranscription;
                    VoiceRecognizer.OnRmsLevel += UpdateRmsLevel;
                    VoiceRecognizer.OnDiscordRmsLevel += UpdateDiscordRmsLevel;
                    VoiceRecognizer.OnSpeakerResolvedForOllama += ShowSpeakerResolvedForOllama;
                    VoiceRecognizer.OnVoiceEmbedding += OnVoiceEmbedding;

                    // Voice enrollment events -> UI
                    try
                    {
                        VoiceEnrollmentManager.OnEnrollmentProgress -= UpdateVoiceEnrollmentProgress;
                        VoiceEnrollmentManager.OnEnrollmentComplete -= OnVoiceEnrollmentComplete;
                        VoiceEnrollmentManager.OnEnrollmentCancelled -= OnVoiceEnrollmentCancelled;
                    }
                    catch { }

                    VoiceEnrollmentManager.OnEnrollmentProgress += UpdateVoiceEnrollmentProgress;
                    VoiceEnrollmentManager.OnEnrollmentComplete += OnVoiceEnrollmentComplete;
                    VoiceEnrollmentManager.OnEnrollmentCancelled += OnVoiceEnrollmentCancelled;

                    // Ensure final-only UI and LLM dispatch wiring
                    WireTranscriptionEvents();
                    WireDispatchPipeline();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook VoiceRecognizer events: {ex.Message}");
                }

                // Hook LLM/Ollama events for streaming response and TTS
                try
                {
                    OllamaService.OnResponseChunk += OnOllamaResponseChunk;
                    OllamaService.OnResponseSentenceReady += OnOllamaResponseSentenceReady;
                    OllamaService.OnResponseReceived += OnOllamaResponseReceived;
                    OllamaService.OnError += OnOllamaError;
                    OllamaService.OnPromptSent += OnOllamaPromptSent;
                    Console.WriteLine("OllamaService events hooked (streaming TTS enabled)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not hook OllamaService events: {ex.Message}");
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

                // Initialize embedded settings into the Settings tab
                InitializeEmbeddedSettings();

                // Initialize optimized RMS visualization pull model
                InitializeRmsVisualizer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MainWindow initialization failed: {ex.Message}");
                this.Title = "Voice Recognition & AI Assistant - Error";

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
                _rmsGreenBrush = (TryFindResource("AccentGreen") as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(34, 197, 94));
                _rmsOrangeBrush = (TryFindResource("AccentOrange") as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(245, 158, 11));
                _rmsRedBrush = (TryFindResource("AccentRed") as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(239, 68, 68));

                _discordLowBrush = (TryFindResource("AccentBlue") as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(59, 130, 246));
                _discordMidBrush = (TryFindResource("AccentPurple") as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(139, 92, 246));
                _discordHighBrush = (TryFindResource("AccentOrange") as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(245, 158, 11));

                // Ensure initial color is set
                if (RmsBar != null)
                {
                    RmsBar.Foreground = _rmsGreenBrush;
                }

                _rmsUiTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS for snappy response
                };
                _rmsUiTimer.Tick += (s, e) => RmsUiTick();
                _rmsUiTimer.Start();
                
                Console.WriteLine("[RMS] Visualizer initialized, timer started at 60fps");
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

            // Determine which source is active and get the appropriate RMS value
            float inputRms = 0f;
            bool isStale = false;
            SolidColorBrush accentBrush = _rmsGreenBrush;

            if (_isMicrophoneInputEnabled)
            {
                isStale = (now - _lastMicRmsTime).TotalMilliseconds > 100; // Faster stale detection
                inputRms = isStale ? 0f : _latestRmsValue;
                accentBrush = _rmsGreenBrush;
            }
            else if (_isDiscordInputEnabled)
            {
                isStale = (now - _lastDiscordRmsTime).TotalMilliseconds > 100;
                inputRms = isStale ? 0f : _latestDiscordRmsValue;
                accentBrush = _discordLowBrush;
            }
            else if (_isWebRtcInputEnabled)
            {
                isStale = (now - _lastWebRtcRmsTime).TotalMilliseconds > 150;
                inputRms = isStale ? 0f : _latestWebRtcRmsValue;
                accentBrush = (TryFindResource("AccentPurple") as SolidColorBrush) ?? new SolidColorBrush(Colors.Purple);
            }

            // Fast attack, moderate decay smoothing for responsive but smooth meter
            if (inputRms > _smoothedRms)
            {
                // Fast attack - jump quickly to loud sounds
                _smoothedRms = _smoothedRms * 0.15f + inputRms * 0.85f;
            }
            else
            {
                // Moderate decay - fall naturally
                _smoothedRms = _smoothedRms * 0.7f + inputRms * 0.3f;
            }

            // Accelerated decay when stale
            if (isStale && inputRms == 0f)
            {
                _smoothedRms *= 0.6f; // Faster decay when no input
                if (_smoothedRms < 10f) _smoothedRms = 0f;
            }

            var pct = Math.Max(0.0, Math.Min(100.0, (_smoothedRms / 10000.0) * 100.0));

            // Update UI - always update to ensure responsiveness
            if (RmsBar != null && RmsText != null)
            {
                RmsBar.Value = pct;
                RmsText.Text = $"{(int)pct}%";
                _lastMicPct = pct;

                // Update color based on level for mic, keep source color for others
                if (_isMicrophoneInputEnabled)
                {
                    int bucket = (pct <= 33) ? 0 : (pct <= 66 ? 1 : 2);
                    if (bucket != _lastMicBucket)
                    {
                        _lastMicBucket = bucket;
                        RmsBar.Foreground = bucket == 0 ? _rmsGreenBrush : bucket == 1 ? _rmsOrangeBrush : _rmsRedBrush;
                    }
                }
                else if (RmsBar.Foreground != accentBrush)
                {
                    RmsBar.Foreground = accentBrush;
                }
            }
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
                    // Dark theme
                    this.Resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(18, 18, 18));
                    this.Resources["SurfaceBackground"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                    this.Resources["SurfaceBackgroundLight"] = new SolidColorBrush(Color.FromRgb(42, 42, 42));
                    this.Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(58, 58, 58));
                    this.Resources["TextPrimary"] = new SolidColorBrush(Colors.White);
                    this.Resources["TextSecondary"] = new SolidColorBrush(Color.FromRgb(176, 176, 176));
                    this.Resources["TextMuted"] = new SolidColorBrush(Color.FromRgb(112, 112, 112));

                    EnrollNameBox.Foreground = Brushes.White;
                    TtsTestTextBox.Foreground = Brushes.White;
                    EnrollNameBox.CaretBrush = Brushes.White;
                    TtsTestTextBox.CaretBrush = Brushes.White;
                }
                else
                {
                    // Light theme
                    this.Resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(250, 250, 250));
                    this.Resources["SurfaceBackground"] = new SolidColorBrush(Colors.White);
                    this.Resources["SurfaceBackgroundLight"] = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                    this.Resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(229, 229, 229));
                    this.Resources["TextPrimary"] = new SolidColorBrush(Color.FromRgb(23, 23, 23));
                    this.Resources["TextSecondary"] = new SolidColorBrush(Color.FromRgb(82, 82, 82));
                    this.Resources["TextMuted"] = new SolidColorBrush(Color.FromRgb(140, 140, 140));

                    EnrollNameBox.Foreground = Brushes.Black;
                    TtsTestTextBox.Foreground = Brushes.Black;
                    EnrollNameBox.CaretBrush = Brushes.Black;
                    TtsTestTextBox.CaretBrush = Brushes.Black;
                }

                // Accent colors (same for both)
                this.Resources["AccentBlue"] = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                this.Resources["AccentGreen"] = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                this.Resources["AccentOrange"] = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                this.Resources["AccentRed"] = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                this.Resources["AccentPurple"] = new SolidColorBrush(Color.FromRgb(139, 92, 246));

                if (ThemeToggleButton != null)
                {
                    ThemeToggleButton.Content = isDarkMode ? "☀️" : "🌙";
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
            _lastMicRmsTime = DateTime.UtcNow;
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

                    var display = string.IsNullOrWhiteSpace(speakerName) ? "Unknown" : speakerName;
                    var score = Math.Max(0f, Math.Min(1f, confidence));

                    // Compact display format
                    SpeakerLabel.Content = score >= 0.5f ? display : $"{display} ({score:P0})";

                    if (OllamaStatusText != null)
                    {
                        OllamaStatusText.Text = $"Speaking as {display}";
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

            _lastStreamingTtsActive = DateTime.UtcNow;

            try
            {
                var ttsEnabled = false;
                try { ttsEnabled = Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { }
                if (!ttsEnabled) return;

                var voice = App.SettingsProvider?.Current?.Tts?.Speaker;

                var speakLocal = _isMicrophoneInputEnabled;
                var speakWebRtc = _isWebRtcInputEnabled;
                var speakDiscord = _isDiscordInputEnabled && Kinectv1.Discord.DiscordNetBotManager.IsInVoiceChannel;

                // Avoid log spam; routing is visible in UI and WebRTC diagnostics.

                if (speakLocal)
                {
                    TtsService.QueueSentenceForStreaming(sentence, voice, webRtcOnly: false);
                }
                else if (speakWebRtc)
                {
                    TtsService.QueueSentenceForStreaming(sentence, voice, webRtcOnly: true);
                }

                if (speakDiscord)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await Kinectv1.Discord.DiscordNetBotManager.SendTtsToDiscordAsync(sentence, voice); }
                        catch (Exception ex) { Console.WriteLine($"Discord streaming TTS error: {ex.Message}"); }
                    });
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

                var timeSinceStreaming = (DateTime.UtcNow - _lastStreamingTtsActive).TotalMilliseconds;
                bool streamingWasUsed = timeSinceStreaming < 30000;

                if (streamingWasUsed)
                {
                    // Avoid spam: this is expected in streaming mode.
                    return;
                }

                if (!TtsService.IsStreamingPlaybackActive)
                {
                    var ttsEnabled = false;
                    try { ttsEnabled = Kinectv1.App.SettingsProvider?.Current?.Tts?.Enabled ?? false; } catch { }
                    if (!ttsEnabled) return;

                    var voice = App.SettingsProvider?.Current?.Tts?.Speaker;

                    var speakLocal = _isMicrophoneInputEnabled;
                    var speakWebRtc = _isWebRtcInputEnabled;
                    var speakDiscord = _isDiscordInputEnabled && Kinectv1.Discord.DiscordNetBotManager.IsInVoiceChannel;

                    if (speakLocal)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await TtsService.SpeakWithPreemptionAsync(response, voice); }
                            catch (Exception ex) { Console.WriteLine($"TTS speak error: {ex.Message}"); }
                        });
                    }
                    else if (speakWebRtc)
                    {
                        TtsService.QueueSentenceForStreaming(response, voice, webRtcOnly: true);
                    }

                    if (speakDiscord)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await Kinectv1.Discord.DiscordNetBotManager.SendTtsToDiscordAsync(response, voice); }
                            catch (Exception ex) { Console.WriteLine($"Discord TTS error: {ex.Message}"); }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OnOllamaResponseReceived error: {ex.Message}");
            }
        }

        private void OnWebUiModeChangeRequested(AudioInMode mode)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // This can fire frequently if the page reconnects; keep quiet unless debugging.
                    PersistAudioMode(mode);
                    ApplyAudioMode(mode);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebUI] Mode change error: {ex.Message}");
                }
            }));
        }

        private void OnWebUiTextInput(string speaker, string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // Avoid console spam on every WebUI keystroke/message.

                    if (TranscriptionLabel != null)
                    {
                        TranscriptionLabel.Content = text;
                    }

                    try { ShowSpeakerResolvedForOllama(speaker, 1.0f, "webui"); } catch { }

                    if (OllamaStatusText != null)
                    {
                        OllamaStatusText.Text = "🤖 Ollama: Processing (from WebUI)...";
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebUI] Text input UI update error: {ex.Message}");
                }
            }));
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
            VoiceRecognizer.OnPartialTranscription += OnPartialTranscription;
            VoiceRecognizer.OnSpeakerResolvedForOllama -= ShowSpeakerResolvedForOllama;
            VoiceRecognizer.OnSpeakerResolvedForOllama += ShowSpeakerResolvedForOllama;
        }

        private void OnPartialTranscription(string text)
        {
            if (_isClosing) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosing) return;
                    if (PartialTranscriptionLabel != null)
                    {
                        // Show partial text with ellipsis to indicate it's still listening
                        PartialTranscriptionLabel.Content = string.IsNullOrWhiteSpace(text) ? "" : $"» {text}...";
                    }
                }), DispatcherPriority.Background);
            }
            catch { }
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
                    // Clear partial when final arrives
                    if (PartialTranscriptionLabel != null)
                        PartialTranscriptionLabel.Content = "";
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
            // Check if transcription mode is enabled
            var transcriptionCfg = App.SettingsProvider?.Current?.Transcription;
            bool transcriptionModeEnabled = transcriptionCfg?.Enabled ?? false;

            // Capture the current voice embedding for diarization
            float[] currentEmbedding = null;
            try { currentEmbedding = _lastVoiceEmbedding != null ? (float[])_lastVoiceEmbedding.Clone() : null; } catch { }

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

                    // If transcription mode is enabled, log to TranscriptionService and skip LLM
                    if (transcriptionModeEnabled)
                    {
                        try
                        {
                            var transcriptionService = Services.Transcription.TranscriptionService.Instance;
                            
                            // Auto-start session if not active and logToFile is enabled
                            if (!transcriptionService.IsActive && (transcriptionCfg?.LogToFile ?? false))
                            {
                                transcriptionService.StartSession();
                            }
                            
                            if (transcriptionService.IsActive)
                            {
                                // Pass embedding for diarization
                                transcriptionService.AddTranscription(speaker, text, currentEmbedding);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Transcription] Failed to log: {ex.Message}");
                        }
                        
                        // Skip LLM dispatch when in transcription mode
                        return;
                    }

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
        private void EnrollVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var name = EnrollNameBox?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    // Avoid dumping large console instructions on UI thread.
                    // Just no-op if the name is missing.
                    return;
                }

                // Start enrollment on a background thread to guarantee the UI never blocks
                // even if downstream logging is slow.
                _ = Task.Run(() =>
                {
                    try { VoiceEnrollmentManager.StartEnrollment(name); } catch { }
                });

                // Make progress UI visible immediately
                if (VoiceEnrollmentPanel != null) VoiceEnrollmentPanel.Visibility = Visibility.Visible;
                if (VoiceEnrollProgress != null)
                {
                    VoiceEnrollProgress.Maximum = VoiceEnrollmentManager.GetRequiredSamples();
                    VoiceEnrollProgress.Value = 0;
                }
                if (VoiceProgressText != null)
                    VoiceProgressText.Text = $"0/{VoiceEnrollmentManager.GetRequiredSamples()}";
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

        private void FlushVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Cancel any enrollment in progress first
                try { VoiceEnrollmentManager.CancelEnrollment(); } catch { }

                SpeakerIdentifier.ClearAll();

                // Reset UI state
                try
                {
                    if (VoiceEnrollmentPanel != null) VoiceEnrollmentPanel.Visibility = Visibility.Collapsed;
                    if (VoiceEnrollProgress != null)
                    {
                        VoiceEnrollProgress.Value = 0;
                        VoiceEnrollProgress.Maximum = VoiceEnrollmentManager.GetRequiredSamples();
                    }
                    if (VoiceProgressText != null)
                        VoiceProgressText.Text = $"0/{VoiceEnrollmentManager.GetRequiredSamples()}";
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FlushVoiceButton_Click failed: {ex.Message}");
            }
        }

        private void ToggleOllamaButton_Click(object sender, RoutedEventArgs e) { }
        private void OllamaModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void RefreshModelsButton_Click(object sender, RoutedEventArgs e) { }
        private void ToggleTtsButton_Click(object sender, RoutedEventArgs e) { }
        private void TestTtsButton_Click(object sender, RoutedEventArgs e) { }
        private void RefreshTtsModelsButton_Click(object sender, RoutedEventArgs e) { }

        /// <summary>
        /// Start WebRTC web server at app startup (always on).
        /// Voice audio only processed when WebRTC input mode is active.
        /// </summary>
        private void StartWebRtcServerAlways()
        {
            try
            {
                var cfg = App.SettingsProvider?.Current?.WebRtc;
                if (cfg == null || !cfg.Enabled)
                {
                    Console.WriteLine("[WebRTC] Server not started (disabled in settings)");
                    return;
                }

                if (_webRtcCts != null && !_webRtcCts.IsCancellationRequested)
                {
                    Console.WriteLine("[WebRTC] Server already running");
                    return;
                }

                EnsureWebRtcWiring();

                _webRtcCts = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _webRtcTransport.StartAsync(_webRtcCts.Token);
                        Console.WriteLine($"[WebRTC] Web server started. Join URL: {_webRtcTransport.GetJoinUrl()}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WebRTC] Server start failed: {ex.Message}");
                    }
                });

                _webRtcSttTask = Task.Run(() => WebRtcSttWorker(_webRtcCts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC] StartWebRtcServerAlways failed: {ex.Message}");
            }
        }

        private void OnWebRtcInboundAudio(AudioFrame frame)
        {
            if (_isClosing) return;

            // Always update UI meter (even when not in WebRTC mode)
            try
            {
                float rms = ComputeRmsFromPcm16(frame.Pcm16);
                _latestWebRtcRmsValue = rms;
                _lastWebRtcRmsTime = DateTime.UtcNow;
            }
            catch { }

            // Only feed STT when WebRTC mode is active
            if (!_isWebRtcInputEnabled)
            {
                return;
            }

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

        private void ApplyAudioMode(AudioInMode mode)
        {
            _updatingAudioMode = true;
            try
            {
                _currentAudioMode = mode;
                _isMicrophoneInputEnabled = (mode == AudioInMode.LocalMic);
                _isDiscordInputEnabled = (mode == AudioInMode.DiscordVoice);
                _isWebRtcInputEnabled = (mode == AudioInMode.WebRtcVoice);

                // Avoid spam: mode changes already update UI.

                try { WebRtcSignalingServer.BroadcastModeChange((int)mode); } catch { }

                if (ActiveSourceLabel != null && RmsBar != null)
                {
                    if (_isMicrophoneInputEnabled)
                    {
                        ActiveSourceLabel.Text = "🎤 Mic";
                        RmsBar.Foreground = _rmsGreenBrush ?? new SolidColorBrush(Colors.Green);
                    }
                    else if (_isDiscordInputEnabled)
                    {
                        ActiveSourceLabel.Text = "💬 Discord";
                        RmsBar.Foreground = _discordLowBrush ?? new SolidColorBrush(Colors.SteelBlue);
                    }
                    else if (_isWebRtcInputEnabled)
                    {
                        ActiveSourceLabel.Text = "📱 WebRTC";
                        RmsBar.Foreground = (TryFindResource("AccentPurple") as SolidColorBrush) ?? new SolidColorBrush(Colors.Purple);
                    }
                }

                _smoothedRms = 0f;
                _lastMicPct = -1;
                _lastMicBucket = -1;

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

                if (_isDiscordInputEnabled)
                {
                    try { VoiceRecognizer.SetMicrophoneInputEnabled(false); } catch { }
                    try { VoiceRecognizer.SetWebRtcInputEnabled(false); } catch { }
                    try { TtsService.CancelCurrentLocalTts(); } catch { }
                }
                else if (_isWebRtcInputEnabled)
                {
                    _ = Task.Run(async () => { try { await DiscordNetBotManager.LeaveAllVoiceAsync(); } catch { } });
                    try { VoiceRecognizer.SetMicrophoneInputEnabled(false); } catch { }
                    try { VoiceRecognizer.SetDiscordInputEnabled(false); } catch { }
                    try { VoiceRecognizer.SetWebRtcInputEnabled(true); } catch { }

                    try
                    {
                        var svc = App.SettingsProvider; var curr = svc?.Current;
                        if (svc != null && curr != null)
                        {
                            var nextWebRtc = new WebRtcSettings(
                                Enabled: true,
                                Port: curr.WebRtc.Port,
                                HttpsEnabled: curr.WebRtc.HttpsEnabled,
                                HttpsPort: curr.WebRtc.HttpsPort
                            );
                            var next = curr with { WebRtc = nextWebRtc };
                            svc.Save(next);
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Persist WebRTC enable failed: {ex.Message}"); }

                    if (_webRtcCts == null || _webRtcCts.IsCancellationRequested)
                    {
                        StartWebRtcServerAlways();
                    }
                }
                else
                {
                    _ = Task.Run(async () => { try { await DiscordNetBotManager.LeaveAllVoiceAsync(); } catch { } });
                    try { VoiceRecognizer.SetMicrophoneInputEnabled(true); } catch { }
                }

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
            int totalSamplesProcessed = 0;
            float peakRms = 0f;
            
            // Track sample rate stats
            int frames16k = 0;
            int framesOther = 0;

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
                    
                    // Track sample rate distribution
                    if (sr == 16000) frames16k++;
                    else framesOther++;

                    byte[] pcm16k;

                    // Handle mono input at various sample rates
                    if (ch == 1)
                    {
                        if (sr == 16000)
                        {
                            // Already at target rate - just convert
                            pcm16k = ShortsToBytes(pcm);
                        }
                        else
                        {
                            // Resample using proper anti-aliasing (float domain)
                            var floatMono = ShortsToFloats(pcm);
                            var resampled = ResampleMonoWithFilter(floatMono, sr, 16000, frame.SourceId);
                            pcm16k = FloatsToPcm16Bytes(resampled);
                        }
                    }
                    else
                    {
                        // Stereo: mix to mono in float domain, then resample
                        var floatStereo = ShortsToFloats(pcm);
                        var floatMono = MixStereoToMonoFloat(floatStereo);
                        
                        if (sr == 16000)
                        {
                            pcm16k = FloatsToPcm16Bytes(floatMono);
                        }
                        else
                        {
                            var resampled = ResampleMonoWithFilter(floatMono, sr, 16000, frame.SourceId);
                            pcm16k = FloatsToPcm16Bytes(resampled);
                        }
                    }

                    if (pcm16k != null && pcm16k.Length > 0)
                    {
                        // IMPORTANT: Keep speaker embedding input unmodified.
                        // The speaker embedder relies on consistent windowing/normalization; mutating
                        // the same buffer in-place can break its rolling window behavior.
                        var pcmForVosk = new byte[pcm16k.Length];
                        Buffer.BlockCopy(pcm16k, 0, pcmForVosk, 0, pcm16k.Length);

                        // Apply audio normalization to boost quiet WebRTC audio for better Vosk recognition
                        // This uses smooth AGC that won't cause artifacts
                        float gainApplied = AudioUtils.NormalizePcm16InPlace(pcmForVosk, pcmForVosk.Length, frame.SourceId ?? "webrtc");

                        totalSamplesProcessed += pcmForVosk.Length / 2;

                        // Compute RMS for diagnostics (after normalization)
                        float rms = NormalizedAudioFrame.ComputeRms(pcmForVosk, pcmForVosk.Length);
                        if (rms > peakRms) peakRms = rms;

                        // Update UI meter to reflect the amplified (post-normalization) audio.
                        // The transport callback shows raw inbound RMS which can be misleading when AGC is enabled.
                        _latestWebRtcRmsValue = rms;
                        _lastWebRtcRmsTime = DateTime.UtcNow;

                        var normalizedFrame = Kinectv1.Voice.NormalizedAudioFrame.Create(
                            pcmForVosk, pcmForVosk.Length,
                            Kinectv1.Voice.AudioSourceType.WebRtc,
                            frame.SourceId);
                        VoiceRecognizer.ProcessAudio(normalizedFrame);
                    }
                }
                catch { }

                var now = DateTime.UtcNow;
                if ((now - lastLog).TotalSeconds >= 5)
                {
                    // Log sample rate distribution every 5 seconds
                    if (frames16k > 0 || framesOther > 0)
                    {
                        Console.WriteLine($"[WebRTC-STT] SampleRate distribution: 16kHz={frames16k}, other={framesOther} (total {frames} frames)");
                    }
                    frames = 0;
                    frames16k = 0;
                    framesOther = 0;
                    totalSamplesProcessed = 0;
                    peakRms = 0f;
                    lastLog = now;
                }
            }
        }

        /// <summary>
        /// Mix stereo float audio to mono by averaging L+R channels.
        /// </summary>
        private static float[] MixStereoToMonoFloat(float[] stereo)
        {
            if (stereo == null || stereo.Length == 0) return Array.Empty<float>();
            var mono = new float[stereo.Length / 2];
            for (int i = 0; i < mono.Length; i++)
            {
                mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
            }
            return mono;
        }

        /// <summary>
        /// Convert short[] PCM to float[] in range [-1, 1].
        /// </summary>
        private static float[] ShortsToFloats(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return Array.Empty<float>();
            var floats = new float[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
            {
                floats[i] = pcm[i] / 32768f;
            }
            return floats;
        }

        // Per-source low-pass filter state for proper anti-aliasing during resampling
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, NAudio.Dsp.BiQuadFilter> _webRtcResampleFilters 
            = new System.Collections.Concurrent.ConcurrentDictionary<string, NAudio.Dsp.BiQuadFilter>();

        /// <summary>
        /// Resample mono float audio with proper anti-aliasing filter.
        /// Uses low-pass filter before decimation to prevent aliasing artifacts.
        /// </summary>
        private static float[] ResampleMonoWithFilter(float[] input, int srcRate, int dstRate, string sourceId)
        {
            if (input == null || input.Length == 0 || srcRate <= 0 || dstRate <= 0)
                return Array.Empty<float>();

            if (srcRate == dstRate) return input;

            // Get or create anti-aliasing low-pass filter for this source
            // Cutoff at Nyquist of target rate (8kHz for 16kHz target) with some headroom
            var filterKey = $"{sourceId ?? "default"}_{srcRate}_{dstRate}";
            var lpf = _webRtcResampleFilters.GetOrAdd(filterKey, _ => 
                NAudio.Dsp.BiQuadFilter.LowPassFilter(srcRate, dstRate * 0.45f, 0.707f));

            // Apply anti-aliasing filter
            var filtered = new float[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                filtered[i] = lpf.Transform(input[i]);
            }

            // Calculate output length
            double ratio = (double)srcRate / dstRate;
            int outputLength = (int)(input.Length / ratio);
            if (outputLength <= 0) return Array.Empty<float>();

            var output = new float[outputLength];
            
            // Linear interpolation on filtered signal
            for (int i = 0; i < outputLength; i++)
            {
                double srcIndex = i * ratio;
                int idx0 = (int)srcIndex;
                int idx1 = Math.Min(idx0 + 1, filtered.Length - 1);
                double frac = srcIndex - idx0;

                output[i] = (float)(filtered[idx0] * (1.0 - frac) + filtered[idx1] * frac);
            }

            return output;
        }

        /// <summary>
        /// Mix stereo PCM16 to mono by averaging L+R channels.
        /// </summary>
        private static short[] MixToMono(short[] stereo)
        {
            if (stereo == null || stereo.Length == 0) return Array.Empty<short>();
            var mono = new short[stereo.Length / 2];
            for (int i = 0; i < mono.Length; i++)
            {
                int left = stereo[i * 2];
                int right = stereo[i * 2 + 1];
                mono[i] = (short)((left + right) / 2);
            }
            return mono;
        }

        /// <summary>
        /// Resample mono PCM16 from srcRate to dstRate using linear interpolation.
        /// </summary>
        private static short[] ResampleMono(short[] input, int srcRate, int dstRate)
        {
            if (input == null || input.Length == 0 || srcRate <= 0 || dstRate <= 0) 
                return Array.Empty<short>();
            
            if (srcRate == dstRate) return input;

            double ratio = (double)srcRate / dstRate;
            int outputLength = (int)(input.Length / ratio);
            if (outputLength <= 0) return Array.Empty<short>();

            var output = new short[outputLength];
            for (int i = 0; i < outputLength; i++)
            {
                double srcIndex = i * ratio;
                int idx0 = (int)srcIndex;
                int idx1 = Math.Min(idx0 + 1, input.Length - 1);
                double frac = srcIndex - idx0;
                
                // Linear interpolation
                double sample = input[idx0] * (1.0 - frac) + input[idx1] * frac;
                output[i] = (short)Math.Max(-32768, Math.Min(32767, sample));
            }
            return output;
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

        // --- Minimal event handlers (avoid console spam by default) ---
        private void OnOllamaError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return;
            try { Console.WriteLine(error); } catch { }
        }

        private void OnTtsSpeakingStarted() { }
        private void OnTtsSpeakingFinished() { }
        private void OnTtsError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return;
            try { Console.WriteLine($"[TTS] {error}"); } catch { }
        }

        private void OnDiscordBotStatusChanged(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return;
            try { Console.WriteLine(status); } catch { }
        }

        private void OnDiscordVoiceMessageReceived(string speaker, string message) { }

        private void OnDiscordBotError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return;
            try { Console.WriteLine(error); } catch { }
        }

        private void InitializeAudioInputControls()
        {
            try
            {
                var modeJson = App.SettingsProvider?.Current?.App?.InputMode;
                var mode = modeJson.HasValue ? (AudioInMode)modeJson.Value : AudioInMode.LocalMic;

                var sttModelPath = App.SettingsProvider?.Current?.Stt?.ModelPath ?? string.Empty;
                VoiceRecognizer.Start(sttModelPath);

                // Load WebRTC normalization settings from config
                AudioUtils.LoadNormalizationSettingsFromConfig();

                EnsureWebRtcWiring();
                StartWebRtcServerAlways();

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
                try { Console.WriteLine($"[WebRTC] State changed: {state}"); } catch { }
            };

            WebRtcSignalingServer.OnModeChangeRequested += OnWebUiModeChangeRequested;
            WebRtcSignalingServer.OnWebTextInput += OnWebUiTextInput;
        }
    }
}