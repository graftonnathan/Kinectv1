using System.Windows;

namespace Kinectv1.UI.Settings.Editors
{
    /// <summary>
    /// Test window to demonstrate GeneralSettingsView functionality
    /// </summary>
    public partial class GeneralSettingsTestWindow : Window
    {
        public GeneralSettingsTestWindow()
        {
            InitializeComponent();
            Title = "General Settings Test";
            Width = 600;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            
            // Add the GeneralSettingsView to the window
            var settingsView = new GeneralSettingsView();
            Content = settingsView;
        }
    }
}