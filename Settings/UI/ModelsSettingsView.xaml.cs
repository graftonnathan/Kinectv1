using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Kinectv1.UI.Settings
{
    public partial class ModelsSettingsView : UserControl
    {
        public ModelsSettingsView()
        {
            InitializeComponent();
            LocalVolumeSlider.ValueChanged += Slider_ValueChanged;
            DiscordVolumeSlider.ValueChanged += Slider_ValueChanged;
            TtsSpeedSlider.ValueChanged += Slider_ValueChanged;
            TrimSilenceThresholdSlider.ValueChanged += Slider_ValueChanged;
            TtsTrimLeaveSlider.ValueChanged += Slider_ValueChanged;
            TtsTrimMaxSlider.ValueChanged += Slider_ValueChanged;
            TtsPaddingSlider.ValueChanged += Slider_ValueChanged;
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
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Browse TTS Model Folder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseTtsVocoderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Select TTS Vocoder (.onnx)",
                    Filter = "ONNX model (*.onnx)|*.onnx|All files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    TtsVocoderPathTextBox.Text = dlg.FileName;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Browse Vocoder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TtsVoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void ExecutionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private async void TestVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = TestPhraseTextBox.Text;
                var voice = TtsVoiceComboBox.SelectedItem?.ToString();
                await CoquiTtsService.SpeakStreamingWithPreemptionAsync(text, voice);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "TTS Test", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void BrowseArcFaceModelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Select ArcFace Model (.onnx)",
                    Filter = "ONNX model (*.onnx)|*.onnx|All files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    ArcFaceModelPathTextBox.Text = dlg.FileName;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Browse ArcFace Model", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            (Window.GetWindow(this) as SettingsWindow)?.TriggerValidate();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            (Window.GetWindow(this) as SettingsWindow)?.TriggerSave();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            (Window.GetWindow(this) as SettingsWindow)?.TriggerDefaults();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender == LocalVolumeSlider)
                LocalVolumeValueText.Text = $"{LocalVolumeSlider.Value:F0}%";
            else if (sender == DiscordVolumeSlider)
                DiscordVolumeValueText.Text = $"{DiscordVolumeSlider.Value:F0}%";
            else if (sender == TtsSpeedSlider)
                TtsSpeedValueText.Text = $"{(TtsSpeedSlider.Value / 100.0):F2}";
            else if (sender == TrimSilenceThresholdSlider)
                TrimSilenceThresholdValueText.Text = $"{(TrimSilenceThresholdSlider.Value / 1000.0):F3}";
            else if (sender == TtsTrimLeaveSlider)
                TtsTrimLeaveValueText.Text = $"{TtsTrimLeaveSlider.Value:F0}";
            else if (sender == TtsTrimMaxSlider)
                TtsTrimMaxValueText.Text = $"{TtsTrimMaxSlider.Value:F0}";
            else if (sender == TtsPaddingSlider)
                TtsPaddingValueText.Text = $"{TtsPaddingSlider.Value:F0}";
        }
    }
}
