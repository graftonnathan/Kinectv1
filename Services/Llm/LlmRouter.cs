using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Llm
{
    public enum LlmProvider { Ollama, LMStudio }

    public sealed class LlmRouter
    {
        private readonly ILlmClient _ollama;
        private readonly ILlmClient _lm;
        private volatile LlmProvider _current;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _gate = new object();

        public LlmRouter(ILlmClient ollama, ILlmClient lm, LlmProvider start)
        {
            _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
            _lm = lm ?? throw new ArgumentNullException(nameof(lm));
            _current = start;
        }

        public void SwitchTo(LlmProvider p)
        {
            lock (_gate)
            {
                _current = p;
                try { _cts.Cancel(); } catch { }
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
        }

        private ILlmClient Active => _current == LlmProvider.Ollama ? _ollama : _lm;

        public IAsyncEnumerable<string> ChatStreamAsync(string system, string user)
        {
            CancellationToken token; lock (_gate) token = _cts.Token;
            return Active.ChatStreamAsync(system, user, token);
        }

        public Task<string> ChatOnceAsync(string system, string user)
        {
            CancellationToken token; lock (_gate) token = _cts.Token;
            return Active.ChatOnceAsync(system, user, token);
        }
    }
}
