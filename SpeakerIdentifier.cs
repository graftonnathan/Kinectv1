//SpeakerIdentifier.cs
using System;

public static class SpeakerIdentifier
{
    private static float _defaultThreshold = 0.40f; // Default threshold

    public static (string name, float score)? Identify(float[] embedding)
    {
        try
        {
            if (embedding == null || embedding.Length == 0)
            {
                Console.WriteLine("⚠️ SpeakerIdentifier: Invalid embedding provided");
                return null;
            }

            // Debug: Show current threshold being used
            Console.WriteLine($"🔍 Checking voice embedding (size: {embedding.Length}) with threshold: {_defaultThreshold:F3}");

            var match = MemoryStore.MatchBestVoice(embedding, thresholdCos: _defaultThreshold);
            
            if (match.HasValue)
            {
                Console.WriteLine($"🎤 Speaker identified: {match.Value.name} (confidence: {match.Value.score:F3}) [Threshold: {_defaultThreshold:F3}]");
                return match;
            }
            else
            {
                // Enhanced debugging - show best scores even below threshold
                var bestMatch = MemoryStore.GetBestVoiceMatchWithScores(embedding);
                if (bestMatch.HasValue)
                {
                    Console.WriteLine($"🎤 Best match below threshold: {bestMatch.Value.name} (score: {bestMatch.Value.score:F3}, threshold: {_defaultThreshold:F3})");
                    Console.WriteLine($"     Difference: {(_defaultThreshold - bestMatch.Value.score):F3} (need to lower threshold by this amount)");
                }
                else
                {
                    Console.WriteLine($"🎤 No enrolled speakers found to compare against");
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

    public static void SetDefaultThreshold(float threshold)
    {
        if (threshold >= 0.0f && threshold <= 1.0f)
        {
            var oldThreshold = _defaultThreshold;
            _defaultThreshold = threshold;
            Console.WriteLine($"🔧 Default voice threshold changed: {oldThreshold:F3} → {threshold:F3}");
        }
        else
        {
            Console.WriteLine($"⚠️ Invalid threshold {threshold:F3}, must be between 0.0 and 1.0");
        }
    }

    public static float GetDefaultThreshold()
    {
        return _defaultThreshold;
    }

    public static void EnrollSpeaker(string name, float[] embedding)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("⚠️ SpeakerIdentifier: Invalid name provided for enrollment");
                return;
            }

            if (embedding == null || embedding.Length == 0)
            {
                Console.WriteLine("⚠️ SpeakerIdentifier: Invalid embedding provided for enrollment");
                return;
            }

            MemoryStore.AddVoiceEmbedding(name, embedding);
            Console.WriteLine($"🎤 Speaker enrolled: {name} (embedding size: {embedding.Length})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ SpeakerIdentifier.EnrollSpeaker failed: {ex.Message}");
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
            Console.WriteLine($"❌ SpeakerIdentifier.GetEnrolledSpeakersCount failed: {ex.Message}");
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
            Console.WriteLine($"🔧 Current system threshold: {_defaultThreshold:F3}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ SpeakerIdentifier.ListEnrolledSpeakers failed: {ex.Message}");
        }
    }

    public static void TestRecognitionWithLowerThreshold(float[] embedding)
    {
        Console.WriteLine("🧪 Testing voice recognition with multiple thresholds:");
        Console.WriteLine($"   Current system threshold: {_defaultThreshold:F3}");
        
        var thresholds = new float[] { 0.10f, 0.15f, 0.20f, 0.25f, 0.30f, 0.35f, 0.40f, 0.45f, 0.50f };
        
        foreach (var threshold in thresholds)
        {
            var match = MemoryStore.MatchBestVoice(embedding, threshold);
            string indicator = (Math.Abs(threshold - _defaultThreshold) < 0.001f) ? " ← CURRENT" : "";
            
            if (match.HasValue)
            {
                Console.WriteLine($"   Threshold {threshold:F2}: {match.Value.name} (score: {match.Value.score:F3}){indicator}");
            }
            else
            {
                Console.WriteLine($"   Threshold {threshold:F2}: No match{indicator}");
            }
        }
    }
}