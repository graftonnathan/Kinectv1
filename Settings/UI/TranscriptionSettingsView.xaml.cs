using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kinectv1.Services.Transcription;
using Kinectv1.Settings;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Kinectv1.UI.Settings
{
    public partial class TranscriptionSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;
        private DispatcherTimer _refreshTimer;
        private bool _suppressSliderEvent;
        private TextBox _webRtcSilenceFlushMsTextBox;

        public TranscriptionSettingsView()
        {
            InitializeComponent();
            Loaded += TranscriptionSettingsView_Loaded;
            Unloaded += TranscriptionSettingsView_Unloaded;
        }

        private void TranscriptionSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _webRtcSilenceFlushMsTextBox = FindName("WebRtcSilenceFlushMsTextBox") as TextBox;

                var cfg = _svc?.Current?.Transcription;
                var audioCfg = _svc?.Current?.Audio;
                if (cfg == null) return;

                EnabledCheckBox.IsChecked = cfg.Enabled;
                MuteTtsCheckBox.IsChecked = cfg.MuteTts;
                LogToFileCheckBox.IsChecked = cfg.LogToFile;
                OutputFolderTextBox.Text = cfg.OutputFolder ?? "transcriptions";
                GenerateSummaryCheckBox.IsChecked = cfg.GenerateSummary;
                SummaryDelayTextBox.Text = cfg.SummaryDelaySeconds.ToString();
                SpeakerModelPathTextBox.Text = cfg.SpeakerEmbeddingModelPath ?? string.Empty;
                SpeakerWindowMsTextBox.Text = cfg.SpeakerEmbeddingWindowMs.ToString(CultureInfo.InvariantCulture);
                SpeakerHopMsTextBox.Text = cfg.SpeakerEmbeddingHopMs.ToString(CultureInfo.InvariantCulture);
                SpeakerSilenceDbTextBox.Text = cfg.SpeakerEmbeddingSilenceDb.ToString("F1", CultureInfo.InvariantCulture);
                
                // Load diarization threshold
                _suppressSliderEvent = true;
                DiarizationThresholdSlider.Value = cfg.DiarizationSimilarityThreshold;
                DiarizationThresholdValueText.Text = cfg.DiarizationSimilarityThreshold.ToString("F2");
                
                // Load enrolled speaker match threshold
                var speakerMatchThreshold = audioCfg?.SpeakerMatchMinScore ?? 0.75;
                SpeakerMatchThresholdSlider.Value = speakerMatchThreshold;
                SpeakerMatchThresholdValueText.Text = speakerMatchThreshold.ToString("F2");
                _suppressSliderEvent = false;

                if (_webRtcSilenceFlushMsTextBox != null)
                    _webRtcSilenceFlushMsTextBox.Text = cfg.WebRtcSilenceFlushMs.ToString();

                UpdateSessionStatus();

                // Subscribe to transcription events
                TranscriptionService.OnLog += OnTranscriptionLog;
                TranscriptionService.OnSummaryGenerated += OnSummaryGenerated;
                TranscriptionService.OnTranscriptionFileCreated += OnFileCreated;

                // Start refresh timer
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _refreshTimer.Tick += (s, args) => UpdateSessionStatus();
                _refreshTimer.Start();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Load error: {ex.Message}";
            }
        }

        private void TranscriptionSettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                TranscriptionService.OnLog -= OnTranscriptionLog;
                TranscriptionService.OnSummaryGenerated -= OnSummaryGenerated;
                TranscriptionService.OnTranscriptionFileCreated -= OnFileCreated;

                _refreshTimer?.Stop();
                _refreshTimer = null;
            }
            catch { }
        }

        private void DiarizationThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvent) return;
            
            // Guard against early calls during XAML initialization
            if (DiarizationThresholdValueText == null || StatusText == null) return;
            
            try
            {
                DiarizationThresholdValueText.Text = e.NewValue.ToString("F2");
                StatusText.Text = "Changed (not saved)";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x0A));
            }
            catch { }
        }

        private void SpeakerMatchThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvent) return;
            
            // Guard against early calls during XAML initialization
            if (SpeakerMatchThresholdValueText == null || StatusText == null) return;
            
            try
            {
                SpeakerMatchThresholdValueText.Text = e.NewValue.ToString("F2");
                StatusText.Text = "Changed (not saved)";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x0A));
            }
            catch { }
        }

        private void OnTranscriptionLog(string message)
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = message;
                });
            }
            catch { }
        }

        private void OnSummaryGenerated(string summary)
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = "Summary generated successfully";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                });
            }
            catch { }
        }

        private void OnFileCreated(string filePath)
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

        private void UpdateSessionStatus()
        {
            try
            {
                var service = TranscriptionService.Instance;

                if (service.IsActive)
                {
                    SessionStatusText.Text = "Active";
                    SessionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                    EntryCountText.Text = $"{service.EntryCount} ({service.SpeakerCount} speakers)";
                    CurrentFileText.Text = !string.IsNullOrEmpty(service.CurrentSessionFile)
                        ? Path.GetFileName(service.CurrentSessionFile)
                        : "--";
                    StartStopButton.Content = "Stop Session";
                }
                else
                {
                    SessionStatusText.Text = "Not Active";
                    SessionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                    EntryCountText.Text = "0";
                    CurrentFileText.Text = "--";
                    StartStopButton.Content = "Start Session";
                }
            }
            catch { }
        }

        private void BrowseOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dlg = new WinForms.FolderBrowserDialog())
                {
                    dlg.Description = "Select folder for transcription files";
                    var currentPath = OutputFolderTextBox.Text;
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
                        OutputFolderTextBox.Text = TryMakeRelative(dlg.SelectedPath);
                        StatusText.Text = "Changed (not saved)";
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void BrowseSpeakerModelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Select Speaker Embedding Model (.onnx)",
                    Filter = "ONNX model (*.onnx)|*.onnx|All files (*.*)|*.*"
                };

                if (dlg.ShowDialog() == true)
                {
                    SpeakerModelPathTextBox.Text = dlg.FileName;
                    StatusText.Text = "Changed (not saved)";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x0A));
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Browse error: {ex.Message}";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = OutputFolderTextBox.Text ?? "transcriptions";
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

        private async void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var service = TranscriptionService.Instance;

                if (service.IsActive)
                {
                    StartStopButton.IsEnabled = false;
                    StatusText.Text = "Stopping session...";

                    await service.StopSessionAsync(generateSummary: true);

                    StatusText.Text = "Session stopped";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                }
                else
                {
                    // Save current settings first
                    SaveSettings();

                    service.StartSession();

                    StatusText.Text = "Session started";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
                }

                UpdateSessionStatus();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
            }
            finally
            {
                StartStopButton.IsEnabled = true;
            }
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

        private void SaveSettings()
        {
            try
            {
                var cur = _svc?.Current ?? throw new InvalidOperationException("Settings unavailable");

                // Parse and validate summary delay
                if (!int.TryParse(SummaryDelayTextBox.Text, out var summaryDelay))
                {
                    summaryDelay = 60;
                }
                summaryDelay = Math.Clamp(summaryDelay, 5, 3600);

                // Get diarization threshold from slider
                var diarizationThreshold = Math.Clamp(DiarizationThresholdSlider.Value, 0.0, 1.0);

                // Get enrolled speaker match threshold from slider
                var speakerMatchThreshold = Math.Clamp(SpeakerMatchThresholdSlider.Value, 0.5, 0.95);

                int webRtcSilenceFlushMs = 1200;
                if (_webRtcSilenceFlushMsTextBox != null && int.TryParse(_webRtcSilenceFlushMsTextBox.Text, out var parsedFlush))
                     webRtcSilenceFlushMs = Math.Clamp(parsedFlush, 0, 2000);

                var speakerModelPathRaw = SpeakerModelPathTextBox?.Text?.Trim() ?? string.Empty;
                var speakerModelPath = string.IsNullOrWhiteSpace(speakerModelPathRaw)
                    ? string.Empty
                    : TryMakeRelative(speakerModelPathRaw);

                int windowMs = 1600;
                if (SpeakerWindowMsTextBox != null && int.TryParse(SpeakerWindowMsTextBox.Text, out var parsedWindow))
                    windowMs = Math.Clamp(parsedWindow, 400, 4000);

                int hopMs = 800;
                if (SpeakerHopMsTextBox != null && int.TryParse(SpeakerHopMsTextBox.Text, out var parsedHop))
                    hopMs = Math.Clamp(parsedHop, 200, 4000);
                if (hopMs > windowMs) hopMs = windowMs;

                double silenceDb = -70.0;
                if (SpeakerSilenceDbTextBox != null && double.TryParse(SpeakerSilenceDbTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSilence))
                    silenceDb = Math.Clamp(parsedSilence, -120.0, -5.0);

                var nextTranscription = new TranscriptionSettings(
                    Enabled: EnabledCheckBox.IsChecked ?? false,
                    MuteTts: MuteTtsCheckBox.IsChecked ?? true,
                    LogToFile: LogToFileCheckBox.IsChecked ?? true,
                    OutputFolder: string.IsNullOrWhiteSpace(OutputFolderTextBox.Text) ? "transcriptions" : OutputFolderTextBox.Text,
                    GenerateSummary: GenerateSummaryCheckBox.IsChecked ?? true,
                    SummaryDelaySeconds: summaryDelay,
                    DiarizationSimilarityThreshold: diarizationThreshold,
                    WebRtcSilenceFlushMs: webRtcSilenceFlushMs,
                    SpeakerEmbeddingModelPath: speakerModelPath,
                    SpeakerEmbeddingWindowMs: windowMs,
                    SpeakerEmbeddingHopMs: hopMs,
                    SpeakerEmbeddingSilenceDb: silenceDb
                );

                // Update audio settings with the new speaker match threshold
                var nextAudio = new AudioSettings(
                    VoiceThreshold: cur.Audio.VoiceThreshold,
                    BufferSize: cur.Audio.BufferSize,
                    SpeakerMatchMinScore: speakerMatchThreshold
                );

                var updated = cur with { Transcription = nextTranscription, Audio = nextAudio };
                SettingsService.ValidateOrThrow(updated);
                _svc.Save(updated);
                
                try { Kinectv1.VoiceRecognizer.SetWebRtcSilenceFlushMs(webRtcSilenceFlushMs); } catch { }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to save settings: {ex.Message}", ex);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettings();
                StatusText.Text = "Saved";
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
