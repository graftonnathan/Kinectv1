// JsonUtils.cs
using System;
using System.Text.Json;

public static class JsonUtils
{
    public static string Extract(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("text", out var textElement))
            {
                return textElement.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Fallback for malformed JSON
        }
        return string.Empty;
    }
}
