using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Kinectv1.UI.Settings
{
    /// <summary>
    /// Settings window with TreeView navigation and ContentPresenter for settings panels
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private SettingsViewModel _viewModel;

        public SettingsWindow()
        {
            InitializeComponent();
            _viewModel = new SettingsViewModel();
            DataContext = _viewModel;
        }

        private void CategoriesTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is SettingsCategoryVM category)
            {
                _viewModel.SelectedCategory = category;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Handle any unsaved changes if needed
            if (_viewModel?.HasUnsavedChanges == true)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Do you want to save before closing?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                switch (result)
                {
                    case MessageBoxResult.Yes:
                        _viewModel.SaveChanges();
                        break;
                    case MessageBoxResult.Cancel:
                        e.Cancel = true;
                        return;
                }
            }

            base.OnClosing(e);
        }
    }
}