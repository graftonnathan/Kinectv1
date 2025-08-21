// VoskModelManager.cs - Singleton to manage Vosk model instances safely

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Runtime.ExceptionServices;
using System.Security;
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
        private static readonly HashSet<string> _failedModels = new HashSet<string>();
        private static bool _logLevelSet = false;
        
        /// <summary>
        /// Get or create a Vosk model instance (thread-safe)
        /// </summary>
        /// <param name="modelPath">Path to the Vosk model directory</param>
        /// <returns>Shared Model instance or null if failed</returns>
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
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

                    // Prevent repeated attempts if this model failed previously
                    if (_failedModels.Contains(normalizedPath))
                    {
                        Console.WriteLine($"? VoskModelManager: Previous load failed for {normalizedPath} - skipping new attempt");
                        return null;
                    }

                    // Set Vosk log level once per process
                    if (!_logLevelSet)
                    {
                        try { Vosk.Vosk.SetLogLevel(-1); } catch { }
                        _logLevelSet = true;
                    }

                    // Validate model structure before loading to avoid native crashes
                    var confDir = Path.Combine(normalizedPath, "conf");
                    var modelConf = Path.Combine(confDir, "model.conf");
                    if (!Directory.Exists(confDir) || !File.Exists(modelConf))
                    {
                        Console.WriteLine($"? VoskModelManager: '{normalizedPath}' is not a valid Vosk model (missing conf/model.conf). Aborting load.");
                        try { _failedModels.Add(normalizedPath); } catch { }
                        return null;
                    }
                    
                    if (_models.ContainsKey(normalizedPath))
                    {
                        // Increment reference count and return existing model
                        _referenceCount[normalizedPath]++;
                        Console.WriteLine($"? VoskModelManager: Reusing existing model for {normalizedPath} (refs: {_referenceCount[normalizedPath]})");
                        return _models[normalizedPath];
                    }
                    
                    // Create new model instance
                    Console.WriteLine($"?? VoskModelManager: Creating new model instance for {normalizedPath}");
                    
                    var model = new Model(normalizedPath);
                    _models[normalizedPath] = model;
                    _referenceCount[normalizedPath] = 1;
                    
                    Console.WriteLine($"? VoskModelManager: Created model for {normalizedPath} (refs: 1)");
                    return model;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? VoskModelManager: Failed to create model for {modelPath}: {ex.Message}");
                    try { _failedModels.Add(Path.GetFullPath(modelPath)); } catch { }
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
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
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
                try { _failedModels.Add(Path.GetFullPath(modelPath)); } catch { }
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
        /// Force dispose any models with no active references. Models with refs > 0 are kept alive
        /// to avoid AccessViolation from disposing a model still used by recognizers.
        /// </summary>
        public static void DisposeAllModels()
        {
            lock (_lock)
            {
                Console.WriteLine($"?? VoskModelManager: Disposing models with zero references ({_models.Count} total tracked)");
                var toRemove = new List<string>();
                
                foreach (var kvp in _models)
                {
                    var path = kvp.Key;
                    var model = kvp.Value;
                    int refs = 0;
                    _referenceCount.TryGetValue(path, out refs);

                    if (refs <= 0)
                    {
                        try
                        {
                            model?.Dispose();
                            Console.WriteLine($"? VoskModelManager: Disposed model {path}");
                            toRemove.Add(path);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"? VoskModelManager: Error disposing model {path}: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"?? VoskModelManager: Skipping dispose of {path} (refs: {refs})");
                    }
                }

                // Remove disposed entries only
                foreach (var path in toRemove)
                {
                    _models.Remove(path);
                    _referenceCount.Remove(path);
                }

                Console.WriteLine($"? VoskModelManager: Dispose complete. Remaining loaded models: {_models.Count}");
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