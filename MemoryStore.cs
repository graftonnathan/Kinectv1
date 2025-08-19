using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1
{
    public class PersonMemory
    {
        public string Name { get; set; } = "";
        public List<string> Snapshots { get; set; } = new List<string>();
        public List<float[]> FaceEmbeddings { get; set; } = new List<float[]>(); // Renamed for clarity
        public List<float[]> VoiceEmbeddings { get; set; } = new List<float[]>(); // Added voice embeddings
    }

    public class MemoryIndex
    {
        public List<PersonMemory> People { get; set; } = new List<PersonMemory>();
    }

    public static class MemoryStore
    {
        private static readonly string DataRoot =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        private static readonly string FacesRoot =
            Path.Combine(DataRoot, "Faces");
        private static readonly string VoicesRoot =
            Path.Combine(DataRoot, "Voices");
        private static readonly string MemoryFile =
            Path.Combine(DataRoot, "memory.json");

        private static MemoryIndex _index = new MemoryIndex();

        public static void Init()
        {
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(FacesRoot);
            Directory.CreateDirectory(VoicesRoot);

            if (File.Exists(MemoryFile))
            {
                try
                {
                    var json = File.ReadAllText(MemoryFile);
                    _index = JsonConvert.DeserializeObject<MemoryIndex>(json) ?? new MemoryIndex();
                    
                    // Migration: Convert old Embeddings to FaceEmbeddings
                    foreach (var person in _index.People)
                    {
                        // Handle legacy data structure
                        if (person.FaceEmbeddings.Count == 0 && HasLegacyEmbeddings(json, person.Name))
                        {
                            MigrateLegacyEmbeddings(person, json);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load memory file: {ex.Message}");
                    _index = new MemoryIndex();
                }
            }
            else
            {
                _index = new MemoryIndex();
                Save();
            }
            Console.WriteLine("MemoryStore initialized");
        }

        private static bool HasLegacyEmbeddings(string json, string name)
        {
            // Simple check for legacy "Embeddings" field
            return json.Contains("\"Embeddings\"");
        }

        private static void MigrateLegacyEmbeddings(PersonMemory person, string json)
        {
            try
            {
                // Parse legacy format and migrate to FaceEmbeddings using Newtonsoft.Json
                var jsonObject = JObject.Parse(json);
                var people = jsonObject["People"] as JArray;
                
                if (people != null)
                {
                    foreach (var p in people)
                    {
                        if (p["Name"]?.ToString() == person.Name)
                        {
                            var embeddings = p["Embeddings"] as JArray;
                            if (embeddings != null)
                            {
                                foreach (var emb in embeddings)
                                {
                                    var embArray = emb.ToObject<float[]>();
                                    if (embArray != null)
                                    {
                                        person.FaceEmbeddings.Add(embArray);
                                    }
                                }
                            }
                            break;
                        }
                    }
                }
                Console.WriteLine($"Migrated legacy embeddings for {person.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to migrate embeddings for {person.Name}: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_index, Formatting.Indented);
                File.WriteAllText(MemoryFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save memory file: {ex.Message}");
            }
        }

        // Face-related methods (updated to use FaceEmbeddings)
        public static void AddEmbedding(string name, float[] embedding)
        {
            AddFaceEmbedding(name, embedding);
        }

        public static void AddFaceEmbedding(string name, float[] embedding)
        {
            var person = GetOrCreatePerson(name);
            person.FaceEmbeddings.Add(embedding);
            Save();
            Console.WriteLine($"Added face embedding for {name}. Total face embeddings: {person.FaceEmbeddings.Count}");
        }

        // Voice-related methods
        public static void AddVoiceEmbedding(string name, float[] embedding)
        {
            var person = GetOrCreatePerson(name);
            person.VoiceEmbeddings.Add(embedding);
            Save();
            Console.WriteLine($"Added voice embedding for {name}. Total: {person.VoiceEmbeddings.Count}");
        }

        // New method to flush all voice embeddings
        public static int FlushAllVoiceEmbeddings()
        {
            try
            {
                int totalRemoved = 0;
                foreach (var person in _index.People)
                {
                    totalRemoved += person.VoiceEmbeddings.Count;
                    person.VoiceEmbeddings.Clear();
                }

                // Remove people who now have no data at all
                _index.People.RemoveAll(p => p.VoiceEmbeddings.Count == 0 && p.FaceEmbeddings.Count == 0 && p.Snapshots.Count == 0);

                Save();
                Console.WriteLine($"Flushed {totalRemoved} voice embeddings from all speakers");
                
                // Also clean up voice folders
                try
                {
                    if (Directory.Exists(VoicesRoot))
                    {
                        Directory.Delete(VoicesRoot, true);
                        Directory.CreateDirectory(VoicesRoot);
                        Console.WriteLine("Cleaned up voice storage folders");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not clean voice folders: {ex.Message}");
                }

                return totalRemoved;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to flush voice embeddings: {ex.Message}");
                return 0;
            }
        }

        // Method to flush voice embeddings for a specific person
        public static int FlushVoiceEmbeddings(string name)
        {
            try
            {
                var person = _index.People.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (person == null)
                {
                    Console.WriteLine($"No person found with name: {name}");
                    return 0;
                }

                int removed = person.VoiceEmbeddings.Count;
                person.VoiceEmbeddings.Clear();

                // Remove person if they have no data left
                if (person.FaceEmbeddings.Count == 0 && person.Snapshots.Count == 0)
                {
                    _index.People.Remove(person);
                    Console.WriteLine($"🗑️ Removed person '{name}' completely (no remaining data)");
                }

                Save();
                Console.WriteLine($"🗑️ Flushed {removed} voice embeddings for '{name}'");
                return removed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to flush voice embeddings for '{name}': {ex.Message}");
                return 0;
            }
        }

        public static (string name, float score)? MatchBestFace(float[] queryEmb, float thresholdCos = 0.45f)
        {
            string best = null;
            float bestCos = -1f;

            foreach (var p in _index.People)
            {
                foreach (var e in p.FaceEmbeddings)
                {
                    var cos = Cosine(queryEmb, e);
                    if (cos > bestCos)
                    {
                        bestCos = cos;
                        best = p.Name;
                    }
                }
            }

            if (best != null && bestCos >= thresholdCos)
                return (best, bestCos);

            return null;
        }

        public static (string name, float score)? MatchBestVoice(float[] queryEmb, float thresholdCos = 0.40f)
        {
            string best = null;
            float bestCos = -1f;

            int totalVoiceEmbeddings = 0;
            foreach (var p in _index.People)
            {
                totalVoiceEmbeddings += p.VoiceEmbeddings.Count;
                foreach (var e in p.VoiceEmbeddings)
                {
                    var cos = Cosine(queryEmb, e);
                    
                    if (cos > bestCos)
                    {
                        bestCos = cos;
                        best = p.Name;
                    }
                }
            }

            if (best != null && bestCos >= thresholdCos)
                return (best, bestCos);

            return null;
        }

        // New method for debugging - get best match regardless of threshold
        public static (string name, float score)? GetBestVoiceMatchWithScores(float[] queryEmb)
        {
            string best = null;
            float bestCos = -1f;

            foreach (var p in _index.People)
            {
                foreach (var e in p.VoiceEmbeddings)
                {
                    var cos = Cosine(queryEmb, e);
                    if (cos > bestCos)
                    {
                        bestCos = cos;
                        best = p.Name;
                    }
                }
            }

            if (best != null)
                return (best, bestCos);

            return null;
        }

        // Legacy method for backward compatibility
        public static (string name, float score)? MatchBest(float[] queryEmb, float thresholdCos = 0.45f)
        {
            return MatchBestFace(queryEmb, thresholdCos);
        }

        private static PersonMemory GetOrCreatePerson(string name)
        {
            var person = _index.People.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (person == null)
            {
                person = new PersonMemory { Name = name };
                _index.People.Add(person);
            }
            return person;
        }

        private static float Cosine(float[] a, float[] b)
        {
            if (a.Length != b.Length)
            {
                Console.WriteLine($"Embedding dimension mismatch: {a.Length} vs {b.Length}");
                return 0f;
            }

            float dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            
            var norm = (float)(Math.Sqrt(na) * Math.Sqrt(nb)) + 1e-9f;
            return dot / norm;
        }

        public static string EnsurePersonFolder(string name)
        {
            var safe = MakeSafeName(name);
            var folder = Path.Combine(FacesRoot, safe);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static string EnsurePersonVoiceFolder(string name)
        {
            var safe = MakeSafeName(name);
            var folder = Path.Combine(VoicesRoot, safe);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static void AddSnapshot(string name, string relativePath)
        {
            var person = GetOrCreatePerson(name);
            if (!person.Snapshots.Contains(relativePath))
                person.Snapshots.Add(relativePath);

            Save();
        }

        public static List<PersonMemory> GetAllPeople()
        {
            return new List<PersonMemory>(_index.People);
        }

        public static PersonMemory GetPerson(string name)
        {
            return _index.People.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static string MakeSafeName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}

















