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

                var serverNameHostOnly = host; // strip :port; use host only for serverName shapes
                Status?.Invoke($"Protocol initialised: {typeof(MumbleConnection).Assembly.GetName().Name} {typeof(MumbleConnection).Assembly.GetName().Version}");
                LogPublicSurface();
                Status?.Invoke($"Connecting to {host}:{port} as {username} (TLS {(validateTls ? "on" : "off")})");

                var connType = typeof(MumbleConnection);
                _conn = (MumbleConnection)CreateConnectionInstance(connType, this, host, port, validateTls) ?? (MumbleConnection)CreateConnectionInstanceFallback(connType);
                if (_conn == null) throw new InvalidOperationException("Failed to construct MumbleConnection");

                var tokens = Array.Empty<string>();
                Exception lastInvokeEx;
                if (!InvokeConnectOrdered(connType, _conn, host, port, validateTls, serverNameHostOnly, username, password, tokens, out lastInvokeEx))
                {
                    if (lastInvokeEx != null) throw new InvalidOperationException(lastInvokeEx.Message);
                    throw new InvalidOperationException("No suitable Connect overload on MumbleConnection");
                }

                Status?.Invoke($"Connect shape: {_connectShapeUsed}");

                // Try auth if present (harmless if Connect already did it)
                TryAuthenticateOrLogin(_conn, username ?? string.Empty, password ?? string.Empty);

                // Wait for ServerSync before reporting connected; also watch for server reject
                var t0 = DateTime.UtcNow;
                while (!ct.IsCancellationRequested && (DateTime.UtcNow - t0).TotalSeconds < 10 && !ReceivedServerSync && !_handshakeRejected)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
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

        public void JoinChannelPath(string channelPath)
        {
            try
            {
                if (_conn == null || string.IsNullOrWhiteSpace(channelPath)) return;
                var join = _conn.GetType().GetMethod("JoinChannel", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (join != null) join.Invoke(_conn, new object[] { channelPath });
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Join channel error: {ex.Message}");
            }
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
                try { m.Invoke(conn, new object[] { host, port, validateTls }); _connectShapeUsed = "Connect(string,int,bool)"; return true; }
                catch (Exception ex) { lastInvokeException = ex.InnerException ?? ex; }
            }

            // Fallback: Connect(string host, int port)
            m = methods.FirstOrDefault(x => HasParams(x, typeof(string), typeof(int)) || HasParams(x, typeof(string), typeof(ushort)));
            if (m != null)
            {
                try { m.Invoke(conn, new object[] { host, port }); _connectShapeUsed = "Connect(string,int)"; return true; }
                catch (Exception ex) { lastInvokeException = ex.InnerException ?? ex; }
            }

            // Fallback: Connect(string serverName)  // host only
            m = methods.FirstOrDefault(x => HasParams(x, typeof(string)));
            if (m != null)
            {
                try { m.Invoke(conn, new object[] { serverNameHostOnly }); _connectShapeUsed = "Connect(string)"; return true; }
                catch (Exception ex) { lastInvokeException = ex.InnerException ?? ex; }
            }

            // Legacy: Connect(string username, string password, string[] tokens, string serverName) // host only
            m = methods.FirstOrDefault(x => HasParams(x, typeof(string), typeof(string), typeof(string[]), typeof(string)));
            if (m != null)
            {
                try { TrySetConnectionPort(conn, port); m.Invoke(conn, new object[] { username ?? string.Empty, password ?? string.Empty, tokens ?? Array.Empty<string>(), serverNameHostOnly }); _connectShapeUsed = "Connect(user,pass,tokens,server)"; return true; }
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

        private void TryAuthenticateOrLogin(object conn, string username, string password)
        {
            try
            {
                var t = conn.GetType();
                var auth = t.GetMethod("Authenticate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(string) }, null);
                if (auth != null) { auth.Invoke(conn, new object[] { username, password }); Status?.Invoke("Authenticate(username, password) called"); return; }
                var login = t.GetMethod("Login", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(string) }, null);
                if (login != null) { login.Invoke(conn, new object[] { username, password }); Status?.Invoke("Login(username, password) called"); }
            }
            catch { }
        }

        public bool TrySendPcm48(byte[] pcm48Mono, CancellationToken ct)
        {
            try
            {
                if (_conn == null || pcm48Mono == null || pcm48Mono.Length == 0) return false;
                var channel = GetCurrentChannel();
                if (channel == null) return false;
                var channelType = channel.GetType();
                var sendVoice = channelType.GetMethod("SendVoice", BindingFlags.Public | BindingFlags.Instance);
                var sendStop = channelType.GetMethod("SendVoiceStop", BindingFlags.Public | BindingFlags.Instance);
                if (sendVoice == null) return false;

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
                    if (ps.Length >= 2 && ps[0].ParameterType == typeof(ArraySegment<byte>)) sendVoice.Invoke(channel, new object[] { segment, targetEnum });
                    else if (ps.Length == 1 && ps[0].ParameterType == typeof(ArraySegment<byte>)) sendVoice.Invoke(channel, new object[] { segment });
                    else return false;
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
        public void ChannelState(ChannelState channelState) { }
        public void UserState(UserState userState) { }
        public void CodecVersion(CodecVersion codecVersion) { Status?.Invoke("Codec version received"); }
        public void ContextAction(ContextAction action) { }
        public void PermissionQuery(PermissionQuery permissionQuery) { }
        public void ServerSync(ServerSync sync) { ReceivedServerSync = true; Status?.Invoke("ServerSync"); }
        public void ServerConfig(ServerConfig config) { }

        public void EncodedVoice(byte[] data, uint session, long sequence, IVoiceCodec codec, SpeechTarget target)
        {
            try
            {
                if (_codec == null) _codec = codec;
                if (_codec == null || data == null || data.Length == 0) return;

                float[] floats = null;
                try
                {
                    // MumbleSharp 1.x codecs typically return float[]
                    var dec = _codec.GetType().GetMethod("Decode", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(byte[]) }, null)
                              ?? _codec.GetType().GetMethod("Decode", BindingFlags.Public | BindingFlags.Instance);
                    object decoded = null;
                    if (dec != null)
                    {
                        var ps = dec.GetParameters();
                        decoded = ps.Length == 1 ? dec.Invoke(_codec, new object[] { data }) : dec.Invoke(_codec, null);
                    }

                    if (decoded is float[] farr) floats = farr;
                    else if (decoded is short[] sarr) floats = ShortsToFloats(sarr);
                    else if (decoded is byte[] barr) floats = BytesToFloatsPcm16(barr);
                }
                catch { }
                if (floats == null || floats.Length == 0) return;

                if (TransmissionCodec == 0) TransmissionCodec = SpeechCodecs.Opus;
                AudioFrameReceived?.Invoke(floats, 48000, 1, $"User{session}");
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Decode error: {ex.Message}");
            }
        }
        public void UdpPing(byte[] ping) { }
        public void Ping(Ping ping) { }
        public void UserRemove(UserRemove userRemove) { }
        public void ChannelRemove(ChannelRemove channelRemove) { }
        public void TextMessage(TextMessage textMessage) { }
        public void UserList(UserList userList) { }
        public void SuggestConfig(SuggestConfig suggestConfig) { }
        public IVoiceCodec GetCodec(uint user, SpeechCodecs knownCodecs) => _codec;
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
    }
}
