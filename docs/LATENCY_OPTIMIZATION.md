# Maggie Latency Optimization Guide

This document describes the latency optimizations implemented in Maggie and how to tune them for your use case.

## Quick Start

Check current optimization status:
```bash
cd ~/workspace/maggie
python3 tools/latency_optimizer.py status
```

Enable low-latency mode (fastest responses):
```bash
python3 tools/latency_optimizer.py low
```

Restore normal mode (better quality):
```bash
python3 tools/latency_optimizer.py normal
```

## Implemented Optimizations

### 1. Embedding Cache (NEW)

**What it does:** Caches embedding vectors to avoid redundant API calls to the embeddings model.

**Latency impact:** Saves 100-500ms per cached query (typical embedding latency).

**Configuration:**
```json
{
  "ollama": {
    "embeddingCacheEnabled": true,
    "embeddingCacheSize": 100,
    "embeddingCacheTtlMinutes": 10
  }
}
```

**Trade-offs:** Uses ~50MB RAM for 100 cached embeddings (depends on embedding dimension).

### 2. Low-Latency Mode (NEW)

**What it does:** Disables expensive operations that add latency:
- Skips LLM-based summary rewriting when merging memory centroids
- Disables embedding cache (memory savings)
- Uses faster fallback summaries instead

**Latency impact:** Saves 500ms-3s during memory archival operations.

**Configuration:**
```json
{
  "ollama": {
    "lowLatencyMode": true
  }
}
```

**Trade-offs:** Memory summaries may be less coherent over time.

### 3. Async History Batching (EXISTING)

**What it does:** Batches conversation history writes instead of blocking on each message.

**Latency impact:** Saves 10-50ms per message.

**Implementation:** Flushes every 500ms or when batch fills up.

### 4. Throttled Memory Overflow Checks (EXISTING)

**What it does:** Limits how often memory overflow is checked to reduce overhead.

**Latency impact:** Prevents latency spikes every message when context is large.

**Configuration:**
```csharp
private const int OVERFLOW_CHECK_MESSAGE_INTERVAL = 3;  // Check every 3 messages
private static readonly TimeSpan OVERFLOW_CHECK_MIN_INTERVAL = TimeSpan.FromSeconds(10);
```

### 5. Optimized Token Budgets (TUNED)

**Settings for lower latency:**

| Setting | Default | Low-Latency | Impact |
|---------|---------|-------------|--------|
| `hotContextTokenLimit` | 4000 | 2500 | Less context to process |
| `memoryContextBudget` | 1200 | 600 | Fewer retrieved chunks |
| `chunkSizeTokens` | 300 | 500 | Fewer embeddings to compute |
| `vectorSearchTopK` | 3 | 2 | Less search overhead |

## Latency Breakdown

Typical response latency components:

```
┌─────────────────────────────────────────────┐
│  50-200ms  │ STT (speech-to-text)          │
│ 100-500ms  │ Embedding generation (cached) │
│  50-100ms  │ Vector search                 │
│2000-5000ms │ LLM response generation       │
│ 100-500ms  │ TTS (text-to-speech)          │
├─────────────────────────────────────────────┤
│2500-6300ms │ TOTAL (typical)               │
└─────────────────────────────────────────────┘
```

The optimizations target everything except LLM generation time (which depends on the model).

## Tuning Guide

### For Fastest Responses (Gaming, Quick Chat)

```json
{
  "ollama": {
    "lowLatencyMode": true,
    "vectorMemoryEnabled": false,
    "hotContextTokenLimit": 1500,
    "conversationMaxTokens": 3000
  }
}
```

**Expected latency:** 2000-4000ms

### For Balanced Performance (Default)

```json
{
  "ollama": {
    "lowLatencyMode": false,
    "vectorMemoryEnabled": true,
    "hotContextTokenLimit": 3000,
    "memoryContextBudget": 800,
    "embeddingCacheEnabled": true
  }
}
```

**Expected latency:** 2500-5000ms

### For Best Quality (Research, Long Conversations)

```json
{
  "ollama": {
    "lowLatencyMode": false,
    "vectorMemoryEnabled": true,
    "hotContextTokenLimit": 6000,
    "memoryContextBudget": 1500,
    "chunkSizeTokens": 200,
    "vectorSearchTopK": 5
  }
}
```

**Expected latency:** 3000-7000ms

## Monitoring Performance

### Check Current Settings
```bash
python3 tools/latency_optimizer.py status
```

### View Logs for Latency Info
```bash
tail -f ~/workspace/maggie/logs/maggie.log | grep -E "(embedding|retrieved|Archived|tokens)"
```

### Key Metrics to Watch
- `?? Retrieved X candidates, injected Y chunks` - Memory retrieval count
- `?? Successfully archived X/Y chunks` - Archival efficiency
- `?? Merged centroid (sim=X.XXX)` - Memory merge similarity scores

## Advanced Tuning

### Disable Vector Memory Entirely
If you don't need long-term memory, disable it completely:

```json
{
  "ollama": {
    "vectorMemoryEnabled": false
  }
}
```

This removes all embedding/retrieval latency.

### Optimize LM Studio Settings
- Use a smaller/faster model (e.g., 7B instead of 13B)
- Enable GPU acceleration
- Use quantization (Q4_K_M or Q5_K_M)
- Set context length appropriate for your needs

### Reduce TTS Latency
- Use CPU TTS if GPU is occupied
- Enable streaming TTS (already default)
- Reduce voice speed if acceptable

## Code Changes Summary

Files modified for latency optimization:

1. **`Services/Llm/EmbeddingCache.cs`** (NEW)
   - LRU cache for query embeddings
   - Configurable size and TTL

2. **`Services/Llm/LmStudioEmbeddingClient.cs`**
   - Added cache integration
   - Cache stats and management methods

3. **`Services/Llm/MemoryManager.cs`**
   - Low-latency mode support
   - Conditional LLM-based rewriting
   - Settings-driven behavior

4. **`Settings/default.json`**
   - Tuned default values for better latency
   - Added new configuration options

5. **`Settings/AppSettings.cs`**
   - Added `LowLatencyMode` property
   - Added embedding cache settings

6. **`tools/latency_optimizer.py`** (NEW)
   - CLI tool for managing optimizations
   - Status display and mode switching

## Future Optimizations

Potential improvements for future releases:

1. **Parallel initialization** - Initialize services concurrently
2. **Memory-mapped vector store** - Faster loading for large memory stores
3. **Quantized embeddings** - Smaller, faster embedding vectors
4. **Local embedding models** - Avoid network round-trip to LM Studio
5. **Streaming embeddings** - Generate embeddings as text streams in

## Troubleshooting

### High latency still occurring
- Check if LM Studio is using GPU (`nvidia-smi`)
- Verify embeddings model is loaded
- Look for `"??"` debug logs indicating memory operations

### Memory not archiving
- Ensure `vectorMemoryEnabled` is true
- Check that `hotContextTokenLimit` is being exceeded
- Look for `?? Memory overflow detected` in logs

### Cache not working
- Verify `embeddingCacheEnabled` is true
- Cache is in-memory only (clears on restart)
- Check stats: cache hit rate improves over time

## References

- [Vector Memory Documentation](VECTOR_MEMORY.md)
- [Settings Documentation](settings.md)
- [LM Studio Documentation](https://lmstudio.ai/docs)
