using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    // Persistent eSpeak NG IPA service (single-file, minimal) for .NET Framework 4.8.1
    internal sealed class EspeakIpaService : IDisposable
    {
        private readonly object _procLock = new object();
        private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1); // serialize stdin/stdout

        private Process _proc;
        private StreamWriter _stdin;
        private StreamReader _stdout;
        private CancellationTokenSource _cts;
        private Task _stderrPump;
        private volatile bool _disposed;

        private readonly string _exePath;
        private readonly string _voice;
        private readonly int _rate;

        public EspeakIpaService(string exePath = "espeak-ng.exe", string voice = "en-us", int rate = 170)
        {
            _exePath = exePath;
            _voice = voice;
            _rate = rate;
            StartProcess();
        }

        private void StartProcess()
        {
            lock (_procLock)
            {
                _cts = new CancellationTokenSource();

                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = string.Format("--stdin --ipa -q -v {0} -s {1}", _voice, _rate),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
#if NETFRAMEWORK
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
#endif
                };
                try
                {
                    // Ensure eSpeak finds its data files when running from a bundled location
                    var exeDir = Path.GetDirectoryName(_exePath);
                    if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir))
                    {
                        psi.WorkingDirectory = exeDir;
                        var dataDir = Path.Combine(exeDir, "espeak-ng-data");
                        if (Directory.Exists(dataDir))
                        {
#if NETFRAMEWORK
                            psi.EnvironmentVariables["ESPEAK_DATA_PATH"] = dataDir;
                            psi.EnvironmentVariables["ESPEAKNG_DATA_PATH"] = dataDir;
#else
                            psi.Environment["ESPEAK_DATA_PATH"] = dataDir;
                            psi.Environment["ESPEAKNG_DATA_PATH"] = dataDir;
#endif
                        }
                    }
                }
                catch { }
#if NETFRAMEWORK
                try { psi.EnvironmentVariables["ESPEAKNG_OUTPUT_CHARSET"] = "utf-8"; } catch { }
                try { psi.EnvironmentVariables["ESPEAK_NG_OUTPUT_CHARSET"] = "utf-8"; } catch { }
#else
                try { psi.Environment["ESPEAKNG_OUTPUT_CHARSET"] = "utf-8"; } catch { }
                try { psi.Environment["ESPEAK_NG_OUTPUT_CHARSET"] = "utf-8"; } catch { }
#endif

                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (!_proc.Start()) throw new InvalidOperationException("Failed to start espeak-ng.");

                // Explicit UTF-8 writers/readers (no BOM)
                _stdin = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                _stdout = new StreamReader(_proc.StandardOutput.BaseStream, new UTF8Encoding(false));

                // Drain stderr so the process never blocks on full pipe
                _stderrPump = Task.Run(async () =>
                {
                    try
                    {
                        using (var err = _proc.StandardError)
                        {
                            var buf = new char[1024];
                            while (!_cts.IsCancellationRequested && !_proc.HasExited)
                            {
                                int n;
                                try { n = await err.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false); }
                                catch { break; }
                                if (n <= 0) break;
                            }
                        }
                    }
                    catch { }
                }, _cts.Token);
            }
        }

        private void RestartProcess()
        {
            lock (_procLock)
            {
                try { _cts?.Cancel(); } catch { }
                try { _stdin?.Close(); } catch { }
                try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
                try { _proc?.Dispose(); } catch { }
                _stdin = null;
                _stdout = null;
                try { _stderrPump?.Wait(200); } catch { }

                StartProcess();
            }
        }

        // One input line -> one IPA line
        public async Task<string> GetIpaAsync(string text, TimeSpan? timeout = null, CancellationToken cancel = default(CancellationToken))
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EspeakIpaService));
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            string oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

            await _ioLock.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                await _stdin.WriteLineAsync(oneLine).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);

                // Read next IPA line
#if NETFRAMEWORK
                var readTask = Task.Run(() => _stdout.ReadLine());
#else
                var readTask = _stdout.ReadLineAsync();
#endif

                if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
                {
                    var completed = await Task.WhenAny(readTask, Task.Delay(timeout.Value, cancel)).ConfigureAwait(false);
                    if (completed != readTask)
                    {
                        RestartProcess();
                        throw new TimeoutException("Timed out waiting for IPA from espeak-ng.");
                    }
                }

                var line = await readTask.ConfigureAwait(false);
                return NormalizeIpa(line ?? string.Empty);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                try { _cts?.Cancel(); } catch { }
                try { _stdin?.Close(); } catch { }
                try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            }
            finally
            {
                try { _proc?.Dispose(); } catch { }
                try { _cts?.Dispose(); } catch { }
                try { _stderrPump?.Wait(300); } catch { }
                try { _ioLock?.Dispose(); } catch { }
            }
        }

        private static string NormalizeIpa(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }
    }
}
