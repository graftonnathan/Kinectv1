using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.IO;
using System.Linq;

/// <summary>
/// Ring buffer for managing rolling audio windows
/// </summary>
public class RingBuffer
{
    private readonly float[] _buffer;
    private readonly int _size;
    private int _writeIndex = 0;
    private int _count = 0;

    public RingBuffer(int size)
    {
        _size = size;
        _buffer = new float[size];
    }

    public void Add(float[] samples)
    {
        foreach (var sample in samples)
        {
            _buffer[_writeIndex] = sample;
            _writeIndex = (_writeIndex + 1) % _size;
            if (_count < _size) _count++;
        }
    }

    public bool HasFullWindow => _count >= _size;

    public float[] ExtractWindow()
    {
        if (!HasFullWindow) return null;

        var result = new float[_size];
        int readIndex = (_writeIndex - _size + _size) % _size;
        
        for (int i = 0; i < _size; i++)
        {
            result[i] = _buffer[readIndex];
            readIndex = (readIndex + 1) % _size;
        }
        
        return result;
    }

    public float CalculateRms()
    {
        if (_count == 0) return 0f;
        
        float sum = 0f;
        int samplesUsed = Math.Min(_count, _size);
        int readIndex = (_writeIndex - samplesUsed + _size) % _size;
        
        for (int i = 0; i < samplesUsed; i++)
        {
            float sample = _buffer[readIndex];
            sum += sample * sample;
            readIndex = (readIndex + 1) % _size;
        }
        
        return (float)Math.Sqrt(sum / samplesUsed);
    }
}

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

            // Request GPU via shared factory (falls back to CPU if CUDA EP not available)
            var requestedGpu = true;
            _model = Kinectv1.OnnxSessionFactory.Create(modelPath, requestedGpu, out _usingGpu);
            _lastModelPath = modelPath;

            var meta = _model.InputMetadata;
            Console.WriteLine($"SpeakerEmbedder loaded ({(_usingGpu ? "GPU" : "CPU")}) - {modelPath}");
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
            // Normalize RMS and clamp peaks as required by the task
            var normalizedPcm = NormalizeAndClampAudio(pcm);
            
            var input = new DenseTensor<float>(new[] { 1, 1, normalizedPcm.Length });
            for (int i = 0; i < normalizedPcm.Length; i++) input[0, 0, i] = normalizedPcm[i];

            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor(_model.InputMetadata.Keys.First(), input)
            };

            using var results = _model.Run(inputs);
            var output = results.First().AsEnumerable<float>().ToArray();
            
            // Add telemetry as required
            Kinectv1.Telemetry.Counter("speaker.infer");
            
            Console.WriteLine($"🔊 Generated speaker embedding of size: {output.Length}");
            return output;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Speaker embedding failed: {ex.Message}");
            return new float[512];
        }
    }

    /// <summary>
    /// Normalize RMS and clamp peaks for speaker recognition
    /// </summary>
    private static float[] NormalizeAndClampAudio(float[] pcm)
    {
        if (pcm == null || pcm.Length == 0) return pcm;

        // Calculate RMS
        float sum = 0f;
        for (int i = 0; i < pcm.Length; i++)
        {
            sum += pcm[i] * pcm[i];
        }
        float rms = (float)Math.Sqrt(sum / pcm.Length);

        // Normalize to target RMS if current RMS is too low
        const float targetRms = 0.1f; // Target RMS level
        const float minRms = 0.001f;  // Minimum RMS to avoid division by zero
        
        var result = new float[pcm.Length];
        
        if (rms > minRms)
        {
            float scale = targetRms / rms;
            // Apply normalization and peak clamping
            for (int i = 0; i < pcm.Length; i++)
            {
                float normalized = pcm[i] * scale;
                // Clamp peaks to [-1.0, 1.0]
                result[i] = Math.Max(-1.0f, Math.Min(1.0f, normalized));
            }
        }
        else
        {
            // If RMS is too low, just copy and clamp
            for (int i = 0; i < pcm.Length; i++)
            {
                result[i] = Math.Max(-1.0f, Math.Min(1.0f, pcm[i]));
            }
        }

        return result;
    }

    public static bool IsUsingGpu => _usingGpu;
    public static bool IsLoaded => _model != null;
}





