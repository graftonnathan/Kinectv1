using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Llm.Tools
{
    /// <summary>
    /// Web search tool that searches, fetches the best article, and extracts relevant content.
    /// Uses DuckDuckGo for search (no API key required) and fetches article content directly.
    /// </summary>
    public class WebSearchTool : ILlmTool
    {
        private static readonly HttpClient _httpClient;
        private const int MaxSearchResults = 5;
        private const int MaxArticlesToFetch = 3;
        private const int MaxContentPerArticle = 3000;
        private const int MaxTotalContent = 4000;

        static WebSearchTool()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
        }

        public string Name => "web_search";

        public string Description => "Search the web and retrieve article content to answer questions about current events, news, facts, or any topic you're unsure about.";

        public string ParameterSchema => @"{""query"": ""search query to find information""}";

        public async Task<string> ExecuteAsync(string parameters, CancellationToken ct = default)
        {
            string query;
            try
            {
                var obj = JObject.Parse(parameters);
                query = obj["query"]?.ToString();
            }
            catch
            {
                query = parameters?.Trim().Trim('"');
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return "Error: No search query provided";
            }

            Console.WriteLine($"[WebSearch] Searching for: {query}");

            try
            {
                // Step 1: Get search results
                var searchResults = await SearchDuckDuckGoAsync(query, ct).ConfigureAwait(false);
                
                if (searchResults.Count == 0)
                {
                    return $"No search results found for: {query}";
                }

                Console.WriteLine($"[WebSearch] Found {searchResults.Count} results, fetching top articles...");

                // Step 2: Fetch content from top results
                var articlesWithContent = new List<ArticleContent>();
                int fetched = 0;

                foreach (var result in searchResults.Take(MaxSearchResults))
                {
                    if (fetched >= MaxArticlesToFetch) break;
                    if (ct.IsCancellationRequested) break;

                    // Skip URLs that are unlikely to have good content
                    if (ShouldSkipUrl(result.Url)) continue;

                    try
                    {
                        Console.WriteLine($"[WebSearch] Fetching: {result.Url}");
                        var content = await FetchArticleContentAsync(result.Url, ct).ConfigureAwait(false);
                        
                        if (!string.IsNullOrWhiteSpace(content) && content.Length > 100)
                        {
                            articlesWithContent.Add(new ArticleContent
                            {
                                Title = result.Title,
                                Url = result.Url,
                                Content = content,
                                Snippet = result.Snippet
                            });
                            fetched++;
                            Console.WriteLine($"[WebSearch] Got {content.Length} chars from {result.Title}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WebSearch] Failed to fetch {result.Url}: {ex.Message}");
                    }
                }

                // Step 3: Build response with extracted content
                return BuildResponse(query, searchResults, articlesWithContent);
            }
            catch (TaskCanceledException)
            {
                return "Search cancelled";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSearch] Error: {ex.Message}");
                return $"Search failed: {ex.Message}";
            }
        }

        private string BuildResponse(string query, List<SearchResult> searchResults, List<ArticleContent> articles)
        {
            var sb = new StringBuilder();
            
            if (articles.Count > 0)
            {
                sb.AppendLine($"## Search Results for: \"{query}\"");
                sb.AppendLine();

                // Include the best article content
                var bestArticle = articles.First();
                sb.AppendLine($"### Best Source: {bestArticle.Title}");
                sb.AppendLine($"URL: {bestArticle.Url}");
                sb.AppendLine();
                sb.AppendLine("**Article Content:**");
                
                // Trim content to fit budget
                var content = bestArticle.Content;
                if (content.Length > MaxContentPerArticle)
                {
                    content = content.Substring(0, MaxContentPerArticle) + "...";
                }
                sb.AppendLine(content);
                sb.AppendLine();

                // Add summaries of other articles if we have room
                if (articles.Count > 1)
                {
                    sb.AppendLine("### Additional Sources:");
                    foreach (var article in articles.Skip(1))
                    {
                        sb.AppendLine($"- **{article.Title}** ({article.Url})");
                        if (!string.IsNullOrWhiteSpace(article.Snippet))
                        {
                            sb.AppendLine($"  {article.Snippet}");
                        }
                    }
                    sb.AppendLine();
                }
            }
            else
            {
                // Fallback: just return search snippets
                sb.AppendLine($"## Search Results for: \"{query}\"");
                sb.AppendLine("(Could not fetch article content, showing search snippets)");
                sb.AppendLine();

                foreach (var result in searchResults.Take(MaxSearchResults))
                {
                    sb.AppendLine($"**{result.Title}**");
                    if (!string.IsNullOrWhiteSpace(result.Snippet))
                    {
                        sb.AppendLine(result.Snippet);
                    }
                    sb.AppendLine($"Source: {result.Url}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---");
            sb.AppendLine("Use the above information to answer the user's question. Cite sources when appropriate.");

            var response = sb.ToString();
            if (response.Length > MaxTotalContent)
            {
                response = response.Substring(0, MaxTotalContent) + "\n[Content truncated]";
            }

            return response;
        }

        private bool ShouldSkipUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;

            var lowerUrl = url.ToLowerInvariant();
            
            // Skip video sites, social media, etc. that don't have good text content
            var skipDomains = new[] 
            { 
                "youtube.com", "youtu.be", "tiktok.com", "instagram.com", 
                "facebook.com", "twitter.com", "x.com", "pinterest.com",
                "reddit.com/gallery", ".pdf", ".jpg", ".png", ".gif",
                "play.google.com", "apps.apple.com"
            };

            return skipDomains.Any(d => lowerUrl.Contains(d));
        }

        private async Task<string> FetchArticleContentAsync(string url, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                
                if (!response.IsSuccessStatusCode)
                    return null;

                // Check content type
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.Contains("text/html") && !contentType.Contains("application/xhtml"))
                    return null;

                var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ExtractMainContent(html);
            }
            catch
            {
                return null;
            }
        }

        private string ExtractMainContent(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            // Remove script, style, nav, header, footer, aside elements
            html = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<nav[^>]*>[\s\S]*?</nav>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<header[^>]*>[\s\S]*?</header>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<footer[^>]*>[\s\S]*?</footer>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<aside[^>]*>[\s\S]*?</aside>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<form[^>]*>[\s\S]*?</form>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<!--[\s\S]*?-->", "", RegexOptions.IgnoreCase);

            // Try to find article or main content
            string mainContent = null;

            // Look for <article> tag first
            var articleMatch = Regex.Match(html, @"<article[^>]*>([\s\S]*?)</article>", RegexOptions.IgnoreCase);
            if (articleMatch.Success && articleMatch.Groups[1].Value.Length > 200)
            {
                mainContent = articleMatch.Groups[1].Value;
            }

            // Try <main> tag
            if (string.IsNullOrWhiteSpace(mainContent))
            {
                var mainMatch = Regex.Match(html, @"<main[^>]*>([\s\S]*?)</main>", RegexOptions.IgnoreCase);
                if (mainMatch.Success && mainMatch.Groups[1].Value.Length > 200)
                {
                    mainContent = mainMatch.Groups[1].Value;
                }
            }

            // Try common content div classes
            if (string.IsNullOrWhiteSpace(mainContent))
            {
                var contentPatterns = new[]
                {
                    @"<div[^>]*class=""[^""]*(?:article|content|post|entry|story)[^""]*""[^>]*>([\s\S]*?)</div>",
                    @"<div[^>]*id=""[^""]*(?:article|content|post|main)[^""]*""[^>]*>([\s\S]*?)</div>"
                };

                foreach (var pattern in contentPatterns)
                {
                    var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                    if (match.Success && match.Groups[1].Value.Length > 200)
                    {
                        mainContent = match.Groups[1].Value;
                        break;
                    }
                }
            }

            // Fallback: use the whole body
            if (string.IsNullOrWhiteSpace(mainContent))
            {
                var bodyMatch = Regex.Match(html, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                if (bodyMatch.Success)
                {
                    mainContent = bodyMatch.Groups[1].Value;
                }
                else
                {
                    mainContent = html;
                }
            }

            // Extract text from paragraphs and headings
            var paragraphs = new List<string>();
            
            // Get headings
            var headingMatches = Regex.Matches(mainContent, @"<h[1-6][^>]*>([\s\S]*?)</h[1-6]>", RegexOptions.IgnoreCase);
            foreach (Match m in headingMatches)
            {
                var text = CleanText(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 3)
                {
                    paragraphs.Add("## " + text);
                }
            }

            // Get paragraphs
            var pMatches = Regex.Matches(mainContent, @"<p[^>]*>([\s\S]*?)</p>", RegexOptions.IgnoreCase);
            foreach (Match m in pMatches)
            {
                var text = CleanText(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 30)
                {
                    paragraphs.Add(text);
                }
            }

            // Get list items
            var liMatches = Regex.Matches(mainContent, @"<li[^>]*>([\s\S]*?)</li>", RegexOptions.IgnoreCase);
            foreach (Match m in liMatches)
            {
                var text = CleanText(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 20)
                {
                    paragraphs.Add("• " + text);
                }
            }

            if (paragraphs.Count == 0)
            {
                // Last resort: just strip all tags
                var plainText = CleanText(mainContent);
                if (plainText.Length > 100)
                {
                    return plainText.Length > MaxContentPerArticle 
                        ? plainText.Substring(0, MaxContentPerArticle) 
                        : plainText;
                }
                return null;
            }

            return string.Join("\n\n", paragraphs);
        }

        private async Task<List<SearchResult>> SearchDuckDuckGoAsync(string query, CancellationToken ct)
        {
            var results = new List<SearchResult>();
            var encodedQuery = HttpUtility.UrlEncode(query);
            var url = $"https://html.duckduckgo.com/html/?q={encodedQuery}";

            try
            {
                var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                
                // Parse results with multiple patterns for robustness
                var resultRegex = new Regex(
                    @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>([^<]+)</a>.*?<a[^>]*class=""result__snippet""[^>]*>([^<]*)</a>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                var matches = resultRegex.Matches(response);
                
                foreach (Match match in matches)
                {
                    if (results.Count >= MaxSearchResults) break;

                    var rawUrl = match.Groups[1].Value;
                    var title = HttpUtility.HtmlDecode(match.Groups[2].Value.Trim());
                    var snippet = HttpUtility.HtmlDecode(match.Groups[3].Value.Trim());
                    var actualUrl = ExtractActualUrl(rawUrl);

                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(actualUrl))
                    {
                        results.Add(new SearchResult
                        {
                            Title = CleanText(title),
                            Snippet = CleanText(snippet),
                            Url = actualUrl
                        });
                    }
                }

                // Fallback pattern
                if (results.Count == 0)
                {
                    var fallbackRegex = new Regex(
                        @"<a[^>]*class=""[^""]*result[^""]*""[^>]*href=""([^""]+)""[^>]*>.*?<[^>]*>([^<]+)</",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    matches = fallbackRegex.Matches(response);
                    foreach (Match match in matches)
                    {
                        if (results.Count >= MaxSearchResults) break;
                        
                        var rawUrl = match.Groups[1].Value;
                        var title = HttpUtility.HtmlDecode(match.Groups[2].Value.Trim());
                        var actualUrl = ExtractActualUrl(rawUrl);

                        if (!string.IsNullOrWhiteSpace(title) && title.Length > 5 && !string.IsNullOrWhiteSpace(actualUrl))
                        {
                            results.Add(new SearchResult
                            {
                                Title = CleanText(title),
                                Url = actualUrl
                            });
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"[WebSearch] HTTP error: {ex.Message}");
                throw;
            }

            return results;
        }

        private static string ExtractActualUrl(string ddgUrl)
        {
            if (string.IsNullOrWhiteSpace(ddgUrl)) return ddgUrl;

            if (ddgUrl.Contains("uddg="))
            {
                var match = Regex.Match(ddgUrl, @"uddg=([^&]+)");
                if (match.Success)
                {
                    return HttpUtility.UrlDecode(match.Groups[1].Value);
                }
            }

            if (ddgUrl.StartsWith("//"))
            {
                return "https:" + ddgUrl;
            }

            return ddgUrl;
        }

        private static string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // Remove HTML tags
            text = Regex.Replace(text, @"<[^>]+>", " ");
            // Decode HTML entities
            text = HttpUtility.HtmlDecode(text);
            // Remove extra whitespace
            text = Regex.Replace(text, @"\s+", " ");
            // Remove common boilerplate phrases
            text = Regex.Replace(text, @"(?i)(click here|read more|subscribe|sign up|cookie|privacy policy|terms of service)", "");
            
            return text.Trim();
        }

        private class SearchResult
        {
            public string Title { get; set; }
            public string Snippet { get; set; }
            public string Url { get; set; }
        }

        private class ArticleContent
        {
            public string Title { get; set; }
            public string Url { get; set; }
            public string Content { get; set; }
            public string Snippet { get; set; }
        }
    }
}
