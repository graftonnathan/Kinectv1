using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Kinectv1
{
    public partial class OllamaSettingsView : UserControl
    {
        public OllamaSettingsView()
        {
            InitializeComponent();
            LoadValues();
            _ = RefreshModelsAsync();

            // Wire immediate persistence for booleans & model using SettingsService
            OllamaEnabledCheckBox.Checked += (s, e) => SaveSnapshot(enabled: true);
            OllamaEnabledCheckBox.Unchecked += (s, e) => SaveSnapshot(enabled: false);
            OllamaMemoryEnabledCheckBox.Checked += (s, e) => SaveSnapshot(memoryEnabled: true);
            OllamaMemoryEnabledCheckBox.Unchecked += (s, e) => SaveSnapshot(memoryEnabled: false);
            OllamaModelComboBox.SelectionChanged += (s, e) =>
            {
                var sel = OllamaModelComboBox.SelectedItem?.ToString();
                if (!string.IsNullOrWhiteSpace(sel))
                {
                    SaveSnapshot(model: sel);
                    OllamaService.SetDefaultModel(sel);
                }
            };

            // Provider change (runtime switch with cancel)
            if (ProviderComboBox != null)
            {
                ProviderComboBox.SelectionChanged += (s, e) =>
                {
                    try
                    {
                        var item = ProviderComboBox.SelectedItem as ComboBoxItem;
                        var provider = item?.Content?.ToString() ?? "Ollama";
                        SaveSnapshot(provider: provider);
                        OllamaService.SetProvider(provider); // cancel in-flight and switch
                    }
                    catch (Exception ex) { Console.WriteLine($"Provider switch failed: {ex.Message}"); }
                };
            }
        }

        private void LoadValues()
        {
            try
            {
                var snap = App.SettingsProvider?.Current;
                var ol = snap?.Ollama;

                OllamaEnabledCheckBox.IsChecked = ol?.Enabled ?? false;

                // Provider
                var provider = ol?.Provider ?? "Ollama";
                foreach (var it in ProviderComboBox.Items)
                {
                    if (it is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
                    {
                        ProviderComboBox.SelectedItem = cbi;
                        break;
                    }
                }

                // Read current model from JSON settings snapshot
                var model = ol?.Model ?? string.Empty;
                OllamaModelComboBox.ItemsSource = null; // set later by refresh
                OllamaModelComboBox.Text = model; // fallback visual until list loads

                OllamaMemoryEnabledCheckBox.IsChecked = ol?.MemoryEnabled ?? false;
                MaxMsgsPerSpeakerTextBox.Text = (ol?.MaxMessagesPerSpeaker ?? 0).ToString();
                MaxSystemMsgsTextBox.Text = (ol?.MaxSystemMessages ?? 0).ToString();
                ConversationTimeoutTextBox.Text = (ol?.ConversationTimeoutMinutes ?? 0).ToString();
                HistoryPathTextBox.Text = ol?.ConversationHistoryPath ?? string.Empty;
                SystemPromptPathTextBox.Text = ol?.SystemPromptPath ?? string.Empty;

                // Output think from snapshot
                var outputThink = ol?.OutputThink ?? false;
                OllamaOutputThinkCheckBox.IsChecked = outputThink;

                Status("Settings loaded.");
            }
            catch (Exception ex)
            {
                Status($"Error loading settings: {ex.Message}", true);
            }
        }

        private async System.Threading.Tasks.Task RefreshModelsAsync()
        {
            try
            {
                Status("Querying Ollama models...");
                var tags = await OllamaService.GetAvailableModelsAsync();
                var names = new List<string>();
                foreach (var t in tags)
                {
                    if (!string.IsNullOrWhiteSpace(t)) names.Add(t);
                }
                if (names.Count == 0)
                {
                    Status("No models returned from Ollama. Is the service running?", true);
                    return;
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                OllamaModelComboBox.ItemsSource = names;

                // Select current model from JSON snapshot if present
                var current = App.SettingsProvider?.Current?.Ollama?.Model;
                if (!string.IsNullOrWhiteSpace(current))
                {
                    var match = names.FirstOrDefault(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        OllamaModelComboBox.SelectedItem = match;
                    }
                }
                Status($"Loaded {names.Count} model(s) from Ollama.");
            }
            catch (Exception ex)
            {
                Status($"Failed to query models: {ex.Message}", true);
            }
        }

        private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshModelsAsync();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var enabled = OllamaEnabledCheckBox.IsChecked == true;
                var provider = (ProviderComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ollama";
                var model = (OllamaModelComboBox.SelectedItem?.ToString()) ?? (OllamaModelComboBox.Text ?? string.Empty);
                var memEnabled = OllamaMemoryEnabledCheckBox.IsChecked == true;
                int maxPerSpeaker; if (!int.TryParse(MaxMsgsPerSpeakerTextBox.Text, out maxPerSpeaker)) maxPerSpeaker = App.SettingsProvider?.Current?.Ollama?.MaxMessagesPerSpeaker ?? 0;
                int maxSys; if (!int.TryParse(MaxSystemMsgsTextBox.Text, out maxSys)) maxSys = App.SettingsProvider?.Current?.Ollama?.MaxSystemMessages ?? 0;
                int timeoutMin; if (!int.TryParse(ConversationTimeoutTextBox.Text, out timeoutMin)) timeoutMin = App.SettingsProvider?.Current?.Ollama?.ConversationTimeoutMinutes ?? 0;
                var historyPath = HistoryPathTextBox.Text ?? string.Empty;
                var systemPrompt = SystemPromptPathTextBox.Text ?? string.Empty;
                var outputThink = OllamaOutputThinkCheckBox.IsChecked == true;

                SaveSnapshot(
                    enabled: enabled,
                    provider: provider,
                    model: model,
                    memoryEnabled: memEnabled,
                    maxMessagesPerSpeaker: maxPerSpeaker,
                    maxSystemMessages: maxSys,
                    conversationTimeoutMinutes: timeoutMin,
                    historyPath: historyPath,
                    systemPromptPath: systemPrompt,
                    outputThink: outputThink
                );

                Status("Settings saved.");
            }
            catch (Exception ex)
            {
                Status($"Error saving settings: {ex.Message}", true);
            }
        }

        private void SaveSnapshot(
            bool? enabled = null,
            string provider = null,
            string model = null,
            bool? memoryEnabled = null,
            int? maxMessagesPerSpeaker = null,
            int? maxSystemMessages = null,
            int? conversationTimeoutMinutes = null,
            string historyPath = null,
            string systemPromptPath = null,
            bool? outputThink = null)
        {
            try
            {
                var svc = App.SettingsProvider;
                var curr = svc?.Current;
                if (svc == null || curr == null) return;

                var ol = curr.Ollama with
                {
                    Enabled = enabled ?? curr.Ollama.Enabled,
                    Provider = provider ?? curr.Ollama.Provider,
                    Model = model ?? curr.Ollama.Model,
                    MemoryEnabled = memoryEnabled ?? curr.Ollama.MemoryEnabled,
                    MaxMessagesPerSpeaker = maxMessagesPerSpeaker ?? curr.Ollama.MaxMessagesPerSpeaker,
                    MaxSystemMessages = maxSystemMessages ?? curr.Ollama.MaxSystemMessages,
                    ConversationTimeoutMinutes = conversationTimeoutMinutes ?? curr.Ollama.ConversationTimeoutMinutes,
                    ConversationHistoryPath = historyPath ?? curr.Ollama.ConversationHistoryPath,
                    SystemPromptPath = systemPromptPath ?? curr.Ollama.SystemPromptPath,
                    OutputThink = outputThink ?? curr.Ollama.OutputThink
                };

                var next = curr with { Ollama = ol };
                svc.Save(next);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ollama settings save failed: {ex.Message}");
            }
        }

        private void BrowseHistoryPathButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select or create history folder",
                    CheckFileExists = false,
                    FileName = "Select Folder",
                    ValidateNames = false,
                    Filter = "All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    var folder = System.IO.Path.GetDirectoryName(dlg.FileName);
                    HistoryPathTextBox.Text = folder;
                }
            }
            catch (Exception ex)
            {
                Status($"Error selecting folder: {ex.Message}", true);
            }
        }

        private void BrowseSystemPromptButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select System Prompt File",
                    Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    CheckFileExists = true
                };
                if (dlg.ShowDialog() == true)
                {
                    SystemPromptPathTextBox.Text = dlg.FileName;
                }
            }
            catch (Exception ex)
            {
                Status($"Error selecting system prompt: {ex.Message}", true);
            }
        }

        private void Status(string msg, bool error = false)
        {
            if (StatusText != null)
            {
                StatusText.Text = msg;
                StatusText.Foreground = error ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.OrangeRed)
                                              : (System.Windows.Media.Brush)FindResource("TextSecondary");
            }
        }
    }
}
