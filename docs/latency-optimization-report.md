# Maggie Latency Optimization Report

## Date: February 1, 2026
## Task: Optimize for lower response latency

---

## 🔍 Issues Identified

### 1. **Query Rewrite Overhead** (CRITICAL)
- **Location**: `MemoryManager.cs` lines 277-292
- **Problem**: Before every vector search, Maggie makes an EXTRA LLM call to "rewrite" the query
- **Impact**: Adds 500ms-2s per query depending on LM Studio load
- **Code**:
```csharp
// Try query rewrite for better retrieval
string searchQuery = userMessage;
try
{
    var rewritten = await RewriteQueryForSearchAsync(userMessage, ct).ConfigureAwait(false);
    if (!string.IsNullOrWhiteSpace(rewritten))
    {
        searchQuery = rewritten;
        Console.WriteLine($"?? Query rewritten: {searchQuery}");
    }
}
```

### 2. **Small Chunk Size** (HIGH)
- **Location**: `Settings/default.json`
- **Problem**: `chunkSizeTokens: 150` creates too many small chunks
- **Impact**: More chunks = more embeddings = slower search
- **Recommendation**: Increase to 400-600 tokens

### 3. **Synchronous File I/O on Every Message** (HIGH)
- **Location**: `ConversationManager.cs` lines 560-700
- **Problem**: History is written synchronously, file locking
- **Impact**: 10-50ms per message, blocks response streaming

### 4. **Memory Overflow Check Every Message** (MEDIUM)
- **Location**: `ConversationManager.cs` line 1118
- **Problem**: `CheckAndProcessMemoryOverflowAsync` runs after EVERY response
- **Impact**: Token counting all speakers every time

### 5. **Excessive Vector Search TopK** (MEDIUM)
- **Location**: `MemoryManager.cs` line 38
- **Problem**: `SEARCH_TOP_K = 20` but only `INJECT_TOP_K = 8` are used
- **Impact**: Wasted embedding comparisons

### 6. **New HttpClient Per Request** (LOW)
- **Location**: `OllamaClient.cs` line 47
- **Problem**: "New HttpClient per request for clean cancellation"
- **Impact**: Connection overhead, no keep-alive

---

## ✅ Optimizations Implemented

### 1. Disabled Query Rewrite (Major Gain)
**File**: `Services/Llm/MemoryManager.cs`
- Commented out the `RewriteQueryForSearchAsync` call
- Savings: 500ms-2s per query with memory retrieval

### 2. Increased Chunk Size
**File**: `Settings/default.json`
- Changed `chunkSizeTokens` from 150 → 500
- Changed `chunkOverlapTokens` from 30 → 75
- Reduces chunk count by ~3x

### 3. Reduced Vector Search Overhead
**File**: `Services/Llm/MemoryManager.cs`
- Changed `SEARCH_TOP_K` from 20 → 10
- Still injects up to 8, but searches fewer candidates

### 4. Lowered Hot Context Limit
**File**: `Settings/default.json`
- Changed `hotContextTokenLimit` from 3000 → 2000
- Faster token counting, less history parsing

### 5. Reduced Memory Context Budget
**File**: `Settings/default.json`
- Changed `memoryContextBudget` from 600 → 400
- Less context injection = faster LLM processing

### 6. Added Keep-Alive to HttpClient
**File**: `Services/Llm/OllamaClient.cs`
- Added connection pooling with 2 minute idle timeout
- Reuses connections for better latency

---

## 📊 Expected Improvements

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| Query with memory | 2-4s | 1-2s | **50% faster** |
| Simple query | 1-2s | 0.8-1.5s | ~20% faster |
| Memory archive | Every message | Every message | Same (but faster) |
| File I/O | Synchronous | Synchronous | No change (batching needed) |

---

## 🔄 Next Steps (Future Work)

1. **Async History Batching**: Queue history writes instead of sync
2. **Memory Check Throttling**: Only check every 5 messages, not every one
3. **HttpClient Singleton**: Use `IHttpClientFactory` pattern
4. **Embedding Cache**: Cache frequent query embeddings
5. **Parallel Warmup**: Pre-warm LLM connection on startup

---

## 📝 Files Modified

1. `~/workspace/maggie/Services/Llm/MemoryManager.cs` - Disabled query rewrite, reduced SEARCH_TOP_K
2. `~/workspace/maggie/Settings/default.json` - Tuned memory settings
3. `~/workspace/maggie/Services/Llm/OllamaClient.cs` - Added connection pooling

---

## 🧪 Testing Notes

To verify improvements:
```bash
cd ~/workspace/maggie
dotnet build MaggieHeadless.csproj
./start-maggie.sh

# Test with memory query
curl -X POST http://localhost:18790/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"What did we discuss yesterday?"}'
```

Watch for:
- No "Query rewritten:" logs (query rewrite disabled)
- Faster response times in console timestamps

