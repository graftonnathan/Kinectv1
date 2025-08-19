// VoiceEnrollmentManager.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Kinectv1
{
    public static class VoiceEnrollmentManager
    {
        private static string _pendingEnrollmentName = null;
        private static readonly List<float[]> _enrollmentSamples = new List<float[]>();
        private static readonly int RequiredSamples = 10; // Increased from 3 to 10 for better voice recognition accuracy
        private static DateTime _lastSampleTime = DateTime.MinValue;
        private static readonly TimeSpan MinSampleInterval = TimeSpan.FromSeconds(1.2); // Reduced from 1.5s to 1.2s for faster enrollment

        public static Action<string, int, int> OnEnrollmentProgress; // name, current, total
        public static Action<string> OnEnrollmentComplete;
        public static Action<string> OnEnrollmentCancelled;

        public static bool IsEnrolling => !string.IsNullOrEmpty(_pendingEnrollmentName);

        public static void StartEnrollment(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("Cannot start voice enrollment: Invalid name");
                return;
            }

            _pendingEnrollmentName = name;
            _enrollmentSamples.Clear();
            _lastSampleTime = DateTime.MinValue;
            
            Console.WriteLine($"Started voice enrollment for '{name}' - {RequiredSamples} samples required");
            
            OnEnrollmentProgress?.Invoke(name, 0, RequiredSamples);
        }

        public static void CancelEnrollment()
        {
            if (_pendingEnrollmentName != null)
            {
                var name = _pendingEnrollmentName;
                _pendingEnrollmentName = null;
                _enrollmentSamples.Clear();
                
                Console.WriteLine($"Voice enrollment cancelled for '{name}'");
                OnEnrollmentCancelled?.Invoke(name);
            }
        }

        public static void ProcessVoiceSample(float[] embedding)
        {
            if (!IsEnrolling || embedding == null || embedding.Length == 0)
                return;

            // Ensure minimum time between samples to get diverse voice data
            var now = DateTime.UtcNow;
            if (now - _lastSampleTime < MinSampleInterval)
            {
                return; // Too soon, skip this sample
            }

            _enrollmentSamples.Add(embedding);
            _lastSampleTime = now;

            var currentCount = _enrollmentSamples.Count;
            var progressPercent = (float)currentCount / RequiredSamples * 100;
            
            Console.WriteLine($"Voice sample {currentCount}/{RequiredSamples} captured for '{_pendingEnrollmentName}' ({progressPercent:F1}% complete)");
            
            OnEnrollmentProgress?.Invoke(_pendingEnrollmentName, currentCount, RequiredSamples);

            if (currentCount >= RequiredSamples)
            {
                CompleteEnrollment();
            }
            else
            {
                var remaining = RequiredSamples - currentCount;
                Console.WriteLine($"Please speak again... {remaining} more sample(s) needed");
                
                // Provide milestone feedback
                if (currentCount == 3)
                    Console.WriteLine($"Great! {remaining} more for enhanced accuracy.");
                else if (currentCount == 7)
                    Console.WriteLine($"Almost there! Just {remaining} more samples.");
            }
        }

        private static void CompleteEnrollment()
        {
            if (_pendingEnrollmentName == null || _enrollmentSamples.Count == 0)
                return;

            var name = _pendingEnrollmentName;
            
            try
            {
                // Store all collected voice samples
                foreach (var sample in _enrollmentSamples)
                {
                    SpeakerIdentifier.EnrollSpeaker(name, sample);
                }

                Console.WriteLine($"Voice enrollment completed for '{name}' with {_enrollmentSamples.Count} samples");
                
                // Reset state
                _pendingEnrollmentName = null;
                _enrollmentSamples.Clear();
                
                OnEnrollmentComplete?.Invoke(name);
                
                // Show enrolled speakers
                Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(500); // Brief delay
                    SpeakerIdentifier.ListEnrolledSpeakers();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to complete voice enrollment for '{name}': {ex.Message}");
                CancelEnrollment();
            }
        }

        public static void ShowEnrollmentInstructions()
        {
            Console.WriteLine("Enhanced Voice Enrollment Instructions:");
            Console.WriteLine("   1. Enter a name in the text box");
            Console.WriteLine("   2. Click 'Enroll Voice' button");
            Console.WriteLine("   3. Speak clearly when audio levels are high (green bar)");
            Console.WriteLine("   4. Pause between speech samples (minimum 1.2 seconds)");
            Console.WriteLine($"   5. Repeat until {RequiredSamples} samples are captured (enhanced accuracy)");
            Console.WriteLine("   6. Enrollment will complete automatically");
            Console.WriteLine("");
            Console.WriteLine("Enhanced Tips:");
            Console.WriteLine("   • Use different phrases/words for each sample");
            Console.WriteLine("   • Vary your speaking volume slightly");
            Console.WriteLine("   • Speak from different angles if possible");
            Console.WriteLine("   • More samples = better recognition accuracy");
        }

        public static string GetEnrollmentStatus()
        {
            if (!IsEnrolling)
                return "Ready for enhanced enrollment";

            var progress = $"{_enrollmentSamples.Count}/{RequiredSamples}";
            var percentage = (float)_enrollmentSamples.Count / RequiredSamples * 100;
            return $"Enrolling '{_pendingEnrollmentName}' - {progress} samples ({percentage:F0}%)";
        }

        public static int GetRequiredSamples()
        {
            return RequiredSamples;
        }

        public static int GetCurrentSampleCount()
        {
            return _enrollmentSamples.Count;
        }
    }
}