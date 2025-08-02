using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Kinectv1
{
    public partial class VideoWindow : Window
    {
        private DispatcherTimer? _fpsTimer;
        private int _frameCount = 0;
        private DateTime _lastFpsUpdate = DateTime.Now;
        private readonly List<FaceDetection> _currentFaces = new();
        private readonly object _faceLock = new object();

        public VideoWindow()
        {
            InitializeComponent();
            InitializeVideoWindow();
        }

        private void InitializeVideoWindow()
        {
            // Position window to the right of the main window
            this.Left = SystemParameters.PrimaryScreenWidth - this.Width - 50;
            this.Top = 50;
            
            // Initialize FPS timer
            _fpsTimer = new DispatcherTimer();
            _fpsTimer.Interval = TimeSpan.FromSeconds(1);
            _fpsTimer.Tick += UpdateFpsCounter;
            _fpsTimer.Start();

            // Set initial status
            UpdateStatus("Video feed ready - waiting for Kinect data");
            
            Console.WriteLine("?? Video window initialized");
        }

        public void UpdateVideoFrame(BitmapSource videoFrame)
        {
            if (videoFrame == null) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    // Update the main video feed
                    VideoImage.Source = videoFrame;
                    
                    // Update resolution info
                    ResolutionText.Text = $"Resolution: {videoFrame.PixelWidth}x{videoFrame.PixelHeight}";
                    
                    // Count frames for FPS
                    _frameCount++;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error updating video frame: {ex.Message}");
            }
        }

        public void AddFaceDetection(int left, int top, int width, int height, string name, float confidence)
        {
            lock (_faceLock)
            {
                _currentFaces.Add(new FaceDetection
                {
                    Left = left,
                    Top = top,
                    Width = width,
                    Height = height,
                    Name = name ?? "Unknown",
                    Confidence = confidence,
                    Timestamp = DateTime.Now
                });
            }

            Dispatcher.Invoke(UpdateFaceOverlays);
        }

        public void ClearFaceDetections()
        {
            lock (_faceLock)
            {
                _currentFaces.Clear();
            }
            Dispatcher.Invoke(UpdateFaceOverlays);
        }

        private void UpdateFaceOverlays()
        {
            try
            {
                // Clear existing overlays
                OverlayCanvas.Children.Clear();

                List<FaceDetection> facesToDraw;
                lock (_faceLock)
                {
                    // Remove old detections (older than 2 seconds)
                    _currentFaces.RemoveAll(f => DateTime.Now - f.Timestamp > TimeSpan.FromSeconds(2));
                    facesToDraw = new List<FaceDetection>(_currentFaces);
                }

                // Draw face rectangles and labels
                foreach (var face in facesToDraw)
                {
                    DrawFaceRectangle(face);
                    DrawFaceLabel(face);
                }

                // Update face count
                FaceCountText.Text = $"Faces: {facesToDraw.Count}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error updating face overlays: {ex.Message}");
            }
        }

        private void DrawFaceRectangle(FaceDetection face)
        {
            // Create rectangle for face bounding box
            var rect = new Rectangle
            {
                Width = face.Width,
                Height = face.Height,
                Stroke = GetFaceColor(face.Name, face.Confidence),
                StrokeThickness = 3,
                Fill = Brushes.Transparent
            };

            // Position the rectangle
            Canvas.SetLeft(rect, face.Left);
            Canvas.SetTop(rect, face.Top);

            OverlayCanvas.Children.Add(rect);
        }

        private void DrawFaceLabel(FaceDetection face)
        {
            // Create background for label
            var labelBackground = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), // Semi-transparent black
                Height = 25
            };

            // Create text label
            var label = new TextBlock
            {
                Text = $"{face.Name} ({face.Confidence:F2})",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5, 2, 5, 2)
            };

            // Measure text to size background
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            labelBackground.Width = label.DesiredSize.Width + 10;

            // Position label above the face rectangle
            var labelLeft = face.Left;
            var labelTop = Math.Max(0, face.Top - 25);

            // Ensure label stays within canvas bounds
            if (labelLeft + labelBackground.Width > OverlayCanvas.Width)
                labelLeft = (int)(OverlayCanvas.Width - labelBackground.Width);

            Canvas.SetLeft(labelBackground, labelLeft);
            Canvas.SetTop(labelBackground, labelTop);
            Canvas.SetLeft(label, labelLeft);
            Canvas.SetTop(label, labelTop);

            OverlayCanvas.Children.Add(labelBackground);
            OverlayCanvas.Children.Add(label);
        }

        private Brush GetFaceColor(string name, float confidence)
        {
            // Color coding for face recognition confidence
            if (name == "Unknown" || confidence < 0.3f)
                return Brushes.Red;
            else if (confidence < 0.5f)
                return Brushes.Orange;
            else if (confidence < 0.7f)
                return Brushes.Yellow;
            else
                return Brushes.LimeGreen;
        }

        private void UpdateFpsCounter(object? sender, EventArgs e)
        {
            var now = DateTime.Now;
            var elapsed = now - _lastFpsUpdate;
            
            if (elapsed.TotalSeconds >= 1.0)
            {
                var fps = _frameCount / elapsed.TotalSeconds;
                FpsText.Text = $"FPS: {fps:F1}";
                
                _frameCount = 0;
                _lastFpsUpdate = now;
            }
        }

        public void UpdateStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _fpsTimer?.Stop();
            Console.WriteLine("?? Video window closed");
            base.OnClosed(e);
        }
    }

    // Data structure for face detection information
    public class FaceDetection
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Name { get; set; } = "Unknown";
        public float Confidence { get; set; }
        public DateTime Timestamp { get; set; }
    }
}