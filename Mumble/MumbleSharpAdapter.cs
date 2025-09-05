using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using MumbleSharp;
using MumbleSharp.Audio;
using MumbleSharp.Audio.Codecs;
using MumbleSharp.Model;
using MumbleProto;
using System.Net;
using System.Collections.Generic;
using System.IO;

namespace Kinectv1.Mumble
{
    /// <summary>
    /// Thin adapter around MumbleSharp that exposes a neutral client surface.
    /// </summary>
    internal sealed class MumbleSharpAdapter : IMumbleProtocol, IDisposable
    {
        public event Action<string> Connected;
        public event Action<string> Disconnected;
        public event Action<float[], int, int, string> AudioFrameReceived; // float[] pcm, 48k, channels, user
        public event Action<string> Status;
        public event Action<string> Error;

        public MumbleConnection Connection { get; private set; }
        public User LocalUser { get; private set; }
        public Channel RootChannel { get; private set; }
        public System.Collections.Generic.IEnumerable<Channel> Channels { get; private set; } = System.Linq.Enumerable.Empty<Channel>();
        public System.Collections.Generic.IEnumerable<User> Users { get; private set; } = System.Linq.Enumerable.Empty<User>();
        public bool ReceivedServerSync { get; private set; }
        public SpeechCodecs TransmissionCodec { get; private set; }

        private MumbleConnection _conn;
        private IVoiceCodec _codec;
        private volatile bool _started;

        private Kinectv1.Settings.MumbleTlsValidate _tlsMode = Kinectv1.Settings.MumbleTlsValidate.Strict;
        private string _connectShapeUsed = "";
        private volatile bool _handshakeRejected;
        private string _handshakeReason;
        private bool _validateTlsFlag = true;
        private Task _pumpTask;
        private bool _authInvoked;
        private DateTime _lastDecodeLogUtc;
        private bool _codecWarned;
        private bool _codecAnnounced;
        private uint? _localSession;
        private bool? _desiredSelfMute;
        private bool? _desiredSelfDeaf;

        // Channel caches
        private readonly Dictionary<uint, string> _channelNames = new Dictionary<uint, string>();
        private readonly Dictionary<uint, uint?> _channelParents = new Dictionary<uint, uint?>();
        private readonly Dictionary<uint, Tuple<string, uint?>> _chan = new Dictionary<uint, Tuple<string, uint?>>();
        private volatile string _pendingJoinPath;
        private uint? _currentChannelId;
        private uint? _lastJoinTargetId;
        private string _lastJoinTargetPath;

        public void SetTlsMode(Kinectv1.Settings.MumbleTlsValidate mode) => _tlsMode = mode;

        public async Task<bool> ConnectAsync(string host, int port, string username, string password, bool validateTls, CancellationToken ct)
        {
            try
            {
                if (_started) return true;
                _started = true;
                _handshakeRejected = false;
                _handshakeReason = null;
                _validateTlsFlag = validateTls;
                _authInvoked = false;
                _lastDecodeLogUtc = DateTime.MinValue;
                _pendingJoinPath = null;

                if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is empty");
                if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port));
                host = host.Trim();

                // Honor validateTls flag directly for certificate behavior
                _tlsMode = validateTls ? Kinectv1.Settings.MumbleTlsValidate.Strict : Kinectv1.Settings.MumbleTlsValidate.Off;

                // Guard: DNS resolve to fail fast on bad host
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                    if (addrs == null || addrs.Length == 0)
                        throw new Exception("DNS resolve returned no addresses");
                }
                catch (Exception ex)
                {
                    Error?.Invoke($"[Mumble] DNS failure for {host}:{port} -> {ex.Message}");
                    throw new InvalidOperationException($"DNS resolve failed for '{host}': {ex.Message}");
                }

                var serverNameHostOnly = host; // SNI target
                Status?.Invoke($"Protocol initialised: {typeof(MumbleConnection).Assembly.GetName().Name} {typeof(MumbleConnection).Assembly.GetName().Version}");
                LogPublicSurface();
                Status?.Invoke($"Connecting to {host}:{port} as {username} (TLS {(validateTls ? "on" : "off")})");

                var connType = typeof(MumbleConnection);
                _conn = (MumbleConnection)CreateConnectionInstance(connType, this, host, port, validateTls) ?? (MumbleConnection)CreateConnectionInstanceFallback(connType);
                if (_conn == null) throw new InvalidOperationException("Failed to construct MumbleConnection");

                // Try to provide tokens (e.g., channel password) if available from settings
                string[] tokens = Array.Empty<string>();
                try
                {
                    var token = Kinectv1.App.SettingsProvider?.Current?.Mumble?.ChannelPassword;
                    if (!string.IsNullOrWhiteSpace(token)) tokens = new[] { token };
                }
                catch { }
                
                Exception lastInvokeEx;
                if (!InvokeConnectOrdered(connType, _conn, host, port, validateTls, serverNameHostOnly, username, password, tokens, out lastInvokeEx))
                {
                    if (lastInvokeEx != null) throw new InvalidOperationException(lastInvokeEx.Message);
                    throw new InvalidOperationException("No suitable Connect overload on MumbleConnection");
                }

                Status?.Invoke($"Connect shape: {_connectShapeUsed}");

                // Start network pump immediately after Connect
                StartPumpLoop(_conn, ct);

                // Optional auth: only if a public Authenticate(string,string) exists; call once
                TryAuthenticateIfAvailable(_conn, username ?? string.Empty, password ?? string.Empty);

                // Wait for ServerSync before reporting connected; also watch for server reject
                var t0 = DateTime.UtcNow;
                var lastLog = t0;
                var timeoutSeconds = 30; // extended handshake wait
                while (!ct.IsCancellationRequested && (DateTime.UtcNow - t0).TotalSeconds < timeoutSeconds && !ReceivedServerSync && !_handshakeRejected)
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastLog).TotalSeconds >= 1)
                    {
                        Status?.Invoke("waiting for ServerSync…");
                        lastLog = now;
                    }
                    try { await Task.Delay(50, ct).ConfigureAwait(false); } catch { }
                    if (_handshakeRejected) break;
                }

                if (_handshakeRejected)
                {
                    try { TryDisconnect(_conn); } catch { }
                    _started = false;
                    Error?.Invoke($"Handshake rejected: {_handshakeReason ?? "unknown"}");
                    return false;
                }

                if (!ReceivedServerSync)
                {
                    try { TryDisconnect(_conn); } catch { }
                    _started = false;
                    Error?.Invoke("Connected TCP but no ServerSync");
                    return false;
                }

                Connected?.Invoke($"Connected to {host}:{port} as {username}");
                return true;
            }
            catch (OperationCanceledException)
            {
                _started = false;
                return false;
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Connect error: {ex.Message}");
                _started = false;
                return false;
            }
        }

        private void TryAuthenticateIfAvailable(object conn, string username, string password)
        {
            if (_authInvoked) return;
            try
            {
                var t = conn.GetType();
                var auth = t.GetMethod("Authenticate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(string) }, null);
                if (auth != null)
                {
                    auth.Invoke(conn, new object[] { username, password });
                    _authInvoked = true;
                    Status?.Invoke("Authenticate(username, password) called");
                }
            }
            catch { }
        }

        private void StartPumpLoop(object conn, CancellationToken ct)
        {
            try
            {
                var t = conn.GetType();
                // Prefer Process()
                var process = t.GetMethod("Process", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (process != null)
                {
                    Status?.Invoke("Pump: Process() loop started");
                    _pumpTask = Task.Run(async () =>
                    {
                        while (!ct.IsCancellationRequested && _started)
                        {
                            try { process.Invoke(conn, null); }
                            catch (Exception ex) { Error?.Invoke($"Pump(Process) error: {ex.InnerException?.Message ?? ex.Message}"); break; }
                            try { await Task.Delay(15, ct).ConfigureAwait(false); } catch { }
                        }
                        Status?.Invoke("Pump: Process() loop stopped");
                    });
                    return;
                }

                // Next, Run(CancellationToken) or Run()
                var runCt = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(CancellationToken) }, null);
                if (runCt != null)
                {
                    Status?.Invoke("Pump: Run(CancellationToken) started");
                    _pumpTask = Task.Run(() =>
                    {
                        try { runCt.Invoke(conn, new object[] { ct }); }
                        catch (Exception ex) { if (!ct.IsCancellationRequested) Error?.Invoke($"Pump(Run, ct) error: {ex.InnerException?.Message ?? ex.Message}"); }
                    });
                    return;
                }
                var run0 = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (run0 != null)
                {
                    Status?.Invoke("Pump: Run() started");
                    _pumpTask = Task.Run(() =>
                    {
                        try { run0.Invoke(conn, null); }
                        catch (Exception ex) { if (!ct.IsCancellationRequested) Error?.Invoke($"Pump(Run) error: {ex.InnerException?.Message ?? ex.Message}"); }
                    });
                    return;
                }

                // Finally, Start()
                var start0 = t.GetMethod("Start", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (start0 != null)
                {
                    Status?.Invoke("Pump: Start() invoked");
                    try { start0.Invoke(conn, null); } catch (Exception ex) { Error?.Invoke($"Pump(Start) error: {ex.InnerException?.Message ?? ex.Message}"); }
                    return;
                }

                Status?.Invoke("Pump: no Process/Run/Start method found");
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Pump setup error: {ex.Message}");
            }
        }

        private void LogPublicSurface()
        {
            try
            {
                var t = typeof(MumbleConnection);
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Connect" || m.Name == "Authenticate" || m.Name == "Login")
                    .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                foreach (var s in methods) Status?.Invoke($"Surface: {s}");
            }
            catch { }
        }

        // Replace reflection-based string join with cache + UserState move
        public void JoinChannelPath(string channelPath)
        {
            try
            {
                if (_conn == null) return;

                // Normalize: strip leading separators, use case-insensitive segments
                var normalized = (channelPath ?? string.Empty).Trim().TrimStart('/', '\\');
                _pendingJoinPath = normalized;

                if (_chan.Count == 0 && _channelNames.Count == 0)
                {
                    Status?.Invoke("Join deferred until channels known");
                    return;
                }

                TryJoinPending();
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Join channel error: {ex.Message}");
            }
        }

        private void TryJoinPendingIfPossible()
        {
            try
            {
                TryJoinPending();
            }
            catch { }
        }

        private void TryJoinPending()
        {
            try
            {
                var path = _pendingJoinPath;
                if (_conn == null || string.IsNullOrWhiteSpace(path)) return;

                // Build tree from cached parents/names
                if (_channelNames.Count == 0) return;

                // Determine real root: prefer name "Root", then no-parent/parent==0
                uint? rootId = _channelNames.FirstOrDefault(kv => string.Equals(kv.Value?.Trim(), "Root", StringComparison.OrdinalIgnoreCase)).Key;
                if (!rootId.HasValue)
                {
                    rootId = _channelNames.Keys.FirstOrDefault(id => !_channelParents.TryGetValue(id, out var p) || !p.HasValue || p.Value == 0);
                }
                if (!rootId.HasValue) return;

                // One-time dump (gated by timestamp reuse) for visibility
                if (_channelNames.Count > 0 && (DateTime.UtcNow - _lastDecodeLogUtc).TotalSeconds > 2)
                {
                    var map = string.Join("; ", _channelNames.Select(kv => $"{kv.Key}:{kv.Value}(parent={( _channelParents.TryGetValue(kv.Key, out var pp) && pp.HasValue ? pp.Value : 0)})"));
                    Status?.Invoke($"Channels: {map}");
                    _lastDecodeLogUtc = DateTime.UtcNow;
                }

                var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                uint current = rootId.Value;
                foreach (var seg in parts)
                {
                    // Find direct child of current with matching name
                    var children = _channelParents.Where(kv => kv.Value.HasValue && kv.Value.Value == current).Select(kv => kv.Key);
                    uint? match = null;
                    foreach (var cid in children)
                    {
                        if (_channelNames.TryGetValue(cid, out var nm) && string.Equals(nm?.Trim(), seg, StringComparison.OrdinalIgnoreCase))
                        { match = cid; break; }
                    }
                    if (!match.HasValue)
                    {
                        // Log siblings
                        try
                        {
                            var sibs = string.Join(", ", children.Select(id => _channelNames.TryGetValue(id, out var n) ? n : id.ToString()));
                            Error?.Invoke($"Channel segment not found: '{seg}' under '{_channelNames[current]}'. Siblings: [{sibs}]");
                        }
                        catch { }
                        return;
                    }
                    current = match.Value;
                }

                var targetName = _channelNames.TryGetValue(current, out var nm1) ? nm1 : current.ToString();
                Status?.Invoke($"Join target resolved: {targetName} ({current})");
                _lastJoinTargetId = current;
                _lastJoinTargetPath = path;

                // Found target id -> send UserState move
                if (SendUserStateMove(current))
                {
                    _pendingJoinPath = null;
                }
            }
            catch { }
        }

        private bool SendUserStateMove(uint targetChannelId)
        {
            try
            {
                // Resolve our session id (optional for some servers)
                uint session = 0;
                if (_localSession.HasValue) session = _localSession.Value;
                else
                {
                    try
                    {
                        var lu = _conn.GetType().GetProperty("LocalUser", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_conn);
                        var sObj = lu?.GetType().GetProperty("Session", BindingFlags.Public | BindingFlags.Instance)?.GetValue(lu);
                        if (sObj is uint u) session = u; else if (sObj is int i) session = unchecked((uint)i); else if (sObj is long l) session = unchecked((uint)l);
                    }
                    catch { }
                }

                // Build UserState with specified flags
                var us = new MumbleProto.UserState();
                TrySetProto(us, "ChannelId", targetChannelId);
                TrySetProto(us, "ChannelIdSpecified", true);
                if (session != 0)
                {
                    TrySetProto(us, "Session", session);
                    TrySetProto(us, "SessionSpecified", true);
                }

                // Try direct send first
                bool sent = TryGenericSendOnTarget(_conn, us) || TrySendViaNestedSenders(us);
                Status?.Invoke($"Join requested via UserState session={session} ? {targetChannelId}");

                // Confirm move up to 3s
                if (WaitForChannel(targetChannelId, 3000))
                {
                    var name = _channelNames.TryGetValue(targetChannelId, out var nm) ? nm : targetChannelId.ToString();
                    Status?.Invoke($"Joined: {name}");
                    return true;
                }

                // As last resort, include Actor for servers that require it, then resend
                if (session != 0)
                {
                    TrySetProto(us, "Actor", session);
                    TrySetProto(us, "ActorSpecified", true);
                    // Some servers also look for StateSpecified flags; set them if present
                    TrySetProto(us, "StateSpecified", true);
                    var resent = TryGenericSendOnTarget(_conn, us) || TrySendViaNestedSenders(us);
                    if (resent) Status?.Invoke("Join re-sent including Actor/StateSpecified");
                    if (WaitForChannel(targetChannelId, 1500))
                    {
                        var name = _channelNames.TryGetValue(targetChannelId, out var nm2) ? nm2 : targetChannelId.ToString();
                        Status?.Invoke($"Joined: {name}");
                        return true;
                    }
                }

                // Model-level fallback: try Channel.Enter()/Join() or LocalUser.Move(channel)
                if (!string.IsNullOrWhiteSpace(_lastJoinTargetPath) && TryJoinUsingModel(_lastJoinTargetPath))
                {
                    return true;
                }

                return sent; // we attempted the move; handlers will surface ACL denials if any
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Failed to send UserState move: {ex.Message}");
                return false;
            }
        }

        private static void TrySetProto(object msg, string prop, object value)
        {
            try
            {
                var t = msg.GetType();

                object Coerce(object val, Type targetType)
                {
                    if (val == null) return null;
                    var nt = Nullable.GetUnderlyingType(targetType) ?? targetType;
                    try
                    {
                        if (nt.IsEnum)
                        {
                            if (val is string s) return Enum.Parse(nt, s, true);
                            return Enum.ToObject(nt, Convert.ChangeType(val, Enum.GetUnderlyingType(nt)));
                        }
                        if (nt == typeof(bool)) return Convert.ToBoolean(val);
                        if (nt == typeof(byte)) return Convert.ToByte(val);
                        if (nt == typeof(sbyte)) return Convert.ToSByte(val);
                        if (nt == typeof(short)) return Convert.ToInt16(val);
                        if (nt == typeof(ushort)) return Convert.ToUInt16(val);
                        if (nt == typeof(int)) return Convert.ToInt32(val);
                        if (nt == typeof(uint)) return Convert.ToUInt32(val);
                        if (nt == typeof(long)) return Convert.ToInt64(val);
                        if (nt == typeof(ulong)) return Convert.ToUInt64(val);
                        if (nt == typeof(float)) return Convert.ToSingle(val);
                        if (nt == typeof(double)) return Convert.ToDouble(val);
                        if (nt == typeof(string)) return Convert.ToString(val);
                        return Convert.ChangeType(val, nt);
                    }
                    catch { return val; }
                }

                bool TrySetProperty(string name)
                {
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.CanWrite)
                    {
                        var coerced = Coerce(value, p.PropertyType);
                        try { p.SetValue(msg, coerced); return true; } catch { }
                    }
                    return false;
                }
                bool TrySetField(string name)
                {
                    var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null)
                    {
                        var coerced = Coerce(value, f.FieldType);
                        try { f.SetValue(msg, coerced); return true; } catch { }
                    }
                    return false;
                }

                // Direct property/field
                if (TrySetProperty(prop) || TrySetField(prop)) return;

                // Case-insensitive property
                var pInsensitive = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                     .FirstOrDefault(pi => string.Equals(pi.Name, prop, StringComparison.OrdinalIgnoreCase) && pi.CanWrite);
                if (pInsensitive != null)
                {
                    var coerced = Coerce(value, pInsensitive.PropertyType);
                    try { pInsensitive.SetValue(msg, coerced); return; } catch { }
                }

                // If setting *Specified, also try HasX variants
                if (prop.EndsWith("Specified", StringComparison.OrdinalIgnoreCase) && value is bool b)
                {
                    var baseName = prop.Substring(0, prop.Length - "Specified".Length);
                    if (TrySetProperty("Has" + baseName) || TrySetField("Has" + baseName)) return;

                    // underscore variants
                    string snake = string.Concat(baseName.Select((ch, i) => char.IsUpper(ch) ? ("_" + char.ToLowerInvariant(ch)) : ch.ToString())).TrimStart('_');
                    foreach (var name in new[] { baseName + "Specified", snake + "Specified", "Has" + baseName, "has" + baseName, "has_" + snake })
                    {
                        if (TrySetProperty(name) || TrySetField(name)) return;
                    }
                }

                // snake_case fallback
                string snakeProp = string.Concat(prop.Select((ch, i) => char.IsUpper(ch) ? ("_" + char.ToLowerInvariant(ch)) : ch.ToString())).TrimStart('_');
                if (TrySetProperty(snakeProp) || TrySetField(snakeProp)) return;
            }
            catch { }
        }

        private bool WaitForChannel(uint expect, int ms)
        {
            var t0 = DateTime.UtcNow;
            var lastLog = DateTime.MinValue;
            while ((DateTime.UtcNow - t0).TotalMilliseconds < ms)
            {
                var cur = GetLocalUserChannelId();
                if (cur.HasValue && cur.Value == expect) return true;
                if ((DateTime.UtcNow - lastLog).TotalMilliseconds >= 250)
                {
                    string curName = null;
                    if (cur.HasValue) _channelNames.TryGetValue(cur.Value, out curName);
                    Status?.Invoke($"Waiting move: current={(cur.HasValue ? (cur.Value+":"+ (curName ?? "?")) : "?")}, target={expect}");
                    lastLog = DateTime.UtcNow;
                }
                Thread.Sleep(50);
            }
            return false;
        }

        private bool TryGenericSendOnTarget(object target, object message)
        {
            try
            {
                var t = target?.GetType(); if (t == null) return false;
                // Skip raw socket here; handled by raw fallback
                if (t.Name != null && t.Name.IndexOf("TcpSocket", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;

                var msgType = message.GetType();
                // Generic Send<T>/SendMessage<T> — close first, then inspect params
                var cands = t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)
                             .Where(mi => (mi.Name == "Send" || mi.Name == "SendMessage")
                                          && mi.IsGenericMethodDefinition
                                          && mi.GetGenericArguments().Length == 1);

                foreach (var mi in cands)
                {
                    MethodInfo gm;
                    try { gm = mi.MakeGenericMethod(msgType); }
                    catch { continue; }
                    var ps = gm.GetParameters();
                    if (ps.Length == 0) continue;

                    var args = new object[ps.Length];
                    args[0] = message;
                    for (int i = 1; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        if (pt == typeof(bool)) args[i] = true;                                   // reliable/flush
                        else if (pt.FullName == "System.Threading.CancellationToken") args[i] = default(System.Threading.CancellationToken);
                        else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;      // sensible default
                    }

                    gm.Invoke(target, args);
                    Status?.Invoke($"Sent UserState via {t.Name}.{mi.Name}<UserState>");
                    return true;
                }

                // Non-generic fallback (rare)
                var nonGen = t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)
                              .Where(mi => (mi.Name == "Send" || mi.Name == "SendMessage") && !mi.IsGenericMethod);
                foreach (var mi in nonGen)
                {
                    var ps = mi.GetParameters();
                    if (ps.Length == 0) continue;
                    // Skip PacketType-first overloads
                    if (ps[0].ParameterType.FullName == "MumbleSharp.Packets.PacketType") continue;
                    if (!ps[0].ParameterType.IsAssignableFrom(msgType)) continue;
                    var args = new object[ps.Length];
                    args[0] = message;
                    for (int i = 1; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        if (pt == typeof(bool)) args[i] = true;
                        else if (pt.FullName == "System.Threading.CancellationToken") args[i] = default(System.Threading.CancellationToken);
                        else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                    }
                    mi.Invoke(target, args);
                    Status?.Invoke($"Sent UserState via {t.Name}.{mi.Name}(UserState)");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Send<T> invoke error on {target?.GetType().Name}: {ex.Message}");
            }
            return false;
        }

        private bool TrySendViaNestedSenders(object us)
        {
            var ct = _conn.GetType();
            // Preferred property/field order
            foreach (var name in new[] { "Messages", "MessageSender", "Sender", "MessageWriter", "Client" })
            {
                try
                {
                    var p = ct.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var v = p?.GetValue(_conn);
                    if (v != null && TryGenericSendOnTarget(v, us)) return true;

                    var f = ct.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    v = f?.GetValue(_conn);
                    if (v != null && TryGenericSendOnTarget(v, us)) return true;
                }
                catch { }
            }
            // Raw fallback on tcp socket only if all above failed
            try
            {
                object tcp = null;
                foreach (var name in new[] { "_tcp", "Tcp", "TcpSocket" })
                {
                    var p = ct.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    tcp = p?.GetValue(_conn);
                    if (tcp != null) break;
                    var f = ct.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    tcp = f?.GetValue(_conn);
                    if (tcp != null) break;
                }
                if (tcp != null) return SendRawUserStateViaTcp(us, tcp);
            }
            catch { }
            return false;
        }

        private bool SendRawUserStateViaTcp(object userState, object tcpSocket)
        {
            try
            {
                // Resolve PacketType.UserState
                var enumType = Type.GetType("MumbleSharp.Packets.PacketType, MumbleSharp", throwOnError: false);
                if (enumType == null) { Error?.Invoke("PacketType enum not found"); return false; }
                object packet = null;
                try { packet = Enum.Parse(enumType, "UserState"); } catch { }
                if (packet == null) { Error?.Invoke("PacketType.UserState not found"); return false; }

                // Serialize
                byte[] bytes = null;
                try
                {
                    var serType = Type.GetType("ProtoBuf.Serializer, protobuf-net", throwOnError: false);
                    if (serType != null)
                    {
                        var methods = serType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .Where(m => m.Name == "Serialize" && m.GetParameters().Length == 2 && typeof(Stream).IsAssignableFrom(m.GetParameters()[0].ParameterType) && m.IsGenericMethodDefinition)
                            .ToArray();
                        var serializeGeneric = methods.FirstOrDefault();
                        if (serializeGeneric != null)
                        {
                            var gm = serializeGeneric.MakeGenericMethod(userState.GetType());
                            using (var ms = new MemoryStream())
                            {
                                gm.Invoke(null, new object[] { ms, userState });
                                bytes = ms.ToArray();
                            }
                        }
                    }
                }
                catch { }
                if (bytes == null)
                {
                    try
                    {
                        var toBytes = userState.GetType().GetMethod("ToByteArray", BindingFlags.Public | BindingFlags.Instance);
                        if (toBytes != null) bytes = (byte[])toBytes.Invoke(userState, null);
                    }
                    catch { }
                }
                if (bytes == null || bytes.Length == 0) { Error?.Invoke("UserState serialization failed"); return false; }

                // Send via TcpSocket.Send
                var t = tcpSocket.GetType();
                var segment = new ArraySegment<byte>(bytes, 0, bytes.Length);
                var sendMethods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name == "Send")
                    .ToArray();
                bool called = false;
                foreach (var m in sendMethods)
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == enumType && ps[1].ParameterType == typeof(ArraySegment<byte>) && ps[2].ParameterType == typeof(bool))
                    { m.Invoke(tcpSocket, new object[] { packet, segment, true }); called = true; break; }
                    if (ps.Length == 2 && ps[0].ParameterType == enumType && ps[1].ParameterType == typeof(ArraySegment<byte>))
                    { m.Invoke(tcpSocket, new object[] { packet, segment }); called = true; break; }
                    if (ps.Length == 3 && ps[0].ParameterType == enumType && ps[1].ParameterType == typeof(byte[]) && ps[2].ParameterType == typeof(bool))
                    { m.Invoke(tcpSocket, new object[] { packet, bytes, true }); called = true; break; }
                    if (ps.Length == 2 && ps[0].ParameterType == enumType && ps[1].ParameterType == typeof(byte[]))
                    { m.Invoke(tcpSocket, new object[] { packet, bytes }); called = true; break; }
                }
                if (!called) { Error?.Invoke("TcpSocket.Send suitable overload not found"); return false; }
                Status?.Invoke("Sent raw UserState via TcpSocket");
                return true;
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Raw send failed: {ex.Message}");
                return false;
            }
        }

        private bool _loggedChannelIdShape;
        private uint? GetLocalUserChannelId()
        {
            try
            {
                // Prefer tracked value
                if (_currentChannelId.HasValue && _currentChannelId.Value != 0) return _currentChannelId.Value;

                var lu = _conn.GetType().GetProperty("LocalUser", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_conn);
                if (lu == null) return null;
                var ch = lu.GetType().GetProperty("Channel", BindingFlags.Public | BindingFlags.Instance)?.GetValue(lu);
                uint? id = null;
                if (ch != null)
                {
                    var idObj = ch.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ch)
                              ?? ch.GetType().GetProperty("ChannelId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ch);
                    if (idObj is uint u) id = u; else if (idObj is int i) id = unchecked((uint)i); else if (idObj is long l) id = unchecked((uint)l);
                    if (!_loggedChannelIdShape)
                    {
                        Status?.Invoke($"LocalUser.Channel id shape: {(id.HasValue ? id.Value.ToString() : "null")}");
                        _loggedChannelIdShape = true;
                    }
                }
                if (!id.HasValue)
                {
                    var luIdObj = lu.GetType().GetProperty("ChannelId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(lu)
                               ?? lu.GetType().GetProperty("ChannelIndex", BindingFlags.Public | BindingFlags.Instance)?.GetValue(lu);
                    if (luIdObj is uint u) id = u; else if (luIdObj is int i) id = unchecked((uint)i); else if (luIdObj is long l) id = unchecked((uint)l);
                    if (!_loggedChannelIdShape)
                    {
                        Status?.Invoke($"LocalUser direct ChannelId shape: {(id.HasValue ? id.Value.ToString() : "null")}");
                        _loggedChannelIdShape = true;
                    }
                }
                return id;
            }
            catch { return null; }
        }

        public Task DisconnectAsync()
        {
            try
            {
                TryDisconnect(_conn);
                Disconnected?.Invoke("Disconnected");
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Disconnect error: {ex.Message}");
            }
            finally
            {
                _started = false;
            }
            return Task.CompletedTask;
        }

        private static void TryDisconnect(object conn)
        {
            if (conn == null) return;
            var t = conn.GetType();
            var m = t.GetMethod("Disconnect", BindingFlags.Public | BindingFlags.Instance)
                 ?? t.GetMethod("Close", BindingFlags.Public | BindingFlags.Instance);
            m?.Invoke(conn, null);
        }

        private static object CreateConnectionInstance(Type connType, IMumbleProtocol protocol, string host, int port, bool validateTls)
        {
            try
            {
                var ctors = connType.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (var ci in ctors.OrderByDescending(c => c.GetParameters().Any(p => p.ParameterType.IsAssignableFrom(typeof(IMumbleProtocol)))))
                {
                    var ps = ci.GetParameters();
                    var args = new object[ps.Length];
                    bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        if (pt.IsAssignableFrom(typeof(IMumbleProtocol)) || typeof(IMumbleProtocol).IsAssignableFrom(pt)) args[i] = protocol;
                        else if (pt == typeof(string)) args[i] = host;
                        else if (pt == typeof(int)) args[i] = port;
                        else if (pt == typeof(ushort)) args[i] = (ushort)port;
                        else if (pt == typeof(bool)) args[i] = validateTls;
                        else args[i] = GetDefault(pt);
                        if (pt.IsValueType && args[i] == null) { ok = false; break; }
                    }
                    if (!ok) continue;
                    try { return ci.Invoke(args); } catch { }
                }
            }
            catch { }
            return null;
        }

        private static object CreateConnectionInstanceFallback(Type connType)
        {
            try
            {
                var ctors = connType.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                Array.Sort(ctors, (a, b) => a.GetParameters().Length.CompareTo(b.GetParameters().Length));
                foreach (var ci in ctors)
                {
                    var ps = ci.GetParameters();
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++) args[i] = GetDefault(ps[i].ParameterType);
                    try { return ci.Invoke(args); } catch { }
                }
            }
            catch { }
            return null;
        }

        private static object GetDefault(Type t)
        {
            if (!t.IsValueType) return null;
            try { return Activator.CreateInstance(t); } catch { return null; }
        }

        private bool InvokeConnectOrdered(Type connType, object conn, string host, int port, bool validateTls, string serverNameHostOnly, string username, string password, string[] tokens, out Exception lastInvokeException)
        {
            lastInvokeException = null;
            var methods = connType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name.Equals("Connect", StringComparison.OrdinalIgnoreCase)).ToArray();

            // Prefer: Connect(string host, int port, bool validateTls)
            var m = methods.FirstOrDefault(x => HasParams(x, typeof(string), typeof(int), typeof(bool)) || HasParams(x, typeof(string), typeof(ushort), typeof(bool)));
            if (m != null)
            {
                try { Status?.Invoke("Invoking Connect shape: Connect(string,int,bool)"); m.Invoke(conn, new object[] { host, port, validateTls }); _connectShapeUsed = "Connect(string,int,bool)"; return true; }
                catch (Exception ex) { lastInvokeException = ex.InnerException ?? ex; }
            }

            // Fallback: Connect(string host, int port)
            m = methods.FirstOrDefault(x => HasParams(x, typeof(string), typeof(int)) || HasParams(x, typeof(string), typeof(ushort)));
            if (m != null)
            {
                try { Status?.Invoke("Invoking Connect shape: Connect(string,int)"); m.Invoke(conn, new object[] { host, port }); _connectShapeUsed = "Connect(string,int)"; return true; }
                catch (Exception ex) { lastInvokeException = ex.InnerException ?? ex; }
            }

            // Fallback: Connect(string serverName)  // pass host-only
            m = methods.FirstOrDefault(x => HasParams(x, typeof(string)));
            if (m != null)
            {
                try { Status?.Invoke("Invoking Connect shape: Connect(string)"); m.Invoke(conn, new object[] { serverNameHostOnly }); _connectShapeUsed = "Connect(string)"; return true; }
                catch (Exception ex) { lastInvokeException = ex.InnerException ?? ex; }
            }

            // Legacy: Connect(string username, string password, string[] tokens, string serverName) // pass host:port
            m = methods.FirstOrDefault(x => HasParams(x, typeof(string), typeof(string), typeof(string[]), typeof(string)));
            if (m != null)
            {
                try { TrySetConnectionPort(conn, port); var serverNameWithPort = $"{host}:{port}"; Status?.Invoke("Invoking Connect shape: Connect(string,string,string[],string)"); m.Invoke(conn, new object[] { username ?? string.Empty, password ?? string.Empty, tokens ?? Array.Empty<string>(), serverNameWithPort }); _connectShapeUsed = "Connect(user,pass,tokens,server)"; return true; }
                catch (Exception ex) { lastInvokeException = ex.InnerException ?? ex; }
            }

            return false;
        }

        private static void TrySetConnectionPort(object conn, int port)
        {
            try
            {
                var t = conn.GetType();
                // Try common property names
                var p = t.GetProperty("Port", BindingFlags.Public | BindingFlags.Instance)
                     ?? t.GetProperty("ServerPort", BindingFlags.Public | BindingFlags.Instance)
                     ?? t.GetProperty("TcpPort", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite)
                {
                    if (p.PropertyType == typeof(int)) p.SetValue(conn, port);
                    else if (p.PropertyType == typeof(ushort)) p.SetValue(conn, (ushort)port);
                    return;
                }
                // Try fields
                var f = t.GetField("Port", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? t.GetField("ServerPort", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? t.GetField("TcpPort", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null)
                {
                    if (f.FieldType == typeof(int)) f.SetValue(conn, port);
                    else if (f.FieldType == typeof(ushort)) f.SetValue(conn, (ushort)port);
                }
            }
            catch { }
        }

        private static bool HasParams(MethodInfo mi, params Type[] ps)
        {
            var a = mi.GetParameters().Select(p => p.ParameterType).ToArray();
            if (a.Length != ps.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != ps[i]) return false;
            }
            return true;
        }

        // Tracks voice frame sequence for SendVoice(ArraySegment<byte>, SpeechTarget, uint)
        private uint _voiceSequence;

        public bool TrySendPcm48(byte[] pcm48Mono, CancellationToken ct)
        {
            try
            {
                if (_conn == null || pcm48Mono == null || pcm48Mono.Length == 0) return false;
                var channel = GetCurrentChannel();
                if (channel == null) return false;
                var channelType = channel.GetType();

                // Select the correct SendVoice overload (there may be multiple)
                var sendVoices = channelType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                            .Where(m => m.Name == "SendVoice")
                                            .ToArray();
                MethodInfo sendVoice = null;
                // Prefer (ArraySegment<byte>, SpeechTarget, uint) or (byte[], SpeechTarget, uint)
                sendVoice = sendVoices.FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return ps.Length >= 3 && (ps[0].ParameterType == typeof(ArraySegment<byte>) || ps[0].ParameterType == typeof(byte[])) && ps[1].ParameterType.IsEnum && (ps[2].ParameterType == typeof(uint) || ps[2].ParameterType == typeof(int));
                })
                // Then (ArraySegment<byte>, SpeechTarget) or (byte[], SpeechTarget)
                ?? sendVoices.FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return ps.Length >= 2 && (ps[0].ParameterType == typeof(ArraySegment<byte>) || ps[0].ParameterType == typeof(byte[])) && ps[1].ParameterType.IsEnum;
                })
                // Then (ArraySegment<byte>) or (byte[])
                ?? sendVoices.FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return ps.Length == 1 && (ps[0].ParameterType == typeof(ArraySegment<byte>) || ps[0].ParameterType == typeof(byte[]));
                });

                if (sendVoice == null) return false;

                // SendVoiceStop (no-arg if available)
                var sendStop = channelType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                           .FirstOrDefault(m => m.Name == "SendVoiceStop" && m.GetParameters().Length == 0);

                var speechTargetType = typeof(SpeechTarget);
                object targetEnum = Enum.ToObject(speechTargetType, 0); // SpeechTarget.Normal

                const int frameBytes = 1920; // 20ms @ 48k, mono 16-bit
                int idx = 0;
                var frameBuf = new byte[frameBytes];
                while (idx < pcm48Mono.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    int take = Math.Min(frameBytes, pcm48Mono.Length - idx);
                    Buffer.BlockCopy(pcm48Mono, idx, frameBuf, 0, take);
                    if (take < frameBytes) Array.Clear(frameBuf, take, frameBytes - take);
                    var segment = new ArraySegment<byte>(frameBuf, 0, frameBytes);
                    var ps = sendVoice.GetParameters();

                    // Build args based on parameter signature
                    if (ps.Length >= 3 && (ps[0].ParameterType == typeof(ArraySegment<byte>) || ps[0].ParameterType == typeof(byte[])) && ps[1].ParameterType.IsEnum)
                    {
                        var seqVal = _voiceSequence++;
                        object packetArg = ps[0].ParameterType == typeof(ArraySegment<byte>) ? (object)segment : (object)frameBuf;
                        object seqArg = ps[2].ParameterType == typeof(int) ? (object)unchecked((int)seqVal) : (object)seqVal;
                        sendVoice.Invoke(channel, new object[] { packetArg, targetEnum, seqArg });
                    }
                    else if (ps.Length >= 2 && (ps[0].ParameterType == typeof(ArraySegment<byte>) || ps[0].ParameterType == typeof(byte[])) && ps[1].ParameterType.IsEnum)
                    {
                        object packetArg = ps[0].ParameterType == typeof(ArraySegment<byte>) ? (object)segment : (object)frameBuf;
                        sendVoice.Invoke(channel, new object[] { packetArg, targetEnum });
                    }
                    else if (ps.Length == 1 && (ps[0].ParameterType == typeof(ArraySegment<byte>) || ps[0].ParameterType == typeof(byte[])))
                    {
                        object packetArg = ps[0].ParameterType == typeof(ArraySegment<byte>) ? (object)segment : (object)frameBuf;
                        sendVoice.Invoke(channel, new object[] { packetArg });
                    }
                    else
                    {
                        return false;
                    }

                    idx += take;
                    Thread.Sleep(20);
                }
                try { sendStop?.Invoke(channel, null); } catch { }
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) { Error?.Invoke($"Send error: {ex.Message}"); return false; }
        }

        private object GetCurrentChannel()
        {
            try
            {
                var localUser = _conn.GetType().GetProperty("LocalUser", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_conn);
                return localUser?.GetType().GetProperty("Channel", BindingFlags.Public | BindingFlags.Instance)?.GetValue(localUser);
            }
            catch { return null; }
        }

        // ===== IMumbleProtocol =====
        public void Initialise(MumbleConnection connection)
        {
            Connection = connection;
            try { Status?.Invoke("Protocol initialised"); } catch { }
        }

        public bool ValidateCertificate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate certificate, System.Security.Cryptography.X509Certificates.X509Chain chain, System.Net.Security.SslPolicyErrors sslPolicyErrors)
        {
            // First honor the per-connection validateTls flag
            if (!_validateTlsFlag)
            {
                if (sslPolicyErrors != System.Net.Security.SslPolicyErrors.None)
                {
                    Status?.Invoke($"WARNING: TLS validation disabled (errors: {sslPolicyErrors})");
                }
                return true;
            }

            // Then apply configured TLS mode
            switch (_tlsMode)
            {
                case Kinectv1.Settings.MumbleTlsValidate.Strict:
                    if (sslPolicyErrors != System.Net.Security.SslPolicyErrors.None)
                    {
                        Error?.Invoke($"TLS certificate error: {sslPolicyErrors}");
                        return false;
                    }
                    return true;
                case Kinectv1.Settings.MumbleTlsValidate.AcceptSelfSigned:
                    if (sslPolicyErrors != System.Net.Security.SslPolicyErrors.None)
                    {
                        Status?.Invoke($"WARNING: Accepting certificate with errors: {sslPolicyErrors}");
                    }
                    return true;
                case Kinectv1.Settings.MumbleTlsValidate.Off:
                    Status?.Invoke("WARNING: TLS certificate validation disabled");
                    return true;
                default:
                    return sslPolicyErrors == System.Net.Security.SslPolicyErrors.None;
            }
        }

        public System.Security.Cryptography.X509Certificates.X509Certificate SelectCertificate(object sender, string targetHost, System.Security.Cryptography.X509Certificates.X509CertificateCollection localCertificates, System.Security.Cryptography.X509Certificates.X509Certificate remoteCertificate, string[] acceptableIssuers)
        {
            try { Status?.Invoke($"TLS target host (SNI): {targetHost}"); } catch { }
            return null;
        }

        public void Version(MumbleProto.Version version) { Status?.Invoke("Server version received"); }
        public void ChannelState(ChannelState channelState)
        {
            try
            {
                if (channelState == null) return;
                uint? id = null; uint? parent = null; string name = null;
                try
                {
                    var t = channelState.GetType();
                    var idObj = t.GetProperty("ChannelId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(channelState);
                    if (idObj is uint u) id = u; else if (idObj is int i) id = unchecked((uint)i); else if (idObj is long l) id = unchecked((uint)l);
                    var parentObj = t.GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance)?.GetValue(channelState);
                    if (parentObj is uint up) parent = up; else if (parentObj is int ip) parent = unchecked((uint)ip); else if (parentObj is long lp) parent = unchecked((uint)lp);
                    name = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(channelState) as string;
                }
                catch { }
                if (!id.HasValue) return;
                if (!string.IsNullOrWhiteSpace(name)) _channelNames[id.Value] = name;
                if (!_channelParents.ContainsKey(id.Value) || parent.HasValue) _channelParents[id.Value] = parent;
                try { _chan[id.Value] = Tuple.Create(name ?? $"Channel{id.Value}", parent); } catch { }

                // If we have a pending join request and at least one channel known, try now
                if (!string.IsNullOrEmpty(_pendingJoinPath) && _channelNames.Count > 0)
                    TryJoinPendingIfPossible();
            }
            catch { }
        }
        public void UserState(UserState userState)
        {
            try
            {
                if (userState == null) return;

                uint? sessionOnMsg = null;
                try
                {
                    var sObj = userState.GetType().GetProperty("Session", BindingFlags.Public | BindingFlags.Instance)?.GetValue(userState);
                    if (sObj is uint us) sessionOnMsg = us;
                    else if (sObj is int isess) sessionOnMsg = unchecked((uint)isess);
                    else if (sObj is long lsess) sessionOnMsg = unchecked((uint)lsess);
                }
                catch { }

                if (sessionOnMsg.HasValue)
                {
                    _localSession = _localSession ?? sessionOnMsg; // capture if not yet known
                }

                // Only treat as our own state if session matches our local session
                if (_localSession.HasValue && sessionOnMsg.HasValue && sessionOnMsg.Value == _localSession.Value)
                {
                    uint? chan = null;
                    try
                    {
                        var chObj = userState.GetType().GetProperty("ChannelId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(userState);
                        if (chObj is uint c) chan = c; else if (chObj is int ci) chan = unchecked((uint)ci); else if (chObj is long cl) chan = unchecked((uint)cl);
                    }
                    catch { }

                    if (chan.HasValue)
                    {
                        _currentChannelId = chan.Value;
                        var name = _channelNames.TryGetValue(chan.Value, out var nm) ? nm : chan.Value.ToString();
                        Status?.Invoke($"Own UserState: session={_localSession.Value}, channel={name}({chan.Value})");
                    }
                }
            }
            catch { }
        }
        public void CodecVersion(CodecVersion codecVersion) { Status?.Invoke("Codec version received"); }
        public void ContextAction(ContextAction action) { }
        public void PermissionQuery(PermissionQuery permissionQuery) { }
        public void ServerConfig(ServerConfig config) { }

        public void ServerSync(ServerSync sync)
        {
            ReceivedServerSync = true;
            Status?.Invoke("ServerSync");
            try
            {
                var sessProp = sync.GetType().GetProperty("Session", BindingFlags.Public | BindingFlags.Instance);
                var val = sessProp?.GetValue(sync);
                if (val is uint u) _localSession = u;
                else if (val is int i) _localSession = unchecked((uint)i);
                else if (val is long l) _localSession = unchecked((uint)l);
                if (_localSession.HasValue && _localSession.Value != 0)
                    Status?.Invoke($"Local session: {_localSession.Value}");
            }
            catch { }
            TryJoinPendingIfPossible();
        }

        public void EncodedVoice(byte[] data, uint session, long sequence, IVoiceCodec codec, SpeechTarget target)
        {
            try
            {
                Status?.Invoke($"VOICE packet: {data?.Length ?? 0}B from session {session}");

                if (_codec == null) _codec = codec;
                if (_codec != null && !_codecAnnounced) { Status?.Invoke($"Codec: {_codec.GetType().Name}"); _codecAnnounced = true; }
                if (_codec == null) _codec = TryCreateOpusCodec();
                if (_codec != null && !_codecAnnounced) { Status?.Invoke($"Codec: {_codec.GetType().Name}"); _codecAnnounced = true; }
                if (_codec == null || data == null || data.Length == 0) return;

                float[] floats = null;
                try
                {
                    var ct = _codec.GetType();
                    object decoded = null;

                    // 1) Decode(byte[] data, int index, int length)
                    var dec3 = ct.GetMethod("Decode", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(byte[]), typeof(int), typeof(int) }, null);
                    if (dec3 != null)
                    {
                        decoded = dec3.Invoke(_codec, new object[] { data, 0, data.Length });
                    }
                    else
                    {
                        // 2) Decode(ArraySegment<byte> packet)
                        var decSeg = ct.GetMethod("Decode", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(ArraySegment<byte>) }, null);
                        if (decSeg != null)
                        {
                            var seg = new ArraySegment<byte>(data, 0, data.Length);
                            decoded = decSeg.Invoke(_codec, new object[] { seg });
                        }
                        else
                        {
                            // 3) Decode(byte[] data)
                            var dec1 = ct.GetMethod("Decode", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(byte[]) }, null);
                            if (dec1 != null)
                            {
                                decoded = dec1.Invoke(_codec, new object[] { data });
                            }
                            else
                            {
                                // 4) Fallback: any public Decode()
                                var decAny = ct.GetMethod("Decode", BindingFlags.Public | BindingFlags.Instance);
                                if (decAny != null)
                                {
                                    var ps = decAny.GetParameters();
                                    decoded = ps.Length == 0 ? decAny.Invoke(_codec, null)
                                                             : ps.Length == 1 && ps[0].ParameterType == typeof(byte[]) ? decAny.Invoke(_codec, new object[] { data })
                                                             : ps.Length == 1 && ps[0].ParameterType == typeof(ArraySegment<byte>) ? decAny.Invoke(_codec, new object[] { new ArraySegment<byte>(data, 0, data.Length) })
                                                             : null;
                                }
                            }
                        }
                    }

                    // Coerce decoded into float[]
                    if (decoded is float[] farr) floats = farr;
                    else if (decoded is IEnumerable<float> fen) floats = fen.ToArray();
                    else if (decoded is short[] sarr) floats = ShortsToFloats(sarr);
                    else if (decoded is ArraySegment<short> sseg)
                    {
                        if (sseg.Array != null) { var tmp = new short[sseg.Count]; Array.Copy(sseg.Array, sseg.Offset, tmp, 0, sseg.Count); floats = ShortsToFloats(tmp); }
                    }
                    else if (decoded is byte[] barr) floats = BytesToFloatsPcm16(barr);
                    else if (decoded is ArraySegment<byte> bseg)
                    {
                        if (bseg.Array != null) { var tmp = new byte[bseg.Count]; Array.Copy(bseg.Array, bseg.Offset, tmp, 0, bseg.Count); floats = BytesToFloatsPcm16(tmp); }
                    }
                }
                catch { }
                if (floats == null || floats.Length == 0) return;

                if ((DateTime.UtcNow - _lastDecodeLogUtc).TotalMilliseconds >= 500)
                {
                    Status?.Invoke($"Decoded {floats.Length} samples");
                    _lastDecodeLogUtc = DateTime.UtcNow;
                }

                if (TransmissionCodec == 0) TransmissionCodec = SpeechCodecs.Opus;
                AudioFrameReceived?.Invoke(floats, 48000, 1, $"User{session}");
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Decode error: {ex.Message}");
            }
        }

        public IVoiceCodec GetCodec(uint user, SpeechCodecs knownCodecs)
        {
            if (_codec != null) return _codec;
            _codec = TryCreateOpusCodec();
            if (_codec != null)
            {
                if (!_codecAnnounced) { Status?.Invoke($"Codec: {_codec.GetType().Name}"); _codecAnnounced = true; }
                return _codec;
            }
            if (!_codecWarned) { Error?.Invoke("No codec available yet; will retry"); _codecWarned = true; }
            // Keep returning null to force library to call again; we will retry creation on each call
            return null;
        }

        private IVoiceCodec TryCreateOpusCodec()
        {
            try
            {
                // Try known types in MumbleSharp
                var typeNames = new[]
                {
                    "MumbleSharp.Audio.Codecs.OpusCodec, MumbleSharp",
                    "MumbleSharp.Audio.Codecs.OpusVoiceDecoder, MumbleSharp"
                };
                foreach (var tn in typeNames)
                {
                    var t = Type.GetType(tn, throwOnError: false);
                    if (t != null && typeof(IVoiceCodec).IsAssignableFrom(t))
                    {
                        var inst = Activator.CreateInstance(t) as IVoiceCodec;
                        if (inst != null) return inst;
                    }
                }

                // Fallback: search loaded assemblies for a type implementing IVoiceCodec containing "Opus"
                var all = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in all)
                {
                    Type cand = null;
                    try { cand = asm.GetTypes().FirstOrDefault(x => typeof(IVoiceCodec).IsAssignableFrom(x) && !x.IsAbstract && x.Name.IndexOf("Opus", StringComparison.OrdinalIgnoreCase) >= 0); }
                    catch { continue; }
                    if (cand != null)
                    {
                        var inst = Activator.CreateInstance(cand) as IVoiceCodec;
                        if (inst != null) return inst;
                    }
                }
            }
            catch { }
            return null;
        }

        // ===== IMumbleProtocol (remaining members) =====
        public void UdpPing(byte[] ping) { }
        public void Ping(Ping ping) { }
        public void UserRemove(UserRemove userRemove) { }
        public void ChannelRemove(ChannelRemove channelRemove) { }
        public void TextMessage(TextMessage textMessage) { }
        public void UserList(UserList userList) { }
        public void SuggestConfig(SuggestConfig suggestConfig) { }
        public void SendVoice(ArraySegment<byte> packet, SpeechTarget target, uint sequence) { }
        public void SendVoiceStop() { }
        public void Reject(Reject reject) { _handshakeRejected = true; _handshakeReason = reject?.Reason; Error?.Invoke($"Server reject: {reject?.Reason}"); }
        public void PermissionDenied(PermissionDenied permissionDenied) { _handshakeRejected = true; _handshakeReason = permissionDenied?.Reason; Error?.Invoke($"Permission denied: {permissionDenied?.Reason}"); }
        public void Acl(Acl acl) { }
        public void QueryUsers(QueryUsers queryUsers) { }
        public void UserStats(UserStats userStats) { }
        public void BanList(BanList banList) { }

        public void Dispose()
        {
            try { TryDisconnect(_conn); } catch { }
            _conn = null;
        }

        public void TryApplySelfState(bool selfMute, bool selfDeaf)
        {
            try
            {
                _desiredSelfMute = selfMute;
                _desiredSelfDeaf = selfDeaf;

                if (_conn == null) return;
                var t = _conn.GetType();
                bool applied = false;

                // Methods on connection
                applied |= TryInvokeBoolSetter(t, _conn, "SetSelfMute", selfMute);
                applied |= TryInvokeBoolSetter(t, _conn, "SetSelfDeaf", selfDeaf);
                applied |= TryInvokeBoolSetter(t, _conn, "SetMute", selfMute);
                applied |= TryInvokeBoolSetter(t, _conn, "SetDeaf", selfDeaf);
                // Properties on connection
                applied |= TrySetBoolProperty(t, _conn, "SelfMute", selfMute);
                applied |= TrySetBoolProperty(t, _conn, "SelfDeaf", selfDeaf);

                // On LocalUser if available
                var localUser = t.GetProperty("LocalUser", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_conn);
                if (localUser != null)
                {
                    var lt = localUser.GetType();
                    applied |= TryInvokeBoolSetter(lt, localUser, "SetSelfMute", selfMute);
                    applied |= TryInvokeBoolSetter(lt, localUser, "SetSelfDeaf", selfDeaf);
                    applied |= TryInvokeBoolSetter(lt, localUser, "SetMute", selfMute);
                    applied |= TryInvokeBoolSetter(lt, localUser, "SetDeaf", selfDeaf);
                    applied |= TrySetBoolProperty(lt, localUser, "SelfMute", selfMute);
                    applied |= TrySetBoolProperty(lt, localUser, "SelfDeaf", selfDeaf);
                }

                // Do not send move-to-0; only send if a non-zero channel id is known
                var cur = GetLocalUserChannelId();
                bool sent = false;
                if (cur.HasValue && cur.Value != 0) sent = SendUserStateMove(cur.Value);

                if (applied || sent) Status?.Invoke($"Applied self state: mute={(selfMute ? "on" : "off")}, deaf={(selfDeaf ? "on" : "off")}");
                else Status?.Invoke("No self mute/deaf controls found on client");
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Apply self state failed: {ex.Message}");
            }
        }

        private static bool TryInvokeBoolSetter(Type t, object instance, string methodName, bool value)
        {
            try
            {
                var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                if (m != null) { m.Invoke(instance, new object[] { value }); return true; }
            }
            catch { }
            return false;
        }

        private static bool TrySetBoolProperty(Type t, object instance, string propertyName, bool value)
        {
            try
            {
                var p = t.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                {
                    p.SetValue(instance, value);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static float[] ShortsToFloats(short[] src)
        {
            var dst = new float[src.Length];
            const float inv = 1f / 32768f;
            for (int i = 0; i < src.Length; i++) dst[i] = Math.Max(-1f, Math.Min(1f, src[i] * inv));
            return dst;
        }
        private static float[] BytesToFloatsPcm16(byte[] src)
        {
            var dst = new float[src.Length / 2];
            const float inv = 1f / 32768f;
            int si = 0;
            for (int i = 0; i < dst.Length; i++)
            {
                short s = (short)(src[si++] | (src[si++] << 8));
                dst[i] = Math.Max(-1f, Math.Min(1f, s * inv));
            }
            return dst;
        }

        private bool TryJoinUsingModel(string path)
        {
            try
            {
                if (_conn == null) return false;
                var norm = (path ?? string.Empty).Trim('/','\\');
                var target = FindChannelByPath(norm);
                if (target == null) return false;

                var connType = _conn.GetType();
                var chType = target.GetType();

                // 1) Connection.JoinChannel(Channel)
                var joinByChannel = connType.GetMethod("JoinChannel", BindingFlags.Public | BindingFlags.Instance, null, new[] { chType }, null);
                if (joinByChannel != null)
                {
                    try { joinByChannel.Invoke(_conn, new object[] { target }); Status?.Invoke("Joined via Connection.JoinChannel(Channel)"); return true; } catch { }
                }

                // 2) Channel.Enter()/Join()
                var join0 = chType.GetMethod("Enter", BindingFlags.Public | BindingFlags.Instance)
                           ?? chType.GetMethod("Join", BindingFlags.Public | BindingFlags.Instance);
                if (join0 != null)
                {
                    try { join0.Invoke(target, null); Status?.Invoke("Joined via Channel.Enter/Join"); return true; } catch { }
                }

                // 3) LocalUser.Move(Channel)/SetChannel
                var localUser = connType.GetProperty("LocalUser", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_conn);
                if (localUser != null)
                {
                    var luType = localUser.GetType();
                    var move = luType.GetMethod("Move", BindingFlags.Public | BindingFlags.Instance, null, new[] { chType }, null)
                           ?? luType.GetMethod("SetChannel", BindingFlags.Public | BindingFlags.Instance, null, new[] { chType }, null);
                    if (move != null)
                    {
                        try { move.Invoke(localUser, new object[] { target }); Status?.Invoke("Joined via LocalUser.Move"); return true; } catch { }
                    }
                }
                return false;
            }
            catch { return false; }
        }

        private object FindChannelByPath(string normPath)
        {
            try
            {
                if (_conn == null) return null;
                var connType = _conn.GetType();
                var root = connType.GetProperty("RootChannel", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_conn);
                if (root == null)
                {
                    // Try Channels enumerable as a fallback
                    var channels = connType.GetProperty("Channels", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_conn) as System.Collections.IEnumerable;
                    if (channels != null)
                    {
                        object rootCh = null;
                        foreach (var ch in channels)
                        {
                            var nm = ch?.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ch) as string;
                            var parent = ch?.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ch);
                            if (string.Equals(nm?.Trim(), "Root", StringComparison.OrdinalIgnoreCase) || parent == null)
                            { rootCh = ch; break; }
                        }
                        if (rootCh == null) return null;
                        return WalkChildren(rootCh, normPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
                    }
                    return null;
                }

                return WalkChildren(root, normPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
            }
            catch { return null; }
        }

        private object WalkChildren(object current, string[] parts)
        {
            object cur = current;
            foreach (var part in parts)
            {
                cur = FindChildChannelByName(cur, part);
                if (cur == null) return null;
            }
            return cur;
        }

        private object FindChildChannelByName(object parentChannel, string name)
        {
            try
            {
                if (parentChannel == null || string.IsNullOrEmpty(name)) return null;
                var ct = parentChannel.GetType();
                foreach (var propName in new[] { "Children", "SubChannels", "Channels", "Childs" })
                {
                    var p = ct.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    var children = p?.GetValue(parentChannel) as System.Collections.IEnumerable;
                    if (children == null) continue;
                    foreach (var ch in children)
                    {
                        try
                        {
                            var nm = ch?.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ch) as string;
                            if (!string.IsNullOrEmpty(nm) && string.Equals(nm, name, StringComparison.OrdinalIgnoreCase))
                                return ch;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
