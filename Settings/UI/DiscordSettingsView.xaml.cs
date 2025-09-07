using System;
using System.Windows;
using System.Windows.Controls;
using Kinectv1.Settings;

namespace Kinectv1.UI.Settings
{
    public partial class DiscordSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;

        public DiscordSettingsView()
        {
            InitializeComponent();
            Loaded += DiscordSettingsView_Loaded;
        }

        private void DiscordSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _svc?.Current?.Discord;
                if (cfg == null) return;
                BotEnabledCheckBox.IsChecked = cfg.Enabled;
                PrefixTextBox.Text = cfg.Prefix;
                AutoJoinCheckBox.IsChecked = cfg.AutoJoinVoice;
                TokenBox.Password = cfg.Token ?? string.Empty;
            }
            catch { }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cur = _svc?.Current ?? throw new InvalidOperationException("Settings unavailable");
                var next = new DiscordSettings(
                    BotEnabledCheckBox.IsChecked ?? false,
                    PrefixTextBox.Text ?? string.Empty,
                    AutoJoinCheckBox.IsChecked ?? false,
                    TokenBox.Password ?? string.Empty
                );
                var updated = cur with { Discord = next };
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
