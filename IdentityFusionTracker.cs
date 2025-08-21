// IdentityFusionTracker.cs - Time-decayed voice+face fusion tracker
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Kinectv1
{
    /// <summary>
    /// Tracks identity across voice and face modalities with time decay and fusion scoring.
    /// Replaces ad-hoc speaker fallback with scored, time-decayed fusion.
    /// </summary>
    public static class IdentityFusionTracker
    {
        // Identity entry for tracking face and voice data per TrackingId
        public struct IdentityEntry
        {
            public ulong TrackingId;
            public string FaceName;
            public float FaceScore;
            public DateTime FaceLastUpdate;
            
            public string VoiceName;
            public float VoiceScore;
            public DateTime VoiceLastUpdate;
            
            public string FusedName;
            public float FusedScore;
            public DateTime LastUpdate;

            public IdentityEntry(ulong trackingId)
            {
                TrackingId = trackingId;
                FaceName = null;
                FaceScore = 0.0f;
                FaceLastUpdate = DateTime.MinValue;
                VoiceName = null;
                VoiceScore = 0.0f;
                VoiceLastUpdate = DateTime.MinValue;
                FusedName = "Unknown";
                FusedScore = 0.0f;
                LastUpdate = DateTime.UtcNow;
            }
        }

        // Thread-safe storage for identity entries
        private static readonly ConcurrentDictionary<ulong, IdentityEntry> _identities = new ConcurrentDictionary<ulong, IdentityEntry>();
        private static readonly object _fusionLock = new object();

        // Voice-only entries for brief association before spatial proximity
        private static readonly ConcurrentDictionary<string, (float score, DateTime timestamp)> _voiceOnlyEntries = new ConcurrentDictionary<string, (float, DateTime)>();

        // Events for fusion updates
        public static Action<ulong, string, float> OnIdentityFused;

        /// <summary>
        /// Update face information for a tracking ID
        /// </summary>
        public static void UpdateFace(ulong trackingId, string name, float confidence)
        {
            if (string.IsNullOrWhiteSpace(name) || confidence <= 0.0f)
                return;

            lock (_fusionLock)
            {
                var entry = _identities.GetOrAdd(trackingId, _ => new IdentityEntry(trackingId));
                
                // Update face data
                entry.FaceName = name;
                entry.FaceScore = confidence;
                entry.FaceLastUpdate = DateTime.UtcNow;
                entry.LastUpdate = DateTime.UtcNow;

                _identities[trackingId] = entry;

                // Recompute fusion
                RecomputeFusion(ref entry);
                _identities[trackingId] = entry;

                OnIdentityFused?.Invoke(trackingId, entry.FusedName, entry.FusedScore);
            }
        }

        /// <summary>
        /// Update voice information for a tracking ID or attempt spatial/temporal proximity matching
        /// </summary>
        public static void UpdateVoice(ulong? trackingId, string name, float confidence)
        {
            if (string.IsNullOrWhiteSpace(name) || confidence <= 0.0f)
                return;

            lock (_fusionLock)
            {
                if (trackingId.HasValue)
                {
                    // Direct association with a face tracking ID
                    var entry = _identities.GetOrAdd(trackingId.Value, _ => new IdentityEntry(trackingId.Value));
                    
                    entry.VoiceName = name;
                    entry.VoiceScore = confidence;
                    entry.VoiceLastUpdate = DateTime.UtcNow;
                    entry.LastUpdate = DateTime.UtcNow;

                    _identities[trackingId.Value] = entry;

                    // Recompute fusion
                    RecomputeFusion(ref entry);
                    _identities[trackingId.Value] = entry;

                    OnIdentityFused?.Invoke(trackingId.Value, entry.FusedName, entry.FusedScore);
                }
                else
                {
                    // No direct tracking ID - try proximity heuristic or store as voice-only briefly
                    var proximityTrackingId = FindProximityTrackingId(name);
                    
                    if (proximityTrackingId.HasValue)
                    {
                        // Found proximity match - update that tracking ID
                        UpdateVoice(proximityTrackingId.Value, name, confidence);
                    }
                    else
                    {
                        // Store as voice-only for brief period
                        _voiceOnlyEntries[name] = (confidence, DateTime.UtcNow);
                    }
                }
            }
        }

        /// <summary>
        /// Get the current fused identity for a tracking ID
        /// </summary>
        public static (string name, float score) GetFusedIdentity(ulong trackingId)
        {
            if (_identities.TryGetValue(trackingId, out var entry))
            {
                lock (_fusionLock)
                {
                    // Apply time decay and recompute if needed
                    var updated = entry;
                    RecomputeFusion(ref updated);
                    
                    if (updated.LastUpdate != entry.LastUpdate)
                    {
                        _identities[trackingId] = updated;
                    }
                    
                    return (updated.FusedName, updated.FusedScore);
                }
            }
            
            return ("Unknown", 0.0f);
        }

        /// <summary>
        /// Get all current tracked identities with their fused results
        /// </summary>
        public static Dictionary<ulong, (string name, float score)> GetAllFusedIdentities()
        {
            var result = new Dictionary<ulong, (string, float)>();
            
            lock (_fusionLock)
            {
                foreach (var kvp in _identities.ToList())
                {
                    var entry = kvp.Value;
                    RecomputeFusion(ref entry);
                    
                    if (entry.LastUpdate != kvp.Value.LastUpdate)
                    {
                        _identities[kvp.Key] = entry;
                    }
                    
                    result[kvp.Key] = (entry.FusedName, entry.FusedScore);
                }
            }
            
            return result;
        }

        /// <summary>
        /// Clean up old entries that haven't been updated recently
        /// </summary>
        public static void CleanupOldEntries()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-30); // Remove entries older than 30 seconds
            
            lock (_fusionLock)
            {
                var keysToRemove = _identities
                    .Where(kvp => kvp.Value.LastUpdate < cutoff)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var key in keysToRemove)
                {
                    _identities.TryRemove(key, out _);
                }

                // Also clean voice-only entries
                var voiceKeysToRemove = _voiceOnlyEntries
                    .Where(kvp => kvp.Value.timestamp < cutoff)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var key in voiceKeysToRemove)
                {
                    _voiceOnlyEntries.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Recompute fusion score with time decay
        /// </summary>
        private static void RecomputeFusion(ref IdentityEntry entry)
        {
            var now = DateTime.UtcNow;
            var halfLifeMs = AppSettings.LoadFusionDecayHalfLifeMs();
            var faceWeight = AppSettings.LoadFusionFaceWeight();
            var voiceWeight = AppSettings.LoadFusionVoiceWeight();
            var unknownThreshold = AppSettings.LoadFusionUnknownThreshold();

            // Apply time decay to face score
            float decayedFaceScore = 0.0f;
            if (!string.IsNullOrEmpty(entry.FaceName) && entry.FaceScore > 0.0f)
            {
                var faceAgeMs = (now - entry.FaceLastUpdate).TotalMilliseconds;
                decayedFaceScore = entry.FaceScore * (float)Math.Exp(-faceAgeMs / halfLifeMs);
            }

            // Apply time decay to voice score
            float decayedVoiceScore = 0.0f;
            if (!string.IsNullOrEmpty(entry.VoiceName) && entry.VoiceScore > 0.0f)
            {
                var voiceAgeMs = (now - entry.VoiceLastUpdate).TotalMilliseconds;
                decayedVoiceScore = entry.VoiceScore * (float)Math.Exp(-voiceAgeMs / halfLifeMs);
            }

            // Compute weighted fusion score
            float fusedScore = (decayedFaceScore * faceWeight + decayedVoiceScore * voiceWeight) / (faceWeight + voiceWeight);
            
            // Determine fused name
            string fusedName = "Unknown";
            
            if (fusedScore >= unknownThreshold)
            {
                // Choose name from the higher-scoring modality
                if (decayedFaceScore >= decayedVoiceScore && !string.IsNullOrEmpty(entry.FaceName))
                {
                    fusedName = entry.FaceName;
                }
                else if (decayedVoiceScore > 0.0f && !string.IsNullOrEmpty(entry.VoiceName))
                {
                    fusedName = entry.VoiceName;
                }
                
                // If both have same name, use that
                if (!string.IsNullOrEmpty(entry.FaceName) && !string.IsNullOrEmpty(entry.VoiceName) 
                    && string.Equals(entry.FaceName, entry.VoiceName, StringComparison.OrdinalIgnoreCase))
                {
                    fusedName = entry.FaceName;
                }
            }

            // Update entry
            entry.FusedName = fusedName;
            entry.FusedScore = fusedScore;
            entry.LastUpdate = now;
        }

        /// <summary>
        /// Find a tracking ID for voice based on spatial/temporal proximity heuristic
        /// </summary>
        private static ulong? FindProximityTrackingId(string voiceName)
        {
            // Simple heuristic: find the most recently updated face entry
            // In a more sophisticated system, this would use spatial proximity
            var recentCutoff = DateTime.UtcNow.AddSeconds(-2); // Only consider faces updated in last 2 seconds
            
            var candidateEntry = _identities
                .Where(kvp => kvp.Value.LastUpdate >= recentCutoff)
                .OrderByDescending(kvp => kvp.Value.LastUpdate)
                .FirstOrDefault();
            
            if (candidateEntry.Key != 0) // Default value for ulong in KeyValuePair
            {
                return candidateEntry.Key;
            }
            
            return null;
        }

        /// <summary>
        /// Get debug status for fusion tracker
        /// </summary>
        public static string GetFusionStatus()
        {
            lock (_fusionLock)
            {
                if (_identities.Count == 0)
                {
                    return "Identity Fusion Tracker: No active identities";
                }

                var status = new System.Text.StringBuilder();
                status.AppendLine($"Identity Fusion Tracker: {_identities.Count} active identities");
                
                foreach (var kvp in _identities.ToList())
                {
                    var entry = kvp.Value;
                    var updated = entry;
                    RecomputeFusion(ref updated);
                    
                    var faceInfo = !string.IsNullOrEmpty(entry.FaceName) 
                        ? $"Face: {entry.FaceName} ({entry.FaceScore:F2}, {(DateTime.UtcNow - entry.FaceLastUpdate).TotalSeconds:F1}s ago)"
                        : "Face: None";
                    
                    var voiceInfo = !string.IsNullOrEmpty(entry.VoiceName)
                        ? $"Voice: {entry.VoiceName} ({entry.VoiceScore:F2}, {(DateTime.UtcNow - entry.VoiceLastUpdate).TotalSeconds:F1}s ago)"
                        : "Voice: None";
                    
                    status.AppendLine($"  TrackingID {kvp.Key}: {updated.FusedName} ({updated.FusedScore:F2})");
                    status.AppendLine($"    {faceInfo}");
                    status.AppendLine($"    {voiceInfo}");
                }

                if (_voiceOnlyEntries.Count > 0)
                {
                    status.AppendLine($"Voice-only entries awaiting proximity match: {_voiceOnlyEntries.Count}");
                    foreach (var kvp in _voiceOnlyEntries.ToList())
                    {
                        var age = (DateTime.UtcNow - kvp.Value.timestamp).TotalSeconds;
                        status.AppendLine($"  {kvp.Key}: {kvp.Value.score:F2} ({age:F1}s ago)");
                    }
                }
                
                return status.ToString();
            }
        }
    }
}