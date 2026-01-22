using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kinectv1.Settings;
using Kinectv1.Llm;
using Newtonsoft.Json.Linq;
using WinForms = System.Windows.Forms;

namespace Kinectv1.UI.Settings
{
    public partial class MemorySettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;

        public MemorySettingsView()
        {
            InitializeComponent();
            Loaded += MemorySettingsView_Loaded;
            Unloaded += MemorySettingsView_Unloaded;
        }

        private async void MemorySettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            // Subscribe to archival events
            OllamaService.OnMemoryArchiveStarted += HandleArchiveStarted;
            OllamaService.OnMemoryArchiveCompleted += HandleArchiveCompleted;
            OllamaService.OnMemoryArchiveFailed += HandleArchiveFailed;

            try
            {
                var cfg = _svc?.Current?.Ollama;
                if (cfg == null) return;

                VectorMemoryEnabledCheckBox.IsChecked = cfg.VectorMemoryEnabled;
                HotContextTokenLimitTextBox.Text = cfg.HotContextTokenLimit.ToString();
                WarmSummaryTokenLimitTextBox.Text = cfg.WarmSummaryTokenLimit.ToString();
                MemoryContextBudgetTextBox.Text = cfg.MemoryContextBudget.ToString();
                VectorDbPathTextBox.Text = cfg.VectorDbPath ?? string.Empty;
                ChunkSizeTokensTextBox.Text = cfg.ChunkSizeTokens.ToString();
                ChunkOverlapTokensTextBox.Text = cfg.ChunkOverlapTokens.ToString();
                VectorSearchTopKTextBox.Text = cfg.VectorSearchTopK.ToString();
                MinRetrievalScoreTextBox.Text = cfg.MinRetrievalScore.ToString("F2");
                RecencyBoostFactorTextBox.Text = cfg.RecencyBoostFactor.ToString("F2");

                // Load embeddings models and set current selection
                await RefreshEmbeddingsModelListAsync();
                EmbeddingsModelComboBox.Text = cfg.EmbeddingsModel ?? string.Empty;

                // Refresh memory stats
                RefreshMemoryStats();
            }
            catch { }
        }

        private void MemorySettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe from events
            OllamaService.OnMemoryArchiveStarted -= HandleArchiveStarted;
            OllamaService.OnMemoryArchiveCompleted -= HandleArchiveCompleted;
            OllamaService.OnMemoryArchiveFailed -= HandleArchiveFailed;
        }

        #region Archive Event Handlers
        private void HandleArchiveStarted()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ArchivalStatusBorder.Visibility = Visibility.Visible;
                ArchivalStatusText.Text = "Archiving conversation to vector memory...";
                ArchivalStatusBorder.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x5C));
                RenderToMemoryButton.IsEnabled = false;
            }));
        }

        private void HandleArchiveCompleted(int messagesArchived, int chunksCreated)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ArchivalStatusText.Text = $"? Archived {messagesArchived} messages to vector memory";
                ArchivalSpinner.Text = "?";
                ArchivalStatusBorder.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x4C, 0x10));
                RenderToMemoryButton.IsEnabled = true;

                // Refresh stats
                RefreshMemoryStats();

                // Hide after delay
                Task.Delay(3000).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ArchivalStatusBorder.Visibility = Visibility.Collapsed;
                        ArchivalSpinner.Text = "?";
                    }));
                });
            }));
        }

        private void HandleArchiveFailed(string error)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ArchivalStatusText.Text = $"? Archive failed: {error}";
                ArchivalSpinner.Text = "?";
                ArchivalStatusBorder.Background = new SolidColorBrush(Color.FromRgb(0x5C, 0x1A, 0x1A));
                RenderToMemoryButton.IsEnabled = true;

                // Hide after delay
                Task.Delay(5000).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ArchivalStatusBorder.Visibility = Visibility.Collapsed;
                        ArchivalSpinner.Text = "?";
                    }));
                });
            }));
        }
        #endregion

        #region Memory Stats
        private void RefreshMemoryStats()
        {
            try
            {
                // Get current memory key from system prompt
                var cfg = _svc?.Current?.Ollama;
                string memoryKey = "default";
                if (!string.IsNullOrWhiteSpace(cfg?.SystemPromptPath))
                {
                    try
                    {
                        memoryKey = System.IO.Path.GetFileNameWithoutExtension(cfg.SystemPromptPath)?.ToLowerInvariant() ?? "default";
                    }
                    catch { }
                }
                
                CurrentMemoryKeyText.Text = memoryKey;

                // Count hot context across ALL speakers in conversation.json.
                var (tokens, messages) = OllamaService.GetHotContextStatsAllSpeakers();

                 HotContextTokensText.Text = $"{tokens:N0}";
                 MessagesCountText.Text = $"{messages}";

                 // Update token display color based on limit
                 if (cfg != null && tokens > cfg.HotContextTokenLimit)
                 {
                     HotContextTokensText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)); // Red
                 }
                 else if (cfg != null && tokens > cfg.HotContextTokenLimit * 0.8)
                 {
                     HotContextTokensText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)); // Orange
                 }
                 else
                 {
                     HotContextTokensText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)); // Green
                 }

                 // Try to get vector store chunk count for current memory key
                 try
                 {
                     var vectorDbBasePath = cfg?.VectorDbPath;
                     if (!string.IsNullOrWhiteSpace(vectorDbBasePath))
                     {
                         // Match runtime resolution (similar to OllamaService history resolution):
                         // allow relative paths like "history/memory" to resolve outside bin/.
                         string basePath;
                         if (Path.IsPathRooted(vectorDbBasePath))
                         {
                             basePath = vectorDbBasePath;
                         }
                         else
                         {
                             var di = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                             basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, vectorDbBasePath);
                             for (int i = 0; i < 6 && di != null; i++)
                             {
                                 var candidate = Path.Combine(di.FullName, vectorDbBasePath);
                                 if (Directory.Exists(candidate) || File.Exists(candidate)) { basePath = candidate; break; }
                                 di = di.Parent;
                             }
                         }

                         basePath = Path.GetFullPath(basePath);

                         var fullPath = Path.Combine(basePath, memoryKey, "vectors.json");

                         if (File.Exists(fullPath))
                         {
                             var json = File.ReadAllText(fullPath);
                             var obj = JObject.Parse(json);
                             var centroids = obj["Centroids"] as JArray;
                             VectorChunksText.Text = $"{centroids?.Count ?? 0}";
                         }
                         else
                         {
                             VectorChunksText.Text = "0";
                         }
                     }
                     else
                     {
                         VectorChunksText.Text = "--";
                     }
                 }
                 catch
                 {
                     VectorChunksText.Text = "--";
                 }
             }
             catch (Exception ex)
             {
                 StatusText.Text = $"Stats error: {ex.Message}";
             }
         }

        private void RefreshStatsButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshMemoryStats();
            StatusText.Text = "Stats refreshed";
        }
        #endregion

        #region Render to Memory
        private async void RenderToMemoryButton_Click(object sender, RoutedEventArgs e)
        {
            var cfg = _svc?.Current?.Ollama;
            
            // Get speaker - use forced speaker if set
            var speaker = cfg?.ForceSpeakerOverrideEnabled == true && !string.IsNullOrWhiteSpace(cfg?.ForcedSpeakerId) 
                ? cfg.ForcedSpeakerId 
                : "UnknownSpeaker";

            var (tokens, messages) = OllamaService.GetHotContextStats(speaker);
            if (messages == 0)
            {
                StatusText.Text = $"No conversation history for '{speaker}'";
                return;
            }

            // Get current memory key
            string memoryKey = "default";
            if (!string.IsNullOrWhiteSpace(cfg?.SystemPromptPath))
            {
                try { memoryKey = Path.GetFileNameWithoutExtension(cfg.SystemPromptPath)?.ToLowerInvariant() ?? "default"; }
                catch { }
            }

            // Confirm with user
            var result = MessageBox.Show(
                $"Archive {messages} messages ({tokens:N0} tokens) to memory '{memoryKey}'?\n\nThis will clear the hot context after archiving.",
                "Confirm Archive",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                RenderToMemoryButton.IsEnabled = false;
                StatusText.Text = "Archiving...";

                var (archived, tokensBefore) = await OllamaService.ForceArchiveToMemoryAsync(speaker);

                if (archived > 0)
                {
                    StatusText.Text = $"Archived {archived} messages ({tokensBefore:N0} tokens) to '{memoryKey}'";
                }
                else
                {
                    StatusText.Text = "No messages archived";
                }

                RefreshMemoryStats();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Archive failed: {ex.Message}";
            }
            finally
            {
                RenderToMemoryButton.IsEnabled = true;
            }
        }
        #endregion

        #region Clear Memory
        private async void ClearMemoryButton_Click(object sender, RoutedEventArgs e)
        {
            var cfg = _svc?.Current?.Ollama;
            
            // Get current memory key
            string memoryKey = "default";
            if (!string.IsNullOrWhiteSpace(cfg?.SystemPromptPath))
            {
                try { memoryKey = Path.GetFileNameWithoutExtension(cfg.SystemPromptPath)?.ToLowerInvariant() ?? "default"; }
                catch { }
            }

            // Confirm with user - this is destructive
            var result = MessageBox.Show(
                $"Are you sure you want to permanently delete ALL vector memory for '{memoryKey}'?\n\n" +
                "This will remove:\n" +
                "• All topic centroids\n" +
                "• All rolling summaries\n" +
                "• All pinned facts\n\n" +
                "This action cannot be undone.",
                "Confirm Clear Memory",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                ClearMemoryButton.IsEnabled = false;
                StatusText.Text = "Clearing memory...";

                await OllamaService.ClearVectorMemoryAsync();

                StatusText.Text = $"Memory cleared for '{memoryKey}'";
                RefreshMemoryStats();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Clear failed: {ex.Message}";
            }
            finally
            {
                ClearMemoryButton.IsEnabled = true;
            }
        }
        #endregion

        private async Task RefreshEmbeddingsModelListAsync()
        {
            try
            {
                var cur = _svc?.Current;
                var eff = _svc?.GetDefaultsEffective();

                // Use LM Studio base URL for embeddings models
                string baseLm = cur?.Ollama?.LmStudioBaseUrl ?? eff?.Ollama?.LmStudioBaseUrl ?? "http://127.0.0.1:1234";
                if (string.IsNullOrWhiteSpace(baseLm))
                {
                    StatusText.Text = "LM Studio Base URL not configured";
                    return;
                }

                string url = baseLm.TrimEnd('/') + "/v1/models";

                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(5);
                    var json = await http.GetStringAsync(url);
                    var names = new List<string>();
                    var obj = JObject.Parse(json);

                    // LM Studio returns models in "data" array with "id" field
                    if (obj["data"] != null)
                    {
                        names.AddRange(obj["data"]
                            .Select(m => m["id"]?.ToString())
                            .Where(n => !string.IsNullOrWhiteSpace(n)));
                    }

                    // Preserve current selection
                    var currentSelection = EmbeddingsModelComboBox.Text;
                    EmbeddingsModelComboBox.ItemsSource = names;

                    // Restore selection if it exists in the list
                    if (!string.IsNullOrWhiteSpace(currentSelection) && names.Contains(currentSelection))
                    {
                        EmbeddingsModelComboBox.Text = currentSelection;
                    }

                    StatusText.Text = $"Found {names.Count} model(s)";
                }
            }
            catch (HttpRequestException)
            {
                StatusText.Text = "Cannot connect to LM Studio. Is it running?";
            }
            catch (TaskCanceledException)
            {
                StatusText.Text = "Connection to LM Studio timed out";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void RefreshEmbeddingsModelsButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshEmbeddingsModelListAsync();
        }

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

        private void BrowseVectorDbPathButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dlg = new WinForms.SaveFileDialog())
                {
                    dlg.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                    dlg.Title = "Select Vector Database JSON File";
                    var current = VectorDbPathTextBox.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        try { dlg.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(current)); } catch { }
                        dlg.FileName = Path.GetFileName(current);
                    }
                    if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        VectorDbPathTextBox.Text = TryMakeRelative(dlg.FileName);
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

                // Get selected embeddings model from ComboBox
                string embeddingsModel = EmbeddingsModelComboBox.Text?.Trim() ?? cur.Ollama.EmbeddingsModel;

                // Build updated OllamaSettings with vector memory fields
                var next = cur.Ollama with
                {
                    VectorMemoryEnabled = VectorMemoryEnabledCheckBox.IsChecked ?? cur.Ollama.VectorMemoryEnabled,
                    HotContextTokenLimit = int.TryParse(HotContextTokenLimitTextBox.Text, out var hctl) ? hctl : cur.Ollama.HotContextTokenLimit,
                    WarmSummaryTokenLimit = int.TryParse(WarmSummaryTokenLimitTextBox.Text, out var wstl) ? wstl : cur.Ollama.WarmSummaryTokenLimit,
                    MemoryContextBudget = int.TryParse(MemoryContextBudgetTextBox.Text, out var mcb) ? mcb : cur.Ollama.MemoryContextBudget,
                    VectorDbPath = string.IsNullOrWhiteSpace(VectorDbPathTextBox.Text) ? cur.Ollama.VectorDbPath : VectorDbPathTextBox.Text,
                    EmbeddingsModel = string.IsNullOrWhiteSpace(embeddingsModel) ? cur.Ollama.EmbeddingsModel : embeddingsModel,
                    ChunkSizeTokens = int.TryParse(ChunkSizeTokensTextBox.Text, out var cst) ? cst : cur.Ollama.ChunkSizeTokens,
                    ChunkOverlapTokens = int.TryParse(ChunkOverlapTokensTextBox.Text, out var cot) ? cot : cur.Ollama.ChunkOverlapTokens,
                    VectorSearchTopK = int.TryParse(VectorSearchTopKTextBox.Text, out var vstk) ? vstk : cur.Ollama.VectorSearchTopK,
                    MinRetrievalScore = double.TryParse(MinRetrievalScoreTextBox.Text, out var mrs) ? mrs : cur.Ollama.MinRetrievalScore,
                    RecencyBoostFactor = double.TryParse(RecencyBoostFactorTextBox.Text, out var rbf) ? rbf : cur.Ollama.RecencyBoostFactor
                };

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
