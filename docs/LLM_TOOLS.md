# LLM Tools System

## Overview

The LLM tools system allows the language model to execute external actions like web searches to gather real-time information. This enables the AI to answer questions about current events, verify facts, and provide up-to-date information.

## Architecture

```
???????????????????
?   User Query    ?
???????????????????
         ?
         ?
???????????????????     ????????????????????
?  OllamaService  ???????  System Prompt   ?
? (ConversationMgr?     ?  + Tool Prompt   ?
???????????????????     ????????????????????
         ?
         ?
???????????????????
?   LLM Router    ? ???? Ollama / LM Studio
???????????????????
         ?
         ? (response with tool calls)
???????????????????
?  ToolRegistry   ?
? - ParseToolCalls?
? - ExecuteToolAsync
???????????????????
         ?
         ?
???????????????????     ????????????????????
?  WebSearchTool  ??????? 1. Search DDG    ?
???????????????????     ? 2. Fetch Articles?
         ?              ? 3. Extract Text  ?
         ?              ????????????????????
         ?
???????????????????
? Continue Response? (with article content)
???????????????????
```

## Configuration

Enable tools in `settings.json`:

```json
{
  "ollama": {
    "toolsEnabled": true,
    // ... other settings
  }
}
```

## Available Tools

### Web Search (`web_search`)

Searches the web using DuckDuckGo, fetches the best articles, extracts their content, and provides it to the LLM for summarization.

**Parameters:**
```json
{"query": "search query to find information"}
```

**Example usage by LLM:**
```
```tool
{"tool": "web_search", "parameters": {"query": "latest news about AI regulations 2024"}}
```
```

**How it works:**
1. **Search** - Queries DuckDuckGo HTML search
2. **Filter** - Skips video sites, social media, PDFs
3. **Fetch** - Downloads top 3 article pages
4. **Extract** - Pulls main content from `<article>`, `<main>`, or content divs
5. **Return** - Provides article text to LLM for summarization

**Returns:** Formatted article content with:
- Best article's full text (up to 3000 chars)
- Source URL and title
- Additional source summaries
- Instruction to cite sources

**Example output:**
```
## Search Results for: "latest AI news"

### Best Source: OpenAI Announces GPT-5 Release Date
URL: https://example.com/ai-news/gpt5

**Article Content:**
OpenAI has announced that GPT-5 will be released in Q3 2024...
[Full article text extracted from the page]

### Additional Sources:
- **Google Releases Gemini 2.0** (https://...)
  Summary of the article...

---
Use the above information to answer the user's question. Cite sources when appropriate.
```

## Tool Response Format

When the LLM needs to use a tool, it outputs a tool call block:

```
Let me search for that information.

```tool
{"tool": "web_search", "parameters": {"query": "OpenAI GPT-4o release date"}}
```
```

The system:
1. Detects the tool call block
2. Searches DuckDuckGo for results
3. Fetches and extracts content from top articles
4. Continues the conversation with the article content
5. LLM summarizes and incorporates results into the final response

## Content Extraction

The tool intelligently extracts article content:

| Priority | Element | Description |
|----------|---------|-------------|
| 1 | `<article>` | Most reliable for news/blog posts |
| 2 | `<main>` | Main content area |
| 3 | Content divs | Classes containing "article", "content", "post" |
| 4 | `<body>` | Fallback: strip all nav/footer/scripts |

**Extracted elements:**
- Headings (`<h1>` - `<h6>`) ? Formatted as `## Heading`
- Paragraphs (`<p>`) ? Plain text
- List items (`<li>`) ? Formatted as `• Item`

**Filtered out:**
- `<script>`, `<style>`, `<nav>`, `<header>`, `<footer>`, `<aside>`, `<form>`
- Comments
- Boilerplate text (cookie notices, subscribe prompts)

## URL Filtering

The tool skips URLs unlikely to have good text content:

| Skipped | Reason |
|---------|--------|
| youtube.com, tiktok.com | Video content |
| facebook.com, twitter.com, instagram.com | Social media |
| reddit.com/gallery | Image galleries |
| .pdf, .jpg, .png | Binary files |
| play.google.com, apps.apple.com | App stores |

## Adding New Tools

1. Create a class implementing `ILlmTool`:

```csharp
public class MyTool : ILlmTool
{
    public string Name => "my_tool";
    public string Description => "Description for the LLM";
    public string ParameterSchema => @"{""param1"": ""description""}";

    public async Task<string> ExecuteAsync(string parameters, CancellationToken ct = default)
    {
        var obj = JObject.Parse(parameters);
        var param1 = obj["param1"]?.ToString();
        
        // Do work...
        return "Result for LLM";
    }
}
```

2. Register in `ToolRegistry` constructor:

```csharp
private ToolRegistry()
{
    Register(new WebSearchTool());
    Register(new MyTool());
}
```

## Events

The system fires events during tool execution:

```csharp
OllamaService.OnToolExecutionStarted += (toolName, query) =>
    Console.WriteLine($"?? Executing {toolName}: {query}");

OllamaService.OnToolExecutionCompleted += (toolName, result) =>
    Console.WriteLine($"? {toolName} completed");
```

## Console Output

```
[WebSearch] Searching for: latest AI regulations 2024
[WebSearch] Found 5 results, fetching top articles...
[WebSearch] Fetching: https://example.com/ai-regulations
[WebSearch] Got 2847 chars from EU AI Act Takes Effect
[WebSearch] Fetching: https://news.site/ai-news
[WebSearch] Got 1923 chars from US Proposes AI Guidelines
[ToolRegistry] Tool web_search completed (4521 chars)
[OllamaService] Continuing with tool results (iteration 1)
```

## Limitations

- **Max iterations:** 3 tool calls per response (prevents infinite loops)
- **Timeout:** 10 seconds per article fetch
- **Content limits:** 3000 chars per article, 4000 chars total
- **No JavaScript:** Can't render JS-heavy pages (SPAs)
- **Rate limiting:** Heavy use may be throttled by DuckDuckGo

## Future Enhancements

- **Weather tool** - Current weather data via API
- **Calculator tool** - Complex math expressions
- **Wikipedia tool** - Direct Wikipedia API access
- **News API** - Dedicated news sources
- **Readability mode** - Better content extraction with Mozilla Readability
