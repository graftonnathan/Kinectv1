using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Kinectv1.Settings;

namespace Kinectv1.UI.Settings
{
    public partial class WebRtcSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;

        public WebRtcSettingsView()
        {
            InitializeComponent();
            Loaded += WebRtcSettingsView_Loaded;
        }

        private void WebRtcSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _svc?.Current?.WebRtc;
                if (cfg == null) return;

                EnabledCheckBox.IsChecked = cfg.Enabled;
                PortTextBox.Text = cfg.Port.ToString();

                UpdateJoinUrl(cfg.Port);
            }
            catch { }
        }

        private void UpdateJoinUrl(int port)
        {
            try
            {
                var ip = GetLocalIPAddress();
                JoinUrlTextBox.Text = $"http://{ip}:{port}/";
            }
            catch
            {
                JoinUrlTextBox.Text = $"http://localhost:{port}/";
            }
        }

        private static string GetLocalIPAddress()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var ip = addr.Address.ToString();
                            if (!ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                                return ip;
                        }
                    }
                }
            }
            catch { }
            return "localhost";
        }

        private void CopyUrlButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(JoinUrlTextBox.Text);
                StatusText.Text = "? URL copied to clipboard";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Copy failed: {ex.Message}";
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cur = _svc?.Current ?? throw new InvalidOperationException("Settings unavailable");

                var port = int.TryParse(PortTextBox.Text, out var p) && p > 0 && p < 65536 ? p : cur.WebRtc.Port;

                var next = new WebRtcSettings(
                    Enabled: EnabledCheckBox.IsChecked ?? false,
                    Port: port
                );

                var updated = cur with { WebRtc = next };
                SettingsService.ValidateOrThrow(updated);
                _svc.Save(updated);

                UpdateJoinUrl(port);
                StatusText.Text = "? Saved";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }
    }
}
