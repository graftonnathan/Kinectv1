// OllamaConversationManager.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1
{
    /// <summary>
    /// Manages conversation history for Ollama LLM memory across multiple speakers with persistent file storage
    /// All speakers are stored in a single conversation file instead of separate files per speaker
    /// </summary>
    public static class OllamaConversationManager
    {
        // Conversation history per speaker (in-memory cache)
        private static readonly Dictionary<string, List<ConversationMessage>> _conversationHistory 
            = new Dictionary<string, List<ConversationMessage>>();
        
        // NEW: Track current conversation part number globally (not per speaker)
        private static int _globalConversationPartNumber = 0;
        
        // Global conversation settings
        private static int _maxMessagesPerSpeaker = 20;       // Maximum messages to keep per speaker
        private static int _maxSystemMessages = 3;           // Maximum system messages to keep
        private static TimeSpan _conversationTimeout = TimeSpan.FromMinutes(30); // Reset conversation after inactivity
        
        // NEW: Token management settings - now applies to entire conversation file
        private static int _maxTokensPerConversation = 4000;  // Maximum tokens to keep per conversation file
        private static bool _enableTokenLimiting = true;     // Enable token-based limiting
        private static bool _createNewFileOnLimit = true;    // NEW: Create new files instead of trimming
        
        // File storage settings - now uses single file for all speakers
        private static string _historyDirectory;
        private static bool _persistenceEnabled = true;
        private static readonly object _fileLock = new object();
        
        // NEW: Single conversation file name
        private static string _conversationFileName = "conversation.json";
        
        static OllamaConversationManager()
        {
            // FIXED: Use debug output directory for history storage instead of project root
            try
            {
                // Use the application's base directory (debug output folder) directly
                var currentDir = AppDomain.CurrentDomain.BaseDirectory;
                
                // Load the history path from settings (with fallback to "history")
                var historyPathSetting = AppSettings.LoadConversationHistoryPath();
                _historyDirectory = Path.Combine(currentDir, historyPathSetting);
                
                Console.WriteLine($"?? Conversation manager initialized:");
                Console.WriteLine($"   Current directory: {currentDir}");
                Console.WriteLine($"   History path setting: {historyPathSetting}");
                Console.WriteLine($"   History directory: {_historyDirectory}");
                
                // Ensure history directory exists
                if (!Directory.Exists(_historyDirectory))
                {
                    Directory.CreateDirectory(_historyDirectory);
                    Console.WriteLine($"?? Created conversation history directory: {_historyDirectory}");
                }
                
                // Load existing conversations on startup
                LoadAllConversationsFromDisk();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error initializing conversation storage: {ex.Message}");
                _persistenceEnabled = false;
            }
        }
        
        /// <summary>
        /// Configuration for conversation memory
        /// </summary>
        public static void ConfigureMemory(int maxMessagesPerSpeaker = 20, int maxSystemMessages = 3, TimeSpan? conversationTimeout = null, int maxTokensPerConversation = 4000, bool createNewFileOnLimit = true)
        {
            _maxMessagesPerSpeaker = Math.Max(2, maxMessagesPerSpeaker); // Minimum 2 messages
            _maxSystemMessages = Math.Max(1, maxSystemMessages);        // Minimum 1 system message
            _conversationTimeout = conversationTimeout ?? TimeSpan.FromMinutes(30);
            _maxTokensPerConversation = Math.Max(500, maxTokensPerConversation); // Minimum 500 tokens
            _createNewFileOnLimit = createNewFileOnLimit;
            
            Console.WriteLine($"?? Conversation memory configured:");
            Console.WriteLine($"   Max messages per speaker: {_maxMessagesPerSpeaker}");
            Console.WriteLine($"   Max system messages: {_maxSystemMessages}");
            Console.WriteLine($"   Max tokens per conversation file: {_maxTokensPerConversation}");
            Console.WriteLine($"   Create new files on limit: {(_createNewFileOnLimit ? "enabled" : "disabled (trim instead)")}");
            Console.WriteLine($"   Conversation timeout: {_conversationTimeout.TotalMinutes:F0} minutes");
            Console.WriteLine($"   Persistence enabled: {_persistenceEnabled}");
            Console.WriteLine($"   History directory: {_historyDirectory}");
            Console.WriteLine($"   Single file storage: {_conversationFileName}");
        }
        
        /// <summary>
        /// Update the conversation history path and recreate directory if needed
        /// </summary>
        public static void SetHistoryPath(string historyPath)
        {
            try
            {
                // Use the application's base directory (debug output folder) directly
                var currentDir = AppDomain.CurrentDomain.BaseDirectory;
                
                // Create new history directory path
                var newHistoryDirectory = Path.Combine(currentDir, historyPath);
                
                Console.WriteLine($"?? Updating conversation history path:");
                Console.WriteLine($"   Old path: {_historyDirectory}");
                Console.WriteLine($"   New path: {newHistoryDirectory}");
                
                // Update the path
                _historyDirectory = newHistoryDirectory;
                
                // Ensure new directory exists
                if (!Directory.Exists(_historyDirectory))
                {
                    Directory.CreateDirectory(_historyDirectory);
                    Console.WriteLine($"?? Created new conversation history directory: {_historyDirectory}");
                }
                
                // Save the new path to settings
                AppSettings.SaveConversationHistoryPath(historyPath);
                
                // Reload conversations from the new location
                LoadAllConversationsFromDisk();
                
                Console.WriteLine($"? Conversation history path updated successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error updating conversation history path: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get the current conversation history directory path
        /// </summary>
        public static string GetHistoryDirectory()
        {
            return _historyDirectory;
        }
        
        private static string GetConversationFilePath()
        {
            return Path.Combine(_historyDirectory, _conversationFileName);
        }

        private static string NormalizeSpeakerName(string speakerName)
        {
            if (string.IsNullOrWhiteSpace(speakerName))
                return "Unknown";
            
            // Normalize speaker name (remove extra whitespace, standardize case, make filename-safe)
            var normalized = speakerName.Trim();
            
            // Remove invalid filename characters
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                normalized = normalized.Replace(c, '_');
            }
            
            return normalized;
        }

        private static void EnsureConversationExists(string speakerName)
        {
            try
            {
                if (!_conversationHistory.ContainsKey(speakerName))
                {
                    // Try to load from disk first (loads most recent conversation)
                    if (_persistenceEnabled && LoadConversationFromDisk(speakerName))
                    {
                        Console.WriteLine($"?? Loaded existing conversation for speaker: {speakerName}");
                    }
                    else
                    {
                        // Create new conversation
                        _conversationHistory[speakerName] = new List<ConversationMessage>();
                        Console.WriteLine($"?? Created new conversation for speaker: {speakerName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error ensuring conversation exists for {speakerName}: {ex.Message}");
                // Fallback: ensure we have an empty list
                if (!_conversationHistory.ContainsKey(speakerName))
                {
                    _conversationHistory[speakerName] = new List<ConversationMessage>();
                }
            }
        }

        private static void CleanupOldConversations()
        {
            try
            {
                var cutoffTime = DateTime.Now - _conversationTimeout; // CHANGED: Use local time for consistency
                var speakersToRemove = new List<string>();
                
                foreach (var kvp in _conversationHistory)
                {
                    var speaker = kvp.Key;
                    var messages = kvp.Value;
                    
                    // Check if conversation is too old
                    if (messages.Any())
                    {
                        var lastActivity = messages.Max(m => m.Timestamp);
                        if (lastActivity < cutoffTime)
                        {
                            speakersToRemove.Add(speaker);
                        }
                    }
                    else
                    {
                        // Empty conversation, remove it
                        speakersToRemove.Add(speaker);
                    }
                }
                
                // Remove old conversations
                foreach (var speaker in speakersToRemove)
                {
                    var messageCount = _conversationHistory[speaker].Count;
                    _conversationHistory.Remove(speaker);
                    
                    Console.WriteLine($"?? Cleaned up old conversation for {speaker} ({messageCount} messages)");
                }
                
                // Update the conversation file on disk if we removed speakers
                if (speakersToRemove.Count > 0)
                {
                    SaveConversationToDiskLegacy();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error during conversation cleanup: {ex.Message}");
            }
        }

        private static void TrimConversation(string speakerName)
        {
            try
            {
                if (!_conversationHistory.ContainsKey(speakerName))
                    return;
                
                var conversation = _conversationHistory[speakerName];
                
                // If conversation is too long, remove oldest messages (but keep system messages)
                while (conversation.Count > _maxMessagesPerSpeaker)
                {
                    // Find the oldest non-system message to remove
                    var oldestUserOrAssistant = conversation
                        .Where(m => m.Role != "system")
                        .OrderBy(m => m.Timestamp)
                        .FirstOrDefault();
                    
                    if (oldestUserOrAssistant != null)
                    {
                        conversation.Remove(oldestUserOrAssistant);
                        Console.WriteLine($"??? Trimmed old {oldestUserOrAssistant.Role} message for {speakerName}");
                    }
                    else
                    {
                        // Only system messages left, trim those too if necessary
                        var oldestMessage = conversation.OrderBy(m => m.Timestamp).FirstOrDefault();
                        if (oldestMessage != null)
                        {
                            conversation.Remove(oldestMessage);
                            Console.WriteLine($"??? Trimmed old system message for {speakerName}");
                        }
                        else
                        {
                            break; // No messages to remove
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error trimming conversation for {speakerName}: {ex.Message}");
            }
        }

        private static void LoadAllConversationsFromDisk()
        {
            if (!_persistenceEnabled) return;
            
            try
            {
                var filePath = GetConversationFilePath();
                
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"?? No existing conversation file found at: {filePath}");
                    return;
                }
                
                var json = File.ReadAllText(filePath, Encoding.UTF8);
                Console.WriteLine($"?? Reading conversation file: {filePath}");
                Console.WriteLine($"?? File content length: {json.Length} characters");
                
                // Handle empty or minimal JSON files
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
                {
                    Console.WriteLine($"?? Conversation file is empty or contains only empty JSON, starting fresh");
                    return;
                }
                
                // FIXED: Add better validation for JSON content
                json = json.Trim();
                if (!json.StartsWith("{") || !json.EndsWith("}"))
                {
                    Console.WriteLine($"?? Conversation file appears corrupted (invalid JSON structure), backing up and starting fresh");
                    BackupAndRecreateConversationFile(filePath, json);
                    return;
                }
                
                // FIXED: Add try-catch specifically for JSON deserialization with recovery
                Dictionary<string, List<ConversationMessage>> conversationData = null;
                try
                {
                    conversationData = JsonConvert.DeserializeObject<Dictionary<string, List<ConversationMessage>>>(json);
                }
                catch (JsonException jsonEx)
                {
                    Console.WriteLine($"?? JSON deserialization failed: {jsonEx.Message}");
                    Console.WriteLine($"?? File content preview: {json.Substring(0, Math.Min(200, json.Length))}...");
                    
                    // Try to recover by creating a backup and starting fresh
                    BackupAndRecreateConversationFile(filePath, json);
                    return;
                }
                catch (Exception deserializeEx)
                {
                    Console.WriteLine($"?? Unexpected deserialization error: {deserializeEx.Message}");
                    BackupAndRecreateConversationFile(filePath, json);
                    return;
                }
                
                if (conversationData != null)
                {
                    foreach (var kvp in conversationData)
                    {
                        var speakerName = kvp.Key;
                        var messages = kvp.Value;
                        
                        if (messages != null && messages.Count > 0)
                        {
                            _conversationHistory[speakerName] = messages;
                            Console.WriteLine($"?? Loaded {messages.Count} messages for speaker: {speakerName}");
                        }
                    }
                    
                    var totalMessages = _conversationHistory.Values.Sum(msgs => msgs.Count);
                    Console.WriteLine($"?? Loaded {_conversationHistory.Count} speakers with {totalMessages} total messages from disk");
                    
                    // NEW: Automatically migrate UTC timestamps to local time if needed
                    MigrateUtcTimestampsToLocal();
                }
                else
                {
                    Console.WriteLine($"?? Failed to deserialize conversation data (null result)");
                    BackupAndRecreateConversationFile(filePath, json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error loading conversations from disk: {ex.Message}");
                Console.WriteLine($"   File path: {GetConversationFilePath()}");
                Console.WriteLine($"   Stack trace: {ex.StackTrace}");
                
                // Try to backup and recreate the file
                try
                {
                    var filePath = GetConversationFilePath();
                    if (File.Exists(filePath))
                    {
                        var corruptedContent = File.ReadAllText(filePath, Encoding.UTF8);
                        BackupAndRecreateConversationFile(filePath, corruptedContent);
                    }
                }
                catch (Exception backupEx)
                {
                    Console.WriteLine($"? Failed to backup corrupted file: {backupEx.Message}");
                }
            }
        }
        
        /// <summary>
        /// NEW: Backup corrupted conversation file and create a fresh one
        /// </summary>
        private static void BackupAndRecreateConversationFile(string filePath, string corruptedContent)
        {
            try
            {
                lock (_fileLock)
                {
                    // Create backup with timestamp
                    var backupFileName = $"conversation_corrupted_{DateTime.Now:yyyyMMdd_HHmmss}.json.bak"; // CHANGED: Use local time
                    var backupPath = Path.Combine(_historyDirectory, backupFileName);
                    
                    File.WriteAllText(backupPath, corruptedContent, Encoding.UTF8);
                    Console.WriteLine($"?? Backed up corrupted conversation file to: {backupFileName}");
                    
                    // Create fresh conversation file
                    var freshData = new Dictionary<string, List<ConversationMessage>>();
                    var freshJson = JsonConvert.SerializeObject(freshData, Formatting.Indented);
                    File.WriteAllText(filePath, freshJson, Encoding.UTF8);
                    
                    Console.WriteLine($"?? Created fresh conversation file");
                    Console.WriteLine($"?? Previous conversations were backed up and can be manually recovered if needed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error backing up corrupted file: {ex.Message}");
                
                // Last resort: delete the corrupted file
                try
                {
                    File.Delete(filePath);
                    Console.WriteLine($"??? Deleted corrupted conversation file as last resort");
                }
                catch (Exception deleteEx)
                {
                    Console.WriteLine($"? Failed to delete corrupted file: {deleteEx.Message}");
                    _persistenceEnabled = false;
                    Console.WriteLine($"? Disabling persistence due to file system issues");
                }
            }
        }

        private static bool LoadConversationFromDisk(string speakerName)
        {
            if (!_persistenceEnabled) return false;
            
            try
            {
                lock (_fileLock)
                {
                    var filePath = GetConversationFilePath();
                    
                    if (!File.Exists(filePath))
                        return false;
                    
                    var json = File.ReadAllText(filePath, Encoding.UTF8);
                    var conversationData = JsonConvert.DeserializeObject<Dictionary<string, List<ConversationMessage>>>(json);
                    
                    if (conversationData != null && conversationData.ContainsKey(speakerName))
                    {
                        var messages = conversationData[speakerName];
                        if (messages != null)
                        {
                            _conversationHistory[speakerName] = messages;
                            Console.WriteLine($"?? Loaded {messages.Count} messages for {speakerName} from disk");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error loading conversation for {speakerName}: {ex.Message}");
            }
            
            return false;
        }

        private static void SaveConversationToDiskLegacy()
        {
            if (!_persistenceEnabled) return;
            
            try
            {
                lock (_fileLock)
                {
                    var filePath = GetConversationFilePath();
                    var conversationData = CreateConversationFileData();
                    
                    var json = JsonConvert.SerializeObject(conversationData, Formatting.Indented);
                    File.WriteAllText(filePath, json, Encoding.UTF8);
                    Console.WriteLine($"?? Saved complete conversation (legacy method)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error saving conversation: {ex.Message}");
            }
        }

        private static Dictionary<string, List<ConversationMessage>> CreateConversationFileData()
        {
            var fileData = new Dictionary<string, List<ConversationMessage>>();
            
            foreach (var kvp in _conversationHistory)
            {
                var speaker = kvp.Key;
                var messages = kvp.Value;
                
                if (messages != null && messages.Count > 0)
                {
                    fileData[speaker] = messages.ToList(); // Create a copy
                }
            }
            
            return fileData;
        }

        // Expose the essential public methods
        public static void AddUserMessage(string speakerName, string message)
        {
            if (string.IsNullOrWhiteSpace(speakerName) || string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine($"? Invalid user message: speaker='{speakerName}', message='{message?.Substring(0, Math.Min(50, message?.Length ?? 0))}'");
                return;
            }
            
            try
            {
                speakerName = NormalizeSpeakerName(speakerName);
                EnsureConversationExists(speakerName);
                CleanupOldConversations();
                
                var userMessage = new ConversationMessage
                {
                    Role = "user",
                    Content = message.Trim(),
                    Timestamp = DateTime.Now, // CHANGED: Use local time instead of UTC
                    Speaker = speakerName
                };
                
                if (!_conversationHistory.ContainsKey(speakerName))
                {
                    _conversationHistory[speakerName] = new List<ConversationMessage>();
                }
                
                _conversationHistory[speakerName].Add(userMessage);
                TrimConversation(speakerName);
                SaveConversationToDiskLegacy();
                
                Console.WriteLine($"?? Added user message for {speakerName}: '{message.Substring(0, Math.Min(50, message.Length))}...'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error adding user message for {speakerName}: {ex.Message}");
            }
        }
        
        public static void AddAssistantMessage(string speakerName, string response)
        {
            if (string.IsNullOrWhiteSpace(speakerName) || string.IsNullOrWhiteSpace(response))
            {
                Console.WriteLine($"? Invalid assistant message: speaker='{speakerName}', response='{response?.Substring(0, Math.Min(50, response?.Length ?? 0))}'");
                return;
            }
            
            try
            {
                speakerName = NormalizeSpeakerName(speakerName);
                EnsureConversationExists(speakerName);
                
                var assistantMessage = new ConversationMessage
                {
                    Role = "assistant",
                    Content = response.Trim(),
                    Timestamp = DateTime.Now, // CHANGED: Use local time instead of UTC
                    Speaker = speakerName
                };
                
                if (!_conversationHistory.ContainsKey(speakerName))
                {
                    _conversationHistory[speakerName] = new List<ConversationMessage>();
                }
                
                _conversationHistory[speakerName].Add(assistantMessage);
                TrimConversation(speakerName);
                SaveConversationToDiskLegacy();
                
                Console.WriteLine($"?? Added assistant response for {speakerName}: '{response.Substring(0, Math.Min(50, response.Length))}...'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error adding assistant message for {speakerName}: {ex.Message}");
            }
        }

        public static string CreateOllamaJsonWithMemory(string model, string systemPrompt, string speakerName, string userMessage)
        {
            try
            {
                speakerName = NormalizeSpeakerName(speakerName);
                CleanupOldConversations();
                EnsureConversationExists(speakerName);
                
                var messagesArray = new JArray();
                
                // Add system message
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    var systemMessage = new JObject();
                    systemMessage["role"] = "system";
                    systemMessage["content"] = systemPrompt.Trim();
                    messagesArray.Add(systemMessage);
                }
                
                // Add conversation history
                var history = _conversationHistory.ContainsKey(speakerName) ? _conversationHistory[speakerName] : new List<ConversationMessage>();
                foreach (var msg in history)
                {
                    var historyMessage = new JObject();
                    historyMessage["role"] = msg.Role;
                    historyMessage["content"] = msg.Content;
                    messagesArray.Add(historyMessage);
                }
                
                // Add current user message
                var currentMessage = new JObject();
                currentMessage["role"] = "user";
                currentMessage["content"] = userMessage;
                messagesArray.Add(currentMessage);
                
                var jsonObject = new JObject();
                jsonObject["model"] = model;
                jsonObject["stream"] = false;
                jsonObject["messages"] = messagesArray;
                
                var options = new JObject();
                options["temperature"] = 0.7;
                options["top_p"] = 0.9;
                options["top_k"] = 40;
                jsonObject["options"] = options;
                
                Console.WriteLine($"?? Created Ollama request with memory for {speakerName}: {messagesArray.Count} messages");
                
                return jsonObject.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error creating Ollama JSON with memory: {ex.Message}");
                return JsonUtils.CreateOllamaJson(model, systemPrompt, userMessage);
            }
        }

        public static string GetConversationStats()
        {
            try
            {
                var stats = new List<string>();
                stats.Add($"?? Conversation Memory Statistics:");
                stats.Add($"   Active speakers: {_conversationHistory.Count}");
                stats.Add($"   Persistence: {(_persistenceEnabled ? "enabled" : "disabled")} ? {_historyDirectory}");
                
                var totalMessages = 0;
                foreach (var kvp in _conversationHistory.OrderBy(x => x.Key))
                {
                    var speaker = kvp.Key;
                    var messages = kvp.Value;
                    var lastActivity = messages.Any() ? messages.Max(m => m.Timestamp) : DateTime.MinValue;
                    var userMessages = messages.Count(m => m.Role == "user");
                    var assistantMessages = messages.Count(m => m.Role == "assistant");
                    
                    totalMessages += messages.Count;
                    stats.Add($"   {speaker}: {messages.Count} messages ({userMessages}U/{assistantMessages}A), Last: {lastActivity:HH:mm:ss}");
                }
                
                stats.Add($"   TOTAL: {totalMessages} messages");
                return string.Join("\n", stats);
            }
            catch (Exception ex)
            {
                return $"? Error getting conversation stats: {ex.Message}";
            }
        }

        public static void TestConversationSaving()
        {
            try
            {
                Console.WriteLine($"?? Testing conversation saving...");
                
                var testSpeaker = "TestUser";
                var testMessage = "Hello, this is a test message to verify conversation saving is working.";
                var testResponse = "Hello! I can confirm that the conversation manager is working properly and saving messages.";
                
                AddUserMessage(testSpeaker, testMessage);
                AddAssistantMessage(testSpeaker, testResponse);
                
                Console.WriteLine($"?? Final conversation statistics:");
                Console.WriteLine(GetConversationStats());
                
                var filePath = GetConversationFilePath();
                if (File.Exists(filePath))
                {
                    var content = File.ReadAllText(filePath, Encoding.UTF8);
                    Console.WriteLine($"? Conversation file exists with {content.Length} characters");
                }
                else
                {
                    Console.WriteLine($"? Conversation file does not exist!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Test failed: {ex.Message}");
            }
        }

        public static string GetManagerStatus()
        {
            try
            {
                var status = new List<string>();
                status.Add("?? Conversation Manager Status:");
                status.Add($"   Persistence enabled: {_persistenceEnabled}");
                status.Add($"   History directory exists: {Directory.Exists(_historyDirectory)}");
                status.Add($"   History directory: {_historyDirectory}");
                status.Add($"   Conversation file exists: {File.Exists(GetConversationFilePath())}");
                status.Add($"   Active speakers in memory: {_conversationHistory.Count}");
                status.Add($"   Total messages in memory: {_conversationHistory.Values.Sum(msgs => msgs.Count)}");
                
                return string.Join("\n", status);
            }
            catch (Exception ex)
            {
                return $"? Error getting manager status: {ex.Message}";
            }
        }

        public static void ClearSpeakerHistory(string speakerName)
        {
            try
            {
                speakerName = NormalizeSpeakerName(speakerName);
                
                if (_conversationHistory.ContainsKey(speakerName))
                {
                    var messageCount = _conversationHistory[speakerName].Count;
                    _conversationHistory.Remove(speakerName);
                    SaveConversationToDiskLegacy();
                    Console.WriteLine($"??? Cleared {messageCount} messages for speaker: {speakerName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error clearing speaker history for {speakerName}: {ex.Message}");
            }
        }
        
        public static void ClearAllHistory()
        {
            try
            {
                var totalMessages = _conversationHistory.Values.Sum(h => h.Count);
                var totalSpeakers = _conversationHistory.Count;
                
                _conversationHistory.Clear();
                SaveConversationToDiskLegacy();
                
                Console.WriteLine($"??? Cleared all conversation history: {totalMessages} messages from {totalSpeakers} speakers");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error clearing all history: {ex.Message}");
            }
        }
        
        public static List<string> GetRecentContext(string speakerName, int maxMessages = 6)
        {
            try
            {
                speakerName = NormalizeSpeakerName(speakerName);
                
                if (!_conversationHistory.ContainsKey(speakerName))
                {
                    LoadConversationFromDisk(speakerName);
                }
                
                if (!_conversationHistory.ContainsKey(speakerName))
                    return new List<string>();
                
                var messages = _conversationHistory[speakerName];
                var recentMessages = messages.Count > maxMessages 
                    ? messages.Skip(messages.Count - maxMessages).ToList()
                    : messages.ToList();
                
                return recentMessages.Select(m => $"{m.Role}: {m.Content}").ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error getting recent context for {speakerName}: {ex.Message}");
                return new List<string>();
            }
        }
        
        public static List<string> GetAllSpeakersWithHistory()
        {
            try
            {
                return _conversationHistory.Keys.OrderBy(s => s).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error getting speakers with history: {ex.Message}");
                return new List<string>();
            }
        }
        
        /// <summary>
        /// Migrate existing UTC timestamps to local time (one-time operation)
        /// Call this method once if you have existing conversations with UTC timestamps
        /// </summary>
        public static void MigrateUtcTimestampsToLocal()
        {
            try
            {
                Console.WriteLine("?? Starting UTC to local time migration...");
                bool migrationNeeded = false;
                int totalMigratedMessages = 0;
                
                foreach (var kvp in _conversationHistory)
                {
                    var speaker = kvp.Key;
                    var messages = kvp.Value;
                    
                    foreach (var message in messages)
                    {
                        // Check if the timestamp looks like UTC (has Kind = Utc or is very close to UTC time)
                        if (message.Timestamp.Kind == DateTimeKind.Utc)
                        {
                            // Convert UTC to local time
                            message.Timestamp = message.Timestamp.ToLocalTime();
                            migrationNeeded = true;
                            totalMigratedMessages++;
                        }
                        else if (message.Timestamp.Kind == DateTimeKind.Unspecified)
                        {
                            // For unspecified timestamps, check if they seem to be UTC by comparing with current time
                            var timeDiffFromNow = Math.Abs((DateTime.UtcNow - message.Timestamp).TotalHours);
                            var timeDiffFromLocal = Math.Abs((DateTime.Now - message.Timestamp).TotalHours);
                            
                            // If the timestamp is much closer to UTC time than local time, assume it's UTC
                            if (timeDiffFromNow < timeDiffFromLocal - 1) // 1 hour tolerance
                            {
                                // Treat as UTC and convert to local
                                message.Timestamp = DateTime.SpecifyKind(message.Timestamp, DateTimeKind.Utc).ToLocalTime();
                                migrationNeeded = true;
                                totalMigratedMessages++;
                                Console.WriteLine($"   Migrated message from {speaker}: {message.Timestamp:yyyy-MM-dd HH:mm:ss}");
                            }
                        }
                    }
                }
                
                if (migrationNeeded)
                {
                    // Save the migrated timestamps to disk
                    SaveConversationToDiskLegacy();
                    Console.WriteLine($"? Migration completed: {totalMigratedMessages} messages migrated to local time");
                    Console.WriteLine($"?? Updated conversation file saved to disk");
                }
                else
                {
                    Console.WriteLine($"?? No migration needed - all timestamps are already in local time");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error during timestamp migration: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Represents a single message in a conversation
    /// </summary>
    public class ConversationMessage
    {
        public string Role { get; set; } = string.Empty;        // "system", "user", or "assistant"
        public string Content { get; set; } = string.Empty;     // The message content
        public DateTime Timestamp { get; set; } = DateTime.Now; // CHANGED: Use local time instead of UTC
        public string Speaker { get; set; } = string.Empty;     // Who spoke (for user messages)
    }
}