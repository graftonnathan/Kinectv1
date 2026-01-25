using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kinectv1.Settings;
using Kinectv1.Voice;
using Kinectv1.Llm;

namespace Kinectv1.Services.Transcription
{
    /// <summary>
    /// Manages transcription-only mode: logs transcriptions to dated files and generates
    /// LLM summaries when the meeting ends. Supports speaker diarization.
    /// </summary>
    public sealed class TranscriptionService : IDisposable
    {
        private static TranscriptionService _instance;
        private static readonly object _lock = new object();

        public static TranscriptionService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new TranscriptionService();
                    }
                }
                return _instance;
            }
        }

        // Events
        public static event Action<string> OnLog;
        public static event Action<string> OnSummaryGenerated;
        public static event Action<string> OnTranscriptionFileCreated;
        public static event Action<string, string> OnSpeakerIdentified; // (label, text)

        // State
        private bool _isActive;
        private string _currentSessionFile;
        private DateTime _sessionStartTime;
        private DateTime _lastTranscriptionTime;
        private readonly List<TranscriptionEntry> _sessionEntries = new();
        private Timer _summaryTimer;
        private CancellationTokenSource _summaryCts;
        private readonly object _entriesLock = new object();

        // Voice embedding for diarization
        private float[] _lastEmbedding;
        private readonly object _embeddingLock = new object();

        // WebRTC speaker boundary detection with hysteresis
        private string _activeSpeakerLabel;
        private string _pendingSpeakerLabel;
        private int _pendingSpeakerCount;
        private volatile bool _webrtcActive;

        // Embeddings seem to hop ~500ms; 3 counts ? 1.5s stable (increased for stability)
        private const int WebRtcSpeakerSwitchConfirmCount = 3;

        // Segment index store (persistent)
        private TranscriptSegmentIndexStore _segmentStore;
        private LmStudioEmbeddingClient _segmentEmbeddingClient;
        private Task _segmentStoreLoadTask;
        private readonly SemaphoreSlim _segmentStoreIoMutex = new SemaphoreSlim(1, 1);
        private int _segmentIndexInSession = 0;
        private readonly object _chunkLock = new();
        private readonly StringBuilder _chunkBuilder = new();
        private double _chunkStartSec = double.NaN;
        private double _chunkEndSec = double.NaN;

        public bool IsActive => _isActive;
        public string CurrentSessionFile => _currentSessionFile;
        public int EntryCount { get { lock (_entriesLock) return _sessionEntries.Count; } }
        public int SpeakerCount => SpeakerDiarizer.Instance.SpeakerCount;

        /// <summary>
        /// Set whether WebRTC mode is active for speaker boundary detection.
        /// Use this instead of checking LastFrameSource which can be racy.
        /// </summary>
        public void SetWebRtcActive(bool active)
        {
            _webrtcActive = active;
            if (!active)
            {
                // Reset speaker boundary state when WebRTC is disabled
                _activeSpeakerLabel = null;
                _pendingSpeakerLabel = null;
                _pendingSpeakerCount = 0;
            }
        }

        private TranscriptionService() 
        {
            // Subscribe to voice embedding events
            SpeakerEmbedder.OnEmbedding += OnVoiceEmbedding;
        }

        private void OnVoiceEmbedding(float[] embedding)
        {
            // Store embedding regardless of session state (for speaker resolution later)
            lock (_embeddingLock)
            {
                _lastEmbedding = embedding;
            }

            // HARD GATE: only drive speaker boundaries when BOTH:
            // 1. WebRTC is explicitly active
            // 2. Transcription mode is enabled (we're in a transcription session)
            // In normal chat mode, we don't want speaker switches to interrupt/flush partial STT
            if (!_webrtcActive)
                return;
                
            // Only trigger speaker boundary flushes in transcription mode
            // In normal WebUI chat mode, let the silence timeout handle emission naturally
            if (!_isActive)
                return;

            if (embedding == null || embedding.Length == 0)
                return;

            // Identify speaker label from diarizer (A/B/C...)
            var (label, conf) = SpeakerDiarizer.Instance.IdentifyOrAssign(embedding);
            
            if (string.IsNullOrWhiteSpace(label) || label.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return;

            if (_activeSpeakerLabel == null)
            {
                _activeSpeakerLabel = label;
                _pendingSpeakerLabel = null;
                _pendingSpeakerCount = 0;
                Console.WriteLine($"[Diarizer] Initial speaker: {label}");
                return;
            }

            if (!label.Equals(_activeSpeakerLabel, StringComparison.OrdinalIgnoreCase))
            {
                // Hysteresis so diarizer doesn't chatter A/B at boundaries
                if (_pendingSpeakerLabel == null || !_pendingSpeakerLabel.Equals(label, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingSpeakerLabel = label;
                    _pendingSpeakerCount = 1;
                    Console.WriteLine($"[Diarizer] Possible switch: {_activeSpeakerLabel} -> {label} (1/{WebRtcSpeakerSwitchConfirmCount})");
                }
                else
                {
                    _pendingSpeakerCount++;
                    if (_pendingSpeakerCount >= WebRtcSpeakerSwitchConfirmCount)
                    {
                        // Speaker switch confirmed -> force WebRTC boundary
                        Console.WriteLine($"[Diarizer] SWITCH CONFIRMED: {_activeSpeakerLabel} -> {label}");
                        Kinectv1.VoiceRecognizer.ForceWebRtcBoundary("speaker_change");

                        _activeSpeakerLabel = label;
                        _pendingSpeakerLabel = null;
                        _pendingSpeakerCount = 0;
                    }
                }
            }
            else
            {
                // Same speaker - reset pending state silently
                _pendingSpeakerLabel = null;
                _pendingSpeakerCount = 0;
            }
        }

        /// <summary>
        /// Start a new transcription session.
        /// </summary>
        public void StartSession()
        {
            if (_isActive) return;

            var cfg = App.SettingsProvider?.Current?.Transcription;
            if (cfg == null)
            {
                Log("[Transcription] Settings unavailable");
                return;
            }

            _isActive = true;
            _sessionStartTime = DateTime.Now;
            _lastTranscriptionTime = _sessionStartTime;

            lock (_entriesLock)
            {
                _sessionEntries.Clear();
                _segmentIndexInSession = 0;
            }
            ResetChunkBuilder();

            // Reset diarizer for new session
            SpeakerDiarizer.Instance.Reset();

            // Clear last embedding and speaker boundary state
            lock (_embeddingLock)
            {
                _lastEmbedding = null;
            }
            _activeSpeakerLabel = null;
            _pendingSpeakerLabel = null;
            _pendingSpeakerCount = 0;

            // Create output folder if needed
            var outputFolder = GetOutputFolder(cfg);
            try
            {
                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                }
            }
            catch (Exception ex)
            {
                Log($"[Transcription] Failed to create output folder: {ex.Message}");
            }

            // Generate filename: Meeting_2024-01-15_14-30-00.txt (unique per session start)
            _currentSessionFile = GetUniqueSessionFilePath(outputFolder, _sessionStartTime);

            // Ensure segment index is initialized now that we know output folder/session id
            try { EnsureSegmentIndexInitialized(); } catch { }

            // Write header
            try
            {
                File.WriteAllText(_currentSessionFile,
                    $"=== Meeting Transcription ===\n" +
                    $"Started: {_sessionStartTime:yyyy-MM-dd HH:mm:ss}\n" +
                    $"---\n\n",
                    Encoding.UTF8);

                OnTranscriptionFileCreated?.Invoke(_currentSessionFile);
                Log($"[Transcription] Session started: {_currentSessionFile}");
            }
            catch (Exception ex)
            {
                Log($"[Transcription] Failed to create session file: {ex.Message}");
            }

            // Start the summary timer if enabled
            if (cfg.GenerateSummary)
            {
                StartSummaryTimer(cfg.SummaryDelaySeconds);
            }
        }

        /// <summary>
        /// Stop the current transcription session and optionally generate a summary.
        /// </summary>
        public async Task StopSessionAsync(bool generateSummary = true)
        {
            if (!_isActive) return;

            _isActive = false;
            StopSummaryTimer();
            await FlushTranscriptChunkAsync(force: true);

            var cfg = App.SettingsProvider?.Current?.Transcription;

            // Finalize the file
            try
            {
                if (!string.IsNullOrEmpty(_currentSessionFile) && File.Exists(_currentSessionFile))
                {
                    var endTime = DateTime.Now;
                    var duration = endTime - _sessionStartTime;

                    // Get speaker statistics
                    var speakerStats = GetSpeakerStatistics();

                    File.AppendAllText(_currentSessionFile,
                        $"\n---\n" +
                        $"Ended: {endTime:yyyy-MM-dd HH:mm:ss}\n" +
                        $"Duration: {duration:hh\\:mm\\:ss}\n" +
                        $"Total entries: {EntryCount}\n" +
                        $"Speakers identified: {SpeakerDiarizer.Instance.SpeakerCount}\n" +
                        $"{speakerStats}\n",
                        Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Log($"[Transcription] Failed to finalize file: {ex.Message}");
            }

            // Generate summary if enabled and requested
            if (generateSummary && cfg?.GenerateSummary == true && EntryCount > 0)
            {
                await GenerateSummaryAsync();
            }

            Log($"[Transcription] Session ended. Total entries: {EntryCount}, Speakers: {SpeakerDiarizer.Instance.SpeakerCount}");

            lock (_entriesLock)
            {
                _sessionEntries.Clear();
            }
            _currentSessionFile = null;
        }

        /// <summary>
        /// Add a transcription entry to the current session with automatic speaker diarization.
        /// </summary>
        public void AddTranscription(string speaker, string text)
        {
            AddTranscription(speaker, text, null);
        }

        /// <summary>
        /// Add a transcription entry with explicit voice embedding for diarization.
        /// </summary>
        public void AddTranscription(string speaker, string text, float[] embedding)
        {
            if (!_isActive) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            // Get embedding if not provided
            if (embedding == null)
            {
                lock (_embeddingLock)
                {
                    embedding = _lastEmbedding;
                }
            }

            // Determine the speaker label
            string resolvedSpeaker = ResolveSpeaker(speaker, embedding);

            var entry = new TranscriptionEntry
            {
                Timestamp = DateTime.Now,
                Speaker = resolvedSpeaker,
                Text = text.Trim()
            };

            lock (_entriesLock)
            {
                _sessionEntries.Add(entry);
            }

            _lastTranscriptionTime = entry.Timestamp;

            // Append to chunked transcript buffer and embed when token budget reached
            _ = Task.Run(async () =>
            {
                try { await AppendTranscriptChunkAsync(entry).ConfigureAwait(false); }
                catch { }
            });

            // Notify listeners
            try { OnSpeakerIdentified?.Invoke(resolvedSpeaker, text); } catch { }

            // Append to file
            try
            {
                if (!string.IsNullOrEmpty(_currentSessionFile))
                {
                    var line = $"[{entry.Timestamp:HH:mm:ss}] {entry.Speaker}: {entry.Text}\n";
                    File.AppendAllText(_currentSessionFile, line, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Log($"[Transcription] Failed to write entry: {ex.Message}");
            }

            // Reset summary timer
            var cfg = App.SettingsProvider?.Current?.Transcription;
            if (cfg?.GenerateSummary == true)
            {
                ResetSummaryTimer(cfg.SummaryDelaySeconds);
            }
        }

        /// <summary>
        /// Resolve the speaker label using enrolled speakers first, then diarization.
        /// </summary>
        private string ResolveSpeaker(string providedSpeaker, float[] embedding)
        {
            // If a known enrolled speaker was already identified, use that
            if (!string.IsNullOrWhiteSpace(providedSpeaker) && 
                !providedSpeaker.Equals("UnknownSpeaker", StringComparison.OrdinalIgnoreCase) &&
                !providedSpeaker.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                // Check if this is an enrolled speaker (not a diarization label)
                if (!providedSpeaker.StartsWith("Speaker ", StringComparison.OrdinalIgnoreCase))
                {
                    return providedSpeaker;
                }
            }

            // Try to identify from embedding
            if (embedding != null && embedding.Length > 0)
            {
                // First, check against enrolled speakers
                var (enrolledName, enrolledScore) = SpeakerIdentifier.IdentifyFromEmbedding(embedding);
                
                // Use enrolled speaker if confidence is high enough
                double threshold = 0.6;
                try { threshold = App.SettingsProvider?.Current?.Audio?.SpeakerMatchMinScore ?? 0.6; } catch { }
                
                if (!string.IsNullOrWhiteSpace(enrolledName) && 
                    !enrolledName.Equals("UnknownSpeaker", StringComparison.OrdinalIgnoreCase) &&
                    enrolledScore >= threshold)
                {
                    return enrolledName;
                }

                // Fall back to diarization
                var (diarizedLabel, diarizedScore) = SpeakerDiarizer.Instance.IdentifyOrAssign(embedding);
                return diarizedLabel;
            }

            // No embedding available - use provided speaker or Unknown
            if (!string.IsNullOrWhiteSpace(providedSpeaker) &&
                !providedSpeaker.Equals("UnknownSpeaker", StringComparison.OrdinalIgnoreCase))
            {
                return providedSpeaker;
            }

            return "Unknown";
        }

        /// <summary>
        /// Get statistics about speaker participation.
        /// </summary>
        private string GetSpeakerStatistics()
        {
            List<TranscriptionEntry> entries;
            lock (_entriesLock)
            {
                entries = new List<TranscriptionEntry>(_sessionEntries);
            }

            if (entries.Count == 0)
            {
                return "";
            }

            var speakerCounts = entries
                .GroupBy(e => e.Speaker, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Speaker = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Speaker participation:");
            foreach (var sc in speakerCounts)
            {
                var pct = (sc.Count * 100.0 / entries.Count);
                sb.AppendLine($"  {sc.Speaker}: {sc.Count} entries ({pct:F1}%)");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate a summary of the meeting using the LLM.
        /// </summary>
        private async Task GenerateSummaryAsync()
        {
            List<TranscriptionEntry> entries;
            lock (_entriesLock)
            {
                entries = new List<TranscriptionEntry>(_sessionEntries);
            }

            if (entries.Count == 0)
            {
                Log("[Transcription] No entries to summarize");
                return;
            }

            Log("[Transcription] Generating meeting summary...");

            try
            {
                // Build the transcript for the LLM
                var sb = new StringBuilder();
                sb.AppendLine("Please provide a brief summary of the following meeting transcript. Include:");
                sb.AppendLine("1. Main topics discussed");
                sb.AppendLine("2. Key decisions or action items");
                sb.AppendLine("3. Participants and their main contributions (note: speakers may be labeled as Speaker A, Speaker B, etc. if not enrolled)");
                sb.AppendLine();
                sb.AppendLine("Transcript:");
                sb.AppendLine("---");

                foreach (var entry in entries)
                {
                    sb.AppendLine($"[{entry.Timestamp:HH:mm:ss}] {entry.Speaker}: {entry.Text}");
                }

                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("Provide a concise summary:");

                var prompt = sb.ToString();

                // Use OllamaService to generate summary
                _summaryCts = new CancellationTokenSource();
                var summary = await GenerateLlmSummaryAsync(prompt, _summaryCts.Token);

                if (!string.IsNullOrWhiteSpace(summary))
                {
                    // Append summary to the file
                    if (!string.IsNullOrEmpty(_currentSessionFile) && File.Exists(_currentSessionFile))
                    {
                        File.AppendAllText(_currentSessionFile,
                            $"\n=== Meeting Summary ===\n{summary}\n",
                            Encoding.UTF8);
                    }

                    // Also create a summary-only file
                    var summaryFileName = Path.ChangeExtension(_currentSessionFile, ".summary.txt");
                    File.WriteAllText(summaryFileName,
                        $"Meeting Summary\n" +
                        $"Date: {_sessionStartTime:yyyy-MM-dd HH:mm}\n" +
                        $"Duration: {(DateTime.Now - _sessionStartTime):hh\\:mm\\:ss}\n" +
                        $"Participants: {GetUniqueParticipants(entries)}\n" +
                        $"Speakers identified: {SpeakerDiarizer.Instance.SpeakerCount}\n\n" +
                        $"{summary}",
                        Encoding.UTF8);

                    Log($"[Transcription] Summary generated: {summaryFileName}");
                    OnSummaryGenerated?.Invoke(summary);
                }
            }
            catch (OperationCanceledException)
            {
                Log("[Transcription] Summary generation cancelled");
            }
            catch (Exception ex)
            {
                Log($"[Transcription] Summary generation failed: {ex.Message}");
            }
        }

        private async Task<string> GenerateLlmSummaryAsync(string prompt, CancellationToken ct)
        {
            try
            {
                // Check if Ollama is available
                if (!OllamaService.IsEnabled())
                {
                    Log("[Transcription] LLM not available for summary generation");
                    return null;
                }

                // Use non-streaming call to avoid shared streaming events
                var response = await OllamaService.ChatOnceAsync(null, prompt, ct, skipHistory: true).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(response) ? null : response;
            }
            catch (Exception ex)
            {
                Log($"[Transcription] LLM summary error: {ex.Message}");
                return null;
            }
        }

        private string GetUniqueParticipants(List<TranscriptionEntry> entries)
        {
            var participants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Speaker) && 
                    !entry.Speaker.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    participants.Add(entry.Speaker);
                }
            }
            return participants.Count > 0 ? string.Join(", ", participants.OrderBy(p => p)) : "Unknown";
        }

        private void StartSummaryTimer(int delaySeconds)
        {
            StopSummaryTimer();
            _summaryTimer = new Timer(OnSummaryTimerElapsed, null, 
                TimeSpan.FromSeconds(delaySeconds), 
                Timeout.InfiniteTimeSpan);
        }

        private void ResetSummaryTimer(int delaySeconds)
        {
            _summaryTimer?.Change(TimeSpan.FromSeconds(delaySeconds), Timeout.InfiniteTimeSpan);
        }

        private void StopSummaryTimer()
        {
            try
            {
                _summaryTimer?.Dispose();
                _summaryTimer = null;
                _summaryCts?.Cancel();
                _summaryCts = null;
            }
            catch { }
        }

        private void OnSummaryTimerElapsed(object state)
        {
            // Auto-generate summary after idle period
            if (_isActive && EntryCount > 0)
            {
                var timeSinceLastEntry = DateTime.Now - _lastTranscriptionTime;
                var cfg = App.SettingsProvider?.Current?.Transcription;

                if (timeSinceLastEntry.TotalSeconds >= (cfg?.SummaryDelaySeconds ?? 60))
                {
                    Log("[Transcription] Idle timeout reached, generating summary...");
                    _ = Task.Run(async () =>
                    {
                        await GenerateSummaryAsync();
                        // Don't stop the session automatically - user can continue
                    });
                }
            }
        }

        private static string GetOutputFolder(TranscriptionSettings cfg)
        {
            var folder = cfg?.OutputFolder ?? "transcriptions";
            if (!Path.IsPathRooted(folder))
            {
                folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder);
            }
            return folder;
        }

        private static void Log(string message)
        {
            try
            {
                Console.WriteLine(message);
                OnLog?.Invoke(message);
            }
            catch { }
        }

        private void EnsureSegmentIndexInitialized()
        {
            if (_segmentStore != null) return;

            try
            {
                // Prefer the active session's folder if available so recall can find it.
                // Fallback to settings folder for initialization outside a running session.
                string folder;
                try
                {
                    folder = !string.IsNullOrWhiteSpace(_currentSessionFile)
                        ? (Path.GetDirectoryName(_currentSessionFile) ?? GetOutputFolder(App.SettingsProvider?.Current?.Transcription))
                        : GetOutputFolder(App.SettingsProvider?.Current?.Transcription);
                }
                catch
                {
                    folder = GetOutputFolder(App.SettingsProvider?.Current?.Transcription);
                }

                Directory.CreateDirectory(folder);

                var path = Path.Combine(folder, "transcript_segments.json");
                _segmentStore = new TranscriptSegmentIndexStore(path);

                _segmentEmbeddingClient = new LmStudioEmbeddingClient(
                    App.SettingsProvider?.Current?.Ollama?.LmStudioBaseUrl ?? "http://127.0.0.1:1234",
                    App.SettingsProvider?.Current?.Ollama?.ApiKey,
                    () => App.SettingsProvider?.Current?.Ollama?.EmbeddingsModel);

                _segmentStoreLoadTask = Task.Run(async () =>
                {
                    await _segmentStoreIoMutex.WaitAsync().ConfigureAwait(false);
                    try { await _segmentStore.LoadAsync().ConfigureAwait(false); }
                    finally { _segmentStoreIoMutex.Release(); }
                });

                Console.WriteLine($"[TranscriptSegmentIndex] Using store: {path}");
            }
            catch (Exception ex)
            {
                Log($"[Transcription] Segment index init failed: {ex.Message}");
            }
        }

        private static string GetSessionIdFromSessionFile(string sessionFile)
        {
            if (string.IsNullOrWhiteSpace(sessionFile)) return string.Empty;
            try { return Path.GetFileNameWithoutExtension(sessionFile) ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string GetSessionTitleFromSessionFile(string sessionFile)
        {
            // Current sessions are named Meeting_yyyy-MM-dd_HH-mm-ss.txt
            // Title is stable and user-facing.
            return GetSessionIdFromSessionFile(sessionFile);
        }

        private double GetCurrentSessionElapsedSeconds(DateTime entryTimestampLocal)
        {
            try
            {
                var start = _sessionStartTime;
                var dt = entryTimestampLocal - start;
                if (dt.TotalSeconds < 0) return 0;
                return dt.TotalSeconds;
            }
            catch { return 0; }
        }

        private int GetTranscriptChunkTokenLimit()
        {
            try
            {
                var limit = App.SettingsProvider?.Current?.Transcription?.TranscriptChunkTokenLimit ?? 600;
                return Math.Clamp(limit, 200, 4000);
            }
            catch { return 600; }
        }

        private static double EstimateEntryDurationSeconds(string text)
        {
            try
            {
                var words = text?.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)?.Length ?? 0;
                var estimated = words / 2.5;
                return Math.Clamp(estimated, 0.5, 20.0);
            }
            catch { return 1.0; }
        }

        private async Task AppendTranscriptChunkAsync(TranscriptionEntry entry)
        {
            try
            {
                EnsureSegmentIndexInitialized();
                if (_segmentStore == null) return;

                var load = _segmentStoreLoadTask;
                if (load != null) { try { await load.ConfigureAwait(false); } catch { } }

                double tEnd = GetCurrentSessionElapsedSeconds(entry.Timestamp);
                double estimatedDur = EstimateEntryDurationSeconds(entry.Text);
                double tStart = Math.Max(0, tEnd - estimatedDur);

                var line = $"[{entry.Timestamp:HH:mm:ss}] {entry.Speaker}: {entry.Text}";
                string chunkText = null;
                double chunkStart = 0;
                double chunkEnd = 0;
                int chunkIndex = -1;

                lock (_chunkLock)
                {
                    if (_chunkBuilder.Length > 0) _chunkBuilder.AppendLine();
                    _chunkBuilder.Append(line);

                    if (double.IsNaN(_chunkStartSec))
                        _chunkStartSec = tStart;
                    _chunkEndSec = tEnd;

                    int limit = GetTranscriptChunkTokenLimit();
                    var tokens = TokenBudget.EstimateTokens(_chunkBuilder.ToString());
                    if (tokens >= limit)
                    {
                        chunkText = _chunkBuilder.ToString();
                        chunkStart = double.IsNaN(_chunkStartSec) ? 0 : _chunkStartSec;
                        chunkEnd = _chunkEndSec;
                        chunkIndex = _segmentIndexInSession++;
                        _chunkBuilder.Clear();
                        _chunkStartSec = double.NaN;
                        _chunkEndSec = double.NaN;
                    }
                }

                if (!string.IsNullOrWhiteSpace(chunkText) && chunkIndex >= 0)
                {
                    await EmbedTranscriptChunkAsync(chunkText, chunkStart, chunkEnd, chunkIndex).ConfigureAwait(false);
                }
            }
            catch { }
        }

        private async Task FlushTranscriptChunkAsync(bool force = false)
        {
            string chunkText;
            double chunkStart;
            double chunkEnd;
            int chunkIndex;

            lock (_chunkLock)
            {
                if (_chunkBuilder.Length == 0) return;
                chunkText = _chunkBuilder.ToString();
                chunkStart = double.IsNaN(_chunkStartSec) ? 0 : _chunkStartSec;
                chunkEnd = _chunkEndSec;
                chunkIndex = _segmentIndexInSession++;
                _chunkBuilder.Clear();
                _chunkStartSec = double.NaN;
                _chunkEndSec = double.NaN;
            }

            await EmbedTranscriptChunkAsync(chunkText, chunkStart, chunkEnd, chunkIndex).ConfigureAwait(false);
        }

        private async Task EmbedTranscriptChunkAsync(string chunkText, double chunkStartSec, double chunkEndSec, int chunkIndex)
        {
            try
            {
                EnsureSegmentIndexInitialized();
                if (_segmentStore == null || _segmentEmbeddingClient == null) return;

                float[] segEmbedding = null;
                try { segEmbedding = await _segmentEmbeddingClient.GetEmbeddingAsync(chunkText, CancellationToken.None).ConfigureAwait(false); }
                catch { }

                if (segEmbedding == null || segEmbedding.Length == 0)
                    return;

                var sessionId = GetSessionIdFromSessionFile(_currentSessionFile);
                var title = GetSessionTitleFromSessionFile(_currentSessionFile);
                var segId = $"{sessionId}:{chunkIndex:D6}";

                var seg = new TranscriptSegment
                {
                    Id = segId,
                    SessionId = sessionId,
                    Title = title,
                    SpeakerLabel = string.Empty,
                    SpeakerName = "Transcript",
                    Text = chunkText,
                    TStartSec = chunkStartSec,
                    TEndSec = chunkEndSec,
                    StartedAtUtc = _sessionStartTime.ToUniversalTime()
                };

                seg.SetEmbedding(segEmbedding);

                _segmentStore.UpsertSegment(seg);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _segmentStoreIoMutex.WaitAsync().ConfigureAwait(false);
                        try { await _segmentStore.SaveAsync().ConfigureAwait(false); }
                        finally { _segmentStoreIoMutex.Release(); }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private static string GetUniqueSessionFilePath(string folder, DateTime startTime)
        {
            var baseName = $"Meeting_{startTime:yyyy-MM-dd_HH-mm-ss}.txt";
            var path = Path.Combine(folder, baseName);

            int counter = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(folder, $"Meeting_{startTime:yyyy-MM-dd_HH-mm-ss}_{counter:D2}.txt");
                counter++;
            }

            return path;
        }
        private void ResetChunkBuilder()
        {
            lock (_chunkLock)
            {
                _chunkBuilder.Clear();
                _chunkStartSec = double.NaN;
                _chunkEndSec = double.NaN;
            }
        }

        public void Dispose()
        {
            StopSummaryTimer();
            _ = StopSessionAsync(false);
            
            // Unsubscribe from events
            try { SpeakerEmbedder.OnEmbedding -= OnVoiceEmbedding; } catch { }
            try { _segmentEmbeddingClient?.Dispose(); } catch { }
        }

        private class TranscriptionEntry
        {
            public DateTime Timestamp { get; set; }
            public string Speaker { get; set; }
            public string Text { get; set; }
        }

        public (int tokens, int limit) GetCurrentChunkTokenUsage()
        {
            int limit = GetTranscriptChunkTokenLimit();
            string chunkText;
            lock (_chunkLock)
            {
                chunkText = _chunkBuilder.ToString();
            }
            int tokens = TokenBudget.EstimateTokens(chunkText);
            return (tokens, limit);
        }

        /// <summary>
        /// Force embedding of the current in-memory transcript chunk (even if below token limit).
        /// Safe to call when no session is active.
        /// </summary>
        public Task ForceEmbedCurrentChunkAsync()
        {
            return FlushTranscriptChunkAsync(force: true);
        }
    }
}
