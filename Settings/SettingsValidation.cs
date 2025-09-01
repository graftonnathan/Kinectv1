// Settings/SettingsValidation.cs
using System;

namespace Kinectv1.Settings
{
    public static class SettingsValidation
    {
        public static void ValidateOrThrow(AppSettings settings)
        {
            // Delegate to SettingsService's validator for a single source of truth
            SettingsService.ValidateOrThrow(settings);
        }
    }
}
