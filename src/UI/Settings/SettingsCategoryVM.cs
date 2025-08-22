using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kinectv1.UI.Settings
{
    /// <summary>
    /// ViewModel for individual settings categories in the TreeView
    /// </summary>
    public class SettingsCategoryVM : INotifyPropertyChanged
    {
        private string _name;
        private SettingsEditorView _editorView;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public SettingsEditorView EditorView
        {
            get => _editorView;
            set
            {
                if (_editorView != value)
                {
                    _editorView = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Simple data class for the content displayed in the ContentPresenter
    /// This will be replaced with actual settings controls in future iterations
    /// </summary>
    public class SettingsEditorView
    {
        public string CategoryName { get; set; }
        public string Description { get; set; }
    }
}