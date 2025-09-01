using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Kinectv1.Settings;

namespace Kinectv1.UI.Settings
{
    /// <summary>
    /// Settings window with TreeView navigation and ContentPresenter for settings panels
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private SettingsViewModel _viewModel;
        private SettingsService _svc => App.SettingsProvider;
        private Kinectv1.ModelsSettingsView _attachedEditor;

        public SettingsWindow()
        {
            InitializeComponent();
            _viewModel = new SettingsViewModel();
            DataContext = _viewModel;
            this.Loaded += SettingsWindow_Loaded;
            AttachDirtyHandlersToCurrentEditor();
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var snapshot = _svc?.Current;
                if (snapshot != null)
                {
                    var editor = _viewModel?.SelectedCategory?.EditorView as Kinectv1.ModelsSettingsView;
                    if (editor != null)
                    {
                        // TTS
                        editor.TtsEnabledCheckBox.IsChecked = snapshot.Tts.Enabled;
                        editor.TtsModelPathTextBox.Text = snapshot.Tts.ModelPath;
                        editor.TtsModelFolderTextBox.Text = snapshot.Tts.ModelFolder;
                        editor.TtsVocoderPathTextBox.Text = snapshot.Tts.VocoderPath;
                        foreach (var item in editor.ExecutionModeComboBox.Items)
                        {
                            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), snapshot.Tts.Execution.ToString(), StringComparison.OrdinalIgnoreCase))
                            {
                                editor.ExecutionModeComboBox.SelectedItem = cbi;
                                break;
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(snapshot.Tts.Speaker))
                        {
                            editor.TtsVoiceComboBox.SelectedItem = snapshot.Tts.Speaker;
                        }

                        // Audio
                        editor.AudioVoiceThresholdTextBox.Text = snapshot.Audio.VoiceThreshold.ToString(CultureInfo.InvariantCulture);
                        editor.AudioVadThresholdTextBox.Text = snapshot.Audio.VadThreshold.ToString(CultureInfo.InvariantCulture);
                        editor.AudioBufferSizeTextBox.Text = snapshot.Audio.BufferSize.ToString(CultureInfo.InvariantCulture);

                        // VAD
                        editor.VadThresholdTextBox.Text = snapshot.Vad.Threshold.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            catch { /* ignore to avoid blocking window load */ }
        }

        private void CategoriesTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is SettingsCategoryVM category)
            {
                _viewModel.SelectedCategory = category;
                AttachDirtyHandlersToCurrentEditor();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_viewModel?.HasUnsavedChanges == true)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Do you want to save before closing?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                switch (result)
                {
                    case MessageBoxResult.Yes:
                        Save_Click(this, new RoutedEventArgs());
                        break;
                    case MessageBoxResult.Cancel:
                        e.Cancel = true;
                        return;
                }
            }

            base.OnClosing(e);
        }

        private global::Kinectv1.Settings.AppSettings BuildFromUI()
        {
            var editor = _viewModel?.SelectedCategory?.EditorView as Kinectv1.ModelsSettingsView;
            if (editor == null) throw new InvalidOperationException("Settings editor not available");

            // TTS
            bool ttsEnabled = editor.TtsEnabledCheckBox.IsChecked ?? false;
            string ttsModel = editor.TtsModelPathTextBox.Text ?? string.Empty;
            string ttsFolder = editor.TtsModelFolderTextBox.Text ?? string.Empty;
            string ttsVocoder = editor.TtsVocoderPathTextBox.Text ?? string.Empty;
            string speaker = editor.TtsVoiceComboBox.SelectedItem?.ToString() ?? string.Empty;
            string execTag = (editor.ExecutionModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "CPU";
            var exec = string.Equals(execTag, "GPU", StringComparison.OrdinalIgnoreCase) ? global::Kinectv1.Settings.TtsExecution.GPU : global::Kinectv1.Settings.TtsExecution.CPU;

            // Audio (strict parse; fail fast)
            double voiceThreshold = double.Parse(editor.AudioVoiceThresholdTextBox.Text, CultureInfo.InvariantCulture);
            int audioVadThreshold = int.Parse(editor.AudioVadThresholdTextBox.Text, CultureInfo.InvariantCulture);
            int bufferSize = int.Parse(editor.AudioBufferSizeTextBox.Text, CultureInfo.InvariantCulture);

            // VAD
            int vadThreshold = int.Parse(editor.VadThresholdTextBox.Text, CultureInfo.InvariantCulture);

            // New TTS UI fields pulled from the editor controls / AppSettings live values
            string outputDevice = AppSettings.LoadTtsOutputDevice();
            double localVolume = AppSettings.LoadLocalTtsVolume();
            double discordVolume = AppSettings.LoadDiscordTtsVolume();
            float speed = AppSettings.LoadTtsSpeed();
            double trimThreshold = AppSettings.LoadTtsTrimThreshold();
            int trimLeaveMs = AppSettings.LoadTtsTrimLeaveMs();
            int trimMaxMs = AppSettings.LoadTtsTrimMaxMs();
            int minClausePaddingMs = AppSettings.LoadTtsMinClausePaddingMs();
            int ipaServiceTimeoutMs = AppSettings.LoadTtsIpaServiceTimeoutMs();
            int ipaOneShotTimeoutMs = AppSettings.LoadTtsIpaOneShotTimeoutMs();

            var audio = new global::Kinectv1.Settings.AudioSettings(voiceThreshold, audioVadThreshold, bufferSize);
            var vad = new global::Kinectv1.Settings.VadSettings(vadThreshold);

            var tts = new global::Kinectv1.Settings.TtsSettings(
                Enabled: ttsEnabled,
                Speaker: string.IsNullOrWhiteSpace(speaker) ? speaker : speaker,
                Execution: exec,
                ModelFolder: ttsFolder,
                ModelPath: ttsModel,
                VocoderPath: ttsVocoder,
                OutputDevice: outputDevice,
                LocalVolume: localVolume,
                DiscordVolume: discordVolume,
                Speed: speed,
                TrimThreshold: trimThreshold,
                TrimLeaveMs: trimLeaveMs,
                TrimMaxMs: trimMaxMs,
                MinClausePaddingMs: minClausePaddingMs,
                IpaServiceTimeoutMs: ipaServiceTimeoutMs,
                IpaOneShotTimeoutMs: ipaOneShotTimeoutMs
            );
            return new global::Kinectv1.Settings.AppSettings(audio, tts, vad);
        }

        private void Verify_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var candidate = BuildFromUI();
                SettingsValidation.ValidateOrThrow(candidate);
                MessageBox.Show("Settings are valid.", "Verify", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var candidate = BuildFromUI();
                SettingsValidation.ValidateOrThrow(candidate);
                _svc.Save(_ => candidate);
                _viewModel.RefreshSnapshotFromService();
                _viewModel.HasUnsavedChanges = false;
                MessageBox.Show("Settings saved.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Defaults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var defaults = _svc.GetDefaults();
                var editor = _viewModel?.SelectedCategory?.EditorView as Kinectv1.ModelsSettingsView;
                if (editor != null)
                {
                    // TTS
                    editor.TtsEnabledCheckBox.IsChecked = defaults.Tts.Enabled;
                    editor.TtsModelPathTextBox.Text = defaults.Tts.ModelPath;
                    editor.TtsModelFolderTextBox.Text = defaults.Tts.ModelFolder;
                    editor.TtsVocoderPathTextBox.Text = defaults.Tts.VocoderPath;
                    foreach (var item in editor.ExecutionModeComboBox.Items)
                    {
                        if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), defaults.Tts.Execution.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            editor.ExecutionModeComboBox.SelectedItem = cbi;
                            break;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(defaults.Tts.Speaker))
                    {
                        editor.TtsVoiceComboBox.SelectedItem = defaults.Tts.Speaker;
                    }

                    // Audio
                    editor.AudioVoiceThresholdTextBox.Text = defaults.Audio.VoiceThreshold.ToString(CultureInfo.InvariantCulture);
                    editor.AudioVadThresholdTextBox.Text = defaults.Audio.VadThreshold.ToString(CultureInfo.InvariantCulture);
                    editor.AudioBufferSizeTextBox.Text = defaults.Audio.BufferSize.ToString(CultureInfo.InvariantCulture);

                    // VAD
                    editor.VadThresholdTextBox.Text = defaults.Vad.Threshold.ToString(CultureInfo.InvariantCulture);
                }
                _viewModel.HasUnsavedChanges = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Defaults Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var snapshot = _svc.Reload();
                var editor = _viewModel?.SelectedCategory?.EditorView as Kinectv1.ModelsSettingsView;
                if (editor != null)
                {
                    // TTS
                    editor.TtsEnabledCheckBox.IsChecked = snapshot.Tts.Enabled;
                    editor.TtsModelPathTextBox.Text = snapshot.Tts.ModelPath;
                    editor.TtsModelFolderTextBox.Text = snapshot.Tts.ModelFolder;
                    editor.TtsVocoderPathTextBox.Text = snapshot.Tts.VocoderPath;
                    foreach (var item in editor.ExecutionModeComboBox.Items)
                    {
                        if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), snapshot.Tts.Execution.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            editor.ExecutionModeComboBox.SelectedItem = cbi;
                            break;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(snapshot.Tts.Speaker))
                    {
                        editor.TtsVoiceComboBox.SelectedItem = snapshot.Tts.Speaker;
                    }

                    // Audio
                    editor.AudioVoiceThresholdTextBox.Text = snapshot.Audio.VoiceThreshold.ToString(CultureInfo.InvariantCulture);
                    editor.AudioVadThresholdTextBox.Text = snapshot.Audio.VadThreshold.ToString(CultureInfo.InvariantCulture);
                    editor.AudioBufferSizeTextBox.Text = snapshot.Audio.BufferSize.ToString(CultureInfo.InvariantCulture);

                    // VAD
                    editor.VadThresholdTextBox.Text = snapshot.Vad.Threshold.ToString(CultureInfo.InvariantCulture);
                }
                _viewModel.RefreshSnapshotFromService();
                _viewModel.HasUnsavedChanges = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Reload Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AttachDirtyHandlersToCurrentEditor()
        {
            // Detach previous
            if (_attachedEditor != null)
            {
                _attachedEditor.TtsModelPathTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.TtsModelFolderTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.TtsVocoderPathTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.TtsEnabledCheckBox.Checked -= OnEditorDirty;
                _attachedEditor.TtsEnabledCheckBox.Unchecked -= OnEditorDirty;
                _attachedEditor.TtsVoiceComboBox.SelectionChanged -= OnEditorDirtySelection;
                _attachedEditor.ExecutionModeComboBox.SelectionChanged -= OnEditorDirtySelection;
                _attachedEditor.SttModelPathTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.SpeakerModelPathTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.ArcFaceModelPathTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.AudioVoiceThresholdTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.AudioVadThresholdTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.AudioBufferSizeTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.VadThresholdTextBox.TextChanged -= OnEditorDirty;
            }

            _attachedEditor = _viewModel?.SelectedCategory?.EditorView as Kinectv1.ModelsSettingsView;
            if (_attachedEditor != null)
            {
                _attachedEditor.TtsModelPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.TtsModelFolderTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.TtsVocoderPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.TtsEnabledCheckBox.Checked += OnEditorDirty;
                _attachedEditor.TtsEnabledCheckBox.Unchecked += OnEditorDirty;
                _attachedEditor.TtsVoiceComboBox.SelectionChanged += OnEditorDirtySelection;
                _attachedEditor.ExecutionModeComboBox.SelectionChanged += OnEditorDirtySelection;
                _attachedEditor.SttModelPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.SpeakerModelPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.ArcFaceModelPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.AudioVoiceThresholdTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.AudioVadThresholdTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.AudioBufferSizeTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.VadThresholdTextBox.TextChanged += OnEditorDirty;
            }
        }

        private void OnEditorDirty(object sender, EventArgs e)
        {
            if (_viewModel != null) _viewModel.HasUnsavedChanges = true;
        }

        private void OnEditorDirtySelection(object sender, SelectionChangedEventArgs e)
        {
            if (_viewModel != null) _viewModel.HasUnsavedChanges = true;
        }
    }
}