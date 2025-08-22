using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Kinectv1.UI.Settings.Editors
{
    /// <summary>
    /// Interaction logic for PathsSettingsView.xaml
    /// Provides UI for configuring file and folder paths for TTS, STT, conversation history, and system prompts
    /// </summary>
    public partial class PathsSettingsView : UserControl
    {
        private bool _isLoading = false;
        private bool _useRelativePaths = false;

        public PathsSettingsView()
        {
            InitializeComponent();
            LoadSettings();
        }

        #region Settings Load/Save

        private void LoadSettings()
        {
            _isLoading = true;
            try
            {
                // Load path values from AppSettings
                TtsModelPathTextBox.Text = AppSettings.LoadTtsModelPath() ?? "";
                TtsModelFolderTextBox.Text = AppSettings.LoadTtsModelFolder() ?? "";
                SttModelPathTextBox.Text = AppSettings.LoadSttModelPath() ?? "";
                ConversationHistoryPathTextBox.Text = AppSettings.LoadConversationHistoryPath() ?? "";
                SystemPromptPathTextBox.Text = AppSettings.LoadSystemPromptPath() ?? "";

                // For now, default to absolute paths (relative path feature can be added later)
                UseRelativePathsCheckBox.IsChecked = false;
                _useRelativePaths = false;
                UpdatePathTypeHelp();

                // Validate all paths on load
                ValidateAllPaths();
                UpdateStatus("Settings loaded");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading settings: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void SaveSettings()
        {
            try
            {
                // Save all path settings
                AppSettings.SaveTtsModelPath(TtsModelPathTextBox.Text);
                AppSettings.SaveTtsModelFolder(TtsModelFolderTextBox.Text);
                AppSettings.SaveSttModelPath(SttModelPathTextBox.Text);
                AppSettings.SaveConversationHistoryPath(ConversationHistoryPathTextBox.Text);
                AppSettings.SaveSystemPromptPath(SystemPromptPathTextBox.Text);

                UpdateStatus("Settings saved successfully");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error saving settings: {ex.Message}");
            }
        }

        #endregion

        #region Event Handlers

        private void UseRelativePathsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            _useRelativePaths = UseRelativePathsCheckBox.IsChecked == true;
            UpdatePathTypeHelp();
            
            // Convert existing paths if needed (placeholder for future implementation)
            if (_useRelativePaths)
            {
                UpdateStatus("Relative paths selected (conversion not yet implemented)");
            }
            else
            {
                UpdateStatus("Using absolute paths");
            }
        }

        private void PathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading) return;

            var textBox = sender as TextBox;
            if (textBox == null) return;

            // Real-time validation for the changed path
            ValidatePath(textBox);
        }

        private void BrowseTtsModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select TTS Model File",
                Filter = "ONNX Model Files (*.onnx)|*.onnx|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            if (!string.IsNullOrEmpty(TtsModelPathTextBox.Text))
            {
                try
                {
                    var dir = Path.GetDirectoryName(TtsModelPathTextBox.Text);
                    if (Directory.Exists(dir))
                        dialog.InitialDirectory = dir;
                }
                catch { }
            }

            if (dialog.ShowDialog() == true)
            {
                TtsModelPathTextBox.Text = dialog.FileName;
                ValidatePath(TtsModelPathTextBox);
            }
        }

        private void BrowseTtsModelFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select TTS Model Folder",
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrEmpty(TtsModelFolderTextBox.Text) && Directory.Exists(TtsModelFolderTextBox.Text))
            {
                dialog.SelectedPath = TtsModelFolderTextBox.Text;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TtsModelFolderTextBox.Text = dialog.SelectedPath;
                ValidatePath(TtsModelFolderTextBox);
            }
        }

        private void BrowseSttModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select STT (Vosk) Model Folder",
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrEmpty(SttModelPathTextBox.Text) && Directory.Exists(SttModelPathTextBox.Text))
            {
                dialog.SelectedPath = SttModelPathTextBox.Text;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SttModelPathTextBox.Text = dialog.SelectedPath;
                ValidatePath(SttModelPathTextBox);
            }
        }

        private void BrowseConversationHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Conversation History Folder",
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrEmpty(ConversationHistoryPathTextBox.Text) && Directory.Exists(ConversationHistoryPathTextBox.Text))
            {
                dialog.SelectedPath = ConversationHistoryPathTextBox.Text;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ConversationHistoryPathTextBox.Text = dialog.SelectedPath;
                ValidatePath(ConversationHistoryPathTextBox);
            }
        }

        private void BrowseSystemPromptButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select System Prompt File",
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            if (!string.IsNullOrEmpty(SystemPromptPathTextBox.Text))
            {
                try
                {
                    var dir = Path.GetDirectoryName(SystemPromptPathTextBox.Text);
                    if (Directory.Exists(dir))
                        dialog.InitialDirectory = dir;
                }
                catch { }
            }

            if (dialog.ShowDialog() == true)
            {
                SystemPromptPathTextBox.Text = dialog.FileName;
                ValidatePath(SystemPromptPathTextBox);
            }
        }

        private void CreateFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.Tag == null) return;

            var pathType = button.Tag.ToString();
            TextBox targetTextBox = null;
            string folderDescription = "";

            switch (pathType)
            {
                case "TtsModel":
                    targetTextBox = TtsModelPathTextBox;
                    folderDescription = "TTS model";
                    break;
                case "TtsModelFolder":
                    targetTextBox = TtsModelFolderTextBox;
                    folderDescription = "TTS model folder";
                    break;
                case "SttModel":
                    targetTextBox = SttModelPathTextBox;
                    folderDescription = "STT model";
                    break;
                case "ConversationHistory":
                    targetTextBox = ConversationHistoryPathTextBox;
                    folderDescription = "conversation history";
                    break;
                case "SystemPrompt":
                    targetTextBox = SystemPromptPathTextBox;
                    folderDescription = "system prompt";
                    break;
            }

            if (targetTextBox == null) return;

            var path = targetTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(path))
            {
                UpdateStatus($"Please enter a path for {folderDescription} before creating folder");
                return;
            }

            try
            {
                string folderPath;
                
                // For file paths, get the directory
                if (pathType == "TtsModel" || pathType == "SystemPrompt")
                {
                    folderPath = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(folderPath))
                    {
                        UpdateStatus($"Invalid file path for {folderDescription}");
                        return;
                    }
                }
                else
                {
                    folderPath = path;
                }

                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                    UpdateStatus($"Created folder: {folderPath}");
                    ValidatePath(targetTextBox);
                }
                else
                {
                    UpdateStatus($"Folder already exists: {folderPath}");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error creating folder: {ex.Message}");
            }
        }

        private void ValidatePathsButton_Click(object sender, RoutedEventArgs e)
        {
            ValidateAllPaths();
        }

        private void ResetPathsButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will reset all paths to their default values. Continue?",
                "Reset Paths",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                ResetToDefaults();
            }
        }

        private void SavePathsButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }

        #endregion

        #region Path Validation

        private void ValidatePath(TextBox textBox)
        {
            if (textBox == null) return;

            var path = textBox.Text?.Trim();
            TextBlock warningTextBlock = null;
            bool isFile = false;

            // Determine which path we're validating
            if (textBox == TtsModelPathTextBox)
            {
                warningTextBlock = TtsModelPathWarning;
                isFile = true;
            }
            else if (textBox == TtsModelFolderTextBox)
            {
                warningTextBlock = TtsModelFolderWarning;
            }
            else if (textBox == SttModelPathTextBox)
            {
                warningTextBlock = SttModelPathWarning;
            }
            else if (textBox == ConversationHistoryPathTextBox)
            {
                warningTextBlock = ConversationHistoryPathWarning;
            }
            else if (textBox == SystemPromptPathTextBox)
            {
                warningTextBlock = SystemPromptPathWarning;
                isFile = true;
            }

            if (warningTextBlock == null) return;

            // Validate the path
            string warning = ValidatePathString(path, isFile);
            
            if (string.IsNullOrEmpty(warning))
            {
                warningTextBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                warningTextBlock.Text = warning;
                warningTextBlock.Visibility = Visibility.Visible;
            }
        }

        private string ValidatePathString(string path, bool isFile)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Path is required";

            try
            {
                // Check if path format is valid
                Path.GetFullPath(path);
                
                if (isFile)
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!Directory.Exists(directory))
                        return "Directory does not exist";
                    
                    if (!File.Exists(path))
                        return "File does not exist";
                }
                else
                {
                    if (!Directory.Exists(path))
                        return "Directory does not exist";
                }
            }
            catch (Exception ex)
            {
                return $"Invalid path: {ex.Message}";
            }

            return null; // Valid
        }

        private void ValidateAllPaths()
        {
            ValidatePath(TtsModelPathTextBox);
            ValidatePath(TtsModelFolderTextBox);
            ValidatePath(SttModelPathTextBox);
            ValidatePath(ConversationHistoryPathTextBox);
            ValidatePath(SystemPromptPathTextBox);

            UpdateStatus("Path validation completed");
        }

        #endregion

        #region Helper Methods

        private void UpdatePathTypeHelp()
        {
            PathTypeHelpText.Text = _useRelativePaths 
                ? "Currently using relative paths" 
                : "Currently using absolute paths";
        }

        private void UpdateStatus(string message)
        {
            StatusTextBlock.Text = message;
        }

        private void ResetToDefaults()
        {
            try
            {
                _isLoading = true;

                // Set default paths (these can be customized based on application requirements)
                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                
                TtsModelPathTextBox.Text = Path.Combine(appDir, "models", "tts", "model.onnx");
                TtsModelFolderTextBox.Text = Path.Combine(appDir, "models", "tts");
                SttModelPathTextBox.Text = Path.Combine(appDir, "models", "vosk");
                ConversationHistoryPathTextBox.Text = Path.Combine(appDir, "history");
                SystemPromptPathTextBox.Text = Path.Combine(appDir, "prompts", "system.txt");

                UseRelativePathsCheckBox.IsChecked = false;
                _useRelativePaths = false;
                UpdatePathTypeHelp();

                ValidateAllPaths();
                UpdateStatus("Paths reset to defaults");
            }
            finally
            {
                _isLoading = false;
            }
        }

        #endregion
    }
}