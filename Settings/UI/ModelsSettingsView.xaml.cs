using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using WinForms = System.Windows.Forms;

namespace Kinectv1.UI.Settings
{
    public partial class ModelsSettingsView : UserControl
    {
        public ModelsSettingsView()
        {
            InitializeComponent();
            Loaded += ModelsSettingsView_Loaded;
        }

        private void ModelsSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            // Populate audio device dropdowns
            TryRefreshAudioDevices();

            // Keep voice list in sync with model folder text
            try { TtsModelFolderTextBox.TextChanged -= TtsModelFolderTextBox_TextChanged; } catch { }
            TtsModelFolderTextBox.TextChanged += TtsModelFolderTextBox_TextChanged;
            // Initial populate
            TryPopulateVoicesFromFolder(TtsModelFolderTextBox.Text);

            // Initialize TTS sliders from current settings snapshot
            try
            {
                var tts = Kinectv1.App.SettingsProvider?.Current?.Tts;
                if (tts != null)
                {
                    LocalVolumeSlider.Value = tts.LocalVolume * 100.0;
                    DiscordVolumeSlider.Value = tts.DiscordVolume * 100.0;
                    TtsSpeedSlider.Value = tts.Speed * 100.0;
                    TrimSilenceThresholdSlider.Value = tts.TrimThreshold * 1000.0;
                    TtsTrimLeaveSlider.Value = tts.TrimLeaveMs;
                    TtsTrimMaxSlider.Value = tts.TrimMaxMs;
                    TtsPaddingSlider.Value = tts.MinClausePaddingMs;
                }
            }
            catch { }

            // Initialize VAD sliders from current settings snapshot
            try
            {
                var asr = Kinectv1.App.SettingsProvider?.Current?.Asr;
                if (asr != null)
                {
                    VadSilenceTimeoutSlider.Value = asr.VadSilenceTimeoutMs;
                    VadDebounceTimeoutSlider.Value = asr.VadDebounceTimeoutMs;
                    BargeInEnabledCheckBox.IsChecked = asr.BargeInEnabled;
                }
            }
            catch { }

            // Show initial numeric values
            UpdateAllSliderValueText();

            // Wire value-changed handlers to keep labels in sync
            LocalVolumeSlider.ValueChanged += (s, _) => { LocalVolumeValueText.Text = string.Format("{0:F0}%", LocalVolumeSlider.Value); };
            DiscordVolumeSlider.ValueChanged += (s, _) => { DiscordVolumeValueText.Text = string.Format("{0:F0}%", DiscordVolumeSlider.Value); };
            TtsSpeedSlider.ValueChanged += (s, _) => { TtsSpeedValueText.Text = string.Format("{0:F2}x", TtsSpeedSlider.Value / 100.0); };
            TrimSilenceThresholdSlider.ValueChanged += (s, _) => { TrimSilenceThresholdValueText.Text = string.Format("{0:F3}", TrimSilenceThresholdSlider.Value / 1000.0); };
            TtsTrimLeaveSlider.ValueChanged += (s, _) => { TtsTrimLeaveValueText.Text = string.Format("{0} ms", (int)TtsTrimLeaveSlider.Value); };
            TtsTrimMaxSlider.ValueChanged += (s, _) => { TtsTrimMaxValueText.Text = string.Format("{0} ms", (int)TtsTrimMaxSlider.Value); };
            TtsPaddingSlider.ValueChanged += (s, _) => { TtsPaddingValueText.Text = string.Format("{0} ms", (int)TtsPaddingSlider.Value); };
            
            // VAD slider value-changed handlers
            VadSilenceTimeoutSlider.ValueChanged += (s, _) => { VadSilenceTimeoutValueText.Text = string.Format("{0} ms", (int)VadSilenceTimeoutSlider.Value); };
            VadDebounceTimeoutSlider.ValueChanged += (s, _) => { VadDebounceTimeoutValueText.Text = string.Format("{0} ms", (int)VadDebounceTimeoutSlider.Value); };
        }

        private void UpdateAllSliderValueText()
        {
            try
            {
                LocalVolumeValueText.Text = string.Format("{0:F0}%", LocalVolumeSlider.Value);
                DiscordVolumeValueText.Text = string.Format("{0:F0}%", DiscordVolumeSlider.Value);
                TtsSpeedValueText.Text = string.Format("{0:F2}x", TtsSpeedSlider.Value / 100.0);
                TrimSilenceThresholdValueText.Text = string.Format("{0:F3}", TrimSilenceThresholdSlider.Value / 1000.0);
                TtsTrimLeaveValueText.Text = string.Format("{0} ms", (int)TtsTrimLeaveSlider.Value);
                TtsTrimMaxValueText.Text = string.Format("{0} ms", (int)TtsTrimMaxSlider.Value);
                TtsPaddingValueText.Text = string.Format("{0} ms", (int)TtsPaddingSlider.Value);
                
                // VAD slider values
                VadSilenceTimeoutValueText.Text = string.Format("{0} ms", (int)VadSilenceTimeoutSlider.Value);
                VadDebounceTimeoutValueText.Text = string.Format("{0} ms", (int)VadDebounceTimeoutSlider.Value);
            }
            catch { }
        }

        private void TryRefreshAudioDevices()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                // Output devices (Render)
                var outs = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                                      .Select(d => d.FriendlyName)
                                      .Where(n => !string.IsNullOrWhiteSpace(n))
                                      .Distinct()
                                      .OrderBy(n => n)
                                      .ToArray();
                TtsOutputDeviceComboBox.ItemsSource = outs;

                // Input devices (Capture)
                var ins = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                                     .Select(d => d.FriendlyName)
                                     .Where(n => !string.IsNullOrWhiteSpace(n))
                                     .Distinct()
                                     .OrderBy(n => n)
                                     .ToArray();
                MicInputComboBox.ItemsSource = ins;

                // Preselect from current settings snapshot when available
                try
                {
                    var snap = Kinectv1.App.SettingsProvider?.Current;
                    var outSaved = snap?.Tts?.OutputDevice;
                    if (!string.IsNullOrWhiteSpace(outSaved))
                    {
                        var match = outs.FirstOrDefault(n => string.Equals(n, outSaved, StringComparison.OrdinalIgnoreCase));
                        if (match != null) TtsOutputDeviceComboBox.SelectedItem = match;
                    }

                    var inSaved = snap?.Stt?.InputDevice;
                    if (!string.IsNullOrWhiteSpace(inSaved))
                    {
                        var matchIn = ins.FirstOrDefault(n => string.Equals(n, inSaved, StringComparison.OrdinalIgnoreCase));
                        if (matchIn != null) MicInputComboBox.SelectedItem = matchIn;
                    }
                }
                catch { }
            }
            catch
            {
                // Do not block UI if device enumeration fails
            }
        }

        private void TtsModelFolderTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TryPopulateVoicesFromFolder(TtsModelFolderTextBox.Text);
        }

        private void TryPopulateVoicesFromFolder(string folder)
        {
            try
            {
                var dir = (folder ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    TtsVoiceComboBox.ItemsSource = Array.Empty<string>();
                    return;
                }

                var voicesDir = Path.Combine(dir, "voices");
                if (!Directory.Exists(voicesDir))
                {
                    TtsVoiceComboBox.ItemsSource = Array.Empty<string>();
                    return;
                }

                var names = Directory.EnumerateFiles(voicesDir, "*.bin", SearchOption.TopDirectoryOnly)
                                      .Select(p => Path.GetFileNameWithoutExtension(p))
                                      .Where(n => !string.IsNullOrWhiteSpace(n))
                                      .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                      .ToArray();
                TtsVoiceComboBox.ItemsSource = names;

                // Preserve current selection if still available
                var currentSel = TtsVoiceComboBox.SelectedItem as string;
                if (!string.IsNullOrWhiteSpace(currentSel) && names.Contains(currentSel, StringComparer.OrdinalIgnoreCase))
                {
                    TtsVoiceComboBox.SelectedItem = names.First(n => string.Equals(n, currentSel, StringComparison.OrdinalIgnoreCase));
                }
                else if (names.Length > 0 && TtsVoiceComboBox.SelectedItem == null)
                {
                    // Do not auto-select to avoid unintended saves; leave empty unless already set
                }
            }
            catch
            {
                // Silent: UI helper, not critical
            }
        }

        private void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            var host = Window.GetWindow(this) as SettingsWindow;
            if (host != null) host.TriggerValidate();
            else UI.Settings.SettingsWindow.CurrentEmbedded?.TriggerValidate();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var host = Window.GetWindow(this) as SettingsWindow;
            if (host != null) host.TriggerSave();
            else UI.Settings.SettingsWindow.CurrentEmbedded?.TriggerSave();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var host = Window.GetWindow(this) as SettingsWindow;
            if (host != null) host.TriggerDefaults();
            else UI.Settings.SettingsWindow.CurrentEmbedded?.TriggerDefaults();
        }

        private void BrowseTtsModelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Select TTS Model (.onnx)",
                    Filter = "ONNX model (*.onnx)|*.onnx|All files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    TtsModelPathTextBox.Text = dlg.FileName;
                    if (string.IsNullOrWhiteSpace(TtsModelFolderTextBox.Text))
                        TtsModelFolderTextBox.Text = Path.GetDirectoryName(dlg.FileName);
                    // Update voices when model path chosen
                    TryPopulateVoicesFromFolder(TtsModelFolderTextBox.Text);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Browse TTS Model", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseTtsModelFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dlg = new WinForms.FolderBrowserDialog { Description = "Select TTS Model Folder" })
                {
                    if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        TtsModelFolderTextBox.Text = dlg.SelectedPath;
                        // Update voices when folder chosen
                        TryPopulateVoicesFromFolder(dlg.SelectedPath);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Browse TTS Model Folder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TtsVoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void ExecutionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        
        private void TestVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = TestPhraseTextBox?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text)) text = "Hello, this is a test.";
                
                var voice = TtsVoiceComboBox?.SelectedItem?.ToString();
                if (string.IsNullOrWhiteSpace(voice))
                {
                    MessageBox.Show("Please select a voice first.", "Test Voice", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                StatusTextBlock.Text = "Testing voice...";
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await Kinectv1.Tts.TtsService.SpeakWithPreemptionAsync(text, voice);
                        Dispatcher.BeginInvoke(new Action(() => StatusTextBlock.Text = "Voice test complete"));
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(() => StatusTextBlock.Text = $"Voice test failed: {ex.Message}"));
                    }
                });
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error: {ex.Message}";
            }
        }

        private void BrowseSttModelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dlg = new WinForms.FolderBrowserDialog { Description = "Select Vosk Model Folder" })
                {
                    if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        SttModelPathTextBox.Text = dlg.SelectedPath;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Browse STT Model", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MicInputComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

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
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Browse Speaker Model", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
