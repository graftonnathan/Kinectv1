using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1
{
    // Minimal playback controller used by TtsService for legacy preemption.
    public class TtsPlaybackController : IPlaybackState
    {
        private static readonly object _sync = new object();
        private static CancellationTokenSource _cts;
        private static Task _task = Task.CompletedTask;
        private static string _uttId;
        private static int _counter;
        private static readonly TtsPlaybackController _instance = new TtsPlaybackController();
        public static IPlaybackState Instance => _instance;

        public bool IsSpeaking { get { lock (_sync) return _cts != null && !_cts.IsCancellationRequested && _task != null && !_task.IsCompleted; } }
        public CancellationToken PlaybackCancellationToken { get { lock (_sync) return _cts?.Token ?? CancellationToken.None; } }
        public event Action OnPlaybackStart;
        public event Action OnPlaybackStop;

        public static async Task<bool> StartUtterance(string text, string speaker, Func<string,string,CancellationToken,Task<bool>> func)
        {
            if (string.IsNullOrWhiteSpace(text) || func == null) return false;
            var newCts = new CancellationTokenSource();
            var old = Interlocked.Exchange(ref _cts, newCts);
            Task oldTask; lock (_sync) oldTask = _task;
            try { old?.Cancel(); } catch { }
            if (oldTask != null && !oldTask.IsCompleted)
            {
                try { await Task.WhenAny(oldTask, Task.Delay(400)); } catch { }
            }
            try { old?.Dispose(); } catch { }
            string localId; lock (_sync) { _counter++; localId = "utt_" + _counter; _uttId = localId; }
            try { (_instance.OnPlaybackStart)?.Invoke(); } catch { }
            Task<bool> run; lock (_sync) { run = func(text, speaker, newCts.Token); _task = run; }
            bool ok = false;
            try { ok = await run.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                bool fire = false; lock (_sync) { if (_uttId == localId) { _cts = null; _uttId = null; fire = true; } }
                if (fire) { try { (_instance.OnPlaybackStop)?.Invoke(); } catch { } }
            }
            return ok;
        }

        public static void CancelCurrent()
        { var c = _cts; try { c?.Cancel(); } catch { } }

        public static bool HasActiveUtterance()
        { Task t; lock (_sync) t = _task; return t != null && !t.IsCompleted; }

        public static string GetCurrentUtteranceId()
        { lock (_sync) return _uttId; }
    }
}
