using System;
using System.Windows;
using System.Globalization;

namespace Kinectv1
{
    public partial class MainWindow : Window
    {
        private float[] _lastVoiceEmbedding = null;
        private float _currentThreshold = 0.40f; // Default threshold

        public MainWindow()
        {
            try
            {
                InitializeComponent();

                // Set window properties for better focus behavior
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                this.WindowState = WindowState.Normal;
                this.ShowInTaskbar = true;
                this.Title = "Kinect Face & Voice Recognition - Starting...";

                Console.WriteLine("?? MainWindow constructor started");

                // Hook GUI events - use try-catch for each in case services aren't ready
                try
                {
                    VoiceRecognizer.OnTranscription += UpdateTranscription;
                    VoiceRecognizer.OnRmsLevel += UpdateRmsLevel;
                    VoiceRecognizer.OnSpeakerMatch += ShowSpeakerMatch;
                    VoiceRecognizer.OnNameHeard += name => KinectFaceTracker.QueueLabel(name);
                    Console.WriteLine("? VoiceRecognizer events hooked");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"?? Could not hook VoiceRecognizer events: {ex.Message}");
                }

                try
                {
                    VoiceEnrollmentManager.OnEnrollmentProgress += UpdateVoiceEnrollmentProgress;
                    VoiceEnrollmentManager.OnEnrollmentComplete += OnVoiceEnrollmentComplete;
                    VoiceEnrollmentManager.OnEnrollmentCancelled += OnVoiceEnrollmentCancelled;
                    VoiceRecognizer.OnVoiceEmbedding += OnVoiceEmbedding;
                    Console.WriteLine("? Voice enrollment events hooked");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"?? Could not hook voice enrollment events: {ex.Message}");
                }

                try
                {
                    KinectFaceTracker.OnFaceDetected += OnFaceDetected;
                    Console.WriteLine("? Kinect face detection events hooked");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"?? Could not hook face detection events: {ex.Message}");
                }

                // Update threshold display - safely
                UpdateThresholdDisplay();

                // Update title to show ready state
                this.Title = "Kinect Face & Voice Recognition - Ready";
                Console.WriteLine("? MainWindow constructor completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? MainWindow constructor failed: {ex.Message}");
                this.Title = "Kinect Face & Voice Recognition - Error";
                
                // Don't throw - let the window still show
                try
                {
                    MessageBox.Show($"MainWindow initialization error:\n{ex.Message}\n\nSome features may not work properly.", 
                        "Initialization Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                    Console.WriteLine("? Could not show initialization warning");
                }
            }
        }

        private void OnFaceDetected(int left, int top, int width, int height, string name, float confidence)
        {
            // Handle face detection events if needed for main window integration
            Console.WriteLine($"?? Face detected in video: {name} at ({left},{top}) {width}x{height} confidence: {confidence:F2}");
        }

        private void UpdateThresholdDisplay()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var currentThreshold = SpeakerIdentifier.GetDefaultThreshold();
                        ThresholdTextBox.Text = currentThreshold.ToString("0.00");
                        _currentThreshold = currentThreshold;
                    }
                    catch (Exception ex)
                    {
                        // SpeakerIdentifier might not be initialized yet - use default
                        Console.WriteLine($"?? Could not get threshold during startup: {ex.Message}");
                        ThresholdTextBox.Text = _currentThreshold.ToString("0.00");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"?? UpdateThresholdDisplay failed: {ex.Message}");
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            
            // Force the window to come to front
            this.Activate();
            this.Focus();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            Console.WriteLine("? MainWindow activated and has focus");
        }

        private void UpdateTranscription(string text)
        {
            Dispatcher.Invoke(() =>
                TranscriptionLabel.Content = text);
        }

        private float _smoothedRms;
        private void UpdateRmsLevel(float rawRms)
        {
            Dispatcher.Invoke(() =>
            {
                // Smooth the RMS values for better visualization
                _smoothedRms = 0.7f * _smoothedRms + 0.3f * rawRms;
                
                // Scale RMS for display (typical voice range 500-5000, scale to 0-100)
                var display = Math.Clamp(_smoothedRms / 50.0f, 0, 100);
                
                RmsBar.Value = display;
                RmsText.Text = $"RMS: {display:F1}";
                
                // Change color based on activity level
                if (display > 20)
                    RmsBar.Foreground = System.Windows.Media.Brushes.Green;
                else if (display > 5)
                    RmsBar.Foreground = System.Windows.Media.Brushes.Orange;
                else
                    RmsBar.Foreground = System.Windows.Media.Brushes.Red;
            });
        }

        private void ShowSpeakerMatch(string name, float score)
        {
            Dispatcher.Invoke(() =>
            {
                var threshold = SpeakerIdentifier.GetDefaultThreshold();
                SpeakerLabel.Content = $"{name} ({score:F2}) [T:{threshold:F2}]";
            });
        }

        private void OnVoiceEmbedding(float[] embedding)
        {
            // Store the last embedding for debugging purposes
            _lastVoiceEmbedding = embedding;
        }

        // Face Enrollment
        private void EnrollButton_Click(object sender, RoutedEventArgs e)
        {
            var name = EnrollNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(
                  "Please enter a name before enrolling.",
                  "Missing Name",
                  MessageBoxButton.OK,
                  MessageBoxImage.Warning);
                return;
            }

            Console.WriteLine($"[GUI] Enrolling face: {name}");
            KinectFaceTracker.QueueLabel(name);
        }

        // Enhanced Voice Enrollment
        private void EnrollVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            var name = VoiceEnrollNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(
                  "Please enter a name before enrolling voice.",
                  "Missing Name",
                  MessageBoxButton.OK,
                  MessageBoxImage.Warning);
                return;
            }

            Console.WriteLine($"[GUI] Starting enhanced voice enrollment: {name}");
            VoiceEnrollmentManager.StartEnrollment(name);
            
            // Update UI state
            EnrollVoiceButton.IsEnabled = false;
            CancelVoiceButton.IsEnabled = true;
            VoiceEnrollNameBox.IsEnabled = false;
        }

        private void CancelVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[GUI] Cancelling enhanced voice enrollment");
            VoiceEnrollmentManager.CancelEnrollment();
        }

        private void UpdateVoiceEnrollmentProgress(string name, int current, int total)
        {
            Dispatcher.Invoke(() =>
            {
                VoiceEnrollProgress.Value = current;
                VoiceProgressText.Text = $"{current}/{total}";
                
                // Calculate and display percentage
                var percentage = total > 0 ? (float)current / total * 100 : 0;
                VoiceProgressPercent.Text = $"({percentage:F0}%)";
                
                // Update status text with enhanced feedback
                if (current == 0)
                {
                    VoiceEnrollStatusText.Text = $"Starting enhanced enrollment for '{name}'";
                }
                else if (current < total)
                {
                    var remaining = total - current;
                    VoiceEnrollStatusText.Text = $"Enrolling '{name}' - {remaining} more sample(s) needed";
                    
                    // Provide milestone feedback
                    if (current == 3)
                        VoiceEnrollStatusText.Text = $"Great progress! {remaining} more for enhanced accuracy";
                    else if (current == 7)
                        VoiceEnrollStatusText.Text = $"Almost done! Just {remaining} more samples";
                }
                else
                {
                    VoiceEnrollStatusText.Text = $"Completing enhanced enrollment for '{name}'...";
                }
                
                // Color-code the progress bar
                if (percentage < 30)
                    VoiceEnrollProgress.Foreground = System.Windows.Media.Brushes.Red;
                else if (percentage < 70)
                    VoiceEnrollProgress.Foreground = System.Windows.Media.Brushes.Orange;
                else
                    VoiceEnrollProgress.Foreground = System.Windows.Media.Brushes.Green;
            });
        }

        private void OnVoiceEnrollmentComplete(string name)
        {
            Dispatcher.Invoke(() =>
            {
                VoiceEnrollStatusText.Text = $"? Enhanced voice enrollment completed for '{name}'";
                ResetVoiceEnrollmentUI();
                
                MessageBox.Show(
                    $"Enhanced voice enrollment completed successfully for '{name}'!\n\n" +
                    $"? 10 voice samples captured for maximum accuracy\n" +
                    $"?? Voice recognition should be highly accurate\n" +
                    $"?? You can now test speaker recognition by speaking\n" +
                    $"?? Face recognition is visible in the video feed",
                    "Enhanced Voice Enrollment Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
        }

        private void OnVoiceEnrollmentCancelled(string name)
        {
            Dispatcher.Invoke(() =>
            {
                VoiceEnrollStatusText.Text = $"? Enhanced voice enrollment cancelled for '{name}'";
                ResetVoiceEnrollmentUI();
            });
        }

        private void ResetVoiceEnrollmentUI()
        {
            EnrollVoiceButton.IsEnabled = true;
            CancelVoiceButton.IsEnabled = false;
            VoiceEnrollNameBox.IsEnabled = true;
            VoiceEnrollProgress.Value = 0;
            VoiceProgressText.Text = "0/10";
            VoiceProgressPercent.Text = "(0%)";
            VoiceEnrollProgress.Foreground = System.Windows.Media.Brushes.Blue;
            
            // Reset status after a delay
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.Tick += (s, e) =>
            {
                VoiceEnrollStatusText.Text = "Ready for enhanced voice enrollment";
                timer.Stop();
            };
            timer.Start();
        }

        private void ListSpeakersButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[GUI] Listing enrolled speakers...");
            SpeakerIdentifier.ListEnrolledSpeakers();
            
            var count = SpeakerIdentifier.GetEnrolledSpeakersCount();
            MessageBox.Show(
                $"Found {count} enrolled speaker(s).\n\nCheck the console output for details.",
                "Enrolled Speakers",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ShowInstructionsButton_Click(object sender, RoutedEventArgs e)
        {
            VoiceEnrollmentManager.ShowEnrollmentInstructions();
            
            MessageBox.Show(
                "Enhanced Voice Enrollment Instructions:\n\n" +
                "1. Enter a name in the text box\n" +
                "2. Click 'Enroll Voice (10x)' button\n" +
                "3. Speak clearly when audio levels are high (green bar)\n" +
                "4. Pause between speech samples (1.2 seconds minimum)\n" +
                "5. Repeat until 10 samples are captured\n" +
                "6. Enrollment will complete automatically\n\n" +
                "Enhanced Tips:\n" +
                "• Use different phrases/words for each sample\n" +
                "• Vary your speaking volume slightly\n" +
                "• Speak from different angles if possible\n" +
                "• More samples = significantly better accuracy\n" +
                "• Progress milestones at 30% and 70%\n\n" +
                "Video Feed:\n" +
                "• Click 'Show Video Feed' to see live camera\n" +
                "• Face detection boxes show in real-time\n" +
                "• Color coding: Green=High confidence, Yellow=Medium, Red=Low/Unknown\n" +
                "• Names appear above detected faces\n\n" +
                "Threshold Adjustment:\n" +
                "• Lower values (0.20-0.35) = more permissive matching\n" +
                "• Higher values (0.40-0.60) = stricter matching\n" +
                "• Start with 0.30 if recognition fails\n" +
                "• Enhanced enrollment may allow higher thresholds",
                "Enhanced Voice Enrollment Instructions",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // Video Display Controls
        private void ShowVideoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Console.WriteLine("?? [GUI] Opening video feed window...");
                
                // Get system status first
                var systemStatus = KinectFaceTracker.GetSystemStatus();
                Console.WriteLine($"?? System status: {systemStatus}");
                
                KinectFaceTracker.ShowVideoWindow();
                
                // Update button states
                ShowVideoButton.IsEnabled = false;
                HideVideoButton.IsEnabled = true;
                
                string statusMessage;
                if (systemStatus == "Kinect not available")
                {
                    statusMessage = "Video window opened with test pattern!\n\n" +
                                  "?? Kinect not detected - showing colorful test pattern\n" +
                                  "?? Window demonstrates video feed functionality\n" +
                                  "?? Connect Kinect for live camera feed\n\n" +
                                  "The video window should appear to the right of this window.";
                }
                else if (systemStatus == "Kinect initializing")
                {
                    statusMessage = "Video window opened - Kinect initializing!\n\n" +
                                  "?? Kinect is starting up - please wait\n" +
                                  "?? Test pattern shown until camera is ready\n" +
                                  "?? This may take 10-15 seconds\n\n" +
                                  "The video window should appear to the right of this window.";
                }
                else
                {
                    statusMessage = "Video feed window opened!\n\n" +
                                  "?? Live camera feed with face detection\n" +
                                  "?? Face bounding boxes and name labels\n" +
                                  "?? Color-coded confidence levels\n" +
                                  "?? Real-time FPS and face count\n\n" +
                                  "The video window should appear to the right of this window.";
                }
                
                MessageBox.Show(statusMessage, "Video Feed Active", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error opening video window: {ex.Message}");
                MessageBox.Show(
                    $"Error opening video window:\n\n{ex.Message}\n\nCheck console for details.",
                    "Video Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void HideVideoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Console.WriteLine("?? [GUI] Hiding video feed window...");
                KinectFaceTracker.HideVideoWindow();
                
                // Update button states
                ShowVideoButton.IsEnabled = true;
                HideVideoButton.IsEnabled = false;
                
                Console.WriteLine("?? Video feed window hidden");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error hiding video window: {ex.Message}");
                MessageBox.Show(
                    $"Error hiding video window:\n\n{ex.Message}",
                    "Video Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // Debugging Methods
        private void TestVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastVoiceEmbedding == null)
            {
                MessageBox.Show(
                    "No voice embedding captured yet.\n\nPlease speak first to generate a voice embedding, then try again.",
                    "No Voice Data",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Console.WriteLine("?? [GUI] Testing enhanced voice recognition with current embedding...");
            SpeakerIdentifier.TestRecognitionWithLowerThreshold(_lastVoiceEmbedding);
            
            MessageBox.Show(
                "Enhanced voice recognition test completed.\n\nCheck the console output for detailed results with different thresholds.",
                "Voice Test Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // New threshold control methods
        private float GetThresholdFromTextBox()
        {
            try
            {
                if (float.TryParse(ThresholdTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float threshold))
                {
                    if (threshold >= 0.0f && threshold <= 1.0f)
                    {
                        return threshold;
                    }
                    else
                    {
                        MessageBox.Show("Threshold must be between 0.0 and 1.0", "Invalid Threshold", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return _currentThreshold;
                    }
                }
                else
                {
                    MessageBox.Show("Please enter a valid decimal number (e.g., 0.40)", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return _currentThreshold;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing threshold: {ex.Message}");
                return _currentThreshold;
            }
        }

        private void TestWithThresholdButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastVoiceEmbedding == null)
            {
                MessageBox.Show("Please speak first to generate a voice embedding.", "No Voice Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            float threshold = GetThresholdFromTextBox();
            
            Console.WriteLine($"?? [GUI] Testing with custom threshold ({threshold:F3})...");
            Console.WriteLine($"     Current system threshold: {SpeakerIdentifier.GetDefaultThreshold():F3}");
            
            var match = MemoryStore.MatchBestVoice(_lastVoiceEmbedding, threshold);
            
            if (match.HasValue)
            {
                Console.WriteLine($"? Custom threshold match: {match.Value.name} (score: {match.Value.score:F3})");
                MessageBox.Show(
                    $"With threshold {threshold:F3}:\n\nMatch found: {match.Value.name}\nScore: {match.Value.score:F3}\n\nIf this looks good, click 'Set Default' to make it permanent.",
                    "Custom Threshold Result",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                // Get the best match regardless of threshold for comparison
                var bestMatch = MemoryStore.GetBestVoiceMatchWithScores(_lastVoiceEmbedding);
                
                string message = $"No match found with threshold {threshold:F3}.";
                if (bestMatch.HasValue)
                {
                    message += $"\n\nBest available match:\n{bestMatch.Value.name} (score: {bestMatch.Value.score:F3})";
                    var suggestedThreshold = Math.Max(0.10f, bestMatch.Value.score - 0.01f);
                    message += $"\n\nTry threshold: {suggestedThreshold:F3}";
                }
                
                Console.WriteLine($"? No match with threshold {threshold:F3}");
                MessageBox.Show(message, "No Match", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetAsDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            float newThreshold = GetThresholdFromTextBox();
            _currentThreshold = newThreshold;
            
            // Update the default threshold in SpeakerIdentifier
            SpeakerIdentifier.SetDefaultThreshold(newThreshold);
            
            Console.WriteLine($"?? [GUI] Set default voice threshold to {newThreshold:F3}");
            
            // Update the display to show current threshold
            UpdateThresholdDisplay();
            
            MessageBox.Show(
                $"Default voice recognition threshold set to {newThreshold:F3}\n\nThis will be used for all future voice recognition.\n\nYou should see this change immediately in the console output.",
                "Threshold Updated",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void CheckDataButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("?? [GUI] Checking enhanced voice enrollment data...");
            SpeakerIdentifier.ListEnrolledSpeakers();
            
            var people = MemoryStore.GetAllPeople();
            int totalVoiceEmbeddings = 0;
            foreach (var person in people)
            {
                totalVoiceEmbeddings += person.VoiceEmbeddings.Count;
            }

            var actualThreshold = SpeakerIdentifier.GetDefaultThreshold();
            var textBoxThreshold = GetThresholdFromTextBox();
            var videoStatus = KinectFaceTracker.IsVideoWindowOpen() ? "Active" : "Hidden";
            
            string message = $"Enhanced Voice Data Summary:\n\n" +
                           $"• People enrolled: {people.Count}\n" +
                           $"• Total voice embeddings: {totalVoiceEmbeddings}\n" +
                           $"• Enhanced samples per person: up to 10\n" +
                           $"• Current voice embedding: {(_lastVoiceEmbedding?.Length ?? 0)} dimensions\n" +
                           $"• System threshold: {actualThreshold:F3}\n" +
                           $"• Text box shows: {textBoxThreshold:F3}\n" +
                           $"• Thresholds match: {(Math.Abs(actualThreshold - textBoxThreshold) < 0.001f ? "YES" : "NO")}\n" +
                           $"• Video feed status: {videoStatus}\n\n" +
                           "Check console for detailed information.";

            MessageBox.Show(message, "Enhanced Voice Data Check", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Voice Data Management
        private void FlushVoiceButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "?? WARNING: This will permanently delete ALL enhanced voice data!\n\n" +
                "This includes:\n" +
                "• All voice embeddings for all people (up to 10 per person)\n" +
                "• Enhanced voice recognition will stop working until you re-enroll\n" +
                "• Face recognition data will remain intact\n\n" +
                "Are you sure you want to continue?",
                "Confirm Enhanced Voice Data Deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                var secondConfirm = MessageBox.Show(
                    "?? FINAL CONFIRMATION ??\n\n" +
                    "This action cannot be undone!\n\n" +
                    "All enhanced voice enrollment data (10 samples per person) will be permanently deleted.\n\n" +
                    "Click YES to permanently delete all voice data.",
                    "Final Confirmation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Stop,
                    MessageBoxResult.No);

                if (secondConfirm == MessageBoxResult.Yes)
                {
                    Console.WriteLine("??? [GUI] Flushing all enhanced voice embeddings...");
                    
                    try
                    {
                        int deletedCount = MemoryStore.FlushAllVoiceEmbeddings();
                        
                        // Clear the last voice embedding
                        _lastVoiceEmbedding = null;
                        
                        Console.WriteLine($"? Successfully flushed {deletedCount} enhanced voice embeddings");
                        
                        MessageBox.Show(
                            $"Enhanced voice data flush completed!\n\n" +
                            $"• Deleted {deletedCount} voice embeddings\n" +
                            $"• Enhanced voice recognition reset\n" +
                            $"• Face recognition still active\n" +
                            $"• Video feed continues to show face detection\n\n" +
                            "You can now re-enroll voices with fresh enhanced data (10 samples each).",
                            "Enhanced Voice Data Flushed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"? Error flushing enhanced voice data: {ex.Message}");
                        MessageBox.Show(
                            $"Error occurred while flushing enhanced voice data:\n\n{ex.Message}\n\nCheck console for details.",
                            "Flush Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Ensure video window is closed when main window closes
            try
            {
                KinectFaceTracker.HideVideoWindow();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Error closing video window: {ex.Message}");
            }
            
            base.OnClosed(e);
        }
    }
}