using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

public sealed class ArcFaceEmbedder : IDisposable
{
    private InferenceSession _arcface;
    private string _inputName;
    private string _outputName;
    private bool _usingGpu = false;

    public ArcFaceEmbedder(string modelPath, int cudaDeviceId = 0)
    {
        try
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("ArcFace model not found", modelPath);

            // TEMPORARY: Use CPU-only to avoid CUDA issues
            _arcface = CreateCpuOnlySession(modelPath);

            _inputName = _arcface.InputMetadata.Keys.First();
            _outputName = _arcface.OutputMetadata.Keys.First();
            
            Console.WriteLine($"ArcFace loaded (CPU-ONLY) - {modelPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ArcFaceEmbedder failed to load: {ex.Message}");
            throw;
        }
    }

    private InferenceSession CreateCpuOnlySession(string modelPath)
    {
        // CPU-only for maximum stability
        var cpuOptions = new SessionOptions();
        cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        
        var session = new InferenceSession(modelPath, cpuOptions);
        _usingGpu = false;
        Console.WriteLine("💻 ArcFace using CPU (stable mode)");
        return session;
    }

    public float[] Embed(float[] chw112)
    {
        try
        {
            if (chw112.Length != 3 * 112 * 112)
                throw new ArgumentException("Input must be 3x112x112 CHW float array.");

            var input = new DenseTensor<float>(new[] { 1, 3, 112, 112 });
            for (int i = 0; i < chw112.Length; i++) 
                input.Buffer.Span[i] = chw112[i];

            using var results = _arcface.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
            var output = results.First(v => v.Name == _outputName).AsTensor<float>().ToArray();

            // L2 normalize
            float sum = 0f;
            for (int i = 0; i < output.Length; i++) sum += output[i] * output[i];
            float norm = (float)Math.Sqrt(sum) + 1e-9f; // Use Math.Sqrt instead of MathF.Sqrt for .NET Framework
            for (int i = 0; i < output.Length; i++) output[i] /= norm;

            return output;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ArcFace embedding failed: {ex.Message}");
            return new float[512]; // Return dummy embedding as fallback
        }
    }

    public bool IsUsingGpu => _usingGpu; // Always false for now

    public void Dispose() => _arcface?.Dispose();
}
