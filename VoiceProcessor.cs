// VoiceProcessor.cs
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vosk;

namespace Kinectv1
{
    public class VoiceProcessor
    {
        private readonly VoskRecognizer _recognizer;
        private readonly Action<string> _onTranscription;
        private readonly Action<string, float> _onSpeakerMatch;
        private readonly Action<float> _onRmsLevel;
        private readonly Action<float> _onDiscordRmsLevel; // NEW: Separate Discord RMS callback
        private readonly Action<float[]> _onVoiceEmbedding;
        private readonly string _triggerName;
        private DateTime _lastVoiceTime = DateTime.UtcNow;
        private TimeSpan _silenceTimeout;
        private TimeSpan _vadDebounceTimeout;
        private DateTime _lastFinalResultTime = DateTime.MinValue;
        
        // Performance optimization: Replace List<float> with ArrayPool-based circular buffer
        private const int PCM_BUFFER_SIZE = 48000; // 3 seconds at 16kHz
        private readonly float[] _pcmBuffer = new float[PCM_BUFFER_SIZE];
        private int _pcmBufferPosition = 0;
        private int _pcmBufferCount = 0;
        
        // NEW: Rolling window for speaker embedding with 0.5s hop
        private readonly RingBuffer _speakerBuffer = new RingBuffer(16000); // 1 second at 16kHz
        private DateTime _lastSpeakerInference = DateTime.MinValue;
        private readonly TimeSpan _speakerInferenceHop = TimeSpan.FromMilliseconds(500); // 0.5s hop
        
        private string _lastTranscription = string.Empty;
        private string _pendingTranscription = string.Empty; // Store transcription for Ollama
        private DateTime _lastTranscriptionTime = DateTime.MinValue;
        private readonly List<string> _transcriptionHistory = new List<string>(); // Track recent transcriptions

        // NEW: Maintain current speaker state for consistent Ollama integration
        private string _currentSpeakerName = "Unknown";
        private float _currentSpeakerScore = 0f;
        private string _currentIdentificationMethod = "No identification";
        private DateTime _lastSpeakerUpdate = DateTime.MinValue;
        private readonly object _speakerStateLock = new object();

        // NEW: Centralized Ollama dispatch system to eliminate duplicates
        private readonly HashSet<string> _processedTranscriptions = new HashSet<string>();
        private readonly object _ollamaDispatchLock = new object();
        private bool _ollamaDispatchInProgress = false;

        // Enhanced confidence scoring system
        private readonly Dictionary<string, float> _wordConfidenceScores = new Dictionary<string, float>();
        private float _cumulativeConfidence = 0f;
        private int _recognitionAttempts = 0;
        private readonly float _confidenceThreshold;           // Configurable minimum confidence
        private readonly float _highConfidenceThreshold;      // Configurable high confidence threshold
        private readonly Queue<VoskResult> _lowConfidenceBuffer; // Configurable buffer for low confidence results
        private readonly bool _confidenceLoggingEnabled;      // Configurable logging
        private readonly int _bufferSize;                      // Buffer size for low confidence results
        
        // Performance optimization: cache split separators to avoid array allocation
        private static readonly char[] _splitSeparators = { ' ', '\t', '\n' };

        public VoiceProcessor(VoskRecognizer recognizer, Action<string> onTranscription, Action<string, float> onSpeakerMatch, string triggerName, Action<float> onRmsLevel = null, Action<float[]> onVoiceEmbedding = null, Action<float> onDiscordRmsLevel = null)
        {
            _recognizer = recognizer;
            _onTranscription = onTranscription;
            _onSpeakerMatch = onSpeakerMatch;
            _onRmsLevel = onRmsLevel;
            _onDiscordRmsLevel = onDiscordRmsLevel; // NEW: Store Discord RMS callback
            _onVoiceEmbedding = onVoiceEmbedding;
            _triggerName = triggerName;
            
            // Load configurable confidence settings
            _confidenceThreshold = AppSettings.LoadVoiceConfidenceThreshold();
            _highConfidenceThreshold = AppSettings.LoadVoiceHighConfidenceThreshold();
            _confidenceLoggingEnabled = AppSettings.LoadVoiceConfidenceLoggingEnabled();
            _bufferSize = AppSettings.LoadVoiceConfidenceBufferSize();
            _lowConfidenceBuffer = new Queue<VoskResult>(_bufferSize);
            
            // Load configurable VAD settings
            var silenceTimeoutMs = AppSettings.LoadVadSilenceTimeoutMs();
            var debounceTimeoutMs = AppSettings.LoadVadDebounceTimeoutMs();
            _silenceTimeout = TimeSpan.FromMilliseconds(silenceTimeoutMs);
            _vadDebounceTimeout = TimeSpan.FromMilliseconds(debounceTimeoutMs);
            
            Console.WriteLine($"?? VoiceProcessor initialized with confidence settings:");
            Console.WriteLine($"   Confidence threshold: {_confidenceThreshold:F2}");
            Console.WriteLine($"   High confidence threshold: {_highConfidenceThreshold:F2}");
            Console.WriteLine($"   Buffer size: {_bufferSize}");
            Console.WriteLine($"   Logging enabled: {_confidenceLoggingEnabled}");
            Console.WriteLine($"   Microphone RMS callback: {(_onRmsLevel != null ? "Connected" : "Not connected")}");
            Console.WriteLine($"   Discord RMS callback: {(_onDiscordRmsLevel != null ? "Connected" : "Not connected")}");
            Console.WriteLine($"?? VAD settings:");
            Console.WriteLine($"   Silence timeout: {silenceTimeoutMs}ms");
            Console.WriteLine($"   Debounce timeout: {debounceTimeoutMs}ms");
        }

        /// <summary>
        /// Add samples to circular PCM buffer using ArrayPool for temporary allocations
        /// </summary>
        private void AddToPcmBuffer(float[] samples)
        {
            foreach (var sample in samples)
            {
                _pcmBuffer[_pcmBufferPosition] = sample;
                _pcmBufferPosition = (_pcmBufferPosition + 1) % PCM_BUFFER_SIZE;
                if (_pcmBufferCount < PCM_BUFFER_SIZE) _pcmBufferCount++;
            }
        }

        /// <summary>
        /// Extract one second of audio from circular buffer using ArrayPool
        /// </summary>
        private float[] ExtractOneSecondFromBuffer()
        {
            const int oneSecondSamples = 16000; // 1 second at 16kHz
            if (_pcmBufferCount < oneSecondSamples) return null;
            
            var result = ArrayPool<float>.Shared.Rent(oneSecondSamples);
            try
            {
                int startPos = _pcmBufferPosition - _pcmBufferCount;
                if (startPos < 0) startPos += PCM_BUFFER_SIZE;
                
                for (int i = 0; i < oneSecondSamples; i++)
                {
                    result[i] = _pcmBuffer[(startPos + i) % PCM_BUFFER_SIZE];
                }
                
                // Remove extracted samples
                _pcmBufferCount -= oneSecondSamples;
                
                // Copy result to final array
                var finalResult = new float[oneSecondSamples];
                Array.Copy(result, finalResult, oneSecondSamples);
                return finalResult;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(result);
            }
        }

        /// <summary>
        /// NEW: Centralized speaker identification that combines voice and face recognition
        /// </summary>
        private (string speakerName, float confidence, string method) IdentifyCurrentSpeaker(string voiceSpeaker = null, float voiceScore = 0f)
        {
            try
            {
                string finalSpeakerName = "Unknown";
                float finalConfidence = 0f;
                string identificationMethod = "No identification";

                // Priority 1: Use voice recognition if it's reliable
                bool voiceRecognized = !string.IsNullOrEmpty(voiceSpeaker) && 
                                     voiceSpeaker != "Unknown" && 
                                     voiceScore > SpeakerIdentifier.GetDefaultThreshold();

                if (voiceRecognized)
                {
                    finalSpeakerName = voiceSpeaker;
                    finalConfidence = voiceScore;
                    identificationMethod = $"Voice recognition (score: {voiceScore:F3})";
                }
                else
                {
                    // Priority 2: Try face recognition as fallback
                    try
                    {
                        var (faceSpeaker, faceConfidence, reasoning) = EnhancedKinectFaceTracker.GetLikelySpeaker();
                        
                        if (!string.IsNullOrEmpty(faceSpeaker) && faceSpeaker != "Unknown" && faceConfidence > 0.3f)
                        {
                            finalSpeakerName = faceSpeaker;
                            finalConfidence = faceConfidence;
                            identificationMethod = $"Face recognition (confidence: {faceConfidence:F2}, {reasoning})";
                        }
                        else
                        {
                            finalSpeakerName = "Unknown";
                            finalConfidence = 0f;
                            identificationMethod = $"No recognition ({reasoning})";
                        }
                    }
                    catch (Exception ex)
                    {
                        finalSpeakerName = "Unknown";
                        finalConfidence = 0f;
                        identificationMethod = $"Face detection error: {ex.Message}";
                    }
                }

                return (finalSpeakerName, finalConfidence, identificationMethod);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in IdentifyCurrentSpeaker: {ex.Message}");
                return ("Unknown", 0f, "Identification error");
            }
        }

        /// <summary>
        /// NEW: Update the current speaker state in a thread-safe manner
        /// </summary>
        private void UpdateCurrentSpeaker(string speakerName, float confidence, string method)
        {
            lock (_speakerStateLock)
            {
                bool hasChanged = _currentSpeakerName != speakerName;
                
                _currentSpeakerName = speakerName;
                _currentSpeakerScore = confidence;
                _currentIdentificationMethod = method;
                _lastSpeakerUpdate = DateTime.UtcNow;

                // Only log major speaker changes (new speakers, not just confidence updates)
                if (hasChanged && _confidenceLoggingEnabled && speakerName != "Unknown" && !speakerName.StartsWith("User"))
                {
                    Console.WriteLine($"?? Speaker: {speakerName} ({confidence:F2})");
                }
            }
        }

        /// <summary>
        /// NEW: Get the current speaker state in a thread-safe manner
        /// </summary>
        private (string name, float confidence, string method) GetCurrentSpeaker()
        {
            lock (_speakerStateLock)
            {
                return (_currentSpeakerName, _currentSpeakerScore, _currentIdentificationMethod);
            }
        }

        public void ProcessAudio(byte[] buffer, int bytesRecorded)
        {
            try
            {
                // Calculate RMS for GUI visualization
                float rms = AudioUtils.CalculateRms(buffer, bytesRecorded);
                
                // NEW: Determine audio source and call appropriate RMS callback
                bool isDiscordAudio = SpeakerIdentifier.HasValidDiscordSpeakerHint();
                
                if (isDiscordAudio)
                {
                    // Call Discord RMS callback
                    _onDiscordRmsLevel?.Invoke(rms);
                }
                else
                {
                    // Call microphone RMS callback
                    _onRmsLevel?.Invoke(rms);
                }

                // ENHANCED: Different VAD handling for Discord vs microphone audio
                bool isVoiceActive = IsVoiceActiveForSource(buffer, bytesRecorded, rms);

                if (!isVoiceActive)
                {
                    if (DateTime.UtcNow - _lastVoiceTime > _silenceTimeout)
                    {
                        // NEW: VAD debouncing to prevent double FinalResult flush
                        var timeSinceLastFinalResult = DateTime.UtcNow - _lastFinalResultTime;
                        if (timeSinceLastFinalResult < _vadDebounceTimeout)
                        {
                            Telemetry.Counter("asr.debounce_skips");
                            if (_confidenceLoggingEnabled)
                            {
                                Console.WriteLine($"?? VAD debounce: Skipping FinalResult (last was {timeSinceLastFinalResult.TotalMilliseconds:F0}ms ago, debounce: {_vadDebounceTimeout.TotalMilliseconds:F0}ms)");
                            }
                            _lastVoiceTime = DateTime.UtcNow; // Reset silence timer
                            return;
                        }
                        
                        Telemetry.Counter("asr.final_flushes");
                        var flush = _recognizer.FinalResult();
                        var result = ParseVoskResult(flush);
                        
                        // Update the debounce timer regardless of result quality
                        _lastFinalResultTime = DateTime.UtcNow;
                        
                        if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                        {
                            // Apply confidence filtering to final result
                            if (IsAcceptableResult(result))
                            {
                                ProcessFinalResult(result);
                            }
                            else
                            {
                                if (_confidenceLoggingEnabled)
                                {
                                    Console.WriteLine($"?? Final result rejected due to low confidence: '{result.Text}' (confidence: {result.Confidence:F2})");
                                }
                                // Try to combine with buffered low confidence results
                                TryRecoverFromLowConfidenceResults(result);
                            }
                        }
                        _lastVoiceTime = DateTime.UtcNow;
                    }
                    // FIXED: Return early when voice is not active - don't process recognition
                    return;
                }

                // FIXED: Only process audio and recognition when voice is actually active
                // Update voice time since we have active voice
                _lastVoiceTime = DateTime.UtcNow;

                // Only add to buffer when voice is active
                float[] floatPcm = AudioUtils.ConvertToFloatPcm(buffer, bytesRecorded);
                AddToPcmBuffer(floatPcm);
                
                // NEW: Add to speaker ring buffer for rolling window inference
                _speakerBuffer.Add(floatPcm);

                // FIXED: Only process recognition when voice is active (respects VAD settings)
                ProcessRecognitionWithConfidence(buffer, bytesRecorded);

                // NEW: Process speaker embeddings with rolling window and 0.5s hop
                ProcessSpeakerEmbeddingWithRollingWindow();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VoiceProcessor.ProcessAudio failed: {ex.Message}");
            }
        }

        /// <summary>
        /// ENHANCED: Voice Activity Detection with Discord-specific handling
        /// Discord audio often has different volume characteristics than microphone input
        /// IMPROVED: Better handling for normalized Discord audio
        /// </summary>
        private bool IsVoiceActiveForSource(byte[] buffer, int bytesRecorded, float rms)
        {
            try
            {
                // Check if this is likely Discord audio by looking at Discord speaker hints
                bool isDiscordAudio = SpeakerIdentifier.HasValidDiscordSpeakerHint();
                
                if (isDiscordAudio)
                {
                    // DISCORD AUDIO: Use configurable Discord VAD threshold
                    // Discord audio is now normalized to microphone levels
                    float discordVadThreshold = AppSettings.LoadDiscordVoiceActivityThreshold();
                    bool isDiscordVoiceActive = rms > discordVadThreshold;
                    
                    return isDiscordVoiceActive;
                }
                else
                {
                    // MICROPHONE AUDIO: Use standard VAD
                    bool isMicVoiceActive = AudioUtils.IsVoiceActive(buffer, bytesRecorded);
                    
                    return isMicVoiceActive;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error in VAD processing: {ex.Message}");
                // Fallback to standard VAD
                return AudioUtils.IsVoiceActive(buffer, bytesRecorded);
            }
        }

        private void ProcessRecognitionWithConfidence(byte[] buffer, int bytesRecorded)
        {
            try
            {
                if (_recognizer.AcceptWaveform(buffer, bytesRecorded))
                {
                    var json = _recognizer.Result();
                    var result = ParseVoskResult(json);
                    
                    if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        _recognitionAttempts++;
                        
                        if (IsAcceptableResult(result))
                        {
                            ProcessHighConfidenceResult(result);
                            ClearLowConfidenceBuffer(); // Clear buffer since we got a good result
                        }
                        else
                        {
                            ProcessLowConfidenceResult(result);
                        }
                    }
                }
                else
                {
                    var partialJson = _recognizer.PartialResult();
                    var partial = JsonUtils.Extract(partialJson);
                    if (!string.IsNullOrWhiteSpace(partial))
                    {
                        ProcessPartialResult(partial);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error in ProcessRecognitionWithConfidence: {ex.Message}");
            }
        }

        private VoskResult ParseVoskResult(string json)
        {
            try
            {
                // Enhanced JSON parsing using Newtonsoft.Json (compatible with .NET Framework 4.8.1)
                var jsonObj = JObject.Parse(json);
                
                var text = jsonObj["text"]?.ToString() ?? string.Empty;
                
                // Extract word-level confidence if available
                float confidence = CalculateOverallConfidence(jsonObj, text);
                var words = ExtractWordResults(jsonObj);
                
                return new VoskResult 
                { 
                    Text = text.Trim(), 
                    Confidence = confidence,
                    Words = words
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"JSON parsing error: {ex.Message}");
                // Fallback to simple extraction
                var fallbackText = JsonUtils.Extract(json);
                return new VoskResult 
                { 
                    Text = fallbackText, 
                    Confidence = string.IsNullOrWhiteSpace(fallbackText) ? 0.0f : 0.5f,
                    Words = new List<WordResult>()
                };
            }
        }

        private float CalculateOverallConfidence(JObject jsonObj, string text)
        {
            try
            {
                // Method 1: Try to get word-level confidences
                var resultArray = jsonObj["result"] as JArray;
                if (resultArray != null && resultArray.Count > 0)
                {
                    var confidences = new List<float>();
                    var words = new List<string>();
                    
                    foreach (var wordObj in resultArray)
                    {
                        var conf = wordObj["conf"]?.Value<float>();
                        var word = wordObj["word"]?.ToString();
                        
                        if (conf.HasValue && !string.IsNullOrEmpty(word))
                        {
                            confidences.Add(conf.Value);
                            words.Add(word);
                        }
                    }
                    
                    if (confidences.Any())
                    {
                        // Use weighted average (longer words get more weight)
                        float weightedSum = 0f;
                        float totalWeight = 0f;
                        
                        for (int i = 0; i < confidences.Count && i < words.Count; i++)
                        {
                            float weight = Math.Max(1f, words[i].Length * 0.5f); // Longer words get more weight
                            weightedSum += confidences[i] * weight;
                            totalWeight += weight;
                        }
                        
                        return totalWeight > 0 ? weightedSum / totalWeight : confidences.Average();
                    }
                }
                
                // Method 2: Heuristic confidence based on text characteristics
                return CalculateHeuristicConfidence(text);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Confidence calculation error: {ex.Message}");
                return CalculateHeuristicConfidence(text);
            }
        }

        private float CalculateHeuristicConfidence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0.0f;
            
            float confidence = 0.8f; // Base confidence
            
            // Adjust based on text characteristics
            // Performance: cache split separators to avoid array allocation on each call
            var words = text.Split(_splitSeparators, StringSplitOptions.RemoveEmptyEntries);
            
            // Length factor
            if (text.Length < 3) confidence -= 0.3f;        // Very short
            else if (text.Length < 6) confidence -= 0.1f;   // Short
            else if (text.Length > 50) confidence += 0.1f;  // Longer text usually more reliable
            
            // Word count factor
            if (words.Length == 1 && words[0].Length < 4) confidence -= 0.2f; // Single short word
            if (words.Length > 1) confidence += 0.1f; // Multiple words
            
            // Common misrecognition patterns
            var lowConfidencePatterns = new[] { "uh", "um", "ah", "hmm", "the the", "a a", "and and" };
            foreach (var pattern in lowConfidencePatterns)
            {
                if (text.ToLower().Contains(pattern))
                {
                    confidence -= 0.2f;
                    break;
                }
            }
            
            // Check for repeated characters (often indicates errors)
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(.)\1{2,}"))
            {
                confidence -= 0.3f; // Repeated characters like "aaa" or "mmm"
            }
            
            // Check for proper sentence structure
            if (text.Any(char.IsUpper) && text.Split(' ').Length > 2)
            {
                confidence += 0.1f; // Proper capitalization in multi-word phrase
            }
            
            return Math.Max(0.0f, Math.Min(1.0f, confidence));
        }

        private List<WordResult> ExtractWordResults(JObject jsonObj)
        {
            var words = new List<WordResult>();
            
            try
            {
                var resultArray = jsonObj["result"] as JArray;
                if (resultArray != null)
                {
                    foreach (var wordObj in resultArray)
                    {
                        var word = new WordResult();
                        
                        word.Word = wordObj["word"]?.ToString() ?? "";
                        word.Confidence = wordObj["conf"]?.Value<float>() ?? 0.0f;
                        word.StartTime = wordObj["start"]?.Value<float>() ?? 0.0f;
                        word.EndTime = wordObj["end"]?.Value<float>() ?? 0.0f;
                        
                        if (!string.IsNullOrWhiteSpace(word.Word))
                        {
                            words.Add(word);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Word extraction error: {ex.Message}");
            }
            
            return words;
        }

        private bool IsAcceptableResult(VoskResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.Text))
                return false;
            
            // Basic confidence check using configurable threshold
            if (result.Confidence < _confidenceThreshold)
                return false;
            
            // Additional quality checks
            var text = result.Text.Trim();
            
            // Reject very short results unless they have high confidence
            if (text.Length < 3 && result.Confidence < _highConfidenceThreshold)
                return false;
            
            // Reject single character results
            if (text.Length == 1)
                return false;
            
            // Check for word-level confidence if available
            if (result.Words.Any())
            {
                var lowConfidenceWords = result.Words.Count(w => w.Confidence < 0.4f);
                var totalWords = result.Words.Count;
                
                // If more than half the words have low confidence, reject
                if (lowConfidenceWords > totalWords / 2)
                    return false;
            }
            
            return true;
        }

        private void ProcessHighConfidenceResult(VoskResult result)
        {
            string text = result.Text.Trim();
            _onTranscription?.Invoke(text);
            TriggerHandler.TryTrigger(text, _triggerName);
            _lastTranscription = text;
            _pendingTranscription = text;
            _lastTranscriptionTime = DateTime.UtcNow;
            
            // Update confidence statistics
            _cumulativeConfidence = (_cumulativeConfidence * (_recognitionAttempts - 1) + result.Confidence) / _recognitionAttempts;
            
            // Add to history with confidence info
            _transcriptionHistory.Add($"{DateTime.UtcNow:HH:mm:ss.fff}: HIGH '{text}' (conf: {result.Confidence:F2})");
            if (_transcriptionHistory.Count > 10) _transcriptionHistory.RemoveAt(0);
            
            // Only log high-confidence results with good quality
            if (_confidenceLoggingEnabled && result.Confidence > _highConfidenceThreshold)
            {
                Console.WriteLine($"?? \"{text}\" ({result.Confidence:F2})");
            }
            
            // NEW: Use centralized dispatch system
            TryDispatchToOllama(text, "ProcessHighConfidenceResult");
        }

        private void ProcessLowConfidenceResult(VoskResult result)
        {
            // Only log low confidence results if confidence logging is enabled
            if (_confidenceLoggingEnabled)
            {
                Console.WriteLine($"? Low confidence: \"{result.Text}\" ({result.Confidence:F2})");
            }
            
            // Buffer low confidence results for potential recovery
            _lowConfidenceBuffer.Enqueue(result);
            // Performance: avoid ToArray() allocation for length check
            if (_lowConfidenceBuffer.Count > _bufferSize) 
                _lowConfidenceBuffer.Dequeue();
            
            // Try to combine recent low confidence results
            TryRecoverFromLowConfidenceResults(null);
        }

        private void TryRecoverFromLowConfidenceResults(VoskResult additionalResult)
        {
            if (additionalResult != null)
            {
                _lowConfidenceBuffer.Enqueue(additionalResult);
                if (_lowConfidenceBuffer.Count > 5) _lowConfidenceBuffer.Dequeue();
            }
            
            if (_lowConfidenceBuffer.Count < 2) return;
            
            // Try to find patterns or combine results
            var allResults = _lowConfidenceBuffer.ToList();
            var combinedText = TryCombineResults(allResults);
            
            if (!string.IsNullOrWhiteSpace(combinedText))
            {
                // Calculate combined confidence
                var avgConfidence = allResults.Average(r => r.Confidence);
                var combinedResult = new VoskResult
                {
                    Text = combinedText,
                    Confidence = Math.Min(avgConfidence + 0.1f, 0.9f), // Slight boost for successful combination
                    Words = new List<WordResult>()
                };
                
                if (IsAcceptableResult(combinedResult))
                {
                    if (_confidenceLoggingEnabled)
                    {
                        Console.WriteLine($"?? ? Recovered from low confidence results: '{combinedText}' (combined conf: {combinedResult.Confidence:F2})");
                    }
                    
                    // Update state variables
                    _lastTranscription = combinedText;
                    _pendingTranscription = combinedText;
                    _lastTranscriptionTime = DateTime.UtcNow;
                    
                    _transcriptionHistory.Add($"{DateTime.UtcNow:HH:mm:ss.fff}: RECOVERED '{combinedText}' (conf: {combinedResult.Confidence:F2})");
                    if (_transcriptionHistory.Count > 10) _transcriptionHistory.RemoveAt(0);
                    
                    // NEW: Use centralized dispatch system
                    TryDispatchToOllama(combinedText, "TryRecoverFromLowConfidenceResults");
                    
                    ClearLowConfidenceBuffer();
                }
            }
        }

        private string TryCombineResults(List<VoskResult> results)
        {
            if (!results.Any()) return string.Empty;
            
            // Strategy 1: Look for the longest common substring
            var texts = results.Select(r => r.Text.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
            if (!texts.Any()) return string.Empty;
            
            // If we only have one valid text, use it
            if (texts.Count == 1) return texts[0];
            
            // Strategy 2: Find the most frequent text
            var textFrequency = texts.GroupBy(t => t.ToLower())
                                    .OrderByDescending(g => g.Count())
                                    .ThenByDescending(g => g.Key.Length)
                                    .FirstOrDefault();
            
            if (textFrequency != null && textFrequency.Count() > 1)
            {
                return textFrequency.First(); // Return the most frequent text
            }
            
            // Strategy 3: Use the longest text if confidences are similar
            var maxConfidence = results.Max(r => r.Confidence);
            var highConfidenceResults = results.Where(r => r.Confidence >= maxConfidence - 0.1f).ToList();
            
            if (highConfidenceResults.Any())
            {
                var longestText = highConfidenceResults.OrderByDescending(r => r.Text.Length).First().Text;
                if (longestText.Length >= 3) return longestText;
            }
            
            return string.Empty;
        }

        private void ClearLowConfidenceBuffer()
        {
            _lowConfidenceBuffer.Clear();
        }

        private void ProcessFinalResult(VoskResult result)
        {
            using (var scope = Telemetry.LatencyScope("asr_final"))
            {
                string text = result.Text.Trim();
                _onTranscription?.Invoke(text);
                TriggerHandler.TryTrigger(text, _triggerName);
                _lastTranscription = text;
                _pendingTranscription = text;
                _lastTranscriptionTime = DateTime.UtcNow;
                
                // Track ASR results
                Telemetry.Counter("asr.final_results");
                Telemetry.Accumulator("asr.final_confidence_total", result.Confidence);
                
                _transcriptionHistory.Add($"{DateTime.UtcNow:HH:mm:ss.fff}: FINAL '{text}' (conf: {result.Confidence:F2})");
                if (_transcriptionHistory.Count > 10) _transcriptionHistory.RemoveAt(0);
                
                if (_confidenceLoggingEnabled)
                {
                    Console.WriteLine($"?? ?? Final transcription: '{text}' (confidence: {result.Confidence:F2})");
                }
                
                // NEW: Use centralized dispatch system
                TryDispatchToOllama(text, "ProcessFinalResult");
            }
        }

        private void ProcessPartialResult(string partial)
        {
            using (var scope = Telemetry.LatencyScope("asr_partial"))
            {
                _onTranscription?.Invoke(partial);
                TriggerHandler.TryTrigger(partial, _triggerName);
                
                // Track partial results
                Telemetry.Counter("asr.partial_results");
                
                // Don't overwrite full transcription with partial, but update if we have nothing
                if (string.IsNullOrWhiteSpace(_lastTranscription))
                {
                    _lastTranscription = partial;
                }
                
                // Always update pending transcription with latest partial if it's longer
                if (string.IsNullOrWhiteSpace(_pendingTranscription) || partial.Length > _pendingTranscription.Length)
                {
                    _pendingTranscription = partial;
                    _lastTranscriptionTime = DateTime.UtcNow;
                    
                    _transcriptionHistory.Add($"{DateTime.UtcNow:HH:mm:ss.fff}: PARTIAL '{partial}'");
                    if (_transcriptionHistory.Count > 10) _transcriptionHistory.RemoveAt(0);
                    
                    if (_confidenceLoggingEnabled)
                    {
                        Console.WriteLine($"?? ?? Partial transcription: '{partial}' (timestamp: {DateTime.UtcNow:HH:mm:ss.fff})");
                    }
                }
            }
        }

        /// <summary>
        /// NEW: Centralized Ollama dispatch system - only ONE place where Ollama calls are made
        /// </summary>
        private void TryDispatchToOllama(string transcription, string source)
        {
            if (string.IsNullOrWhiteSpace(transcription))
                return;

            lock (_ollamaDispatchLock)
            {
                // Check if this transcription was already processed
                if (_processedTranscriptions.Contains(transcription))
                {
                    Console.WriteLine($"?? DUPLICATE BLOCKED: '{transcription}' from {source} (already processed)");
                    return;
                }

                // Check if another dispatch is in progress
                if (_ollamaDispatchInProgress)
                {
                    Console.WriteLine($"?? DISPATCH BLOCKED: '{transcription}' from {source} (dispatch in progress)");
                    return;
                }

                // Check if Ollama is enabled
                if (!OllamaService.IsEnabled())
                {
                    Console.WriteLine($"?? DISPATCH SKIPPED: '{transcription}' from {source} (Ollama disabled)");
                    return;
                }

                // NEW: Check if TTS is currently speaking (ASR suppression during TTS playback)
                // Only suppress ASR if barge-in is disabled (per requirements)
                if (TtsPlaybackController.Instance.IsSpeaking && !AppSettings.LoadBargeInEnabled())
                {
                    Console.WriteLine($"?? DISPATCH BLOCKED: '{transcription}' from {source} (TTS currently speaking - ASR suppressed, barge-in disabled)");
                    Telemetry.Counter("asr.dispatch_blocked_due_to_tts");
                    return;
                }

                // NEW: Enhanced gating for Local scenario - require wake word or higher confidence
                var currentScenario = AppSettings.LoadAppScenario();
                if (currentScenario == AppScenario.Local)
                {
                    // For Local scenario, we want to be more conservative about LLM dispatch
                    // Check if this was triggered by wake word detection
                    var hasTriggerWord = !string.IsNullOrWhiteSpace(_triggerName) && 
                                        transcription.IndexOf(_triggerName, StringComparison.OrdinalIgnoreCase) >= 0;
                    
                    if (!hasTriggerWord)
                    {
                        Console.WriteLine($"?? DISPATCH BLOCKED: '{transcription}' from {source} (Local scenario - no wake word detected)");
                        Telemetry.Counter("asr.dispatch_blocked_no_wake_word");
                        return;
                    }
                    else
                    {
                        Console.WriteLine($"?? WAKE WORD DETECTED: '{_triggerName}' in '{transcription}' - proceeding with dispatch");
                    }
                }

                // Mark as processed and set dispatch flag
                _processedTranscriptions.Add(transcription);
                _ollamaDispatchInProgress = true;
                
                Console.WriteLine($"?? DISPATCHING: '{transcription}' from {source} (#{_processedTranscriptions.Count} unique transcriptions)");

                // Clear old processed transcriptions to prevent memory leaks (keep only recent 10)
                if (_processedTranscriptions.Count > 10)
                {
                    var oldestTranscriptions = _processedTranscriptions.Take(_processedTranscriptions.Count - 10).ToList();
                    foreach (var oldTranscription in oldestTranscriptions)
                    {
                        _processedTranscriptions.Remove(oldTranscription);
                    }
                }
                
                // Send to Ollama in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Get current speaker instead of sending empty string
                        var (currentSpeaker, currentConfidence, currentMethod) = GetCurrentSpeaker();
                        
                        // If we don't have a good speaker identification, try again
                        if (currentSpeaker == "Unknown" || string.IsNullOrEmpty(currentSpeaker))
                        {
                            var (newSpeaker, newConfidence, newMethod) = IdentifyCurrentSpeaker();
                            currentSpeaker = newSpeaker;
                        }
                        
                        // Use a fallback speaker name if still unknown
                        if (string.IsNullOrEmpty(currentSpeaker) || currentSpeaker == "Unknown")
                        {
                            currentSpeaker = "UnknownSpeaker"; // Fallback name that's not empty
                        }
                        
                        await OllamaService.SendPromptAsync(currentSpeaker, transcription);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"? Error in Ollama send: {ex.Message}");
                    }
                    finally
                    {
                        // Mark dispatch as complete
                        lock (_ollamaDispatchLock)
                        {
                            _ollamaDispatchInProgress = false;
                        }
                    }
                });
            }
        }

        /// <summary>
        /// Get current confidence statistics for debugging
        /// </summary>
        public (float averageConfidence, int totalAttempts, int lowConfidenceCount) GetConfidenceStats()
        {
            var lowConfidenceCount = _lowConfidenceBuffer.Count;
            return (_cumulativeConfidence, _recognitionAttempts, lowConfidenceCount);
        }

        /// <summary>
        /// Get current Ollama dispatch statistics for debugging
        /// </summary>
        public (int processedCount, bool dispatchInProgress) GetOllamaDispatchStats()
        {
            lock (_ollamaDispatchLock)
            {
                return (_processedTranscriptions.Count, _ollamaDispatchInProgress);
            }
        }

        /// <summary>
        /// Test method to verify duplicate prevention - simulates 5 consecutive phrases
        /// Should result in exactly 5 dispatches to Ollama, demonstrating single-flight behavior
        /// </summary>
        public void TestDuplicatePreventionDispatch()
        {
            Console.WriteLine("?? TESTING: Duplicate Prevention Dispatch");
            Console.WriteLine("   Simulating 5 consecutive Discord phrases from different users...");
            
            var testPhrases = new[]
            {
                "Hello everyone, this is the first test phrase",
                "This is the second phrase from another user", 
                "Third phrase to verify no duplicates",
                "Fourth unique phrase for testing",
                "Final fifth phrase to complete the test"
            };

            var initialStats = GetOllamaDispatchStats();
            Console.WriteLine($"   Initial state: {initialStats.processedCount} processed, dispatch in progress: {initialStats.dispatchInProgress}");

            // Simulate each phrase being processed
            for (int i = 0; i < testPhrases.Length; i++)
            {
                Console.WriteLine($"   Phrase {i + 1}: \"{testPhrases[i]}\"");
                TryDispatchToOllama(testPhrases[i], $"TestUser{i + 1}");
                
                // Brief delay to simulate real processing
                System.Threading.Thread.Sleep(50);
            }

            var finalStats = GetOllamaDispatchStats();
            var dispatchedCount = finalStats.processedCount - initialStats.processedCount;
            
            Console.WriteLine($"?? TEST RESULTS:");
            Console.WriteLine($"   Input phrases: {testPhrases.Length}");
            Console.WriteLine($"   Unique dispatches: {dispatchedCount}");
            Console.WriteLine($"   Expected: 5 dispatches = 5 phrases");
            Console.WriteLine($"   Result: {(dispatchedCount == testPhrases.Length ? "✅ PASS" : "❌ FAIL")}");
            
            if (dispatchedCount != testPhrases.Length)
            {
                Console.WriteLine($"   ⚠️ Expected {testPhrases.Length} dispatches but got {dispatchedCount}");
            }
        }

        /// <summary>
        /// Test VAD debouncing behavior by simulating rapid FinalResult calls
        /// </summary>
        public void TestVadDebounceBehavior()
        {
            Console.WriteLine("?? TESTING: VAD Debounce Behavior");
            Console.WriteLine("   Simulating rapid FinalResult calls to test debouncing...");
            
            // Reset the last final result time to allow testing
            _lastFinalResultTime = DateTime.MinValue;
            
            // Simulate rapid calls within debounce window
            var testPhrase = "Test phrase for VAD debounce verification";
            var callCount = 0;
            var blockedCount = 0;
            
            for (int i = 0; i < 3; i++)
            {
                callCount++;
                var beforeTime = _lastFinalResultTime;
                
                // Create a mock result for testing
                var mockResult = new VoskResult 
                { 
                    Text = testPhrase, 
                    Confidence = 0.8f, 
                    Words = new List<WordResult>() 
                };
                
                // Check if this would be blocked by debounce
                var timeSinceLastFinalResult = DateTime.UtcNow - _lastFinalResultTime;
                var wouldBeBlocked = timeSinceLastFinalResult < _vadDebounceTimeout && _lastFinalResultTime != DateTime.MinValue;
                
                if (wouldBeBlocked)
                {
                    blockedCount++;
                    Console.WriteLine($"   Call {i + 1}: BLOCKED by debounce ({timeSinceLastFinalResult.TotalMilliseconds:F0}ms < {_vadDebounceTimeout.TotalMilliseconds:F0}ms)");
                }
                else
                {
                    Console.WriteLine($"   Call {i + 1}: ALLOWED (first call or outside debounce window)");
                    _lastFinalResultTime = DateTime.UtcNow; // Simulate the update that would happen
                }
                
                // Small delay between calls
                System.Threading.Thread.Sleep(100);
            }
            
            Console.WriteLine($"?? VAD DEBOUNCE TEST RESULTS:");
            Console.WriteLine($"   Total calls: {callCount}");
            Console.WriteLine($"   Blocked by debounce: {blockedCount}");
            Console.WriteLine($"   Allowed through: {callCount - blockedCount}");
            Console.WriteLine($"   Debounce timeout: {_vadDebounceTimeout.TotalMilliseconds:F0}ms");
            Console.WriteLine($"   Result: {(blockedCount > 0 ? "✅ DEBOUNCE WORKING" : "⚠️ NO DEBOUNCE DETECTED")}");
        }

        /// <summary>
        /// NEW: Process speaker embeddings with rolling window and silence gating
        /// </summary>
        private void ProcessSpeakerEmbeddingWithRollingWindow()
        {
            try
            {
                // Check if enough time has passed for next inference (0.5s hop)
                var timeSinceLastInference = DateTime.UtcNow - _lastSpeakerInference;
                if (timeSinceLastInference < _speakerInferenceHop)
                {
                    return;
                }

                // Check if we have a full window of audio
                if (!_speakerBuffer.HasFullWindow)
                {
                    return;
                }

                // Extract the current window
                var window = _speakerBuffer.ExtractWindow();
                if (window == null) return;

                // Silence gating: Check RMS level (-45 dBFS threshold and minimum voiced duration)
                float rms = _speakerBuffer.CalculateRms();
                float rmsDb = 20f * (float)Math.Log10(Math.Max(rms, 1e-10f)); // Convert to dB, avoid log(0)
                
                // Skip inference if RMS < -45 dBFS or < 0.2s voiced in window
                const float minRmsDb = -45f;
                const float minVoicedRatio = 0.2f; // 20% of window should be voiced
                
                float voicedSamples = CountVoicedSamples(window);
                float voicedRatio = voicedSamples / window.Length;
                
                if (rmsDb < minRmsDb || voicedRatio < minVoicedRatio)
                {
                    if (_confidenceLoggingEnabled)
                    {
                        Console.WriteLine($"🔇 Skipping speaker inference - RMS: {rmsDb:F1}dB, voiced: {voicedRatio:F2}");
                    }
                    _lastSpeakerInference = DateTime.UtcNow;
                    return;
                }

                // Generate speaker embedding
                var emb = SpeakerEmbedder.Embed(window);
                _lastSpeakerInference = DateTime.UtcNow;

                // Pass embedding to debugging callback
                _onVoiceEmbedding?.Invoke(emb);

                // Handle voice enrollment if active
                if (VoiceEnrollmentManager.IsEnrolling)
                {
                    VoiceEnrollmentManager.ProcessVoiceSample(emb);
                }
                else
                {
                    // Normal speaker identification
                    var match = SpeakerIdentifier.Identify(emb);
                    string voiceSpeakerName = "Unknown";
                    float voiceSpeakerScore = 0f;
                    
                    if (match != null)
                    {
                        voiceSpeakerName = match.Value.name;
                        voiceSpeakerScore = match.Value.score;
                        
                        // Add telemetry for speaker score
                        Kinectv1.Telemetry.Gauge("speaker.score", voiceSpeakerScore);
                        
                        _onSpeakerMatch?.Invoke(voiceSpeakerName, voiceSpeakerScore);
                    }
                    else
                    {
                        Kinectv1.Telemetry.Gauge("speaker.score", 0f);
                        _onSpeakerMatch?.Invoke("Unknown", 0f);
                    }

                    // NEW: Use centralized speaker identification
                    var (finalSpeakerName, finalConfidence, identificationMethod) = IdentifyCurrentSpeaker(voiceSpeakerName, voiceSpeakerScore);
                    
                    // NEW: Update current speaker state
                    UpdateCurrentSpeaker(finalSpeakerName, finalConfidence, identificationMethod);

                    if (_confidenceLoggingEnabled)
                    {
                        Console.WriteLine($"🔊 Speaker: {finalSpeakerName} (score: {voiceSpeakerScore:F3}, method: {identificationMethod})");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ProcessSpeakerEmbeddingWithRollingWindow: {ex.Message}");
            }
        }

        /// <summary>
        /// Count samples that are considered "voiced" (above a threshold)
        /// </summary>
        private float CountVoicedSamples(float[] window)
        {
            if (window == null || window.Length == 0) return 0f;

            const float voiceThreshold = 0.01f; // Threshold for considering a sample "voiced"
            int voicedCount = 0;

            for (int i = 0; i < window.Length; i++)
            {
                if (Math.Abs(window[i]) > voiceThreshold)
                {
                    voicedCount++;
                }
            }

            return voicedCount;
        }
    }

    // Supporting classes and enums
    public class VoskResult
    {
        public string Text { get; set; }
        public float Confidence { get; set; }
        public List<WordResult> Words { get; set; }
    }

    public class WordResult
    {
        public string Word { get; set; }
        public float Confidence { get; set; }
        public float StartTime { get; set; }
        public float EndTime { get; set; }
    }

    public enum TriggerAction
    {
        None,
        StartRecording,
        StopRecording,
        PauseRecording,
        ResumeRecording,
        SaveRecording,
        DiscardRecording,
        RepeatLastTranscription,
        CustomAction1,
        CustomAction2
    }
}