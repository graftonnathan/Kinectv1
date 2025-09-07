using System;
using System.Runtime.InteropServices;

namespace Kinectv1
{
    public static class ConsoleManager
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private const int ATTACH_PARENT_PROCESS = -1;

        public static void ShowConsole()
        {
            try
            {
                // Try to attach to parent console first (if launched from console)
                if (!AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    // Allocate a new console if attach failed
                    AllocConsole();
                }
            }
            catch { }
        }

        public static void HideConsole()
        {
            try { FreeConsole(); } catch { }
        }
    }
}
