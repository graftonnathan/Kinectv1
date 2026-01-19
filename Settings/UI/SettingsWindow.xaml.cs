using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kinectv1.Settings;
using Kinectv1;

namespace Kinectv1.UI.Settings
{
    /// <summary>
    /// Settings window with TreeView navigation and ContentPresenter for settings panels
    /// </summary>
    public partial class SettingsWindow : Window
    {
        // When embedded into another window, host can set this so child views can route actions
        public static SettingsWindow CurrentEmbedded { get; set; }

        private SettingsViewModel _viewModel;
        private SettingsService _svc => App.SettingsProvider;
        private ModelsSettingsView _attachedEditor;
        private bool _suppressDirty; // prevent dirty flag during programmatic updates

        public SettingsWindow()
        {
            InitializeComponent();
            _viewModel = new SettingsViewModel();
            DataContext = _viewModel;
            this.Loaded += SettingsWindow_Loaded;
            AttachDirtyHandlersToCurrentEditor();
        }

        // Expose actions so the embedded editor (ModelsSettingsView) can forward button clicks
        public void TriggerValidate() => Verify_Click(this, new RoutedEventArgs());
        public void TriggerSave() => Save_Click(this, new RoutedEventArgs());
        public void TriggerDefaults() => Defaults_Click(this, new RoutedEventArgs());

        // Added: allow host to manually populate editor when embedding (Loaded won't fire)
        public void PopulateEditorFromCurrentSnapshot()
        {
            // Reuse existing load handler logic for consistency
            SettingsWindow_Loaded(this, new RoutedEventArgs());
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _suppressDirty = true;
                var snapshot = _svc?.Current;
                if (snapshot != null)
                {
                    var editor = _viewModel?.SelectedCategory?.EditorView as ModelsSettingsView;
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
                        // Extra TTS fields
                        if (editor.TtsOutputDeviceComboBox != null)
                        {
                            editor.TtsOutputDeviceComboBox.Text = snapshot.Tts.OutputDevice ?? string.Empty;
                        }
                        if (editor.TtsIpaServiceTimeoutTextBox != null)
                        {
                            editor.TtsIpaServiceTimeoutTextBox.Text = snapshot.Tts.IpaServiceTimeoutMs.ToString(CultureInfo.InvariantCulture);
                        }
                        if (editor.TtsIpaOneShotTimeoutTextBox != null)
                        {
                            editor.TtsIpaOneShotTimeoutTextBox.Text = snapshot.Tts.IpaOneShotTimeoutMs.ToString(CultureInfo.InvariantCulture);
                        }
                        // Initialize all sliders from snapshot
                        editor.LocalVolumeSlider.Value = snapshot.Tts.LocalVolume * 100;
                        editor.DiscordVolumeSlider.Value = snapshot.Tts.DiscordVolume * 100;
                        editor.TtsSpeedSlider.Value = snapshot.Tts.Speed * 100;
                        editor.TrimSilenceThresholdSlider.Value = snapshot.Tts.TrimThreshold * 1000;
                        editor.TtsTrimLeaveSlider.Value = snapshot.Tts.TrimLeaveMs;
                        editor.TtsTrimMaxSlider.Value = snapshot.Tts.TrimMaxMs;
                        editor.TtsPaddingSlider.Value = snapshot.Tts.MinClausePaddingMs;

                        // STT
                        if (editor.SttModelPathTextBox != null)
                            editor.SttModelPathTextBox.Text = snapshot.Stt.ModelPath ?? string.Empty;
                        if (editor.MicInputComboBox != null)
                            editor.MicInputComboBox.Text = snapshot.Stt.InputDevice ?? string.Empty;

                        // Audio
                        // Show normalized voice threshold as RMS 0-10000 integer
                        editor.AudioVoiceThresholdTextBox.Text = ((int)Math.Round(snapshot.Audio.VoiceThreshold * 10000.0)).ToString(CultureInfo.InvariantCulture);
                        if (editor.AudioVoiceHighThresholdTextBox != null)
                            editor.AudioVoiceHighThresholdTextBox.Text = snapshot.Asr.VoiceHighConfidenceThreshold.ToString(CultureInfo.InvariantCulture);
                        if (editor.VoiceConfidenceLoggingCheckBox != null)
                            editor.VoiceConfidenceLoggingCheckBox.IsChecked = snapshot.Asr.VoiceConfidenceLoggingEnabled;
                        editor.AudioBufferSizeTextBox.Text = snapshot.Audio.BufferSize.ToString(CultureInfo.InvariantCulture);
                        // New: Speaker match min score
                        if (editor.SpeakerMatchThresholdTextBox != null)
                        {
                            editor.SpeakerMatchThresholdTextBox.Text = snapshot.Audio.SpeakerMatchMinScore.ToString(CultureInfo.InvariantCulture);
                        }

                        // VAD legacy control removed; wake word only now
                        if (editor.RequireWakeWordCheckBox != null)
                            editor.RequireWakeWordCheckBox.IsChecked = snapshot.App.RequireWakeWord;

                        // Face
                        if (editor.FaceThresholdTextBox != null)
                            editor.FaceThresholdTextBox.Text = snapshot.Face.Threshold.ToString(CultureInfo.InvariantCulture);
                        if (editor.FusionFaceWeightTextBox != null)
                            editor.FusionFaceWeightTextBox.Text = snapshot.Face.FusionFaceWeight.ToString(CultureInfo.InvariantCulture);
                        if (editor.FusionVoiceWeightTextBox != null)
                            editor.FusionVoiceWeightTextBox.Text = snapshot.Face.FusionVoiceWeight.ToString(CultureInfo.InvariantCulture);
                        if (editor.FusionHalfLifeTextBox != null)
                            editor.FusionHalfLifeTextBox.Text = snapshot.Face.FusionDecayHalfLifeMs.ToString(CultureInfo.InvariantCulture);
                        if (editor.FusionUnknownThresholdTextBox != null)
                            editor.FusionUnknownThresholdTextBox.Text = snapshot.Face.FusionUnknownThreshold.ToString(CultureInfo.InvariantCulture);
                        if (editor.ArcFaceModelPathTextBox != null)
                            editor.ArcFaceModelPathTextBox.Text = snapshot.Face.ArcFaceModelPath ?? string.Empty;
                        if (editor.SpeakerModelPathTextBox != null)
                            editor.SpeakerModelPathTextBox.Text = snapshot.Face.SpeakerEmbeddingModelPath ?? string.Empty;
                    }
                }
                _viewModel.HasUnsavedChanges = false; // initial load is clean
            }
            catch { /* ignore to avoid blocking window load */ }
            finally { _suppressDirty = false; }
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
            var editor = _viewModel?.SelectedCategory?.EditorView as ModelsSettingsView;
            if (editor == null) throw new InvalidOperationException("Settings editor not available");

            var current = _svc?.Current ?? throw new InvalidOperationException("Settings snapshot unavailable");

            // TTS
            bool ttsEnabled = editor.TtsEnabledCheckBox.IsChecked ?? false;
            string ttsModel = editor.TtsModelPathTextBox.Text ?? string.Empty;
            string ttsFolder = editor.TtsModelFolderTextBox.Text ?? string.Empty;
            string ttsVocoder = editor.TtsVocoderPathTextBox.Text ?? string.Empty;
            string speaker = editor.TtsVoiceComboBox.SelectedItem?.ToString() ?? current.Tts.Speaker;
            string execTag = (editor.ExecutionModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? current.Tts.Execution.ToString();
            var exec = string.Equals(execTag, "GPU", StringComparison.OrdinalIgnoreCase) ? global::Kinectv1.Settings.TtsExecution.GPU : global::Kinectv1.Settings.TtsExecution.CPU;
            string outputDevice = editor.TtsOutputDeviceComboBox?.Text ?? current.Tts.OutputDevice;
            int ipaServiceTimeoutMs = current.Tts.IpaServiceTimeoutMs;
            int ipaOneShotTimeoutMs = current.Tts.IpaOneShotTimeoutMs;
            if (!string.IsNullOrWhiteSpace(editor.TtsIpaServiceTimeoutTextBox?.Text))
                ipaServiceTimeoutMs = int.Parse(editor.TtsIpaServiceTimeoutTextBox.Text, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(editor.TtsIpaOneShotTimeoutTextBox?.Text))
                ipaOneShotTimeoutMs = int.Parse(editor.TtsIpaOneShotTimeoutTextBox.Text, CultureInfo.InvariantCulture);

            // Audio: parse RMS (0-10000) then normalize 0..1
            double voiceThresholdRms = double.Parse(editor.AudioVoiceThresholdTextBox.Text, CultureInfo.InvariantCulture);
            if (voiceThresholdRms < 0 || voiceThresholdRms > 10000) throw new InvalidOperationException("Voice threshold RMS must be 0–10000");
            double voiceThreshold = voiceThresholdRms / 10000.0; // normalized stored value
            int bufferSize = int.Parse(editor.AudioBufferSizeTextBox.Text, CultureInfo.InvariantCulture);
            double speakerMatchMin = 0.6;
            if (editor.SpeakerMatchThresholdTextBox != null && !string.IsNullOrWhiteSpace(editor.SpeakerMatchThresholdTextBox.Text))
                speakerMatchMin = double.Parse(editor.SpeakerMatchThresholdTextBox.Text, CultureInfo.InvariantCulture);

            // ASR (partial, mapped to visible fields)
            double asrHighConf = current.Asr.VoiceHighConfidenceThreshold;
            bool asrLogging = current.Asr.VoiceConfidenceLoggingEnabled;
            if (!string.IsNullOrWhiteSpace(editor.AudioVoiceHighThresholdTextBox?.Text))
                asrHighConf = double.Parse(editor.AudioVoiceHighThresholdTextBox.Text, CultureInfo.InvariantCulture);
            if (editor.VoiceConfidenceLoggingCheckBox != null)
                asrLogging = editor.VoiceConfidenceLoggingCheckBox.IsChecked ?? current.Asr.VoiceConfidenceLoggingEnabled;

            // VAD (legacy removed)

            // STT
            string sttModelPath = editor.SttModelPathTextBox?.Text ?? current.Stt.ModelPath;
            string sttInputDevice = editor.MicInputComboBox?.Text ?? current.Stt.InputDevice;

            // Face
            double faceThreshold = current.Face.Threshold;
            double faceW = current.Face.FusionFaceWeight;
            double voiceW = current.Face.FusionVoiceWeight;
            int halfLife = current.Face.FusionDecayHalfLifeMs;
            double unkThresh = current.Face.FusionUnknownThreshold;
            string arcPath = current.Face.ArcFaceModelPath;
            string spkPath = current.Face.SpeakerEmbeddingModelPath;
            if (!string.IsNullOrWhiteSpace(editor.FaceThresholdTextBox?.Text))
                faceThreshold = double.Parse(editor.FaceThresholdTextBox.Text, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(editor.FusionFaceWeightTextBox?.Text))
                faceW = double.Parse(editor.FusionFaceWeightTextBox.Text, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(editor.FusionVoiceWeightTextBox?.Text))
                voiceW = double.Parse(editor.FusionVoiceWeightTextBox.Text, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(editor.FusionHalfLifeTextBox?.Text))
                halfLife = int.Parse(editor.FusionHalfLifeTextBox.Text, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(editor.FusionUnknownThresholdTextBox?.Text))
                unkThresh = double.Parse(editor.FusionUnknownThresholdTextBox.Text, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(editor.ArcFaceModelPathTextBox?.Text))
                arcPath = editor.ArcFaceModelPathTextBox.Text;
            if (!string.IsNullOrWhiteSpace(editor.SpeakerModelPathTextBox?.Text))
                spkPath = editor.SpeakerModelPathTextBox.Text;

            // App-level
            bool requireWake = editor.RequireWakeWordCheckBox?.IsChecked ?? current.App.RequireWakeWord;

            // Extended TTS fields from sliders
            double localVolume = editor.LocalVolumeSlider.Value / 100.0;
            double discordVolume = editor.DiscordVolumeSlider.Value / 100.0;
            float speed = (float)(editor.TtsSpeedSlider.Value / 100.0);
            double trimThreshold = editor.TrimSilenceThresholdSlider.Value / 1000.0;
            int trimLeaveMs = (int)editor.TtsTrimLeaveSlider.Value;
            int trimMaxMs = (int)editor.TtsTrimMaxSlider.Value;
            int minClausePaddingMs = (int)editor.TtsPaddingSlider.Value;

            var audio = new global::Kinectv1.Settings.AudioSettings(voiceThreshold, bufferSize, speakerMatchMin);
            // var vad = new global::Kinectv1.Settings.VadSettings(vadThreshold); // removed

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
            // Preserve existing Ollama, Discord, TeamTalk snapshots while updating this editor's sections
            var ollama = current.Ollama;
            var discord = current.Discord;
            var teamTalk = current.TeamTalk;
            var app = new global::Kinectv1.Settings.AppConfig(
                RequireWakeWord: requireWake,
                Scenario: current.App.Scenario,
                InputMode: current.App.InputMode // keep existing unless saved elsewhere
            );

            var asr = new global::Kinectv1.Settings.AsrSettings(
                VoiceConfidenceThreshold: current.Asr.VoiceConfidenceThreshold,
                VoiceHighConfidenceThreshold: asrHighConf,
                VoiceConfidenceBufferSize: current.Asr.VoiceConfidenceBufferSize,
                VoiceConfidenceLoggingEnabled: asrLogging,
                VadSilenceTimeoutMs: current.Asr.VadSilenceTimeoutMs,
                VadDebounceTimeoutMs: current.Asr.VadDebounceTimeoutMs,
                DiscordVadThreshold: current.Asr.DiscordVadThreshold,
                BargeInEnabled: current.Asr.BargeInEnabled
            );

            var stt = new global::Kinectv1.Settings.SttSettings(
                ModelPath: sttModelPath,
                InputDevice: sttInputDevice
            );

            var face = new global::Kinectv1.Settings.FaceSettings(
                Threshold: faceThreshold,
                FusionFaceWeight: faceW,
                FusionVoiceWeight: voiceW,
                FusionDecayHalfLifeMs: halfLife,
                FusionUnknownThreshold: unkThresh,
                ArcFaceModelPath: arcPath,
                SpeakerEmbeddingModelPath: spkPath
            );

            return new global::Kinectv1.Settings.AppSettings(audio, tts, ollama, discord, teamTalk, current.Ui, asr, stt, face, app);
        }

        private void Verify_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var candidate = BuildFromUI();
                SettingsService.ValidateOrThrow(candidate);
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
                SettingsService.ValidateOrThrow(candidate);
                _svc.Save(candidate);
                _viewModel.RefreshSnapshotFromService();
                _viewModel.HasUnsavedChanges = false; // saved -> clean
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
                _suppressDirty = true;
                var defaults = _svc.GetDefaultsEffective();
                var editor = _viewModel?.SelectedCategory?.EditorView as ModelsSettingsView;
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
                    editor.LocalVolumeSlider.Value = defaults.Tts.LocalVolume * 100;
                    editor.DiscordVolumeSlider.Value = defaults.Tts.DiscordVolume * 100;
                    editor.TtsSpeedSlider.Value = defaults.Tts.Speed * 100;
                    editor.TrimSilenceThresholdSlider.Value = defaults.Tts.TrimThreshold * 1000;
                    editor.TtsTrimLeaveSlider.Value = defaults.Tts.TrimLeaveMs;
                    editor.TtsTrimMaxSlider.Value = defaults.Tts.TrimMaxMs;
                    editor.TtsPaddingSlider.Value = defaults.Tts.MinClausePaddingMs;
                    if (editor.TtsOutputDeviceComboBox != null)
                        editor.TtsOutputDeviceComboBox.Text = defaults.Tts.OutputDevice ?? string.Empty;
                    if (editor.TtsIpaServiceTimeoutTextBox != null)
                        editor.TtsIpaServiceTimeoutTextBox.Text = defaults.Tts.IpaServiceTimeoutMs.ToString(CultureInfo.InvariantCulture);
                    if (editor.TtsIpaOneShotTimeoutTextBox != null)
                        editor.TtsIpaOneShotTimeoutTextBox.Text = defaults.Tts.IpaOneShotTimeoutMs.ToString(CultureInfo.InvariantCulture);

                    // STT
                    if (editor.SttModelPathTextBox != null)
                        editor.SttModelPathTextBox.Text = defaults.Stt.ModelPath ?? string.Empty;
                    if (editor.MicInputComboBox != null)
                        editor.MicInputComboBox.Text = defaults.Stt.InputDevice ?? string.Empty;

                    // Audio
                    editor.AudioVoiceThresholdTextBox.Text = ((int)Math.Round(defaults.Audio.VoiceThreshold * 10000.0)).ToString(CultureInfo.InvariantCulture);
                    if (editor.AudioVoiceHighThresholdTextBox != null)
                        editor.AudioVoiceHighThresholdTextBox.Text = defaults.Asr.VoiceHighConfidenceThreshold.ToString(CultureInfo.InvariantCulture);
                    if (editor.VoiceConfidenceLoggingCheckBox != null)
                        editor.VoiceConfidenceLoggingCheckBox.IsChecked = defaults.Asr.VoiceConfidenceLoggingEnabled;
                    editor.AudioBufferSizeTextBox.Text = defaults.Audio.BufferSize.ToString(CultureInfo.InvariantCulture);
                    if (editor.SpeakerMatchThresholdTextBox != null)
                        editor.SpeakerMatchThresholdTextBox.Text = defaults.Audio.SpeakerMatchMinScore.ToString(CultureInfo.InvariantCulture);

                    // VAD legacy removed
                    if (editor.RequireWakeWordCheckBox != null)
                        editor.RequireWakeWordCheckBox.IsChecked = defaults.App.RequireWakeWord;

                    // Face
                    if (editor.FaceThresholdTextBox != null)
                        editor.FaceThresholdTextBox.Text = defaults.Face.Threshold.ToString(CultureInfo.InvariantCulture);
                    if (editor.FusionFaceWeightTextBox != null)
                        editor.FusionFaceWeightTextBox.Text = defaults.Face.FusionFaceWeight.ToString(CultureInfo.InvariantCulture);
                    if (editor.FusionVoiceWeightTextBox != null)
                        editor.FusionVoiceWeightTextBox.Text = defaults.Face.FusionVoiceWeight.ToString(CultureInfo.InvariantCulture);
                    if (editor.FusionHalfLifeTextBox != null)
                        editor.FusionHalfLifeTextBox.Text = defaults.Face.FusionDecayHalfLifeMs.ToString(CultureInfo.InvariantCulture);
                    if (editor.FusionUnknownThresholdTextBox != null)
                        editor.FusionUnknownThresholdTextBox.Text = defaults.Face.FusionUnknownThreshold.ToString(CultureInfo.InvariantCulture);
                    if (editor.ArcFaceModelPathTextBox != null)
                        editor.ArcFaceModelPathTextBox.Text = defaults.Face.ArcFaceModelPath ?? string.Empty;
                    if (editor.SpeakerModelPathTextBox != null)
                        editor.SpeakerModelPathTextBox.Text = defaults.Face.SpeakerEmbeddingModelPath ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Defaults Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _suppressDirty = false;
                _viewModel.HasUnsavedChanges = true; // applying defaults is a user change
            }
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _suppressDirty = true;
                var snapshot = _svc.Reload();
                var editor = _viewModel?.SelectedCategory?.EditorView as ModelsSettingsView;
                if (editor != null)
                {
                    // TTS
                    editor.TtsEnabledCheckBox.IsChecked = snapshot.Tts.Enabled;
                    editor.TtsModelPathTextBox.Text = snapshot.Tts.ModelPath;
                    editor.TtsModelFolderTextBox.Text = snapshot.Tts.ModelFolder;
                    editor.TtsVocoderPathTextBox.Text = snapshot.Tts.VocoderPath;
                    // Voice list is populated from folder by ModelsSettingsView
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
                    editor.LocalVolumeSlider.Value = snapshot.Tts.LocalVolume * 100;
                    editor.DiscordVolumeSlider.Value = snapshot.Tts.DiscordVolume * 100;
                    editor.TtsSpeedSlider.Value = snapshot.Tts.Speed * 100;
                    editor.TrimSilenceThresholdSlider.Value = snapshot.Tts.TrimThreshold * 1000;
                    editor.TtsTrimLeaveSlider.Value = snapshot.Tts.TrimLeaveMs;
                    editor.TtsTrimMaxSlider.Value = snapshot.Tts.TrimMaxMs;
                    editor.TtsPaddingSlider.Value = snapshot.Tts.MinClausePaddingMs;
                    if (editor.TtsOutputDeviceComboBox != null)
                        editor.TtsOutputDeviceComboBox.Text = snapshot.Tts.OutputDevice ?? string.Empty;
                    if (editor.TtsIpaServiceTimeoutTextBox != null)
                        editor.TtsIpaServiceTimeoutTextBox.Text = snapshot.Tts.IpaServiceTimeoutMs.ToString(CultureInfo.InvariantCulture);
                    if (editor.TtsIpaOneShotTimeoutTextBox != null)
                        editor.TtsIpaOneShotTimeoutTextBox.Text = snapshot.Tts.IpaOneShotTimeoutMs.ToString(CultureInfo.InvariantCulture);

                    // STT
                    if (editor.SttModelPathTextBox != null)
                        editor.SttModelPathTextBox.Text = snapshot.Stt.ModelPath ?? string.Empty;
                    if (editor.MicInputComboBox != null)
                        editor.MicInputComboBox.Text = snapshot.Stt.InputDevice ?? string.Empty;

                    // Audio
                    editor.AudioVoiceThresholdTextBox.Text = ((int)Math.Round(snapshot.Audio.VoiceThreshold * 10000.0)).ToString(CultureInfo.InvariantCulture);
                    if (editor.AudioVoiceHighThresholdTextBox != null)
                        editor.AudioVoiceHighThresholdTextBox.Text = snapshot.Asr.VoiceHighConfidenceThreshold.ToString(CultureInfo.InvariantCulture);
                    if (editor.VoiceConfidenceLoggingCheckBox != null)
                        editor.VoiceConfidenceLoggingCheckBox.IsChecked = snapshot.Asr.VoiceConfidenceLoggingEnabled;
                    editor.AudioBufferSizeTextBox.Text = snapshot.Audio.BufferSize.ToString(CultureInfo.InvariantCulture);
                    if (editor.SpeakerMatchThresholdTextBox != null)
                        editor.SpeakerMatchThresholdTextBox.Text = snapshot.Audio.SpeakerMatchMinScore.ToString(CultureInfo.InvariantCulture);

                    // VAD legacy removed
                    if (editor.RequireWakeWordCheckBox != null)
                        editor.RequireWakeWordCheckBox.IsChecked = snapshot.App.RequireWakeWord;

                    // Face
                    if (editor.FaceThresholdTextBox != null)
                        editor.FaceThresholdTextBox.Text = snapshot.Face.Threshold.ToString(CultureInfo.InvariantCulture);
                    if (editor.FusionFaceWeightTextBox != null)
                        editor.FusionFaceWeightTextBox.Text = snapshot.Face.FusionFaceWeight.ToString(CultureInfo.InvariantCulture);
                    if (editor.FusionVoiceWeightTextBox != null)
                        editor.FusionVoiceWeightTextBox.Text = snapshot.Face.FusionVoiceWeight.ToString(CultureInfo.InvariantCulture);
                    if (editor.FusionHalfLifeTextBox != null)
                        editor.FusionHalfLifeTextBox.Text = snapshot.Face.FusionDecayHalfLifeMs.ToString(CultureInfo.InvariantCulture);
                    if (editor.FusionUnknownThresholdTextBox != null)
                        editor.FusionUnknownThresholdTextBox.Text = snapshot.Face.FusionUnknownThreshold.ToString(CultureInfo.InvariantCulture);
                    if (editor.ArcFaceModelPathTextBox != null)
                        editor.ArcFaceModelPathTextBox.Text = snapshot.Face.ArcFaceModelPath ?? string.Empty;
                    if (editor.SpeakerModelPathTextBox != null)
                        editor.SpeakerModelPathTextBox.Text = snapshot.Face.SpeakerEmbeddingModelPath ?? string.Empty;
                }
                _viewModel.RefreshSnapshotFromService();
                _viewModel.HasUnsavedChanges = false; // reload -> clean
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Reload Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _suppressDirty = false; }
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
                _attachedEditor.LocalVolumeSlider.ValueChanged -= OnEditorDirtyValue;
                _attachedEditor.DiscordVolumeSlider.ValueChanged -= OnEditorDirtyValue;
                _attachedEditor.TtsSpeedSlider.ValueChanged -= OnEditorDirtyValue;
                _attachedEditor.TrimSilenceThresholdSlider.ValueChanged -= OnEditorDirtyValue;
                _attachedEditor.TtsTrimLeaveSlider.ValueChanged -= OnEditorDirtyValue;
                _attachedEditor.TtsTrimMaxSlider.ValueChanged -= OnEditorDirtyValue;
                _attachedEditor.TtsPaddingSlider.ValueChanged -= OnEditorDirtyValue;
                _attachedEditor.SttModelPathTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.SpeakerModelPathTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.ArcFaceModelPathTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.AudioVoiceThresholdTextBox.TextChanged -= OnEditorDirty;
                _attachedEditor.AudioBufferSizeTextBox.TextChanged -= OnEditorDirty;
                if (_attachedEditor.SpeakerMatchThresholdTextBox != null)
                    _attachedEditor.SpeakerMatchThresholdTextBox.TextChanged -= OnEditorDirty;
                if (_attachedEditor.AudioVoiceHighThresholdTextBox != null)
                    _attachedEditor.AudioVoiceHighThresholdTextBox.TextChanged -= OnEditorDirty;
                if (_attachedEditor.TtsIpaServiceTimeoutTextBox != null)
                    _attachedEditor.TtsIpaServiceTimeoutTextBox.TextChanged -= OnEditorDirty;
                if (_attachedEditor.TtsIpaOneShotTimeoutTextBox != null)
                    _attachedEditor.TtsIpaOneShotTimeoutTextBox.TextChanged -= OnEditorDirty;
                if (_attachedEditor.TtsOutputDeviceComboBox != null)
                    _attachedEditor.TtsOutputDeviceComboBox.SelectionChanged -= OnEditorDirtySelection;
                if (_attachedEditor.MicInputComboBox != null)
                    _attachedEditor.MicInputComboBox.SelectionChanged -= OnEditorDirtySelection;
                if (_attachedEditor.RequireWakeWordCheckBox != null)
                {
                    _attachedEditor.RequireWakeWordCheckBox.Checked -= OnEditorDirty;
                    _attachedEditor.RequireWakeWordCheckBox.Unchecked -= OnEditorDirty;
                }
                if (_attachedEditor.VoiceConfidenceLoggingCheckBox != null)
                {
                    _attachedEditor.VoiceConfidenceLoggingCheckBox.Checked -= OnEditorDirty;
                    _attachedEditor.VoiceConfidenceLoggingCheckBox.Unchecked -= OnEditorDirty;
                }
            }

            _attachedEditor = _viewModel?.SelectedCategory?.EditorView as ModelsSettingsView;
            if (_attachedEditor != null)
            {
                _attachedEditor.TtsModelPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.TtsModelFolderTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.TtsVocoderPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.TtsEnabledCheckBox.Checked += OnEditorDirty;
                _attachedEditor.TtsEnabledCheckBox.Unchecked += OnEditorDirty;
                _attachedEditor.TtsVoiceComboBox.SelectionChanged += OnEditorDirtySelection;
                _attachedEditor.ExecutionModeComboBox.SelectionChanged += OnEditorDirtySelection;
                _attachedEditor.LocalVolumeSlider.ValueChanged += OnEditorDirtyValue;
                _attachedEditor.DiscordVolumeSlider.ValueChanged += OnEditorDirtyValue;
                _attachedEditor.TtsSpeedSlider.ValueChanged += OnEditorDirtyValue;
                _attachedEditor.TrimSilenceThresholdSlider.ValueChanged += OnEditorDirtyValue;
                _attachedEditor.TtsTrimLeaveSlider.ValueChanged += OnEditorDirtyValue;
                _attachedEditor.TtsTrimMaxSlider.ValueChanged += OnEditorDirtyValue;
                _attachedEditor.TtsPaddingSlider.ValueChanged += OnEditorDirtyValue;
                _attachedEditor.SttModelPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.SpeakerModelPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.ArcFaceModelPathTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.AudioVoiceThresholdTextBox.TextChanged += OnEditorDirty;
                _attachedEditor.AudioBufferSizeTextBox.TextChanged += OnEditorDirty;
                if (_attachedEditor.SpeakerMatchThresholdTextBox != null)
                    _attachedEditor.SpeakerMatchThresholdTextBox.TextChanged += OnEditorDirty;
                if (_attachedEditor.AudioVoiceHighThresholdTextBox != null)
                    _attachedEditor.AudioVoiceHighThresholdTextBox.TextChanged += OnEditorDirty;
                if (_attachedEditor.TtsIpaServiceTimeoutTextBox != null)
                    _attachedEditor.TtsIpaServiceTimeoutTextBox.TextChanged += OnEditorDirty;
                if (_attachedEditor.TtsIpaOneShotTimeoutTextBox != null)
                    _attachedEditor.TtsIpaOneShotTimeoutTextBox.TextChanged += OnEditorDirty;
                if (_attachedEditor.TtsOutputDeviceComboBox != null)
                    _attachedEditor.TtsOutputDeviceComboBox.SelectionChanged += OnEditorDirtySelection;
                if (_attachedEditor.MicInputComboBox != null)
                    _attachedEditor.MicInputComboBox.SelectionChanged += OnEditorDirtySelection;
                if (_attachedEditor.RequireWakeWordCheckBox != null)
                {
                    _attachedEditor.RequireWakeWordCheckBox.Checked += OnEditorDirty;
                    _attachedEditor.RequireWakeWordCheckBox.Unchecked += OnEditorDirty;
                }
                if (_attachedEditor.VoiceConfidenceLoggingCheckBox != null)
                {
                    _attachedEditor.VoiceConfidenceLoggingCheckBox.Checked += OnEditorDirty;
                    _attachedEditor.VoiceConfidenceLoggingCheckBox.Unchecked += OnEditorDirty;
                }
            }
        }

        private void OnEditorDirty(object sender, EventArgs e)
        {
            if (_suppressDirty) return;
            if (_viewModel != null) _viewModel.HasUnsavedChanges = true;
        }

        private void OnEditorDirtySelection(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDirty) return;
            if (_viewModel != null) _viewModel.HasUnsavedChanges = true;
        }

        private void OnEditorDirtyValue(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressDirty) return;
            if (_viewModel != null) _viewModel.HasUnsavedChanges = true;
        }
    }
}
