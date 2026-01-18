# Vector Memory System (Chunked + LLM Rewrite)

This document describes the vector memory system that provides long-term semantic memory for the AI assistant.

## Overview

The memory system uses a **2-layer architecture** with chunked archiving and LLM-based summary rewriting:

| Layer | Purpose | Storage | Token Budget |
|-------|---------|---------|--------------|
| **Hot** | Recent raw messages | JSON file | 3000 (configurable) |
| **Cold** | Embedded chunks with LLM summaries | Vector DB | 100 centroids × 1800 chars |

## Pipeline Flow

### Archiving (Overflow ? Storage)

```
Hot Context > 3000 tokens
       ?
Split into chunks (~600 tokens each, 80 token overlap)
       ?
For each chunk:
  1. Generate embedding via LM Studio /v1/embeddings
  2. Generate summary via LM Studio /v1/chat/completions
  3. Upsert to VectorStore:
     - If similar centroid exists (cosine > 0.85): MERGE
       - Weighted average embedding
       - LLM REWRITES summary (old + new ? canonical)
     - Else: Create new centroid
       ?
Save to vectors.json
```

### Recall (Query ? Context)

```
User question
       ?
Generate query embedding
       ?
Cosine similarity search against all centroids
       ?
Apply recency boost + importance boost
       ?
Return top-K summaries within token budget
       ?
Inject as "RELEVANT MEMORY:" in prompt
```

## Key Features

### 1. Chunked Archiving
- Messages are split into **~600 token chunks** with **80 token overlap**
- Never splits inside a message boundary
- Each chunk is independently embedded and summarized
- Results in more precise semantic search matches

### 2. LLM Summary Rewrite on Merge
When a new chunk merges with an existing centroid (cosine > 0.85):
- The LLM receives both the old and new summaries
- It produces a **single canonical summary** preserving:
  - Key entities (names, APIs, products)
  - Decisions and conclusions
  - Technical values and settings
- Prevents unbounded summary growth
- Resolves contradictions (prefers newest info)

### 3. Fallback Behavior
If LLM calls fail:
- Summary: Falls back to extracting first question + first answer sentence
- Merge: Falls back to simple concatenation with trimming

## Configuration

Settings are in the **Memory** tab of the Settings window:

| Setting | Default | Description |
|---------|---------|-------------|
| Enable Vector Memory | false | Master toggle |
| Hot Context Token Limit | 3000 | Triggers archival when exceeded |
| Memory Context Budget | 600 | Max tokens for retrieved memory |
| Vector DB Path | memory | Base folder for vector storage |
| Embeddings Model | (select) | Model for generating embeddings |
| Search Top-K | 3 | Centroids to retrieve per query |
| Recency Boost | 0.15 | Boost factor for recent centroids |
| Min Retrieval Score | 0.55 | Minimum similarity to include |

## Data Model

### TopicCentroid
```json
{
  "Id": "abc123def456",
  "EmbeddingBase64": "aW50OC1xdWFudGl6ZWQ...",
  "Summary": "User asked about configuring vector memory. Key points: embeddings are numeric fingerprints for semantic search, summaries provide context for LLM, configure via vectorMemoryEnabled setting.",
  "MergeCount": 3,
  "CreatedAt": "2024-01-15T10:30:00Z",
  "UpdatedAt": "2024-01-15T14:20:00Z",
  "Speakers": ["Nathan"],
  "Importance": 0.6
}
```

### ArchivedChunk (Internal)
```json
{
  "Text": "USER: How do I configure...?\nASSISTANT: You need to...",
  "Start": "2024-01-15T10:00:00Z",
  "End": "2024-01-15T10:05:00Z",
  "Speakers": ["Nathan"],
  "ApproxTokens": 580
}
```

### PinnedFact
```json
{
  "Id": "fact001",
  "Text": "Nathan prefers concise responses",
  "CreatedAt": "2024-01-10T08:00:00Z",
  "Tags": ["user-preference"]
}
```

## Storage

| File | Location | Purpose |
|------|----------|---------|
| Vector DB | `memory/{persona}/vectors.json` | Centroids + pinned facts |
| Hot Context | `history/conversation.json` | Recent messages |

Note: Each system prompt (persona) gets isolated memory storage.

## LLM Prompts

### Initial Summary Prompt
```
SYSTEM: You are a summarization assistant. Create a concise summary capturing:
1. The main question or topic discussed
2. Key facts, decisions, or conclusions reached
3. Any specific values, settings, or technical details mentioned

Output ONLY the summary, no preamble. Keep it under 500 characters.
```

### Merge Rewrite Prompt
```
SYSTEM: You maintain a compact, accurate long-term memory summary for a topic centroid.

USER: Rewrite the centroid summary by integrating the new information.

Rules:
- Output a single updated summary (no headings).
- Preserve key entities (product names, APIs, people, device models), constraints, and decisions.
- Resolve contradictions by preferring the newest info when conflicts exist.
- Keep length <= 1500 characters.
- No filler, no speculation.

Existing summary:
<<<OLD>>>
{oldSummary}
<<<END>>>

New info to integrate:
<<<NEW>>>
{newSummary}
<<<END>>>
```

## Requirements

### LM Studio Setup
1. Download [LM Studio](https://lmstudio.ai/)
2. Load an **embedding model** (e.g., `nomic-embed-text`)
3. Load a **chat model** for summarization (e.g., `llama-3.1-8b`)
4. Enable local server (Settings ? Developer)
5. Configure LM Studio Base URL in settings

### Recommended Models
| Purpose | Model | Notes |
|---------|-------|-------|
| Embeddings | `nomic-embed-text` | 768 dims, good quality |
| Embeddings | `bge-small-en-v1.5` | 384 dims, faster |
| Summarization | `llama-3.1-8b-instruct` | Good balance |
| Summarization | `qwen2.5-7b-instruct` | Fast, concise |

## Performance

| Operation | Latency | Notes |
|-----------|---------|-------|
| Embedding (query) | ~50-150ms | Per user message |
| Embedding (chunk) | ~50-150ms | Per archived chunk |
| Summary (chunk) | ~500-2000ms | Per archived chunk |
| Rewrite (merge) | ~500-2000ms | Only when merging |
| Search | <10ms | Cosine similarity O(n) |

## Troubleshooting

### "Cannot connect to LM Studio"
- Ensure LM Studio is running with server enabled
- Check the LM Studio Base URL setting (default: `http://127.0.0.1:1234`)

### "No LLM model configured"
- Select a chat model in AI Assistant settings
- This model is used for both main responses and memory summarization

### Memory not recalling relevant info
- Check `minRetrievalScore` isn't too high (try 0.4-0.5)
- Verify embeddings model is loaded in LM Studio
- Check console for `??` log messages

### Summaries growing too long
- The rewrite system should prevent this
- If using fallback mode, check LM Studio connectivity

## Storage Capacity

```
100 centroids × 1800 chars = 180,000 chars
? 36,000 words of compressed knowledge
? 45,000 tokens of storage

With chunking: each centroid represents ~600 tokens of source conversation
Effective coverage: 100 × 600 = 60,000 tokens of conversation history
