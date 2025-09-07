//SpeakerIdentifier.cs
using System;

namespace Kinectv1
{
    public static class SpeakerIdentifier
    {
        private static float _defaultThreshold = 0.40f; // Default threshold (legacy)
        private static string _discordSpeakerHint = ""; // Discord speaker hint
        private static DateTime _discordSpeakerHintTime = DateTime.MinValue;
        private static readonly TimeSpan _discordSpeakerHintTimeout = TimeSpan.FromSeconds(10); // Hint expires after 10 seconds

        public static (string name, float score)? Identify(float[] embedding)
        {
            try
            {
                if (embedding == null || embedding.Length == 0)
                {
                    return null;
                }

                // Use configurable threshold from settings
                var threshold = (float)(Kinectv1.App.SettingsProvider?.Current?.Audio?.SpeakerMatchMinScore ?? _defaultThreshold);
                var match = MemoryStore.MatchBestVoice(embedding, thresholdCos: threshold);
                
                if (match.HasValue)
                {
                    return match;
                }
                else
                {
                    // Check for Discord speaker hint if no voice match found
                    if (HasValidDiscordSpeakerHint())
                    {
                        return (_discordSpeakerHint, 0.5f); // Return Discord speaker with moderate confidence
                    }
                    
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ SpeakerIdentifier.Identify failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Set a Discord speaker hint that can be used when voice recognition fails
        /// </summary>
        public static void SetDiscordSpeakerHint(string discordUsername)
        {
            if (!string.IsNullOrEmpty(discordUsername))
            {
                _discordSpeakerHint = discordUsername;
                _discordSpeakerHintTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Get the current Discord speaker hint if it's still valid
        /// </summary>
        public static string GetDiscordSpeakerHint()
        {
            if (HasValidDiscordSpeakerHint())
            {
                return _discordSpeakerHint;
            }
            return null;
        }

        /// <summary>
        /// Check if we have a valid Discord speaker hint that hasn't expired
        /// </summary>
        public static bool HasValidDiscordSpeakerHint()
        {
            return !string.IsNullOrEmpty(_discordSpeakerHint) && 
                   (DateTime.UtcNow - _discordSpeakerHintTime) < _discordSpeakerHintTimeout;
        }

        public static void SetDefaultThreshold(float threshold)
        {
            if (threshold >= 0.0f && threshold <= 1.0f)
            {
                var oldThreshold = _defaultThreshold;
                _defaultThreshold = threshold;
                Console.WriteLine($"Default voice threshold changed: {oldThreshold:F3} → {threshold:F3}");
            }
            else
            {
                Console.WriteLine($"Invalid threshold {threshold:F3}, must be between 0.0 and 1.0");
            }
        }

        public static float GetDefaultThreshold()
        {
            // Return threshold from settings instead of hardcoded value
            return (float)(Kinectv1.App.SettingsProvider?.Current?.Audio?.SpeakerMatchMinScore ?? _defaultThreshold);
        }

        public static void EnrollSpeaker(string name, float[] embedding)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    Console.WriteLine("SpeakerIdentifier: Invalid name provided for enrollment");
                    return;
                }

                if (embedding == null || embedding.Length == 0)
                {
                    Console.WriteLine("SpeakerIdentifier: Invalid embedding provided for enrollment");
                    return;
                }

                MemoryStore.AddVoiceEmbedding(name, embedding);
                Console.WriteLine($"🎤 Speaker enrolled: {name} (embedding size: {embedding.Length})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SpeakerIdentifier.EnrollSpeaker failed: {ex.Message}");
            }
        }

        public static int GetEnrolledSpeakersCount()
        {
            try
            {
                var people = MemoryStore.GetAllPeople();
                int count = 0;
                foreach (var person in people)
                {
                    if (person.VoiceEmbeddings.Count > 0)
                        count++;
                }
                return count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SpeakerIdentifier.GetEnrolledSpeakersCount failed: {ex.Message}");
                return 0;
            }
        }

        public static void ListEnrolledSpeakers()
        {
            try
            {
                var people = MemoryStore.GetAllPeople();
                Console.WriteLine("🎤 Enrolled speakers:");
                
                int count = 0;
                foreach (var person in people)
                {
                    if (person.VoiceEmbeddings.Count > 0)
                    {
                        Console.WriteLine($"  - {person.Name} ({person.VoiceEmbeddings.Count} voice samples)");
                        
                        // Show embedding info for debugging
                        for (int i = 0; i < person.VoiceEmbeddings.Count && i < 3; i++)
                        {
                            var emb = person.VoiceEmbeddings[i];
                            Console.WriteLine($"    Sample {i + 1}: {emb.Length} dimensions");
                        }
                        count++;
                    }
                }
                
                if (count == 0)
                {
                    Console.WriteLine("  No speakers enrolled yet.");
                }
                else
                {
                    Console.WriteLine($"  Total: {count} enrolled speaker(s)");
                }
                // Print threshold from settings (not legacy field)
                var current = (float)(Kinectv1.App.SettingsProvider?.Current?.Audio?.SpeakerMatchMinScore ?? _defaultThreshold);
                Console.WriteLine($"🔧 Current system threshold: {current:F3}");
                
                // Show Discord speaker hint status
                if (HasValidDiscordSpeakerHint())
                {
                    var timeLeft = _discordSpeakerHintTimeout - (DateTime.UtcNow - _discordSpeakerHintTime);
                    Console.WriteLine($"🎤 Discord speaker hint: {_discordSpeakerHint} (expires in {timeLeft.TotalSeconds:F0}s)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SpeakerIdentifier.ListEnrolledSpeakers failed: {ex.Message}");
            }
        }

        public static void TestRecognitionWithLowerThreshold(float[] embedding)
        {
            Console.WriteLine("🧪 Testing voice recognition with multiple thresholds:");
            var current = (float)(Kinectv1.App.SettingsProvider?.Current?.Audio?.SpeakerMatchMinScore ?? _defaultThreshold);
            Console.WriteLine($"   Current system threshold: {current:F3}");
            
            var thresholds = new float[] { 0.10f, 0.15f, 0.20f, 0.25f, 0.30f, 0.35f, 0.40f, 0.45f, 0.50f };
            
            foreach (var threshold in thresholds)
            {
                var match = MemoryStore.MatchBestVoice(embedding, threshold);
                string indicator = (Math.Abs(threshold - current) < 0.001f) ? " ← CURRENT" : "";
                
                if (match.HasValue)
                {
                    Console.WriteLine($"   Threshold {threshold:F2}: {match.Value.name} (score: {match.Value.score:F3}){indicator}");
                }
                else
                {
                    Console.WriteLine($"   Threshold {threshold:F2}: No match{indicator}");
                }
            }
            
            // Also test Discord hint fallback
            if (HasValidDiscordSpeakerHint())
            {
                Console.WriteLine($"   Discord fallback: {_discordSpeakerHint} (hint-based identification)");
            }
        }

        /// <summary>
        /// Get speaker ID from speaker name (backward compatibility method)
        /// </summary>
        /// <param name="name">The speaker name</param>
        /// <returns>The speaker ID (same as name for now)</returns>
        public static string GetSpeakerId(string name)
        {
            try
            {
                // For now, return the name as the speaker ID
                // This could be enhanced to return a more specific ID in the future
                return name ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ SpeakerIdentifier.GetSpeakerId failed: {ex.Message}");
                return string.Empty;
            }
        }
    }
}