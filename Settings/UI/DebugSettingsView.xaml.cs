using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kinectv1.Services.Debug;
using Kinectv1.Settings;
using WinForms = System.Windows.Forms;

namespace Kinectv1.UI.Settings
{
    public partial class DebugSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;
        private DispatcherTimer _refreshTimer;
        private float _currentRms;
        private float _peakRms;
        private bool _suppressDspEvents;
        private const double MeterMaxWidth = 450.0;
        private const float RmsScaleMax = 5000f;

        // Track VAD diagnostic states (not persisted - runtime only)
        private bool _rmsLoggingEnabled = false;
        private bool _vadBypassEnabled = false;
        private bool _verboseDiagEnabled = false;

        // Normalization control references (populated via FindName for compatibility)
        private CheckBox _normalizationEnabledCheckBox;
        private TextBox _normalizationTargetRmsTextBox;
        private TextBox _normalizationMaxGainTextBox;
        private TextBlock _normalizationStatusText;

        public DebugSettingsView()
        {
            InitializeComponent();
            Loaded += DebugSettingsView_Loaded;
            Unloaded += DebugSettingsView_Unloaded;
        }

        private void DebugSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Find normalization controls by name (workaround for XAML code-gen sync issues)
                _normalizationEnabledCheckBox = FindName("NormalizationEnabledCheckBox") as CheckBox;
                _normalizationTargetRmsTextBox = FindName("NormalizationTargetRmsTextBox") as TextBox;
                _normalizationMaxGainTextBox = FindName("NormalizationMaxGainTextBox") as TextBox;
                _normalizationStatusText = FindName("NormalizationStatusText") as TextBlock;

                var cfg = _svc?.Current?.Debug;
                AudioCaptureFolderTextBox.Text = cfg?.AudioCaptureFolder ?? "debug_audio";
                
                // Load WebRTC DSP settings
                _suppressDspEvents = true;
                if (EchoCancellationCheckBox != null)
                    EchoCancellationCheckBox.IsChecked = cfg?.WebRtcEchoCancellation ?? false;
                if (NoiseSuppressionCheckBox != null)
                    NoiseSuppressionCheckBox.IsChecked = cfg?.WebRtcNoiseSuppression ?? false;
                if (AutoGainCheckBox != null)
                    AutoGainCheckBox.IsChecked = cfg?.WebRtcAutoGainControl ?? false;
                
                // Load WebRTC normalization settings
                if (_normalizationEnabledCheckBox != null)
                    _normalizationEnabledCheckBox.IsChecked = cfg?.WebRtcNormalizationEnabled ?? true;
                if (_normalizationTargetRmsTextBox != null)
                    _normalizationTargetRmsTextBox.Text = (cfg?.WebRtcNormalizationTargetRms ?? 3000f).ToString("F0");
                if (_normalizationMaxGainTextBox != null)
                    _normalizationMaxGainTextBox.Text = (cfg?.WebRtcNormalizationMaxGain ?? 8f).ToString("F1");
                
                _suppressDspEvents = false;
                
                // Initialize VAD diagnostic controls (runtime state, not persisted)
                if (RmsLoggingCheckBox != null)
                    RmsLoggingCheckBox.IsChecked = _rmsLoggingEnabled;
                if (VadBypassCheckBox != null)
                    VadBypassCheckBox.IsChecked = _vadBypassEnabled;
                if (VerboseDiagCheckBox != null)
                    VerboseDiagCheckBox.IsChecked = _verboseDiagEnabled;
                
                UpdateWebRtcDspStatus();
                UpdateNormalizationStatus();
                UpdateCaptureStatus();
                UpdateVadThreshold();
                UpdateVadSettingsDisplay();

                // Subscribe to capture events
                DebugAudioCapture.OnStatusChanged += OnCaptureStatusChanged;
                DebugAudioCapture.OnCaptureFileCreated += OnCaptureFileCreated;
                DebugAudioCapture.OnRmsLevel += OnRmsLevelChanged;

                // Also subscribe to VoiceRecognizer RMS
                VoiceRecognizer.OnRmsLevel += OnVoiceRecognizerRms;

                // Start refresh timer for UI updates
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _refreshTimer.Tick += RefreshTimer_Tick;
                _refreshTimer.Start();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Load error: {ex.Message}";
            }
        }

        private void DebugSettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                DebugAudioCapture.OnStatusChanged -= OnCaptureStatusChanged;
                DebugAudioCapture.OnCaptureFileCreated -= OnCaptureFileCreated;
                DebugAudioCapture.OnRmsLevel -= OnRmsLevelChanged;
                VoiceRecognizer.OnRmsLevel -= OnVoiceRecognizerRms;

                _refreshTimer?.Stop();
                _refreshTimer = null;
            }
            catch { }
        }

        private void OnRmsLevelChanged(float rms)
        {
            _currentRms = rms;
            if (rms > _peakRms) _peakRms = rms;
        }

        private void OnVoiceRecognizerRms(float rms)
        {
            _currentRms = rms;
            if (rms > _peakRms) _peakRms = rms;
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Update RMS display
                RmsValueText.Text = _currentRms.ToString("F0");
                PeakValueText.Text = _peakRms.ToString("F0");

                // Update level bar
                double levelWidth = Math.Min(MeterMaxWidth, (_currentRms / RmsScaleMax) * MeterMaxWidth);
                RmsLevelBar.Width = Math.Max(0, levelWidth);

                // Color the bar based on level
                if (_currentRms > 4000)
                    RmsLevelBar.Fill = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
                else if (_currentRms > 2000)
                    RmsLevelBar.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
                else
                    RmsLevelBar.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));

                // Update peak indicator position
                double peakPos = Math.Min(MeterMaxWidth, (_peakRms / RmsScaleMax) * MeterMaxWidth);
                PeakIndicator.Margin = new Thickness(peakPos + 2, 2, 0, 0);

                // Decay peak slowly
                _peakRms *= 0.995f;

                UpdateCaptureStatus();
                UpdateVadStatus();
            }
            catch { }
        }

        private void UpdateVadThreshold()
        {
            try
            {
                var audio = _svc?.Current?.Audio;
                if (audio != null)
                {
                    double threshold = audio.VoiceThreshold * 10000.0;
                    VadThresholdText.Text = threshold.ToString("F0");
                }
            }
            catch { }
        }

        private void UpdateVadSettingsDisplay()
        {
            try
            {
                var audio = _svc?.Current?.Audio;
                var asr = _svc?.Current?.Asr;
                if (audio == null) return;

                double threshold = audio.VoiceThreshold * 10000.0;
                double thresholdLow = threshold * 0.4;
                int debounceMs = asr?.VadDebounceTimeoutMs ?? 80;
                int silenceMs = asr?.VadSilenceTimeoutMs ?? 800;

                if (VadSettingsText != null)
                {
                    VadSettingsText.Text = $"Threshold: {threshold:F0}, Low: {thresholdLow:F0}, Debounce: {debounceMs}ms, Silence: {silenceMs}ms";
                }
            }
            catch { }
        }

        private void UpdateVadStatus()
        {
            try
            {
                var audio = _svc?.Current?.Audio;
                if (audio == null) return;

                double threshold = audio.VoiceThreshold * 10000.0;
                bool isAboveThreshold = _currentRms >= threshold;

                if (_vadBypassEnabled)
                {
                    VadStatusText.Text = "BYPASSED";
                    VadStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
                }
                else if (isAboveThreshold)
                {
                    VadStatusText.Text = "VOICE";
                    VadStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                }
                else
                {
                    VadStatusText.Text = "Silent";
                    VadStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                }
            }
            catch { }
        }

        private void RmsLoggingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                _rmsLoggingEnabled = RmsLoggingCheckBox?.IsChecked ?? false;
                VoiceRecognizer.EnableRmsLogging(_rmsLoggingEnabled);
                
                if (RmsLoggingStatusText != null)
                {
                    RmsLoggingStatusText.Text = _rmsLoggingEnabled ? "Logging to console..." : "Off";
                    RmsLoggingStatusText.Foreground = _rmsLoggingEnabled 
                        ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10))
                        : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                }

                if (_rmsLoggingEnabled)
                {
                    Console.WriteLine("[Debug] RMS logging enabled - watch console for [RMS] messages");
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void VadBypassCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                _vadBypassEnabled = VadBypassCheckBox?.IsChecked ?? false;
                VoiceRecognizer.SetVadBypass(_vadBypassEnabled);
                
                if (VadBypassStatusText != null)
                {
                    VadBypassStatusText.Text = _vadBypassEnabled ? "VAD BYPASSED - all audio to Vosk" : "VAD Active";
                    VadBypassStatusText.Foreground = _vadBypassEnabled 
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00))
                        : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                }

                if (VadDiagStatusText != null)
                {
                    VadDiagStatusText.Visibility = _vadBypassEnabled ? Visibility.Visible : Visibility.Collapsed;
                    VadDiagStatusText.Text = "?? VAD bypassed - all audio feeds directly to Vosk. This is for testing only.";
                }

                Console.WriteLine(_vadBypassEnabled 
                    ? "[Debug] VAD BYPASSED - all mic audio will go to Vosk (for testing)" 
                    : "[Debug] VAD restored to normal operation");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void VerboseDiagCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                _verboseDiagEnabled = VerboseDiagCheckBox?.IsChecked ?? false;
                VoiceRecognizer.EnableDiagnostics(_verboseDiagEnabled);
                
                if (VerboseDiagStatusText != null)
                {
                    VerboseDiagStatusText.Text = _verboseDiagEnabled ? "Verbose logging enabled" : "Off";
                    VerboseDiagStatusText.Foreground = _verboseDiagEnabled 
                        ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10))
                        : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                }

                Console.WriteLine(_verboseDiagEnabled 
                    ? "[Debug] Verbose VoiceRecognizer diagnostics enabled" 
                    : "[Debug] Verbose diagnostics disabled");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void NormalizationCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressDspEvents) return;
            UpdateNormalizationStatus();
            StatusText.Text = "Normalization settings changed (click Save to apply)";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
        }

        private void UpdateNormalizationStatus()
        {
            try
            {
                if (_normalizationEnabledCheckBox == null || _normalizationStatusText == null) return;

                bool enabled = _normalizationEnabledCheckBox.IsChecked ?? false;
                _normalizationStatusText.Text = enabled ? "Enabled - boosting quiet audio" : "Disabled";
                _normalizationStatusText.Foreground = enabled
                    ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10))
                    : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            }
            catch { }
        }

        private void UpdateWebRtcDspStatus()
        {
            try
            {
                if (EchoCancellationCheckBox == null || NoiseSuppressionCheckBox == null || 
                    AutoGainCheckBox == null || WebRtcDspStatusText == null) return;

                bool echo = EchoCancellationCheckBox.IsChecked ?? false;
                bool noise = NoiseSuppressionCheckBox.IsChecked ?? false;
                bool agc = AutoGainCheckBox.IsChecked ?? false;

                if (!echo && !noise && !agc)
                {
                    WebRtcDspStatusText.Text = "All DSP disabled (recommended for speakerphone). Clients use default URL.";
                }
                else if (echo && noise && agc)
                {
                    WebRtcDspStatusText.Text = "All DSP enabled. Clients should use: ?dsp=1";
                }
                else
                {
                    WebRtcDspStatusText.Text = "Custom DSP settings. WebRTC clients need to reconnect to apply.";
                }
            }
            catch { }
        }

        private void WebRtcDspCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressDspEvents) return;
            
            try
            {
                UpdateWebRtcDspStatus();
                StatusText.Text = "DSP settings changed (click Save to apply)";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
            }
            catch { }
        }

        private void OnCaptureStatusChanged(string status)
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = status;
                });
            }
            catch { }
        }

        private void OnCaptureFileCreated(string filePath)
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    CurrentFileText.Text = Path.GetFileName(filePath);
                });
            }
            catch { }
        }

        private void UpdateCaptureStatus()
        {
            try
            {
                if (DebugAudioCapture.IsCapturing)
                {
                    CaptureStatusText.Text = "Recording";
                    CaptureStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
                    FrameCountText.Text = DebugAudioCapture.FrameCount.ToString();
                    CurrentFileText.Text = !string.IsNullOrEmpty(DebugAudioCapture.CurrentFilePath)
                        ? Path.GetFileName(DebugAudioCapture.CurrentFilePath)
                        : "--";
                    StartStopCaptureButton.Content = "Stop Capture";

                    if (DebugAudioCapture.IsVadActive)
                    {
                        CaptureVadStatusText.Text = "Writing";
                        CaptureVadStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                    }
                    else
                    {
                        CaptureVadStatusText.Text = "Waiting...";
                        CaptureVadStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                    }

                    int zeroFrames = DebugAudioCapture.ZeroFrameCount;
                    int totalFrames = DebugAudioCapture.TotalFramesSeen;
                    ZeroFrameCountText.Text = zeroFrames.ToString();
                    
                    double zeroPercent = totalFrames > 0 ? (100.0 * zeroFrames / totalFrames) : 0;
                    ZeroFramePercentText.Text = $"{zeroPercent:F1}%";
                    
                    if (zeroPercent > 20)
                        ZeroFramePercentText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
                    else if (zeroPercent > 5)
                        ZeroFramePercentText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
                    else
                        ZeroFramePercentText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                }
                else
                {
                    CaptureStatusText.Text = "Not Active";
                    CaptureStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                    FrameCountText.Text = "0";
                    CurrentFileText.Text = "--";
                    StartStopCaptureButton.Content = "Start Capture";
                    CaptureVadStatusText.Text = "--";
                    CaptureVadStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                    ZeroFrameCountText.Text = "0";
                    ZeroFramePercentText.Text = "0%";
                    ZeroFramePercentText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                }
            }
            catch { }
        }

        private void BrowseAudioFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dlg = new WinForms.FolderBrowserDialog())
                {
                    dlg.Description = "Select folder for debug audio files";
                    var currentPath = AudioCaptureFolderTextBox.Text;
                    if (!string.IsNullOrWhiteSpace(currentPath))
                    {
                        try
                        {
                            var fullPath = Path.IsPathRooted(currentPath)
                                ? currentPath
                                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, currentPath);
                            if (Directory.Exists(fullPath))
                                dlg.SelectedPath = fullPath;
                        }
                        catch { }
                    }

                    if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        AudioCaptureFolderTextBox.Text = TryMakeRelative(dlg.SelectedPath);
                        StatusText.Text = "Folder changed (click Save to apply)";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = AudioCaptureFolderTextBox.Text ?? "debug_audio";
                if (!Path.IsPathRooted(folder))
                {
                    folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder);
                }

                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void StartStopCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DebugAudioCapture.IsCapturing)
                {
                    DebugAudioCapture.StopCapture();
                    StatusText.Text = "Capture stopped";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                }
                else
                {
                    DebugAudioCapture.SetOutputFolder(AudioCaptureFolderTextBox.Text);
                    DebugAudioCapture.StartCapture();
                    StatusText.Text = "Capture started - speak to record";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                }

                UpdateCaptureStatus();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
            }
        }

        private void RotateFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!DebugAudioCapture.IsCapturing)
                {
                    DebugAudioCapture.SetOutputFolder(AudioCaptureFolderTextBox.Text);
                    DebugAudioCapture.StartCapture();
                    StatusText.Text = "Capture started - speak to record";
                }
                else
                {
                    DebugAudioCapture.RotateFile();
                    StatusText.Text = "Started new capture file";
                }
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                UpdateCaptureStatus();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
            }
        }

        private void ResetPeakButton_Click(object sender, RoutedEventArgs e)
        {
            _peakRms = 0;
            DebugAudioCapture.ResetPeak();
            PeakValueText.Text = "0";
        }

        private static string TryMakeRelative(string fullPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fullPath)) return fullPath;
                var baseDir = AppDomain.CurrentDomain.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
                var fp = Path.GetFullPath(fullPath);
                if (fp.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = fp.Substring(baseDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return rel.Replace('/', Path.DirectorySeparatorChar);
                }
            }
            catch { }
            return fullPath;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cur = _svc?.Current ?? throw new InvalidOperationException("Settings unavailable");

                // Parse normalization settings with validation
                float targetRms = 3000f;
                float maxGain = 8f;
                
                if (_normalizationTargetRmsTextBox != null && float.TryParse(_normalizationTargetRmsTextBox.Text, out var parsedRms))
                {
                    targetRms = Math.Max(500f, Math.Min(10000f, parsedRms));
                }
                
                if (_normalizationMaxGainTextBox != null && float.TryParse(_normalizationMaxGainTextBox.Text, out var parsedGain))
                {
                    maxGain = Math.Max(1f, Math.Min(20f, parsedGain));
                }

                var next = new DebugSettings(
                    AudioCaptureEnabled: false,
                    AudioCaptureFolder: string.IsNullOrWhiteSpace(AudioCaptureFolderTextBox.Text) ? "debug_audio" : AudioCaptureFolderTextBox.Text,
                    WebRtcEchoCancellation: EchoCancellationCheckBox?.IsChecked ?? false,
                    WebRtcNoiseSuppression: NoiseSuppressionCheckBox?.IsChecked ?? false,
                    WebRtcAutoGainControl: AutoGainCheckBox?.IsChecked ?? false,
                    WebRtcNormalizationEnabled: _normalizationEnabledCheckBox?.IsChecked ?? true,
                    WebRtcNormalizationTargetRms: targetRms,
                    WebRtcNormalizationMaxGain: maxGain
                );

                var updated = cur with { Debug = next };
                SettingsService.ValidateOrThrow(updated);
                _svc.Save(updated);

                DebugAudioCapture.SetOutputFolder(AudioCaptureFolderTextBox.Text);
                
                // Update AudioUtils with new normalization settings
                AudioUtils.UpdateNormalizationSettings(
                    next.WebRtcNormalizationEnabled,
                    next.WebRtcNormalizationTargetRms,
                    next.WebRtcNormalizationMaxGain
                );

                StatusText.Text = "Settings saved. WebRTC clients need to reconnect for DSP changes.";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
            }
        }
    }
}
