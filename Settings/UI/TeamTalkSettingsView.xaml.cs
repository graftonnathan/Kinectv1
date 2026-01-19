using System;
using System.Windows;
using System.Windows.Controls;
using Kinectv1.Settings;

namespace Kinectv1.UI.Settings
{
    public partial class TeamTalkSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;

        public TeamTalkSettingsView()
        {
            InitializeComponent();
            Loaded += TeamTalkSettingsView_Loaded;
        }

        private void TeamTalkSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _svc?.Current?.TeamTalk;
                if (cfg == null) return;
                EnabledCheckBox.IsChecked = cfg.Enabled;
                AutoConnectCheckBox.IsChecked = cfg.AutoConnect;
                var enc = this.FindName("EncryptedCheckBox") as CheckBox;
                if (enc != null) enc.IsChecked = cfg.Encrypted;
                var tls = this.FindName("TlsValidateComboBox") as ComboBox;
                if (tls != null) tls.Text = cfg.TlsValidate.ToString();
                HostTextBox.Text = cfg.Host;
                TcpPortTextBox.Text = cfg.TcpPort.ToString();
                
                // Optional UI elements may not exist yet in XAML; guard with FindName
                var udp = this.FindName("UdpPortTextBox") as TextBox;
                if (udp != null) udp.Text = cfg.UdpPort.ToString();
                var nick = this.FindName("NicknameTextBox") as TextBox;
                if (nick != null) nick.Text = cfg.Nickname ?? string.Empty;
                var chanPath = this.FindName("ChannelPathTextBox") as TextBox;
                if (chanPath != null) chanPath.Text = cfg.ChannelPath ?? string.Empty;

                UsernameTextBox.Text = cfg.Username;
                PasswordBox.Password = cfg.Password ?? string.Empty;
                ChannelTextBox.Text = cfg.Channel;
                ChannelPasswordBox.Password = cfg.ChannelPassword ?? string.Empty;
            }
            catch { }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cur = _svc?.Current ?? throw new InvalidOperationException("Settings unavailable");

                var udp = this.FindName("UdpPortTextBox") as TextBox;
                var nick = this.FindName("NicknameTextBox") as TextBox;
                var chanPath = this.FindName("ChannelPathTextBox") as TextBox;
                var enc = this.FindName("EncryptedCheckBox") as CheckBox;
                var tls = this.FindName("TlsValidateComboBox") as ComboBox;
                
                var tlsVal = cur.TeamTalk.TlsValidate;
                if (tls != null && !string.IsNullOrWhiteSpace(tls.Text))
                {
                    if (Enum.TryParse<TeamTalkTlsValidate>(tls.Text, ignoreCase: true, out var parsed))
                        tlsVal = parsed;
                }

                var next = new TeamTalkSettings(
                    Enabled: EnabledCheckBox.IsChecked ?? false,
                    AutoConnect: AutoConnectCheckBox.IsChecked ?? false,
                    Host: HostTextBox.Text ?? string.Empty,
                    TcpPort: int.TryParse(TcpPortTextBox.Text, out var p) ? p : cur.TeamTalk.TcpPort,
                    UdpPort: udp != null && int.TryParse(udp.Text, out var up) ? up : cur.TeamTalk.UdpPort,
                    Encrypted: enc?.IsChecked ?? cur.TeamTalk.Encrypted,
                    TlsValidate: tlsVal,
                    Nickname: nick?.Text,
                    Username: UsernameTextBox.Text ?? string.Empty,
                    Password: PasswordBox.Password ?? string.Empty,
                    Channel: ChannelTextBox.Text ?? string.Empty,
                    ChannelPassword: ChannelPasswordBox.Password ?? string.Empty,
                    ChannelPath: chanPath?.Text
                );

                var updated = cur with { TeamTalk = next };
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
