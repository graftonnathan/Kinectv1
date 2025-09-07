using System;

namespace Kinectv1
{
    public static class IdentityFusionTracker
    {
        public static event Action<ulong, string, float> OnIdentityFused;
        public static void TestFusion() { }
        public static string GetFusionStatus() => string.Empty;
    }
}
