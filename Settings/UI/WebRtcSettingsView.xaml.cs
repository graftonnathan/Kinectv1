using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kinectv1.Settings;
using Kinectv1.Voice;

namespace Kinectv1.UI.Settings
{
    public partial class WebRtcSettingsView : UserControl
    {
        private readonly SettingsService _svc = App.SettingsProvider;
        private DispatcherTimer _refreshTimer;

        public WebRtcSettingsView()
        {
            InitializeComponent();
            Loaded += WebRtcSettingsView_Loaded;
            Unloaded += WebRtcSettingsView_Unloaded;
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
                
                // Show web content path
                WebFolderPathText.Text = WebRtcSignalingServer.GetWebContentPath();
                
                // Subscribe to connection status changes
                WebRtcSpeakerTracker.OnConnectionStatusChanged += OnConnectionStatusChanged;
                WebRtcSpeakerTracker.OnSpeakerActivity += OnSpeakerActivity;
                
                // Initial refresh
                RefreshConnectionStatus();
                RefreshSpeakersList();
                
                // Start refresh timer for live updates
                _refreshTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _refreshTimer.Tick += (s, args) =>
                {
                    RefreshConnectionStatus();
                    RefreshSpeakersList();
                };
                _refreshTimer.Start();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Load error: {ex.Message}";
            }
        }

        private void WebRtcSettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe from events
            WebRtcSpeakerTracker.OnConnectionStatusChanged -= OnConnectionStatusChanged;
            WebRtcSpeakerTracker.OnSpeakerActivity -= OnSpeakerActivity;
            
            // Stop refresh timer
            _refreshTimer?.Stop();
            _refreshTimer = null;
        }

        private void OnConnectionStatusChanged(WebRtcConnectionInfo info)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateConnectionUI(info)));
            }
            catch { }
        }

        private void OnSpeakerActivity(string speakerId, DateTime timestamp)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(RefreshSpeakersList));
            }
            catch { }
        }

        private void RefreshConnectionStatus()
        {
            try
            {
                var info = WebRtcSpeakerTracker.GetConnectionInfo();
                UpdateConnectionUI(info);
            }
            catch { }
        }

        private void UpdateConnectionUI(WebRtcConnectionInfo info)
        {
            try
            {
                // Update status indicator
                ConnectionStatusText.Text = info.StatusText;
                
                var color = (Color)ColorConverter.ConvertFromString(info.StatusColor);
                StatusIndicator.Fill = new SolidColorBrush(color);
                
                // Update client count
                ClientCountText.Text = info.ConnectedClients == 1 
                    ? "1 client connected" 
                    : $"{info.ConnectedClients} clients connected";
                
                // Update audio stats
                if (info.ConnectedClients > 0 && info.InboundFramesPerSecond > 0)
                {
                    AudioInStatsText.Text = $"{info.InboundFramesPerSecond} frames/sec";
                }
                else if (info.IsListening)
                {
                    AudioInStatsText.Text = "Waiting for audio...";
                }
                else
                {
                    AudioInStatsText.Text = "--";
                }
            }
            catch { }
        }

        private void RefreshSpeakersList()
        {
            try
            {
                var speakers = WebRtcSpeakerTracker.GetRecentSpeakers(10);
                
                if (speakers.Count == 0)
                {
                    NoSpeakersText.Visibility = Visibility.Visible;
                    SpeakersListControl.Visibility = Visibility.Collapsed;
                }
                else
                {
                    NoSpeakersText.Visibility = Visibility.Collapsed;
                    SpeakersListControl.Visibility = Visibility.Visible;
                    SpeakersListControl.ItemsSource = speakers;
                }
            }
            catch { }
        }

        private void RefreshSpeakersButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshConnectionStatus();
            RefreshSpeakersList();
            StatusText.Text = "Refreshed";
        }

        private void OpenWebFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = WebRtcSignalingServer.GetWebContentPath();
                
                // Create directory if it doesn't exist
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    StatusText.Text = "Created web folder";
                }
                
                // Open in explorer
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void ReloadWebContentButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Signal any active WebRTC transport to reload
                // This is a simple approach - ideally we'd have a reference to the active server
                StatusText.Text = "To reload, restart WebRTC or navigate to /reload in browser";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
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
                StatusText.Text = "URL copied to clipboard";
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
                StatusText.Text = "Saved";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
            }
        }
    }
}
