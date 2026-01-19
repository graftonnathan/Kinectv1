using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BearWare;

namespace Kinectv1
{
    public sealed class TeamTalkConfig
    {
        public string Host { get; init; }
        public int TcpPort { get; init; } = 10333;
        public int UdpPort { get; init; } = 10333;
        public bool Encrypted { get; init; }
        public Kinectv1.Settings.TeamTalkTlsValidate TlsValidate { get; init; } = Kinectv1.Settings.TeamTalkTlsValidate.Strict;
        public string Nickname { get; init; }
        public string Username { get; init; }
        public string Password { get; init; }
        public string ChannelPath { get; init; } = "/Naya-Heavy";
        public string Channel { get; init; }
        public string ChannelPassword { get; init; }
    }

    public sealed class TeamTalkVoiceService : IDisposable
    {
        public event Action<int, short[], int, int> OnPcmFrame;
        public event Action<string> OnLog;

        private readonly object _lock = new object();

        private TeamTalk5 _tt;
        private CancellationTokenSource _cts;
        private Task _pumpTask;
        private Task _audioTask;

        private volatile bool _loginIssued;

        private readonly ConcurrentQueue<(int userId, short[] pcm, int sampleRate, int channels)> _audioQueue = new();

        private volatile bool _running;
        private volatile bool _joinAttempted;
        private volatile bool _loggedIn;
        private volatile bool _channelsReady;

        private DateTime _lastFrameUtc = DateTime.MinValue;
        private int _framesThisSecond;
        private DateTime _lastDiagUtc = DateTime.MinValue;
        private DateTime _lastEnableScanUtc = DateTime.MinValue;
        private DateTime _lastJoinAttemptUtc = DateTime.MinValue;

        public DateTime LastFrameUtc => _lastFrameUtc;

        public async Task StartAsync(TeamTalkConfig cfg, CancellationToken ct)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            lock (_lock)
            {
                if (_running) return;
                _running = true;
                _joinAttempted = false;
                _loggedIn = false;
                _channelsReady = false;
                _loginIssued = false;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _tt = new TeamTalk5(poll_based: true);
            }

            Log($"[TeamTalk] Start host={cfg.Host}:{cfg.TcpPort}/{cfg.UdpPort} nick={cfg.Nickname} user={cfg.Username} channelPath={cfg.ChannelPath}");

            _audioTask = Task.Run(() => AudioDispatchLoop(_cts.Token), _cts.Token);
            _pumpTask = Task.Run(() => PumpLoop(cfg, _cts.Token), _cts.Token);

            await Task.CompletedTask;
        }

        private void PumpLoop(TeamTalkConfig cfg, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cfg.Host)) throw new InvalidOperationException("TeamTalk Host missing");

                bool connected = _tt.Connect(cfg.Host, cfg.TcpPort, cfg.UdpPort, 0, 0, bEncrypted: cfg.Encrypted);
                Log(connected ? $"[TeamTalk] Connect() ok -> {cfg.Host}:{cfg.TcpPort}/{cfg.UdpPort}" : $"[TeamTalk] Connect() failed -> {cfg.Host}:{cfg.TcpPort}/{cfg.UdpPort}");

                if (!connected) return;

                while (!ct.IsCancellationRequested)
                {
                    TTMessage msg = new TTMessage();
                    if (_tt.GetMessage(ref msg, 50))
                    {
                        HandleMessage(cfg, msg);
                    }
                    else
                    {
                        TryJoinIfNeeded(cfg);
                    }

                    EmitDiagnostics();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("[TeamTalk] PumpLoop error: " + ex.Message);
            }
        }

        private void HandleMessage(TeamTalkConfig cfg, TTMessage msg)
        {
            try
            {
                switch (msg.nClientEvent)
                {
                    case ClientEvent.CLIENTEVENT_CON_SUCCESS:
                        Log("[TeamTalk] Connected");

                        if (!_loginIssued)
                        {
                            _loginIssued = true;
                            var nick = string.IsNullOrWhiteSpace(cfg.Nickname) ? cfg.Username : cfg.Nickname;
                            var cmdId = _tt.DoLogin(nick ?? "Kinectv1", cfg.Username ?? string.Empty, cfg.Password ?? string.Empty);
                            Log($"[TeamTalk] DoLogin cmdId={cmdId} nick={nick} user={cfg.Username}");
                            if (cmdId < 0)
                            {
                                Log($"[TeamTalk] DoLogin rejected/not queued (cmdId={cmdId}). Check credentials / server auth policy. state userId={_tt.UserID} channelId={_tt.ChannelID}");
                            }
                        }
                        break;

                    case ClientEvent.CLIENTEVENT_CON_FAILED:
                        Log("[TeamTalk] Connection failed");
                        break;

                    case ClientEvent.CLIENTEVENT_CON_LOST:
                        Log("[TeamTalk] Connection lost");
                        break;

                    case ClientEvent.CLIENTEVENT_CMD_SUCCESS:
                        // After login, TeamTalk will start sending channel/user sync. Allow join attempts.
                        if (_tt.UserID > 0) _loggedIn = true;
                        TryJoinIfNeeded(cfg);
                        break;

                    case ClientEvent.CLIENTEVENT_CMD_ERROR:
                        Log("[TeamTalk] Command error src=" + msg.nSource);
                        break;

                    case ClientEvent.CLIENTEVENT_CMD_SERVER_UPDATE:
                    case ClientEvent.CLIENTEVENT_CMD_CHANNEL_UPDATE:
                    case ClientEvent.CLIENTEVENT_CMD_CHANNEL_NEW:
                        // Channels are being synchronized; allow join attempts by path once present.
                        _channelsReady = true;
                        break;

                    case ClientEvent.CLIENTEVENT_USER_AUDIOBLOCK:
                        HandleUserAudioBlock(msg);
                        break;
                }

                // Periodically ensure audio blocks are enabled for active users
                var now = DateTime.UtcNow;
                if ((now - _lastEnableScanUtc).TotalSeconds >= 1)
                {
                    _lastEnableScanUtc = now;
                    try
                    {
                        if (_tt.GetServerUsers(out var users) && users != null)
                        {
                            foreach (var u in users)
                            {
                                if (u.nUserID <= 0) continue;
                                try { _tt.EnableAudioBlockEvent(u.nUserID, StreamType.STREAMTYPE_VOICE, bEnable: true); } catch { }
                            }
                        }
                    }
                    catch { }
                }

                TryJoinIfNeeded(cfg);
            }
            catch (Exception ex)
            {
                Log("[TeamTalk] HandleMessage error: " + ex.Message);
            }
        }

        private void TryJoinIfNeeded(TeamTalkConfig cfg)
        {
            try
            {
                if (_joinAttempted && _tt.ChannelID > 0) return;

                // Only attempt join after login completed
                if (_tt.UserID <= 0 || !_loggedIn) return;

                if (_tt.ChannelID > 0)
                {
                    _joinAttempted = true;
                    return;
                }

                // Rate-limit join attempts (independent from other timers)
                var now = DateTime.UtcNow;
                if ((now - _lastJoinAttemptUtc).TotalMilliseconds < 1500) return;
                _lastJoinAttemptUtc = now;

                // If channel tree isn't synced yet, wait a bit.
                if (!_channelsReady) return;

                _joinAttempted = true;
                if (!TryJoinByPathOrName(cfg))
                {
                    // If not found yet, allow another attempt later.
                    _joinAttempted = false;
                }
            }
            catch { }
        }

        private bool TryJoinByPathOrName(TeamTalkConfig cfg)
        {
            try
            {
                var channelPath = cfg?.ChannelPath;
                var channelName = cfg?.Channel;
                var chanPwd = cfg?.ChannelPassword ?? string.Empty;

                if (string.IsNullOrWhiteSpace(channelPath) && string.IsNullOrWhiteSpace(channelName))
                    channelPath = "/";

                int chanId = 0;
                if (!string.IsNullOrWhiteSpace(channelPath))
                {
                    string path = channelPath;
                    if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;

                    try { chanId = _tt.GetChannelIDFromPath(path); } catch { chanId = 0; }
                    if (chanId > 0)
                    {
                        var cmd = _tt.DoJoinChannelByID(chanId, chanPwd);
                        Log($"[TeamTalk] DoJoinChannelByID cmdId={cmd} chanId={chanId} path={path}");
                        return true;
                    }
                }

                // Fallback: join by channel name if provided (server may not expose full paths)
                if (!string.IsNullOrWhiteSpace(channelName))
                {
                    try
                    {
                        if (_tt.GetServerChannels(out var chans) && chans != null)
                        {
                            foreach (var c in chans)
                            {
                                if (c.nChannelID <= 0) continue;
                                if (string.Equals(c.szName, channelName, StringComparison.OrdinalIgnoreCase))
                                {
                                    var cmd = _tt.DoJoinChannelByID(c.nChannelID, chanPwd);
                                    Log($"[TeamTalk] DoJoinChannelByID cmdId={cmd} chanId={c.nChannelID} name={channelName}");
                                    return true;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Nothing resolved yet.
                return false;
            }
            catch (Exception ex)
            {
                Log("[TeamTalk] Join error: " + ex.Message);
                return false;
            }
        }

        private void HandleUserAudioBlock(TTMessage msg)
        {
            try
            {
                int userId = msg.nSource;
                var ab = _tt.AcquireUserAudioBlock(StreamType.STREAMTYPE_VOICE, userId);
                try
                {
                    if (ab.lpRawAudio == IntPtr.Zero || ab.nSamples <= 0) return;

                    int samples = ab.nSamples;
                    int channels = ab.nChannels;
                    int sampleRate = ab.nSampleRate;

                    var pcm = new short[samples * Math.Max(1, channels)];
                    System.Runtime.InteropServices.Marshal.Copy(ab.lpRawAudio, pcm, 0, pcm.Length);

                    _lastFrameUtc = DateTime.UtcNow;
                    _framesThisSecond++;

                    _audioQueue.Enqueue((userId, pcm, sampleRate, channels));
                }
                finally
                {
                    _tt.ReleaseUserAudioBlock(ab);
                }
            }
            catch (Exception ex)
            {
                Log("[TeamTalk] AudioBlock error: " + ex.Message);
            }
        }

        private void AudioDispatchLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (_audioQueue.TryDequeue(out var item))
                    {
                        try { OnPcmFrame?.Invoke(item.userId, item.pcm, item.sampleRate, item.channels); } catch { }
                        continue;
                    }
                    Thread.Sleep(1);
                }
            }
            catch (OperationCanceledException) { }
        }

        private void EmitDiagnostics()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastDiagUtc).TotalSeconds < 1) return;
            _lastDiagUtc = now;

            var fps = _framesThisSecond;
            _framesThisSecond = 0;

            Log($"[TeamTalk][diag] lastFrame={(LastFrameUtc == DateTime.MinValue ? "never" : (now - LastFrameUtc).TotalMilliseconds.ToString("F0") + "ms ago")} fps={fps} q={_audioQueue.Count}");
        }

        private void Log(string s)
        {
            try { OnLog?.Invoke(s); } catch { }
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource cts;
            Task pump;
            Task audio;
            TeamTalk5 tt;

            lock (_lock)
            {
                if (!_running) return;
                _running = false;

                cts = _cts;
                pump = _pumpTask;
                audio = _audioTask;
                tt = _tt;

                _cts = null;
                _pumpTask = null;
                _audioTask = null;
                _tt = null;
            }

            try { cts?.Cancel(); } catch { }

            try { if (pump != null) await Task.WhenAny(pump, Task.Delay(1500)); } catch { }
            try { if (audio != null) await Task.WhenAny(audio, Task.Delay(1500)); } catch { }

            try { tt?.Disconnect(); } catch { }
            try { tt?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }

            Log("[TeamTalk] Stopped");
        }
    }
}
