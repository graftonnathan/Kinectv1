using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Kinect;
using Microsoft.Kinect.Face;
using System.Collections.Generic;
using System.Linq;
using Kinectv1;

public static class EnhancedKinectFaceTracker
{
    // kinect sensor instance
    static KinectSensor _sensor;
    
    // embedder for ArcFace model
    static ArcFaceEmbedder _embedder;

    // Color stream fields
    static ColorFrameReader _colorReader;
    static FrameDescription _colorDesc;
    static byte[] _colorPixels; // BGRA
    static readonly object _colorLock = new object();

    // Body tracking fields
    static BodyFrameReader _bodyReader;
    static Body[] _bodies;

    // Face tracking using Kinect Face x64 NuGet package
    static FaceFrameSource[] _faceFrameSources;
    static FaceFrameReader[] _faceFrameReaders;
    static FaceAlignment[] _faceAlignments;
    static FaceModel[] _faceModels;
    const int BODY_COUNT = 6;
    static bool _useFaceSDK = true;
    static bool _faceSDKInitialized = false;

    // Enhanced face tracking data - track ALL detected faces
    static Dictionary<ulong, DetectedFace> _activeFaces = new Dictionary<ulong, DetectedFace>();
    static Dictionary<ulong, (string name, float confidence, DateTime recognizedAt)> _recognitionResults = new Dictionary<ulong, (string, float, DateTime)>();
    static Dictionary<ulong, EmotionState> _emotionResults = new Dictionary<ulong, EmotionState>(); // Track emotions per tracking ID
    static readonly object _faceDataLock = new object();

    // Throttle video overlay updates to prevent ghosting
    static DateTime _lastOverlayUpdate = DateTime.MinValue;
    static readonly TimeSpan OverlayUpdateInterval = TimeSpan.FromMilliseconds(100); // 10 FPS for overlays

    // Emotion detection settings
    static bool _emotionDetectionEnabled = true;
    static DateTime _lastEmotionUpdate = DateTime.MinValue;
    static readonly TimeSpan EmotionUpdateInterval = TimeSpan.FromMilliseconds(500); // 2 FPS for emotion updates

    static volatile bool _initialized;
    static readonly object _initLock = new object();
    static DateTime _recognitionCooldownUntil = DateTime.MinValue;

    // labeling the NEXT detected face with a name
    static string _pendingLabelName = null;
    
    // auto recognize faces after this interval
    static bool _recognitionEnabled = true;
    static System.Diagnostics.Stopwatch _recogSw = System.Diagnostics.Stopwatch.StartNew();
    static int _recogIntervalMs = 2000; // Slower for better tracking

    // Video window integration
    static VideoWindow _videoWindow;
    static bool _videoWindowEnabled = false;
    static DateTime _lastVideoUpdate = DateTime.MinValue;
    static readonly TimeSpan VideoUpdateInterval = TimeSpan.FromMilliseconds(33); // ~30 FPS

    // System status tracking
    static bool _kinectAvailable = false;
    static bool _systemReady = false;

    // Events for video feed - Enhanced with tracking ID
    public static event Action<BitmapSource> OnVideoFrame;
    public static event Action<List<FaceTrackingInfo>> OnAllFacesDetected; // All detected faces with tracking info

    // Structure to hold detected face information
    public struct DetectedFace
    {
        public ulong TrackingId;
        public RectI BoundingBox;
        public DateTime LastSeen;
        public bool IsTracked;
        public int ReaderIndex; // Which face reader this came from
        
        public DetectedFace(ulong trackingId, RectI boundingBox, bool isTracked, int readerIndex)
        {
            TrackingId = trackingId;
            BoundingBox = boundingBox;
            LastSeen = DateTime.UtcNow;
            IsTracked = isTracked;
            ReaderIndex = readerIndex;
        }
    }

    // Information about a tracked face for the UI
    public struct FaceTrackingInfo
    {
        public ulong TrackingId;
        public int Left, Top, Width, Height;
        public string Name; // "Unknown" if not recognized
        public float Confidence; // 0.0 if not recognized
        public bool IsRecognized;
        public DateTime LastUpdated;
        public EmotionState Emotion; // New emotion data

        public FaceTrackingInfo(ulong trackingId, RectI boundingBox, string name = "Unknown", float confidence = 0.0f, EmotionState emotion = new EmotionState())
        {
            TrackingId = trackingId;
            Left = boundingBox.Left;
            Top = boundingBox.Top;
            Width = boundingBox.Right - boundingBox.Left;
            Height = boundingBox.Bottom - boundingBox.Top;
            Name = name;
            Confidence = confidence;
            IsRecognized = !string.IsNullOrEmpty(name) && name != "Unknown";
            LastUpdated = DateTime.UtcNow;
            Emotion = emotion;
        }
    }

    // Structure to hold emotion detection data
    public struct EmotionState
    {
        public string PrimaryEmotion;
        public float Confidence;
        public bool IsHappy;
        public bool IsEngaged;
        public bool IsLookingAway;
        public bool LeftEyeClosed;
        public bool RightEyeClosed;
        public bool MouthOpen;
        public bool MouthMoved;
        public DateTime LastDetected;

        public EmotionState(FaceFrameResult faceResult)
        {
            // Initialize all fields first
            PrimaryEmotion = "Neutral";
            Confidence = 0.0f;
            IsHappy = false;
            IsEngaged = false;
            IsLookingAway = false;
            LeftEyeClosed = false;
            RightEyeClosed = false;
            MouthOpen = false;
            MouthMoved = false;
            LastDetected = DateTime.UtcNow;
            
            if (faceResult != null)
            {
                var faceProperties = faceResult.FaceProperties;
                
                IsHappy = faceProperties.ContainsKey(FaceProperty.Happy) && 
                         faceProperties[FaceProperty.Happy] == DetectionResult.Yes;
                
                IsEngaged = faceProperties.ContainsKey(FaceProperty.Engaged) && 
                           faceProperties[FaceProperty.Engaged] == DetectionResult.Yes;
                
                IsLookingAway = faceProperties.ContainsKey(FaceProperty.LookingAway) && 
                               faceProperties[FaceProperty.LookingAway] == DetectionResult.Yes;
                
                LeftEyeClosed = faceProperties.ContainsKey(FaceProperty.LeftEyeClosed) && 
                               faceProperties[FaceProperty.LeftEyeClosed] == DetectionResult.Yes;
                
                RightEyeClosed = faceProperties.ContainsKey(FaceProperty.RightEyeClosed) && 
                                faceProperties[FaceProperty.RightEyeClosed] == DetectionResult.Yes;
                
                MouthOpen = faceProperties.ContainsKey(FaceProperty.MouthOpen) && 
                           faceProperties[FaceProperty.MouthOpen] == DetectionResult.Yes;
                
                MouthMoved = faceProperties.ContainsKey(FaceProperty.MouthMoved) && 
                            faceProperties[FaceProperty.MouthMoved] == DetectionResult.Yes;

                // Determine primary emotion based on facial features
                PrimaryEmotion = DeterminePrimaryEmotion();
                Confidence = CalculateEmotionConfidence();
            }
        }

        private string DeterminePrimaryEmotion()
        {
            // Prioritized emotion detection logic
            if (IsHappy && IsEngaged)
                return "Very Happy";
            else if (IsHappy)
                return "Happy";
            else if (LeftEyeClosed && RightEyeClosed)
                return "Eyes Closed";
            else if (LeftEyeClosed || RightEyeClosed)
                return "Winking";
            else if (MouthOpen && !MouthMoved)
                return "Surprised";
            else if (MouthOpen && MouthMoved)
                return "Speaking";
            else if (IsLookingAway)
                return "Distracted";
            else if (!IsEngaged)
                return "Disengaged";
            else if (MouthMoved)
                return "Talking";
            else
                return "Neutral";
        }

        private float CalculateEmotionConfidence()
        {
            // Calculate confidence based on number of detected features
            int detectedFeatures = 0;
            int totalFeatures = 7;

            if (IsHappy) detectedFeatures++;
            if (IsEngaged) detectedFeatures++;
            if (IsLookingAway) detectedFeatures++;
            if (LeftEyeClosed) detectedFeatures++;
            if (RightEyeClosed) detectedFeatures++;
            if (MouthOpen) detectedFeatures++;
            if (MouthMoved) detectedFeatures++;

            return (float)detectedFeatures / totalFeatures;
        }

        public override string ToString()
        {
            return $"{PrimaryEmotion} ({Confidence:F2})";
        }

        public string GetDetailedDescription()
        {
            var features = new List<string>();
            
            if (IsHappy) features.Add("Happy");
            if (IsEngaged) features.Add("Engaged");
            if (IsLookingAway) features.Add("Looking Away");
            if (LeftEyeClosed) features.Add("Left Eye Closed");
            if (RightEyeClosed) features.Add("Right Eye Closed");
            if (MouthOpen) features.Add("Mouth Open");
            if (MouthMoved) features.Add("Mouth Moving");

            return features.Count > 0 ? string.Join(", ", features) : "No specific features detected";
        }
    }

    public static void Start()
    {
        try
        {
            Console.WriteLine("Initializing Enhanced KinectFaceTracker with full face tracking...");
            
            MemoryStore.Init();

            _sensor = KinectSensor.GetDefault();
            if (_sensor == null)
            {
                Console.WriteLine("Kinect runtime not found or no default sensor.");
                _kinectAvailable = false;
                _systemReady = true;
                return;
            }

            Console.WriteLine("Kinect sensor found, opening...");
            _sensor.IsAvailableChanged += Sensor_IsAvailableChanged;
            _sensor.Open();

            // Start initialization in background
            Task.Run(async () =>
            {
                var waited = 0;
                const int maxWaitTime = 8000;
                const int checkInterval = 200;
                
                while (!_initialized && waited < maxWaitTime)
                {
                    if (_sensor != null && _sensor.IsAvailable)
                    {
                        Console.WriteLine($"Kinect became available after {waited}ms");
                        await TryInitializeReadersIfAvailable();
                        return;
                    }
                    
                    await Task.Delay(checkInterval);
                    waited += checkInterval;
                }

                if (!_initialized)
                {
                    Console.WriteLine("Kinect initialization timeout");
                    _kinectAvailable = false;
                    _systemReady = true;
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Enhanced KinectFaceTracker.Start failed: {ex.Message}");
            _kinectAvailable = false;
            _systemReady = true;
        }
    }

    private static void Sensor_IsAvailableChanged(object sender, IsAvailableChangedEventArgs e)
    {
        Console.WriteLine($"Kinect availability changed: {e.IsAvailable}");
        _kinectAvailable = e.IsAvailable;
        
        if (e.IsAvailable) 
            Task.Run(() => TryInitializeReadersIfAvailable());
        else 
            TeardownReaders();
    }

    private static async Task TryInitializeReadersIfAvailable()
    {
        if (_initialized || !_sensor.IsAvailable) 
        {
            return;
        }

        lock (_initLock)
        {
            if (_initialized || !_sensor.IsAvailable) return;

            try
            {
                Console.WriteLine("Initializing enhanced Kinect streams...");
                
                // Initialize color reader
                _colorReader = _sensor.ColorFrameSource.OpenReader();
                _colorDesc = _sensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);
                _colorPixels = new byte[_colorDesc.Width * _colorDesc.Height * 4];
                _colorReader.FrameArrived += ColorReader_FrameArrived;

                // Initialize body reader
                _bodyReader = _sensor.BodyFrameSource.OpenReader();
                _bodyReader.FrameArrived += BodyReader_FrameArrived;
                _bodies = new Body[_sensor.BodyFrameSource.BodyCount];

                // Initialize Face SDK
                InitializeFaceSDK();

                _initialized = true;
                _kinectAvailable = true;
                
                Console.WriteLine("Enhanced Kinect initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Enhanced KinectFaceTracker initialization failed: {ex.Message}");
                TeardownReaders();
                _kinectAvailable = false;
                return;
            }
        }

        // Load ArcFace model in background
        if (_embedder == null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var modelPath = Path.Combine(baseDir, "models", "arcface.onnx");
                    
                    if (File.Exists(modelPath))
                    {
                        Console.WriteLine("Loading ArcFace model for enhanced tracking...");
                        _embedder = new ArcFaceEmbedder(modelPath, cudaDeviceId: 0);
                        Console.WriteLine("ArcFace model loaded - enhanced face recognition ready");
                        _systemReady = true;
                    }
                    else
                    {
                        Console.WriteLine($"ArcFace model not found: {modelPath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ArcFace model loading failed: {ex.Message}");
                }
            });
        }
    }

    private static void InitializeFaceSDK()
    {
        try
        {
            Console.WriteLine("Initializing enhanced Kinect Face SDK...");
            
            _faceFrameSources = new FaceFrameSource[BODY_COUNT];
            _faceFrameReaders = new FaceFrameReader[BODY_COUNT];
            _faceAlignments = new FaceAlignment[BODY_COUNT];
            _faceModels = new FaceModel[BODY_COUNT];

            var faceFrameFeatures = FaceFrameFeatures.BoundingBoxInColorSpace
                                   | FaceFrameFeatures.PointsInColorSpace
                                   | FaceFrameFeatures.RotationOrientation
                                   | FaceFrameFeatures.FaceEngagement
                                   | FaceFrameFeatures.Happy
                                   | FaceFrameFeatures.LeftEyeClosed
                                   | FaceFrameFeatures.RightEyeClosed
                                   | FaceFrameFeatures.LookingAway
                                   | FaceFrameFeatures.MouthMoved
                                   | FaceFrameFeatures.MouthOpen;

            for (int i = 0; i < BODY_COUNT; i++)
            {
                _faceFrameSources[i] = new FaceFrameSource(_sensor, 0, faceFrameFeatures);
                _faceFrameReaders[i] = _faceFrameSources[i].OpenReader();
                
                if (_faceFrameReaders[i] != null)
                {
                    int readerIndex = i; // Capture for closure
                    _faceFrameReaders[i].FrameArrived += (sender, e) => FaceReader_FrameArrived(sender, e, readerIndex);
                }

                _faceAlignments[i] = new FaceAlignment();
                _faceModels[i] = new FaceModel();
            }

            _faceSDKInitialized = true;
            _useFaceSDK = true;
            Console.WriteLine("Enhanced Kinect Face SDK initialized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Enhanced Kinect Face SDK initialization failed: {ex.Message}");
            _faceSDKInitialized = false;
            _useFaceSDK = false;
        }
    }

    // Enhanced face frame processing with tracking ID support
    private static void FaceReader_FrameArrived(object sender, FaceFrameArrivedEventArgs e, int readerIndex)
    {
        using (var faceFrame = e.FrameReference.AcquireFrame())
        {
            if (faceFrame == null) return;

            var faceResult = faceFrame.FaceFrameResult;
            if (faceResult == null) return;

            try
            {
                var trackingId = faceFrame.TrackingId;
                var faceBoundingBox = faceResult.FaceBoundingBoxInColorSpace;
                
                if (IsValidFaceBoundingBox(faceBoundingBox))
                {
                    ProcessEnhancedFaceDetection(trackingId, faceBoundingBox, faceResult, readerIndex);
                }
                else
                {
                    // Remove invalid faces from tracking
                    lock (_faceDataLock)
                    {
                        _activeFaces.Remove(trackingId);
                        _recognitionResults.Remove(trackingId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Enhanced face frame processing error: {ex.Message}");
            }
        }
    }

    private static bool IsValidFaceBoundingBox(RectI boundingBox)
    {
        if (_colorDesc == null) return false;
        
        if (boundingBox.Left < 0 || boundingBox.Top < 0 ||
            boundingBox.Right >= _colorDesc.Width || boundingBox.Bottom >= _colorDesc.Height)
            return false;

        int width = boundingBox.Right - boundingBox.Left;
        int height = boundingBox.Bottom - boundingBox.Top;
        
        return width >= 50 && height >= 50;
    }

    // Enhanced face detection processing that tracks ALL faces
    private static void ProcessEnhancedFaceDetection(ulong trackingId, RectI faceBoundingBox, FaceFrameResult faceResult, int readerIndex)
    {
        try
        {
            // Update the detected face in our tracking system
            lock (_faceDataLock)
            {
                _activeFaces[trackingId] = new DetectedFace(trackingId, faceBoundingBox, true, readerIndex);
                
                // Clean up old faces (not seen for more than 3 seconds)
                var cutoff = DateTime.UtcNow.AddSeconds(-3);
                var staleTrackingIds = _activeFaces.Where(kvp => kvp.Value.LastSeen < cutoff).Select(kvp => kvp.Key).ToList();
                foreach (var staleId in staleTrackingIds)
                {
                    _activeFaces.Remove(staleId);
                    _recognitionResults.Remove(staleId);
                    _emotionResults.Remove(staleId); // Clean up emotion data too
                    Console.WriteLine($"Removed stale face TrackingID {staleId}");
                }
            }

            // Process emotion detection
            if (_emotionDetectionEnabled && faceResult != null)
            {
                var emotionState = new EmotionState(faceResult);
                
                lock (_faceDataLock)
                {
                    var previousEmotion = _emotionResults.ContainsKey(trackingId) ? _emotionResults[trackingId] : new EmotionState();
                    _emotionResults[trackingId] = emotionState;
                    
                    // Log emotion changes
                    if (previousEmotion.PrimaryEmotion != emotionState.PrimaryEmotion || 
                        Math.Abs(previousEmotion.Confidence - emotionState.Confidence) > 0.2f)
                    {
                        Console.WriteLine($"TrackingID {trackingId} Emotion: {emotionState.PrimaryEmotion} ({emotionState.Confidence:F2}) - {emotionState.GetDetailedDescription()}");
                    }
                }
            }

            // Handle enrollment
            if (!string.IsNullOrWhiteSpace(_pendingLabelName))
            {
                try
                {
                    var savedPath = SaveFaceSnapshot(_pendingLabelName, faceBoundingBox);
                    if (!string.IsNullOrEmpty(savedPath))
                    {
                        Console.WriteLine($"Saved face for '{_pendingLabelName}': {savedPath} (TrackingID: {trackingId})");
                        
                        // Mark this tracking ID as the enrolled person immediately
                        lock (_faceDataLock)
                        {
                            _recognitionResults[trackingId] = (_pendingLabelName, 1.0f, DateTime.UtcNow);
                            
                            // Update identity fusion tracker with enrollment
                            IdentityFusionTracker.UpdateFace(trackingId, _pendingLabelName, 1.0f);
                            
                            Console.WriteLine($"TrackingID {trackingId} immediately identified as '{_pendingLabelName}' after enrollment");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error saving face snapshot: {ex.Message}");
                }
                finally
                {
                    _pendingLabelName = null;
                }

                _recognitionCooldownUntil = DateTime.UtcNow.AddSeconds(2);
                UpdateVideoFeedWithAllFaces();
                return;
            }

            // Handle recognition with improved persistence logic
            bool shouldRecognize = false;
            bool hasExistingRecognition = false;
            string existingName = "Unknown";
            float existingConfidence = 0.0f;
            
            lock (_faceDataLock)
            {
                if (_recognitionResults.ContainsKey(trackingId))
                {
                    var existing = _recognitionResults[trackingId];
                    hasExistingRecognition = true;
                    existingName = existing.name;
                    existingConfidence = existing.confidence;
                    
                    // Only re-recognize if:
                    // 1. Current recognition is "Unknown" (try again)
                    // 2. Confidence is very low (< 0.3) and it's been at least 3 seconds
                    // 3. It's been more than 30 seconds since last recognition (periodic refresh)
                    var timeSinceRecognition = DateTime.UtcNow - existing.recognizedAt;
                    
                    if (existing.name == "Unknown")
                    {
                        // Always retry unknown faces every 5 seconds
                        shouldRecognize = timeSinceRecognition > TimeSpan.FromSeconds(5);
                    }
                    else if (existing.confidence < 0.3f)
                    {
                        // Re-check low confidence faces every 10 seconds
                        shouldRecognize = timeSinceRecognition > TimeSpan.FromSeconds(10);
                    }
                    else if (existing.confidence < 0.5f)
                    {
                        // Re-check medium confidence faces every 20 seconds
                        shouldRecognize = timeSinceRecognition > TimeSpan.FromSeconds(20);
                    }
                    else
                    {
                        // High confidence faces: only refresh every 60 seconds
                        shouldRecognize = timeSinceRecognition > TimeSpan.FromSeconds(60);
                    }
                }
                else
                {
                    // New face - always recognize
                    shouldRecognize = true;
                }
            }

            if (shouldRecognize && _recognitionEnabled && _embedder != null && DateTime.UtcNow >= _recognitionCooldownUntil)
            {
                var maybeKnown = TryRecognize(faceBoundingBox);
                
                lock (_faceDataLock)
                {
                    if (maybeKnown != null)
                    {
                        // Only update if new recognition is better than existing or if it's a new face
                        bool shouldUpdate = true;
                        
                        if (hasExistingRecognition && existingName != "Unknown")
                        {
                            // If we already have a good recognition, only update if new one is significantly better
                            if (existingConfidence >= 0.5f && maybeKnown.Value.score < existingConfidence + 0.1f)
                            {
                                shouldUpdate = false;
                                Console.WriteLine($"TrackingID {trackingId}: Keeping existing identification '{existingName}' ({existingConfidence:F3}) over new '{maybeKnown.Value.name}' ({maybeKnown.Value.score:F3})");
                            }
                        }
                        
                        if (shouldUpdate)
                        {
                            _recognitionResults[trackingId] = (maybeKnown.Value.name, maybeKnown.Value.score, DateTime.UtcNow);
                            
                            // Update identity fusion tracker with face recognition
                            IdentityFusionTracker.UpdateFace(trackingId, maybeKnown.Value.name, maybeKnown.Value.score);
                            
                            // Include emotion in the identification log
                            string emotionInfo = "";
                            if (_emotionResults.ContainsKey(trackingId))
                            {
                                var emotion = _emotionResults[trackingId];
                                emotionInfo = $" | Emotion: {emotion.PrimaryEmotion}";
                            }
                            
                            Console.WriteLine($"TrackingID {trackingId}: {(hasExistingRecognition ? "Updated" : "Identified")} as '{maybeKnown.Value.name}' (confidence={maybeKnown.Value.score:F3}){emotionInfo}");
                        }
                    }
                    else
                    {
                        // Only mark as unknown if we don't already have a good identification
                        if (!hasExistingRecognition || (existingName == "Unknown") || existingConfidence < 0.3f)
                        {
                            _recognitionResults[trackingId] = ("Unknown", 0.0f, DateTime.UtcNow);
                            Console.WriteLine($"TrackingID {trackingId}: No recognition match found");
                        }
                        else
                        {
                            Console.WriteLine($"TrackingID {trackingId}: Keeping existing identification '{existingName}' despite failed recognition attempt");
                        }
                    }
                }
            }

            // Always update the video feed with all detected faces (throttled to prevent ghosting)
            var now = DateTime.UtcNow;
            if (now - _lastOverlayUpdate >= OverlayUpdateInterval)
            {
                _lastOverlayUpdate = now;
                UpdateVideoFeedWithAllFaces();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Enhanced face processing failed: {ex.Message}");
        }
    }

    // Update video feed showing ALL detected faces with their tracking information
    private static void UpdateVideoFeedWithAllFaces()
    {
        if (!_videoWindowEnabled) return;

        List<FaceTrackingInfo> allFaces = new List<FaceTrackingInfo>();
        List<FaceDetection> faceDetections = new List<FaceDetection>();

        lock (_faceDataLock)
        {
            foreach (var face in _activeFaces.Values)
            {
                string name = "Unknown";
                float confidence = 0.0f;
                EmotionState emotion = new EmotionState();

                // Get fused identity instead of raw recognition result
                var fusedIdentity = IdentityFusionTracker.GetFusedIdentity(face.TrackingId);
                name = fusedIdentity.name;
                confidence = fusedIdentity.score;

                if (_emotionResults.ContainsKey(face.TrackingId))
                {
                    emotion = _emotionResults[face.TrackingId];
                }

                var faceInfo = new FaceTrackingInfo(face.TrackingId, face.BoundingBox, name, confidence, emotion);
                allFaces.Add(faceInfo);

                // Create FaceDetection for video window - include emotion and fusion indicator in name display
                var width = face.BoundingBox.Right - face.BoundingBox.Left;
                var height = face.BoundingBox.Bottom - face.BoundingBox.Top;
                
                string displayName = name;
                if (emotion.PrimaryEmotion != "Neutral")
                {
                    displayName += $" ({emotion.PrimaryEmotion})";
                }
                
                // Add fusion indicator if it's a fused result with decent confidence
                if (confidence > 0.0f && name != "Unknown")
                {
                    displayName += " [F]"; // [F] indicates fused result
                }
                
                faceDetections.Add(new FaceDetection
                {
                    Left = face.BoundingBox.Left,
                    Top = face.BoundingBox.Top,
                    Width = width,
                    Height = height,
                    Name = displayName,
                    Confidence = confidence,
                    Timestamp = DateTime.Now
                });
            }
        }

        // Update video window with all current faces at once (prevents ghosting)
        _videoWindow?.UpdateAllFaceDetections(faceDetections);

        // Fire event with all face tracking information (now includes emotion)
        OnAllFacesDetected?.Invoke(allFaces);
    }

    private static (string name, float score)? TryRecognize(RectI boundingBox)
    {
        try
        {
            var crop = CropCurrentColor(boundingBox, inflate: 15, minSize: 80);
            if (crop == null) return null;

            var chw = ImagePreprocess.ToArcFaceCHW(crop);
            var emb = _embedder.Embed(chw);

            var match = MemoryStore.MatchBest(emb, thresholdCos: 0.45f); 
            return match;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Enhanced recognition failed: {ex.Message}");
            return null;
        }
    }
    
    private static BitmapSource CropCurrentColor(RectI boundingBox, int inflate, int minSize)
    {
        if (_colorDesc == null || _colorPixels == null) return null;

        byte[] pixelsCopy;
        lock (_colorLock)
        {
            if (_colorPixels == null) return null;
            pixelsCopy = new byte[_colorPixels.Length];
            Buffer.BlockCopy(_colorPixels, 0, pixelsCopy, 0, _colorPixels.Length);
        }

        int width = _colorDesc.Width;
        int height = _colorDesc.Height;
        int stride = width * 4;

        int x = Math.Max(0, boundingBox.Left - inflate);
        int y = Math.Max(0, boundingBox.Top - inflate);
        int w = Math.Min(width - x, Math.Max(minSize, (boundingBox.Right - boundingBox.Left) + 2 * inflate));
        int h = Math.Min(height - y, Math.Max(minSize, (boundingBox.Bottom - boundingBox.Top) + 2 * inflate));
        if (w <= 0 || h <= 0) return null;

        var full = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixelsCopy, stride);
        var cropRect = new Int32Rect(x, y, w, h);
        var cropped = new CroppedBitmap(full, cropRect);
        cropped.Freeze();
        return cropped;
    }

    private static string SaveFaceSnapshot(string name, RectI boundingBox)
    {
        if (_embedder == null) return null;

        try
        {
            if (_colorDesc == null || _colorPixels == null) return null;

            byte[] pixelsCopy;
            lock (_colorLock)
            {
                if (_colorPixels == null) return null;
                pixelsCopy = new byte[_colorPixels.Length];
                Buffer.BlockCopy(_colorPixels, 0, pixelsCopy, 0, _colorPixels.Length);
            }

            int width = _colorDesc.Width;
            int height = _colorDesc.Height;
            int stride = width * 4;

            int margin = 15;
            int x = Math.Max(0, boundingBox.Left - margin);
            int y = Math.Max(0, boundingBox.Top - margin);
            int w = Math.Min(width - x, Math.Max(1, (boundingBox.Right - boundingBox.Left) + 2 * margin));
            int h = Math.Min(height - y, Math.Max(1, (boundingBox.Bottom - boundingBox.Top) + 2 * margin));

            if (w < 60 || h < 60) return null;

            var full = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixelsCopy, stride);
            var cropRect = new Int32Rect(x, y, w, h);
            var cropped = new CroppedBitmap(full, cropRect);
            cropped.Freeze();

            var chw = ImagePreprocess.ToArcFaceCHW(cropped);
            var emb = _embedder.Embed(chw);
            MemoryStore.AddEmbedding(name, emb);

            var personFolder = MemoryStore.EnsurePersonFolder(name);
            var filename = $"{DateTime.Now:yyyyMMdd_HHmmssfff}.png";
            var absolutePath = Path.Combine(personFolder, filename);
            var relativePath = Path.Combine("Faces", Path.GetFileName(personFolder), filename);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using (var fs = new FileStream(absolutePath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(fs);
            }

            MemoryStore.AddSnapshot(name, relativePath);
            return relativePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Enhanced SaveFaceSnapshot failed: {ex.Message}");
            return null;
        }
    }

    private static void ColorReader_FrameArrived(object sender, ColorFrameArrivedEventArgs e)
    {
        using (var frame = e.FrameReference.AcquireFrame())
        {
            if (frame == null) return;

            lock (_colorLock)
            {
                frame.CopyConvertedFrameDataToArray(_colorPixels, ColorImageFormat.Bgra);
            }

            var now = DateTime.UtcNow;
            if (now - _lastVideoUpdate >= VideoUpdateInterval)
            {
                _lastVideoUpdate = now;
                UpdateVideoDisplay();
            }
        }
    }

    private static void UpdateVideoDisplay()
    {
        if (!_videoWindowEnabled || _colorPixels == null || _colorDesc == null) return;

        try
        {
            byte[] pixelsCopy;
            lock (_colorLock)
            {
                pixelsCopy = new byte[_colorPixels.Length];
                Buffer.BlockCopy(_colorPixels, 0, pixelsCopy, 0, _colorPixels.Length);
            }

            var bitmap = BitmapSource.Create(
                _colorDesc.Width, 
                _colorDesc.Height, 
                96, 96, 
                PixelFormats.Bgra32, 
                null, 
                pixelsCopy, 
                _colorDesc.Width * 4);

            bitmap.Freeze();
            _videoWindow?.UpdateVideoFrame(bitmap);
            OnVideoFrame?.Invoke(bitmap);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Enhanced video display error: {ex.Message}");
        }
    }

    private static void BodyReader_FrameArrived(object sender, BodyFrameArrivedEventArgs e)
    {
        using (var bodyFrame = e.FrameReference.AcquireFrame())
        {
            if (bodyFrame == null) return;

            bodyFrame.GetAndRefreshBodyData(_bodies);

            if (_useFaceSDK && _faceSDKInitialized)
            {
                for (int i = 0; i < BODY_COUNT && i < _bodies.Length; i++)
                {
                    if (_faceFrameSources[i] != null)
                    {
                        if (_bodies[i].IsTracked)
                        {
                            _faceFrameSources[i].TrackingId = _bodies[i].TrackingId;
                        }
                        else
                        {
                            _faceFrameSources[i].TrackingId = 0;
                        }
                    }
                }
            }
        }
    }

    private static void TeardownReaders()
    {
        try
        {
            if (_colorReader != null)
            {
                _colorReader.FrameArrived -= ColorReader_FrameArrived;
                _colorReader.Dispose();
                _colorReader = null;
            }

            if (_bodyReader != null)
            {
                _bodyReader.FrameArrived -= BodyReader_FrameArrived;
                _bodyReader.Dispose();
                _bodyReader = null;
            }

            if (_faceSDKInitialized)
            {
                for (int i = 0; i < BODY_COUNT; i++)
                {
                    if (_faceFrameReaders[i] != null)
                    {
                        _faceFrameReaders[i].Dispose();
                        _faceFrameReaders[i] = null;
                    }
                    
                    if (_faceFrameSources[i] != null)
                    {
                        _faceFrameSources[i].Dispose();
                        _faceFrameSources[i] = null;
                    }
                    
                    if (_faceModels[i] != null)
                    {
                        _faceModels[i].Dispose();
                        _faceModels[i] = null;
                    }
                    
                    _faceAlignments[i] = null;
                }
                _faceSDKInitialized = false;
            }

            // Log removed faces before clearing
            lock (_faceDataLock)
            {
                if (_activeFaces.Count > 0)
                {
                    Console.WriteLine($"Clearing {_activeFaces.Count} tracked faces, {_recognitionResults.Count} recognition results, and {_emotionResults.Count} emotion states");
                    foreach (var face in _activeFaces.Values)
                    {
                        string faceInfo = $"TrackingID {face.TrackingId}";
                        
                        if (_recognitionResults.ContainsKey(face.TrackingId))
                        {
                            var recognition = _recognitionResults[face.TrackingId];
                            faceInfo += $": {recognition.name} ({recognition.confidence:F3})";
                        }
                        
                        if (_emotionResults.ContainsKey(face.TrackingId))
                        {
                            var emotion = _emotionResults[face.TrackingId];
                            faceInfo += $" | {emotion.PrimaryEmotion}";
                        }
                        
                        Console.WriteLine($"  Lost {faceInfo}");
                    }
                }
                
                _activeFaces.Clear();
                _recognitionResults.Clear();
                _emotionResults.Clear(); // Clear emotion data
            }

            _initialized = false;
            _kinectAvailable = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Enhanced KinectFaceTracker teardown failed: {ex.Message}");
        }
    }

    // Public API methods
    public static void ShowVideoWindow()
    {
        try
        {
            if (_videoWindow == null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _videoWindow = new VideoWindow();
                    _videoWindow.Show();
                    _videoWindowEnabled = true;
                    Console.WriteLine("Enhanced video window opened");
                });
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _videoWindow.Show();
                    _videoWindow.Activate();
                    _videoWindowEnabled = true;
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error showing enhanced video window: {ex.Message}");
        }
    }

    public static void HideVideoWindow()
    {
        try
        {
            _videoWindowEnabled = false;
            Application.Current.Dispatcher.Invoke(() =>
            {
                _videoWindow?.Hide();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error hiding enhanced video window: {ex.Message}");
        }
    }

    public static void QueueLabel(string name)
    {
        _pendingLabelName = name;
        Console.WriteLine($"Enhanced face enrollment queued: {name}");
    }

    public static bool IsVideoWindowOpen()
    {
        return _videoWindowEnabled && _videoWindow != null;
    }

    public static string GetSystemStatus()
    {
        if (!_kinectAvailable)
            return "Kinect not available";
        else if (!_initialized)
            return "Kinect initializing";
        else if (_embedder == null)
            return "Loading face recognition";
        else
            return "Enhanced system ready";
    }

    public static List<FaceTrackingInfo> GetCurrentTrackedFaces()
    {
        List<FaceTrackingInfo> faces = new List<FaceTrackingInfo>();
        
        lock (_faceDataLock)
        {
            foreach (var face in _activeFaces.Values)
            {
                string name = "Unknown";
                float confidence = 0.0f;
                EmotionState emotion = new EmotionState();

                if (_recognitionResults.ContainsKey(face.TrackingId))
                {
                    var recognition = _recognitionResults[face.TrackingId];
                    name = recognition.name;
                    confidence = recognition.confidence;
                }

                if (_emotionResults.ContainsKey(face.TrackingId))
                {
                    emotion = _emotionResults[face.TrackingId];
                }

                faces.Add(new FaceTrackingInfo(face.TrackingId, face.BoundingBox, name, confidence, emotion));
            }
        }

        return faces;
    }

    // Get a summary of all current tracking information
    public static string GetTrackingSummary()
    {
        lock (_faceDataLock)
        {
            if (_activeFaces.Count == 0)
            {
                return "No faces currently being tracked";
            }
            
            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"Tracking {_activeFaces.Count} face(s) with emotion detection:");
            
            foreach (var face in _activeFaces.Values)
            {
                string name = "Unknown";
                float confidence = 0.0f;
                DateTime lastRecognition = DateTime.MinValue;
                EmotionState emotion = new EmotionState();
                
                if (_recognitionResults.ContainsKey(face.TrackingId))
                {
                    var recognition = _recognitionResults[face.TrackingId];
                    name = recognition.name;
                    confidence = recognition.confidence;
                    lastRecognition = recognition.recognizedAt;
                }

                if (_emotionResults.ContainsKey(face.TrackingId))
                {
                    emotion = _emotionResults[face.TrackingId];
                }
                
                var timeSinceLastSeen = DateTime.UtcNow - face.LastSeen;
                summary.AppendLine($"  ID {face.TrackingId}: {name} ({confidence:F2}) - {emotion} - Last seen {timeSinceLastSeen.TotalSeconds:F1}s ago");
                
                if (emotion.PrimaryEmotion != "Neutral")
                {
                    summary.AppendLine($"    Emotion details: {emotion.GetDetailedDescription()}");
                }
                
                if (lastRecognition != DateTime.MinValue)
                {
                    var timeSinceRecognition = DateTime.UtcNow - lastRecognition;
                    summary.AppendLine($"    Last recognized {timeSinceRecognition.TotalSeconds:F1}s ago");
                }
            }
            
            return summary.ToString();
        }
    }

    // Set/lock the identification for a specific tracking ID
    public static bool SetTrackingIdIdentification(ulong trackingId, string name, float confidence = 0.95f)
    {
        lock (_faceDataLock)
        {
            if (_activeFaces.ContainsKey(trackingId))
            {
                _recognitionResults[trackingId] = (name, confidence, DateTime.UtcNow);
                Console.WriteLine($"Manually set TrackingID {trackingId} to '{name}' with confidence {confidence:F3}");
                
                // Trigger video update
                var now = DateTime.UtcNow;
                if (now - _lastOverlayUpdate >= TimeSpan.FromMilliseconds(50)) // Allow immediate update for manual changes
                {
                    _lastOverlayUpdate = now;
                    UpdateVideoFeedWithAllFaces();
                }
                
                return true;
            }
            else
            {
                Console.WriteLine($"TrackingID {trackingId} not found in active faces");
                return false;
            }
        }
    }

    // Get detailed information about a specific tracking ID
    public static (bool found, string name, float confidence, DateTime lastSeen, DateTime lastRecognition, EmotionState emotion)? GetTrackingIdInfo(ulong trackingId)
    {
        lock (_faceDataLock)
        {
            if (_activeFaces.ContainsKey(trackingId))
            {
                var face = _activeFaces[trackingId];
                string name = "Unknown";
                float confidence = 0.0f;
                DateTime lastRecognition = DateTime.MinValue;
                EmotionState emotion = new EmotionState();
                
                if (_recognitionResults.ContainsKey(trackingId))
                {
                    var recognition = _recognitionResults[trackingId];
                    name = recognition.name;
                    confidence = recognition.confidence;
                    lastRecognition = recognition.recognizedAt;
                }

                if (_emotionResults.ContainsKey(trackingId))
                {
                    emotion = _emotionResults[trackingId];
                }
                
                return (true, name, confidence, face.LastSeen, lastRecognition, emotion);
            }
            
            return null;
        }
    }

    // Clear identification for a specific tracking ID (force re-recognition)
    public static bool ClearTrackingIdIdentification(ulong trackingId)
    {
        lock (_faceDataLock)
        {
            if (_activeFaces.ContainsKey(trackingId))
            {
                _recognitionResults.Remove(trackingId);
                Console.WriteLine($"Cleared identification for TrackingID {trackingId}");
                
                // Trigger video update
                var now = DateTime.UtcNow;
                if (now - _lastOverlayUpdate >= TimeSpan.FromMilliseconds(50))
                {
                    _lastOverlayUpdate = now;
                    UpdateVideoFeedWithAllFaces();
                }
                
                return true;
            }
            
            return false;
        }
    }

    // Emotion detection control methods
    public static void EnableEmotionDetection(bool enable)
    {
        _emotionDetectionEnabled = enable;
        Console.WriteLine($"Emotion detection {(enable ? "enabled" : "disabled")}");
    }

    public static bool IsEmotionDetectionEnabled()
    {
        return _emotionDetectionEnabled;
    }

    // Get emotion statistics for all currently tracked faces
    public static Dictionary<string, int> GetEmotionStatistics()
    {
        var emotionCounts = new Dictionary<string, int>();
        
        lock (_faceDataLock)
        {
            foreach (var emotionResult in _emotionResults.Values)
            {
                var emotion = emotionResult.PrimaryEmotion;
                if (emotionCounts.ContainsKey(emotion))
                    emotionCounts[emotion]++;
                else
                    emotionCounts[emotion] = 1;
            }
        }
        
        return emotionCounts;
    }

    // Get all current emotions with detailed info
    public static List<(ulong trackingId, string name, EmotionState emotion)> GetCurrentEmotions()
    {
        var emotions = new List<(ulong, string, EmotionState)>();
        
        lock (_faceDataLock)
        {
            foreach (var face in _activeFaces.Values)
            {
                string name = "Unknown";
                if (_recognitionResults.ContainsKey(face.TrackingId))
                {
                    name = _recognitionResults[face.TrackingId].name;
                }
                
                EmotionState emotion = new EmotionState();
                if (_emotionResults.ContainsKey(face.TrackingId))
                {
                    emotion = _emotionResults[face.TrackingId];
                }
                
                emotions.Add((face.TrackingId, name, emotion));
            }
        }
        
        return emotions;
    }

    /// <summary>
    /// Get the most likely speaker based on face detection and talking indicators
    /// Returns the name if there's a single person or clear talking indication
    /// </summary>
    public static (string speakerName, float confidence, string reasoning) GetLikelySpeaker()
    {
        lock (_faceDataLock)
        {
            var recognizedFaces = new List<(string name, float confidence, bool isTalking, DateTime lastSeen, ulong trackingId)>();
            
            // Gather all faces with recognition data
            foreach (var face in _activeFaces.Values)
            {
                if (_recognitionResults.ContainsKey(face.TrackingId))
                {
                    var recognition = _recognitionResults[face.TrackingId];
                    bool isTalking = false;
                    
                    // Check if this person appears to be talking based on emotions
                    if (_emotionResults.ContainsKey(face.TrackingId))
                    {
                        var emotion = _emotionResults[face.TrackingId];
                        isTalking = emotion.PrimaryEmotion == "Speaking" || 
                                   emotion.PrimaryEmotion == "Talking" ||
                                   emotion.MouthMoved || 
                                   emotion.MouthOpen;
                    }
                    
                    recognizedFaces.Add((recognition.name, recognition.confidence, isTalking, face.LastSeen, face.TrackingId));
                }
            }
            
            // No faces detected
            if (recognizedFaces.Count == 0)
            {
                return ("Unknown", 0.0f, "No faces detected");
            }
            
            // Filter out "Unknown" faces first, then work with recognized faces
            var knownFaces = recognizedFaces.Where(f => f.name != "Unknown" && f.confidence > 0.3f).ToList();
            
            // Case 1: Single known person with high confidence
            if (knownFaces.Count == 1)
            {
                var person = knownFaces[0];
                string talkingInfo = person.isTalking ? " (talking detected)" : "";
                return (person.name, person.confidence, $"Single recognized person: {person.name}{talkingInfo}");
            }
            
            // Case 2: Multiple known people - prefer the one who appears to be talking
            if (knownFaces.Count > 1)
            {
                var talkingPeople = knownFaces.Where(f => f.isTalking).ToList();
                
                if (talkingPeople.Count == 1)
                {
                    var speaker = talkingPeople[0];
                    return (speaker.name, speaker.confidence, $"Talking person among {knownFaces.Count} faces: {speaker.name}");
                }
                else if (talkingPeople.Count > 1)
                {
                    // Multiple talking people - choose highest confidence
                    var bestTalker = talkingPeople.OrderByDescending(f => f.confidence).First();
                    return (bestTalker.name, bestTalker.confidence, $"Best confidence among {talkingPeople.Count} talking people: {bestTalker.name}");
                }
                else
                {
                    // No clear talking indication - use highest confidence
                    var bestPerson = knownFaces.OrderByDescending(f => f.confidence).First();
                    return (bestPerson.name, bestPerson.confidence, $"Highest confidence among {knownFaces.Count} people: {bestPerson.name}");
                }
            }
            
            // Case 3: Only unknown faces - check if single person
            if (recognizedFaces.Count == 1)
            {
                var person = recognizedFaces[0];
                string talkingInfo = person.isTalking ? " (talking detected)" : "";
                return ("Unknown", 0.0f, $"Single unrecognized person{talkingInfo}");
            }
            
            // Case 4: Multiple unknown faces - prefer talking person
            var unknownTalking = recognizedFaces.Where(f => f.isTalking).ToList();
            if (unknownTalking.Count == 1)
            {
                return ("Unknown", 0.0f, $"Single talking person among {recognizedFaces.Count} unrecognized faces");
            }
            
            // Default: Multiple people, unclear who's speaking
            return ("Unknown", 0.0f, $"Multiple people detected ({recognizedFaces.Count} faces), unclear who's speaking");
        }
    }

    /// <summary>
    /// Get current face-based speaker detection status for debugging
    /// </summary>
    public static string GetSpeakerDetectionStatus()
    {
        lock (_faceDataLock)
        {
            if (_activeFaces.Count == 0)
            {
                return "No faces detected for speaker identification";
            }
            
            var status = new System.Text.StringBuilder();
            status.AppendLine($"Face-based Speaker Detection Status ({_activeFaces.Count} faces):");
            
            foreach (var face in _activeFaces.Values)
            {
                string name = "Unknown";
                float confidence = 0.0f;
                bool isTalking = false;
                string emotionInfo = "";
                
                if (_recognitionResults.ContainsKey(face.TrackingId))
                {
                    var recognition = _recognitionResults[face.TrackingId];
                    name = recognition.name;
                    confidence = recognition.confidence;
                }
                
                if (_emotionResults.ContainsKey(face.TrackingId))
                {
                    var emotion = _emotionResults[face.TrackingId];
                    isTalking = emotion.PrimaryEmotion == "Speaking" || 
                               emotion.PrimaryEmotion == "Talking" ||
                               emotion.MouthMoved || 
                               emotion.MouthOpen;
                    emotionInfo = $" | Emotion: {emotion.PrimaryEmotion}";
                    if (emotion.MouthMoved) emotionInfo += " (mouth moving)";
                    if (emotion.MouthOpen) emotionInfo += " (mouth open)";
                }
                
                var talkingIndicator = isTalking ? " ??? TALKING" : "";
                status.AppendLine($"  ID {face.TrackingId}: {name} ({confidence:F2}){emotionInfo}{talkingIndicator}");
            }
            
            var (speakerName, speakerConfidence, reasoning) = GetLikelySpeaker();
            status.AppendLine($"\n  ? Likely Speaker: {speakerName} ({speakerConfidence:F2})");
            status.AppendLine($"  ? Reasoning: {reasoning}");
            
            return status.ToString();
        }
    }

    public static void Stop()
    {
        try
        {
            TeardownReaders();
            _sensor?.Close();
            
            _videoWindowEnabled = false;
            Application.Current.Dispatcher.Invoke(() =>
            {
                _videoWindow?.Close();
                _videoWindow = null;
            });
            
            Console.WriteLine("Enhanced Kinect stopped");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Enhanced KinectFaceTracker.Stop failed: {ex.Message}");
        }
    }
}