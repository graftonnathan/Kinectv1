using System;
using System.Windows;
using System.Windows.Controls;

namespace Kinectv1
{
    public partial class OllamaSettingsView : UserControl
    {
        public OllamaSettingsView()
        {
            InitializeComponent();
        }

        private void RefreshModelsButton_Click(object sender, RoutedEventArgs e) { }
        private void BrowseHistoryPathButton_Click(object sender, RoutedEventArgs e) { }
        private void BrowseSystemPromptButton_Click(object sender, RoutedEventArgs e) { }
        private void SaveButton_Click(object sender, RoutedEventArgs e) { }
    }
}
