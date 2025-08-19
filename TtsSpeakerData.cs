using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kinectv1
{
    /// <summary>
    /// Data structure for TTS speaker information from VCTK dataset
    /// </summary>
    public class TtsSpeakerInfo
    {
        public int RefId { get; set; }          // REF ID that the model expects
        public int SpeakerId { get; set; }      // Speaker ID from VCTK
        public int Age { get; set; }            // Age of speaker
        public string Gender { get; set; }      // M/F
        public string Accent { get; set; }      // Accent/nationality
        public string Region { get; set; }      // Specific region
        
        /// <summary>
        /// Display text for dropdown - combines key info
        /// </summary>
        public string DisplayText => $"{SpeakerId} - {Age} {Gender} {Accent} {Region}".Trim();
    }

    /// <summary>
    /// Static class for loading and managing TTS speaker data
    /// </summary>
    public static class TtsSpeakerData
    {
        private static List<TtsSpeakerInfo> _speakers = null;
        
        /// <summary>
        /// Load speakers from SpeakerList.txt file
        /// </summary>
        public static List<TtsSpeakerInfo> LoadSpeakers()
        {
            if (_speakers != null)
                return _speakers;
                
            try
            {
                // Try multiple possible paths for the SpeakerList.txt file
                var possiblePaths = new[]
                {
                    Path.Combine("models", "tts", "SpeakerList.txt"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "tts", "SpeakerList.txt"),
                    Path.Combine(Environment.CurrentDirectory, "models", "tts", "SpeakerList.txt"),
                    "SpeakerList.txt",
                    Path.Combine("tts", "SpeakerList.txt")
                };
                
                string speakerFile = null;
                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        speakerFile = path;
                        break;
                    }
                }
                
                if (string.IsNullOrEmpty(speakerFile))
                {
                    Console.WriteLine($"? SpeakerList.txt not found in expected locations");
                    return new List<TtsSpeakerInfo>();
                }

                var lines = File.ReadAllLines(speakerFile);
                _speakers = new List<TtsSpeakerInfo>();

                // Skip header line
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        // Split by tab - DO NOT filter out empty parts, we need to preserve column positions
                        var parts = line.Split('\t');
                        
                        // Trim each part but keep the array structure intact
                        for (int j = 0; j < parts.Length; j++)
                        {
                            parts[j] = parts[j].Trim();
                        }
                        
                        // Need at least 6 columns: REF, SPEAKER, AGE, GENDER, ACCENTS, REGION
                        if (parts.Length >= 6)
                        {
                            var refStr = parts[0];
                            var speakerStr = parts[1];
                            var ageStr = parts[2];
                            var genderStr = parts[3];
                            var accentStr = parts[4];
                            var regionStr = parts[5];
                            
                            // Validate required fields
                            if (int.TryParse(refStr, out int refId) && int.TryParse(speakerStr, out int speakerId))
                            {
                                var speaker = new TtsSpeakerInfo
                                {
                                    RefId = refId,
                                    SpeakerId = speakerId,
                                    Age = int.TryParse(ageStr, out int age) ? age : 0,
                                    Gender = string.IsNullOrEmpty(genderStr) ? "Unknown" : genderStr,
                                    Accent = string.IsNullOrEmpty(accentStr) ? "Unknown" : accentStr,
                                    Region = regionStr // Can be empty
                                };
                                
                                _speakers.Add(speaker);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"?? Failed to parse speaker line {i}: {ex.Message}");
                    }
                }

                Console.WriteLine($"?? Loaded {_speakers.Count} speakers from SpeakerList.txt");
                
                return _speakers;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Failed to load speakers: {ex.Message}");
                return new List<TtsSpeakerInfo>();
            }
        }

        /// <summary>
        /// Get all loaded speakers
        /// </summary>
        public static List<TtsSpeakerInfo> GetAllSpeakers()
        {
            return LoadSpeakers();
        }

        /// <summary>
        /// Find speaker by REF ID
        /// </summary>
        public static TtsSpeakerInfo FindByRefId(int refId)
        {
            return LoadSpeakers().FirstOrDefault(s => s.RefId == refId);
        }

        /// <summary>
        /// Find speaker by Speaker ID
        /// </summary>
        public static TtsSpeakerInfo FindBySpeakerId(int speakerId)
        {
            return LoadSpeakers().FirstOrDefault(s => s.SpeakerId == speakerId);
        }

        /// <summary>
        /// Get speakers by gender
        /// </summary>
        public static List<TtsSpeakerInfo> GetByGender(string gender)
        {
            return LoadSpeakers().Where(s => s.Gender.Equals(gender, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Get speakers by accent
        /// </summary>
        public static List<TtsSpeakerInfo> GetByAccent(string accent)
        {
            return LoadSpeakers().Where(s => s.Accent.IndexOf(accent, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        /// <summary>
        /// Clear cached speaker data (for reloading)
        /// </summary>
        public static void ClearCache()
        {
            _speakers = null;
        }

        /// <summary>
        /// Test speaker data loading for debugging
        /// </summary>
        public static void TestSpeakerLoading()
        {
            try
            {
                Console.WriteLine("?? Testing TTS speaker data loading...");
                
                // Clear cache first
                ClearCache();
                
                // Test file existence
                var speakerFile = Path.Combine("models", "tts", "SpeakerList.txt");
                if (File.Exists(speakerFile))
                {
                    // Test loading
                    var speakers = LoadSpeakers();
                    Console.WriteLine($"? Loaded {speakers.Count} speakers");
                    
                    if (speakers.Count > 0)
                    {
                        Console.WriteLine("?? First 3 speakers:");
                        for (int i = 0; i < Math.Min(3, speakers.Count); i++)
                        {
                            var s = speakers[i];
                            Console.WriteLine($"   {i + 1}. RefId:{s.RefId}, SpeakerId:{s.SpeakerId}, {s.Gender}, {s.Accent}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"? File not found: {speakerFile}");
                }
                
                Console.WriteLine("? Speaker loading test completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Speaker loading test failed: {ex.Message}");
            }
        }
    }
}