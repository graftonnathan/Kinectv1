using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Text.RegularExpressions;

namespace Kinectv1
{
    /// <summary>
    /// Simplified Coqui TTS ONNX service with proper G2P conversion
    /// </summary>
    public static class CoquiTtsService
    {
        private static InferenceSession _ttsModel;
        private static bool _isInitialized = false;
        private static bool _isEnabled = false;
        private static readonly object _lockObject = new object();
        private static string _currentModelPath = "";
        private static string _currentSpeaker = "1";
        private static bool _useGpu = false; // Track GPU preference - defaults to CPU mode
        
        // G2P Converter using your exact implementation
        private static Dictionary<string, string[]> _cmu;
        private static Dictionary<string, int> _sym2id;
        
        // Audio playback
        private static WaveOutEvent _waveOut;
        private static bool _isPlaying = false;
        
        // Events for UI integration
        public static event Action<string> OnTtsSpeakingStarted;
        public static event Action OnTtsSpeakingFinished;
        public static event Action<string> OnTtsError;
        
        /// <summary>
        /// Initialize the TTS service
        /// </summary>
        public static bool Initialize(string ttsModelPath, string cmudictPath = null, string symbolsPath = null)
        {
            lock (_lockObject)
            {
                try
                {
                    if (_isInitialized)
                    {
                        return true;
                    }

                    Console.WriteLine("?? Initializing Coqui TTS service...");

                    // Load GPU preference FIRST before loading model
                    _useGpu = AppSettings.LoadTtsUseGpu();
                    Console.WriteLine($"?? GPU preference: {(_useGpu ? "GPU" : "CPU")} mode");

                    // Load G2P components using your exact implementation
                    if (!LoadG2PComponents(cmudictPath, symbolsPath))
                    {
                        Console.WriteLine("? Failed to load G2P components");
                        return false;
                    }

                    // Load TTS model (now with correct GPU preference)
                    if (!LoadTtsModel(ttsModelPath))
                    {
                        Console.WriteLine("? Failed to load TTS model");
                        return false;
                    }

                    // Initialize audio playback
                    InitializeAudioPlayback();

                    _isInitialized = true;
                    _isEnabled = AppSettings.LoadTtsEnabled();
                    _currentModelPath = ttsModelPath;

                    Console.WriteLine($"? Coqui TTS service initialized successfully (Enabled: {_isEnabled}, Mode: {(_useGpu ? "GPU" : "CPU")})");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? TTS initialization failed: {ex.Message}");
                    OnTtsError?.Invoke($"TTS initialization failed: {ex.Message}");
                    Cleanup();
                    return false;
                }
            }
        }

        /// <summary>
        /// Load G2P components using your exact implementation pattern
        /// </summary>
        private static bool LoadG2PComponents(string cmudictPath, string symbolsPath)
        {
            try
            {
                // 1. load cmudict once at startup
                cmudictPath = cmudictPath ?? Path.Combine("models", "tts", "cmudict.dict");
                if (!File.Exists(cmudictPath))
                {
                    Console.WriteLine($"? CMU Dictionary not found at: {cmudictPath}");
                    Console.WriteLine("Please place cmudict.dict in models/tts/ folder");
                    return false;
                }

                // Enhanced parsing to handle both single and double space formats
                var lines = File.ReadAllLines(cmudictPath);
                
                _cmu = lines
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith(";;;"))
                    .Select(l => {
                        // Try double spaces first (original format), then single space
                        var parts = l.Split(new string[] { "  " }, 2, StringSplitOptions.None);
                        if (parts.Length != 2)
                        {
                            // Fall back to single space split
                            var firstSpace = l.IndexOf(' ');
                            if (firstSpace > 0 && firstSpace < l.Length - 1)
                            {
                                parts = new string[] {
                                    l.Substring(0, firstSpace),
                                    l.Substring(firstSpace + 1)
                                };
                            }
                        }
                        return parts;
                    })
                    .Where(p => p.Length == 2 && !string.IsNullOrWhiteSpace(p[0]) && !string.IsNullOrWhiteSpace(p[1]))
                    .ToDictionary(p => p[0].ToUpper(), p => p[1].Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

                Console.WriteLine($"?? Loaded {_cmu.Count} words from CMU Dictionary");

                // 2. load symbols.txt
                symbolsPath = symbolsPath ?? Path.Combine("models", "tts", "symbols.txt");
                if (!File.Exists(symbolsPath))
                {
                    Console.WriteLine($"? Symbols file not found at: {symbolsPath}");
                    Console.WriteLine("Please place symbols.txt in models/tts/ folder");
                    return false;
                }

                var symbols = File.ReadAllLines(symbolsPath);
                _sym2id = symbols.Select((s, i) => (s, i))
                    .ToDictionary(x => x.s, x => x.i);

                Console.WriteLine($"?? Loaded {_sym2id.Count} symbols from symbols.txt");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Failed to load G2P components: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Load TTS model
        /// </summary>
        private static bool LoadTtsModel(string modelPath)
        {
            try
            {
                if (!File.Exists(modelPath))
                {
                    Console.WriteLine($"? TTS model not found: {modelPath}");
                    return false;
                }

                var options = new SessionOptions();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                // Try GPU first if preferred, otherwise use CPU
                if (_useGpu)
                {
                    try
                    {
                        options.AppendExecutionProvider_CUDA(0);
                        _ttsModel = new InferenceSession(modelPath, options);
                        Console.WriteLine("?? TTS using GPU acceleration");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"?? GPU initialization failed, using CPU: {ex.Message}");
                        
                        // Fallback to CPU
                        var cpuOptions = new SessionOptions();
                        cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                        _ttsModel = new InferenceSession(modelPath, cpuOptions);
                        Console.WriteLine("?? TTS using CPU (fallback)");
                    }
                }
                else
                {
                    // Use CPU explicitly
                    var cpuOptions = new SessionOptions();
                    cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    _ttsModel = new InferenceSession(modelPath, cpuOptions);
                    Console.WriteLine("?? TTS using CPU");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Failed to load TTS model: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Initialize audio playback with better error handling
        /// </summary>
        private static void InitializeAudioPlayback()
        {
            try
            {
                // Don't initialize WaveOut here - create it fresh for each playback
                // This prevents the "Can't re-initialize during playback" error
                _waveOut = null;
                _isPlaying = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Audio playback warning: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert text to speech and play it
        /// </summary>
        public static async Task<bool> SpeakAsync(string text, string speakerName = null)
        {
            if (!IsEnabled() || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                OnTtsSpeakingStarted?.Invoke(text);

                // Tokenize text using your exact implementation
                var tokenIds = Tokenize(text);
                if (tokenIds.Length == 0)
                {
                    Console.WriteLine("? Failed to tokenize text");
                    return false;
                }

                // Generate audio
                var audioData = await Task.Run(() => GenerateAudio(tokenIds, speakerName));
                
                if (audioData != null && audioData.Length > 0)
                {
                    await PlayAudioAsync(audioData);
                    return true;
                }
                else
                {
                    Console.WriteLine($"? Failed to generate audio");
                    OnTtsError?.Invoke("Failed to generate audio");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Speech synthesis error: {ex.Message}");
                OnTtsError?.Invoke($"Speech synthesis error: {ex.Message}");
                return false;
            }
            finally
            {
                if (!_isPlaying) // Only invoke if not already invoked by playback completion
                {
                    OnTtsSpeakingFinished?.Invoke();
                }
            }
        }

        /// <summary>
        /// Generate TTS audio data without playing it (for Discord transmission)
        /// </summary>
        public static async Task<float[]> GenerateAudioDataAsync(string text, string speakerName = null)
        {
            if (!IsEnabled() || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                // Tokenize text using existing implementation
                var tokenIds = Tokenize(text);
                if (tokenIds.Length == 0)
                {
                    Console.WriteLine("? Failed to tokenize text for Discord TTS");
                    return null;
                }

                // Generate audio data using existing implementation
                var audioData = await Task.Run(() => GenerateAudio(tokenIds, speakerName));
                
                if (audioData != null && audioData.Length > 0)
                {
                    return audioData;
                }
                else
                {
                    Console.WriteLine($"? Failed to generate audio data for Discord");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Discord TTS generation error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Tokenize text using proper punctuation handling based on symbols.txt vocabulary
        /// </summary>
        public static long[] Tokenize(string text)
        {
            if (_cmu == null || _sym2id == null)
            {
                Console.WriteLine("? G2P components not loaded");
                return new long[0];
            }

            try
            {
                // CRITICAL FIX: Validate vocabulary first
                var maxValidId = _sym2id.Count - 1;
                if (maxValidId > 511)
                {
                    Console.WriteLine($"?? WARNING: Vocabulary size {_sym2id.Count} exceeds model limit. Model expects IDs in range [-512,511]");
                }

                // a) Parse text into words and punctuation tokens, preserving both
                var tokens = new List<string>();
                var currentWord = new System.Text.StringBuilder();
                
                foreach (char c in text.ToLower())
                {
                    if (char.IsLetter(c) || char.IsDigit(c)
                        // Allow accented characters and symbols as part of words
                        || c == '\u00C3' || c == '\u00E9' || c == '\u00E8' || c == '\u00E0' || c == '\u00F1'
                        || c == '-' || c == '\'')
                    {
                        currentWord.Append(c);
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        if (currentWord.Length > 0)
                        {
                            tokens.Add(currentWord.ToString());
                            currentWord.Clear();
                        }
                        // Space becomes word boundary - we'll handle this with <sp> if needed
                    }
                    else
                    {
                        // Handle punctuation
                        if (currentWord.Length > 0)
                        {
                            tokens.Add(currentWord.ToString());
                            currentWord.Clear();
                        }
                        
                        // Map punctuation character to symbol - FIXED: Use exact symbols from vocabulary
                        string punctSymbol = null;
                        switch (c)
                        {
                            case '.': punctSymbol = "."; break;
                            case ',': punctSymbol = "','"; break;
                            case '?': punctSymbol = "'?'"; break;
                            case '!': punctSymbol = "'!'"; break;
                            case '\'': punctSymbol = "''''"; break;
                            // Add more punctuation mappings as needed
                        }
                        
                        if (punctSymbol != null && _sym2id.ContainsKey(punctSymbol))
                        {
                            tokens.Add(punctSymbol);
                        }
                        // If punctuation not in vocabulary, skip it silently
                    }
                }
                
                // Add final word if exists
                if (currentWord.Length > 0)
                {
                    tokens.Add(currentWord.ToString());
                }

                if (tokens.Count == 0)
                {
                    if (_sym2id.TryGetValue("<blank>", out var blankId))
                        return new long[] { blankId };
                    return new long[] { 0 }; // Fallback to first token
                }

                // b) Convert tokens to phonemes or symbols
                var allSymbols = new List<string>();
                var unknownWords = new List<string>();
                
                foreach (var token in tokens)
                {
                    // Check if token is already a punctuation symbol
                    if (_sym2id.ContainsKey(token))
                    {
                        allSymbols.Add(token);
                    }
                    else
                    {
                        // It's a word - look up phonemes in CMU dict
                        if (_cmu.TryGetValue(token.ToUpper(), out var phonemes))
                        {
                            // CRITICAL: Filter phonemes to only include those in vocabulary
                            var validPhonemes = phonemes.Where(p => _sym2id.ContainsKey(p)).ToArray();
                            if (validPhonemes.Length > 0)
                            {
                                allSymbols.AddRange(validPhonemes);
                            }
                            else
                            {
                                var basicPhonemes = GetBasicPhonemes(token);
                                var validBasicPhonemes = basicPhonemes.Where(p => _sym2id.ContainsKey(p)).ToArray();
                                if (validBasicPhonemes.Length > 0)
                                {
                                    allSymbols.AddRange(validBasicPhonemes);
                                }
                                else
                                {
                                    // Ultimate fallback to <unk>
                                    allSymbols.Add("<unk>");
                                }
                                unknownWords.Add(token);
                            }
                        }
                        else
                        {
                            // Word not in dictionary - use basic phoneme approximation
                            var basicPhonemes = GetBasicPhonemes(token);
                            var validBasicPhonemes = basicPhonemes.Where(p => _sym2id.ContainsKey(p)).ToArray();
                            if (validBasicPhonemes.Length > 0)
                            {
                                allSymbols.AddRange(validBasicPhonemes);
                            }
                            else
                            {
                                // Ultimate fallback to <unk>
                                allSymbols.Add("<unk>");
                            }
                            unknownWords.Add(token);
                        }
                    }
                }

                // c) map symbols to IDs with STRICT validation
                var result = new List<long>();
                var unmappedSymbols = new List<string>();
                
                foreach (var symbol in allSymbols)
                {
                    if (_sym2id.TryGetValue(symbol, out var id))
                    {
                        // CRITICAL CHECK: Ensure ID is within BOTH vocabulary range AND model range
                        if (id >= 0 && id < _sym2id.Count && id <= 511)
                        {
                            result.Add((long)id);
                        }
                        else
                        {
                            Console.WriteLine($"? ERROR: Symbol '{symbol}' mapped to invalid ID {id} (vocab range: 0-{_sym2id.Count - 1}, model range: 0-511)");
                            // Use <unk> token as fallback
                            if (_sym2id.TryGetValue("<unk>", out var unkId) && unkId <= 511)
                                result.Add((long)unkId);
                            else
                                result.Add(1L); // Fallback to ID 1
                        }
                    }
                    else
                    {
                        // This should not happen after filtering, but handle it anyway
                        unmappedSymbols.Add(symbol);
                        if (_sym2id.TryGetValue("<unk>", out var unkId) && unkId <= 511)
                        {
                            result.Add((long)unkId);
                        }
                        else
                        {
                            result.Add(1L);
                        }
                    }
                }

                // Final validation - ensure ALL IDs are within model range
                var modelMaxId = Math.Min(511, _sym2id.Count - 1); // Use the smaller of model limit or vocab size
                var invalidIds = result.Where(id => id < 0 || id > modelMaxId).ToList();
                if (invalidIds.Any())
                {
                    Console.WriteLine($"? CRITICAL ERROR: Found {invalidIds.Count} invalid token IDs: [{string.Join(", ", invalidIds)}]");
                    Console.WriteLine($"? Valid range is 0 to {modelMaxId}");
                    
                    // Replace invalid IDs with <unk> token
                    var unkId = _sym2id.ContainsKey("<unk>") ? _sym2id["<unk>"] : 1;
                    for (int i = 0; i < result.Count; i++)
                    {
                        if (result[i] < 0 || result[i] > modelMaxId)
                        {
                            result[i] = unkId;
                        }
                    }
                }

                // FINAL SAFETY CHECK: Verify no tokens exceed the sequence limit
                if (result.Count > 512)
                {
                    Console.WriteLine($"?? WARNING: Token sequence too long ({result.Count} tokens), truncating to 512");
                    result = result.Take(512).ToList();
                }
                
                return result.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Tokenization failed: {ex.Message}");
                
                // Return a safe fallback sequence
                if (_sym2id.ContainsKey("<unk>"))
                {
                    return new long[] { _sym2id["<unk>"] };
                }
                return new long[] { 1 }; // Ultimate fallback
            }
        }

        /// <summary>
        /// Get basic phonemes for unknown words as a fallback - FIXED to only return valid vocabulary phonemes
        /// </summary>
        private static string[] GetBasicPhonemes(string word)
        {
            if (_sym2id == null)
            {
                return new string[] { "<unk>" };
            }

            // Very basic letter-to-phoneme mapping for fallback - only use phonemes that exist in vocabulary
            var phonemes = new List<string>();
            foreach (char c in word.ToLower())
            {
                string phoneme = null;
                switch (c)
                {
                    case 'a': phoneme = _sym2id.ContainsKey("AE1") ? "AE1" : _sym2id.ContainsKey("AH0") ? "AH0" : null; break;
                    case 'b': phoneme = _sym2id.ContainsKey("B") ? "B" : null; break;
                    case 'c': phoneme = _sym2id.ContainsKey("K") ? "K" : null; break;
                    case 'd': phoneme = _sym2id.ContainsKey("D") ? "D" : null; break;
                    case 'e': phoneme = _sym2id.ContainsKey("EH1") ? "EH1" : _sym2id.ContainsKey("AH0") ? "AH0" : null; break;
                    case 'f': phoneme = _sym2id.ContainsKey("F") ? "F" : null; break;
                    case 'g': phoneme = _sym2id.ContainsKey("G") ? "G" : null; break;
                    case 'h': phoneme = _sym2id.ContainsKey("HH") ? "HH" : null; break;
                    case 'i': phoneme = _sym2id.ContainsKey("IH1") ? "IH1" : _sym2id.ContainsKey("IY1") ? "IY1" : null; break;
                    case 'j': phoneme = _sym2id.ContainsKey("JH") ? "JH" : _sym2id.ContainsKey("Y") ? "Y" : null; break;
                    case 'k': phoneme = _sym2id.ContainsKey("K") ? "K" : null; break;
                    case 'l': phoneme = _sym2id.ContainsKey("L") ? "L" : null; break;
                    case 'm': phoneme = _sym2id.ContainsKey("M") ? "M" : null; break;
                    case 'n': phoneme = _sym2id.ContainsKey("N") ? "N" : null; break;
                    case 'o': phoneme = _sym2id.ContainsKey("OW1") ? "OW1" : _sym2id.ContainsKey("AH0") ? "AH0" : null; break;
                    case 'p': phoneme = _sym2id.ContainsKey("P") ? "P" : null; break;
                    case 'q': phoneme = _sym2id.ContainsKey("K") ? "K" : null; break;
                    case 'r': phoneme = _sym2id.ContainsKey("R") ? "R" : null; break;
                    case 's': phoneme = _sym2id.ContainsKey("S") ? "S" : null; break;
                    case 't': phoneme = _sym2id.ContainsKey("T") ? "T" : null; break;
                    case 'u': phoneme = _sym2id.ContainsKey("UW1") ? "UW1" : _sym2id.ContainsKey("AH0") ? "AH0" : null; break;
                    case 'v': phoneme = _sym2id.ContainsKey("V") ? "V" : null; break;
                    case 'w': phoneme = _sym2id.ContainsKey("W") ? "W" : null; break;
                    case 'x': 
                        // For 'x', try to add both K and S if they exist
                        if (_sym2id.ContainsKey("K")) phonemes.Add("K");
                        if (_sym2id.ContainsKey("S")) phonemes.Add("S");
                        continue;
                    case 'y': phoneme = _sym2id.ContainsKey("Y") ? "Y" : _sym2id.ContainsKey("IY1") ? "IY1" : null; break;
                    case 'z': phoneme = _sym2id.ContainsKey("Z") ? "Z" : _sym2id.ContainsKey("S") ? "S" : null; break;
                    default: phoneme = _sym2id.ContainsKey("AH0") ? "AH0" : null; break; // Default vowel
                }
                
                if (phoneme != null)
                {
                    phonemes.Add(phoneme);
                }
            }
            
            // If no valid phonemes found, return <unk>
            if (phonemes.Count == 0)
            {
                phonemes.Add(_sym2id.ContainsKey("<unk>") ? "<unk>" : "AH0");
            }
            
            return phonemes.ToArray();
        }

        /// <summary>
        /// Generate audio from token IDs using ONNX model
        /// </summary>
        private static float[] GenerateAudio(long[] tokenIds, string speakerName = null)
        {
            if (_ttsModel == null || !_isInitialized)
            {
                Console.WriteLine("? TTS model not initialized");
                return null;
            }

            try
            {
                // CRITICAL: Validate token IDs AND sequence length before sending to model
                var minToken = tokenIds.Min();
                var maxToken = tokenIds.Max();
                
                if (maxToken > 511 || minToken < -512)
                {
                    Console.WriteLine($"? CRITICAL: Token IDs outside model range [-512, 511]!");
                    return null;
                }

                // NEW: Check sequence length limit
                if (tokenIds.Length > 512)
                {
                    Console.WriteLine($"? CRITICAL: Sequence too long ({tokenIds.Length} tokens). Truncating to 512.");
                    tokenIds = tokenIds.Take(512).ToArray();
                }

                var inputs = new List<NamedOnnxValue>();

                // Text input - use the actual input name from the model
                if (_ttsModel.InputMetadata.ContainsKey("text"))
                {
                    var textTensor = new DenseTensor<long>(tokenIds, new int[] { tokenIds.Length });
                    inputs.Add(NamedOnnxValue.CreateFromTensor("text", textTensor));
                }
                else
                {
                    Console.WriteLine("? Model doesn't have 'text' input!");
                    return null;
                }

                // Speaker input - check for various possible speaker input names
                if (!string.IsNullOrEmpty(speakerName) && int.TryParse(speakerName, out int speakerId))
                {
                    // CRITICAL FIX: Convert REF ID to actual Speaker ID that model expects
                    int modelSpeakerId = speakerId;
                    
                    // Check if this looks like a REF ID (225+) and convert to Speaker ID
                    if (speakerId >= 225)
                    {
                        // Find the corresponding speaker info to get the actual Speaker ID
                        var speakerInfo = TtsSpeakerData.FindByRefId(speakerId);
                        if (speakerInfo != null)
                        {
                            // Use the SpeakerId (1-107) instead of RefId (225-374)
                            modelSpeakerId = speakerInfo.SpeakerId;
                        }
                        else
                        {
                            // Fallback: map REF ID range to Speaker ID range
                            modelSpeakerId = ((speakerId - 225) % 107) + 1;
                        }
                    }
                    
                    // For CPU mode, ensure speaker ID is within valid range [-128, 127]
                    if (!_useGpu && (modelSpeakerId < -128 || modelSpeakerId > 127))
                    {
                        modelSpeakerId = Math.Max(-128, Math.Min(127, modelSpeakerId));
                    }
                    
                    if (_ttsModel.InputMetadata.ContainsKey("sids"))
                    {
                        var speakerTensor = new DenseTensor<long>(new long[] { modelSpeakerId }, new int[] { 1 });
                        inputs.Add(NamedOnnxValue.CreateFromTensor("sids", speakerTensor));
                    }
                    else if (_ttsModel.InputMetadata.ContainsKey("speaker_id"))
                    {
                        var speakerTensor = new DenseTensor<long>(new long[] { modelSpeakerId }, new int[] { 1 });
                        inputs.Add(NamedOnnxValue.CreateFromTensor("speaker_id", speakerTensor));
                    }
                    else if (_ttsModel.InputMetadata.ContainsKey("spks"))
                    {
                        var speakerTensor = new DenseTensor<long>(new long[] { modelSpeakerId }, new int[] { 1 });
                        inputs.Add(NamedOnnxValue.CreateFromTensor("spks", speakerTensor));
                    }
                }
                else
                {
                    // For models that require speaker input, provide default speaker ID
                    if (_ttsModel.InputMetadata.ContainsKey("sids"))
                    {
                        var defaultSpeakerId = 1; // Default to speaker 1 (safe for both CPU and GPU)
                        var speakerTensor = new DenseTensor<long>(new long[] { defaultSpeakerId }, new int[] { 1 });
                        inputs.Add(NamedOnnxValue.CreateFromTensor("sids", speakerTensor));
                    }
                }

                // Run inference
                using (var outputs = _ttsModel.Run(inputs))
                {
                    // Get the audio output (usually the first output)
                    var audioOutput = outputs.FirstOrDefault();
                    if (audioOutput?.Value is Tensor<float> audioTensor)
                    {
                        // Extract audio data based on tensor rank
                        float[] audioData;
                        
                        if (audioTensor.Rank == 1)
                        {
                            // 1D tensor: [samples]
                            audioData = audioTensor.ToArray(); 
                        }
                        else if (audioTensor.Rank == 2)
                        {
                            // 2D tensor: [batch, samples] - take first batch
                            var batchSize = audioTensor.Dimensions[0];
                            var sampleCount = audioTensor.Dimensions[1];
                            audioData = new float[sampleCount];
                            for (int i = 0; i < sampleCount; i++)
                            {
                                audioData[i] = audioTensor[0, i];
                            }
                        }
                        else
                        {
                            Console.WriteLine("? Unexpected audio tensor rank");
                            return null;
                        }

                        return audioData;
                    }
                    else
                    {
                        Console.WriteLine("? No valid audio tensor in model output");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Audio generation failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Play audio data using NAudio with proper queue management and configured output device
        /// </summary>
        private static async Task PlayAudioAsync(float[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                Console.WriteLine("? Cannot play audio: invalid data");
                return;
            }

            try
            {
                Console.WriteLine($"?? Starting TTS audio playback - {audioData.Length} samples");
                
                // ENHANCED: Analyze audio data for volume and content
                var maxSample = audioData.Max(Math.Abs);
                var avgSample = audioData.Select(Math.Abs).Average();
                var nonZeroSamples = audioData.Count(s => Math.Abs(s) > 0.001f);
                
                Console.WriteLine($"?? Audio analysis:");
                Console.WriteLine($"   Max amplitude: {maxSample:F4} (0.0 = silent, 1.0 = max)");
                Console.WriteLine($"   Average amplitude: {avgSample:F4}");
                Console.WriteLine($"   Non-zero samples: {nonZeroSamples}/{audioData.Length} ({(float)nonZeroSamples/audioData.Length*100:F1}%)");
                
                if (maxSample < 0.001f)
                {
                    Console.WriteLine("?? WARNING: Audio data appears to be silent (max amplitude < 0.001)!");
                }
                else if (maxSample < 0.01f)
                {
                    Console.WriteLine("?? WARNING: Audio data is very quiet (max amplitude < 0.01)!");
                }
                
                // Convert float samples to 16-bit PCM with volume boost
                var sampleRate = 22050; // Common TTS sample rate
                var pcmData = new short[audioData.Length];
                
                // ENHANCED: Apply significant volume boost for TTS audio (3x boost)
                var volumeBoost = 3.0f; // Boost audio by 3x to make it more audible
                
                for (int i = 0; i < audioData.Length; i++)
                {
                    var boostedSample = audioData[i] * volumeBoost;
                    // Prevent clipping while maintaining loudness
                    boostedSample = Math.Max(-0.95f, Math.Min(0.95f, boostedSample));
                    pcmData[i] = (short)(boostedSample * 32767);
                }

                Console.WriteLine($"?? Converted to PCM: {pcmData.Length} samples at {sampleRate}Hz with {volumeBoost}x volume boost");
                
                // Analyze PCM data volume
                var maxPcm = pcmData.Max(Math.Abs);
                var avgPcm = pcmData.Select(s => (double)Math.Abs(s)).Average();
                Console.WriteLine($"?? PCM analysis: Max={maxPcm} (0-32767), Avg={avgPcm:F0}");

                // Create wave format and provider
                var waveFormat = new WaveFormat(sampleRate, 16, 1);
                var memoryStream = new MemoryStream();
                var writer = new BinaryWriter(memoryStream);
                
                foreach (var sample in pcmData)
                {
                    writer.Write(sample);
                }
                
                memoryStream.Position = 0;
                var rawSourceWaveStream = new RawSourceWaveStream(memoryStream, waveFormat);

                Console.WriteLine($"?? Created audio stream: {waveFormat}");

                // Ensure proper cleanup and re-initialization
                bool needsWait = false;
                lock (_lockObject)
                {
                    try
                    {
                        // Stop any current playback
                        if (_waveOut != null)
                        {
                            if (_waveOut.PlaybackState == PlaybackState.Playing)
                            {
                                Console.WriteLine($"?? Stopping previous TTS playback");
                                _waveOut.Stop();
                                needsWait = true;
                            }
                            
                            // Dispose the current instance
                            _waveOut.Dispose();
                            _waveOut = null;
                        }
                    }
                    catch (Exception stopEx)
                    {
                        Console.WriteLine($"?? Warning during audio stop: {stopEx.Message}");
                    }
                }

                // Wait for stop to complete outside the lock
                if (needsWait)
                {
                    await Task.Delay(100);
                }

                // Initialize new audio instance with configured output device
                lock (_lockObject)
                {
                    try
                    {
                        // Get the configured output device
                        var outputDevice = AudioDeviceManager.GetConfiguredOutputDevice();
                        
                        Console.WriteLine($"?? Initializing WaveOutEvent for device: {outputDevice?.DeviceName ?? "Default"}");
                        
                        // Create a new WaveOutEvent instance with the selected device
                        _waveOut = new WaveOutEvent();
                        
                        if (outputDevice != null && outputDevice.DeviceNumber >= 0)
                        {
                            _waveOut.DeviceNumber = outputDevice.DeviceNumber;
                            Console.WriteLine($"?? Set audio output to device #{outputDevice.DeviceNumber}: {outputDevice.DeviceName}");
                        }
                        else
                        {
                            Console.WriteLine($"?? Using default audio output device");
                        }
                        
                        // ENHANCED: Set volume to maximum for TTS
                        _waveOut.Volume = 1.0f; // Set to maximum volume
                        // Apply user-configured local volume
                        var localVolume = AppSettings.LoadLocalTtsVolume();
                        _waveOut.Volume = (float)localVolume;
                        Console.WriteLine($"?? Set WaveOut volume to: {_waveOut.Volume * 100:F0}% (user setting)");
                        
                        _waveOut.PlaybackStopped += (s, e) =>
                        {
                            Console.WriteLine($"?? TTS playback stopped. Error: {e.Exception?.Message ?? "None"}");
                            _isPlaying = false;
                            OnTtsSpeakingFinished?.Invoke();
                        };

                        // Initialize with new audio stream
                        Console.WriteLine($"?? Initializing WaveOut with audio stream...");
                        _waveOut.Init(rawSourceWaveStream);
                        _isPlaying = true;
                        
                        Console.WriteLine($"?? Starting TTS playback...");
                        _waveOut.Play();
                        
                        Console.WriteLine($"?? TTS playback started successfully! State: {_waveOut.PlaybackState}, Volume: {_waveOut.Volume}");
                        
                        // ENHANCED: Check system volume levels
                        try
                        {
                            // Log additional playback info
                            Console.WriteLine($"?? Playback details:");
                            Console.WriteLine($"   Device Number: {_waveOut.DeviceNumber}");
                            Console.WriteLine($"   Volume Level: {_waveOut.Volume * 100:F0}%");
                            Console.WriteLine($"   Audio Format: {waveFormat.SampleRate}Hz, {waveFormat.BitsPerSample}-bit, {waveFormat.Channels} channel(s)");
                            Console.WriteLine($"   Buffer Duration: ~{(float)audioData.Length / sampleRate:F1} seconds");
                        }
                        catch (Exception infoEx)
                        {
                            Console.WriteLine($"?? Could not get extended playback info: {infoEx.Message}");
                        }
                    }
                    catch (Exception initEx)
                    {
                        Console.WriteLine($"? Audio initialization failed: {initEx.Message}");
                        Console.WriteLine($"? Stack trace: {initEx.StackTrace}");
                        _isPlaying = false;
                        OnTtsError?.Invoke($"Audio initialization failed: {initEx.Message}");
                        return;
                    }
                }

                // Wait for playback to complete (outside the lock)
                var playbackTimeout = DateTime.Now.AddSeconds(30); // 30 second timeout for safety
                Console.WriteLine($"? Waiting for TTS playback to complete...");
                
                var lastState = _waveOut?.PlaybackState ?? PlaybackState.Stopped;
                var stateCheckCount = 0;
                
                while (_isPlaying && _waveOut?.PlaybackState == PlaybackState.Playing && DateTime.Now < playbackTimeout)
                {
                    await Task.Delay(100);
                    stateCheckCount++;
                    
                    // Log progress every 1 second
                    if (stateCheckCount % 10 == 0)
                    {
                        var currentState = _waveOut?.PlaybackState ?? PlaybackState.Stopped;
                        Console.WriteLine($"?? Playback progress: {stateCheckCount/10}s, State: {currentState}");
                    }
                }

                if (DateTime.Now >= playbackTimeout)
                {
                    Console.WriteLine("? Audio playback timeout - forcing stop");
                    lock (_lockObject)
                    {
                        try
                        {
                            _waveOut?.Stop();
                            _isPlaying = false;
                        }
                        catch (Exception timeoutEx)
                        {
                            Console.WriteLine($"?? Error stopping timed out playback: {timeoutEx.Message}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"? TTS playback completed successfully");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Audio playback failed: {ex.Message}");
                Console.WriteLine($"? Stack trace: {ex.StackTrace}");
                _isPlaying = false;
                OnTtsError?.Invoke($"Audio playback failed: {ex.Message}");
                
                // Cleanup on error
                lock (_lockObject)
                {
                    try
                    {
                        _waveOut?.Stop();
                        _waveOut?.Dispose();
                        _waveOut = null;
                    }
                    catch (Exception cleanupEx)
                    {
                        Console.WriteLine($"?? Error during audio cleanup: {cleanupEx.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Get current local TTS volume (0.0 to 1.0)
        /// </summary>
        public static double GetLocalVolume()
        {
            return AppSettings.LoadLocalTtsVolume();
        }

        /// <summary>
        /// Set local TTS volume (0.0 to 1.0)
        /// </summary>
        public static void SetLocalVolume(double volume)
        {
            // Clamp and persist
            var clamped = Math.Max(0.0, Math.Min(1.0, volume));
            AppSettings.SaveLocalTtsVolume(clamped);

            // Apply in real-time to current playback
            try
            {
                lock (_lockObject)
                {
                    if (_waveOut != null)
                    {
                        _waveOut.Volume = (float)clamped;
                        Console.WriteLine($"?? Local TTS volume updated in real-time: {_waveOut.Volume * 100:F0}%");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? Warning: Failed to update local TTS volume live: {ex.Message}");
            }
        }

        /// <summary>
        /// Get current Discord TTS volume (0.0 to 1.0)
        /// </summary>
        public static double GetDiscordVolume()
        {
            return AppSettings.LoadDiscordTtsVolume();
        }

        /// <summary>
        /// Set Discord TTS volume (0.0 to 1.0)
        /// </summary>
        public static void SetDiscordVolume(double volume)
        {
            AppSettings.SaveDiscordTtsVolume(volume);
        }

        /// <summary>
        /// Check if TTS is enabled
        /// </summary>
        public static bool IsEnabled()
        {
            return _isInitialized && _isEnabled;
        }

        /// <summary>
        /// Enable or disable TTS
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
            AppSettings.SaveTtsEnabled(enabled);
            Console.WriteLine($"?? TTS {(enabled ? "enabled" : "disabled")}");

            // Immediate re-initialization if disabling
            if (!enabled)
            {
                lock (_lockObject)
                {
                    Cleanup();
                }
            }
        }

        /// <summary>
        /// Set current speaker
        /// </summary>
        public static void SetSpeaker(string speaker)
        {
            _currentSpeaker = speaker ?? "1";
        }

        /// <summary>
        /// Set current model path
        /// </summary>
        public static void SetModelPath(string modelPath)
        {
            _currentModelPath = modelPath;
        }

        /// <summary>
        /// Get available TTS models
        /// </summary>
        public static string[] GetAvailableModels()
        {
            try
            {
                var modelsDir = Path.Combine("models", "tts");
                if (!Directory.Exists(modelsDir))
                {
                    Console.WriteLine($"Models directory not found: {modelsDir}");
                    return new string[0];
                }

                var models = Directory.GetFiles(modelsDir, "*.onnx")
                    .Select(Path.GetFileName)
                    .ToArray();

                return models;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scanning for TTS models: {ex.Message}");
                return new string[0];
            }
        }

        /// <summary>
        /// Switch to a different TTS model
        /// </summary>
        public static bool SwitchModel(string modelName)
        {
            try
            {
                var modelPath = Path.Combine("models", "tts", modelName);
                if (!File.Exists(modelPath))
                {
                    Console.WriteLine($"Model not found: {modelPath}");
                    return false;
                }

                Console.WriteLine($"?? Switching to TTS model: {modelName}");

                // Cleanup current model
                lock (_lockObject)
                {
                    _ttsModel?.Dispose();
                    _ttsModel = null;
                    _isInitialized = false;
                }

                // Initialize with new model
                var success = Initialize(modelPath);
                if (success)
                {
                    _currentModelPath = modelPath;
                    AppSettings.SaveTtsModelPath(modelPath);
                    Console.WriteLine($"? Successfully switched to model: {modelName}");
                }
                else
                {
                    Console.WriteLine($"? Failed to switch to model: {modelName}");
                }

                return success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error switching TTS model: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get current model name
        /// </summary>
        public static string GetCurrentModel()
        {
            return !string.IsNullOrEmpty(_currentModelPath) ? Path.GetFileName(_currentModelPath) : "None";
        }

        /// <summary>
        /// Get a random speaker from VCTK dataset
        /// </summary>
        public static int GetRandomSpeaker(string accent = "any")
        {
            var random = new Random();
            
            // VCTK speakers with their accents (simplified)
            var speakers = new Dictionary<string, int[]>
            {
                ["english"] = new[] { 225, 226, 227, 228, 229, 230, 231, 232, 233, 236, 237, 239, 240, 243, 244, 256, 257, 258, 259, 267, 268, 269, 270, 273, 274, 277, 278, 279, 286, 287 },
                ["scottish"] = new[] { 234, 237, 246, 247, 249, 252, 255, 260, 262, 263, 265, 271, 272, 275, 281, 284, 285 },
                ["american"] = new[] { 294, 297, 299, 300, 301, 305, 306, 308, 310, 311, 318, 329, 330, 333, 334, 339, 341, 345, 360, 361, 362 },
                ["irish"] = new[] { 245, 266, 283, 288, 295, 298, 312, 340, 364 },
                ["northern_irish"] = new[] { 238, 261, 292, 293, 304, 351 },
                ["welsh"] = new[] { 253 },
                ["canadian"] = new[] { 302, 303, 307, 312, 316, 317, 343, 363 },
                ["south_african"] = new[] { 314, 323, 336, 347 },
                ["australian"] = new[] { 326, 374 },
                ["indian"] = new[] { 248, 251 }
            };

            if (accent.ToLowerInvariant() == "any" || !speakers.ContainsKey(accent.ToLowerInvariant()))
            {
                // Return any speaker from 1-107
                return random.Next(1, 108);
            }

            var accentSpeakers = speakers[accent.ToLowerInvariant()];
            return accentSpeakers[random.Next(accentSpeakers.Length)];
        }

        /// <summary>
        /// Get current execution mode (GPU or CPU)
        /// </summary>
        public static string GetExecutionMode()
        {
            if (!_isInitialized)
                return "Not initialized";
                
            return _useGpu ? "GPU" : "CPU";
        }

        /// <summary>
        /// Check if GPU mode is preferred
        /// </summary>
        public static bool IsUsingGpu()
        {
            return _useGpu;
        }

        /// <summary>
        /// Switch between CPU and GPU execution modes
        /// </summary>
        public static bool SwitchExecutionMode(bool useGpu)
        {
            try
            {
                if (_useGpu == useGpu && _isInitialized)
                {
                    return true;
                }

                _useGpu = useGpu;
                Console.WriteLine($"?? Switching TTS to {(useGpu ? "GPU" : "CPU")} mode...");

                // Save preference to settings
                AppSettings.SaveTtsUseGpu(useGpu);

                // If initialized, reload the model with new execution provider
                if (_isInitialized && !string.IsNullOrEmpty(_currentModelPath))
                {
                    // Cleanup current model
                    lock (_lockObject)
                    {
                        _ttsModel?.Dispose();
                        _ttsModel = null;
                        _isInitialized = false;
                    }

                    // Re-initialize with new execution mode
                    var success = Initialize(_currentModelPath);
                    if (success)
                    {
                        Console.WriteLine($"? Successfully switched to {(useGpu ? "GPU" : "CPU")} mode");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"? Failed to switch to {(useGpu ? "GPU" : "CPU")} mode");
                        return false;
                    }
                }
                else
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error switching execution mode: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        private static void Cleanup()
        {
            try
            {
                lock (_lockObject)
                {
                    _waveOut?.Stop();
                    _waveOut?.Dispose();
                    _waveOut = null;

                    _ttsModel?.Dispose();
                    _ttsModel = null;

                    _isInitialized = false;
                    _isPlaying = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during TTS cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Shutdown TTS service
        /// </summary>
        public static void Shutdown()
        {
            Console.WriteLine("?? Shutting down TTS service...");
            Cleanup();
        }

        /// <summary>
        /// Stop current audio playback if playing
        /// </summary>
        public static void StopCurrentPlayback()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
                    {
                        _waveOut.Stop();
                        _isPlaying = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping TTS playback: {ex.Message}");
                _isPlaying = false;
            }
        }

        /// <summary>
        /// Check if TTS is currently playing audio
        /// </summary>
        public static bool IsCurrentlyPlaying()
        {
            try
            {
                return _isPlaying && _waveOut?.PlaybackState == PlaybackState.Playing;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Test tokenization with a simple phrase to verify token ranges
        /// </summary>
        public static void TestTokenization()
        {
            try
            {
                Console.WriteLine("?? Testing TTS tokenization...");
                
                if (_cmu == null || _sym2id == null)
                {
                    Console.WriteLine("? G2P components not loaded - cannot test");
                    return;
                }
                
                var testPhrases = new[] {
                    "Hello world",
                    "How are you today?",
                    "This is a test.",
                    "Good morning!"
                };
                
                foreach (var phrase in testPhrases)
                {
                    var tokens = Tokenize(phrase);
                    
                    if (tokens.Length > 0)
                    {
                        var minToken = tokens.Min();
                        var maxToken = tokens.Max();
                        
                        if (maxToken > 511)
                        {
                            Console.WriteLine($"? ERROR: Token {maxToken} exceeds model limit of 511!");
                        }
                        else
                        {
                            Console.WriteLine($"? '{phrase}': {tokens.Length} tokens, range: {minToken} to {maxToken}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"?? No tokens generated for phrase: '{phrase}'");
                    }
                }
                
                Console.WriteLine("? Tokenization test completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Tokenization test failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Comprehensive TTS diagnostic method to identify why local TTS isn't working
        /// </summary>
        public static void DiagnoseTtsIssues()
        {
            try
            {
                Console.WriteLine("?? === TTS DIAGNOSTIC REPORT ===");
                
                // 1. Check TTS Service State
                Console.WriteLine($"1. TTS Service State:");
                Console.WriteLine($"   IsInitialized: {_isInitialized}");
                Console.WriteLine($"   IsEnabled: {_isEnabled}");
                Console.WriteLine($"   Current Model: {GetCurrentModel()}");
                Console.WriteLine($"   Current Speaker: {_currentSpeaker}");
                Console.WriteLine($"   Execution Mode: {GetExecutionMode()}");
                
                // 2. Check Model and Components
                Console.WriteLine($"\n2. Model and Components:");
                Console.WriteLine($"   TTS Model Loaded: {_ttsModel != null}");
                Console.WriteLine($"   CMU Dictionary Loaded: {_cmu != null} ({_cmu?.Count ?? 0} words)");
                Console.WriteLine($"   Symbols Loaded: {_sym2id != null} ({_sym2id?.Count ?? 0} symbols)");
                Console.WriteLine($"   Current Model Path: {_currentModelPath}");
                
                // 3. Check Audio Configuration
                Console.WriteLine($"\n3. Audio Configuration:");
                var localVolume = AppSettings.LoadLocalTtsVolume();
                Console.WriteLine($"   Local TTS Volume: {localVolume * 100:F0}%");
                
                // Check configured output device
                var outputDevice = AudioDeviceManager.GetConfiguredOutputDevice();
                if (outputDevice != null)
                {
                    Console.WriteLine($"   Configured Output Device: {outputDevice.DeviceName} (#{outputDevice.DeviceNumber})");
                    
                    // Test the output device
                    var deviceTest = AudioDeviceManager.TestOutputDevice(outputDevice.DeviceNumber);
                    Console.WriteLine($"   Output Device Test: {(deviceTest ? "? Pass" : "? Fail")}");
                }
                else
                {
                    Console.WriteLine($"   Output Device: Default system device");
                }
                
                // 4. Check Current Playback State
                Console.WriteLine($"\n4. Current Playback State:");
                Console.WriteLine($"   Is Playing: {_isPlaying}");
                Console.WriteLine($"   WaveOut Instance: {_waveOut != null}");
                if (_waveOut != null)
                {
                    try
                    {
                        Console.WriteLine($"   WaveOut State: {_waveOut.PlaybackState}");
                        Console.WriteLine($"   WaveOut Volume: {_waveOut.Volume * 100:F0}%");
                        Console.WriteLine($"   WaveOut Device: #{_waveOut.DeviceNumber}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   WaveOut Status: Error - {ex.Message}");
                    }
                }
                
                // 5. Test Basic Functionality
                Console.WriteLine($"\n5. Basic TTS Test:");
                if (!_isInitialized)
                {
                    Console.WriteLine($"   ? TTS not initialized - cannot test");
                }
                else if (!_isEnabled)
                {
                    Console.WriteLine($"   ? TTS not enabled - cannot test");
                }
                else
                {
                    try
                    {
                        // Test tokenization
                        var testTokens = Tokenize("Hello world");
                        Console.WriteLine($"   Tokenization Test: {(testTokens.Length > 0 ? "? Pass" : "? Fail")} ({testTokens.Length} tokens)");
                        
                        // Test audio generation (without playing)
                        var testAudio = GenerateAudio(testTokens, "225");
                        if (testAudio != null && testAudio.Length > 0)
                        {
                            var maxSample = testAudio.Max(Math.Abs);
                            var avgSample = testAudio.Select(Math.Abs).Average();
                            Console.WriteLine($"   Audio Generation: ? Pass ({testAudio.Length} samples, max: {maxSample:F4}, avg: {avgSample:F4})");
                            
                            if (maxSample < 0.001f)
                            {
                                Console.WriteLine($"   ?? WARNING: Generated audio is silent!");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"   Audio Generation: ? Fail - No audio data generated");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   Basic Test Error: {ex.Message}");
                    }
                }
                
                // 6. Check System Audio
                Console.WriteLine($"\n6. System Audio Check:");
                try
                {
                    var outputDevices = AudioDeviceManager.GetOutputDevices();
                    Console.WriteLine($"   Available Output Devices: {outputDevices.Count}");
                    foreach (var device in outputDevices.Take(3)) // Show first 3 devices
                    {
                        var testResult = AudioDeviceManager.TestOutputDevice(device.DeviceNumber);
                        var statusIcon = testResult ? "?" : "?";
                        var defaultIcon = device.IsDefault ? " ??" : "";
                        Console.WriteLine($"   [{device.DeviceNumber}] {device.DeviceName}{defaultIcon} {statusIcon}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   System Audio Error: {ex.Message}");
                }
                
                // 7. Recommendations
                Console.WriteLine($"\n7. Troubleshooting Recommendations:");
                
                if (!_isInitialized)
                {
                    Console.WriteLine($"   ?? Initialize TTS service first");
                }
                else if (!_isEnabled)
                {
                    Console.WriteLine($"   ?? Enable TTS service: CoquiTtsService.SetEnabled(true)");
                }
                else if (localVolume < 0.1)
                {
                    Console.WriteLine($"   ?? Increase local TTS volume (currently {localVolume * 100:F0}%)");
                }
                else if (outputDevice == null)
                {
                    Console.WriteLine($"   ?? Configure a specific output device for better reliability");
                }
                else
                {
                    Console.WriteLine($"   ? Basic configuration appears correct");
                    Console.WriteLine($"   ?? Try running CoquiTtsService.SpeakAsync(\"Hello world\", \"225\") for a direct test");
                }
                
                Console.WriteLine($"\n?? === END TTS DIAGNOSTIC ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Diagnostic failed: {ex.Message}");
            }
        }
    }
}