using System;

namespace Kinectv1
{
    /// <summary>
    /// Normalized error categories for consistent error reporting and UI display
    /// </summary>
    public enum AppErrorCategory
    {
        Config,
        IO,
        Audio,
        Network,
        GPU,
        ASR,
        TTS,
        Discord,
        Unknown
    }

    /// <summary>
    /// Structured error record with category, code, message, and optional user hint
    /// </summary>
    public sealed class AppError
    {
        public AppErrorCategory Category { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public string Hint { get; private set; }
        public Exception Exception { get; private set; }

        private AppError(AppErrorCategory category, string code, string message, string hint = null, Exception exception = null)
        {
            Category = category;
            Code = code;
            Message = message;
            Hint = hint;
            Exception = exception;
        }

        public static AppError FromException(Exception ex, string code = null, string hint = null)
        {
            if (ex == null)
                return new AppError(AppErrorCategory.Unknown, code ?? "UNKNOWN_ERROR", "Unknown error occurred", hint);

            var category = CategorizeException(ex);
            var errorCode = code ?? GenerateCode(category, ex);
            var message = ex.Message ?? "No error message available";
            LogToTelemetry(category, errorCode, message, hint, ex);
            return new AppError(category, errorCode, message, hint, ex);
        }

        public static AppError Create(AppErrorCategory category, string code, string message, string hint = null, Exception ex = null)
        {
            LogToTelemetry(category, code, message, hint, ex);
            return new AppError(category, code, message, hint, ex);
        }

        public static AppError Config(string code, string message, string hint = null, Exception ex = null) => Create(AppErrorCategory.Config, code, message, hint, ex);
        public static AppError IO(string code, string message, string hint = null, Exception ex = null) => Create(AppErrorCategory.IO, code, message, hint, ex);
        public static AppError GPU(string code, string message, string hint = null, Exception ex = null) => Create(AppErrorCategory.GPU, code, message, hint, ex);
        public static AppError Discord(string code, string message, string hint = null, Exception ex = null) => Create(AppErrorCategory.Discord, code, message, hint, ex);
        public static AppError TTS(string code, string message, string hint = null, Exception ex = null) => Create(AppErrorCategory.TTS, code, message, hint, ex);
        public static AppError ASR(string code, string message, string hint = null, Exception ex = null) => Create(AppErrorCategory.ASR, code, message, hint, ex);
        public static AppError Audio(string code, string message, string hint = null, Exception ex = null) => Create(AppErrorCategory.Audio, code, message, hint, ex);
        public static AppError Network(string code, string message, string hint = null, Exception ex = null) => Create(AppErrorCategory.Network, code, message, hint, ex);

        private static AppErrorCategory CategorizeException(Exception ex)
        {
            var message = ex.Message?.ToLowerInvariant() ?? string.Empty;
            if (ex is System.IO.FileNotFoundException || ex is System.IO.DirectoryNotFoundException ||
                ex is System.IO.IOException || ex is UnauthorizedAccessException ||
                message.Contains("file") || message.Contains("path") || message.Contains("directory"))
                return AppErrorCategory.IO;

            if (ex is System.Net.WebException || ex is System.Net.Http.HttpRequestException ||
                ex is TimeoutException || message.Contains("network") || message.Contains("connection") ||
                message.Contains("timeout") || message.Contains("socket"))
                return AppErrorCategory.Network;

            if (ex is System.Configuration.ConfigurationException ||
                message.Contains("configuration") || message.Contains("setting") || message.Contains("config"))
                return AppErrorCategory.Config;

            if (message.Contains("cuda") || message.Contains("gpu") || message.Contains("onnxruntime") ||
                message.Contains("provider") || message.Contains("directml") || message.Contains("tensorrt"))
                return AppErrorCategory.GPU;

            if (message.Contains("discord") || message.Contains("token") || message.Contains("authorization") ||
                message.Contains("opus") || message.Contains("libsodium") || message.Contains("voice channel"))
                return AppErrorCategory.Discord;

            if (message.Contains("audio") || message.Contains("microphone") || message.Contains("speaker") ||
                message.Contains("wasapi") || message.Contains("naudio") || message.Contains("waveform"))
                return AppErrorCategory.Audio;

            if (message.Contains("tts") || message.Contains("text-to-speech") || message.Contains("synthesis") ||
                message.Contains("kokoro") || message.Contains("coqui"))
                return AppErrorCategory.TTS;

            if (message.Contains("speech") || message.Contains("recognition") || message.Contains("vosk") ||
                message.Contains("transcription") || message.Contains("asr"))
                return AppErrorCategory.ASR;

            return AppErrorCategory.Unknown;
        }

        private static string GenerateCode(AppErrorCategory category, Exception ex)
        {
            var prefix = category.ToString().ToUpperInvariant();
            var suffix = ex.GetType().Name.Replace("Exception", string.Empty).ToUpperInvariant();
            return $"{prefix}_{suffix}";
        }

        private static void LogToTelemetry(AppErrorCategory category, string code, string message, string hint, Exception ex)
        {
            try
            {
                var data = new
                {
                    category = category.ToString(),
                    code,
                    message,
                    hint,
                    exception_type = ex?.GetType().Name,
                    stack_trace = ex?.StackTrace
                };
                Telemetry.Event("error", data, TelemetryLevel.Error);
            }
            catch { }
        }

        public string GetDisplayString()
        {
            var categoryEmoji = GetCategoryEmoji();
            return $"{categoryEmoji} {Message}";
        }

        public string GetDetailedDisplayString()
        {
            var display = GetDisplayString();
            if (!string.IsNullOrWhiteSpace(Hint)) display += $"\n💡 {Hint}";
            if (Exception != null) display += $"\n🔍 {Exception.GetType().Name}: {Exception.Message}";
            return display;
        }

        private string GetCategoryEmoji()
        {
            switch (Category)
            {
                case AppErrorCategory.Config: return "⚙️";
                case AppErrorCategory.IO: return "📁";
                case AppErrorCategory.Audio: return "🎙️";
                case AppErrorCategory.Network: return "🌐";
                case AppErrorCategory.GPU: return "🖥️";
                case AppErrorCategory.ASR: return "🗣️";
                case AppErrorCategory.TTS: return "🎤";
                case AppErrorCategory.Discord: return "🤖";
                case AppErrorCategory.Unknown: return "❓";
                default: return "❌";
            }
        }
    }
}