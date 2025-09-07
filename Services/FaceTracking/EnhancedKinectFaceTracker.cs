using System;
using System.Collections.Generic;

namespace Kinectv1
{
    public static class EnhancedKinectFaceTracker
    {
        public class EmotionInfo
        {
            public string PrimaryEmotion { get; set; } = "Neutral";
            public override string ToString() => PrimaryEmotion;
        }

        public class FaceTrackingInfo
        {
            public bool IsRecognized { get; set; }
            public string Name { get; set; }
            public float Confidence { get; set; }
            public ulong TrackingId { get; set; }
            public int Left { get; set; }
            public int Top { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public EmotionInfo Emotion { get; set; } = new EmotionInfo();
        }

        public static event Action<List<FaceTrackingInfo>> OnAllFacesDetected;
        public static void Start() { }
        public static void QueueLabel(string name) { }
        public static bool IsVideoWindowOpen() => false;
        public static void ShowVideoWindow() { }
        public static void HideVideoWindow() { }
    }
}
