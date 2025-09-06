using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Threading.Tasks;
using Kinectv1.Settings; // for JSON enums

namespace Kinectv1
{
    /// <summary>
    /// Models Settings View for configuring TTS/STT model paths and execution modes
    /// </summary>
    public partial class ModelsSettingsView : UserControl
    {
        // Guard to avoid re-initializing pacing sliders when the control is reloaded/hosted again
        private bool _pacingInitialized;

        public ModelsSettingsView()
        {
            InitializeComponent();
            InitializeControls();
            LoadCurrentSettings();
            // Re-apply volume values after the visual tree is fully loaded
            this.Loaded += (s, e) =>
            {
                try { InitializeVolumes(); InitializePacingControls(); } catch { }
            };
        }

        /// <summary>
        /// Initialize UI controls without defaulting values
        /// </summary>
        private void InitializeControls()
        {
            // Populate voice dropdown from actual Kokoro voices if available; do not provide defaults
            try
            {
                // Initialize to load voice list even if model isn't present (Initialize loads voices before model check)
                KokoroTtsService.Initialize();
                var voices = KokoroTtsService.GetVoices()?.ToList() ?? new List<string>();
                TtsVoiceComboBox.ItemsSource = voices;
            }
            catch
            {
                TtsVoiceComboBox.ItemsSource = new List<string>();
            }

            // Populate microphone devices
            try
            {
                var inputs = AudioDeviceManager.GetInputDevices() ?? new List<AudioDeviceManager.AudioInputDevice>();
                var names = new List<string> { "Default" };
                names.AddRange(inputs.Select(d => d.DeviceName).Distinct().OrderBy(s => s));
                MicInputComboBox.ItemsSource = names;
            }
            catch
            {
                MicInputComboBox.ItemsSource = new List<string> { "Default" };
            }

            // Populate TTS output devices
            try
            {
                var outputs = AudioDeviceManager.GetOutputDevices() ?? new List<AudioDeviceManager.AudioOutputDevice>();
                var outNames = new List<string> { "Default" };
                outNames.AddRange(outputs.Select(d => d.DeviceName).Distinct().OrderBy(s => s));
                TtsOutputDeviceComboBox.ItemsSource = outNames;
            }
            catch
            {
                TtsOutputDeviceComboBox.ItemsSource = new List<string> { "Default" };
            }

            // Do not set default execution mode; leave unselected until user chooses or a saved value exists
            ExecutionModeComboBox.SelectedIndex = -1;
        }

        /// <summary>
        /// Load current settings from JSON snapshot (fallback to legacy only where JSON does not yet cover)
        /// </summary>
        private void LoadCurrentSettings()
        {
            try
            {
                var snap = App.SettingsProvider?.Current;

                // TTS core fields (use JSON snapshot)
                var ttsSnap = snap?.Tts;
                TtsModelPathTextBox.Text = ttsSnap?.ModelPath ?? string.Empty;
                TtsModelFolderTextBox.Text = ttsSnap?.ModelFolder ?? string.Empty;
                TtsVocoderPathTextBox.Text = ttsSnap?.VocoderPath ?? string.Empty;

                var ttsEnabled = ttsSnap?.Enabled ?? false;
                TtsEnabledCheckBox.IsChecked = ttsEnabled;

                var currentVoice = ttsSnap?.Speaker;
                if (!string.IsNullOrEmpty(currentVoice))
                {
                    var items = TtsVoiceComboBox.ItemsSource as IEnumerable<string>;
                    if (items != null && items.Contains(currentVoice))
                        TtsVoiceComboBox.SelectedItem = currentVoice;
                    else
                        TtsVoiceComboBox.SelectedIndex = -1;
                }
                else
                {
                    TtsVoiceComboBox.SelectedIndex = -1;
                }

                if (ttsSnap != null)
                {
                    bool useGpu = (ttsSnap.Execution == Kinectv1.Settings.TtsExecution.GPU);
                    ExecutionModeComboBox.SelectedIndex = useGpu ? 1 : 0;
                }
                else
                {
                    ExecutionModeComboBox.SelectedIndex = -1;
                }

                // STT/Models (JSON-backed)
                SttModelPathTextBox.Text = snap?.Stt?.ModelPath ?? string.Empty;
                SpeakerModelPathTextBox.Text = snap?.Face?.SpeakerEmbeddingModelPath ?? string.Empty;
                ArcFaceModelPathTextBox.Text = snap?.Face?.ArcFaceModelPath ?? string.Empty;

                // Microphone selection (JSON-backed)
                var mic = snap?.Stt?.InputDevice;
                if (string.IsNullOrWhiteSpace(mic)) mic = "Default";
                MicInputComboBox.SelectedItem = mic;

                // Output device (JSON)
                var outDev = ttsSnap?.OutputDevice;
                if (!string.IsNullOrWhiteSpace(outDev))
                {
                    TtsOutputDeviceComboBox.SelectedItem = outDev;
                }

                // Voice Match Threshold
                var jsonMatch = snap?.Audio?.SpeakerMatchMinScore;
                if (jsonMatch.HasValue)
                {
                    SpeakerMatchThresholdTextBox.Text = (jsonMatch.Value > 0 && jsonMatch.Value <= 1.0)
                        ? jsonMatch.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty;
                }
                else
                {
                    SpeakerMatchThresholdTextBox.Text = string.Empty;
                }

                // Audio section – thresholds/buffer (JSON)
                var vtVal = snap?.Audio?.VoiceThreshold;
                AudioVoiceThresholdTextBox.Text = (vtVal.HasValue && vtVal.Value > 0)
                    ? vtVal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty;

                // High threshold currently legacy-only -> leave empty
                AudioVoiceHighThresholdTextBox.Text = string.Empty;

                // Confidence logging (JSON)
                VoiceConfidenceLoggingCheckBox.IsChecked = snap?.Asr?.VoiceConfidenceLoggingEnabled ?? false;

                // Discord VAD (JSON)
                var discVad = snap?.Asr?.DiscordVadThreshold ?? 0.0;
                AudioVadThresholdTextBox.Text = discVad > 0 ? discVad.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

                var bs = snap?.Audio?.BufferSize;
                AudioBufferSizeTextBox.Text = (bs.HasValue && bs.Value > 0) ? bs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

                // VAD section (microphone) + wake word requirement (JSON + legacy)
                var micVad = snap?.Audio?.VadThreshold;
                VadThresholdTextBox.Text = (micVad.HasValue && micVad.Value > 0)
                    ? micVad.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty;
                RequireWakeWordCheckBox.IsChecked = snap?.App?.RequireWakeWord ?? false;

                // Face recognition section (JSON)
                var faceThr = snap?.Face?.Threshold ?? 0.0;
                FaceThresholdTextBox.Text = faceThr > 0 ? faceThr.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
                FusionFaceWeightTextBox.Text = (snap?.Face?.FusionFaceWeight ?? 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture);
                FusionVoiceWeightTextBox.Text = (snap?.Face?.FusionVoiceWeight ?? 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture);
                FusionHalfLifeTextBox.Text = (snap?.Face?.FusionDecayHalfLifeMs ?? 2000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                FusionUnknownThresholdTextBox.Text = (snap?.Face?.FusionUnknownThreshold ?? 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture);

                // IPA timeouts (JSON)
                if (TtsIpaServiceTimeoutTextBox != null)
                    TtsIpaServiceTimeoutTextBox.Text = (ttsSnap?.IpaServiceTimeoutMs ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TtsIpaOneShotTimeoutTextBox != null)
                    TtsIpaOneShotTimeoutTextBox.Text = (ttsSnap?.IpaOneShotTimeoutMs ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

                UpdateStatus("Settings loaded", false);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading settings: {ex.Message}", true);
            }
        }

        /// <summary>
        /// Browse for TTS model file
        /// </summary>
        private void BrowseTtsModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select TTS Model File",
                Filter = "ONNX Models (*.onnx)|*.onnx|All Files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = GetInitialDirectory(TtsModelPathTextBox.Text)
            };

            if (dialog.ShowDialog() == true)
            {
                TtsModelPathTextBox.Text = dialog.FileName;
                UpdateStatus($"TTS model file selected: {Path.GetFileName(dialog.FileName)}", false);
            }
        }

        /// <summary>
        /// Browse for TTS model folder
        /// </summary>
        private void BrowseTtsModelFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select TTS Model Folder",
                Filter = "All Files (*.*)|*.*",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Folder Selection",
                ValidateNames = false,
                InitialDirectory = GetInitialDirectory(TtsModelFolderTextBox.Text)
            };

            if (dialog.ShowDialog() == true)
            {
                string selectedPath = Path.GetDirectoryName(dialog.FileName);
                TtsModelFolderTextBox.Text = selectedPath;
                UpdateStatus($"TTS model folder selected: {Path.GetFileName(selectedPath)}", false);
            }
        }

        /// <summary>
        /// Browse for TTS vocoder file
        /// </summary>
        private void BrowseTtsVocoderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select TTS Vocoder File",
                Filter = "ONNX Models (*.onnx)|*.onnx|All Files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = GetInitialDirectory(TtsVocoderPathTextBox.Text)
            };

            if (dialog.ShowDialog() == true)
            {
                TtsVocoderPathTextBox.Text = dialog.FileName;
                UpdateStatus($"TTS vocoder file selected: {Path.GetFileName(dialog.FileName)}", false);
            }
        }

        /// <summary>
        /// Browse for STT model directory (Vosk models are directories)
        /// </summary>
        private void BrowseSttModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Vosk STT Model Directory",
                Filter = "All Files (*.*)|*.*",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Folder Selection",
                ValidateNames = false,
                InitialDirectory = GetInitialDirectory(SttModelPathTextBox.Text)
            };

            if (dialog.ShowDialog() == true)
            {
                string selectedPath = Path.GetDirectoryName(dialog.FileName);
                SttModelPathTextBox.Text = selectedPath;
                UpdateStatus($"STT model directory selected: {Path.GetFileName(selectedPath)}", false);
            }
        }

        /// <summary>
        /// Browse for speaker embedding model file
        /// </summary>
        private void BrowseSpeakerModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Speaker Embedding Model File",
                Filter = "ONNX Models (*.onnx)|*.onnx|All Files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = GetInitialDirectory(SpeakerModelPathTextBox.Text)
            };

            if (dialog.ShowDialog() == true)
            {
                SpeakerModelPathTextBox.Text = dialog.FileName;
                UpdateStatus($"Speaker model file selected: {Path.GetFileName(dialog.FileName)}", false);
            }
        }

        /// <summary>
        /// Browse for ArcFace model file
        /// </summary>
        private void BrowseArcFaceModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select ArcFace ONNX Model",
                Filter = "ONNX Models (*.onnx)|*.onnx|All Files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = GetInitialDirectory(ArcFaceModelPathTextBox.Text)
            };

            if (dialog.ShowDialog() == true)
            {
                ArcFaceModelPathTextBox.Text = dialog.FileName;
                UpdateStatus($"ArcFace model selected: {Path.GetFileName(dialog.FileName)}", false);
            }
        }

        private void TtsVoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TtsVoiceComboBox.SelectedItem != null)
            {
                string selectedVoice = TtsVoiceComboBox.SelectedItem.ToString();
                UpdateStatus($"TTS voice selected: {selectedVoice}", false);
            }
        }

        private void ExecutionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ExecutionModeComboBox.SelectedItem != null)
            {
                var selectedItem = ExecutionModeComboBox.SelectedItem as ComboBoxItem;
                string mode = selectedItem?.Tag?.ToString() ?? "";
                if (!string.IsNullOrEmpty(mode))
                    UpdateStatus($"Execution mode selected: {mode}", false);
            }
        }

        private void MicInputComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MicInputComboBox.SelectedItem is string name && !string.IsNullOrWhiteSpace(name))
            {
                UpdateStatus($"Microphone selected: {name}", false);
            }
        }

        /// <summary>
        /// Test the selected TTS voice with the test phrase
        /// </summary>
        private async void TestVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var phrase = TestPhraseTextBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(phrase))
                {
                    UpdateStatus("Please enter a test phrase", true);
                    return;
                }

                if (TtsEnabledCheckBox.IsChecked != true)
                {
                    UpdateStatus("TTS is disabled. Enable TTS to run a voice test.", true);
                    MessageBox.Show("Enable TTS first (check 'Enable TTS').", "TTS Disabled", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Persist current output device selection so test and pipeline use it
                var outDev = TtsOutputDeviceComboBox.SelectedItem as string;
                if (!string.IsNullOrWhiteSpace(outDev))
                {
                    var svc = App.SettingsProvider; var curr = svc?.Current;
                    if (svc != null && curr != null)
                    {
                        var next = curr with { Tts = curr.Tts with { OutputDevice = outDev } };
                        svc.Save(next);
                    }
                    AudioDeviceManager.InvalidateOutputDeviceCache();
                }

                // Save selected voice (if any) for the test
                var selectedVoice = TtsVoiceComboBox.SelectedItem?.ToString();
                if (!string.IsNullOrWhiteSpace(selectedVoice))
                {
                    var svc = App.SettingsProvider;
                    var curr = svc?.Current;
                    if (svc != null && curr != null)
                    {
                        var tts = curr.Tts with
                        {
                            Speaker = selectedVoice,
                            OutputDevice = string.IsNullOrWhiteSpace(outDev) ? curr.Tts.OutputDevice : outDev
                        };
                        var next = new Kinectv1.Settings.AppSettings(curr.Audio, tts, curr.Vad, curr.Ollama, curr.Discord, curr.Mumble, curr.Ui, curr.Asr, curr.Stt, curr.Face, curr.App);
                        svc.Save(next);
                    }
                }

                UpdateStatus($"🔊 Testing voice{(string.IsNullOrWhiteSpace(selectedVoice) ? string.Empty : $" '{selectedVoice}'")}", false);

                // Speak with preemption so repeated clicks interrupt
                var ok = await CoquiTtsService.SpeakStreamingWithPreemptionAsync(phrase, selectedVoice);
                if (ok)
                {
                    UpdateStatus("Voice test completed.", false);
                }
                else
                {
                    UpdateStatus("Voice test failed (see console for details).", true);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Voice test failed: {ex.Message}", true);
            }
        }

        private void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var validationResults = new List<string>();

                string ttsModelPath = TtsModelPathTextBox.Text;
                validationResults.Add(string.IsNullOrWhiteSpace(ttsModelPath) ? "⚠️ TTS model path not configured" : (File.Exists(ttsModelPath) ? "✅ TTS model file exists" : "❌ TTS model file not found"));

                string ttsModelFolder = TtsModelFolderTextBox.Text;
                validationResults.Add(string.IsNullOrWhiteSpace(ttsModelFolder) ? "⚠️ TTS model folder not configured" : (Directory.Exists(ttsModelFolder) ? "✅ TTS model folder exists" : "❌ TTS model folder not found"));

                string vocoderPath = TtsVocoderPathTextBox.Text;
                validationResults.Add(string.IsNullOrWhiteSpace(vocoderPath) ? "⚠️ TTS vocoder path not configured" : (File.Exists(vocoderPath) ? "✅ TTS vocoder file exists" : "❌ TTS vocoder file not found"));

                string sttModelPath = SttModelPathTextBox.Text;
                validationResults.Add(string.IsNullOrWhiteSpace(sttModelPath) ? "⚠️ STT model path not configured" : (Directory.Exists(sttModelPath) ? "✅ STT model directory exists" : "❌ STT model directory not found"));

                string speakerModelPath = SpeakerModelPathTextBox.Text;
                validationResults.Add(string.IsNullOrWhiteSpace(speakerModelPath) ? "⚠️ Speaker model path not configured" : (File.Exists(speakerModelPath) ? "✅ Speaker model file exists" : "❌ Speaker model file not found"));

                string arcPath = ArcFaceModelPathTextBox.Text;
                validationResults.Add(string.IsNullOrWhiteSpace(arcPath) ? "⚠️ ArcFace model path not configured" : (File.Exists(arcPath) ? "✅ ArcFace model file exists" : "❌ ArcFace model file not found"));

                // Microphone presence
                if (MicInputComboBox.SelectedItem is string mic && !string.IsNullOrWhiteSpace(mic))
                {
                    if (mic != "Default")
                    {
                        var num = AudioDeviceManager.GetInputDeviceNumberByName(mic);
                        validationResults.Add(num.HasValue ? $"✅ Microphone available: {mic}" : $"❌ Microphone not found: {mic}");
                    }
                    else
                    {
                        validationResults.Add("ℹ️ Microphone: Default");
                    }
                }

                // Voice Match Threshold
                if (!string.IsNullOrWhiteSpace(SpeakerMatchThresholdTextBox.Text))
                {
                    if (float.TryParse(SpeakerMatchThresholdTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var thr))
                    {
                        validationResults.Add(thr >= 0f && thr <= 1f ? $"✅ Voice Match Threshold OK ({thr:0.00})" : "❌ Voice Match Threshold must be 0..1");
                    }
                    else
                    {
                        validationResults.Add("❌ Voice Match Threshold invalid number");
                    }
                }
                else
                {
                    validationResults.Add("⚠️ Voice Match Threshold is empty (will fall back to default logic)");
                }

                // Voice thresholds
                if (!string.IsNullOrWhiteSpace(AudioVoiceThresholdTextBox.Text))
                {
                    if (float.TryParse(AudioVoiceThresholdTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vThr))
                        validationResults.Add(vThr >= 0f && vThr <= 1f ? "✅ Voice Threshold OK" : "❌ Voice Threshold must be 0..1");
                    else
                        validationResults.Add("❌ Voice Threshold invalid number");
                }
                if (!string.IsNullOrWhiteSpace(AudioVoiceHighThresholdTextBox.Text))
                {
                    if (float.TryParse(AudioVoiceHighThresholdTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vHi))
                        validationResults.Add(vHi >= 0f && vHi <= 1f ? "✅ High Confidence Threshold OK" : "❌ High Confidence Threshold must be 0..1");
                    else
                        validationResults.Add("❌ High Confidence Threshold invalid number");
                }

                if (!string.IsNullOrWhiteSpace(AudioBufferSizeTextBox.Text))
                {
                    if (int.TryParse(AudioBufferSizeTextBox.Text, out var buf) && buf > 0)
                        validationResults.Add("✅ Buffer Size OK");
                    else
                        validationResults.Add("❌ Buffer Size must be a positive integer");
                }

                // IPA timeouts validation (200..5000)
                if (!string.IsNullOrWhiteSpace(TtsIpaServiceTimeoutTextBox?.Text))
                {
                    if (int.TryParse(TtsIpaServiceTimeoutTextBox.Text, out var svc) && svc >= 200 && svc <= 5000)
                        validationResults.Add("✅ IPA Service Timeout OK");
                    else
                        validationResults.Add("❌ IPA Service Timeout must be 200-5000 ms");
                }
                if (!string.IsNullOrWhiteSpace(TtsIpaOneShotTimeoutTextBox?.Text))
                {
                    if (int.TryParse(TtsIpaOneShotTimeoutTextBox.Text, out var one) && one >= 200 && one <= 5000)
                        validationResults.Add("✅ IPA One-Shot Timeout OK");
                    else
                        validationResults.Add("❌ IPA One-Shot Timeout must be 200-5000 ms");
                }

                // Face recognition settings
                if (!string.IsNullOrWhiteSpace(FaceThresholdTextBox.Text))
                {
                    if (float.TryParse(FaceThresholdTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ft))
                        validationResults.Add(ft >= 0f && ft <= 1f ? "✅ Face Threshold OK" : "❌ Face Threshold must be 0..1");
                    else
                        validationResults.Add("❌ Face Threshold invalid number");
                }
                if (!string.IsNullOrWhiteSpace(FusionFaceWeightTextBox.Text))
                {
                    if (float.TryParse(FusionFaceWeightTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ff))
                        validationResults.Add(ff > 0f && ff <= 1f ? "✅ Fusion Face Weight OK" : "❌ Fusion Face Weight must be >0 and <=1");
                    else
                        validationResults.Add("❌ Fusion Face Weight invalid number");
                }
                if (!string.IsNullOrWhiteSpace(FusionVoiceWeightTextBox.Text))
                {
                    if (float.TryParse(FusionVoiceWeightTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv))
                        validationResults.Add(fv > 0f && fv <= 1f ? "✅ Fusion Voice Weight OK" : "❌ Fusion Voice Weight must be >0 and <=1");
                    else
                        validationResults.Add("❌ Fusion Voice Weight invalid number");
                }
                if (!string.IsNullOrWhiteSpace(FusionHalfLifeTextBox.Text))
                {
                    if (int.TryParse(FusionHalfLifeTextBox.Text, out var hl) && hl >= 500 && hl <= 10000)
                        validationResults.Add("✅ Fusion Decay Half-Life OK" );
                    else
                        validationResults.Add("❌ Fusion Decay Half-Life must be 500-10000 ms");
                }
                if (!string.IsNullOrWhiteSpace(FusionUnknownThresholdTextBox.Text))
                {
                    if (float.TryParse(FusionUnknownThresholdTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fu))
                        validationResults.Add(fu >= 0f && fu <= 1f ? "✅ Fusion Unknown Threshold OK" : "❌ Fusion Unknown Threshold must be 0..1");
                    else
                        validationResults.Add("❌ Fusion Unknown Threshold invalid number");
                }
                // Telemetry sampling validation removed

                string result = string.Join("\n", validationResults);
                bool hasErrors = validationResults.Any(r => r.StartsWith("❌"));

                UpdateStatus($"Validation completed:\n{result}", hasErrors);

                MessageBox.Show(result, "Validation",
                    MessageBoxButton.OK,
                    hasErrors ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Validation failed: {ex.Message}", true);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // TTS enabled flag
                bool ttsEnabled = TtsEnabledCheckBox.IsChecked ?? false;

                // Selected execution mode
                var selectedModeItem = ExecutionModeComboBox.SelectedItem as ComboBoxItem;
                bool useGpu = selectedModeItem?.Tag?.ToString() == "GPU";

                // TTS output selection
                var outDev = TtsOutputDeviceComboBox.SelectedItem as string;

                // Volumes (0..100 UI -> 0..1)
                double localVol = 0.0;
                double discVol = 0.0;
                if (LocalVolumeSlider != null)
                {
                    localVol = Math.Max(0, Math.Min(100, LocalVolumeSlider.Value)) / 100.0;
                }
                if (DiscordVolumeSlider != null)
                {
                    discVol = Math.Max(0, Math.Min(100, DiscordVolumeSlider.Value)) / 100.0;
                }

                // TTS pacing settings
                float speedVal = 0f;
                double trimThr = 0.0;
                int leaveVal = 0;
                int maxVal = 0;
                int padVal = 0;

                if (TtsSpeedSlider != null)
                {
                    var speed = Math.Max(0.5, Math.Min(2.0, TtsSpeedSlider.Value / 100.0));
                    speedVal = (float)speed;
                }

                if (TrimSilenceThresholdSlider != null)
                {
                    // Map UI 0..100 to 0.0005..0.02
                    var thr = 0.0005 + (TrimSilenceThresholdSlider.Value / 100.0) * (0.02 - 0.0005);
                    trimThr = thr;
                }

                if (TtsTrimLeaveSlider != null)
                {
                    var leave = (int)Math.Round(Math.Max(0, Math.Min(100, TtsTrimLeaveSlider.Value)));
                    leaveVal = leave;
                }

                if (TtsTrimMaxSlider != null)
                {
                    var max = (int)Math.Round(Math.Max(50, Math.Min(3000, TtsTrimMaxSlider.Value)));
                    maxVal = max;
                }

                if (TtsPaddingSlider != null)
                {
                    var pad = (int)Math.Round(Math.Max(0, Math.Min(200, TtsPaddingSlider.Value)));
                    padVal = pad;
                }

                // IPA timeouts
                int ipaSvc = 0;
                int ipaOne = 0;
                if (!string.IsNullOrWhiteSpace(TtsIpaServiceTimeoutTextBox?.Text))
                {
                    if (!int.TryParse(TtsIpaServiceTimeoutTextBox.Text, out ipaSvc))
                        throw new InvalidOperationException("IPA Service Timeout must be an integer");
                }
                if (!string.IsNullOrWhiteSpace(TtsIpaOneShotTimeoutTextBox?.Text))
                {
                    if (!int.TryParse(TtsIpaOneShotTimeoutTextBox.Text, out ipaOne))
                        throw new InvalidOperationException("IPA One-Shot Timeout must be an integer");
                }

                var svcSnap = App.SettingsProvider?.Current;
                bool requireWake = RequireWakeWordCheckBox.IsChecked ?? (svcSnap?.App?.RequireWakeWord ?? false);
                string sttPath = SttModelPathTextBox.Text ?? svcSnap?.Stt?.ModelPath ?? string.Empty;
                string micDev = MicInputComboBox.SelectedItem as string;
                string spkModel = SpeakerModelPathTextBox.Text ?? svcSnap?.Face?.SpeakerEmbeddingModelPath ?? string.Empty;
                string arcModel = ArcFaceModelPathTextBox.Text ?? svcSnap?.Face?.ArcFaceModelPath ?? string.Empty;
                double faceThreshold = string.IsNullOrWhiteSpace(FaceThresholdTextBox.Text) ? (svcSnap?.Face?.Threshold ?? 0.7) : double.Parse(FaceThresholdTextBox.Text, System.Globalization.CultureInfo.InvariantCulture);
                double fusionFace = string.IsNullOrWhiteSpace(FusionFaceWeightTextBox.Text) ? (svcSnap?.Face?.FusionFaceWeight ?? 0.5) : double.Parse(FusionFaceWeightTextBox.Text, System.Globalization.CultureInfo.InvariantCulture);
                double fusionVoice = string.IsNullOrWhiteSpace(FusionVoiceWeightTextBox.Text) ? (svcSnap?.Face?.FusionVoiceWeight ?? 0.5) : double.Parse(FusionVoiceWeightTextBox.Text, System.Globalization.CultureInfo.InvariantCulture);
                int fusionHalf = string.IsNullOrWhiteSpace(FusionHalfLifeTextBox.Text) ? (svcSnap?.Face?.FusionDecayHalfLifeMs ?? 2000) : int.Parse(FusionHalfLifeTextBox.Text, System.Globalization.CultureInfo.InvariantCulture);
                double fusionUnknown = string.IsNullOrWhiteSpace(FusionUnknownThresholdTextBox.Text) ? (svcSnap?.Face?.FusionUnknownThreshold ?? 0.5) : double.Parse(FusionUnknownThresholdTextBox.Text, System.Globalization.CultureInfo.InvariantCulture);

                // ----- Synchronize with JSON settings (used at startup/services) -----
                try
                {
                    var svc = App.SettingsProvider;
                    var curr = svc?.Current;
                    if (svc != null && curr != null)
                    {
                        var outDevice = string.IsNullOrWhiteSpace(outDev) ? (curr.Tts.OutputDevice ?? "Default") : outDev;
                        var speaker = TtsVoiceComboBox.SelectedItem?.ToString();

                        var exec = useGpu ? Kinectv1.Settings.TtsExecution.GPU : Kinectv1.Settings.TtsExecution.CPU;

                        // Audio snapshot from text boxes
                        double vt = curr.Audio.VoiceThreshold;
                        int vad = curr.Audio.VadThreshold;
                        int buf = curr.Audio.BufferSize;
                        if (double.TryParse(AudioVoiceThresholdTextBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vtParsed)) vt = vtParsed;
                        if (int.TryParse(AudioVadThresholdTextBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var vadParsed)) vad = vadParsed;
                        if (int.TryParse(AudioBufferSizeTextBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bufParsed)) buf = bufParsed;

                        // Speaker match threshold (0..1)
                        double spkMatch = curr.Audio.SpeakerMatchMinScore;
                        var spkText = SpeakerMatchThresholdTextBox?.Text;
                        if (!string.IsNullOrWhiteSpace(spkText))
                        {
                            if (double.TryParse(spkText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sm) && sm >= 0.0 && sm <= 1.0)
                            {
                                spkMatch = sm;
                            }
                        }

                        var audio = new Kinectv1.Settings.AudioSettings(vt, vad, buf, spkMatch);
                        var tts = new Kinectv1.Settings.TtsSettings(
                            Enabled: ttsEnabled,
                            Speaker: string.IsNullOrWhiteSpace(speaker) ? curr.Tts.Speaker : speaker,
                            Execution: exec,
                            ModelFolder: TtsModelFolderTextBox.Text ?? curr.Tts.ModelFolder,
                            ModelPath: TtsModelPathTextBox.Text ?? curr.Tts.ModelPath,
                            VocoderPath: TtsVocoderPathTextBox.Text ?? curr.Tts.VocoderPath,
                            OutputDevice: outDevice,
                            LocalVolume: localVol,
                            DiscordVolume: discVol,
                            Speed: speedVal,
                            TrimThreshold: trimThr,
                            TrimLeaveMs: leaveVal,
                            TrimMaxMs: maxVal,
                            MinClausePaddingMs: padVal,
                            IpaServiceTimeoutMs: ipaSvc,
                            IpaOneShotTimeoutMs: ipaOne
                        );
                        var vadSettings = new Kinectv1.Settings.VadSettings(vad);
                        var asr = new Kinectv1.Settings.AsrSettings(
                            VoiceConfidenceThreshold: curr?.Asr?.VoiceConfidenceThreshold ?? 0.6,
                            VoiceHighConfidenceThreshold: curr?.Asr?.VoiceHighConfidenceThreshold ?? 0.8,
                            VoiceConfidenceBufferSize: curr?.Asr?.VoiceConfidenceBufferSize ?? 3,
                            VoiceConfidenceLoggingEnabled: VoiceConfidenceLoggingCheckBox.IsChecked ?? (curr?.Asr?.VoiceConfidenceLoggingEnabled ?? false),
                            VadSilenceTimeoutMs: curr?.Asr?.VadSilenceTimeoutMs ?? 1000,
                            VadDebounceTimeoutMs: curr?.Asr?.VadDebounceTimeoutMs ?? 150,
                            DiscordVadThreshold: string.IsNullOrWhiteSpace(AudioVadThresholdTextBox.Text) ? (curr?.Asr?.DiscordVadThreshold ?? 25.0) : double.Parse(AudioVadThresholdTextBox.Text, System.Globalization.CultureInfo.InvariantCulture),
                            BargeInEnabled: curr?.Asr?.BargeInEnabled ?? true
                        );
                        var stt = new Kinectv1.Settings.SttSettings(
                            ModelPath: sttPath,
                            InputDevice: string.IsNullOrWhiteSpace(micDev) ? (curr?.Stt?.InputDevice ?? "Default") : micDev
                        );
                        var face = new Kinectv1.Settings.FaceSettings(
                            Threshold: faceThreshold,
                            FusionFaceWeight: fusionFace,
                            FusionVoiceWeight: fusionVoice,
                            FusionDecayHalfLifeMs: fusionHalf,
                            FusionUnknownThreshold: fusionUnknown,
                            ArcFaceModelPath: arcModel,
                            SpeakerEmbeddingModelPath: spkModel
                        );
                        var app = new Kinectv1.Settings.AppConfig(
                            RequireWakeWord: requireWake,
                            Scenario: curr?.App?.Scenario ?? Kinectv1.Settings.AppScenario.Local,
                            InputMode: curr?.App?.InputMode ?? Kinectv1.Settings.AudioInMode.LocalMic
                        );
                        var next = new Kinectv1.Settings.AppSettings(audio, tts, vadSettings, curr.Ollama, curr.Discord, curr.Mumble, curr.Ui, asr, stt, face, app);
                        svc.Save(next);
                    }
                }
                catch (Exception jsEx)
                {
                    Console.WriteLine($"JSON settings sync failed: {jsEx.Message}");
                }

                // Ensure runtime session is recreated to apply potential GPU/CPU change immediately
                try
                {
                    var recreated = KokoroTtsService.RecreateSessionFromSettings();
                    if (!recreated)
                    {
                        UpdateStatus("TTS session recreate failed; check GPU runtime availability.", true);
                      }
                    else
                    {
                        UpdateStatus($"TTS session now using {(KokoroTtsService.IsUsingGpu() ? "GPU" : "CPU")}.", false);
                    }
                }
                catch (Exception rex)
                {
                    UpdateStatus($"Error recreating TTS session: {rex.Message}", true);
                }

                UpdateStatus("All settings saved successfully!", false);
                MessageBox.Show("Model settings have been saved successfully!", "Settings Saved",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error saving settings: {ex.Message}", true);
                MessageBox.Show($"Error saving settings: {ex.Message}", "Save Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to clear all model and audio settings?",
                "Clear Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    TtsEnabledCheckBox.IsChecked = false;
                    TtsModelPathTextBox.Text = string.Empty;
                    TtsModelFolderTextBox.Text = string.Empty;
                    TtsVocoderPathTextBox.Text = string.Empty;
                    TtsVoiceComboBox.SelectedIndex = -1;
                    ExecutionModeComboBox.SelectedIndex = -1;
                    SttModelPathTextBox.Text = string.Empty;
                    SpeakerModelPathTextBox.Text = string.Empty;
                    ArcFaceModelPathTextBox.Text = string.Empty;
                    MicInputComboBox.SelectedIndex = 0; // Default

                    AudioVoiceThresholdTextBox.Text = string.Empty;
                    AudioVoiceHighThresholdTextBox.Text = string.Empty;
                    VoiceConfidenceLoggingCheckBox.IsChecked = false;
                    AudioVadThresholdTextBox.Text = string.Empty;
                    AudioBufferSizeTextBox.Text = string.Empty;
                    VadThresholdTextBox.Text = string.Empty;
                    SpeakerMatchThresholdTextBox.Text = string.Empty;
                    RequireWakeWordCheckBox.IsChecked = false;

                    // Reset JSON settings to defaults
                    try { App.SettingsProvider?.ResetToDefaults(); } catch { }

                    UpdateStatus("Fields cleared. Click Save to persist.", false);
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error clearing settings: {ex.Message}", true);
                }
            }
        }

        private void UpdateStatus(string message, bool isError)
        {
            if (StatusTextBlock != null)
            {
                StatusTextBlock.Text = $"{(isError ? "❌" : "ℹ️")} {message}";
                StatusTextBlock.Foreground = isError ?
                    (System.Windows.Media.Brush)FindResource("AccentRed") :
                    (System.Windows.Media.Brush)FindResource("TextSecondary");
            }
        }

        private string GetInitialDirectory(string currentPath)
        {
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                try
                {
                    if (File.Exists(currentPath))
                    {
                        return Path.GetDirectoryName(currentPath);
                    }
                    else if (Directory.Exists(currentPath))
                    {
                        return currentPath;
                    }
                    else if (!string.IsNullOrWhiteSpace(Path.GetDirectoryName(currentPath)) && Directory.Exists(Path.GetDirectoryName(currentPath)))
                    {
                        return Path.GetDirectoryName(currentPath);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            string modelsDir = Path.Combine(Environment.CurrentDirectory, "models");
            return Directory.Exists(modelsDir) ? modelsDir : Environment.CurrentDirectory;
        }

        private void InitializeVolumes()
        {
            try
            {
                // JSON snapshot only
                var snap = App.SettingsProvider?.Current;
                double local = snap?.Tts?.LocalVolume ?? 0.0;
                double disc = snap?.Tts?.DiscordVolume ?? 0.0;

                if (LocalVolumeSlider != null)
                {
                    LocalVolumeSlider.Value = Math.Round(Math.Max(0.0, Math.Min(1.0, local)) * 100);
                    LocalVolumeValueText.Text = $"{LocalVolumeSlider.Value:F0}%";
                    LocalVolumeSlider.ValueChanged -= LocalVolumeSlider_ValueChanged;
                    LocalVolumeSlider.ValueChanged += LocalVolumeSlider_ValueChanged;
                }
                if (DiscordVolumeSlider != null)
                {
                    DiscordVolumeSlider.Value = Math.Round(Math.Max(0.0, Math.Min(1.0, disc)) * 100);
                    DiscordVolumeValueText.Text = $"{DiscordVolumeSlider.Value:F0}%";
                    DiscordVolumeSlider.ValueChanged -= DiscordVolumeSlider_ValueChanged;
                    DiscordVolumeSlider.ValueChanged += DiscordVolumeSlider_ValueChanged;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading volumes: {ex.Message}", true);
            }
        }

        private void LocalVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                LocalVolumeValueText.Text = $"{LocalVolumeSlider.Value:F0}%";
                var lv = Math.Max(0, Math.Min(100, LocalVolumeSlider.Value)) / 100.0;
                // Persist to JSON
                var svc = App.SettingsProvider; var curr = svc?.Current;
                if (svc != null && curr != null)
                {
                    var next = curr with { Tts = curr.Tts with { LocalVolume = lv } };
                    svc.Save(next);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error saving Local volume: {ex.Message}", true);
            }
        }

        private void DiscordVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                DiscordVolumeValueText.Text = $"{DiscordVolumeSlider.Value:F0}%";
                var dv = Math.Max(0, Math.Min(100, DiscordVolumeSlider.Value)) / 100.0;
                // Persist to JSON
                var svc = App.SettingsProvider; var curr = svc?.Current;
                if (svc != null && curr != null)
                {
                    var next = curr with { Tts = curr.Tts with { DiscordVolume = dv } };
                    svc.Save(next);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error saving Discord volume: {ex.Message}", true);
            }
        }

        private void TtsSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                var speed = Math.Max(0.5, Math.Min(2.0, e.NewValue / 100.0));
                TtsSpeedValueText.Text = $"{speed:F2}x";
                var svc = App.SettingsProvider; var curr = svc?.Current;
                if (svc != null && curr != null)
                {
                    var next = curr with { Tts = curr.Tts with { Speed = (float)speed } };
                    svc.Save(next);
                }
            }
            catch (Exception ex) { UpdateStatus($"Error saving TTS speed: {ex.Message}", true); }
        }

        private void TrimSilenceThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                // Map UI 0..100 to 0.0005..0.02
                var thr = 0.0005 + (e.NewValue / 100.0) * (0.02 - 0.0005);
                TrimSilenceThresholdValueText.Text = thr.ToString("F4");
                var svc = App.SettingsProvider; var curr = svc?.Current;
                if (svc != null && curr != null)
                {
                    var next = curr with { Tts = curr.Tts with { TrimThreshold = thr } };
                    svc.Save(next);
                }
            }
            catch (Exception ex) { UpdateStatus($"Error saving trim threshold: {ex.Message}", true); }
        }

        private void TtsTrimLeaveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                var leave = (int)Math.Round(Math.Max(0, Math.Min(100, e.NewValue)));
                if (TtsTrimLeaveValueText != null) TtsTrimLeaveValueText.Text = $"{leave} ms";
                var svc = App.SettingsProvider; var curr = svc?.Current;
                if (svc != null && curr != null)
                {
                    var next = curr with { Tts = curr.Tts with { TrimLeaveMs = leave } };
                    svc.Save(next);
                }
            }
            catch (Exception ex) { UpdateStatus($"Error saving Trim Leave (ms): {ex.Message}", true); }
        }

        private void TtsTrimMaxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                var max = (int)Math.Round(Math.Max(50, Math.Min(3000, e.NewValue)));
                if (TtsTrimMaxValueText != null) TtsTrimMaxValueText.Text = $"{max} ms";
                var svc = App.SettingsProvider; var curr = svc?.Current;
                if (svc != null && curr != null)
                {
                    var next = curr with { Tts = curr.Tts with { TrimMaxMs = max } };
                    svc.Save(next);
                }
            }
            catch (Exception ex) { UpdateStatus($"Error saving Trim Max (ms): {ex.Message}", true); }
        }

        private void TtsPaddingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                var pad = (int)Math.Round(Math.Max(0, Math.Min(200, e.NewValue)));
                TtsPaddingValueText.Text = $"{pad} ms";
                var svc = App.SettingsProvider; var curr = svc?.Current;
                if (svc != null && curr != null)
                {
                    var next = curr with { Tts = curr.Tts with { MinClausePaddingMs = pad } };
                    svc.Save(next);
                }
            }
            catch (Exception ex) { UpdateStatus($"Error saving padding: {ex.Message}", true); }
        }

        private void InitializePacingControls()
        {
            try
            {
                if (_pacingInitialized) return; // avoid resetting slider positions after they were changed by the user

                var ttsSnap = App.SettingsProvider?.Current?.Tts;
                if (ttsSnap == null) { _pacingInitialized = true; return; }

                // Speed slider maps 0.5x..2.0x to 50..200 (we display 1.00x style number)
                var speedVal = ttsSnap.Speed;
                if (TtsSpeedSlider != null)
                {
                    var uiVal = Math.Max(50, Math.Min(200, (int)Math.Round(speedVal * 100))); // 1.05 -> 105
                    TtsSpeedSlider.Value = uiVal;
                    TtsSpeedValueText.Text = $"{speedVal:F2}x";
                    TtsSpeedSlider.ValueChanged -= TtsSpeedSlider_ValueChanged;
                    TtsSpeedSlider.ValueChanged += TtsSpeedSlider_ValueChanged;
                }

                // Trim threshold slider: map 0.0005..0.02 -> 0..100 UI
                var thr = ttsSnap.TrimThreshold;
                if (TrimSilenceThresholdSlider != null)
                {
                    var norm = (thr - 0.0005) / (0.02 - 0.0005); // 0..1
                    var ui = Math.Max(0, Math.Min(100, norm * 100));
                    TrimSilenceThresholdSlider.Value = ui;
                    TrimSilenceThresholdValueText.Text = thr.ToString("F4");
                    TrimSilenceThresholdSlider.ValueChanged -= TrimSilenceThresholdSlider_ValueChanged;
                    TrimSilenceThresholdSlider.ValueChanged += TrimSilenceThresholdSlider_ValueChanged;
                }

                // Trim leave (ms)
                var leaveMs = ttsSnap.TrimLeaveMs;
                if (TtsTrimLeaveSlider != null)
                {
                    TtsTrimLeaveSlider.Value = Math.Max(0, Math.Min(100, leaveMs));
                    if (TtsTrimLeaveValueText != null) TtsTrimLeaveValueText.Text = $"{(int)TtsTrimLeaveSlider.Value} ms";
                    TtsTrimLeaveSlider.ValueChanged -= TtsTrimLeaveSlider_ValueChanged;
                    TtsTrimLeaveSlider.ValueChanged += TtsTrimLeaveSlider_ValueChanged;
                }

                // Trim max (ms)
                var maxMs = ttsSnap.TrimMaxMs;
                if (TtsTrimMaxSlider != null)
                {
                    TtsTrimMaxSlider.Value = Math.Max(50, Math.Min(3000, maxMs));
                    if (TtsTrimMaxValueText != null) TtsTrimMaxValueText.Text = $"{(int)TtsTrimMaxSlider.Value} ms";
                    TtsTrimMaxSlider.ValueChanged -= TtsTrimMaxSlider_ValueChanged;
                    TtsTrimMaxSlider.ValueChanged += TtsTrimMaxSlider_ValueChanged;
                }

                // Padding slider in ms (0..200)
                var padMs = ttsSnap.MinClausePaddingMs;
                if (TtsPaddingSlider != null)
                {
                    TtsPaddingSlider.Value = Math.Max(0, Math.Min(200, padMs));
                    TtsPaddingValueText.Text = $"{(int)TtsPaddingSlider.Value} ms";
                    TtsPaddingSlider.ValueChanged -= TtsPaddingSlider_ValueChanged;
                    TtsPaddingSlider.ValueChanged += TtsPaddingSlider_ValueChanged;
                }

                _pacingInitialized = true;
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error initializing pacing controls: {ex.Message}", true);
            }
        }
    }
}