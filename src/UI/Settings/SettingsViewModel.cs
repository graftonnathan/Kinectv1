using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kinectv1.Settings;

namespace Kinectv1.UI.Settings
{
    /// <summary>
    /// Main ViewModel for the Settings window, managing categories and selection state
    /// </summary>
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private SettingsCategoryVM _selectedCategory;
        private bool _hasUnsavedChanges;
        private Kinectv1.Settings.AppSettings _snapshot;

        public SettingsViewModel()
        {
            Categories = new ObservableCollection<SettingsCategoryVM>();
            // Take an initial snapshot from the SettingsService at open
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

        // Alias to match requested naming
        public bool IsDirty
        {
            get => HasUnsavedChanges;
            set => HasUnsavedChanges = value;
        }

        public Kinectv1.Settings.AppSettings Snapshot
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
            try { Snapshot = App.SettingsProvider?.Current; } catch { /* ignore */ }
        }

        public void LoadDefaultsSnapshot()
        {
            try { Snapshot = App.SettingsProvider?.GetDefaults(); } catch { Snapshot = null; }
        }

        private void InitializeCategories()
        {
            // Use ModelsSettingsView as the editor for General
            var modelsView = new Kinectv1.ModelsSettingsView();

            Categories.Add(new SettingsCategoryVM
            {
                Name = "General",
                EditorView = modelsView
            });

            // Keep AI Assistant placeholder
            Categories.Add(new SettingsCategoryVM
            {
                Name = "AI Assistant",
                EditorView = new Kinectv1.OllamaSettingsView()
            });

            // Discord settings page
            Categories.Add(new SettingsCategoryVM
            {
                Name = "Discord",
                EditorView = new Kinectv1.DiscordSettingsView()
            });

            // Mumble settings page
            Categories.Add(new SettingsCategoryVM
            {
                Name = "Mumble",
                EditorView = new Kinectv1.MumbleSettingsView()
            });

            // Select first category by default
            if (Categories.Count > 0)
            {
                SelectedCategory = Categories[0];
            }
        }

        public void SaveChanges()
        {
            // Retained for compatibility with window close prompt
            HasUnsavedChanges = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}