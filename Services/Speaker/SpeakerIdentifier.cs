using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Kinectv1
{
    public static class SpeakerIdentifier
    {
        private static string _discordHint;
        // name -> list of embeddings
        private static readonly ConcurrentDictionary<string, List<float[]>> _store = new ConcurrentDictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _gate = new object();

        public static void EnrollSpeaker(string name, float[] embedding)
        {
            if (string.IsNullOrWhiteSpace(name) || embedding == null || embedding.Length == 0) return;
            var list = _store.GetOrAdd(name.Trim(), _ => new List<float[]>());
            lock (_gate)
            {
                list.Add((float[])embedding.Clone());
            }
        }

        public static void ListEnrolledSpeakers()
        {
            try
            {
                var all = _store.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                Console.WriteLine($"Speakers enrolled: {all.Length}");
                foreach (var n in all) Console.WriteLine($" - {n} ({_store[n].Count} samples)");
            }
            catch { }
        }

        public static int GetEnrolledSpeakersCount() => _store.Count;

        public static void SetDiscordSpeakerHint(string username)
        {
            _discordHint = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        }

        public static (string name, float score) IdentifyFromEmbedding(float[] embedding)
        {
            if (embedding == null || embedding.Length == 0 || _store.IsEmpty)
                return Identify(); // fallback to hint

            // Threshold from settings pipeline: prefer Current, fallback to effective defaults
            double thresh = 0.6; // placeholder initial
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
            foreach (var kvp in _store)
            {
                var samples = kvp.Value; if (samples == null || samples.Count == 0) continue;
                var center = Mean(samples);
                var sim = Cosine(embedding, center);
                if (sim > best) { best = sim; bestName = kvp.Key; }
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
            return count;
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
