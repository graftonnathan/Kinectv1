// VoskModelManager.cs - Singleton to manage Vosk model instances safely

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Vosk;

namespace Kinectv1
{
    /// <summary>
    /// Singleton manager for Vosk models to prevent memory corruption from multiple instances
    /// This ensures only one Model instance exists per model path at any time
    /// </summary>
    public static class VoskModelManager
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Model> _models = new Dictionary<string, Model>();
        private static readonly Dictionary<string, int> _referenceCount = new Dictionary<string, int>();
        
        /// <summary>
        /// Get or create a Vosk model instance (thread-safe)
        /// </summary>
        /// <param name="modelPath">Path to the Vosk model directory</param>
        /// <returns>Shared Model instance or null if failed</returns>
        public static Model GetModel(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath) || !Directory.Exists(modelPath))
            {
                Console.WriteLine($"? VoskModelManager: Invalid model path: {modelPath}");
                return null;
            }
            
            lock (_lock)
            {
                try
                {
                    // Normalize path for consistent dictionary keys
                    var normalizedPath = Path.GetFullPath(modelPath);
                    
                    if (_models.ContainsKey(normalizedPath))
                    {
                        // Increment reference count and return existing model
                        _referenceCount[normalizedPath]++;
                        Console.WriteLine($"? VoskModelManager: Reusing existing model for {normalizedPath} (refs: {_referenceCount[normalizedPath]})");
                        return _models[normalizedPath];
                    }
                    
                    // Create new model instance
                    Console.WriteLine($"?? VoskModelManager: Creating new model instance for {normalizedPath}");
                    
                    // Set Vosk log level to minimal to reduce spam
                    Vosk.Vosk.SetLogLevel(-1);
                    
                    var model = new Model(normalizedPath);
                    _models[normalizedPath] = model;
                    _referenceCount[normalizedPath] = 1;
                    
                    Console.WriteLine($"? VoskModelManager: Created model for {normalizedPath} (refs: 1)");
                    return model;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? VoskModelManager: Failed to create model for {modelPath}: {ex.Message}");
                    return null;
                }
            }
        }
        
        /// <summary>
        /// Create a VoskRecognizer using the shared model (thread-safe)
        /// </summary>
        /// <param name="modelPath">Path to the Vosk model directory</param>
        /// <param name="sampleRate">Sample rate (default 16000)</param>
        /// <returns>New VoskRecognizer instance or null if failed</returns>
        public static VoskRecognizer CreateRecognizer(string modelPath, float sampleRate = 16000.0f)
        {
            var model = GetModel(modelPath);
            if (model == null)
            {
                Console.WriteLine($"? VoskModelManager: Cannot create recognizer - model loading failed");
                return null;
            }
            
            try
            {
                var recognizer = new VoskRecognizer(model, sampleRate);
                Console.WriteLine($"? VoskModelManager: Created recognizer with sample rate {sampleRate}");
                return recognizer;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? VoskModelManager: Failed to create recognizer: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Release a reference to a model (call when disposing recognizers)
        /// </summary>
        /// <param name="modelPath">Path to the Vosk model directory</param>
        public static void ReleaseModel(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
                return;
                
            lock (_lock)
            {
                try
                {
                    var normalizedPath = Path.GetFullPath(modelPath);
                    
                    if (_referenceCount.ContainsKey(normalizedPath))
                    {
                        _referenceCount[normalizedPath]--;
                        Console.WriteLine($"?? VoskModelManager: Released reference to {normalizedPath} (refs: {_referenceCount[normalizedPath]})");
                        
                        // Don't dispose the model even if ref count reaches 0
                        // Keep it alive for potential reuse to prevent reload overhead
                        if (_referenceCount[normalizedPath] <= 0)
                        {
                            Console.WriteLine($"?? VoskModelManager: Model {normalizedPath} has no active references but keeping alive for reuse");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? VoskModelManager: Error releasing model reference: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Get current model statistics for debugging
        /// </summary>
        /// <returns>Dictionary of model paths and their reference counts</returns>
        public static Dictionary<string, int> GetModelStats()
        {
            lock (_lock)
            {
                return new Dictionary<string, int>(_referenceCount);
            }
        }
        
        /// <summary>
        /// Force dispose all models (use only on application shutdown)
        /// </summary>
        public static void DisposeAllModels()
        {
            lock (_lock)
            {
                Console.WriteLine($"?? VoskModelManager: Disposing all models ({_models.Count} models)");
                
                foreach (var kvp in _models)
                {
                    try
                    {
                        kvp.Value?.Dispose();
                        Console.WriteLine($"? VoskModelManager: Disposed model {kvp.Key}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"? VoskModelManager: Error disposing model {kvp.Key}: {ex.Message}");
                    }
                }
                
                _models.Clear();
                _referenceCount.Clear();
                Console.WriteLine($"? VoskModelManager: All models disposed");
            }
        }
        
        /// <summary>
        /// Check if a model is already loaded
        /// </summary>
        /// <param name="modelPath">Path to check</param>
        /// <returns>True if model is loaded</returns>
        public static bool IsModelLoaded(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
                return false;
                
            lock (_lock)
            {
                try
                {
                    var normalizedPath = Path.GetFullPath(modelPath);
                    return _models.ContainsKey(normalizedPath);
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}