using System;
using System.Windows;
using System.Windows.Controls;
using Kinectv1.Settings;

namespace Kinectv1.UI.Settings
{
    public partial class MumbleSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;

        public MumbleSettingsView()
        {
            InitializeComponent();
            Loaded += MumbleSettingsView_Loaded;
        }

        private void MumbleSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _svc?.Current?.Mumble;
                if (cfg == null) return;
                EnabledCheckBox.IsChecked = cfg.Enabled;
                AutoConnectCheckBox.IsChecked = cfg.AutoConnect;
                HostTextBox.Text = cfg.Host;
                PortTextBox.Text = cfg.Port.ToString();
                UsernameTextBox.Text = cfg.Username;
                ServerPasswordBox.Password = cfg.ServerPassword ?? string.Empty;
                ChannelTextBox.Text = cfg.Channel;
                ChannelPasswordBox.Password = cfg.ChannelPassword ?? string.Empty;
                ValidateTlsCheckBox.IsChecked = cfg.ValidateTls;
                SelfMuteCheckBox.IsChecked = cfg.SelfMute;
                SelfDeafCheckBox.IsChecked = cfg.SelfDeaf;
                OpusBitrateTextBox.Text = cfg.OpusBitrate.ToString();
                VadThresholdTextBox.Text = cfg.VadThreshold.ToString();
                ReconnectBackoffTextBox.Text = cfg.ReconnectBackoffMs.ToString();
                TextCommandsCheckBox.IsChecked = cfg.TextCommandsEnabled;
            }
            catch { }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cur = _svc?.Current ?? throw new InvalidOperationException("Settings unavailable");
                var next = new MumbleSettings(
                    Enabled: EnabledCheckBox.IsChecked ?? false,
                    AutoConnect: AutoConnectCheckBox.IsChecked ?? false,
                    Host: HostTextBox.Text ?? string.Empty,
                    Port: int.TryParse(PortTextBox.Text, out var p) ? p : cur.Mumble.Port,
                    Username: UsernameTextBox.Text ?? string.Empty,
                    ServerPassword: ServerPasswordBox.Password ?? string.Empty,
                    Channel: ChannelTextBox.Text ?? string.Empty,
                    ChannelPassword: ChannelPasswordBox.Password ?? string.Empty,
                    ValidateTls: ValidateTlsCheckBox.IsChecked ?? true,
                    SelfMute: SelfMuteCheckBox.IsChecked ?? true,
                    SelfDeaf: SelfDeafCheckBox.IsChecked ?? true,
                    OpusBitrate: int.TryParse(OpusBitrateTextBox.Text, out var ob) ? ob : cur.Mumble.OpusBitrate,
                    VadThreshold: int.TryParse(VadThresholdTextBox.Text, out var vt) ? vt : cur.Mumble.VadThreshold,
                    ReconnectBackoffMs: int.TryParse(ReconnectBackoffTextBox.Text, out var rb) ? rb : cur.Mumble.ReconnectBackoffMs,
                    TextCommandsEnabled: TextCommandsCheckBox.IsChecked ?? cur.Mumble.TextCommandsEnabled,
                    TlsValidate: cur.Mumble.TlsValidate
                );
                var updated = cur with { Mumble = next };
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
