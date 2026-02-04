using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Llm.Tools
{
    /// <summary>
    /// Registry and executor for LLM tools.
    /// Handles tool registration, prompt generation, and execution.
    /// </summary>
    public class ToolRegistry
    {
        private readonly Dictionary<string, ILlmTool> _tools = new(StringComparer.OrdinalIgnoreCase);
        private static ToolRegistry _instance;
        private static readonly object _lock = new();

        public static ToolRegistry Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (_lock)
                {
                    _instance ??= new ToolRegistry();
                    return _instance;
                }
            }
        }

        private ToolRegistry()
        {
            // Register built-in tools
            Register(new WebSearchTool());
        }

        /// <summary>
        /// Register a tool with the registry.
        /// </summary>
        public void Register(ILlmTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            _tools[tool.Name] = tool;
            Console.WriteLine($"[ToolRegistry] Registered tool: {tool.Name}");
        }

        /// <summary>
        /// Get a tool by name.
        /// </summary>
        public ILlmTool GetTool(string name)
        {
            return _tools.TryGetValue(name, out var tool) ? tool : null;
        }

        /// <summary>
        /// Get all registered tools.
        /// </summary>
        public IEnumerable<ILlmTool> GetAllTools() => _tools.Values;

        /// <summary>
        /// Check if tools are enabled in settings.
        /// </summary>
        public bool IsEnabled
        {
            get
            {
                try
                {
                    return App.SettingsProvider?.Current?.Ollama?.ToolsEnabled ?? false;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Generate the tool instructions to append to the system prompt.
        /// </summary>
        public string GenerateToolPrompt()
        {
            if (!IsEnabled || _tools.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("## Available Tools");
            sb.AppendLine("You have access to the following tools. To use a tool, respond with a tool call in this exact format:");
            sb.AppendLine();
            sb.AppendLine("```tool");
            sb.AppendLine("{\"tool\": \"tool_name\", \"parameters\": {\"param1\": \"value1\"}}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("After receiving the tool result, incorporate it into your response naturally.");
            sb.AppendLine("Only use tools when necessary - for questions about current events, facts you're unsure about, or when the user explicitly asks you to search.");
            sb.AppendLine();

            foreach (var tool in _tools.Values)
            {
                sb.AppendLine($"### {tool.Name}");
                sb.AppendLine(tool.Description);
                sb.AppendLine($"Parameters: {tool.ParameterSchema}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Parse tool calls from LLM response text.
        /// Returns list of (toolName, parameters) tuples.
        /// </summary>
        public List<(string toolName, string parameters)> ParseToolCalls(string response)
        {
            var results = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(response)) return results;

            // Match ```tool ... ``` blocks
            var toolBlockRegex = new Regex(@"```tool\s*\n?(.*?)\n?```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var matches = toolBlockRegex.Matches(response);

            foreach (Match match in matches)
            {
                TryParseToolJson(match.Groups[1].Value, results);
            }

            // Some models emit <toolcall>...</toolcall>
            if (results.Count == 0)
            {
                var tagRegex = new Regex(@"<toolcall[^>]*>([\s\S]*?)</toolcall>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var tagMatches = tagRegex.Matches(response);
                foreach (Match match in tagMatches)
                {
                    TryParseToolJson(match.Groups[1].Value, results);
                }
            }

            // Also try to match inline JSON tool calls
            if (results.Count == 0)
            {
                // Accept either {"tool":"x","parameters":{...}} or {"tool":"x","parameters":"..."}
                var inlineRegex = new Regex(
                    @"\{[\s\S]*?""tool""\s*:\s*""([^""\\]+)""[\s\S]*?""parameters""\s*:\s*([\s\S]*?)\}",
                    RegexOptions.IgnoreCase);

                var inlineMatches = inlineRegex.Matches(response);
                foreach (Match match in inlineMatches)
                {
                    TryParseToolJson(match.Value, results);
                }
            }

            // Last resort: if the entire response is a JSON object containing tool/parameters
            if (results.Count == 0 && response.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                TryParseToolJson(response, results);
            }

            return results;
        }

        private static void TryParseToolJson(string jsonLike, List<(string toolName, string parameters)> results)
        {
            try
            {
                var json = (jsonLike ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(json)) return;

                // Strip possible surrounding code fences/tags
                json = Regex.Replace(json, @"^```(?:json|tool)?\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
                json = Regex.Replace(json, @"```$", string.Empty, RegexOptions.IgnoreCase).Trim();

                var obj = JObject.Parse(json);
                var toolName = obj["tool"]?.ToString();

                string parameters;
                var pTok = obj["parameters"];
                if (pTok == null)
                {
                    // Allow { tool: "web_search", query: "..." }
                    var q = obj["query"]?.ToString();
                    parameters = string.IsNullOrWhiteSpace(q) ? "{}" : new JObject { ["query"] = q }.ToString(Formatting.None);
                }
                else if (pTok.Type == JTokenType.Object)
                {
                    parameters = pTok.ToString(Formatting.None);
                }
                else
                {
                    // string or other primitive
                    parameters = pTok.ToString();
                }

                if (!string.IsNullOrWhiteSpace(toolName))
                {
                    results.Add((toolName, parameters ?? "{}"));
                }
            }
            catch { }
        }

        /// <summary>
        /// Remove tool call blocks from the response text.
        /// </summary>
        public string RemoveToolCalls(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return response;

            // Remove ```tool ... ``` blocks
            var result = Regex.Replace(response, @"```tool\s*\n?.*?\n?```", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            // Remove <toolcall>...</toolcall>
            result = Regex.Replace(result, @"<toolcall[^>]*>[\s\S]*?</toolcall>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            // Clean up extra whitespace
            result = Regex.Replace(result, @"\n{3,}", "\n\n");
            return result.Trim();
        }

        public bool HasToolCalls(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return false;
            return response.Contains("```tool", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("<toolcall", StringComparison.OrdinalIgnoreCase) ||
                   Regex.IsMatch(response, @"\{[^{}]*""tool""\s*:", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Execute a tool call and return the result.
        /// </summary>
        public async Task<string> ExecuteToolAsync(string toolName, string parameters, CancellationToken ct = default)
        {
            var tool = GetTool(toolName);
            if (tool == null)
            {
                return $"Error: Unknown tool '{toolName}'";
            }

            try
            {
                Console.WriteLine($"[ToolRegistry] Executing tool: {toolName}");
                var result = await tool.ExecuteAsync(parameters, ct).ConfigureAwait(false);
                Console.WriteLine($"[ToolRegistry] Tool {toolName} completed ({result?.Length ?? 0} chars)");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ToolRegistry] Tool {toolName} failed: {ex.Message}");
                return $"Error executing {toolName}: {ex.Message}";
            }
        }
    }
}
