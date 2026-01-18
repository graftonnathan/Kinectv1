using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kinectv1.Settings;
using Newtonsoft.Json.Linq;
using WinForms = System.Windows.Forms;
using System.IO;

namespace Kinectv1.UI.Settings
{
    public partial class OllamaSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;

        public OllamaSettingsView()
        {
            InitializeComponent();
            Loaded += OllamaSettingsView_Loaded;
            // Toggle enablement to follow schema-like pattern (label on left, control on right)
            OllamaSpeakerAutoRadio.Checked += (_, __) => { if (OllamaForcedSpeakerIdTextBox != null) OllamaForcedSpeakerIdTextBox.IsEnabled = false; };
            OllamaSpeakerForceRadio.Checked += (_, __) => { if (OllamaForcedSpeakerIdTextBox != null) OllamaForcedSpeakerIdTextBox.IsEnabled = true; };
        }

        private async void OllamaSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _svc?.Current?.Ollama;
                if (cfg == null) return;
                OllamaEnabledCheckBox.IsChecked = cfg.Enabled;
                ProviderComboBox.Text = cfg.Provider;
                BaseUrlTextBox.Text = cfg.BaseUrl;
                LmStudioBaseUrlTextBox.Text = cfg.LmStudioBaseUrl;
                ApiKeyTextBox.Text = cfg.ApiKey;
                OllamaModelComboBox.Text = cfg.Model;
                OllamaMemoryEnabledCheckBox.IsChecked = cfg.MemoryEnabled;
                MaxMsgsPerSpeakerTextBox.Text = cfg.MaxMessagesPerSpeaker.ToString();
                MaxSystemMsgsTextBox.Text = cfg.MaxSystemMessages.ToString();
                ConversationTimeoutTextBox.Text = cfg.ConversationTimeoutMinutes.ToString();
                HistoryPathTextBox.Text = cfg.ConversationHistoryPath;
                SystemPromptPathTextBox.Text = cfg.SystemPromptPath;
                OllamaOutputThinkCheckBox.IsChecked = cfg.OutputThink;

                // Speaker override
                var force = cfg.ForceSpeakerOverrideEnabled;
                OllamaSpeakerAutoRadio.IsChecked = !force;
                OllamaSpeakerForceRadio.IsChecked = force;
                if (OllamaForcedSpeakerIdTextBox != null)
                {
                    OllamaForcedSpeakerIdTextBox.IsEnabled = force;
                    OllamaForcedSpeakerIdTextBox.Text = cfg.ForcedSpeakerId ?? string.Empty;
                }

                await RefreshModelListAsync();
            }
            catch { }
        }

        private async Task RefreshModelListAsync()
        {
            try
            {
                var cur = _svc?.Current;
                var eff = _svc?.GetDefaultsEffective();
                string provider = ProviderComboBox.Text?.Trim();

                string baseOllama = string.IsNullOrWhiteSpace(BaseUrlTextBox.Text) ? (cur?.Ollama?.BaseUrl ?? eff?.Ollama?.BaseUrl) : BaseUrlTextBox.Text;
                string baseLm = string.IsNullOrWhiteSpace(LmStudioBaseUrlTextBox.Text) ? (cur?.Ollama?.LmStudioBaseUrl ?? eff?.Ollama?.LmStudioBaseUrl) : LmStudioBaseUrlTextBox.Text;

                string url = null;
                if (string.Equals(provider, "LMStudio", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(baseLm)) url = baseLm.TrimEnd('/') + "/v1/models";
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(baseOllama)) url = baseOllama.TrimEnd('/') + "/api/tags";
                }
                if (string.IsNullOrWhiteSpace(url)) { StatusText.Text = "Base URL not configured"; return; }

                using (var http = new HttpClient())
                {
                    var json = await http.GetStringAsync(url);
                    var names = new List<string>();
                    var obj = JObject.Parse(json);
                    if (obj["models"] != null)
                        names.AddRange(obj["models"].Select(m => m["name"]?.ToString()).Where(n => !string.IsNullOrWhiteSpace(n)));
                    else if (obj["data"] != null)
                        names.AddRange(obj["data"].Select(m => m["id"]?.ToString()).Where(n => !string.IsNullOrWhiteSpace(n)));
                    OllamaModelComboBox.ItemsSource = names;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e) => await RefreshModelListAsync();

        private static string TryMakeRelative(string fullPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fullPath)) return fullPath;
                var baseDir = AppDomain.CurrentDomain.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
                var fp = Path.GetFullPath(fullPath);
                if (fp.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = fp.Substring(baseDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return rel.Replace('/', Path.DirectorySeparatorChar);
                }
            }
            catch { }
            return fullPath;
        }

        private void BrowseHistoryPathButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // If current text ends with .json -> treat as file selection; else folder selection
                var current = HistoryPathTextBox.Text?.Trim();
                bool chooseFile = !string.IsNullOrWhiteSpace(current) && current.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

                if (chooseFile)
                {
                    using (var dlg = new WinForms.SaveFileDialog())
                    {
                        dlg.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                        dlg.Title = "Select Conversation History JSON";
                        if (!string.IsNullOrWhiteSpace(current))
                        {
                            try { dlg.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(current)); } catch { }
                            dlg.FileName = Path.GetFileName(current);
                        }
                        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                        {
                            HistoryPathTextBox.Text = TryMakeRelative(dlg.FileName);
                            StatusText.Text = "Changed (not saved)";
                        }
                    }
                }
                else
                {
                    using (var dlg = new WinForms.FolderBrowserDialog())
                    {
                        dlg.Description = "Select folder for conversation history (conversation.json & rotation files)";
                        if (!string.IsNullOrWhiteSpace(current))
                        {
                            try { dlg.SelectedPath = Path.GetFullPath(current); } catch { }
                        }
                        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                        {
                            HistoryPathTextBox.Text = TryMakeRelative(dlg.SelectedPath);
                            StatusText.Text = "Changed (not saved)";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void BrowseSystemPromptButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dlg = new WinForms.OpenFileDialog())
                {
                    dlg.Filter = "Prompt/System text|*.txt;*.md;*.prompt|All files|*.*";
                    dlg.Title = "Select System Prompt File";
                    var current = SystemPromptPathTextBox.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        try
                        {
                            var full = Path.GetFullPath(current);
                            dlg.InitialDirectory = File.Exists(full) ? Path.GetDirectoryName(full) : full;
                            dlg.FileName = Path.GetFileName(full);
                        }
                        catch { }
                    }
                    if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        SystemPromptPathTextBox.Text = TryMakeRelative(dlg.FileName);
                        StatusText.Text = "Changed (not saved)";
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cur = _svc?.Current ?? throw new InvalidOperationException("Settings unavailable");

                // Validate forced speaker choice
                bool force = OllamaSpeakerForceRadio.IsChecked == true;
                string forcedId = OllamaForcedSpeakerIdTextBox.Text?.Trim();
                if (force && string.IsNullOrWhiteSpace(forcedId))
                {
                    StatusText.Text = "Forced Speaker ID cannot be empty";
                    return;
                }

                var next = new OllamaSettings(
                    Provider: ProviderComboBox.Text ?? cur.Ollama.Provider,
                    Enabled: OllamaEnabledCheckBox.IsChecked ?? cur.Ollama.Enabled,
                    Model: OllamaModelComboBox.Text ?? cur.Ollama.Model,
                    MemoryEnabled: OllamaMemoryEnabledCheckBox.IsChecked ?? cur.Ollama.MemoryEnabled,
                    MaxMessagesPerSpeaker: int.TryParse(MaxMsgsPerSpeakerTextBox.Text, out var mps) ? mps : cur.Ollama.MaxMessagesPerSpeaker,
                    MaxSystemMessages: int.TryParse(MaxSystemMsgsTextBox.Text, out var msm) ? msm : cur.Ollama.MaxSystemMessages,
                    ConversationTimeoutMinutes: int.TryParse(ConversationTimeoutTextBox.Text, out var ctm) ? ctm : cur.Ollama.ConversationTimeoutMinutes,
                    ConversationHistoryPath: string.IsNullOrWhiteSpace(HistoryPathTextBox.Text) ? cur.Ollama.ConversationHistoryPath : HistoryPathTextBox.Text,
                    SystemPromptPath: string.IsNullOrWhiteSpace(SystemPromptPathTextBox.Text) ? cur.Ollama.SystemPromptPath : SystemPromptPathTextBox.Text,
                    OutputThink: OllamaOutputThinkCheckBox.IsChecked ?? cur.Ollama.OutputThink,
                    BaseUrl: string.IsNullOrWhiteSpace(BaseUrlTextBox.Text) ? cur.Ollama.BaseUrl : BaseUrlTextBox.Text,
                    LmStudioBaseUrl: string.IsNullOrWhiteSpace(LmStudioBaseUrlTextBox.Text) ? cur.Ollama.LmStudioBaseUrl : LmStudioBaseUrlTextBox.Text,
                    ApiKey: string.IsNullOrWhiteSpace(ApiKeyTextBox.Text) ? cur.Ollama.ApiKey : ApiKeyTextBox.Text,
                    ForceSpeakerOverrideEnabled: force,
                    ForcedSpeakerId: force ? forcedId : (cur.Ollama.ForcedSpeakerId ?? string.Empty),
                    // Preserve vector memory settings (edited in Memory tab)
                    VectorMemoryEnabled: cur.Ollama.VectorMemoryEnabled,
                    HotContextTokenLimit: cur.Ollama.HotContextTokenLimit,
                    WarmSummaryTokenLimit: cur.Ollama.WarmSummaryTokenLimit,
                    MemoryContextBudget: cur.Ollama.MemoryContextBudget,
                    VectorDbPath: cur.Ollama.VectorDbPath,
                    EmbeddingsModel: cur.Ollama.EmbeddingsModel,
                    ChunkSizeTokens: cur.Ollama.ChunkSizeTokens,
                    ChunkOverlapTokens: cur.Ollama.ChunkOverlapTokens,
                    VectorSearchTopK: cur.Ollama.VectorSearchTopK,
                    RecencyBoostFactor: cur.Ollama.RecencyBoostFactor,
                    MinRetrievalScore: cur.Ollama.MinRetrievalScore
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
