using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Kinectv1.Llm
{
    public enum LlmProvider { Ollama, LMStudio }

    /// <summary>
    /// Routes LLM requests to the configured provider (Ollama or LM Studio).
    /// Supports barge-in cancellation via CancelCurrentRequest().
    /// </summary>
    public sealed class LlmRouter
    {
        private readonly ILlmClient _ollama;
        private readonly ILlmClient _lm;
        private volatile LlmProvider _current;
        
        // Master CTS for barge-in cancellation
        private CancellationTokenSource _masterCts = new CancellationTokenSource();
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
                ResetMasterCts();
            }
        }

        /// <summary>
        /// Cancel the current streaming request to the LLM backend.
        /// This triggers the cancellation callback in the client which aborts the HTTP connection.
        /// </summary>
        public void CancelCurrentRequest()
        {
            lock (_gate)
            {
                if (!_masterCts.IsCancellationRequested)
                {
                    Console.WriteLine("[LlmRouter] Cancelling current request");
                    try { _masterCts.Cancel(); } catch { }
                }
                ResetMasterCts();
            }
        }

        private void ResetMasterCts()
        {
            try { _masterCts.Dispose(); } catch { }
            _masterCts = new CancellationTokenSource();
        }

        private ILlmClient Active => _current == LlmProvider.Ollama ? _ollama : _lm;

        /// <summary>
        /// Stream chat completion from the active LLM provider.
        /// Cancellation can occur via the external token or CancelCurrentRequest().
        /// </summary>
        public async IAsyncEnumerable<string> ChatStreamAsync(
            string system, 
            string user,
            [EnumeratorCancellation] CancellationToken externalCt = default,
            string[] images = null)
         {
            CancellationToken masterToken;
            lock (_gate) { masterToken = _masterCts.Token; }
            
            // Link external token with master token so either can trigger cancellation
            using var linkedCts = externalCt == default 
                ? CancellationTokenSource.CreateLinkedTokenSource(masterToken)
                : CancellationTokenSource.CreateLinkedTokenSource(externalCt, masterToken);
            
            var linkedToken = linkedCts.Token;
            
            await foreach (var chunk in Active.ChatStreamAsync(system, user, linkedToken, images))
             {
                 if (linkedToken.IsCancellationRequested)
                     yield break;
                 yield return chunk;
             }
         }

        public async Task<string> ChatOnceAsync(string system, string user, CancellationToken externalCt = default, string[] images = null)
         {
             CancellationToken masterToken;
             lock (_gate) { masterToken = _masterCts.Token; }
            
             using var linkedCts = externalCt == default 
                 ? CancellationTokenSource.CreateLinkedTokenSource(masterToken)
                 : CancellationTokenSource.CreateLinkedTokenSource(externalCt, masterToken);
            
             return await Active.ChatOnceAsync(system, user, linkedCts.Token, images).ConfigureAwait(false);
         }
     }
 }
