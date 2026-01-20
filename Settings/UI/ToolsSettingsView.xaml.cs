using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kinectv1.Settings;
using Kinectv1.Llm.Tools;

namespace Kinectv1.UI.Settings
{
    public partial class ToolsSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;

        public ToolsSettingsView()
        {
            InitializeComponent();
            Loaded += ToolsSettingsView_Loaded;
        }

        private void ToolsSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _svc?.Current?.Ollama;
                if (cfg == null) return;

                ToolsEnabledCheckBox.IsChecked = cfg.ToolsEnabled;
                UpdateToolStatus();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading settings: {ex.Message}";
            }
        }

        private void ToolsEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateToolStatus();
            StatusText.Text = "Changed (not saved)";
        }

        private void UpdateToolStatus()
        {
            var enabled = ToolsEnabledCheckBox.IsChecked == true;
            
            if (enabled)
            {
                WebSearchStatusText.Text = "Enabled";
                WebSearchStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
            }
            else
            {
                WebSearchStatusText.Text = "Disabled";
                WebSearchStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            }
        }

        private async void TestSearchButton_Click(object sender, RoutedEventArgs e)
        {
            var query = TestSearchQueryTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                TestSearchResultText.Text = "Please enter a search query";
                return;
            }

            try
            {
                TestSearchButton.IsEnabled = false;
                TestSearchResultText.Text = "Searching...";
                TestSearchResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));

                var tool = new WebSearchTool();
                var result = await Task.Run(() => tool.ExecuteAsync($"{{\"query\": \"{query}\"}}"));

                // Show truncated result
                var displayResult = result;
                if (displayResult.Length > 500)
                {
                    displayResult = displayResult.Substring(0, 500) + "...\n[Truncated for display]";
                }

                TestSearchResultText.Text = displayResult;
                TestSearchResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
            }
            catch (Exception ex)
            {
                TestSearchResultText.Text = $"Search failed: {ex.Message}";
                TestSearchResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
            }
            finally
            {
                TestSearchButton.IsEnabled = true;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cur = _svc?.Current ?? throw new InvalidOperationException("Settings unavailable");

                // Update only the ToolsEnabled field using with expression
                var next = cur.Ollama with
                {
                    ToolsEnabled = ToolsEnabledCheckBox.IsChecked ?? false
                };

                var updated = cur with { Ollama = next };
                SettingsService.ValidateOrThrow(updated);
                _svc.Save(updated);
                
                StatusText.Text = "Saved";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Save failed: {ex.Message}";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
            }
        }
    }
}
