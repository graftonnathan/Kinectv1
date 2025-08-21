// Telemetry.cs
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace Kinectv1
{
    public enum TelemetryLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>
    /// Lightweight, dependency-free telemetry layer for structured events (NDJSON) and counters.
    /// Thread-safe, negligible overhead design for audio/TTS hot loops.
    /// </summary>
    public static class Telemetry
    {
        private static readonly object _fileLock = new object();
        private static readonly ConcurrentDictionary<string, long> _counters = new ConcurrentDictionary<string, long>();
        private static readonly ConcurrentDictionary<string, double> _accumulators = new ConcurrentDictionary<string, double>();
        
        private static bool _enabled = true;
        private static string _logFilePath = "logs/telemetry.ndjson";
        private static int _samplingPct = 100;
        private static readonly Random _random = new Random();
        
        // File rotation settings
        private const long MAX_FILE_SIZE_BYTES = 5 * 1024 * 1024; // 5MB
        
        static Telemetry()
        {
            LoadSettings();
        }

        /// <summary>
        /// Load telemetry settings from AppSettings
        /// </summary>
        private static void LoadSettings()
        {
            try
            {
                _enabled = AppSettings.LoadTelemetryEnabled();
                _logFilePath = AppSettings.LoadTelemetryFile();
                _samplingPct = AppSettings.LoadTelemetrySamplingPct();
                
                // Ensure logs directory exists
                var logDir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telemetry: Failed to load settings: {ex.Message}");
                // Use defaults on error
                _enabled = false;
            }
        }

        /// <summary>
        /// Refresh settings from AppSettings (call when configuration changes)
        /// </summary>
        public static void RefreshSettings()
        {
            LoadSettings();
        }

        /// <summary>
        /// Emit a structured telemetry event
        /// </summary>
        /// <param name="name">Event name</param>
        /// <param name="data">Optional event data (will be JSON serialized)</param>
        /// <param name="level">Event level</param>
        public static void Event(string name, object data = null, TelemetryLevel level = TelemetryLevel.Info)
        {
            if (!_enabled || ShouldSample() == false)
                return;

            try
            {
                var eventData = new
                {
                    ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    lvl = level.ToString().ToLowerInvariant(),
                    name = name,
                    data = data,
                    threadId = Thread.CurrentThread.ManagedThreadId
                };

                var json = JsonConvert.SerializeObject(eventData, Formatting.None);
                
                // Console output (single line)
                Console.WriteLine(json);
                
                // File output with rotation
                WriteToFile(json);
            }
            catch (Exception ex)
            {
                // Silently fail to avoid disrupting application
                Console.WriteLine($"Telemetry.Event error: {ex.Message}");
            }
        }

        /// <summary>
        /// Increment a counter by name
        /// </summary>
        /// <param name="name">Counter name</param>
        /// <param name="increment">Amount to increment (default: 1)</param>
        public static void Counter(string name, long increment = 1)
        {
            if (!_enabled)
                return;

            _counters.AddOrUpdate(name, increment, (key, existing) => existing + increment);
        }

        /// <summary>
        /// Add value to an accumulator (for averages, totals, etc.)
        /// </summary>
        /// <param name="name">Accumulator name</param>
        /// <param name="value">Value to add</param>
        public static void Accumulator(string name, double value)
        {
            if (!_enabled)
                return;

            _accumulators.AddOrUpdate(name, value, (key, existing) => existing + value);
        }

        /// <summary>
        /// Get current counter value
        /// </summary>
        /// <param name="name">Counter name</param>
        /// <returns>Current counter value</returns>
        public static long GetCounter(string name)
        {
            return _counters.TryGetValue(name, out var value) ? value : 0L;
        }

        /// <summary>
        /// Get current accumulator value
        /// </summary>
        /// <param name="name">Accumulator name</param>
        /// <returns>Current accumulator value</returns>
        public static double GetAccumulator(string name)
        {
            return _accumulators.TryGetValue(name, out var value) ? value : 0.0;
        }

        /// <summary>
        /// Take a snapshot of all counters and accumulators
        /// </summary>
        /// <returns>Dictionary of all current values</returns>
        public static object GetSnapshot()
        {
            var snapshot = new
            {
                counters = new ConcurrentDictionary<string, long>(_counters),
                accumulators = new ConcurrentDictionary<string, double>(_accumulators),
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            };
            return snapshot;
        }

        /// <summary>
        /// Reset all counters and accumulators
        /// </summary>
        public static void Reset()
        {
            _counters.Clear();
            _accumulators.Clear();
        }

        /// <summary>
        /// Create a latency measurement scope that automatically tracks duration
        /// </summary>
        /// <param name="name">Scope name (will create name.count and name.total_ms counters)</param>
        /// <param name="emitEvent">Whether to emit an event when scope completes</param>
        /// <returns>Disposable scope</returns>
        public static IDisposable LatencyScope(string name, bool emitEvent = false)
        {
            return new LatencyScopeImpl(name, emitEvent);
        }

        /// <summary>
        /// Check if we should sample this event based on sampling percentage
        /// </summary>
        private static bool ShouldSample()
        {
            if (_samplingPct >= 100)
                return true;
            if (_samplingPct <= 0)
                return false;
            return _random.Next(100) < _samplingPct;
        }

        /// <summary>
        /// Write JSON line to file with rotation
        /// </summary>
        private static void WriteToFile(string json)
        {
            try
            {
                lock (_fileLock)
                {
                    // Check if rotation is needed
                    if (File.Exists(_logFilePath))
                    {
                        var fileInfo = new FileInfo(_logFilePath);
                        if (fileInfo.Length > MAX_FILE_SIZE_BYTES)
                        {
                            RotateLogFile();
                        }
                    }

                    // Append to current log file
                    File.AppendAllText(_logFilePath, json + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // Silently fail to avoid disrupting application
                Console.WriteLine($"Telemetry file write error: {ex.Message}");
            }
        }

        /// <summary>
        /// Rotate log file when it gets too large
        /// </summary>
        private static void RotateLogFile()
        {
            try
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var directory = Path.GetDirectoryName(_logFilePath);
                var filename = Path.GetFileNameWithoutExtension(_logFilePath);
                var extension = Path.GetExtension(_logFilePath);
                var rotatedFile = Path.Combine(directory, $"{filename}_{timestamp}{extension}");
                
                File.Move(_logFilePath, rotatedFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telemetry log rotation error: {ex.Message}");
            }
        }

        /// <summary>
        /// Internal implementation of latency scope
        /// </summary>
        private class LatencyScopeImpl : IDisposable
        {
            private readonly string _name;
            private readonly bool _emitEvent;
            private readonly Stopwatch _stopwatch;

            public LatencyScopeImpl(string name, bool emitEvent)
            {
                _name = name;
                _emitEvent = emitEvent;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _stopwatch.Stop();
                var elapsedMs = _stopwatch.Elapsed.TotalMilliseconds;
                
                // Update counters
                Counter($"{_name}.count");
                Accumulator($"{_name}.total_ms", elapsedMs);
                
                // Optionally emit event
                if (_emitEvent)
                {
                    Event($"{_name}.completed", new { duration_ms = elapsedMs });
                }
            }
        }
    }
}