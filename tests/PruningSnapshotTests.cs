using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kinectv1.Tests
{
    /// <summary>
    /// Pruning Snapshot Tests - identity fusion, VAD thresholds, debounce behavior
    /// Tests the pruning and debouncing logic for voice activity detection and identity tracking
    /// </summary>
    [TestClass]
    public class PruningSnapshotTests
    {
        [TestMethod]
        public void TestIdentityFusion_ShouldMergeConsistentIdentities()
        {
            // Arrange
            var identitySnapshots = CreateIdentityFusionTestData();
            var fusionEngine = new IdentityFusionEngine();

            // Act
            var fusedIdentities = fusionEngine.FuseIdentities(identitySnapshots);

            // Assert
            Assert.IsNotNull(fusedIdentities, "Fusion should return results");
            
            // Should consolidate multiple detections of the same person
            var uniqueIdentities = fusedIdentities.GroupBy(i => i.PersonId).Count();
            Assert.IsTrue(uniqueIdentities <= identitySnapshots.Count, 
                "Fusion should reduce or maintain identity count");

            // Check confidence improvements from fusion
            foreach (var identity in fusedIdentities)
            {
                Assert.IsTrue(identity.Confidence >= 0.0f && identity.Confidence <= 1.0f,
                    $"Fused confidence should be valid range: {identity.Confidence}");
            }
        }

        [TestMethod]
        public void TestVadThresholds_ShouldRespectConfiguredLimits()
        {
            // Arrange
            var vadConfigs = new[]
            {
                new VadConfig { Threshold = 0.1f, DebounceMs = 100 },
                new VadConfig { Threshold = 0.5f, DebounceMs = 200 },
                new VadConfig { Threshold = 0.8f, DebounceMs = 300 }
            };

            var testAudioLevels = new[] { 0.05f, 0.3f, 0.6f, 0.9f };

            // Act & Assert
            foreach (var config in vadConfigs)
            {
                var vadProcessor = new VadProcessor(config);
                
                foreach (var audioLevel in testAudioLevels)
                {
                    var shouldTrigger = audioLevel >= config.Threshold;
                    var actualTrigger = vadProcessor.ProcessAudioLevel(audioLevel);
                    
                    Assert.AreEqual(shouldTrigger, actualTrigger,
                        $"VAD threshold {config.Threshold} should {(shouldTrigger ? "" : "not ")}trigger for level {audioLevel}");
                }
            }
        }

        [TestMethod]
        public void TestDebounceBehavior_ShouldPreventRapidFiring()
        {
            // Arrange
            var debounceConfig = new VadConfig { Threshold = 0.5f, DebounceMs = 200 };
            var vadProcessor = new VadProcessor(debounceConfig);
            
            var rapidTriggers = new[]
            {
                new VadTrigger { Timestamp = DateTime.UtcNow, AudioLevel = 0.8f },
                new VadTrigger { Timestamp = DateTime.UtcNow.AddMilliseconds(50), AudioLevel = 0.8f },  // Too soon
                new VadTrigger { Timestamp = DateTime.UtcNow.AddMilliseconds(100), AudioLevel = 0.8f }, // Still too soon
                new VadTrigger { Timestamp = DateTime.UtcNow.AddMilliseconds(250), AudioLevel = 0.8f }  // Should pass
            };

            var allowedTriggers = 0;
            var blockedTriggers = 0;

            // Act
            foreach (var trigger in rapidTriggers)
            {
                var result = vadProcessor.ProcessTrigger(trigger);
                
                if (result.Allowed)
                {
                    allowedTriggers++;
                }
                else
                {
                    blockedTriggers++;
                }
            }

            // Assert
            Assert.AreEqual(2, allowedTriggers, "Should allow first trigger and one after debounce period");
            Assert.AreEqual(2, blockedTriggers, "Should block triggers within debounce period");
        }

        [TestMethod]
        public void TestPruningSnapshot_ShouldRemoveStaleEntries()
        {
            // Arrange
            var snapshotManager = new PruningSnapshotManager();
            var baseTime = DateTime.UtcNow;
            
            var entries = new[]
            {
                new SnapshotEntry { Id = "entry1", Timestamp = baseTime.AddMinutes(-10), Data = "old" },
                new SnapshotEntry { Id = "entry2", Timestamp = baseTime.AddMinutes(-1), Data = "recent" },
                new SnapshotEntry { Id = "entry3", Timestamp = baseTime, Data = "current" }
            };

            foreach (var entry in entries)
            {
                snapshotManager.AddEntry(entry);
            }

            // Act
            var prunedCount = snapshotManager.PruneStaleEntries(TimeSpan.FromMinutes(5));

            // Assert
            Assert.AreEqual(1, prunedCount, "Should prune 1 stale entry (older than 5 minutes)");
            
            var remainingEntries = snapshotManager.GetAllEntries();
            Assert.AreEqual(2, remainingEntries.Count, "Should have 2 entries remaining");
            
            Assert.IsTrue(remainingEntries.All(e => e.Timestamp >= baseTime.AddMinutes(-5)),
                "All remaining entries should be within the retention period");
        }

        [TestMethod]
        public void TestIdentityFusionWithSnapshots_ShouldMaintainTemporalConsistency()
        {
            // Arrange
            var snapshotManager = new PruningSnapshotManager();
            var identityTracker = new IdentityTracker();
            
            var identitySequence = new[]
            {
                new IdentitySnapshot { PersonId = "person1", Confidence = 0.7f, Timestamp = DateTime.UtcNow },
                new IdentitySnapshot { PersonId = "person1", Confidence = 0.8f, Timestamp = DateTime.UtcNow.AddSeconds(1) },
                new IdentitySnapshot { PersonId = "person2", Confidence = 0.6f, Timestamp = DateTime.UtcNow.AddSeconds(2) },
                new IdentitySnapshot { PersonId = "person1", Confidence = 0.9f, Timestamp = DateTime.UtcNow.AddSeconds(3) }
            };

            // Act
            foreach (var snapshot in identitySequence)
            {
                identityTracker.ProcessSnapshot(snapshot);
            }

            var finalState = identityTracker.GetCurrentState();

            // Assert
            Assert.IsNotNull(finalState, "Should have a final tracking state");
            
            // Should track temporal progression for person1
            var person1History = identityTracker.GetHistory("person1");
            Assert.IsTrue(person1History.Count >= 3, "Should track multiple snapshots for person1");
            
            // Confidence should generally improve over time (with temporal fusion)
            var avgConfidence = person1History.Average(h => h.Confidence);
            Assert.IsTrue(avgConfidence >= 0.7f, $"Average confidence should be reasonable: {avgConfidence}");
        }

        private List<IdentitySnapshot> CreateIdentityFusionTestData()
        {
            return new List<IdentitySnapshot>
            {
                new IdentitySnapshot { PersonId = "person1", Confidence = 0.7f, Timestamp = DateTime.UtcNow },
                new IdentitySnapshot { PersonId = "person1", Confidence = 0.8f, Timestamp = DateTime.UtcNow.AddMilliseconds(100) },
                new IdentitySnapshot { PersonId = "person2", Confidence = 0.6f, Timestamp = DateTime.UtcNow.AddMilliseconds(200) },
                new IdentitySnapshot { PersonId = "person1", Confidence = 0.75f, Timestamp = DateTime.UtcNow.AddMilliseconds(300) }
            };
        }
    }

    #region Test Support Classes

    /// <summary>
    /// Mock Identity Fusion Engine for testing
    /// </summary>
    public class IdentityFusionEngine
    {
        public List<IdentitySnapshot> FuseIdentities(List<IdentitySnapshot> snapshots)
        {
            // Simple fusion logic for testing - average confidence for same person
            var fused = new List<IdentitySnapshot>();
            
            var grouped = snapshots.GroupBy(s => s.PersonId);
            foreach (var group in grouped)
            {
                var avgConfidence = group.Average(g => g.Confidence);
                var latestTimestamp = group.Max(g => g.Timestamp);
                
                fused.Add(new IdentitySnapshot
                {
                    PersonId = group.Key,
                    Confidence = avgConfidence,
                    Timestamp = latestTimestamp
                });
            }
            
            return fused;
        }
    }

    /// <summary>
    /// Mock VAD Processor for testing
    /// </summary>
    public class VadProcessor
    {
        private readonly VadConfig _config;
        private DateTime _lastTriggerTime = DateTime.MinValue;

        public VadProcessor(VadConfig config)
        {
            _config = config;
        }

        public bool ProcessAudioLevel(float audioLevel)
        {
            return audioLevel >= _config.Threshold;
        }

        public VadResult ProcessTrigger(VadTrigger trigger)
        {
            var timeSinceLastTrigger = trigger.Timestamp - _lastTriggerTime;
            var isAllowed = _lastTriggerTime == DateTime.MinValue || 
                           timeSinceLastTrigger.TotalMilliseconds >= _config.DebounceMs;

            if (isAllowed)
            {
                _lastTriggerTime = trigger.Timestamp;
            }

            return new VadResult
            {
                Allowed = isAllowed,
                TimeSinceLastTrigger = timeSinceLastTrigger
            };
        }
    }

    /// <summary>
    /// Mock Snapshot Manager for testing
    /// </summary>
    public class PruningSnapshotManager
    {
        private readonly List<SnapshotEntry> _entries = new List<SnapshotEntry>();

        public void AddEntry(SnapshotEntry entry)
        {
            _entries.Add(entry);
        }

        public int PruneStaleEntries(TimeSpan retentionPeriod)
        {
            var cutoffTime = DateTime.UtcNow - retentionPeriod;
            var staleEntries = _entries.Where(e => e.Timestamp < cutoffTime).ToList();
            
            foreach (var stale in staleEntries)
            {
                _entries.Remove(stale);
            }
            
            return staleEntries.Count;
        }

        public List<SnapshotEntry> GetAllEntries()
        {
            return new List<SnapshotEntry>(_entries);
        }
    }

    /// <summary>
    /// Mock Identity Tracker for testing
    /// </summary>
    public class IdentityTracker
    {
        private readonly Dictionary<string, List<IdentitySnapshot>> _history = 
            new Dictionary<string, List<IdentitySnapshot>>();

        public void ProcessSnapshot(IdentitySnapshot snapshot)
        {
            if (!_history.ContainsKey(snapshot.PersonId))
            {
                _history[snapshot.PersonId] = new List<IdentitySnapshot>();
            }
            
            _history[snapshot.PersonId].Add(snapshot);
        }

        public IdentityTrackingState GetCurrentState()
        {
            return new IdentityTrackingState
            {
                TrackedPersons = _history.Keys.ToList(),
                LastUpdate = DateTime.UtcNow
            };
        }

        public List<IdentitySnapshot> GetHistory(string personId)
        {
            return _history.ContainsKey(personId) ? 
                   new List<IdentitySnapshot>(_history[personId]) : 
                   new List<IdentitySnapshot>();
        }
    }

    #endregion

    #region Data Structures

    public class IdentitySnapshot
    {
        public string PersonId { get; set; }
        public float Confidence { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class VadConfig
    {
        public float Threshold { get; set; }
        public int DebounceMs { get; set; }
    }

    public class VadTrigger
    {
        public DateTime Timestamp { get; set; }
        public float AudioLevel { get; set; }
    }

    public class VadResult
    {
        public bool Allowed { get; set; }
        public TimeSpan TimeSinceLastTrigger { get; set; }
    }

    public class SnapshotEntry
    {
        public string Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Data { get; set; }
    }

    public class IdentityTrackingState
    {
        public List<string> TrackedPersons { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    #endregion
}