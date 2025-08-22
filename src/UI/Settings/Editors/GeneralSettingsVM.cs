using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Kinectv1.UI.Settings.Editors
{
    public class GeneralSettingsVM : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isDarkMode;
        private string _selectedLanguage;
        private TelemetryLevel _selectedTelemetryLevel;
        private bool _isDirty;

        // Available options
        public List<ThemeOption> ThemeOptions { get; }
        public List<LanguageOption> LanguageOptions { get; }
        public List<TelemetryLevelOption> TelemetryLevelOptions { get; }

        // Commands
        public ICommand SaveCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand ResetCommand { get; }

        public GeneralSettingsVM()
        {
            // Initialize theme options
            ThemeOptions = new List<ThemeOption>
            {
                new ThemeOption { DisplayName = "Light", Value = false },
                new ThemeOption { DisplayName = "Dark", Value = true }
            };

            // Initialize language options
            LanguageOptions = new List<LanguageOption>
            {
                new LanguageOption { DisplayName = "English (US)", Code = "en-US" },
                new LanguageOption { DisplayName = "Spanish (Spain)", Code = "es-ES" },
                new LanguageOption { DisplayName = "French (France)", Code = "fr-FR" },
                new LanguageOption { DisplayName = "German (Germany)", Code = "de-DE" },
                new LanguageOption { DisplayName = "Japanese", Code = "ja-JP" }
            };

            // Initialize telemetry level options
            TelemetryLevelOptions = new List<TelemetryLevelOption>
            {
                new TelemetryLevelOption { DisplayName = "Debug", Level = TelemetryLevel.Debug },
                new TelemetryLevelOption { DisplayName = "Info", Level = TelemetryLevel.Info },
                new TelemetryLevelOption { DisplayName = "Warning", Level = TelemetryLevel.Warning },
                new TelemetryLevelOption { DisplayName = "Error", Level = TelemetryLevel.Error }
            };

            // Initialize commands
            SaveCommand = new RelayCommand(Save, CanSave);
            ApplyCommand = new RelayCommand(Apply, CanApply);
            ResetCommand = new RelayCommand(Reset);

            // Load current settings
            LoadSettings();
        }

        #region Properties

        public bool IsDarkMode
        {
            get => _isDarkMode;
            set
            {
                if (SetProperty(ref _isDarkMode, value))
                {
                    MarkDirty();
                }
            }
        }

        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (SetProperty(ref _selectedLanguage, value))
                {
                    MarkDirty();
                }
            }
        }

        public TelemetryLevel SelectedTelemetryLevel
        {
            get => _selectedTelemetryLevel;
            set
            {
                if (SetProperty(ref _selectedTelemetryLevel, value))
                {
                    MarkDirty();
                }
            }
        }

        public bool IsDirty
        {
            get => _isDirty;
            private set => SetProperty(ref _isDirty, value);
        }

        #endregion

        #region Methods

        private void LoadSettings()
        {
            try
            {
                _isDarkMode = AppSettings.LoadDarkMode();
                _selectedLanguage = AppSettings.LoadLanguage();
                _selectedTelemetryLevel = AppSettings.LoadTelemetryLevel();
                
                // Notify all properties changed
                OnPropertyChanged(nameof(IsDarkMode));
                OnPropertyChanged(nameof(SelectedLanguage));
                OnPropertyChanged(nameof(SelectedTelemetryLevel));
                
                // Clear dirty state after loading
                IsDirty = false;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"ERROR: Failed to load general settings: {ex.Message}");
            }
        }

        private void Save()
        {
            Apply();
            // Additional save logic if needed
        }

        private void Apply()
        {
            try
            {
                AppSettings.SaveDarkMode(IsDarkMode);
                AppSettings.SaveLanguage(SelectedLanguage);
                AppSettings.SaveTelemetryLevel(SelectedTelemetryLevel);
                
                IsDirty = false;
                System.Console.WriteLine("General settings applied successfully");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"ERROR: Failed to apply general settings: {ex.Message}");
            }
        }

        private void Reset()
        {
            LoadSettings();
        }

        private bool CanSave() => IsDirty;
        private bool CanApply() => IsDirty;

        private void MarkDirty()
        {
            IsDirty = true;
        }

        #endregion

        #region INotifyPropertyChanged Implementation

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        #endregion
    }

    #region Helper Classes

    public class ThemeOption
    {
        public string DisplayName { get; set; }
        public bool Value { get; set; }
    }

    public class LanguageOption
    {
        public string DisplayName { get; set; }
        public string Code { get; set; }
    }

    public class TelemetryLevelOption
    {
        public string DisplayName { get; set; }
        public TelemetryLevel Level { get; set; }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object parameter) => _execute();
    }

    #endregion
}