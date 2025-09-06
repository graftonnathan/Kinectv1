using System;
using System.Windows;
using System.Windows.Controls;

namespace Kinectv1
{
    public partial class DiscordSettingsView : UserControl
    {
        public DiscordSettingsView()
        {
            InitializeComponent();
            LoadValues();

            // Immediate persistence through JSON pipeline for toggles
            BotEnabledCheckBox.Checked += (s, e) => SaveSnapshot(enabled: true);
            BotEnabledCheckBox.Unchecked += (s, e) => SaveSnapshot(enabled: false);
            AutoJoinCheckBox.Checked += (s, e) => SaveSnapshot(autoJoin: true);
            AutoJoinCheckBox.Unchecked += (s, e) => SaveSnapshot(autoJoin: false);
        }

        private void LoadValues()
        {
            try
            {
                var snap = App.SettingsProvider?.Current;
                var dc = snap?.Discord;
                if (dc != null)
                {
                    BotEnabledCheckBox.IsChecked = dc.Enabled;
                    PrefixTextBox.Text = dc.Prefix ?? "!";
                    AutoJoinCheckBox.IsChecked = dc.AutoJoinVoice;
                    // Token is sensitive; mask if present
                    if (!string.IsNullOrEmpty(dc.Token)) TokenBox.Password = new string('•', 8);
                }
                else
                {
                    BotEnabledCheckBox.IsChecked = false;
                    PrefixTextBox.Text = "!";
                    AutoJoinCheckBox.IsChecked = false;
                    TokenBox.Password = string.Empty;
                }
                Status("Settings loaded.");
            }
            catch (Exception ex)
            {
                Status($"Error loading settings: {ex.Message}", true);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var enabled = BotEnabledCheckBox.IsChecked == true;
                var prefix = string.IsNullOrWhiteSpace(PrefixTextBox.Text) ? "!" : PrefixTextBox.Text.Trim();
                var auto = AutoJoinCheckBox.IsChecked == true;

                // Only persist token if user typed a real value (not bullets)
                var tokenInput = TokenBox.Password ?? string.Empty;
                string tokenToSave = null;
                if (!string.IsNullOrWhiteSpace(tokenInput) && tokenInput.Trim('•').Length == tokenInput.Length)
                {
                    tokenToSave = tokenInput.Trim();
                }

                SaveSnapshot(enabled: enabled, prefix: prefix, autoJoin: auto, token: tokenToSave);
                Status("Settings saved.");
            }
            catch (Exception ex)
            {
                Status($"Error saving settings: {ex.Message}", true);
            }
        }

        private void SaveSnapshot(bool? enabled = null, string prefix = null, bool? autoJoin = null, string token = null)
        {
            try
            {
                var svc = App.SettingsProvider; var curr = svc?.Current; if (svc == null || curr == null) return;
                var dc = curr.Discord;
                var nextDiscord = new Kinectv1.Settings.DiscordSettings(
                    Enabled: enabled ?? dc.Enabled,
                    Prefix: prefix ?? dc.Prefix ?? "!",
                    AutoJoinVoice: autoJoin ?? dc.AutoJoinVoice,
                    Token: token ?? dc.Token
                );
                var next = new Kinectv1.Settings.AppSettings(curr.Audio, curr.Tts, curr.Vad, curr.Ollama, nextDiscord, curr.Mumble, curr.Ui, curr.Asr, curr.Stt, curr.Face, curr.App);
                svc.Save(next);
            }
            catch (Exception ex)
            {
                Status($"Error saving snapshot: {ex.Message}", true);
            }
        }

        private void Status(string msg, bool error = false)
        {
            StatusText.Text = msg;
            if (error)
                StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            else
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
        }
    }
}
