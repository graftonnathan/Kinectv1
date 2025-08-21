using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.IO;
using System.Linq;

public static class SpeakerEmbedder
{
    private static InferenceSession? _model;
    private static bool _usingGpu = false;
    private static string _lastModelPath = null;

    public static void Load(string modelPath)
    {
        try
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Speaker model not found: {modelPath}");

            // Dispose previous session if any before recreating
            try { _model?.Dispose(); } catch { }

            // TEMP: Force CPU for isolation (avoid GPU EP for embedder)
            var requestedGpu = false;
            _model = Kinectv1.OnnxSessionFactory.Create(modelPath, requestedGpu, out _usingGpu);
            _lastModelPath = modelPath;

            var meta = _model.InputMetadata;
            Console.WriteLine($"SpeakerEmbedder loaded (FORCED CPU) - {modelPath}");
            Console.WriteLine("Speaker model input nodes:");
            foreach (var name in meta.Keys)
            {
                Console.WriteLine($" - {name}: shape = [{string.Join(",", meta[name].Dimensions)}]");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load SpeakerEmbedder: {ex.Message}");
            throw;
        }
    }

    public static bool ReloadFromSettings()
    {
        try
        {
            var path = Kinectv1.AppSettings.LoadSpeakerEmbeddingModelPath();
            if (string.IsNullOrWhiteSpace(path)) return false;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path);
            if (!File.Exists(fullPath)) return false;
            Load(fullPath);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeakerEmbedder reload failed: {ex.Message}");
            return false;
        }
    }

    public static float[] Embed(float[] pcm)
    {
        if (_model == null)
            throw new InvalidOperationException("SpeakerEmbedder not initialized. Call Load() first.");

        try
        {
            var input = new DenseTensor<float>(new[] { 1, 1, pcm.Length });
            for (int i = 0; i < pcm.Length; i++) input[0, 0, i] = pcm[i];

            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor(_model.InputMetadata.Keys.First(), input)
            };

            using var results = _model.Run(inputs);
            var output = results.First().AsEnumerable<float>().ToArray();
            Console.WriteLine($"🔊 Generated speaker embedding of size: {output.Length}");
            return output;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Speaker embedding failed: {ex.Message}");
            return new float[512];
        }
    }

    public static bool IsUsingGpu => _usingGpu;
    public static bool IsLoaded => _model != null;
}



