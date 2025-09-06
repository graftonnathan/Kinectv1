using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Mumble
{
    public static class MumbleClientManager
    {
        private static readonly SemaphoreSlim _voiceOpLock = new SemaphoreSlim(1, 1);
        public static SemaphoreSlim VoiceOpLock => _voiceOpLock;

        public static event Action<string> OnStatusChanged;
        public static event Action<float> OnRmsLevel;
        public static event Action<string> OnError;

        private static volatile bool _isRunning = false;
        private static volatile bool _isConnected = false;
        private static CancellationTokenSource _cts;

        private static MumbleSharp.MumbleConnection _conn;
        private static MumbleSimpleClient _proto;

        public static bool IsRunning => _isRunning;
        public static bool IsConnected => _isConnected;

        public static Task<bool> StartAsync()
        {
            _isRunning = true; OnStatusChanged?.Invoke("Initialized");
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

                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                // Build with IPv4 endpoint, then connect (sample pattern)
                var ip4 = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                var ep = new IPEndPoint(ip4, port);

                _proto = new MumbleSimpleClient();
                _proto.OnRms += (speaker, rms) => { try { OnRmsLevel?.Invoke(rms); } catch { } };
                _proto.OnPcm16kFrame += (speaker, frame16k) =>
                {
                    try
                    {
                        if (frame16k == null || frame16k.Length == 0) return;
                        var bytes = new byte[frame16k.Length * 2];
                        Buffer.BlockCopy(frame16k, 0, bytes, 0, bytes.Length);
                        if (VoiceRecognizer.IsReady())
                        {
                            SpeakerIdentifier.SetDiscordSpeakerHint(speaker);
                            VoiceRecognizer.ProcessExternalAudio(bytes, bytes.Length, $"Mumble:{speaker}");
                        }
                    }
                    catch (Exception ex) { OnError?.Invoke($"Ingest error: {ex.Message}"); }
                };

                _conn = new MumbleSharp.MumbleConnection(ep, _proto);

                _conn.Connect(username ?? string.Empty, serverPassword ?? string.Empty, Array.Empty<string>(), host);

                // Start pump loop like the sample
                _ = Task.Run(() =>
                {
                    try
                    {
                        while (_conn != null && _conn.State != MumbleSharp.ConnectionStates.Disconnected)
                        {
                            if (_conn.Process())
                                Thread.Yield();
                            else
                                Thread.Sleep(1);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke($"Pump error: {ex.Message}");
                    }
                }, _cts.Token);

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
                try { _conn?.Close(); } catch { }
                _conn = null;
                _proto = null;
                _isConnected = false;
                OnStatusChanged?.Invoke("Disconnected");
                await Task.Delay(50);
            }
            finally { _voiceOpLock.Release(); }
        }

        public static string GetStatusSummary() => $"Running: {_isRunning}, Connected: {_isConnected}";
        public static string GetConnectionStatus() => _isConnected ? "Connected" : "Disconnected";

        // Explicitly not implementing TTS send here in the minimal refactor
        public static Task<bool> SendTtsToMumbleAsync(string text, string speakerRefId = null, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
