using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class EspeakIpaNet48 : IDisposable
{
    private readonly string _exe, _voice;
    private Process _p;
    private StreamWriter _stdin;      // UTF-8 writer (BaseStream)
    private StreamReader _stdout;     // UTF-8 reader  (BaseStream)
    private Task _stderrPump;
    private readonly SemaphoreSlim _lineLock = new SemaphoreSlim(1,1);
    private volatile bool _disposed;

    public EspeakIpaNet48(string exe, string voice)
    {
        _exe = exe; _voice = voice;
        Start();
    }

    private void Start()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            Arguments = $"--stdin --ipa -q -v {_voice}",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Force UTF-8 IPA on Windows consoles
        psi.EnvironmentVariables["ESPEAK_NG_OUTPUT_CHARSET"] = "utf-8";

        _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!_p.Start()) throw new InvalidOperationException("Failed to start espeak-ng");

        _stdin  = new StreamWriter(_p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        _stdout = new StreamReader(_p.StandardOutput.BaseStream, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        // Drain stderr to avoid backpressure
        _stderrPump = Task.Run(() =>
        {
            char[] buf = new char[1024];
            try
            {
                while (!_disposed && !_p.HasExited)
                {
                    int n = _p.StandardError.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                }
            } catch { }
        });
    }

    private void Restart()
    {
        try { _stdin?.Close(); } catch { }
        try
        {
            if (_p != null && !_p.HasExited)
            {
                _p.Kill();
                try { _p.WaitForExit(200); } catch { }
            }
        }
        catch { }
        try { _p?.Dispose(); } catch { }
        Start();
    }

    public async Task<string> GetIpaAsync(string text, TimeSpan timeout)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EspeakIpaNet48));
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string one = text.Replace("\r", " ").Replace("\n", " ").Trim();

        await _lineLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(one).ConfigureAwait(false);

            var readTask = _stdout.ReadLineAsync();
            if (await Task.WhenAny(readTask, Task.Delay(timeout)).ConfigureAwait(false) != readTask)
            {
                Restart(); // self-heal on stall
                throw new TimeoutException("Timed out waiting for IPA from espeak-ng (restarted).");
            }

            var line = await readTask.ConfigureAwait(false);
            if (line == null)
            {
                Restart();
                throw new IOException("espeak-ng exited while reading IPA (restarted).");
            }
            return line.Trim();
        }
        finally
        {
            _lineLock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _stdin?.Close(); } catch { }
        try { if (_p != null && !_p.HasExited) _p.Kill(); } catch { }
        try { _p?.Dispose(); } catch { }
    }
}
