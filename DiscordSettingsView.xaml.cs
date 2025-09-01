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

            BotEnabledCheckBox.Checked += (s, e) => AppSettings.SaveDiscordBotEnabled(true);
            BotEnabledCheckBox.Unchecked += (s, e) => AppSettings.SaveDiscordBotEnabled(false);
            AutoJoinCheckBox.Checked += (s, e) => AppSettings.SaveDiscordAutoJoinVoice(true);
            AutoJoinCheckBox.Unchecked += (s, e) => AppSettings.SaveDiscordAutoJoinVoice(false);
        }

        private void LoadValues()
        {
            try
            {
                BotEnabledCheckBox.IsChecked = AppSettings.LoadDiscordBotEnabled();
                PrefixTextBox.Text = AppSettings.LoadDiscordBotPrefix() ?? string.Empty;
                AutoJoinCheckBox.IsChecked = AppSettings.LoadDiscordAutoJoinVoice();
                // Token is sensitive; do not show actual value; indicate presence only
                var token = AppSettings.LoadDiscordBotToken();
                if (!string.IsNullOrEmpty(token)) TokenBox.Password = new string('•', 8);
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
                AppSettings.SaveDiscordBotEnabled(BotEnabledCheckBox.IsChecked == true);

                var pwd = TokenBox.Password ?? string.Empty;
                // Save when a non-empty value is present (we avoid writing bullets placeholder)
                if (!string.IsNullOrWhiteSpace(pwd) && pwd.Trim('•').Length == pwd.Length)
                {
                    AppSettings.SaveDiscordBotToken(pwd.Trim());
                }

                var prefix = string.IsNullOrWhiteSpace(PrefixTextBox.Text) ? "!" : PrefixTextBox.Text.Trim();
                AppSettings.SaveDiscordBotPrefix(prefix);
                AppSettings.SaveDiscordAutoJoinVoice(AutoJoinCheckBox.IsChecked == true);

                Status("Settings saved.");
            }
            catch (Exception ex)
            {
                Status($"Error saving settings: {ex.Message}", true);
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
