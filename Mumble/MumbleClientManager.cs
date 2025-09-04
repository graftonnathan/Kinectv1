using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Structs;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;

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

        private static readonly object _ttsGate = new object();
        private static OpusEncoder _ttsEncoder;

        private static MumbleSharpAdapter _adapter;

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
                host = host.Trim();

                try { _cts?.Cancel(); } catch { }
                _cts = new CancellationTokenSource();

                _adapter = new MumbleSharpAdapter();
                // Apply TLS policy from JSON settings if available
                try
                {
                    var tlsMode = App.SettingsProvider?.Current?.Mumble.TlsValidate ?? Kinectv1.Settings.MumbleTlsValidate.Strict;
                    _adapter.SetTlsMode(tlsMode);
                    OnStatusChanged?.Invoke($"[Mumble] TLS policy: {tlsMode}");
                }
                catch { }

                _adapter.Status += s => OnStatusChanged?.Invoke($"[Mumble] {s}");
                _adapter.Error += e => OnError?.Invoke($"[Mumble] {e}");
                _adapter.AudioFrameReceived += (pcm48, sampleRate, channels, user) =>
                {
                    try
                    {
                        if (pcm48 == null || pcm48.Length == 0) return;
                        // Convert float 48k mono to 16k mono PCM16 for recognizer
                        var shorts48 = FloatsToInt16(pcm48);
                        var pcm16k = MumbleSharpClient.Downsample48kTo16kPcm16(shorts48);
                        var rms = MumbleSharpClient.CalculateRms(pcm16k, pcm16k.Length);
                        OnRmsLevel?.Invoke(rms);
                        if (VoiceRecognizer.IsReady())
                        {
                            SpeakerIdentifier.SetDiscordSpeakerHint(user);
                            VoiceRecognizer.ProcessExternalAudio(pcm16k, pcm16k.Length, $"Mumble:{user}");
                        }
                    }
                    catch (Exception ex) { OnError?.Invoke($"Ingest error: {ex.Message}"); }
                };

                var ok = await _adapter.ConnectAsync(host, port, username, serverPassword, validateTls, _cts.Token).ConfigureAwait(false);
                if (!ok) { OnError?.Invoke("Mumble connect failed"); return false; }

                if (!string.IsNullOrWhiteSpace(channel))
                {
                    try { _adapter.JoinChannelPath(channel); } catch (Exception ex) { OnError?.Invoke($"Join channel error: {ex.Message}"); }
                }

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
        { await _voiceOpLock.WaitAsync(); try { try { _cts?.Cancel(); } catch { } if (_adapter != null) { try { await _adapter.DisconnectAsync(); } catch { } try { _adapter.Dispose(); } catch { } _adapter = null; } _isConnected = false; OnStatusChanged?.Invoke("Disconnected"); await Task.Delay(50); } finally { _voiceOpLock.Release(); } }

        private static short[] FloatsToInt16(float[] src)
        {
            var dst = new short[src.Length];
            for (int i = 0; i < src.Length; i++) { var f = src[i]; if (f > 1f) f = 1f; else if (f < -1f) f = -1f; dst[i] = (short)Math.Round(f * 32767f); }
            return dst;
        }

        public static async Task<bool> SendTtsToMumbleAsync(string text, string speakerRefId = null, CancellationToken ct = default)
        {
            try
            {
                if (!_isConnected || _adapter == null) return false; if (string.IsNullOrWhiteSpace(text)) return false; if (!CoquiTtsService.IsEnabled()) return false;
                var floats = await CoquiTtsService.GenerateAudioDataAsync(text, speakerRefId).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); if (floats == null || floats.Length == 0) return false;
                var gain = Math.Max(0f, (float)AppSettings.LoadDiscordTtsVolume()); if (gain <= 0f) gain = 1f; for (int i = 0; i < floats.Length; i++) { var f = floats[i] * gain; if (f > 1f) f = 1f; else if (f < -1f) f = -1f; floats[i] = f; }
                int srcRate = KokoroTtsService.GetSampleRate(); var resampled = ResampleLinearMono(floats, srcRate, 48000); var pcm = FloatsToPcm16(resampled);
                return _adapter.TrySendPcm48(pcm, ct);
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) { OnError?.Invoke($"TTS send error: {ex.Message}"); return false; }
        }

        private static byte[] FloatsToPcm16(float[] mono)
        { if (mono == null || mono.Length == 0) return Array.Empty<byte>(); var dst = new byte[mono.Length * 2]; int b = 0; for (int i = 0; i < mono.Length; i++) { float f = mono[i]; if (f > 1f) f = 1f; else if (f < -1f) f = -1f; short s = (short)Math.Round(f * 32767f); dst[b++] = (byte)(s & 0xFF); dst[b++] = (byte)((s >> 8) & 0xFF); } return dst; }

        private static float[] ResampleLinearMono(float[] source, int srcRate, int dstRate)
        { if (source == null || source.Length == 0 || srcRate == dstRate) return source ?? Array.Empty<float>(); double ratio = (double)dstRate / srcRate; int dstLen = (int)Math.Round(source.Length * ratio); var dst = new float[dstLen]; for (int n = 0; n < dstLen; n++) { double t = n / ratio; int i0 = (int)t; int i1 = Math.Min(i0 + 1, source.Length - 1); double frac = t - i0; dst[n] = (float)((1.0 - frac) * source[i0] + frac * source[i1]); } return dst; }

        public static string GetStatusSummary() => $"Running: {_isRunning}, Connected: {_isConnected}";
        public static string GetConnectionStatus() => _isConnected ? "Connected" : "Disconnected";
    }
}
