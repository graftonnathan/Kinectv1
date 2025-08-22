using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Kinectv1.Tests
{
    /// <summary>
    /// Unit tests for eSpeak IPA stability improvements
    /// </summary>
    public static class EspeakIpaStabilityTest
    {
        public static void RunTests()
        {
            Console.WriteLine("[Test] Running eSpeak IPA stability tests...");
            
            try
            {
                TestInputSanitization();
                TestIpaNormalization();
                TestCacheFunctionality();
                TestSimpleG2PFallback();
                
                Console.WriteLine("[Test] ✅ All eSpeak IPA stability tests passed!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Test] ❌ Test failed: {ex.Message}");
                throw;
            }
        }
        
        private static void TestInputSanitization()
        {
            Console.WriteLine("[Test] Testing input sanitization...");
            
            var testCases = new Dictionary<string, string>
            {
                {"\"Hello world\"", "\"Hello world\""},
                {"Smart 'quotes' test", "Smart 'quotes' test"},
                {"Em—dash and en–dash", "Em-dash and en-dash"},
                {"Ellipsis…", "Ellipsis..."},
                {"Quotes «text» here", "Quotes \"text\" here"}
            };
            
            foreach (var test in testCases)
            {
                var result = SanitizeInputTextTest(test.Key);
                if (result != test.Value)
                {
                    throw new Exception($"Input sanitization failed for '{test.Key}'. Expected '{test.Value}', got '{result}'");
                }
            }
            
            Console.WriteLine("[Test] Input sanitization tests passed");
        }
        
        private static void TestIpaNormalization()
        {
            Console.WriteLine("[Test] Testing IPA normalization...");
            
            var testCases = new[]
            {
                ("həˈloʊ / wɜrld", "həˈloʊ wɜrld"),
                ("test Γòö bad", "test bad"),
                ("normal IPA həˈloʊ", "normal IPA həˈloʊ"),
                (" /spaced/ text ", "spaced text")
            };
            
            foreach (var (input, expected) in testCases)
            {
                var result = NormalizeIpaTest(input);
                if (result != expected)
                {
                    throw new Exception($"IPA normalization failed for '{input}'. Expected '{expected}', got '{result}'");
                }
            }
            
            Console.WriteLine("[Test] IPA normalization tests passed");
        }
        
        private static void TestCacheFunctionality()
        {
            Console.WriteLine("[Test] Testing cache functionality...");
            
            // Cache should be empty initially
            var testText = "test cache";
            var cacheResult = TryGetCachedIpaTest(testText, out string cachedIpa);
            if (cacheResult)
            {
                throw new Exception("Cache should be empty initially");
            }
            
            // Add to cache
            var testIpa = "tɛst kæʃ";
            CacheIpaTest(testText, testIpa);
            
            // Should now be in cache
            cacheResult = TryGetCachedIpaTest(testText, out cachedIpa);
            if (!cacheResult || cachedIpa != testIpa)
            {
                throw new Exception("Cache should contain the added entry");
            }
            
            Console.WriteLine("[Test] Cache functionality tests passed");
        }
        
        private static void TestSimpleG2PFallback()
        {
            Console.WriteLine("[Test] Testing simple G2P fallback...");
            
            var testWords = new[] { "hello", "world", "test", "chat" };
            
            foreach (var word in testWords)
            {
                var result = ApplySimpleG2PTest(word);
                if (string.IsNullOrWhiteSpace(result))
                {
                    throw new Exception($"G2P fallback should not return empty result for '{word}'");
                }
                
                // Should contain phonetic characters
                if (!result.Contains(" ") && result.Length <= 1)
                {
                    throw new Exception($"G2P result seems too short for '{word}': '{result}'");
                }
            }
            
            Console.WriteLine("[Test] Simple G2P fallback tests passed");
        }
        
        // Test versions of the methods from KokoroTtsService
        private static readonly Dictionary<string, string> _testCache = new Dictionary<string, string>();
        
        private static string SanitizeInputTextTest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            
            // Normalize smart quotes to ASCII
            text = text.Replace('\u201C', '"').Replace('\u201D', '"'); // “ ” -> "
            text = text.Replace('\u2018', '\'').Replace('\u2019', '\''); // ‘ ’ -> '
            text = text.Replace("–", "-").Replace("—", "-");
            text = text.Replace("…", "...");
            
            // Normalize other Unicode punctuation to ASCII equivalents
            text = text.Replace("«", "\"").Replace("»", "\"");
            text = text.Replace("‚", ",").Replace("„", "\"");
            text = text.Replace("‹", "'").Replace("›", "'");
            
            // Remove or replace problematic Unicode characters
            text = Regex.Replace(text, @"[^\x00-\x7F]+", " "); // Replace non-ASCII with space
            text = Regex.Replace(text, @"[\x00-\x1F\x7F]", " "); // Replace control chars with space
            text = Regex.Replace(text, @"\s+", " ").Trim(); // Normalize whitespace
            
            return text;
        }
        
        private static string NormalizeIpaTest(string ipa)
        {
            if (string.IsNullOrWhiteSpace(ipa)) return ipa;
            // Clean spacing and remove slashes, keep UTF-8 IPA intact
            ipa = ipa.Replace("/", " ");
            ipa = Regex.Replace(ipa, "\\s+", " ").Trim();
            
            // Filter out mojibake characters that shouldn't appear in IPA
            ipa = Regex.Replace(ipa, @"[Γòö├¬]", " ");
            ipa = Regex.Replace(ipa, @"\\x[0-9a-fA-F]{2}", " "); // Remove hex escape sequences
            ipa = Regex.Replace(ipa, "\\s+", " ").Trim();
            
            return ipa;
        }
        
        private static bool TryGetCachedIpaTest(string text, out string ipa)
        {
            ipa = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            
            string cacheKey = text.ToLowerInvariant().Trim();
            return _testCache.TryGetValue(cacheKey, out ipa);
        }
        
        private static void CacheIpaTest(string text, string ipa)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(ipa)) return;
            
            string cacheKey = text.ToLowerInvariant().Trim();
            _testCache[cacheKey] = ipa;
        }
        
        private static string ApplySimpleG2PTest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            
            // Simple grapheme-to-phoneme rules as final fallback
            text = text.ToLowerInvariant();
            
            // Basic English G2P mappings
            var g2pRules = new Dictionary<string, string>
            {
                {"ch", "tʃ"}, {"sh", "ʃ"}, {"th", "θ"}, {"ph", "f"},
                {"ck", "k"}, {"ng", "ŋ"}, {"qu", "kw"},
                {"a", "æ"}, {"e", "ɛ"}, {"i", "ɪ"}, {"o", "ɒ"}, {"u", "ʌ"},
                {"b", "b"}, {"c", "k"}, {"d", "d"}, {"f", "f"}, {"g", "g"},
                {"h", "h"}, {"j", "dʒ"}, {"k", "k"}, {"l", "l"}, {"m", "m"},
                {"n", "n"}, {"p", "p"}, {"r", "r"}, {"s", "s"}, {"t", "t"},
                {"v", "v"}, {"w", "w"}, {"x", "ks"}, {"y", "j"}, {"z", "z"}
            };
            
            var result = text;
            foreach (var rule in g2pRules)
            {
                result = result.Replace(rule.Key, rule.Value + " ");
            }
            
            result = Regex.Replace(result, @"\s+", " ").Trim();
            
            return result;
        }
    }
}