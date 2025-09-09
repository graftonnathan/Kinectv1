using Microsoft.ML.OnnxRuntime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Kinectv1
{
    internal static class OnnxSessionFactory
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private static bool HasOrtExport(string exportName)
        {
            try
            {
                var h = GetModuleHandle("onnxruntime.dll");
                if (h == IntPtr.Zero) h = GetModuleHandle("onnxruntime");
                if (h == IntPtr.Zero) return false;
                return GetProcAddress(h, exportName) != IntPtr.Zero;
            }
            catch { return false; }
        }

        private static void PreloadOrtNative()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidateDirs = new[]
                {
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    Path.Combine(baseDir, "lib", "onnxruntime"),
                    baseDir,
                };

                string PickOrtDll()
                {
                    string selectedWithCuda = null;

                    foreach (var dir in candidateDirs)
                    {
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        var ort = Path.Combine(dir, "onnxruntime.dll");
                        if (!File.Exists(ort)) continue;

                        var h = LoadLibrary(ort);
                        if (h == IntPtr.Zero) continue;
                        var hasCudaExport = GetProcAddress(h, "OrtSessionOptionsAppendExecutionProvider_CUDA") != IntPtr.Zero;
                        FreeLibrary(h);

                        if (hasCudaExport && selectedWithCuda == null) { selectedWithCuda = ort; }
                    }

                    if (!string.IsNullOrEmpty(selectedWithCuda)) return selectedWithCuda;

                    foreach (var dir in candidateDirs)
                    {
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        var ort = Path.Combine(dir, "onnxruntime.dll");
                        if (File.Exists(ort)) return ort;
                    }
                    return null;
                }

                var selected = PickOrtDll();
                if (!string.IsNullOrEmpty(selected))
                {
                    var h = LoadLibrary(selected);
                    if (h != IntPtr.Zero)
                    {
                        Console.WriteLine("[Onnx] Preloaded onnxruntime: " + selected);
                    }
                }
            }
            catch { }
        }

        private static string FindCudaProviderDir()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new List<string>
                {
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    Path.Combine(baseDir, "lib", "onnxruntime"),
                    Path.Combine(baseDir, "lib"),
                    baseDir,
                };
                foreach (var dir in candidates)
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        var cuda = Path.Combine(dir, "onnxruntime_providers_cuda.dll");
                        var shared = Path.Combine(dir, "onnxruntime_providers_shared.dll");
                        if (File.Exists(cuda) && File.Exists(shared)) return dir;
                    }
                    catch { }
                }
                return null;
            }
            catch { return null; }
        }

        private static bool IsCudaProviderAvailable()
        {
            try
            {
                var dir = FindCudaProviderDir();
                if (string.IsNullOrEmpty(dir)) return false;
                var cudaProvider = Path.Combine(dir, "onnxruntime_providers_cuda.dll");
                var shared = Path.Combine(dir, "onnxruntime_providers_shared.dll");
                if (!(File.Exists(cudaProvider) && File.Exists(shared))) return false;
                return true;
            }
            catch { return false; }
        }

        public static InferenceSession Create(string modelPath, bool requestedGpu, out bool usingGpu)
        {
            usingGpu = false;
            PreloadOrtNative();
            var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED };
            try { so.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING; } catch { }

            if (requestedGpu)
            {
                try
                {
                    if (HasOrtExport("OrtSessionOptionsAppendExecutionProvider_CUDA") && IsCudaProviderAvailable())
                    {
#if NETFRAMEWORK
                        so.AppendExecutionProvider_CUDA();
#else
                        so.AppendExecutionProvider_CUDA();
#endif
                        usingGpu = true;
                        Console.WriteLine("[Onnx] EP: CUDA (GPU)");
                    }
                    else
                    {
                        Console.WriteLine("[Onnx] CUDA EP unavailable - using CPU");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Onnx] CUDA EP error: " + ex.Message);
                }
            }

            if (!usingGpu)
            {
                try { so.AppendExecutionProvider_CPU(0); Console.WriteLine("[Onnx] EP: CPU"); } catch { }
            }

            var session = new InferenceSession(modelPath, so);
            Console.WriteLine("[Onnx] Session created.");
            return session;
        }
    }
}
