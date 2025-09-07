using System;
using System.Threading.Tasks;

namespace Kinectv1
{
    public static class OllamaService
    {
        public static event Action<string> OnPromptSent;
        public static event Action<string> OnResponseReceived;
        public static event Action<string> OnError;

        public static Task<bool> GetAvailableModelsAsync(System.Threading.CancellationToken ct = default) => Task.FromResult(false);
    }
}
