using Microsoft.ML.OnnxRuntime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;

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
                    // Prefer NuGet runtimes location first (contains GPU-capable ORT)
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    // Then our organized lib folder
                    Path.Combine(baseDir, "lib", "onnxruntime"),
                    // Finally the app base directory
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
                    // NuGet native assets
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    // Organized lib layout
                    Path.Combine(baseDir, "lib", "onnxruntime"),
                    Path.Combine(baseDir, "lib"),
                    // App root
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
                // Do not pre-load; let ORT attempt and surface detailed errors
                return true;
            }
            catch { return false; }
        }

        // NOTE: DirectML intentionally disabled per user request.
        // NOTE: TensorRT disabled per user request to stay on CUDA EP.

        public static InferenceSession Create(string modelPath, bool requestedGpu, out bool usingGpu)
        {
            using (var scope = Telemetry.LatencyScope("onnx_session_create", emitEvent: true))
            {
                usingGpu = false;
                PreloadOrtNative();
                var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED };
                try { so.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE; } catch { }

                Telemetry.Counter("onnx.session_create_requests");

                if (requestedGpu)
                {
                    try
                    {
                        if (HasOrtExport("OrtSessionOptionsAppendExecutionProvider_CUDA") && IsCudaProviderAvailable())
                        {
                            int deviceId = AppSettings.LoadTtsGpuDeviceId();
#if NETFRAMEWORK
                            if (deviceId > 0) so.AppendExecutionProvider_CUDA(deviceId); else so.AppendExecutionProvider_CUDA();
#else
                            if (deviceId > 0)
                            {
                                using var cudaOpts = SessionOptions.MakeSessionOptionWithCudaProvider(deviceId);
                                so = cudaOpts;
                            }
                            else
                            {
                                so.AppendExecutionProvider_CUDA();
                            }
#endif
                            usingGpu = true;
                            Telemetry.Counter("onnx.cuda_ep_success");
                            Console.WriteLine("[Onnx] EP: CUDA (GPU)");
                        }
                        else
                        {
                            Telemetry.Counter("onnx.cuda_ep_unavailable");
                            var error = AppError.GPU("GPU_PROVIDER_MISSING", 
                                "CUDA execution provider not available or provider DLLs missing",
                                "Install CUDA EP runtime or switch to CPU in Settings.");
                            Console.WriteLine($"[Onnx] {error.GetDisplayString()}");
                        }
                    }
                    catch (EntryPointNotFoundException epex) 
                    {
                        Telemetry.Counter("onnx.cuda_ep_entry_point_not_found");
                        var error = AppError.GPU("GPU_ENTRY_POINT_NOT_FOUND", 
                            "CUDA entry point not found in runtime",
                            "Install compatible CUDA EP runtime or switch to CPU in Settings.", epex);
                        Console.WriteLine($"[Onnx] {error.GetDisplayString()}");
                    }
                    catch (OnnxRuntimeException orex)
                    {
                        Telemetry.Counter("onnx.cuda_ep_runtime_error");
                        var error = AppError.GPU("GPU_RUNTIME_ERROR", 
                            $"CUDA EP load failed: {orex.Message}",
                            "Check GPU drivers, CUDA installation, or switch to CPU in Settings.", orex);
                        Console.WriteLine($"[Onnx] {error.GetDisplayString()}");
                    }
                    catch (Exception ex)
                    {
                        Telemetry.Counter("onnx.cuda_ep_unexpected_error");
                        var error = AppError.GPU("GPU_UNEXPECTED_ERROR", 
                            $"CUDA EP unexpected error: {ex.Message}",
                            "Check GPU configuration or switch to CPU in Settings.", ex);
                        Console.WriteLine($"[Onnx] {error.GetDisplayString()}");
                    }
                }

                if (!usingGpu)
                {
                    Telemetry.Counter("onnx.cpu_fallbacks");
                    try { so.AppendExecutionProvider_CPU(0); Console.WriteLine("[Onnx] EP: CPU"); } catch { }
                }

                var session = new InferenceSession(modelPath, so);
                try
                {
                    Telemetry.Counter("onnx.sessions_created");
                    Console.WriteLine("[Onnx] Session created.");
                }
                catch { }
                return session;
            }
        }
    }
}
