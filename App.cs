// App.cs - Minimal App class for headless operation
// Provides static access to settings without WPF dependencies

using Kinectv1.Settings;

namespace Kinectv1
{
    /// <summary>
    /// Minimal Application class for headless mode.
    /// Replaces the WPF App class for console/ Linux operation.
    /// </summary>
    public static class App
    {
        public static SettingsService SettingsProvider { get; set; }
    }
}
