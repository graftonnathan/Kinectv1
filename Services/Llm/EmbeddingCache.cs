using System;
using System.Collections.Generic;
using System.Threading;

namespace Kinectv1.Llm
{
    /// <summary>
    /// Simple LRU cache for embedding vectors to avoid recomputing for identical queries.
    /// Significantly reduces latency for repeated or similar queries.
    /// </summary>
    public sealed class EmbeddingCache
    {
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        private readonly LinkedList<string> _lruList = new LinkedList<string>();
        private readonly object _lock = new object();
        private readonly int _maxSize;
        private readonly TimeSpan _ttl;

        public EmbeddingCache(int maxSize = 100, int ttlMinutes = 10)
        {
            _maxSize = maxSize;
            _ttl = TimeSpan.FromMinutes(ttlMinutes);
        }

        /// <summary>
        /// Try to get embedding from cache.
        /// </summary>
        public bool TryGet(string text, out float[] embedding)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                embedding = null;
                return false;
            }

            var key = NormalizeKey(text);
            
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    // Check TTL
                    if (DateTime.UtcNow - entry.CreatedAt < _ttl)
                    {
                        // Move to front (most recently used)
                        _lruList.Remove(entry.Node);
                        _lruList.AddFirst(entry.Node);
                        
                        embedding = entry.Embedding;
                        return true;
                    }
                    
                    // Expired - remove
                    RemoveEntry(key, entry);
                }
            }
            
            embedding = null;
            return false;
        }

        /// <summary>
        /// Add embedding to cache.
        /// </summary>
        public void Set(string text, float[] embedding)
        {
            if (string.IsNullOrWhiteSpace(text) || embedding == null || embedding.Length == 0)
                return;

            var key = NormalizeKey(text);
            
            lock (_lock)
            {
                // Remove if exists
                if (_cache.TryGetValue(key, out var existing))
                {
                    RemoveEntry(key, existing);
                }
                
                // Evict oldest if at capacity
                while (_cache.Count >= _maxSize && _lruList.Count > 0)
                {
                    var oldest = _lruList.Last?.Value;
                    if (oldest != null && _cache.TryGetValue(oldest, out var oldEntry))
                    {
                        RemoveEntry(oldest, oldEntry);
                    }
                    else
                    {
                        _lruList.RemoveLast();
                    }
                }
                
                // Add new entry
                var node = _lruList.AddFirst(key);
                _cache[key] = new CacheEntry
                {
                    Embedding = embedding,
                    CreatedAt = DateTime.UtcNow,
                    Node = node
                };
            }
        }

        /// <summary>
        /// Clear all cached entries.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
                _lruList.Clear();
            }
        }

        /// <summary>
        /// Get cache statistics.
        /// </summary>
        public (int count, int maxSize, TimeSpan ttl) GetStats()
        {
            lock (_lock)
            {
                return (_cache.Count, _maxSize, _ttl);
            }
        }

        private void RemoveEntry(string key, CacheEntry entry)
        {
            _cache.Remove(key);
            if (entry.Node != null)
            {
                _lruList.Remove(entry.Node);
            }
        }

        private static string NormalizeKey(string text)
        {
            // Normalize whitespace and case for better hit rate
            var normalized = text.Trim().ToLowerInvariant();
            var sb = new System.Text.StringBuilder(normalized.Length);
            bool lastWasSpace = false;
            
            foreach (var c in normalized)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            
            return sb.ToString();
        }

        private class CacheEntry
        {
            public float[] Embedding { get; set; }
            public DateTime CreatedAt { get; set; }
            public LinkedListNode<string> Node { get; set; }
        }
    }
}
