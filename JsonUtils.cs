// JsonUtils.cs
using System;
using Newtonsoft.Json.Linq;

public static class JsonUtils
{
    public static string Extract(string json)
    {
        try
        {
            var jsonObject = JObject.Parse(json);
            return jsonObject["text"]?.ToString() ?? string.Empty;
        }
        catch
        {
            // Fallback for malformed JSON
        }
        return string.Empty;
    }

    // Enhanced method for extracting specific fields
    public static string Extract(string json, string fieldName)
    {
        try
        {
            var jsonObject = JObject.Parse(json);
            return jsonObject[fieldName]?.ToString() ?? string.Empty;
        }
        catch
        {
            // Fallback for malformed JSON
        }
        return string.Empty;
    }

    /// <summary>
    /// Extract content from Ollama chat API response
    /// </summary>
    public static string ExtractChatContent(string json)
    {
        try
        {
            var jsonObject = JObject.Parse(json);
            
            // Try format: { "message": { "content": "response" } }
            var message = jsonObject["message"];
            if (message != null)
            {
                var content = message["content"]?.ToString();
                if (!string.IsNullOrEmpty(content))
                {
                    return content;
                }
            }
            
            // Try format: { "choices": [{ "message": { "content": "response" } }] }
            var choices = jsonObject["choices"] as JArray;
            if (choices != null && choices.Count > 0)
            {
                var firstChoice = choices[0];
                var choiceMessage = firstChoice["message"];
                if (choiceMessage != null)
                {
                    var content = choiceMessage["content"]?.ToString();
                    if (!string.IsNullOrEmpty(content))
                    {
                        return content;
                    }
                }
            }
            
            // Try direct content field
            var directContent = jsonObject["content"]?.ToString();
            if (!string.IsNullOrEmpty(directContent))
            {
                return directContent;
            }
            
            // Try legacy response field as fallback
            var response = jsonObject["response"]?.ToString();
            if (!string.IsNullOrEmpty(response))
            {
                return response;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Chat content extraction error: {ex.Message}");
        }
        
        return string.Empty;
    }

    // Method to create simple JSON objects
    public static string CreateSimpleJson(params (string key, object value)[] keyValuePairs)
    {
        try
        {
            var jsonObject = new JObject();
            foreach (var (key, value) in keyValuePairs)
            {
                if (value is string stringValue)
                {
                    jsonObject[key] = stringValue;
                }
                else if (value is bool boolValue)
                {
                    jsonObject[key] = boolValue;
                }
                else if (value is int intValue)
                {
                    jsonObject[key] = intValue;
                }
                else
                {
                    jsonObject[key] = value?.ToString();
                }
            }
            return jsonObject.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JSON creation error: {ex.Message}");
            return "{}";
        }
    }

    /// <summary>
    /// Create Ollama JSON with proper chat format and system role support
    /// </summary>
    public static string CreateOllamaJson(string model, string systemPrompt, string userPrompt)
    {
        try
        {
            var jsonObject = new JObject();
            jsonObject["model"] = model;
            jsonObject["stream"] = false;
            
            // Use proper messages format with role="system" for modern Ollama API
            var messagesArray = new JArray();
            
            // Add system message if we have a system prompt
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                string cleanSystemPrompt = systemPrompt.Trim();
                
                // Remove "SYSTEM:" or "System:" prefix if present since we're using role="system"
                if (cleanSystemPrompt.StartsWith("SYSTEM:", StringComparison.OrdinalIgnoreCase))
                {
                    cleanSystemPrompt = cleanSystemPrompt.Substring(7).Trim();
                }
                else if (cleanSystemPrompt.StartsWith("System:", StringComparison.OrdinalIgnoreCase))
                {
                    cleanSystemPrompt = cleanSystemPrompt.Substring(7).Trim();
                }
                
                var systemMessage = new JObject();
                systemMessage["role"] = "system";
                systemMessage["content"] = cleanSystemPrompt;
                messagesArray.Add(systemMessage);
                
                Console.WriteLine($"📝 Added system message with role='system' (length: {cleanSystemPrompt.Length} chars)");
            }
            
            // Add user message
            var userMessage = new JObject();
            userMessage["role"] = "user";
            userMessage["content"] = userPrompt;
            messagesArray.Add(userMessage);
            
            // Use messages format for modern Ollama chat API
            jsonObject["messages"] = messagesArray;
            
            // Add options for better responses
            var options = new JObject();
            options["temperature"] = 0.7;
            options["top_p"] = 0.9;
            options["top_k"] = 40;
            jsonObject["options"] = options;
            
            Console.WriteLine($"📝 Created Ollama chat format with {messagesArray.Count} messages");
            
            return jsonObject.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ollama JSON creation error: {ex.Message}");
            
            // Fallback to old format if new format fails
            return CreateOllamaLegacyJson(model, systemPrompt, userPrompt);
        }
    }
    
    /// <summary>
    /// Fallback method using legacy Ollama prompt format
    /// </summary>
    public static string CreateOllamaLegacyJson(string model, string systemPrompt, string userPrompt)
    {
        try
        {
            var jsonObject = new JObject();
            jsonObject["model"] = model;
            jsonObject["stream"] = false;
            
            // Create properly formatted prompt with system prompt (legacy format)
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                string formattedSystemPrompt = systemPrompt.Trim();
                
                // Check if the system prompt already starts with "SYSTEM:" or "System:"
                if (formattedSystemPrompt.StartsWith("SYSTEM:", StringComparison.OrdinalIgnoreCase) ||
                    formattedSystemPrompt.StartsWith("System:", StringComparison.OrdinalIgnoreCase))
                {
                    // Use the system prompt as-is since it already has a system prefix
                    var combinedPrompt = $"{formattedSystemPrompt}\n\n{userPrompt}";
                    jsonObject["prompt"] = combinedPrompt;
                }
                else
                {
                    // Add our own system prefix
                    var combinedPrompt = $"System: {formattedSystemPrompt}\n\nUser: {userPrompt}";
                    jsonObject["prompt"] = combinedPrompt;
                }
                
                Console.WriteLine($"📝 Using legacy prompt format as fallback");
            }
            else
            {
                jsonObject["prompt"] = userPrompt;
            }
            
            // Add options for better responses
            var options = new JObject();
            options["temperature"] = 0.7;
            options["top_p"] = 0.9;
            options["top_k"] = 40;
            jsonObject["options"] = options;
            
            return jsonObject.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Legacy Ollama JSON creation error: {ex.Message}");
            
            // Final fallback to simple format
            return CreateSimpleJson(
                ("model", model),
                ("prompt", userPrompt),
                ("stream", false)
            );
        }
    }

    // Method to extract model names from Ollama API response
    public static string[] ExtractModelNames(string json)
    {
        try
        {
            var jsonObject = JObject.Parse(json);
            var modelsArray = jsonObject["models"] as JArray;
            
            if (modelsArray != null)
            {
                var modelNames = new System.Collections.Generic.List<string>();
                
                foreach (var model in modelsArray)
                {
                    var name = model["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        modelNames.Add(name);
                    }
                }
                
                return modelNames.ToArray();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JSON model extraction error: {ex.Message}");
        }
        
        return new string[0]; // Return empty array if parsing fails
    }
}
