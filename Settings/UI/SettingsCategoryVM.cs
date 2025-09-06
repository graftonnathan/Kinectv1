using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Kinectv1.UI.Settings
{
    public class SettingsCategoryVM : INotifyPropertyChanged
    {
        private string _name;
        private FrameworkElement _editorView;

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

        public FrameworkElement EditorView
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

    public class SettingsEditorView
    {
        public string CategoryName { get; set; }
        public string Description { get; set; }
    }
}
