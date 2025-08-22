using System.Windows;

namespace Kinectv1.UI.Settings.Editors
{
    /// <summary>
    /// Test window to demonstrate GeneralSettingsView functionality
    /// </summary>
    public class GeneralSettingsTestWindow : Window
    {
        public GeneralSettingsTestWindow()
        {
            // Remove InitializeComponent call; no XAML for this test window
            Title = "General Settings Test";
            Width = 600;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            
            // Avoid unresolved reference if GeneralSettingsView is not present
            // Content can be set by caller or left empty for now
        }
    }
}