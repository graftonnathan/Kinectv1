using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Kinectv1
{
    public static class SpeakerIdentifier
    {
        private static string _discordHint;
        // name -> list of embeddings
        private static readonly ConcurrentDictionary<string, List<float[]>> _store = new ConcurrentDictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _gate = new object();
        
        private static string _enrollmentsPath;
        private static bool _initialized = false;

        /// <summary>
        /// Initialize the speaker identifier and load any saved enrollments.
        /// Call this after App.SettingsProvider is ready.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            
            try
            {
                // Store enrollments in the app data folder alongside settings
                var appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Kinectv1");
                Directory.CreateDirectory(appDataPath);
                _enrollmentsPath = Path.Combine(appDataPath, "speaker_enrollments.json");
                
                LoadEnrollments();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeakerIdentifier] Initialize error: {ex.Message}");
            }
        }

        public static void EnrollSpeaker(string name, float[] embedding)
        {
            if (string.IsNullOrWhiteSpace(name) || embedding == null || embedding.Length == 0) return;
            
            // Ensure initialized
            if (!_initialized) Initialize();
            
            var list = _store.GetOrAdd(name.Trim(), _ => new List<float[]>());
            lock (_gate)
            {
                list.Add((float[])embedding.Clone());
            }
            
            // Auto-save after enrollment
            try { SaveEnrollments(); } catch { }
        }

        public static void ListEnrolledSpeakers()
        {
            // Ensure initialized
            if (!_initialized) Initialize();
            
            try
            {
                var all = _store.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                Console.WriteLine($"Speakers enrolled: {all.Length}");
                foreach (var n in all) Console.WriteLine($" - {n} ({_store[n].Count} samples)");
            }
            catch { }
        }

        public static int GetEnrolledSpeakersCount()
        {
            if (!_initialized) Initialize();
            return _store.Count;
        }

        public static void SetDiscordSpeakerHint(string username)
        {
            _discordHint = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        }

        public static (string name, float score) IdentifyFromEmbedding(float[] embedding)
        {
            // Ensure initialized
            if (!_initialized) Initialize();
            
            if (embedding == null || embedding.Length == 0 || _store.IsEmpty)
                return Identify(); // fallback to hint

            // Threshold from settings pipeline: prefer Current, fallback to effective defaults
            // LOWERED default from 0.75 to 0.35 - cross-source (mic vs WebRTC) matching produces lower scores
            double thresh = 0.35;
            try
            {
                var curr = App.SettingsProvider?.Current?.Audio?.SpeakerMatchMinScore;
                if (curr.HasValue) thresh = curr.Value;
                else
                {
                    var def = App.SettingsProvider?.GetDefaultsEffective()?.Audio?.SpeakerMatchMinScore;
                    if (def.HasValue) thresh = def.Value;
                }
            }
            catch { }

            string bestName = null; double best = -1.0;
            string secondBestName = null; double secondBest = -1.0;
            
            foreach (var kvp in _store)
            {
                var samples = kvp.Value; if (samples == null || samples.Count == 0) continue;
                var center = Mean(samples);
                var sim = Cosine(embedding, center);
                
                if (sim > best) 
                { 
                    secondBest = best;
                    secondBestName = bestName;
                    best = sim; 
                    bestName = kvp.Key; 
                }
                else if (sim > secondBest)
                {
                    secondBest = sim;
                    secondBestName = kvp.Key;
                }
            }

            // Log enrolled speaker matching for diagnostics
            if (bestName != null && best >= 0)
            {
                var accepted = best >= thresh;
                var margin = secondBestName != null ? (best - secondBest) : best;
                Console.WriteLine($"[EnrolledSpeaker] Best={bestName}({best:F3}) second={secondBestName ?? "none"}({secondBest:F3}) thr={thresh:F2} accepted={accepted}");
            }

            if (bestName == null || best < thresh)
                return ("UnknownSpeaker", (float)Math.Max(0, best));
            return (bestName, (float)Math.Min(1.0, best));
        }

        // Minimal hint-only path retained
        public static (string name, float score) Identify()
        {
            if (!string.IsNullOrWhiteSpace(_discordHint))
                return (_discordHint, 1.0f);
            return ("UnknownSpeaker", 0f);
        }

        public static int ClearAll()
        {
            var count = _store.Count;
            _store.Clear();
            
            // Delete the saved file
            try
            {
                if (!string.IsNullOrEmpty(_enrollmentsPath) && File.Exists(_enrollmentsPath))
                {
                    File.Delete(_enrollmentsPath);
                    Console.WriteLine("[SpeakerIdentifier] Enrollments file deleted");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeakerIdentifier] Failed to delete enrollments file: {ex.Message}");
            }
            
            return count;
        }

        /// <summary>
        /// Save all enrolled speakers to disk.
        /// </summary>
        public static void SaveEnrollments()
        {
            if (string.IsNullOrEmpty(_enrollmentsPath)) return;
            
            try
            {
                var data = new Dictionary<string, List<float[]>>();
                foreach (var kvp in _store)
                {
                    lock (_gate)
                    {
                        data[kvp.Key] = kvp.Value.ToList();
                    }
                }
                
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                // Atomic write
                var tempPath = _enrollmentsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _enrollmentsPath, overwrite: true);
                
                Console.WriteLine($"[SpeakerIdentifier] Saved {data.Count} enrolled speakers to {_enrollmentsPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeakerIdentifier] Failed to save enrollments: {ex.Message}");
            }
        }

        /// <summary>
        /// Load enrolled speakers from disk.
        /// </summary>
        private static void LoadEnrollments()
        {
            if (string.IsNullOrEmpty(_enrollmentsPath) || !File.Exists(_enrollmentsPath)) return;
            
            try
            {
                var json = File.ReadAllText(_enrollmentsPath);
                var data = JsonSerializer.Deserialize<Dictionary<string, List<float[]>>>(json);
                
                if (data == null || data.Count == 0)
                {
                    Console.WriteLine("[SpeakerIdentifier] No enrollments found in file");
                    return;
                }
                
                foreach (var kvp in data)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null) continue;
                    
                    var list = _store.GetOrAdd(kvp.Key.Trim(), _ => new List<float[]>());
                    lock (_gate)
                    {
                        foreach (var emb in kvp.Value)
                        {
                            if (emb != null && emb.Length > 0)
                                list.Add(emb);
                        }
                    }
                }
                
                Console.WriteLine($"[SpeakerIdentifier] Loaded {data.Count} enrolled speakers from {_enrollmentsPath}");
                ListEnrolledSpeakers();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeakerIdentifier] Failed to load enrollments: {ex.Message}");
            }
        }

        private static float[] Mean(List<float[]> vecs)
        {
            int d = vecs[0].Length;
            var acc = new double[d];
            foreach (var v in vecs)
            {
                for (int i = 0; i < d; i++) acc[i] += v[i];
            }
            var m = new float[d];
            double inv = 1.0 / vecs.Count;
            for (int i = 0; i < d; i++) m[i] = (float)(acc[i] * inv);
            return m;
        }

        private static double Cosine(float[] a, float[] b)
        {
            if (a.Length != b.Length) return -1.0;
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++) { var x = a[i]; var y = b[i]; dot += x * y; na += x * x; nb += y * y; }
            if (na == 0 || nb == 0) return -1.0;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }
    }
}
