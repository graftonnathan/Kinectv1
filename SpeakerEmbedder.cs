using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.IO;
using System.Linq;

public static class SpeakerEmbedder
{
    private static InferenceSession? _model;
    private static bool _usingGpu = false;

    public static void Load(string modelPath)
    {
        try
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Speaker model not found: {modelPath}");

            // TEMPORARY: Use CPU-only to avoid CUDA issues
            _model = CreateCpuOnlySession(modelPath);
            
            var meta = _model.InputMetadata;
            Console.WriteLine($"✅ SpeakerEmbedder loaded (CPU-ONLY) - {modelPath}");
            Console.WriteLine("Speaker model input nodes:");
            foreach (var name in meta.Keys)
            {
                Console.WriteLine($" - {name}: shape = [{string.Join(",", meta[name].Dimensions)}]");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to load SpeakerEmbedder: {ex.Message}");
            throw;
        }
    }

    private static InferenceSession CreateCpuOnlySession(string modelPath)
    {
        // CPU-only for maximum stability
        var cpuOptions = new SessionOptions();
        cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        
        var session = new InferenceSession(modelPath, cpuOptions);
        _usingGpu = false;
        Console.WriteLine("💻 SpeakerEmbedder using CPU (stable mode)");
        return session;
    }

    public static float[] Embed(float[] pcm)
    {
        if (_model == null)
            throw new InvalidOperationException("SpeakerEmbedder not initialized. Call Load() first.");

        try
        {
            // Pyannote model expects 3D tensor: [batch_size, channels, samples]
            // For mono audio: [1, 1, samples]
            var input = new DenseTensor<float>(new[] { 1, 1, pcm.Length });
            
            // Copy PCM data into the tensor
            for (int i = 0; i < pcm.Length; i++)
            {
                input[0, 0, i] = pcm[i];
            }

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
            Console.WriteLine($"⚠️ Speaker embedding failed: {ex.Message}");
            return new float[512]; // Return dummy embedding as fallback
        }
    }

    public static bool IsUsingGpu => _usingGpu; // Always false for now
}



