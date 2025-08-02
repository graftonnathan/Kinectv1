using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Kinect;
using Microsoft.Kinect.Face;
using Kinectv1;

public static class KinectFaceTracker
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
    static bool _useFaceSDK = true; // Default to using Face SDK
    static bool _faceSDKInitialized = false;

    static volatile bool _initialized;
    static readonly object _initLock = new object();
    static DateTime _recognitionCooldownUntil = DateTime.MinValue;

    // labeling the NEXT detected face with a name
    static string _pendingLabelName = null;
    
    // auto recognize faces after this interval
    static bool _recognitionEnabled = true;
    static System.Diagnostics.Stopwatch _recogSw = System.Diagnostics.Stopwatch.StartNew();
    static int _recogIntervalMs = 1000;

    // Enhanced face detection using multiple body joints (fallback mode)
    static bool _enhancedFaceDetectionEnabled = true;

    // Video window integration
    static VideoWindow _videoWindow;
    static bool _videoWindowEnabled = false;
    static DateTime _lastVideoUpdate = DateTime.MinValue;
    static readonly TimeSpan VideoUpdateInterval = TimeSpan.FromMilliseconds(33); // ~30 FPS

    // System status tracking
    static bool _kinectAvailable = false;
    static bool _systemReady = false;

    // Events for video feed
    public static event Action<BitmapSource> OnVideoFrame;
    public static event Action<int, int, int, int, string, float> OnFaceDetected; // left, top, width, height, name, confidence

    public static void Start()
    {
        try
        {
            Console.WriteLine("🔄 KinectFaceTracker.Start() called");
            
            MemoryStore.Init(); // ensure folders + memory.json exist

            _sensor = KinectSensor.GetDefault();
            if (_sensor == null)
            {
                Console.WriteLine("⚠️ Kinect runtime not found or no default sensor.");
                _kinectAvailable = false;
                return;
            }

            Console.WriteLine("📡 Kinect sensor found, setting up event handlers...");
            _sensor.IsAvailableChanged += Sensor_IsAvailableChanged;

            Console.WriteLine("🔌 Attempting to open Kinect sensor...");
            try 
            { 
                _sensor.Open();
                Console.WriteLine("✅ Kinect sensor.Open() succeeded");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Failed to open Kinect sensor: {ex.Message}");
                _kinectAvailable = false;
                return;
            }

            Console.WriteLine("🔄 Kinect opening… warming up.");

            // Start initialization in background with more aggressive timeout
            Task.Run(async () =>
            {
                var waited = 0;
                const int maxWaitTime = 10000; // 10 seconds instead of 6
                const int checkInterval = 500; // Check every 500ms instead of 200ms
                
                Console.WriteLine($"⏱️ Waiting up to {maxWaitTime/1000}s for Kinect to become available...");
                
                while (!_initialized && waited < maxWaitTime)
                {
                    if (_sensor != null && _sensor.IsAvailable)
                    {
                        Console.WriteLine($"✅ Kinect became available after {waited}ms");
                        await TryInitializeReadersIfAvailable();
                        return;
                    }
                    
                    await Task.Delay(checkInterval);
                    waited += checkInterval;
                    
                    // Log progress every 2 seconds
                    if (waited % 2000 == 0)
                    {
                        Console.WriteLine($"⏳ Still waiting for Kinect... ({waited/1000}s elapsed)");
                    }
                }

                if (!_initialized)
                {
                    Console.WriteLine("⏰ Kinect initialization timeout - the sensor may not be connected");
                    Console.WriteLine("💡 You can still try using the video feed later if you connect the Kinect");
                    _kinectAvailable = false;
                }
            });
            
            Console.WriteLine("🔄 KinectFaceTracker.Start() completed (initialization continuing in background)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ KinectFaceTracker.Start failed: {ex.Message}");
            Console.WriteLine($"📋 Exception details: {ex}");
            _kinectAvailable = false;
        }
    }

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
                    Console.WriteLine("📺 Video window opened");
                    
                    // Update status based on system state
                    UpdateVideoWindowStatus();
                });
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _videoWindow.Show();
                    _videoWindow.Activate();
                    _videoWindowEnabled = true;
                    UpdateVideoWindowStatus();
                });
            }

            // If Kinect isn't available, show a test pattern
            if (!_kinectAvailable || !_initialized)
            {
                StartTestPattern();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error showing video window: {ex.Message}");
        }
    }

    private static void UpdateVideoWindowStatus()
    {
        if (_videoWindow == null) return;

        string status;
        if (!_kinectAvailable)
        {
            status = "⚠️ Kinect not available - showing test pattern";
        }
        else if (!_initialized)
        {
            status = "🔄 Kinect initializing - please wait...";
        }
        else if (_embedder == null)
        {
            var trackingMode = _useFaceSDK ? "Kinect Face x64 SDK" : "Body Joint Tracking";
            status = $"📹 Video active with {trackingMode} - loading recognition model...";
        }
        else
        {
            var trackingMode = _useFaceSDK ? "Kinect Face x64 SDK" : "Enhanced Body Tracking";
            status = $"✅ Full system ready - {trackingMode} + face recognition active";
        }

        _videoWindow.UpdateStatus(status);
    }

    private static void StartTestPattern()
    {
        Task.Run(async () =>
        {
            try
            {
                Console.WriteLine("📺 Starting test pattern for video window");
                
                while (_videoWindowEnabled && (!_kinectAvailable || !_initialized))
                {
                    await Task.Delay(100); // 10 FPS for test pattern
                    
                    if (_videoWindow != null)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                // Create a simple test pattern
                                var testBitmap = CreateTestPattern();
                                _videoWindow.UpdateVideoFrame(testBitmap);
                                
                                // Add a test face detection
                                _videoWindow.ClearFaceDetections();
                                _videoWindow.AddFaceDetection(400, 300, 200, 200, "TEST PATTERN", 1.0f);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"❌ Test pattern error: {ex.Message}");
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Test pattern failed: {ex.Message}");
            }
        });
    }

    private static BitmapSource CreateTestPattern()
    {
        int width = 640;
        int height = 480;
        int stride = width * 4; // 4 bytes per pixel (BGRA)
        byte[] pixels = new byte[height * stride];

        // Create a simple gradient test pattern
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * stride + x * 4;
                
                // Create a colorful test pattern
                pixels[index] = (byte)(x % 256);     // Blue
                pixels[index + 1] = (byte)(y % 256); // Green  
                pixels[index + 2] = (byte)((x + y) % 256); // Red
                pixels[index + 3] = 255; // Alpha
            }
        }

        // Add some text-like pattern
        var time = DateTime.Now;
        int textY = height / 2;
        for (int x = 100; x < 500; x += 20)
        {
            for (int y = textY - 10; y < textY + 10; y++)
            {
                if (y >= 0 && y < height && x < width)
                {
                    int index = y * stride + x * 4;
                    pixels[index] = 255;     // Blue
                    pixels[index + 1] = 255; // Green  
                    pixels[index + 2] = 255; // Red
                    pixels[index + 3] = 255; // Alpha
                }
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static void HideVideoWindow()
    {
        try
        {
            _videoWindowEnabled = false;
            Application.Current.Dispatcher.Invoke(() =>
            {
                _videoWindow?.Hide();
                Console.WriteLine("📺 Video window hidden");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error hiding video window: {ex.Message}");
        }
    }

    private static void Sensor_IsAvailableChanged(object sender, IsAvailableChangedEventArgs e)
    {
        Console.WriteLine($"🔄 Kinect availability changed: {e.IsAvailable}");
        _kinectAvailable = e.IsAvailable;
        
        if (e.IsAvailable) 
            Task.Run(() => TryInitializeReadersIfAvailable());
        else 
            TeardownReaders();
            
        UpdateVideoWindowStatus();
    }

    private static async Task TryInitializeReadersIfAvailable()
    {
        if (_initialized || !_sensor.IsAvailable) 
        {
            Console.WriteLine($"⚠️ Skipping initialization: _initialized={_initialized}, sensor.IsAvailable={_sensor?.IsAvailable}");
            return;
        }

        lock (_initLock)
        {
            if (_initialized || !_sensor.IsAvailable) return;

            try
            {
                Console.WriteLine("🔧 Initializing Kinect streams...");
                
                // Initialize basic Kinect readers with individual error handling
                try
                {
                    _colorReader = _sensor.ColorFrameSource.OpenReader();
                    _colorDesc = _sensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);
                    _colorPixels = new byte[_colorDesc.Width * _colorDesc.Height * 4];
                    _colorReader.FrameArrived += ColorReader_FrameArrived;
                    Console.WriteLine("✅ Kinect color stream initialized");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Color stream initialization failed: {ex.Message}");
                    throw; // Re-throw to prevent partial initialization
                }

                try
                {
                    _bodyReader = _sensor.BodyFrameSource.OpenReader();
                    _bodyReader.FrameArrived += BodyReader_FrameArrived;
                    _bodies = new Body[_sensor.BodyFrameSource.BodyCount];
                    Console.WriteLine("✅ Kinect body tracking initialized");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Body tracking initialization failed: {ex.Message}");
                    throw; // Re-throw to prevent partial initialization
                }

                // Initialize Kinect Face x64 SDK (this can fail gracefully)
                InitializeFaceSDK();

                _initialized = true;
                _kinectAvailable = true;
                
                var trackingMethod = _useFaceSDK ? "Kinect Face x64 SDK" : "Enhanced Body Joint Tracking";
                Console.WriteLine($"🎉 Kinect initialized successfully! Face tracking: {trackingMethod}");

                // Update video window status
                UpdateVideoWindowStatus();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ KinectFaceTracker initialization failed: {ex.Message}");
                Console.WriteLine($"📋 Full exception: {ex}");
                
                // Cleanup any partial initialization
                try
                {
                    TeardownReaders();
                }
                catch (Exception cleanupEx)
                {
                    Console.WriteLine($"⚠️ Error during cleanup: {cleanupEx.Message}");
                }
                
                _kinectAvailable = false;
                return;
            }
        }

        // Load ArcFace model AFTER Kinect is initialized (HEAVY - ~254MB)
        // This runs in background and doesn't block the UI
        if (_embedder == null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var modelPath = Path.Combine(baseDir, "models", "arcface.onnx");
                    
                    if (File.Exists(modelPath))
                    {
                        Console.WriteLine("🔄 Loading ArcFace model (this may take a moment)...");
                        UpdateVideoWindowStatus();
                        
                        // Add timeout for model loading
                        var loadTask = Task.Run(() => new ArcFaceEmbedder(modelPath, cudaDeviceId: 0));
                        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2)); // 2 minute timeout for large model
                        
                        var completedTask = await Task.WhenAny(loadTask, timeoutTask);
                        
                        if (completedTask == loadTask)
                        {
                            _embedder = await loadTask;
                            Console.WriteLine("✅ ArcFace model loaded - face recognition ready!");
                            
                            _systemReady = true;
                            UpdateVideoWindowStatus();
                        }
                        else
                        {
                            Console.WriteLine("⏰ ArcFace model loading timeout (2min) - face recognition will not be available");
                            UpdateVideoWindowStatus();
                        }
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ ArcFace model not found: {modelPath}");
                        Console.WriteLine("💡 Face detection will work, but face recognition will not be available");
                        UpdateVideoWindowStatus();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ ArcFace model loading failed: {ex.Message}");
                    Console.WriteLine("💡 Face detection will work, but face recognition will not be available");
                    UpdateVideoWindowStatus();
                }
            });
        }
    }

    private static void InitializeFaceSDK()
    {
        try
        {
            Console.WriteLine("🔄 Initializing Kinect Face x64 SDK...");
            
            // Initialize Face SDK arrays
            _faceFrameSources = new FaceFrameSource[BODY_COUNT];
            _faceFrameReaders = new FaceFrameReader[BODY_COUNT];
            _faceAlignments = new FaceAlignment[BODY_COUNT];
            _faceModels = new FaceModel[BODY_COUNT];

            // Configure face frame features for comprehensive tracking
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
                // Create face frame source with enhanced features
                _faceFrameSources[i] = new FaceFrameSource(_sensor, 0, faceFrameFeatures);
                
                // Create face frame reader
                _faceFrameReaders[i] = _faceFrameSources[i].OpenReader();
                
                if (_faceFrameReaders[i] != null)
                {
                    _faceFrameReaders[i].FrameArrived += FaceReader_FrameArrived;
                }

                // Initialize face alignment and model for HD face tracking
                _faceAlignments[i] = new FaceAlignment();
                _faceModels[i] = new FaceModel();
            }

            _faceSDKInitialized = true;
            _useFaceSDK = true;
            Console.WriteLine("✅ Kinect Face x64 SDK initialized successfully!");
            Console.WriteLine("🎯 Enhanced face tracking features enabled:");
            Console.WriteLine("   • Bounding box detection");
            Console.WriteLine("   • Facial landmark points");
            Console.WriteLine("   • Head rotation tracking");
            Console.WriteLine("   • Engagement detection");
            Console.WriteLine("   • Emotion detection (Happy)");
            Console.WriteLine("   • Eye state detection");
            Console.WriteLine("   • Mouth state detection");
            Console.WriteLine("   • Looking away detection");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Kinect Face x64 SDK initialization failed: {ex.Message}");
            Console.WriteLine("📝 Falling back to enhanced body joint tracking");
            
            _faceSDKInitialized = false;
            _useFaceSDK = false;
            
            // Ensure we clean up any partially initialized Face SDK resources
            CleanupFaceSDK();
            
            // Fall back to enhanced body joint tracking
            Console.WriteLine("✅ Enhanced body joint face detection enabled (fallback mode)");
        }
    }

    private static void CleanupFaceSDK()
    {
        try
        {
            // Dispose face readers and sources
            if (_faceFrameReaders != null)
            {
                for (int i = 0; i < _faceFrameReaders.Length; i++)
                {
                    if (_faceFrameReaders[i] != null)
                    {
                        _faceFrameReaders[i].FrameArrived -= FaceReader_FrameArrived;
                        _faceFrameReaders[i].Dispose();
                        _faceFrameReaders[i] = null;
                    }
                }
            }

            if (_faceFrameSources != null)
            {
                for (int i = 0; i < _faceFrameSources.Length; i++)
                {
                    if (_faceFrameSources[i] != null)
                    {
                        _faceFrameSources[i].Dispose();
                        _faceFrameSources[i] = null;
                    }
                }
            }

            // Dispose face models - these implement IDisposable
            if (_faceModels != null)
            {
                for (int i = 0; i < _faceModels.Length; i++)
                {
                    if (_faceModels[i] != null)
                    {
                        _faceModels[i].Dispose();
                        _faceModels[i] = null;
                    }
                }
            }

            // Note: FaceAlignment doesn't implement IDisposable in this version
            // Just set to null to allow garbage collection
            if (_faceAlignments != null)
            {
                for (int i = 0; i < _faceAlignments.Length; i++)
                {
                    _faceAlignments[i] = null;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error cleaning up Face SDK resources: {ex.Message}");
        }
    }

    private static void FaceReader_FrameArrived(object sender, FaceFrameArrivedEventArgs e)
    {
        using (var faceFrame = e.FrameReference.AcquireFrame())
        {
            if (faceFrame == null) return;

            var faceResult = faceFrame.FaceFrameResult;
            if (faceResult == null) return;

            try
            {
                // Check face engagement and quality
                bool isFaceEngaged = faceResult.FaceProperties.ContainsKey(FaceProperty.Engaged) &&
                                   faceResult.FaceProperties[FaceProperty.Engaged] == DetectionResult.Yes;

                bool isLookingAway = faceResult.FaceProperties.ContainsKey(FaceProperty.LookingAway) &&
                                   faceResult.FaceProperties[FaceProperty.LookingAway] == DetectionResult.Yes;

                // Get face bounding box in color space
                var faceBoundingBox = faceResult.FaceBoundingBoxInColorSpace;
                
                // Process face if it's valid and engaged (or not looking away)
                if (IsValidFaceBoundingBox(faceBoundingBox) && (isFaceEngaged || !isLookingAway))
                {
                    ProcessFaceDetection(faceBoundingBox, faceResult);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Face frame processing error: {ex.Message}");
                
                // Fallback to basic bounding box check
                var faceBoundingBox = faceResult.FaceBoundingBoxInColorSpace;
                if (IsValidFaceBoundingBox(faceBoundingBox))
                {
                    ProcessFaceDetection(faceBoundingBox, faceResult);
                }
            }
        }
    }

    private static bool IsValidFaceBoundingBox(RectI boundingBox)
    {
        // Check if bounding box is valid
        if (boundingBox.Left < 0 || boundingBox.Top < 0 ||
            boundingBox.Right >= _colorDesc.Width || boundingBox.Bottom >= _colorDesc.Height)
            return false;

        // Check minimum face size
        int width = boundingBox.Right - boundingBox.Left;
        int height = boundingBox.Bottom - boundingBox.Top;
        
        return width >= 50 && height >= 50;
    }

    private static void ProcessFaceDetection(RectI faceBoundingBox, FaceFrameResult faceResult = null)
    {
        try
        {
            // Calculate confidence based on face properties if available
            float confidence = CalculateFaceConfidence(faceResult);
            
            // ENROLLMENT: if a label is queued, save a snapshot now
            if (!string.IsNullOrWhiteSpace(_pendingLabelName))
            {
                try
                {
                    var savedPath = SaveFaceSnapshot(_pendingLabelName, faceBoundingBox);

                    if (!string.IsNullOrEmpty(savedPath))
                    {
                        Console.WriteLine($"💾 Saved face for '{_pendingLabelName}': {savedPath}");
                        Console.WriteLine($"   Face confidence: {confidence:F2}, Features detected: {GetFaceFeaturesSummary(faceResult)}");
                        
                        // Show enrollment feedback in video window
                        if (_videoWindowEnabled)
                        {
                            var width = faceBoundingBox.Right - faceBoundingBox.Left;
                            var height = faceBoundingBox.Bottom - faceBoundingBox.Top;
                            _videoWindow?.AddFaceDetection(faceBoundingBox.Left, faceBoundingBox.Top, width, height, 
                                $"✅ ENROLLED: {_pendingLabelName}", 1.0f);
                        }
                    }
                    else
                        Console.WriteLine("⚠️ Snapshot skipped (no valid face region detected).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error saving face snapshot: {ex.Message}");
                }
                finally
                {
                    _pendingLabelName = null; // reset
                }

                // Start a short cooldown to avoid instant self-match
                _recognitionCooldownUntil = DateTime.UtcNow.AddSeconds(2);
                Console.WriteLine("✅ Enroll complete. Recognition cooling down for 2s…");
                return; // important: do not run recognition on the same frame
            }

            // RECOGNITION: only when NOT enrolling (and ArcFace model is loaded)
            if (_recognitionEnabled
                && _embedder != null
                && DateTime.UtcNow >= _recognitionCooldownUntil
                && _recogSw.ElapsedMilliseconds > _recogIntervalMs)
            {
                _recogSw.Restart();

                var maybeKnown = TryRecognize(faceBoundingBox);
                if (maybeKnown != null)
                {
                    Console.WriteLine($"🔍 Recognize: {maybeKnown.Value.name} (cos={maybeKnown.Value.score:F3}, face_conf={confidence:F2})");
                    
                    // Display recognized face in video window
                    if (_videoWindowEnabled)
                    {
                        var width = faceBoundingBox.Right - faceBoundingBox.Left;
                        var height = faceBoundingBox.Bottom - faceBoundingBox.Top;
                        _videoWindow?.AddFaceDetection(faceBoundingBox.Left, faceBoundingBox.Top, width, height, 
                            maybeKnown.Value.name, maybeKnown.Value.score);
                        
                        // Fire face detection event
                        OnFaceDetected?.Invoke(faceBoundingBox.Left, faceBoundingBox.Top, width, height, 
                            maybeKnown.Value.name, maybeKnown.Value.score);
                    }
                }
                else
                {
                    // Show unknown face in video window
                    if (_videoWindowEnabled)
                    {
                        var width = faceBoundingBox.Right - faceBoundingBox.Left;
                        var height = faceBoundingBox.Bottom - faceBoundingBox.Top;
                        _videoWindow?.AddFaceDetection(faceBoundingBox.Left, faceBoundingBox.Top, width, height, 
                            "Unknown", confidence);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Face processing failed: {ex.Message}");
        }
    }

    private static float CalculateFaceConfidence(FaceFrameResult faceResult)
    {
        if (faceResult == null) return 0.5f;

        float confidence = 0.5f; // Base confidence
        
        try
        {
            // Boost confidence based on detected features
            if (faceResult.FaceProperties.ContainsKey(FaceProperty.Engaged) &&
                faceResult.FaceProperties[FaceProperty.Engaged] == DetectionResult.Yes)
                confidence += 0.2f;

            if (faceResult.FaceProperties.ContainsKey(FaceProperty.LookingAway) &&
                faceResult.FaceProperties[FaceProperty.LookingAway] == DetectionResult.No)
                confidence += 0.1f;

            // Check if eyes are detected and open
            if (faceResult.FaceProperties.ContainsKey(FaceProperty.LeftEyeClosed) &&
                faceResult.FaceProperties[FaceProperty.LeftEyeClosed] == DetectionResult.No)
                confidence += 0.1f;

            if (faceResult.FaceProperties.ContainsKey(FaceProperty.RightEyeClosed) &&
                faceResult.FaceProperties[FaceProperty.RightEyeClosed] == DetectionResult.No)
                confidence += 0.1f;

            // Ensure confidence is within bounds
            confidence = Math.Clamp(confidence, 0.0f, 1.0f);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error calculating face confidence: {ex.Message}");
            confidence = 0.5f;
        }

        return confidence;
    }

    private static string GetFaceFeaturesSummary(FaceFrameResult faceResult)
    {
        if (faceResult == null) return "basic";

        var features = new List<string>();
        
        try
        {
            if (faceResult.FaceProperties.ContainsKey(FaceProperty.Engaged) &&
                faceResult.FaceProperties[FaceProperty.Engaged] == DetectionResult.Yes)
                features.Add("engaged");

            if (faceResult.FaceProperties.ContainsKey(FaceProperty.Happy) &&
                faceResult.FaceProperties[FaceProperty.Happy] == DetectionResult.Yes)
                features.Add("happy");

            if (faceResult.FaceProperties.ContainsKey(FaceProperty.MouthOpen) &&
                faceResult.FaceProperties[FaceProperty.MouthOpen] == DetectionResult.Yes)
                features.Add("mouth-open");

            if (faceResult.FaceProperties.ContainsKey(FaceProperty.LeftEyeClosed) &&
                faceResult.FaceProperties[FaceProperty.LeftEyeClosed] == DetectionResult.No)
                features.Add("eyes-open");

            return features.Count > 0 ? string.Join(", ", features) : "basic";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error getting face features: {ex.Message}");
            return "basic";
        }
    }

    private static void TeardownReaders()
    {
        try
        {
            // Dispose color reader
            if (_colorReader != null)
            {
                _colorReader.FrameArrived -= ColorReader_FrameArrived;
                _colorReader.Dispose();
                _colorReader = null;
            }

            // Dispose body reader
            if (_bodyReader != null)
            {
                _bodyReader.FrameArrived -= BodyReader_FrameArrived;
                _bodyReader.Dispose();
                _bodyReader = null;
            }

            // Cleanup Face SDK if it was initialized
            if (_faceSDKInitialized)
            {
                CleanupFaceSDK();
                _faceSDKInitialized = false;
                _useFaceSDK = false;
            }

            _initialized = false;
            _kinectAvailable = false;
            Console.WriteLine("🔄 Kinect readers and Face x64 SDK disposed.");
            
            UpdateVideoWindowStatus();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ KinectFaceTracker teardown failed: {ex.Message}");
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

            // Update video feed at controlled rate
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

            // Create bitmap from color data
            var bitmap = BitmapSource.Create(
                _colorDesc.Width, 
                _colorDesc.Height, 
                96, 96, 
                PixelFormats.Bgra32, 
                null, 
                pixelsCopy, 
                _colorDesc.Width * 4);

            bitmap.Freeze();

            // Send to video window
            _videoWindow?.UpdateVideoFrame(bitmap);
            
            // Fire event for other subscribers
            OnVideoFrame?.Invoke(bitmap);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error updating video display: {ex.Message}");
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
                // Update face frame sources with tracked body IDs for Face SDK
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
            else
            {
                // Use fallback body joint tracking method
                // Clear previous face detections in video window
                if (_videoWindowEnabled)
                    _videoWindow?.ClearFaceDetections();

                // Process each tracked body for enhanced face detection
                foreach (var body in _bodies)
                {
                    if (body.IsTracked)
                    {
                        ProcessBodyForEnhancedFaceDetection(body);
                    }
                }
            }
        }
    }

    private static void ProcessBodyForEnhancedFaceDetection(Body body)
    {
        try
        {
            // Get multiple joints for better face positioning
            var headJoint = body.Joints[JointType.Head];
            var neckJoint = body.Joints[JointType.Neck];

            // Check if we have good tracking data
            if (headJoint.TrackingState == TrackingState.NotTracked ||
                neckJoint.TrackingState == TrackingState.NotTracked)
                return;

            // Convert 3D positions to 2D color space
            var headPoint = _sensor.CoordinateMapper.MapCameraPointToColorSpace(headJoint.Position);
            var neckPoint = _sensor.CoordinateMapper.MapCameraPointToColorSpace(neckJoint.Position);

            // Calculate head-neck distance for face size estimation
            var headNeckDistance = Math.Sqrt(
                Math.Pow(headPoint.X - neckPoint.X, 2) + 
                Math.Pow(headPoint.Y - neckPoint.Y, 2));

            // Adaptive face size based on distance and body proportions
            int baseFaceSize = 120;
            var distanceFactor = Math.Max(0.5, Math.Min(2.0, headNeckDistance / 30.0));
            int adaptiveFaceSize = (int)(baseFaceSize * distanceFactor);

            // Calculate better face position (slightly above neck towards head)
            var faceX = headPoint.X * 0.7f + neckPoint.X * 0.3f; // 70% head, 30% neck
            var faceY = headPoint.Y * 0.8f + neckPoint.Y * 0.2f; // 80% head, 20% neck

            int halfSize = adaptiveFaceSize / 2;
            
            var enhancedBbox = new FakeFaceBoundingBox
            {
                Left = (int)(faceX - halfSize),
                Top = (int)(faceY - halfSize),
                Right = (int)(faceX + halfSize),
                Bottom = (int)(faceY + halfSize)
            };

            // Additional quality checks
            if (!IsValidFaceRegion(enhancedBbox))
                return;

            // Process face detection using enhanced method
            ProcessEnhancedFaceDetection(enhancedBbox);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Body processing failed: {ex.Message}");
        }
    }

    private static void ProcessEnhancedFaceDetection(FakeFaceBoundingBox faceBoundingBox)
    {
        try
        {
            // Convert to RectI for compatibility
            var rectI = new RectI
            {
                Left = faceBoundingBox.Left,
                Top = faceBoundingBox.Top,
                Right = faceBoundingBox.Right,
                Bottom = faceBoundingBox.Bottom
            };

            ProcessFaceDetection(rectI, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Enhanced face processing failed: {ex.Message}");
        }
    }

    private static bool IsValidFaceRegion(FakeFaceBoundingBox bbox)
    {
        // Check if face region is within camera bounds
        if (bbox.Left < 0 || bbox.Top < 0 || 
            bbox.Right >= _colorDesc.Width || bbox.Bottom >= _colorDesc.Height)
            return false;

        // Check minimum face size
        int width = bbox.Right - bbox.Left;
        int height = bbox.Bottom - bbox.Top;
        if (width < 60 || height < 60)
            return false;

        // Check aspect ratio (faces should be roughly square-ish)
        float aspectRatio = (float)width / height;
        if (aspectRatio < 0.6f || aspectRatio > 1.4f)
            return false;

        return true;
    }

    // Simple structure for fallback face detection
    private struct FakeFaceBoundingBox
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static (string name, float score)? TryRecognize(RectI boundingBox)
    {
        try
        {
            var crop = CropCurrentColor(boundingBox, inflate: 15, minSize: 80);
            if (crop == null)
            {
                return null;
            }

            var chw = ImagePreprocess.ToArcFaceCHW(crop);
            var emb = _embedder.Embed(chw);

            var match = MemoryStore.MatchBest(emb, thresholdCos: 0.45f); 
            return match;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Recognition failed: {ex.Message}");
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
        // Only save snapshots if we have the ArcFace model loaded
        if (_embedder == null)
        {
            Console.WriteLine("⚠️ Cannot save face snapshot - ArcFace model not loaded yet");
            return null;
        }

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

            int margin = 15; // Reduced margin for tighter crops
            int x = Math.Max(0, boundingBox.Left - margin);
            int y = Math.Max(0, boundingBox.Top - margin);
            int w = Math.Min(width - x, Math.Max(1, (boundingBox.Right - boundingBox.Left) + 2 * margin));
            int h = Math.Min(height - y, Math.Max(1, (boundingBox.Bottom - boundingBox.Top) + 2 * margin));

            if (w < 60 || h < 60) return null; // Minimum face size check

            var full = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixelsCopy, stride);
            var cropRect = new Int32Rect(x, y, w, h);
            var cropped = new CroppedBitmap(full, cropRect);
            cropped.Freeze();

            // Generate embedding and save
            var chw = ImagePreprocess.ToArcFaceCHW(cropped);
            var emb = _embedder.Embed(chw);
            MemoryStore.AddEmbedding(name, emb);

            // Save image file
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
            Console.WriteLine($"❌ SaveFaceSnapshot failed: {ex.Message}");
            return null;
        }
    }

    public static void QueueLabel(string name)
    {
        _pendingLabelName = name;
        var trackingMethod = _useFaceSDK ? "Kinect Face x64 SDK" : "Enhanced Body Tracking";
        Console.WriteLine($"📝 [Face] Queued label: {name} (using {trackingMethod})");
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
            return "System ready";
    }

    public static void Stop()
    {
        try
        {
            TeardownReaders();
            _sensor?.Close();
            
            // Close video window
            _videoWindowEnabled = false;
            Application.Current.Dispatcher.Invoke(() =>
            {
                _videoWindow?.Close();
                _videoWindow = null;
            });
            
            Console.WriteLine("🔄 Kinect stopped");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ KinectFaceTracker.Stop failed: {ex.Message}");
        }
    }
}

































































