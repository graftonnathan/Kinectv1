# Conversation History Directory

This directory stores persistent conversation history for all speakers that interact with the Ollama AI system.

## ? FIXED: Conversation Manager Issues

The OllamaConversationManager has been updated to resolve saving issues:
- **Fixed path resolution**: Now correctly saves to project root `history/` directory instead of output directory
- **Improved error handling**: Better logging and fallback mechanisms for file operations
- **Enhanced persistence**: Robust file creation and updating with duplicate detection
- **Better initialization**: Automatic project root detection using .csproj, App.config, and App.xaml files

## File Format
- **Files**: `conversation.json` (single file for all speakers), with archive files `conversation.1.json`, `conversation.2.json`, etc.
- **Content**: JSON object with speaker names as keys and arrays of conversation messages as values
- **Encoding**: UTF-8

## Structure
The conversation file contains an object with all speakers and their messages:
```json
{
  "Nathan": [
    {
      "Role": "user",
      "Content": "Hello, how are you?",
      "Timestamp": "2024-01-15T10:30:00.000Z",
      "Speaker": "Nathan"
    },
    {
      "Role": "assistant", 
      "Content": "I'm doing well, thank you for asking!",
      "Timestamp": "2024-01-15T10:30:05.000Z",
      "Speaker": "Nathan"
    }
  ],
  "Alice": [
    {
      "Role": "user",
      "Content": "What's the weather like?",
      "Timestamp": "2024-01-15T10:35:00.000Z",
      "Speaker": "Alice"
    },
    {
      "Role": "assistant", 
      "Content": "I don't have access to current weather data.",
      "Timestamp": "2024-01-15T10:35:05.000Z",
      "Speaker": "Alice"
    }
  ]
}
```

## Token Management & Archiving
- **Single File Storage**: All speakers are stored in one `conversation.json` file
- **Token Limits**: When the total token count across all speakers exceeds the configured limit, the entire conversation file is archived
- **Archiving**: Current conversation is saved as `conversation.1.json`, `conversation.2.json`, etc., and a new `conversation.json` is started
- **Per-Speaker Limits**: Individual speakers still have message count limits within the current conversation file

## Management
- **Automatic**: Conversations are automatically saved after each exchange
- **Cleanup**: Old conversations are automatically removed based on timeout settings
- **Manual**: Use `OllamaService.ClearSpeakerConversation(speakerName)` or `OllamaService.ClearAllConversations()`
- **Archiving**: Automatically creates new conversation files when token limits are reached

## Settings
- Max messages per speaker: Configurable via `OllamaMaxMessagesPerSpeaker` in App.config
- Max tokens per conversation file: Configurable via `OllamaMaxTokensPerConversation` in App.config
- Conversation timeout: Configurable via `OllamaConversationTimeoutMinutes` in App.config
- Enable/disable: Configurable via `OllamaMemoryEnabled` in App.config
- Archive on token limit: Configurable via `CreateNewFileOnLimit` parameter in ConfigureMemory method

## Debugging Information
The conversation manager now provides extensive logging for troubleshooting:
- Project root detection logs
- File path resolution details
- Message saving confirmation
- Error handling with fallback mechanisms
- Conversation statistics and token counting

When conversation memory is enabled, you'll see detailed logs in the console showing:
- `?? Conversation manager initialized` - Shows project root detection
- `?? Adding user message` - User input being stored
- `?? Adding assistant response` - AI responses being saved
- `?? Conversation stats` - Current message counts and token usage
- `? Updated conversation file successfully` - Confirmation of successful saves