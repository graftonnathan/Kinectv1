using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Tracks speakers detected via WebRTC audio input.
    /// Maintains a list of unique speakers with their last activity timestamp.
    /// </summary>
    public static class WebRtcSpeakerTracker
    {
        private static readonly ConcurrentDictionary<string, SpeakerActivity> _speakers = new();
        private static readonly object _lock = new();
        
        /// <summary>
        /// Event fired when speaker activity changes (new speaker or activity update).
        /// </summary>
        public static event Action<string, DateTime> OnSpeakerActivity;
        
        /// <summary>
        /// Event fired when connection status changes.
        /// </summary>
        public static event Action<WebRtcConnectionInfo> OnConnectionStatusChanged;
        
        // Connection tracking
        private static volatile int _connectedClients = 0;
        private static volatile bool _isListening = false;
        private static volatile int _inboundFramesPerSecond = 0;
        private static DateTime _lastStatsUpdate = DateTime.MinValue;

        /// <summary>
        /// Record speaker activity from WebRTC audio.
        /// </summary>
        /// <param name="speakerId">Speaker identifier (from source ID or resolved name).</param>
        public static void RecordActivity(string speakerId)
        {
            if (string.IsNullOrWhiteSpace(speakerId)) return;
            
            var normalizedId = NormalizeSpeakerId(speakerId);
            var now = DateTime.UtcNow;
            
            _speakers.AddOrUpdate(
                normalizedId,
                _ => new SpeakerActivity { SpeakerId = normalizedId, FirstSeen = now, LastSeen = now, ActivityCount = 1 },
                (_, existing) => { existing.LastSeen = now; existing.ActivityCount++; return existing; }
            );
            
            try { OnSpeakerActivity?.Invoke(normalizedId, now); } catch { }
        }

        /// <summary>
        /// Record speaker activity with a resolved speaker name.
        /// </summary>
        public static void RecordActivityWithName(string sourceId, string resolvedName)
        {
            var displayName = !string.IsNullOrWhiteSpace(resolvedName) && resolvedName != "UnknownSpeaker"
                ? resolvedName
                : NormalizeSpeakerId(sourceId);
            
            RecordActivity(displayName);
        }

        /// <summary>
        /// Get all tracked speakers, ordered by most recent activity.
        /// </summary>
        public static List<SpeakerActivity> GetRecentSpeakers(int maxCount = 10)
        {
            return _speakers.Values
                .OrderByDescending(s => s.LastSeen)
                .Take(maxCount)
                .ToList();
        }

        /// <summary>
        /// Get speakers active within the specified time window.
        /// </summary>
        public static List<SpeakerActivity> GetActiveSpeakers(TimeSpan window)
        {
            var cutoff = DateTime.UtcNow - window;
            return _speakers.Values
                .Where(s => s.LastSeen >= cutoff)
                .OrderByDescending(s => s.LastSeen)
                .ToList();
        }

        /// <summary>
        /// Clear all tracked speakers.
        /// </summary>
        public static void Clear()
        {
            _speakers.Clear();
        }

        /// <summary>
        /// Update connection status (called from WebRTC transport).
        /// </summary>
        public static void UpdateConnectionStatus(bool isListening, int connectedClients, int framesPerSecond = 0)
        {
            _isListening = isListening;
            _connectedClients = connectedClients;
            _inboundFramesPerSecond = framesPerSecond;
            _lastStatsUpdate = DateTime.UtcNow;
            
            try
            {
                OnConnectionStatusChanged?.Invoke(new WebRtcConnectionInfo
                {
                    IsListening = isListening,
                    ConnectedClients = connectedClients,
                    InboundFramesPerSecond = framesPerSecond,
                    LastUpdate = _lastStatsUpdate
                });
            }
            catch { }
        }

        /// <summary>
        /// Get current connection info.
        /// </summary>
        public static WebRtcConnectionInfo GetConnectionInfo()
        {
            return new WebRtcConnectionInfo
            {
                IsListening = _isListening,
                ConnectedClients = _connectedClients,
                InboundFramesPerSecond = _inboundFramesPerSecond,
                LastUpdate = _lastStatsUpdate
            };
        }

        private static string NormalizeSpeakerId(string sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return "Unknown";
            
            // Clean up common WebRTC source IDs
            var id = sourceId.Trim();
            
            if (id.StartsWith("webrtc-", StringComparison.OrdinalIgnoreCase))
                id = id.Substring(7);
            
            if (id == "client" || id == "default")
                return "WebRTC Client";
            
            // Capitalize first letter
            if (id.Length > 0)
                id = char.ToUpper(id[0]) + id.Substring(1);
            
            return id;
        }
    }

    /// <summary>
    /// Speaker activity record.
    /// </summary>
    public class SpeakerActivity
    {
        public string SpeakerId { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public int ActivityCount { get; set; }
        
        /// <summary>
        /// Whether the speaker was active in the last 5 seconds.
        /// </summary>
        public bool IsActive => (DateTime.UtcNow - LastSeen).TotalSeconds < 5;
        
        /// <summary>
        /// Display name for UI.
        /// </summary>
        public string DisplayName => SpeakerId;
        
        /// <summary>
        /// Formatted last seen time for UI.
        /// </summary>
        public string LastSeenDisplay
        {
            get
            {
                var elapsed = DateTime.UtcNow - LastSeen;
                
                if (elapsed.TotalSeconds < 5)
                    return "now";
                if (elapsed.TotalSeconds < 60)
                    return $"{(int)elapsed.TotalSeconds}s ago";
                if (elapsed.TotalMinutes < 60)
                    return $"{(int)elapsed.TotalMinutes}m ago";
                if (elapsed.TotalHours < 24)
                    return $"{(int)elapsed.TotalHours}h ago";
                
                return LastSeen.ToLocalTime().ToString("MMM d, HH:mm");
            }
        }
    }

    /// <summary>
    /// WebRTC connection status info.
    /// </summary>
    public class WebRtcConnectionInfo
    {
        public bool IsListening { get; set; }
        public int ConnectedClients { get; set; }
        public int InboundFramesPerSecond { get; set; }
        public DateTime LastUpdate { get; set; }
        
        public string StatusText
        {
            get
            {
                if (ConnectedClients > 0)
                    return "Connected";
                if (IsListening)
                    return "Listening";
                return "Not Started";
            }
        }
        
        public string StatusColor
        {
            get
            {
                if (ConnectedClients > 0)
                    return "#FF107C10"; // Green
                if (IsListening)
                    return "#FFFF8C00"; // Orange
                return "#FF666666"; // Gray
            }
        }
    }
}
