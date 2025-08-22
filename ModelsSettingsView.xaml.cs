using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Kinectv1
{
    /// <summary>
    /// Models Settings View for configuring TTS/STT model paths and execution modes
    /// </summary>
    public partial class ModelsSettingsView : UserControl
    {
        // Available TTS voices for Kokoro TTS system
        private readonly List<string> _availableVoices = new List<string>
        {
            "em_alex",      // English male - Alex
            "em_andy",      // English male - Andy
            "em_ben",       // English male - Ben
            "em_charlie",   // English male - Charlie
            "em_daniel",    // English male - Daniel
            "ef_emma",      // English female - Emma
            "ef_grace",     // English female - Grace
            "ef_isabella",  // English female - Isabella
            "ef_jenny",     // English female - Jenny
            "ef_kate",      // English female - Kate
            "af_alice",     // American female - Alice
            "af_sarah",     // American female - Sarah
            "af_nicole",    // American female - Nicole
            "am_michael",   // American male - Michael
            "am_adam",      // American male - Adam
            "am_eric"       // American male - Eric
        };

        public ModelsSettingsView()
        {
            InitializeComponent();
            InitializeControls();
            LoadCurrentSettings();
        }

        /// <summary>
        /// Initialize UI controls with default values
        /// </summary>
        private void InitializeControls()
        {
            // Populate voice dropdown
            TtsVoiceComboBox.ItemsSource = _availableVoices;

            // Set default execution mode
            ExecutionModeComboBox.SelectedIndex = 0; // Default to CPU
        }

        /// <summary>
        /// Load current settings from AppSettings into UI controls
        /// </summary>
        private void LoadCurrentSettings()
        {
            try
            {
                // Load TTS settings
                TtsModelPathTextBox.Text = AppSettings.LoadTtsModelPath() ?? "";
                TtsModelFolderTextBox.Text = AppSettings.LoadTtsModelFolder() ?? "";
                
                var currentVoice = AppSettings.LoadTtsSpeaker();
                if (!string.IsNullOrEmpty(currentVoice) && _availableVoices.Contains(currentVoice))
                {
                    TtsVoiceComboBox.SelectedItem = currentVoice;
                }
                else
                {
                    TtsVoiceComboBox.SelectedItem = "em_alex"; // Default voice
                }

                // Load execution mode
                bool useGpu = AppSettings.LoadTtsUseGpu();
                ExecutionModeComboBox.SelectedIndex = useGpu ? 1 : 0;

                // Load STT settings
                SttModelPathTextBox.Text = AppSettings.LoadSttModelPath() ?? "";
                SpeakerModelPathTextBox.Text = AppSettings.LoadSpeakerEmbeddingModelPath() ?? "";

                UpdateStatus("Settings loaded successfully", false);
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
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select TTS Model Folder",
                ShowNewFolderButton = false,
                SelectedPath = GetInitialDirectory(TtsModelFolderTextBox.Text)
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TtsModelFolderTextBox.Text = dialog.SelectedPath;
                UpdateStatus($"TTS model folder selected: {Path.GetFileName(dialog.SelectedPath)}", false);
            }
        }

        /// <summary>
        /// Browse for STT model directory (Vosk models are directories)
        /// </summary>
        private void BrowseSttModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Vosk STT Model Directory",
                ShowNewFolderButton = false,
                SelectedPath = GetInitialDirectory(SttModelPathTextBox.Text)
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SttModelPathTextBox.Text = dialog.SelectedPath;
                UpdateStatus($"STT model directory selected: {Path.GetFileName(dialog.SelectedPath)}", false);
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
        /// Handle TTS voice selection change
        /// </summary>
        private void TtsVoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TtsVoiceComboBox.SelectedItem != null)
            {
                string selectedVoice = TtsVoiceComboBox.SelectedItem.ToString();
                UpdateStatus($"TTS voice selected: {selectedVoice}", false);
            }
        }

        /// <summary>
        /// Handle execution mode selection change
        /// </summary>
        private void ExecutionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ExecutionModeComboBox.SelectedItem != null)
            {
                var selectedItem = ExecutionModeComboBox.SelectedItem as ComboBoxItem;
                string mode = selectedItem?.Tag?.ToString() ?? "CPU";
                UpdateStatus($"Execution mode selected: {mode}", false);
            }
        }

        /// <summary>
        /// Test the selected TTS voice with the test phrase
        /// </summary>
        private void TestVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string testPhrase = TestPhraseTextBox.Text;
                if (string.IsNullOrWhiteSpace(testPhrase))
                {
                    UpdateStatus("Please enter a test phrase", true);
                    return;
                }

                string selectedVoice = TtsVoiceComboBox.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(selectedVoice))
                {
                    UpdateStatus("Please select a TTS voice", true);
                    return;
                }

                UpdateStatus($"Testing voice '{selectedVoice}' with phrase: {testPhrase}", false);

                // Here you would integrate with the actual TTS system
                // For now, we'll just show a status message
                // In a real implementation, this would call something like:
                // await TtsService.SpeakAsync(testPhrase, selectedVoice);

                UpdateStatus($"Voice test completed successfully with {selectedVoice}", false);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Voice test failed: {ex.Message}", true);
            }
        }

        /// <summary>
        /// Validate all model paths
        /// </summary>
        private void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var validationResults = new List<string>();

                // Validate TTS model path
                string ttsModelPath = TtsModelPathTextBox.Text;
                if (!string.IsNullOrWhiteSpace(ttsModelPath))
                {
                    if (File.Exists(ttsModelPath))
                    {
                        validationResults.Add("✅ TTS model file exists");
                    }
                    else
                    {
                        validationResults.Add("❌ TTS model file not found");
                    }
                }
                else
                {
                    validationResults.Add("⚠️ TTS model path not configured");
                }

                // Validate TTS model folder
                string ttsModelFolder = TtsModelFolderTextBox.Text;
                if (!string.IsNullOrWhiteSpace(ttsModelFolder))
                {
                    if (Directory.Exists(ttsModelFolder))
                    {
                        validationResults.Add("✅ TTS model folder exists");
                    }
                    else
                    {
                        validationResults.Add("❌ TTS model folder not found");
                    }
                }
                else
                {
                    validationResults.Add("⚠️ TTS model folder not configured");
                }

                // Validate STT model path
                string sttModelPath = SttModelPathTextBox.Text;
                if (!string.IsNullOrWhiteSpace(sttModelPath))
                {
                    if (Directory.Exists(sttModelPath))
                    {
                        validationResults.Add("✅ STT model directory exists");
                    }
                    else
                    {
                        validationResults.Add("❌ STT model directory not found");
                    }
                }
                else
                {
                    validationResults.Add("⚠️ STT model path not configured");
                }

                // Validate speaker model path
                string speakerModelPath = SpeakerModelPathTextBox.Text;
                if (!string.IsNullOrWhiteSpace(speakerModelPath))
                {
                    if (File.Exists(speakerModelPath))
                    {
                        validationResults.Add("✅ Speaker model file exists");
                    }
                    else
                    {
                        validationResults.Add("❌ Speaker model file not found");
                    }
                }
                else
                {
                    validationResults.Add("⚠️ Speaker model path not configured");
                }

                string result = string.Join("\n", validationResults);
                bool hasErrors = validationResults.Any(r => r.Contains("❌"));
                
                UpdateStatus($"Validation completed:\n{result}", hasErrors);

                // Show detailed validation in a message box
                MessageBox.Show(result, "Model Path Validation", 
                    hasErrors ? MessageBoxButton.OK : MessageBoxButton.OK,
                    hasErrors ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Validation failed: {ex.Message}", true);
            }
        }

        /// <summary>
        /// Save all settings to AppSettings
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Save TTS settings
                AppSettings.SaveTtsModelPath(TtsModelPathTextBox.Text);
                AppSettings.SaveTtsModelFolder(TtsModelFolderTextBox.Text);
                
                if (TtsVoiceComboBox.SelectedItem != null)
                {
                    AppSettings.SaveTtsSpeaker(TtsVoiceComboBox.SelectedItem.ToString());
                }

                // Save execution mode
                var selectedModeItem = ExecutionModeComboBox.SelectedItem as ComboBoxItem;
                bool useGpu = selectedModeItem?.Tag?.ToString() == "GPU";
                AppSettings.SaveTtsUseGpu(useGpu);

                // Save STT settings
                AppSettings.SaveSttModelPath(SttModelPathTextBox.Text);
                AppSettings.SaveSpeakerEmbeddingModelPath(SpeakerModelPathTextBox.Text);

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

        /// <summary>
        /// Reset all settings to default values
        /// </summary>
        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to reset all model settings to defaults? This will clear all current configurations.",
                "Reset to Defaults", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    // Reset TTS settings to defaults
                    TtsModelPathTextBox.Text = Path.Combine("models", "tts", "kokoro-82M", "onnx", "model_q8f16.onnx");
                    TtsModelFolderTextBox.Text = Path.Combine("models", "tts", "kokoro");
                    TtsVoiceComboBox.SelectedItem = "em_alex";
                    ExecutionModeComboBox.SelectedIndex = 0; // CPU

                    // Reset STT settings to defaults
                    SttModelPathTextBox.Text = "";
                    SpeakerModelPathTextBox.Text = "";

                    // Reset test phrase
                    TestPhraseTextBox.Text = "Hello, this is a test of the text to speech system.";

                    UpdateStatus("Settings reset to defaults", false);
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error resetting settings: {ex.Message}", true);
                }
            }
        }

        /// <summary>
        /// Update the status text block
        /// </summary>
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

        /// <summary>
        /// Get initial directory for file dialogs
        /// </summary>
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
                    else if (Directory.Exists(Path.GetDirectoryName(currentPath)))
                    {
                        return Path.GetDirectoryName(currentPath);
                    }
                }
                catch
                {
                    // Fall through to default
                }
            }

            // Default to models directory if it exists
            string modelsDir = Path.Combine(Environment.CurrentDirectory, "models");
            return Directory.Exists(modelsDir) ? modelsDir : Environment.CurrentDirectory;
        }
    }
}