using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Kinectv1.Discord
{
    /// <summary>
    /// Helper class to ensure Discord.Net native audio libraries are properly loaded
    /// Enhanced with native verification using DLL imports
    /// </summary>
    public static class DiscordNativeLoader
    {
        private static bool _librariesLoaded = false;
        private static readonly object _lock = new object();

        // Native DLL imports for verification
        [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr opus_get_version_string();

        [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sodium_init();

        /// <summary>
        /// Ensure native libraries are loaded for Discord.Net voice functionality
        /// </summary>
        public static bool LoadNativeLibraries()
        {
            lock (_lock)
            {
                if (_librariesLoaded)
                    return true;

                try
                {
                    Console.WriteLine("?? Loading Discord.Net native audio libraries...");
                    Console.WriteLine($"?? Process bitness: {(Environment.Is64BitProcess ? "x64" : "x86")}");
                    
                    // Get the application directory
                    var appDir = AppDomain.CurrentDomain.BaseDirectory;
                    // Go up three levels to reach the project root when running from bin/Debug/net481
                    var projectRoot = Directory.GetParent(appDir)?.Parent?.Parent?.Parent?.FullName ?? appDir;
                    
                    // Define all possible library locations in order of preference
                    var libraryLocations = new[]
                    {
                        // 1. Main application directory (runtime location)
                        new { Name = "Application Directory", OpusPath = Path.Combine(appDir, "opus.dll"), SodiumPath = Path.Combine(appDir, "libsodium.dll") },
                        
                        // 2. Project libs directory (new preferred location for fresh copies)
                        new { Name = "Project libs Directory", OpusPath = Path.Combine(projectRoot, "libs", "opus.dll"), SodiumPath = Path.Combine(projectRoot, "libs", "libsodium.dll") },
                        
                        // 3. Runtimes directory (NuGet standard location)
                        new { Name = "Runtimes Directory", OpusPath = Path.Combine(appDir, "runtimes", "win-x64", "native", "opus.dll"), SodiumPath = Path.Combine(appDir, "runtimes", "win-x64", "native", "libsodium.dll") },
                        
                        // 4. Old lib directory (fallback for old installations)
                        new { Name = "Legacy lib Directory", OpusPath = Path.Combine(projectRoot, "lib", "libopus.dll"), SodiumPath = Path.Combine(projectRoot, "lib", "libsodium.dll") },

                        // 5. User-provided custom folder: lib\\discord (common mistake) - handle both names
                        new { Name = "Custom lib\\discord Directory", OpusPath = Path.Combine(projectRoot, "lib", "discord", "opus.dll"), SodiumPath = Path.Combine(projectRoot, "lib", "discord", "libsodium.dll") },
                        new { Name = "Custom lib\\discord Directory (libopus)", OpusPath = Path.Combine(projectRoot, "lib", "discord", "libopus.dll"), SodiumPath = Path.Combine(projectRoot, "lib", "discord", "libsodium.dll") }
                    };
                    
                    Console.WriteLine($"?? Searching for native libraries in {libraryLocations.Length} locations...");
                    
                    bool opusLoaded = false;
                    bool sodiumLoaded = false;
                    string opusSource = "";
                    string sodiumSource = "";
                    
                    // Try each location in order
                    foreach (var location in libraryLocations)
                    {
                        Console.WriteLine($"?? Checking {location.Name}:");
                        Console.WriteLine($"   opus.dll: {(File.Exists(location.OpusPath) ? "? Found" : "? Missing")} at {location.OpusPath}");
                        Console.WriteLine($"   libsodium.dll: {(File.Exists(location.SodiumPath) ? "? Found" : "? Missing")} at {location.SodiumPath}");
                        
                        // Try to load opus if not already loaded
                        if (!opusLoaded && File.Exists(location.OpusPath))
                        {
                            try
                            {
                                LoadLibrary(location.OpusPath);
                                opusLoaded = true;
                                opusSource = location.Name;
                                Console.WriteLine($"? opus.dll loaded successfully from {location.Name}");
                                
                                // Verify file size for corruption check
                                var opusInfo = new FileInfo(location.OpusPath);
                                Console.WriteLine($"?? opus.dll size: {opusInfo.Length:N0} bytes");
                                
                                if (opusInfo.Length < 400000)
                                {
                                    Console.WriteLine($"?? WARNING: opus.dll size ({opusInfo.Length:N0} bytes) seems small - possible corruption");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"? Failed to load opus.dll from {location.Name}: {ex.Message}");
                            }
                        }
                        
                        // Try to load libsodium if not already loaded
                        if (!sodiumLoaded && File.Exists(location.SodiumPath))
                        {
                            try
                            {
                                LoadLibrary(location.SodiumPath);
                                sodiumLoaded = true;
                                sodiumSource = location.Name;
                                Console.WriteLine($"? libsodium.dll loaded successfully from {location.Name}");
                                
                                // Verify file size for corruption check
                                var sodiumInfo = new FileInfo(location.SodiumPath);
                                Console.WriteLine($"?? libsodium.dll size: {sodiumInfo.Length:N0} bytes");
                                
                                if (sodiumInfo.Length < 400000)
                                {
                                    Console.WriteLine($"?? WARNING: libsodium.dll size ({sodiumInfo.Length:N0} bytes) seems small - possible corruption");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"? Failed to load libsodium.dll from {location.Name}: {ex.Message}");
                            }
                        }
                        
                        // If both libraries are loaded, we can stop searching
                        if (opusLoaded && sodiumLoaded)
                        {
                            break;
                        }
                    }
                    
                    _librariesLoaded = opusLoaded && sodiumLoaded;
                    
                    Console.WriteLine("");
                    if (_librariesLoaded)
                    {
                        Console.WriteLine("?? All Discord.Net native libraries loaded successfully!");
                        Console.WriteLine($"?? opus.dll source: {opusSource}");
                        Console.WriteLine($"?? libsodium.dll source: {sodiumSource}");
                        
                        // Enhanced verification using DLL imports
                        PerformNativeVerification();
                    }
                    else
                    {
                        Console.WriteLine("? Some Discord.Net native libraries are missing or failed to load");
                        Console.WriteLine("");
                        Console.WriteLine("?? To fix this issue:");
                        Console.WriteLine("   1. Run the download script: .\\download-discord-natives.ps1");
                        Console.WriteLine("   2. This will download fresh libraries from Discord.Net repository");
                        Console.WriteLine("   3. Libraries will be placed in multiple locations for compatibility");
                        Console.WriteLine("   4. Restart the application after running the script");
                        Console.WriteLine("");
                        
                        if (!opusLoaded)
                        {
                            Console.WriteLine("? Missing opus.dll - required for Discord voice codec");
                        }
                        if (!sodiumLoaded)
                        {
                            Console.WriteLine("? Missing libsodium.dll - required for Discord voice encryption");
                        }
                        
                        Console.WriteLine("");
                        Console.WriteLine("?? Manual installation alternative:");
                        Console.WriteLine("   Download from: https://github.com/discord-net/Discord.Net/tree/dev/voice-natives");
                        Console.WriteLine("   Place files in: libs\\ and bin\\Debug\\net481\\");
                        Console.WriteLine("   Or place in: lib\\discord\\ (we now search this too)");
                    }
                    
                    return _librariesLoaded;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"? Critical error loading Discord.Net native libraries: {ex.Message}");
                    Console.WriteLine($"?? Stack trace: {ex.StackTrace}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Perform enhanced native library verification using DLL imports
        /// </summary>
        private static void PerformNativeVerification()
        {
            try
            {
                Console.WriteLine("?? Performing native library verification...");
                
                // Test libsodium
                var sodiumResult = sodium_init(); // >=0 means loaded
                Console.WriteLine($"?? libsodium init result: {sodiumResult} {(sodiumResult >= 0 ? "? SUCCESS" : "? FAILED")}");
                
                // Test opus
                var versionPtr = opus_get_version_string();
                var version = Marshal.PtrToStringAnsi(versionPtr);
                Console.WriteLine($"?? opus version: {version ?? "? FAILED"}");
                
                if (sodiumResult >= 0 && !string.IsNullOrEmpty(version))
                {
                    Console.WriteLine("?? Native library verification PASSED - Voice functionality should work!");
                }
                else
                {
                    Console.WriteLine("? Native library verification FAILED - Voice may not work properly");
                    _librariesLoaded = false; // Mark as failed if verification fails
                }
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine($"? Native verification failed - DLL not found: {ex.Message}");
                _librariesLoaded = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"? Native verification error: {ex.Message}");
                _librariesLoaded = false;
            }
        }

        /// <summary>
        /// Get the status of native library loading
        /// </summary>
        public static bool AreLibrariesLoaded => _librariesLoaded;

        /// <summary>
        /// Force reload of native libraries (useful after downloading fresh copies)
        /// </summary>
        public static bool ReloadNativeLibraries()
        {
            _librariesLoaded = false;
            return LoadNativeLibraries();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);
    }
}