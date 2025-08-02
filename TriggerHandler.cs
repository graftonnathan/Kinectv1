    // TriggerHandler.cs
using System;

public static class TriggerHandler
{
    public static void TryTrigger(string text, string triggerName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(triggerName))
            return;

        if (text.Contains(triggerName, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Trigger] Detected trigger word: {triggerName}");
            VoiceRecognizer.OnNameHeard?.Invoke(triggerName);
        }
    }
}
