using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MumbleSharp;
using MumbleSharp.Model;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kinectv1.Mumble
{
    // Thin protocol: owns no connection; only handles events and audio plumbing.
    public sealed class MumbleSimpleClient : BasicMumbleProtocol
    {
        public event Action<string, short[]> OnPcm16kFrame; // (speakerName, 20ms 16k mono PCM16)
        public event Action<string, float> OnRms;            // (speakerName, RMS 0..1)

        protected override void UserJoined(User user)
        {
            base.UserJoined(user);

            // user.Voice is an IWaveProvider (decoded audio). Convert to 16k PCM16 and stream frames.
            var sampleProv = user.Voice.ToSampleProvider(); // float32 samples
            var to16k = new WdlResamplingSampleProvider(sampleProv, 16000); // mono preserved

            // Metering for RMS (approx via peak as proxy)
            var meter = new MeteringSampleProvider(to16k, samplesPerNotification: 1600); // 100ms windows @16k
            meter.StreamVolume += (s, a) =>
            {
                OnRms?.Invoke(user.Name, a.MaxSampleValues?.FirstOrDefault() ?? 0f);
            };

            var waveProvider = meter.ToWaveProvider16();

            _ = Task.Run(() =>
            {
                try
                {
                    var frameBytes = 2 * 16000 / 50; // 20ms of 16k PCM16 = 640 bytes
                    var buffer = new byte[frameBytes];
                    var shortFrame = new short[frameBytes / 2];

                    while (true)
                    {
                        int read = waveProvider.Read(buffer, 0, buffer.Length);
                        if (read == frameBytes)
                        {
                            Buffer.BlockCopy(buffer, 0, shortFrame, 0, read);
                            OnPcm16kFrame?.Invoke(user.Name, (short[])shortFrame.Clone());
                        }
                        else
                        {
                            Thread.Sleep(1);
                        }
                    }
                }
                catch
                {
                    // Exit task on errors or disposal
                }
            });
        }
    }
}
