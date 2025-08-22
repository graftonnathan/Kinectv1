// OllamaService.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Linq;

namespace Kinectv1
{
    /// <summary>
    /// Enumeration of available prompt tones for Ollama requests
    /// </summary>
    public enum PromptTone
    {
        Neutral,
        Friendly,
        Professional,
        Casual,
        Formal
    }

    public static class OllamaService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static string _baseUrl = "http://localhost:11434";
        private static string _defaultModel = "gemma3:4b"; // FIXED: Match App.config default
        private static bool _isEnabled = false; // Changed from true to false
        private static string _systemPrompt = ""; // Cached system prompt
        private static DateTime _lastSystemPromptLoad = DateTime.MinValue;

        // Initialization guard
        private static bool _initialized = false;
        private static readonly object _initLock = new object();

        // Events for UI integration
        public static event Action<string> OnPromptSent;
        public static event Action<string> OnResponseReceived;
        public static event Action<string> OnError;

        static OllamaService()
        {
            try
            {
                // Set a reasonable timeout for Ollama requests
                _httpClient.Timeout = TimeSpan.FromMinutes(2);
                
                // UPDATED: Load saved model from AppSettings
                try
                {
                    var savedModel = AppSettings.LoadOllamaModel();
                    if (!string.IsNullOrEmpty(savedModel))
                    {
                        _defaultModel = savedModel;
                        Console.WriteLine($"?? Loaded saved Ollama model from settings: {_defaultModel}");
                    }
                    else
                    {
                        Console.WriteLine($"?? Using default Ollama model: {_defaultModel}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Warning: Failed to load Ollama model setting, using default: {ex.Message}");
                }
                
                // Load saved enabled state from AppSettings
                try
                {
                    var savedEnabled = AppSettings.LoadOllamaEnabled();
                    _isEnabled = savedEnabled;
                    Console.WriteLine($"?? Loaded Ollama enabled state from settings: {_isEnabled}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Warning: Failed to load Ollama enabled setting, using default (false): {ex.Message}");
                    _isEnabled = false;
                }
                
                // Load system prompt on startup
                try
                {
                    LoadSystemPrompt();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Warning: Failed to load system prompt, using default: {ex.Message}");
                    _systemPrompt = "You are a helpful AI assistant.";
                }
                
                // Initialize conversation memory with settings from App.config
                try
                {
                    var maxMessagesPerSpeaker = AppSettings.LoadOllamaMaxMessagesPerSpeaker();
                    var maxSystemMessages = AppSettings.LoadOllamaMaxSystemMessages();
                    var timeoutMinutes = AppSettings.LoadOllamaConversationTimeoutMinutes();
                    var memoryEnabled = AppSettings.LoadOllamaMemoryEnabled();
                    
                    // NEW: Set 4000 token limit and enable file archiving as requested
                    var maxTokensPerConversation = 4000;
                    var createNewFileOnLimit = true;  // Enable new file creation instead of trimming
                    
                    if (memoryEnabled)
                    {
                        OllamaConversationManager.ConfigureMemory(
                            maxMessagesPerSpeaker: maxMessagesPerSpeaker,
                            maxSystemMessages: maxSystemMessages,
                            conversationTimeout: TimeSpan.FromMinutes(timeoutMinutes),
                            maxTokensPerConversation: maxTokensPerConversation,
                            createNewFileOnLimit: createNewFileOnLimit
                        );
                        
                        Console.WriteLine("?? OllamaService initialized with conversation memory support");
                        Console.WriteLine($"?? Token limit: {maxTokensPerConversation} tokens per conversation");
                        Console.WriteLine($"?? Archive mode: {(createNewFileOnLimit ? "Create new files on limit" : "Trim existing files")}");
                    }
                    else
                    {
                        Console.WriteLine("?? OllamaService initialized with conversation memory DISABLED");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Warning: Failed to initialize conversation memory, feature disabled: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error: OllamaService static constructor failed: {ex.Message}");
                Console.WriteLine($"? Stack trace: {ex.StackTrace}");
                // Ensure basic fields are initialized even if setup fails
                _isEnabled = false;
                _systemPrompt = "You are a helpful AI assistant.";
            }
        }

                    _initialized = true;
                }
                catch (Exception ex)
                {
                    // Do not rethrow from initialization; keep service usable with defaults
                    Console.WriteLine($"?? OllamaService initialization error: {ex.Message}");
                }
            }
        }

        public static void SetBaseUrl(string url)
        {
            InitializeIfNeeded();
            _baseUrl = url.TrimEnd('/');
            Console.WriteLine($"?? Ollama base URL set to: {_baseUrl}");
        }

        public static void SetDefaultModel(string model)
        {
            InitializeIfNeeded();
            _defaultModel = model;
            // Save the model to persistent storage
            try { AppSettings.SaveOllamaModel(model); } catch (Exception ex) { Console.WriteLine($"?? Save model failed: {ex.Message}"); }
            Console.WriteLine($"?? Ollama default model set to: {_defaultModel}");
        }

        public static void SetEnabled(bool enabled)
        {
            InitializeIfNeeded();
            _isEnabled = enabled;
            // Save the enabled state to persistent storage
            try { AppSettings.SaveOllamaEnabled(enabled); } catch (Exception ex) { Console.WriteLine($"?? Save enabled failed: {ex.Message}"); }
            Console.WriteLine($"?? Ollama service {(enabled ? "enabled" : "disabled")}");
        }

        public static bool IsEnabled() { InitializeIfNeeded(); return _isEnabled; }
        public static string GetDefaultModel() { InitializeIfNeeded(); return _defaultModel; }
        public static string GetBaseUrl() { InitializeIfNeeded(); return _baseUrl; }

        /// <summary>
        /// Load system prompt from prompts/system.txt file
        /// </summary>
        public static string LoadSystemPrompt()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var systemPromptPath = Path.Combine(baseDir, "prompts", "system.txt");

                if (File.Exists(systemPromptPath))
                {
                    var prompt = File.ReadAllText(systemPromptPath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        _systemPrompt = prompt;
                        _lastSystemPromptLoad = DateTime.UtcNow;
                        Console.WriteLine($"?? System prompt loaded: {prompt.Substring(0, Math.Min(50, prompt.Length))}...");
                        return _systemPrompt;
                    }
                }
                else
                {
                    // Create default system prompt file
                    Directory.CreateDirectory(Path.GetDirectoryName(systemPromptPath));
                    var defaultPrompt = "You are a helpful AI assistant integrated with a voice and face recognition system. You can see who is speaking and respond naturally to their voice commands and questions.";
                    File.WriteAllText(systemPromptPath, defaultPrompt, Encoding.UTF8);
                    _systemPrompt = defaultPrompt;
                    Console.WriteLine($"?? Created default system prompt file: {systemPromptPath}");
                    return _systemPrompt;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Error loading system prompt: {ex.Message}");
            }

            // Fallback to default if loading fails
            _systemPrompt = "You are a helpful AI assistant.";
            return _systemPrompt;
        }

        /// <summary>
        /// Get current system prompt (reload if file changed recently)
        /// </summary>
        public static string GetSystemPrompt()
        {
            InitializeIfNeeded();
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var systemPromptPath = Path.Combine(baseDir, "prompts", "system.txt");

                if (File.Exists(systemPromptPath))
                {
                    var lastWriteTime = File.GetLastWriteTime(systemPromptPath);

                    // Reload if file was modified after our last load
                    if (lastWriteTime > _lastSystemPromptLoad)
                    {
                        Console.WriteLine("?? System prompt file changed, reloading...");
                        return LoadSystemPrompt();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Error checking system prompt file: {ex.Message}");
            }

            return _systemPrompt;
        }

        /// <summary>
        /// Reload system prompt manually
        /// </summary>
        public static string ReloadSystemPrompt()
        {
            InitializeIfNeeded();
            return LoadSystemPrompt();
        }

        public static async Task<bool> TestConnectionAsync()
        {
            InitializeIfNeeded();
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags");
                bool isConnected = response.IsSuccessStatusCode;

                if (isConnected)
                {
                    Console.WriteLine("? Ollama connection successful");
                }
                else
                {
                    Console.WriteLine($"?? Ollama connection failed: {response.StatusCode}");
                }

                return isConnected;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Ollama connection error: {ex.Message}");
                return false;
            }
        }

        public static async Task<string> SendPromptAsync(string speakerName, string transcription)
        {
            InitializeIfNeeded();
            if (!_isEnabled)
            {
                Console.WriteLine("?? Ollama service is disabled - no prompt sent");
                return "Ollama service is disabled";
            }

            try
            {
                // Get current system prompt (auto-reload if changed)
                string systemPrompt = GetSystemPrompt();

                // Format the user prompt with speaker information for the LLM
                string userPrompt = $"{speakerName} says: {transcription}";

                // Store the raw transcription in conversation memory, not the formatted prompt
                string transcriptionForMemory = transcription.Trim();

                Console.WriteLine($"?? Sending to Ollama with memory: '{userPrompt}'");
                Console.WriteLine($"   Model: {_defaultModel}");
                Console.WriteLine($"   Speaker: {speakerName}");
                Console.WriteLine($"   Raw transcription for memory: '{transcriptionForMemory}'");
                Console.WriteLine($"   System Prompt Length: {systemPrompt.Length} characters");

                OnPromptSent?.Invoke(userPrompt);

                // Create JSON with conversation memory using the new conversation manager (if enabled)
                string requestJson;
                bool memoryEnabled = AppSettings.LoadOllamaMemoryEnabled();

                if (memoryEnabled)
                {
                    Console.WriteLine($"   ? Using conversation memory for {speakerName}");
                    requestJson = OllamaConversationManager.CreateOllamaJsonWithMemory(
                        _defaultModel,
                        systemPrompt,
                        speakerName,
                        userPrompt
                    );
                }
                else
                {
                    Console.WriteLine($"   ?? Using standard request (memory disabled)");
                    requestJson = JsonUtils.CreateOllamaJson(_defaultModel, systemPrompt, userPrompt);
                }

                // Enhanced logging for debugging
                Console.WriteLine($"   Request JSON Length: {requestJson.Length} characters");
                if (requestJson.Length < 800)
                {
                    Console.WriteLine($"   Full Request JSON: {requestJson}");
                }
                else
                {
                    Console.WriteLine($"   Request JSON Preview: {requestJson.Substring(0, 400)}...");
                }

                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                // Try modern chat API first (required for conversation memory)
                HttpResponseMessage response = null;
                string endpoint = "/api/chat";

                try
                {
                    Console.WriteLine($"   Using chat API for conversation memory: {_baseUrl}{endpoint}");
                    response = await _httpClient.PostAsync($"{_baseUrl}{endpoint}", content);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"   Chat API failed ({response.StatusCode})");
                        var errorContent = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"   Error details: {errorContent}");

                        // For conversation memory, we need the chat API - don't fall back to generate
                        var error = $"Ollama chat API error: {response.StatusCode} - {errorContent}";
                        OnError?.Invoke(error);
                        return error;
                    }
                    else
                    {
                        Console.WriteLine($"   ? Chat API succeeded with conversation memory!");
                    }
                }
                catch (Exception apiEx)
                {
                    Console.WriteLine($"   Chat API call failed: {apiEx.Message}");
                    throw;
                }

                Console.WriteLine($"   HTTP Status: {response.StatusCode} on {endpoint}");

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();

                    // Enhanced response logging
                    Console.WriteLine($"   Raw Response Length: {responseJson.Length} characters");
                    if (responseJson.Length < 200)
                    {
                        Console.WriteLine($"   Raw Response: {responseJson}");
                    }
                    else
                    {
                        Console.WriteLine($"   Raw Response Preview: {responseJson.Substring(0, 200)}...");
                    }

                    // Extract response using JsonUtils
                    string aiResponse = ExtractOllamaResponse(responseJson, endpoint);

                    if (string.IsNullOrEmpty(aiResponse))
                    {
                        aiResponse = "No response from Ollama";
                        Console.WriteLine("?? Empty response extracted from Ollama");
                    }
                    else
                    {
                        var preview = aiResponse.Substring(0, Math.Min(100, aiResponse.Length));
                        Console.WriteLine($"? Ollama response extracted: '{preview}...'");
                        Console.WriteLine($"? Full response length: {aiResponse.Length} characters");

                        // Add the conversation to memory after successful response (if enabled)
                        if (memoryEnabled)
                        {
                            OllamaConversationManager.AddUserMessage(speakerName, transcriptionForMemory);
                            OllamaConversationManager.AddAssistantMessage(speakerName, aiResponse);

                            // Log conversation stats
                            Console.WriteLine(OllamaConversationManager.GetConversationStats());
                        }
                    }

                    OnResponseReceived?.Invoke(aiResponse);

                    return aiResponse;
                }
                else
                {
                    string errorDetails = await response.Content.ReadAsStringAsync();
                    string error = $"Ollama HTTP error: {response.StatusCode} on {endpoint} - {errorDetails}";
                    Console.WriteLine($"?? {error}");
                    OnError?.Invoke(error);
                    return error;
                }
            }
            catch (HttpRequestException ex)
            {
                string error = $"Ollama network error: {ex.Message}";
                Console.WriteLine($"?? {error}");
                OnError?.Invoke(error);
                return error;
            }
            catch (Exception ex)
            {
                string error = $"Ollama error: {ex.Message}";
                Console.WriteLine($"?? {error}");
                OnError?.Invoke(error);
                return error;
            }
        }

        /// <summary>
        /// Extract response from Ollama API response (handles both chat and generate formats)
        /// </summary>
        private static string ExtractOllamaResponse(string responseJson, string endpoint)
        {
            try
            {
                if (endpoint == "/api/chat")
                {
                    // Use enhanced chat content extraction
                    var chatResponse = JsonUtils.ExtractChatContent(responseJson);
                    if (!string.IsNullOrEmpty(chatResponse))
                    {
                        Console.WriteLine($"   ? Extracted chat response successfully");
                        return chatResponse;
                    }

                    Console.WriteLine($"   ?? Chat response extraction failed, trying fallback");
                }

                // Generate API response format: { "response": "content" }
                var generateResponse = JsonUtils.Extract(responseJson, "response");
                if (!string.IsNullOrEmpty(generateResponse))
                {
                    Console.WriteLine($"   ? Extracted generate response successfully");
                    return generateResponse;
                }

                Console.WriteLine($"   ?? All response extraction methods failed");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ?? Error extracting response: {ex.Message}");
                return JsonUtils.Extract(responseJson, "response"); // Final fallback
            }
        }

        public static async Task<string[]> GetAvailableModelsAsync()
        {
            InitializeIfNeeded();
            try
            {
                Console.WriteLine("?? Querying Ollama for available models...");
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/tags");

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"   Raw models response: {responseJson}");

                    // Parse the JSON response to extract model names using JsonUtils
                    var models = new List<string>();

                    try
                    {
                        var extractedModels = JsonUtils.ExtractModelNames(responseJson);
                        if (extractedModels.Length > 0)
                        {
                            models.AddRange(extractedModels);
                            foreach (var model in extractedModels)
                            {
                                Console.WriteLine($"   Found model: {model}");
                            }
                        }
                        else
                        {
                            Console.WriteLine("?? No models found in response, using fallback list");
                        }
                    }
                    catch (Exception parseEx)
                    {
                        Console.WriteLine($"?? Error parsing models response: {parseEx.Message}");
                    }

                    // If parsing failed or no models found, use fallback models
                    if (models.Count == 0)
                    {
                        Console.WriteLine("?? Using fallback model list");
                        models.AddRange(new[] { _defaultModel, "llama3.1", "llama3.2", "codellama", "mistral", "gemma", "qwen", "phi" });
                    }

                    // Ensure current default model is in the list
                    if (!models.Contains(_defaultModel))
                    {
                        models.Insert(0, _defaultModel);
                    }

                    Console.WriteLine($"? Available models: {string.Join(", ", models)}");
                    return models.ToArray();
                }
                else
                {
                    Console.WriteLine($"?? Failed to get models: {response.StatusCode}");
                    return new[] { _defaultModel };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Error getting Ollama models: {ex.Message}");
                return new[] { _defaultModel, "llama3.1", "llama3.2", "codellama", "mistral", "gemma" };
            }
        }

        public static void Dispose()
        {
            try { _httpClient?.Dispose(); } catch { }
        }

        /// <summary>
        /// Configure conversation memory settings
        /// </summary>
        public static void ConfigureConversationMemory(int maxMessagesPerSpeaker = 20, int maxSystemMessages = 3, int timeoutMinutes = 30, int maxTokensPerConversation = 4000, bool createNewFileOnLimit = true)
        {
            InitializeIfNeeded();
            try
            {
                OllamaConversationManager.ConfigureMemory(
                    maxMessagesPerSpeaker,
                    maxSystemMessages,
                    TimeSpan.FromMinutes(timeoutMinutes),
                    maxTokensPerConversation,
                    createNewFileOnLimit
                );
                Console.WriteLine($"? Conversation memory reconfigured");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Conversation memory reconfiguration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get conversation statistics for debugging
        /// </summary>
        public static string GetConversationStats()
        {
            InitializeIfNeeded();
            return OllamaConversationManager.GetConversationStats();
        }

        /// <summary>
        /// Clear conversation history for a specific speaker
        /// </summary>
        public static void ClearSpeakerConversation(string speakerName)
        {
            InitializeIfNeeded();
            OllamaConversationManager.ClearSpeakerHistory(speakerName);
        }

        /// <summary>
        /// Clear all conversation history
        /// </summary>
        public static void ClearAllConversations()
        {
            InitializeIfNeeded();
            OllamaConversationManager.ClearAllHistory();
        }

        /// <summary>
        /// Get recent conversation context for a speaker (for UI display)
        /// </summary>
        public static List<string> GetRecentConversation(string speakerName, int maxMessages = 6)
        {
            InitializeIfNeeded();
            return OllamaConversationManager.GetRecentContext(speakerName, maxMessages);
        }

        /// <summary>
        /// Get list of all speakers with conversation history
        /// </summary>
        public static List<string> GetAllSpeakersWithHistory()
        {
            InitializeIfNeeded();
            return OllamaConversationManager.GetAllSpeakersWithHistory();
        }

        /// <summary>
        /// Get detailed conversation history for a specific speaker
        /// </summary>
        public static List<string> GetFullConversationHistory(string speakerName)
        {
            InitializeIfNeeded();
            return OllamaConversationManager.GetRecentContext(speakerName, 1000); // Get up to 1000 messages
        }

        /// <summary>
        /// Test conversation saving functionality (for debugging)
        /// </summary>
        public static void TestConversationSaving()
        {
            InitializeIfNeeded();
            OllamaConversationManager.TestConversationSaving();
        }

        /// <summary>
        /// Set the conversation history directory path
        /// </summary>
        public static void SetConversationHistoryPath(string historyPath)
        {
            InitializeIfNeeded();
            OllamaConversationManager.SetHistoryPath(historyPath);
        }

        /// <summary>
        /// Get the current conversation history directory path
        /// </summary>
        public static string GetConversationHistoryDirectory()
        {
            InitializeIfNeeded();
            return OllamaConversationManager.GetHistoryDirectory();
        }

        /// <summary>
        /// Get conversation manager status for debugging
        /// </summary>
        public static string GetConversationManagerStatus()
        {
            InitializeIfNeeded();
            return OllamaConversationManager.GetManagerStatus();
        }

        /// <summary>
        /// Comprehensive diagnostic check for conversation history issues
        /// </summary>
        public static string DiagnoseConversationHistory()
        {
            InitializeIfNeeded();
            var diagnostics = new List<string>();
            diagnostics.Add("?? Conversation History Diagnostic Report:");
            diagnostics.Add("");

            try
            {
                // Check Ollama service settings
                var ollamaEnabled = AppSettings.LoadOllamaEnabled();
                var memoryEnabled = AppSettings.LoadOllamaMemoryEnabled();

                diagnostics.Add("?? Service Configuration:");
                diagnostics.Add($"   Ollama Service Enabled: {(ollamaEnabled ? "? YES" : "? NO - Enable in settings")}");
                diagnostics.Add($"   Memory Enabled: {(memoryEnabled ? "? YES" : "? NO - Enable in settings")}");
                diagnostics.Add($"   Default Model: {GetDefaultModel()}");
                diagnostics.Add($"   Base URL: {GetBaseUrl()}");
                diagnostics.Add("");

                // Check conversation manager status
                diagnostics.Add("?? Conversation Manager Status:");
                var managerStatus = OllamaConversationManager.GetManagerStatus();
                var statusLines = managerStatus.Split('\n');
                foreach (var line in statusLines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        diagnostics.Add($"   {line.Trim()}");
                }
                diagnostics.Add("");

                // Connectivity info (no async call here)
                diagnostics.Add("?? Ollama Server Status:");
                diagnostics.Add($"   Connection: ? Test connection with TestConnectionAsync() method");
                diagnostics.Add($"   Server URL: {GetBaseUrl()}");
                diagnostics.Add("");

                // Check conversation stats
                diagnostics.Add("?? Current Conversation Data:");
                var stats = GetConversationStats();
                var statsLines = stats.Split('\n');
                foreach (var line in statsLines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        diagnostics.Add($"   {line.Trim()}");
                }
                diagnostics.Add("");

                // Recommendations
                diagnostics.Add("??? Troubleshooting Recommendations:");
                if (!ollamaEnabled)
                    diagnostics.Add("   1. Enable Ollama service in application settings");
                if (!memoryEnabled)
                    diagnostics.Add("   2. Enable conversation memory in Ollama settings");

                diagnostics.Add("   3. Ensure Ollama server is running (http://localhost:11434)");
                diagnostics.Add("   4. Test voice recognition and verify it triggers Ollama");
                diagnostics.Add("   5. Check console output for error messages");
                diagnostics.Add("   6. Verify successful Ollama responses in logs");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"?? Diagnostic error: {ex.Message}");
            }

            return string.Join("\n", diagnostics);
        }

        /// <summary>
        /// Test conversation history with a manual conversation exchange
        /// </summary>
        public static async Task<string> TestConversationHistoryAsync(string testSpeaker = "DiagnosticTest")
        {
            InitializeIfNeeded();
            try
            {
                var testMessage = "This is a diagnostic test to verify conversation history is working.";

                Console.WriteLine($"?? Testing conversation history with speaker: {testSpeaker}");
                Console.WriteLine($"?? Test message: {testMessage}");

                // Send test message through normal Ollama flow
                var response = await SendPromptAsync(testSpeaker, testMessage);

                if (!string.IsNullOrEmpty(response) && !response.Contains("error") && !response.Contains("Error"))
                {
                    // Check if conversation was saved
                    var recentConversation = GetRecentConversation(testSpeaker, 10);
                    var hasHistory = recentConversation.Count > 0;

                    var result = $"? Conversation history test completed successfully!\n" +
                               $"   Response received: {response.Substring(0, Math.Min(100, response.Length))}...\n" +
                               $"   History saved: {(hasHistory ? "YES" : "NO")}\n" +
                               $"   Messages in history: {recentConversation.Count}";

                    if (hasHistory)
                    {
                        result += $"\n   Latest messages:\n";
                        var latestMessages = recentConversation.Count > 3
                            ? recentConversation.Skip(recentConversation.Count - 3).ToList()
                            : recentConversation;
                        foreach (var msg in latestMessages)
                        {
                            result += $"     - {msg}\n";
                        }
                    }

                    return result;
                }
                else
                {
                    return $"?? Conversation history test failed:\n" +
                           $"   Response: {response}\n" +
                           $"   This indicates Ollama service issues preventing history saving.";
                }
            }
            catch (Exception ex)
            {
                return $"?? Conversation history test error: {ex.Message}";
            }
        }

        /// <summary>
        /// Async version of diagnostic with connectivity testing
        /// </summary>
        public static async Task<string> DiagnoseConversationHistoryAsync()
        {
            InitializeIfNeeded();
            var diagnostics = new List<string>();
            diagnostics.Add("?? Conversation History Diagnostic Report (Full):");
            diagnostics.Add("");

            try
            {
                // Check Ollama service settings
                var ollamaEnabled = AppSettings.LoadOllamaEnabled();
                var memoryEnabled = AppSettings.LoadOllamaMemoryEnabled();

                diagnostics.Add("?? Service Configuration:");
                diagnostics.Add($"   Ollama Service Enabled: {(ollamaEnabled ? "? YES" : "? NO - Enable in settings")}");
                diagnostics.Add($"   Memory Enabled: {(memoryEnabled ? "? YES" : "? NO - Enable in settings")}");
                diagnostics.Add($"   Default Model: {GetDefaultModel()}");
                diagnostics.Add($"   Base URL: {GetBaseUrl()}");
                diagnostics.Add("");

                // Check conversation manager status
                diagnostics.Add("?? Conversation Manager Status:");
                var managerStatus = OllamaConversationManager.GetManagerStatus();
                var statusLines = managerStatus.Split('\n');
                foreach (var line in statusLines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        diagnostics.Add($"   {line.Trim()}");
                }
                diagnostics.Add("");

                // Check Ollama connectivity (async version)
                diagnostics.Add("?? Ollama Server Status:");
                try
                {
                    var connectionTest = await TestConnectionAsync();
                    diagnostics.Add($"   Connection: {(connectionTest ? "? Connected" : "? Not connected - Start Ollama server")}");
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"   Connection: ?? Error - {ex.Message}");
                }
                diagnostics.Add("");

                // Check conversation stats
                diagnostics.Add("?? Current Conversation Data:");
                var stats = GetConversationStats();
                var statsLines = stats.Split('\n');
                foreach (var line in statsLines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        diagnostics.Add($"   {line.Trim()}");
                }
                diagnostics.Add("");

                // Provide recommendations
                diagnostics.Add("??? Troubleshooting Recommendations:");
                if (!ollamaEnabled)
                    diagnostics.Add("   1. Enable Ollama service in application settings");
                if (!memoryEnabled)
                    diagnostics.Add("   2. Enable conversation memory in Ollama settings");

                diagnostics.Add("   3. Ensure Ollama server is running (http://localhost:11434)");
                diagnostics.Add("   4. Test voice recognition and verify it triggers Ollama");
                diagnostics.Add("   5. Check console output for error messages");
                diagnostics.Add("   6. Verify successful Ollama responses in logs");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"?? Diagnostic error: {ex.Message}");
            }

            return string.Join("\n", diagnostics);
        }

        /// <summary>
        /// Send a prompt to Ollama with specified tone (synchronous wrapper)
        /// </summary>
        /// <param name="prompt">The prompt text</param>
        /// <param name="tone">The prompt tone</param>
        /// <returns>The response from Ollama</returns>
        public static string SendPrompt(string prompt, PromptTone tone)
        {
            InitializeIfNeeded();
            try
            {
                // For now, ignore the tone parameter and use default speaker
                var task = SendPromptAsync("TestSpeaker", prompt);
                task.Wait();
                return task.Result;
            }
            catch (Exception ex)
            {
                string error = $"SendPrompt error: {ex.Message}";
                Console.WriteLine($"?? {error}");
                OnError?.Invoke(error);
                return error;
            }
        }
    }
}