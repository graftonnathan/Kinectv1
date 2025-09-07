using System;
using System.Windows;
using System.Windows.Controls;
using Kinectv1.Settings;

namespace Kinectv1.UI.Settings
{
    public partial class OllamaSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;

        public OllamaSettingsView()
        {
            InitializeComponent();
            Loaded += OllamaSettingsView_Loaded;
        }

        private void OllamaSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _svc?.Current?.Ollama;
                if (cfg == null) return;
                OllamaEnabledCheckBox.IsChecked = cfg.Enabled;
                ProviderComboBox.Text = cfg.Provider;
                OllamaModelComboBox.Text = cfg.Model;
                OllamaMemoryEnabledCheckBox.IsChecked = cfg.MemoryEnabled;
                MaxMsgsPerSpeakerTextBox.Text = cfg.MaxMessagesPerSpeaker.ToString();
                MaxSystemMsgsTextBox.Text = cfg.MaxSystemMessages.ToString();
                ConversationTimeoutTextBox.Text = cfg.ConversationTimeoutMinutes.ToString();
                HistoryPathTextBox.Text = cfg.ConversationHistoryPath;
                SystemPromptPathTextBox.Text = cfg.SystemPromptPath;
                OllamaOutputThinkCheckBox.IsChecked = cfg.OutputThink;
            }
            catch { }
        }

        private void RefreshModelsButton_Click(object sender, RoutedEventArgs e) { }
        private void BrowseHistoryPathButton_Click(object sender, RoutedEventArgs e) { }
        private void BrowseSystemPromptButton_Click(object sender, RoutedEventArgs e) { }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cur = _svc?.Current ?? throw new InvalidOperationException("Settings unavailable");
                var next = new OllamaSettings(
                    Provider: ProviderComboBox.Text ?? "Ollama",
                    Enabled: OllamaEnabledCheckBox.IsChecked ?? false,
                    Model: OllamaModelComboBox.Text ?? string.Empty,
                    MemoryEnabled: OllamaMemoryEnabledCheckBox.IsChecked ?? false,
                    MaxMessagesPerSpeaker: int.TryParse(MaxMsgsPerSpeakerTextBox.Text, out var mps) ? mps : cur.Ollama.MaxMessagesPerSpeaker,
                    MaxSystemMessages: int.TryParse(MaxSystemMsgsTextBox.Text, out var msm) ? msm : cur.Ollama.MaxSystemMessages,
                    ConversationTimeoutMinutes: int.TryParse(ConversationTimeoutTextBox.Text, out var ctm) ? ctm : cur.Ollama.ConversationTimeoutMinutes,
                    ConversationHistoryPath: HistoryPathTextBox.Text ?? string.Empty,
                    SystemPromptPath: SystemPromptPathTextBox.Text ?? string.Empty,
                    OutputThink: OllamaOutputThinkCheckBox.IsChecked ?? cur.Ollama.OutputThink
                );
                var updated = cur with { Ollama = next };
                SettingsService.ValidateOrThrow(updated);
                _svc.Save(updated);
                StatusText.Text = "Saved";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }
    }
}
