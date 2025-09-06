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

            // Wire immediate persistence for booleans & model
            OllamaEnabledCheckBox.Checked += (s, e) => AppSettings.SaveOllamaEnabled(true);
            OllamaEnabledCheckBox.Unchecked += (s, e) => AppSettings.SaveOllamaEnabled(false);
            OllamaMemoryEnabledCheckBox.Checked += (s, e) => AppSettings.SaveOllamaMemoryEnabled(true);
            OllamaMemoryEnabledCheckBox.Unchecked += (s, e) => AppSettings.SaveOllamaMemoryEnabled(false);
            OllamaModelComboBox.SelectionChanged += (s, e) =>
            {
                var sel = OllamaModelComboBox.SelectedItem?.ToString();
                if (!string.IsNullOrWhiteSpace(sel))
                {
                    // Persist via JSON settings pipeline and update live model immediately
                    try
                    {
                        var svc = App.SettingsProvider;
                        var curr = svc?.Current;
                        if (svc != null && curr != null)
                        {
                            var next = curr with { Ollama = curr.Ollama with { Model = sel } };
                            svc.Save(next);
                        }
                    }
                    catch { }
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
                        var svc = App.SettingsProvider;
                        var curr = svc?.Current;
                        if (svc != null && curr != null)
                        {
                            var next = curr with { Ollama = curr.Ollama with { Provider = provider } };
                            svc.Save(next);
                        }
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
                OllamaEnabledCheckBox.IsChecked = AppSettings.LoadOllamaEnabled();
                // Provider
                var provider = App.SettingsProvider?.Current?.Ollama?.Provider ?? "Ollama";
                foreach (var it in ProviderComboBox.Items)
                {
                    if (it is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
                    {
                        ProviderComboBox.SelectedItem = cbi;
                        break;
                    }
                }

                // Read current model from JSON settings snapshot for persistence per docs
                var model = App.SettingsProvider?.Current?.Ollama?.Model ?? string.Empty;
                OllamaModelComboBox.ItemsSource = null; // set later by refresh
                OllamaModelComboBox.Text = model; // fallback visual until list loads
                OllamaMemoryEnabledCheckBox.IsChecked = AppSettings.LoadOllamaMemoryEnabled();
                MaxMsgsPerSpeakerTextBox.Text = AppSettings.LoadOllamaMaxMessagesPerSpeaker().ToString();
                MaxSystemMsgsTextBox.Text = AppSettings.LoadOllamaMaxSystemMessages().ToString();
                ConversationTimeoutTextBox.Text = AppSettings.LoadOllamaConversationTimeoutMinutes().ToString();
                HistoryPathTextBox.Text = AppSettings.LoadConversationHistoryPath() ?? string.Empty;
                SystemPromptPathTextBox.Text = AppSettings.LoadSystemPromptPath() ?? string.Empty;

                // Output think from snapshot
                var outputThink = App.SettingsProvider?.Current?.Ollama?.OutputThink ?? false;
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
                try
                {
                    var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { Enabled = enabled } }; svc.Save(next); }
                }
                catch { AppSettings.SaveOllamaEnabled(enabled); }

                // Provider
                var provider = (ProviderComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ollama";
                try
                {
                    var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { Provider = provider } }; svc.Save(next); }
                }
                catch { }
                OllamaService.SetProvider(provider);

                var model = (OllamaModelComboBox.SelectedItem?.ToString()) ?? (OllamaModelComboBox.Text ?? string.Empty);
                try
                {
                    var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { Model = model } }; svc.Save(next); }
                }
                catch { AppSettings.SaveOllamaModel(model); }
                if (!string.IsNullOrWhiteSpace(model))
                    OllamaModelComboBox.SelectedItem = model;

                var memEnabled = OllamaMemoryEnabledCheckBox.IsChecked == true;
                try
                {
                    var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { MemoryEnabled = memEnabled } }; svc.Save(next); }
                }
                catch { AppSettings.SaveOllamaMemoryEnabled(memEnabled); }

                if (int.TryParse(MaxMsgsPerSpeakerTextBox.Text, out var maxPerSpeaker))
                    try { var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { MaxMessagesPerSpeaker = maxPerSpeaker } }; svc.Save(next); } } catch { AppSettings.SaveOllamaMaxMessagesPerSpeaker(maxPerSpeaker); }
                if (int.TryParse(MaxSystemMsgsTextBox.Text, out var maxSys))
                    try { var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { MaxSystemMessages = maxSys } }; svc.Save(next); } } catch { AppSettings.SaveOllamaMaxSystemMessages(maxSys); }
                if (int.TryParse(ConversationTimeoutTextBox.Text, out var timeoutMin))
                    try { var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { ConversationTimeoutMinutes = timeoutMin } }; svc.Save(next); } } catch { AppSettings.SaveOllamaConversationTimeoutMinutes(timeoutMin); }

                try { var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { ConversationHistoryPath = HistoryPathTextBox.Text ?? string.Empty } }; svc.Save(next); } } catch { AppSettings.SaveConversationHistoryPath(HistoryPathTextBox.Text ?? string.Empty); }
                try { var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { SystemPromptPath = SystemPromptPathTextBox.Text ?? string.Empty } }; svc.Save(next); } } catch { AppSettings.SaveSystemPromptPath(SystemPromptPathTextBox.Text ?? string.Empty); }

                // Output think
                var outputThink = OllamaOutputThinkCheckBox.IsChecked == true;
                try { var svc = App.SettingsProvider; var curr = svc?.Current; if (svc != null && curr != null) { var next = curr with { Ollama = curr.Ollama with { OutputThink = outputThink } }; svc.Save(next); } } catch { }

                Status("Settings saved.");
            }
            catch (Exception ex)
            {
                Status($"Error saving settings: {ex.Message}", true);
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
