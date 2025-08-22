using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kinectv1.UI.Settings
{
    /// <summary>
    /// Main ViewModel for the Settings window, managing categories and selection state
    /// </summary>
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private SettingsCategoryVM _selectedCategory;
        private bool _hasUnsavedChanges;

        public SettingsViewModel()
        {
            Categories = new ObservableCollection<SettingsCategoryVM>();
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
                }
            }
        }

        private void InitializeCategories()
        {
            // Add placeholder categories for testing
            Categories.Add(new SettingsCategoryVM
            {
                Name = "General",
                EditorView = new SettingsEditorView 
                { 
                    CategoryName = "General Settings",
                    Description = "Configure general application settings including theme, startup behavior, and window preferences."
                }
            });

            Categories.Add(new SettingsCategoryVM
            {
                Name = "Audio",
                EditorView = new SettingsEditorView 
                { 
                    CategoryName = "Audio Settings",
                    Description = "Configure microphone input, Discord integration, and TTS output settings."
                }
            });

            Categories.Add(new SettingsCategoryVM
            {
                Name = "Recognition",
                EditorView = new SettingsEditorView 
                { 
                    CategoryName = "Voice Recognition",
                    Description = "Configure Vosk ASR models, recognition thresholds, and speaker identification settings."
                }
            });

            Categories.Add(new SettingsCategoryVM
            {
                Name = "AI Assistant",
                EditorView = new SettingsEditorView 
                { 
                    CategoryName = "AI Assistant Settings",
                    Description = "Configure Ollama model selection, conversation context, and response behavior."
                }
            });

            // Select first category by default
            if (Categories.Count > 0)
            {
                SelectedCategory = Categories[0];
            }
        }

        public void SaveChanges()
        {
            // TODO: Implement actual settings persistence
            HasUnsavedChanges = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}