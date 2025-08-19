// TriggerHandler.cs
using System;

namespace Kinectv1
{
    public static class TriggerHandler
    {
        public static void TryTrigger(string text, string triggerName)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(triggerName))
                return;

            // Use IndexOf for .NET Framework compatibility since Contains with StringComparison doesn't exist
            if (text.IndexOf(triggerName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine($"[Trigger] Detected trigger word: {triggerName}");
                VoiceRecognizer.OnNameHeard?.Invoke(triggerName);
            }
        }
    }
}
