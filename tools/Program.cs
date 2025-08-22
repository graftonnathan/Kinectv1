using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using Vosk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kinectv1.Tools
{
    /// <summary>
    /// Offline ASR testing tool for WAV->JSON transcription with golden result comparison
    /// Usage: AsrOffline.exe input.wav [--model path] [--golden path] [--verbose]
    /// </summary>
    class AsrOffline
    {
        private static bool _verbose = false;
        private static string _modelPath = null;
        
        static int Main(string[] args)
        {
            try
            {
                var result = ProcessArguments(args);
                if (result != 0) return result;

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                if (_verbose)
                {
                    Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
                }
                return 1;
            }
        }

        static int ProcessArguments(string[] args)
        {
            if (args.Length == 0)
            {
                ShowUsage();
                return 1;
            }

            // Check for special test modes
            if (args.Length == 1 && args[0] == "--test-debounce")
            {
                return DebounceTest.RunAllTests();
            }
            
            if (args.Length == 1 && args[0] == "--test-discord")
            {
                return SyntheticDiscordTest.TestSyntheticDiscordFrames();
            }
            
            if (args.Length == 1 && args[0] == "--test-all")
            {
                Console.WriteLine("🧪 Running All Test Suites");
                Console.WriteLine("==========================");
                
                var debounceResult = DebounceTest.RunAllTests();
                Console.WriteLine();
                
                var discordResult = SyntheticDiscordTest.TestSyntheticDiscordFrames();
                Console.WriteLine();
                
                if (debounceResult == 0 && discordResult == 0)
                {
                    Console.WriteLine("🎉 All test suites passed!");
                    return 0;
                }
                else
                {
                    Console.WriteLine("❌ Some test suites failed. Check output above for details.");
                    return 1;
                }
            }

            string inputWav = null;
            string goldenPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help":
                    case "-h":
                        ShowUsage();
                        return 0;
                    
                    case "--model":
                    case "-m":
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("ERROR: --model requires a path argument");
                            return 1;
                        }
                        _modelPath = args[++i];
                        break;
                    
                    case "--golden":
                    case "-g":
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("ERROR: --golden requires a path argument");
                            return 1;
                        }
                        goldenPath = args[++i];
                        break;
                    
                    case "--verbose":
                    case "-v":
                        _verbose = true;
                        break;
                    
                    default:
                        if (inputWav == null && !args[i].StartsWith("-"))
                        {
                            inputWav = args[i];
                        }
                        else
                        {
                            Console.Error.WriteLine($"ERROR: Unknown argument or multiple input files: {args[i]}");
                            return 1;
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(inputWav))
            {
                Console.Error.WriteLine("ERROR: Input WAV file is required");
                ShowUsage();
                return 1;
            }

            return ProcessWavFile(inputWav, goldenPath);
        }

        static void ShowUsage()
        {
            Console.WriteLine("ASR Offline Testing Tool");
            Console.WriteLine("Usage: AsrOffline.exe input.wav [options]");
            Console.WriteLine("       AsrOffline.exe --test-debounce");
            Console.WriteLine("       AsrOffline.exe --test-discord");
            Console.WriteLine("       AsrOffline.exe --test-all");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --model, -m <path>    Path to Vosk model directory");
            Console.WriteLine("  --golden, -g <path>   Path to golden result JSON for comparison");
            Console.WriteLine("  --verbose, -v         Enable verbose output");
            Console.WriteLine("  --test-debounce       Run debounce and pruning logic tests");
            Console.WriteLine("  --test-discord        Run synthetic Discord frame processing tests");
            Console.WriteLine("  --test-all            Run all test suites");
            Console.WriteLine("  --help, -h            Show this help message");
            Console.WriteLine();
            Console.WriteLine("Output: JSON with {text, confidence, tokens} to stdout");
            Console.WriteLine("Exit codes: 0=success, 1=error, 2=golden mismatch");
        }

        static int ProcessWavFile(string inputWav, string goldenPath)
        {
            if (!File.Exists(inputWav))
            {
                Console.Error.WriteLine($"ERROR: Input file not found: {inputWav}");
                return 1;
            }

            if (_verbose)
            {
                Console.Error.WriteLine($"Processing WAV file: {inputWav}");
            }

            // Find model path
            var modelPath = FindModelPath();
            if (modelPath == null)
            {
                Console.Error.WriteLine("ERROR: No Vosk model found. Use --model to specify path or place model in models/ directory");
                return 1;
            }

            if (_verbose)
            {
                Console.Error.WriteLine($"Using model: {modelPath}");
            }

            // Process audio file
            var result = ProcessAudioFile(inputWav, modelPath);
            if (result == null)
            {
                return 1;
            }

            // Output JSON result
            var jsonOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
            Console.WriteLine(jsonOutput);

            // Compare with golden result if provided
            if (!string.IsNullOrEmpty(goldenPath))
            {
                return CompareWithGolden(result, goldenPath);
            }

            return 0;
        }

        static string FindModelPath()
        {
            if (!string.IsNullOrEmpty(_modelPath))
            {
                if (Directory.Exists(_modelPath))
                {
                    return _modelPath;
                }
                Console.Error.WriteLine($"ERROR: Specified model path not found: {_modelPath}");
                return null;
            }

            // Search common model locations
            var searchPaths = new[]
            {
                "models",
                "../models",
                "../../models",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vosk", "models")
            };

            foreach (var searchPath in searchPaths)
            {
                if (Directory.Exists(searchPath))
                {
                    var modelDirs = Directory.GetDirectories(searchPath);
                    if (modelDirs.Length > 0)
                    {
                        var modelPath = modelDirs[0]; // Use first model found
                        if (_verbose)
                        {
                            Console.Error.WriteLine($"Auto-detected model: {modelPath}");
                        }
                        return modelPath;
                    }
                }
            }

            return null;
        }

        static AsrResult ProcessAudioFile(string inputWav, string modelPath)
        {
            try
            {
                // Load audio file
                using (var reader = new WaveFileReader(inputWav))
                {
                    if (_verbose)
                    {
                        Console.Error.WriteLine($"Audio format: {reader.WaveFormat.SampleRate}Hz, {reader.WaveFormat.Channels} channels, {reader.WaveFormat.BitsPerSample} bits");
                    }

                    // Convert to 16kHz mono if needed
                    var targetFormat = new WaveFormat(16000, 16, 1);
                    var audioData = ConvertAudio(reader, targetFormat);

                    // Initialize Vosk
                    Vosk.Vosk.SetLogLevel(-1); // Reduce log noise
                    var model = new Model(modelPath);
                    var recognizer = new VoskRecognizer(model, 16000.0f);

                    if (_verbose)
                    {
                        Console.Error.WriteLine($"Processing {audioData.Length} bytes of audio data...");
                    }

                    // Process audio in chunks
                    var chunkSize = 4000; // ~250ms at 16kHz
                    var results = new List<VoskPartialResult>();
                    var finalText = "";

                    for (int i = 0; i < audioData.Length; i += chunkSize)
                    {
                        var remainingBytes = Math.Min(chunkSize, audioData.Length - i);
                        var chunk = new byte[remainingBytes];
                        Array.Copy(audioData, i, chunk, 0, remainingBytes);

                        if (recognizer.AcceptWaveform(chunk, chunk.Length))
                        {
                            var resultJson = recognizer.Result();
                            var partialResult = ParseVoskResult(resultJson);
                            if (!string.IsNullOrWhiteSpace(partialResult.Text))
                            {
                                results.Add(partialResult);
                                finalText += (finalText.Length > 0 ? " " : "") + partialResult.Text;
                            }
                        }
                    }

                    // Get final result
                    var finalJson = recognizer.FinalResult();
                    var finalResult = ParseVoskResult(finalJson);
                    if (!string.IsNullOrWhiteSpace(finalResult.Text))
                    {
                        finalText += (finalText.Length > 0 ? " " : "") + finalResult.Text;
                    }

                    // Calculate overall confidence
                    var avgConfidence = results.Count > 0 ? results.Average(r => r.Confidence) : finalResult.Confidence;

                    // Extract tokens
                    var allTokens = new List<TokenInfo>();
                    foreach (var result in results)
                    {
                        allTokens.AddRange(result.Tokens);
                    }
                    if (finalResult.Tokens.Count > 0)
                    {
                        allTokens.AddRange(finalResult.Tokens);
                    }

                    recognizer.Dispose();
                    model.Dispose();

                    return new AsrResult
                    {
                        Text = finalText.Trim(),
                        Confidence = avgConfidence,
                        Tokens = allTokens
                    };
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR processing audio file: {ex.Message}");
                if (_verbose)
                {
                    Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
                }
                return null;
            }
        }

        static byte[] ConvertAudio(WaveFileReader reader, WaveFormat targetFormat)
        {
            // Read all audio data
            var sourceProvider = reader.ToSampleProvider();
            
            // Convert to mono if needed
            if (sourceProvider.WaveFormat.Channels > 1)
            {
                sourceProvider = sourceProvider.ToMono();
            }

            // Resample if needed
            if (sourceProvider.WaveFormat.SampleRate != targetFormat.SampleRate)
            {
                sourceProvider = new WdlResamplingSampleProvider(sourceProvider, targetFormat.SampleRate);
            }

            // Convert to bytes
            var samples = new List<float>();
            var buffer = new float[1024];
            int samplesRead;
            
            while ((samplesRead = sourceProvider.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < samplesRead; i++)
                {
                    samples.Add(buffer[i]);
                }
            }

            // Convert float samples to 16-bit PCM bytes
            var audioData = new byte[samples.Count * 2];
            for (int i = 0; i < samples.Count; i++)
            {
                var sample = Math.Max(-1.0f, Math.Min(1.0f, samples[i])); // Clamp
                var intSample = (short)(sample * 32767);
                audioData[i * 2] = (byte)(intSample & 0xFF);
                audioData[i * 2 + 1] = (byte)((intSample >> 8) & 0xFF);
            }

            return audioData;
        }

        static VoskPartialResult ParseVoskResult(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                var text = obj["text"]?.ToString() ?? "";
                var confidence = 0.0f;
                var tokens = new List<TokenInfo>();

                // Extract confidence from result array if available
                var result = obj["result"];
                if (result is JArray resultArray)
                {
                    var confidenceSum = 0.0f;
                    var count = 0;
                    
                    foreach (var item in resultArray)
                    {
                        var conf = item["conf"]?.ToObject<float>() ?? 0.0f;
                        var word = item["word"]?.ToString() ?? "";
                        var start = item["start"]?.ToObject<float>() ?? 0.0f;
                        var end = item["end"]?.ToObject<float>() ?? 0.0f;
                        
                        if (!string.IsNullOrWhiteSpace(word))
                        {
                            tokens.Add(new TokenInfo
                            {
                                Word = word,
                                Confidence = conf,
                                Start = start,
                                End = end
                            });
                            confidenceSum += conf;
                            count++;
                        }
                    }
                    
                    confidence = count > 0 ? confidenceSum / count : 0.0f;
                }

                return new VoskPartialResult
                {
                    Text = text,
                    Confidence = confidence,
                    Tokens = tokens
                };
            }
            catch (Exception ex)
            {
                if (_verbose)
                {
                    Console.Error.WriteLine($"Warning: Failed to parse Vosk result JSON: {ex.Message}");
                }
                return new VoskPartialResult
                {
                    Text = "",
                    Confidence = 0.0f,
                    Tokens = new List<TokenInfo>()
                };
            }
        }

        static int CompareWithGolden(AsrResult result, string goldenPath)
        {
            try
            {
                if (!File.Exists(goldenPath))
                {
                    Console.Error.WriteLine($"ERROR: Golden result file not found: {goldenPath}");
                    return 1;
                }

                var goldenJson = File.ReadAllText(goldenPath);
                var golden = JsonConvert.DeserializeObject<AsrResult>(goldenJson);

                if (_verbose)
                {
                    Console.Error.WriteLine("Comparing with golden result...");
                    Console.Error.WriteLine($"  Expected: '{golden.Text}'");
                    Console.Error.WriteLine($"  Actual:   '{result.Text}'");
                }

                // Compare text (normalize whitespace)
                var expectedText = NormalizeText(golden.Text);
                var actualText = NormalizeText(result.Text);

                if (expectedText != actualText)
                {
                    Console.Error.WriteLine($"ERROR: Text mismatch");
                    Console.Error.WriteLine($"  Expected: '{expectedText}'");
                    Console.Error.WriteLine($"  Actual:   '{actualText}'");
                    return 2;
                }

                // Check confidence is reasonable (within 0.2 of expected)
                var confDiff = Math.Abs(result.Confidence - golden.Confidence);
                if (confDiff > 0.2f)
                {
                    Console.Error.WriteLine($"WARNING: Confidence differs significantly");
                    Console.Error.WriteLine($"  Expected: {golden.Confidence:F3}");
                    Console.Error.WriteLine($"  Actual:   {result.Confidence:F3}");
                    Console.Error.WriteLine($"  Difference: {confDiff:F3}");
                }

                if (_verbose)
                {
                    Console.Error.WriteLine("Golden comparison passed");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR comparing with golden result: {ex.Message}");
                return 1;
            }
        }

        static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            
            // Normalize whitespace and case
            return string.Join(" ", text.Trim().ToLowerInvariant().Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    public class AsrResult
    {
        public string Text { get; set; }
        public float Confidence { get; set; }
        public List<TokenInfo> Tokens { get; set; } = new List<TokenInfo>();
    }

    public class VoskPartialResult
    {
        public string Text { get; set; }
        public float Confidence { get; set; }
        public List<TokenInfo> Tokens { get; set; } = new List<TokenInfo>();
    }

    public class TokenInfo
    {
        public string Word { get; set; }
        public float Confidence { get; set; }
        public float Start { get; set; }
        public float End { get; set; }
    }
}