using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kinectv1.Settings;

namespace Kinectv1.UI.Settings
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private SettingsCategoryVM _selectedCategory;
        private bool _hasUnsavedChanges;
        private AppSettings _snapshot;

        public SettingsViewModel()
        {
            Categories = new ObservableCollection<SettingsCategoryVM>();
            try { _snapshot = App.SettingsProvider?.Current; } catch { _snapshot = null; }
            InitializeCategories();
        }

        public ObservableCollection<SettingsCategoryVM> Categories { get; private set; }

        public SettingsCategoryVM SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (_selectedCategory != value)
                {
                    _selectedCategory = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            set
            {
                if (_hasUnsavedChanges != value)
                {
                    _hasUnsavedChanges = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsDirty));
                }
            }
        }

        public bool IsDirty
        {
            get => HasUnsavedChanges;
            set => HasUnsavedChanges = value;
        }

        public AppSettings Snapshot
        {
            get => _snapshot;
            private set
            {
                if (!Equals(_snapshot, value))
                {
                    _snapshot = value;
                    OnPropertyChanged();
                }
            }
        }

        public void RefreshSnapshotFromService()
        {
            try { Snapshot = App.SettingsProvider?.Current; } catch { }
        }

        public void LoadDefaultsSnapshot()
        {
            try { Snapshot = App.SettingsProvider?.GetDefaults(); } catch { Snapshot = null; }
        }

        private void InitializeCategories()
        {
            var modelsView = new ModelsSettingsView();

            Categories.Add(new SettingsCategoryVM
            {
                Name = "General",
                EditorView = modelsView
            });

            Categories.Add(new SettingsCategoryVM
            {
                Name = "AI Assistant",
                EditorView = new OllamaSettingsView()
            });

            Categories.Add(new SettingsCategoryVM
            {
                Name = "Discord",
                EditorView = new DiscordSettingsView()
            });

            Categories.Add(new SettingsCategoryVM
            {
                Name = "WebRTC",
                EditorView = new WebRtcSettingsView()
            });

            Categories.Add(new SettingsCategoryVM
            {
                Name = "Memory",
                EditorView = new MemorySettingsView()
            });

            if (Categories.Count > 0)
            {
                SelectedCategory = Categories[0];
            }
        }

        public void SaveChanges()
        {
            HasUnsavedChanges = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
