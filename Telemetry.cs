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
        
        private static bool _enabled = false; // disabled globally
        private static string _logFilePath = "logs/telemetry.ndjson";
        private static int _samplingPct = 0;
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
            // Force disabled regardless of persisted settings to remove telemetry
            _enabled = false;
            _logFilePath = null;
            _samplingPct = 0;
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
            if (!_enabled)
                return; // fully disabled

            // Legacy logic retained (no-op due to _enabled=false)
        }

        /// <summary>
        /// Emit an info-level telemetry event
        /// </summary>
        /// <param name="name">Event name</param>
        /// <param name="data">Optional event data</param>
        public static void Info(string name, object data = null) => Event(name, data, TelemetryLevel.Info);

        /// <summary>
        /// Emit a warning-level telemetry event
        /// </summary>
        /// <param name="name">Event name</param>
        /// <param name="data">Optional event data</param>
        public static void Warn(string name, object data = null) => Event(name, data, TelemetryLevel.Warning);

        /// <summary>
        /// Emit an error-level telemetry event
        /// </summary>
        /// <param name="name">Event name</param>
        /// <param name="data">Optional event data</param>
        public static void Error(string name, object data = null) => Event(name, data, TelemetryLevel.Error);

        /// <summary>
        /// Emit a structured metric event with data fields
        /// </summary>
        /// <param name="name">Metric name</param>
        /// <param name="data">Metric data (will be JSON serialized)</param>
        public static void Metric(string name, object data) => Event(name, data, TelemetryLevel.Info);

        /// <summary>
        /// Increment a counter by name
        /// </summary>
        /// <param name="name">Counter name</param>
        /// <param name="increment">Amount to increment (default: 1)</param>
        public static void Counter(string name, long increment = 1)
        {
            if (!_enabled) return;
            _counters.AddOrUpdate(name, increment, (key, existing) => existing + increment);
        }

        /// <summary>
        /// Add value to an accumulator (for averages, totals, etc.)
        /// </summary>
        /// <param name="name">Accumulator name</param>
        /// <param name="value">Value to add</param>
        public static void Accumulator(string name, double value)
        {
            if (!_enabled) return;
            _accumulators.AddOrUpdate(name, value, (key, existing) => existing + value);
        }

        /// <summary>
        /// Get current counter value
        /// </summary>
        /// <param name="name">Counter name</param>
        /// <returns>Current counter value</returns>
        public static long GetCounter(string name) => _counters.TryGetValue(name, out var value) ? value : 0L;

        /// <summary>
        /// Get current accumulator value
        /// </summary>
        /// <param name="name">Accumulator name</param>
        /// <returns>Current accumulator value</returns>
        public static double GetAccumulator(string name) => _accumulators.TryGetValue(name, out var value) ? value : 0.0;

        /// <summary>
        /// Record a timer value in milliseconds
        /// </summary>
        /// <param name="name">Timer name</param>
        /// <param name="milliseconds">Duration in milliseconds</param>
        public static void Timer(string name, long milliseconds)
        {
            if (!_enabled) return;
            Counter($"{name}.count");
            Accumulator($"{name}.total_ms", milliseconds);
        }

        /// <summary>
        /// Set a gauge value (current state metric)
        /// </summary>
        /// <param name="name">Gauge name</param>
        /// <param name="value">Current value</param>
        public static void Gauge(string name, double value)
        {
            if (!_enabled) return;
            _accumulators[name] = value;
        }

        /// <summary>
        /// Take a snapshot of all counters and accumulators
        /// </summary>
        /// <returns>Dictionary of all current values</returns>
        public static object GetSnapshot()
        {
            return new { }; // disabled
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
            return false; // disabled
        }

        /// <summary>
        /// Write JSON line to file with rotation
        /// </summary>
        private static void WriteToFile(string json)
        {
            // disabled
        }

        /// <summary>
        /// Rotate log file when it gets too large
        /// </summary>
        private static void RotateLogFile() { }

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
                if (!_enabled) return;
                Counter($"{_name}.count");
                Accumulator($"{_name}.total_ms", elapsedMs);
                if (_emitEvent) Event($"{_name}.completed", new { duration_ms = elapsedMs });
            }
        }
    }
}