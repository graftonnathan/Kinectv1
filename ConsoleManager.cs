using System;
using System.Runtime.InteropServices;

namespace Kinectv1
{
    /// <summary>
    /// Console manager for WPF applications to show debug output
    /// </summary>
    public static class ConsoleManager
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        /// <summary>
        /// Allocate a console window for the application
        /// </summary>
        public static void ShowConsole()
        {
            try
            {
                // Check if console is already allocated
                if (GetConsoleWindow() == IntPtr.Zero)
                {
                    AllocConsole();
                    Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                    Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                    Console.SetIn(new System.IO.StreamReader(Console.OpenStandardInput()));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to allocate console: {ex.Message}");
            }
        }

        /// <summary>
        /// Free the console window
        /// </summary>
        public static void HideConsole()
        {
            try
            {
                if (GetConsoleWindow() != IntPtr.Zero)
                {
                    FreeConsole();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to free console: {ex.Message}");
            }
        }
    }
}

