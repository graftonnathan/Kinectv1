using System;

namespace Kinectv1
{
    /// <summary>
    /// Normalized error categories for consistent error reporting and UI display
    /// </summary>
    public enum AppErrorCategory
    {
        Config,   // Configuration and settings issues
        IO,       // File system, model paths, file access
        Audio,    // Microphone, speaker, audio device issues
        Network,  // Connectivity, web requests, timeouts
        GPU,      // CUDA, graphics, GPU provider issues
        ASR,      // Automatic speech recognition failures
        TTS,      // Text-to-speech model and synthesis issues
        Discord,  // Discord bot, authentication, voice channels
        Unknown   // Fallback for unclassified errors
    }

    /// <summary>
    /// Structured error record with category, code, message, and optional user hint
    /// </summary>
    public record AppError(
        AppErrorCategory Category,
        string Code,
        string Message,
        string Hint = null,
        Exception Exception = null)
    {
        /// <summary>
        /// Create AppError from exception with automatic categorization
        /// </summary>
        public static AppError FromException(Exception ex, string code = null, string hint = null)
        {
            if (ex == null)
                return new AppError(AppErrorCategory.Unknown, code ?? "UNKNOWN_ERROR", "Unknown error occurred", hint);

            var category = CategorizeException(ex);
            var errorCode = code ?? GenerateCode(category, ex);
            var message = ex.Message ?? "No error message available";

            // Log to telemetry
            LogToTelemetry(category, errorCode, message, hint, ex);

            return new AppError(category, errorCode, message, hint, ex);
        }

        /// <summary>
        /// Create AppError with explicit parameters and automatic telemetry logging
        /// </summary>
        public static AppError Create(AppErrorCategory category, string code, string message, string hint = null, Exception ex = null)
        {
            // Log to telemetry
            LogToTelemetry(category, code, message, hint, ex);

            return new AppError(category, code, message, hint, ex);
        }

        /// <summary>
        /// Create configuration error with hint
        /// </summary>
        public static AppError Config(string code, string message, string hint = null, Exception ex = null)
        {
            return Create(AppErrorCategory.Config, code, message, hint, ex);
        }

        /// <summary>
        /// Create IO error with hint
        /// </summary>
        public static AppError IO(string code, string message, string hint = null, Exception ex = null)
        {
            return Create(AppErrorCategory.IO, code, message, hint, ex);
        }

        /// <summary>
        /// Create GPU error with hint
        /// </summary>
        public static AppError GPU(string code, string message, string hint = null, Exception ex = null)
        {
            return Create(AppErrorCategory.GPU, code, message, hint, ex);
        }

        /// <summary>
        /// Create Discord error with hint
        /// </summary>
        public static AppError Discord(string code, string message, string hint = null, Exception ex = null)
        {
            return Create(AppErrorCategory.Discord, code, message, hint, ex);
        }

        /// <summary>
        /// Create TTS error with hint
        /// </summary>
        public static AppError TTS(string code, string message, string hint = null, Exception ex = null)
        {
            return Create(AppErrorCategory.TTS, code, message, hint, ex);
        }

        /// <summary>
        /// Create ASR error with hint
        /// </summary>
        public static AppError ASR(string code, string message, string hint = null, Exception ex = null)
        {
            return Create(AppErrorCategory.ASR, code, message, hint, ex);
        }

        /// <summary>
        /// Create Audio error with hint
        /// </summary>
        public static AppError Audio(string code, string message, string hint = null, Exception ex = null)
        {
            return Create(AppErrorCategory.Audio, code, message, hint, ex);
        }

        /// <summary>
        /// Create Network error with hint
        /// </summary>
        public static AppError Network(string code, string message, string hint = null, Exception ex = null)
        {
            return Create(AppErrorCategory.Network, code, message, hint, ex);
        }

        /// <summary>
        /// Automatically categorize exception by type and message content
        /// </summary>
        private static AppErrorCategory CategorizeException(Exception ex)
        {
            var type = ex.GetType().Name;
            var message = ex.Message?.ToLowerInvariant() ?? "";

            // File and IO related
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException || 
                ex is IOException || ex is UnauthorizedAccessException ||
                message.Contains("file") || message.Contains("path") || message.Contains("directory"))
                return AppErrorCategory.IO;

            // Network and connectivity
            if (ex is System.Net.WebException || ex is System.Net.Http.HttpRequestException ||
                ex is TimeoutException || message.Contains("network") || message.Contains("connection") ||
                message.Contains("timeout") || message.Contains("socket"))
                return AppErrorCategory.Network;

            // Configuration
            if (ex is System.Configuration.ConfigurationException ||
                message.Contains("configuration") || message.Contains("setting") || message.Contains("config"))
                return AppErrorCategory.Config;

            // GPU and CUDA
            if (message.Contains("cuda") || message.Contains("gpu") || message.Contains("onnxruntime") ||
                message.Contains("provider") || message.Contains("directml") || message.Contains("tensorrt"))
                return AppErrorCategory.GPU;

            // Discord specific
            if (message.Contains("discord") || message.Contains("token") || message.Contains("authorization") ||
                message.Contains("opus") || message.Contains("libsodium") || message.Contains("voice channel"))
                return AppErrorCategory.Discord;

            // Audio related
            if (message.Contains("audio") || message.Contains("microphone") || message.Contains("speaker") ||
                message.Contains("wasapi") || message.Contains("naudio") || message.Contains("waveform"))
                return AppErrorCategory.Audio;

            // TTS related
            if (message.Contains("tts") || message.Contains("text-to-speech") || message.Contains("synthesis") ||
                message.Contains("kokoro") || message.Contains("coqui"))
                return AppErrorCategory.TTS;

            // ASR related
            if (message.Contains("speech") || message.Contains("recognition") || message.Contains("vosk") ||
                message.Contains("transcription") || message.Contains("asr"))
                return AppErrorCategory.ASR;

            return AppErrorCategory.Unknown;
        }

        /// <summary>
        /// Generate error code from category and exception
        /// </summary>
        private static string GenerateCode(AppErrorCategory category, Exception ex)
        {
            var prefix = category.ToString().ToUpperInvariant();
            var suffix = ex.GetType().Name.Replace("Exception", "").ToUpperInvariant();
            return $"{prefix}_{suffix}";
        }

        /// <summary>
        /// Log structured error event to telemetry
        /// </summary>
        private static void LogToTelemetry(AppErrorCategory category, string code, string message, string hint, Exception ex)
        {
            try
            {
                var data = new
                {
                    category = category.ToString(),
                    code = code,
                    message = message,
                    hint = hint,
                    exception_type = ex?.GetType().Name,
                    stack_trace = ex?.StackTrace
                };

                Telemetry.Event("error", data, TelemetryLevel.Error);
            }
            catch
            {
                // Silently fail to avoid recursive errors in error handling
            }
        }

        /// <summary>
        /// Get display string for UI showing category and message
        /// </summary>
        public string GetDisplayString()
        {
            var categoryEmoji = GetCategoryEmoji();
            return $"{categoryEmoji} {Message}";
        }

        /// <summary>
        /// Get detailed display string including hint if available
        /// </summary>
        public string GetDetailedDisplayString()
        {
            var display = GetDisplayString();
            if (!string.IsNullOrWhiteSpace(Hint))
                display += $"\n💡 {Hint}";
            if (Exception != null)
                display += $"\n🔍 {Exception.GetType().Name}: {Exception.Message}";
            return display;
        }

        /// <summary>
        /// Get emoji for category
        /// </summary>
        private string GetCategoryEmoji()
        {
            return Category switch
            {
                AppErrorCategory.Config => "⚙️",
                AppErrorCategory.IO => "📁",
                AppErrorCategory.Audio => "🎙️",
                AppErrorCategory.Network => "🌐",
                AppErrorCategory.GPU => "🖥️",
                AppErrorCategory.ASR => "🗣️",
                AppErrorCategory.TTS => "🎤",
                AppErrorCategory.Discord => "🤖",
                AppErrorCategory.Unknown => "❓",
                _ => "❌"
            };
        }
    }
}