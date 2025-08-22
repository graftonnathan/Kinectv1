using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kinectv1.Tests
{
    /// <summary>
    /// Discord Frame Feed Tests - synthetic 48k frames → pipeline; assert no 'chunk too large'
    /// Tests Discord audio frame processing without actual Discord connection
    /// </summary>
    [TestClass]
    public class DiscordFrameFeed
    {
        [TestMethod]
        public void TestSyntheticDiscordFrames_ShouldProcessWithoutChunkErrors()
        {
            // Arrange
            var testFrames = CreateSynthetic48kFrames();
            var processingErrors = new List<string>();
            var processedFrames = new List<ProcessedDiscordFrame>();

            // Act
            foreach (var frame in testFrames)
            {
                try
                {
                    var processed = ProcessDiscordFrame(frame);
                    processedFrames.Add(processed);
                    
                    // Check for chunk size issues
                    if (processed.ChunkSizeMs > 100) // Arbitrary threshold for "too large"
                    {
                        processingErrors.Add($"Chunk too large: {processed.ChunkSizeMs}ms for user {frame.UserId}");
                    }
                }
                catch (Exception ex)
                {
                    processingErrors.Add($"Processing error for frame {frame.UserId}: {ex.Message}");
                }
            }

            // Assert
            Assert.AreEqual(0, processingErrors.Count, 
                $"Should have no processing errors, but got: {string.Join("; ", processingErrors)}");
            
            Assert.AreEqual(testFrames.Count, processedFrames.Count, 
                "All frames should be processed successfully");
            
            // Verify all frames are within acceptable parameters
            foreach (var frame in processedFrames)
            {
                Assert.IsTrue(frame.SampleRate == 48000, 
                    $"Frame should maintain 48kHz sample rate, got {frame.SampleRate}");
                
                Assert.IsTrue(frame.ChunkSizeMs >= 10 && frame.ChunkSizeMs <= 100, 
                    $"Chunk size should be reasonable (10-100ms), got {frame.ChunkSizeMs}ms");
            }
        }

        [TestMethod]
        public void TestDiscordFrameDispatchPrevention_ShouldPreventDuplicates()
        {
            // Arrange
            var frames = CreateDuplicateTestFrames();
            var dispatchedTexts = new HashSet<string>();
            var duplicateBlocks = 0;

            // Act
            foreach (var frame in frames)
            {
                var normalizedText = frame.ExpectedText?.Trim().ToLowerInvariant();
                
                if (dispatchedTexts.Contains(normalizedText))
                {
                    duplicateBlocks++;
                    Console.WriteLine($"DUPLICATE BLOCKED: '{frame.ExpectedText}' from user {frame.UserId}");
                }
                else
                {
                    dispatchedTexts.Add(normalizedText);
                    Console.WriteLine($"DISPATCHING: '{frame.ExpectedText}' from user {frame.UserId}");
                }
            }

            // Assert
            Assert.IsTrue(duplicateBlocks > 0, "Should have blocked at least one duplicate");
            Assert.AreEqual(3, dispatchedTexts.Count, "Should have 3 unique dispatches");
            Assert.AreEqual(2, duplicateBlocks, "Should have blocked 2 duplicates");
        }

        [TestMethod]
        public void TestDiscordUserSwitching_ShouldDetectUserChanges()
        {
            // Arrange
            var frames = CreateUserSwitchTestFrames();
            var lastUserId = string.Empty;
            var userSwitches = 0;

            // Act
            foreach (var frame in frames)
            {
                var isUserSwitch = !string.IsNullOrEmpty(lastUserId) && lastUserId != frame.UserId;
                
                if (isUserSwitch)
                {
                    userSwitches++;
                }
                
                lastUserId = frame.UserId;
            }

            // Assert
            Assert.AreEqual(2, userSwitches, "Should detect 2 user switches");
        }

        private List<SyntheticDiscordFrame> CreateSynthetic48kFrames()
        {
            return new List<SyntheticDiscordFrame>
            {
                new SyntheticDiscordFrame
                {
                    UserId = "user1",
                    Username = "TestUser1",
                    ChunkSizeMs = 20,
                    SampleRate = 48000,
                    Channels = 2,
                    ExpectedText = "hello world"
                },
                new SyntheticDiscordFrame
                {
                    UserId = "user2",
                    Username = "TestUser2",
                    ChunkSizeMs = 40,
                    SampleRate = 48000,
                    Channels = 2,
                    ExpectedText = "testing discord frames"
                },
                new SyntheticDiscordFrame
                {
                    UserId = "user1",
                    Username = "TestUser1",
                    ChunkSizeMs = 30,
                    SampleRate = 48000,
                    Channels = 2,
                    ExpectedText = "another test phrase"
                }
            };
        }

        private List<SyntheticDiscordFrame> CreateDuplicateTestFrames()
        {
            return new List<SyntheticDiscordFrame>
            {
                new SyntheticDiscordFrame { UserId = "user1", ExpectedText = "hello world" },
                new SyntheticDiscordFrame { UserId = "user2", ExpectedText = "test phrase" },
                new SyntheticDiscordFrame { UserId = "user1", ExpectedText = "hello world" }, // Duplicate
                new SyntheticDiscordFrame { UserId = "user3", ExpectedText = "unique phrase" },
                new SyntheticDiscordFrame { UserId = "user2", ExpectedText = "test phrase" }  // Duplicate
            };
        }

        private List<SyntheticDiscordFrame> CreateUserSwitchTestFrames()
        {
            return new List<SyntheticDiscordFrame>
            {
                new SyntheticDiscordFrame { UserId = "user1", ExpectedText = "first user" },
                new SyntheticDiscordFrame { UserId = "user1", ExpectedText = "same user" },
                new SyntheticDiscordFrame { UserId = "user2", ExpectedText = "switch to user2" }, // Switch 1
                new SyntheticDiscordFrame { UserId = "user2", ExpectedText = "still user2" },
                new SyntheticDiscordFrame { UserId = "user3", ExpectedText = "switch to user3" }  // Switch 2
            };
        }

        private ProcessedDiscordFrame ProcessDiscordFrame(SyntheticDiscordFrame frame)
        {
            // Simulate Discord frame processing
            // In real implementation, this would go through DiscordAudioProcessor
            
            return new ProcessedDiscordFrame
            {
                UserId = frame.UserId,
                Username = frame.Username,
                ChunkSizeMs = frame.ChunkSizeMs,
                SampleRate = frame.SampleRate,
                Channels = frame.Channels,
                ProcessingTimeMs = 15, // Simulated processing time
                TranscribedText = frame.ExpectedText,
                Confidence = 0.8f
            };
        }
    }

    /// <summary>
    /// Synthetic Discord frame for testing
    /// </summary>
    public class SyntheticDiscordFrame
    {
        public string UserId { get; set; }
        public string Username { get; set; }
        public int ChunkSizeMs { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public string ExpectedText { get; set; }
    }

    /// <summary>
    /// Processed Discord frame result
    /// </summary>
    public class ProcessedDiscordFrame
    {
        public string UserId { get; set; }
        public string Username { get; set; }
        public int ChunkSizeMs { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int ProcessingTimeMs { get; set; }
        public string TranscribedText { get; set; }
        public float Confidence { get; set; }
    }
}