using System;

namespace Kinectv1
{
    internal static class KokoroTtsService
    {
        public static bool RecreateSessionFromSettings()
        {
            try { return TtsPlaybackController.RecreateSessionFromSettings(); } catch { return false; }
        }
        public static bool IsUsingGpu()
        {
            try { return TtsPlaybackController.IsUsingGpu(); } catch { return false; }
        }
        public static int GetSampleRate()
        {
            try { return TtsPlaybackController.GetSampleRate(); } catch { return 22050; }
        }
    }
}
