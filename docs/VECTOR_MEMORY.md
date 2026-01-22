# Vector Memory System (Chunked + LLM Rewrite)

This document describes the vector memory system that provides long-term semantic memory for the AI assistant.

## Overview

The memory system uses a **2-layer architecture** with chunked archiving and LLM-based summary rewriting:

| Layer | Purpose | Storage | Token Budget |
|-------|---------|---------|--------------|
| **Hot** | Recent raw messages | JSON file (per prompt) | 3000 (configurable) |
| **Cold** | Embedded chunks with LLM summaries | Vector DB (per prompt) | 100 centroids × 1800 chars |

## Key Storage Model (Refactor)

### Per-System-Prompt (Persona) Isolation

Conversation history and vector DB are scoped by the currently selected `Ollama.SystemPromptPath`.
The system derives a **memory key** from the prompt filename (lowercased, safe for folder names).

That memory key is used for:
- Hot context file: `history/{memoryKey}/conversation.json`
- Vector DB: `memory/{memoryKey}/vectors.json`

### All-Speakers Hot Context

The hot context file is a single JSON object keyed by speaker name, e.g.:

```json
{
  "Nathan": [ { "Role": "user", "Content": "...", "Timestamp": "...", "Speaker": "Nathan" } ],
  "Alice":  [ { "Role": "assistant", "Content": "...", "Timestamp": "...", "Speaker": "Alice" } ]
}
```

Token counting and archival decisions are based on **the total across ALL speakers** for the current memory key.
This matches how prompts are constructed (`USER (Speaker): ...`, `ASSISTANT: ...`).

## Pipeline Flow

### Archiving (Overflow ? Storage)

```
Hot Context (all speakers) > HotContextTokenLimit
       ?
Split into chunks (~ChunkSizeTokens each, ChunkOverlapTokens overlap)
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

### Manual Archive (Settings UI)

The **Archive to memory** button archives the current hot context for the active memory key:
- Archives **all speakers** (no speaker-id requirement)
- Clears the hot context file for that memory key after archiving

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
Inject as "MEMORY CONTEXT:" in prompt
```

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

## Storage

| File | Location | Purpose |
|------|----------|---------|
| Vector DB | `memory/{persona}/vectors.json` | Centroids + pinned facts |
| Hot Context | `history/{persona}/conversation.json` | Recent messages (all speakers) |

Note: Each system prompt (persona) gets isolated memory storage.

## Data Model

### TopicCentroid
```json
{
  "Id": "abc123def456",
  "EmbeddingBase64": "aW50OC1xdWFudGl6ZWQ...",
  "Summary": "...",
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
  "Text": "USER (Nathan): ...\nASSISTANT: ...",
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

## Troubleshooting

### Archive doesn't trigger when UI shows over the limit
Ensure the code and UI both use the same token estimator and the same history formatting.
Token counting is based on `USER (Speaker): ...` / `ASSISTANT: ...` formatting.

### "Vector memory not enabled"
- Enable Vector Memory in Settings ? Memory
- Ensure LM Studio base URL is correct and an embeddings model is loaded
