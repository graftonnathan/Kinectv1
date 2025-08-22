# General Settings Editor

This directory contains the implementation of the GeneralSettingsView, a WPF user control that provides a unified interface for editing general application settings.

## Files

### GeneralSettingsView.xaml
- Main XAML file defining the UI layout
- Contains three main setting controls:
  - **Theme Dropdown**: Light/Dark theme selection
  - **Language Dropdown**: Application language selection (en-US, es-ES, fr-FR, de-DE, ja-JP)
  - **Logging Level Dropdown**: Telemetry verbosity control (Debug, Info, Warning, Error)
- Features dirty state indicator and action buttons (Apply, Save, Reset)
- Styled to match the main application theme

### GeneralSettingsView.xaml.cs
- Code-behind file for the UserControl
- Minimal implementation that sets up the DataContext with GeneralSettingsVM
- Includes BooleanToVisibilityConverter for XAML binding

### GeneralSettingsVM.cs
- View Model implementing INotifyPropertyChanged
- Manages the state of all general settings:
  - IsDarkMode (bool) - binds to Theme dropdown
  - SelectedLanguage (string) - binds to Language dropdown  
  - SelectedTelemetryLevel (TelemetryLevel) - binds to Logging Level dropdown
  - IsDirty (bool) - tracks if settings have been modified
- Provides commands for Apply, Save, and Reset operations
- Handles validation and persistence through AppSettings

### GeneralSettingsTestWindow.cs
- Test window for demonstrating the GeneralSettingsView
- Can be used for testing the control in isolation

## Integration

The GeneralSettingsView integrates with the existing AppSettings class:

### New AppSettings Methods Added:
- `LoadLanguage()` / `SaveLanguage(string)` - Language setting persistence
- `LoadTelemetryLevel()` / `SaveTelemetryLevel(TelemetryLevel)` - Logging level persistence
- Updated `InitializeSettingsOnStartup()` to display the new settings

### Existing AppSettings Methods Used:
- `LoadDarkMode()` / `SaveDarkMode(bool)` - Theme setting persistence

## Usage

```csharp
// Create and show the settings view
var settingsView = new GeneralSettingsView();
// Add to existing window or show in dialog

// Or use the test window
var testWindow = new GeneralSettingsTestWindow();
testWindow.Show();
```

## Features

1. **Data Binding**: All controls are bound to the view model with two-way binding
2. **Validation**: Settings are validated before saving
3. **Dirty State Tracking**: Visual indicator when settings are modified but not saved
4. **Command Pattern**: Apply, Save, and Reset operations use ICommand
5. **Theme Integration**: Styled to match the application's dark/light theme
6. **Error Handling**: Robust error handling with console logging
7. **Responsive Design**: Scrollable layout adapts to different window sizes

## Dependencies

- WPF (.NET Framework 4.8.1)
- Existing AppSettings class
- TelemetryLevel enum (from Telemetry.cs)
- Application theme resources (defined in MainWindow.xaml)

## Future Enhancements

- Localization support for UI text
- Setting validation with user feedback
- Settings export/import functionality
- Advanced telemetry configuration options