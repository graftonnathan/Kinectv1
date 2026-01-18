using System;
using System.Threading;
using System.Threading.Tasks;
using MumbleSharp;

namespace Kinectv1.Mumble
{
    public static class MumbleClientManager
    {
        private static readonly SemaphoreSlim _voiceOpLock = new SemaphoreSlim(1, 1);
        public static SemaphoreSlim VoiceOpLock => _voiceOpLock;

        public static event Action<string> OnStatusChanged;
        public static event Action<float> OnRmsLevel; // kept for UI compatibility (not used)
        public static event Action<string> OnError;

        private static volatile bool _isRunning = false;
        private static volatile bool _isConnected = false;
        private static CancellationTokenSource _cts;

        // Console demo hosting
        private static BasicMumbleProtocol _protocol;
        private static MumbleConnection _conn;
        private static Thread _pump;
        private static volatile bool _run;

        // Expose last-used settings for GUI
        public static string CurrentHost { get; private set; }
        public static int CurrentPort { get; private set; }
        public static string CurrentUsername { get; private set; }
        public static string CurrentChannel { get; private set; }
        public static bool CurrentSelfMute { get; private set; }
        public static bool CurrentSelfDeaf { get; private set; }

        public static bool IsRunning => _isRunning;
        public static bool IsConnected => _isConnected;

        public static Task<bool> StartAsync()
        {
            _isRunning = true;
            OnStatusChanged?.Invoke("Initialized");
            return Task.FromResult(true);
        }

        public static async Task ShutdownAsync()
        {
            try { await DisconnectAsync(); _isRunning = false; OnStatusChanged?.Invoke("Stopped"); }
            catch (Exception ex) { OnError?.Invoke($"Shutdown error: {ex.Message}"); }
        }

        public static async Task<bool> ConnectAsync(string host, int port, string username, string serverPassword, string channel, string channelPassword, bool validateTls, bool selfMute, bool selfDeaf)
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                if (string.IsNullOrWhiteSpace(host)) { OnError?.Invoke("Mumble host is empty"); return false; }
                if (port <= 0) { OnError?.Invoke("Mumble port must be > 0"); return false; }

                // Save for GUI
                CurrentHost = host; CurrentPort = port; CurrentUsername = username; CurrentChannel = channel;
                CurrentSelfMute = selfMute; CurrentSelfDeaf = selfDeaf;

                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                // Run the console protocol
                _protocol = new MumbleClient.ConsoleMumbleProtocol();
                _conn = new MumbleConnection(host, port, _protocol);
                _conn.Connect(username ?? string.Empty, serverPassword ?? string.Empty, Array.Empty<string>(), host);

                _run = true;
                _pump = new Thread(() =>
                {
                    while (_run && _conn != null && _conn.State != ConnectionStates.Disconnected)
                    {
                        if (!_conn.Process()) Thread.Sleep(1);
                    }
                }) { IsBackground = true, Name = "MumblePump" };
                _pump.Start();

                _isConnected = true;
                OnStatusChanged?.Invoke($"Connected to {host}:{port} as {username}");
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Connect failed: {ex.Message}");
                _isConnected = false; return false;
            }
            finally { _voiceOpLock.Release(); }
        }

        public static async Task DisconnectAsync()
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                _cts?.Cancel();
                _run = false;
                try { _pump?.Join(500); } catch { }

                // Safely close connection: guard state and clear reference before closing to avoid race/NRE
                var conn = _conn;
                _conn = null;
                if (conn != null)
                {
                    try
                    {
                        if (conn.State != ConnectionStates.Disconnected)
                            conn.Close();
                    }
                    catch { }
                }

                _protocol = null; _pump = null;

                _isConnected = false;
                OnStatusChanged?.Invoke("Disconnected");
                await Task.Delay(50);
            }
            finally { _voiceOpLock.Release(); }
        }

        public static string GetStatusSummary() => $"Running: {_isRunning}, Connected: {_isConnected}";
        public static string GetConnectionStatus() => _isConnected ? "Connected" : "Disconnected";

        public static Task<bool> SendTtsToMumbleAsync(string text, string speakerRefId = null, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
