using System;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using Kinectv1.Mumble;

namespace Kinectv1
{
    public partial class MumbleSettingsView : UserControl
    {
        public MumbleSettingsView()
        {
            InitializeComponent();
            LoadValues();

            EnabledCheckBox.Checked += (s, e) => { SaveSnapshot(enabled: true); TryConnectMumble(); };
            EnabledCheckBox.Unchecked += (s, e) => { SaveSnapshot(enabled: false); TryDisconnectMumble(); };
            AutoConnectCheckBox.Checked += (s, e) => SaveSnapshot(autoConnect: true);
            AutoConnectCheckBox.Unchecked += (s, e) => SaveSnapshot(autoConnect: false);
            ValidateTlsCheckBox.Checked += (s, e) => SaveSnapshot(validateTls: true);
            ValidateTlsCheckBox.Unchecked += (s, e) => SaveSnapshot(validateTls: false);
            SelfMuteCheckBox.Checked += (s, e) => SaveSnapshot(selfMute: true);
            SelfMuteCheckBox.Unchecked += (s, e) => SaveSnapshot(selfMute: false);
            SelfDeafCheckBox.Checked += (s, e) => SaveSnapshot(selfDeaf: true);
            SelfDeafCheckBox.Unchecked += (s, e) => SaveSnapshot(selfDeaf: false);
            TextCommandsCheckBox.Checked += (s, e) => SaveSnapshot(textCommands: true);
            TextCommandsCheckBox.Unchecked += (s, e) => SaveSnapshot(textCommands: false);
        }

        private void LoadValues()
        {
            try
            {
                var snap = App.SettingsProvider?.Current;
                var mb = snap?.Mumble;
                if (mb != null)
                {
                    EnabledCheckBox.IsChecked = mb.Enabled;
                    AutoConnectCheckBox.IsChecked = mb.AutoConnect;
                    HostTextBox.Text = mb.Host ?? "localhost";
                    PortTextBox.Text = mb.Port.ToString();
                    UsernameTextBox.Text = mb.Username ?? "Kinectv1";
                    ServerPasswordBox.Password = string.IsNullOrEmpty(mb.ServerPassword) ? string.Empty : new string('•', 8);
                    ChannelTextBox.Text = mb.Channel ?? "/";
                    ChannelPasswordBox.Password = string.IsNullOrEmpty(mb.ChannelPassword) ? string.Empty : new string('•', 8);
                    ValidateTlsCheckBox.IsChecked = mb.ValidateTls;
                    SelfMuteCheckBox.IsChecked = mb.SelfMute;
                    SelfDeafCheckBox.IsChecked = mb.SelfDeaf;
                    OpusBitrateTextBox.Text = mb.OpusBitrate.ToString();
                    VadThresholdTextBox.Text = mb.VadThreshold.ToString();
                    ReconnectBackoffTextBox.Text = mb.ReconnectBackoffMs.ToString();
                    TextCommandsCheckBox.IsChecked = mb.TextCommandsEnabled;
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
                var host = HostTextBox.Text?.Trim();
                int.TryParse(PortTextBox.Text, out var port);
                var username = UsernameTextBox.Text?.Trim();
                var serverPw = ServerPasswordBox.Password;
                var channel = ChannelTextBox.Text?.Trim();
                var channelPw = ChannelPasswordBox.Password;
                bool.TryParse(ValidateTlsCheckBox.IsChecked?.ToString() ?? "false", out var validateTls);
                bool.TryParse(SelfMuteCheckBox.IsChecked?.ToString() ?? "true", out var selfMute);
                bool.TryParse(SelfDeafCheckBox.IsChecked?.ToString() ?? "true", out var selfDeaf);
                int.TryParse(OpusBitrateTextBox.Text, out var bitrate);
                int.TryParse(VadThresholdTextBox.Text, out var vad);
                int.TryParse(ReconnectBackoffTextBox.Text, out var backoff);
                bool.TryParse(TextCommandsCheckBox.IsChecked?.ToString() ?? "true", out var txtCmd);

                SaveSnapshot(host: host, port: port, username: username, serverPassword: serverPw, channel: channel, channelPassword: channelPw,
                             validateTls: validateTls, selfMute: selfMute, selfDeaf: selfDeaf, opusBitrate: bitrate, vadThreshold: vad,
                             reconnectBackoffMs: backoff, textCommands: txtCmd);
                Status("Settings saved.");
            }
            catch (Exception ex)
            {
                Status($"Error saving settings: {ex.Message}", true);
            }
        }

        private void SaveSnapshot(bool? enabled = null, bool? autoConnect = null,
            string host = null, int? port = null, string username = null, string serverPassword = null,
            string channel = null, string channelPassword = null,
            bool? validateTls = null, bool? selfMute = null, bool? selfDeaf = null,
            int? opusBitrate = null, int? vadThreshold = null, int? reconnectBackoffMs = null,
            bool? textCommands = null)
        {
            try
            {
                App.SettingsProvider?.Save(curr =>
                {
                    var mb = curr.Mumble;
                    // Only accept real password changes (not bullets)
                    var serverPwToSave = (!string.IsNullOrWhiteSpace(serverPassword) && serverPassword.Trim('•').Length == serverPassword.Length) ? serverPassword : mb.ServerPassword;
                    var channelPwToSave = (!string.IsNullOrWhiteSpace(channelPassword) && channelPassword.Trim('•').Length == channelPassword.Length) ? channelPassword : mb.ChannelPassword;

                    var next = new Kinectv1.Settings.MumbleSettings(
                        Enabled: enabled ?? mb.Enabled,
                        AutoConnect: autoConnect ?? mb.AutoConnect,
                        Host: host ?? mb.Host,
                        Port: port ?? mb.Port,
                        Username: username ?? mb.Username,
                        ServerPassword: serverPwToSave,
                        Channel: channel ?? mb.Channel,
                        ChannelPassword: channelPwToSave,
                        ValidateTls: validateTls ?? mb.ValidateTls,
                        SelfMute: selfMute ?? mb.SelfMute,
                        SelfDeaf: selfDeaf ?? mb.SelfDeaf,
                        OpusBitrate: opusBitrate ?? mb.OpusBitrate,
                        VadThreshold: vadThreshold ?? mb.VadThreshold,
                        ReconnectBackoffMs: reconnectBackoffMs ?? mb.ReconnectBackoffMs,
                        TextCommandsEnabled: textCommands ?? mb.TextCommandsEnabled
                    );

                    return new Kinectv1.Settings.AppSettings(curr.Audio, curr.Tts, curr.Vad, curr.Ollama, curr.Discord, next);
                });
            }
            catch (Exception ex)
            {
                Status($"Error saving snapshot: {ex.Message}", true);
            }
        }

        private void TryConnectMumble()
        {
            try
            {
                var mb = App.SettingsProvider?.Current?.Mumble;
                if (mb == null || !mb.Enabled) return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await MumbleClientManager.StartAsync();
                        await MumbleClientManager.ConnectAsync(mb.Host, mb.Port, mb.Username, mb.ServerPassword, mb.Channel, mb.ChannelPassword, mb.ValidateTls, mb.SelfMute, mb.SelfDeaf);
                        Status($"Connecting to {mb.Host}:{mb.Port} as {mb.Username}...");
                    }
                    catch (Exception ex)
                    {
                        Status($"Mumble connect failed: {ex.Message}", true);
                    }
                });
            }
            catch (Exception ex)
            {
                Status($"Mumble connect error: {ex.Message}", true);
            }
        }

        private void TryDisconnectMumble()
        {
            _ = Task.Run(async () =>
            {
                try { await MumbleClientManager.DisconnectAsync(); Status("Mumble disconnected"); }
                catch (Exception ex) { Status($"Mumble disconnect failed: {ex.Message}", true); }
            });
        }

        private void Status(string msg, bool error = false)
        {
            StatusText.Text = msg;
            StatusText.Foreground = error ? System.Windows.Media.Brushes.OrangeRed : (System.Windows.Media.Brush)FindResource("TextSecondary");
        }
    }
}
