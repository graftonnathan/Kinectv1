using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MumbleSharp;
using MumbleSharp.Audio; // SpeechTarget
using MumbleSharp.Audio.Codecs; // IVoiceCodec, SpeechCodecs
using MumbleSharp.Model; // User, Channel
using MumbleProto; // protobuf message types

namespace Kinectv1.Mumble
{
    internal class MumbleSharpBridge : IMumbleProtocol
    {
        public event Action<string> OnStatus;
        public event Action<string> OnError;
        public event Action<byte[], uint, long, IVoiceCodec, SpeechTarget> OnEncodedVoice;

        public MumbleConnection Connection { get; private set; }
        public User LocalUser { get; private set; }
        public Channel RootChannel { get; private set; }
        public IEnumerable<Channel> Channels { get; private set; } = Enumerable.Empty<Channel>();
        public IEnumerable<User> Users { get; private set; } = Enumerable.Empty<User>();
        public bool ReceivedServerSync { get; private set; }
        public SpeechCodecs TransmissionCodec { get; private set; }

        public void Initialise(MumbleConnection connection)
        {
            Connection = connection;
            try { OnStatus?.Invoke("Mumble protocol initialised"); } catch { }
        }

        public bool ValidateCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            var ok = errors == SslPolicyErrors.None;
            if (!ok) OnError?.Invoke($"TLS certificate validation error: {errors}");
            return ok;
        }

        public X509Certificate SelectCertificate(object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate remoteCertificate, string[] acceptableIssuers)
        {
            return null; // no client certificate
        }

        public void Version(MumbleProto.Version version)
        {
            try { OnStatus?.Invoke("Server version received"); } catch { }
        }

        public void ChannelState(ChannelState channelState)
        {
            // Optionally refresh known channels
        }

        public void UserState(UserState userState)
        {
            // Optionally refresh known users
        }

        public void CodecVersion(CodecVersion codecVersion)
        {
            try { OnStatus?.Invoke("Codec version received"); } catch { }
        }

        public void ContextAction(ContextAction contextAction) { }
        public void PermissionQuery(PermissionQuery permissionQuery) { }

        public void ServerSync(ServerSync serverSync)
        {
            ReceivedServerSync = true;
            OnStatus?.Invoke("Server sync received");
        }

        public void ServerConfig(ServerConfig serverConfig)
        {
            OnStatus?.Invoke("Server config received");
        }

        public void EncodedVoice(byte[] packet, uint userSession, long sequence, IVoiceCodec codec, SpeechTarget target)
        {
            try
            {
                if (codec != null) TransmissionCodec = SpeechCodecs.Opus; // best-effort
                OnEncodedVoice?.Invoke(packet, userSession, sequence, codec, target);
            }
            catch { }
        }

        public void UdpPing(byte[] packet) { }
        public void Ping(Ping ping) { }
        public void UserRemove(UserRemove userRemove) { }
        public void ChannelRemove(ChannelRemove channelRemove) { }
        public void TextMessage(TextMessage textMessage) { }
        public void UserList(UserList userList) { }
        public void SuggestConfig(SuggestConfig suggestedConfiguration) { }

        public IVoiceCodec GetCodec(uint user, SpeechCodecs codec)
        {
            return null; // let external code supply codec
        }

        public void SendVoice(ArraySegment<byte> pcm, SpeechTarget target, uint targetId) { }
        public void SendVoiceStop() { }
        public void Reject(Reject reject) { }
        public void PermissionDenied(PermissionDenied permissionDenied) { }
        public void Acl(Acl acl) { }
        public void QueryUsers(QueryUsers queryUsers) { }
        public void UserStats(UserStats userStats) { }
        public void BanList(BanList banList) { }
    }
}
