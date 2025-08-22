# Settings UI UX Plan

## Overview

This document outlines the UX design for a comprehensive Settings window for the Kinectv1 application. The Settings window uses a two-panel layout with hierarchical navigation on the left and data-bound configuration forms on the right.

## Layout

### Two-Panel Design
```
┌─────────────────────────────────────────────────────────────┐
│ Settings                                           [X]       │
├─────────────────────────────────────────────────────────────┤
│ ┌─TreeView────┐ │ ┌─Detail Panel──────────────────────────┐ │
│ │ ▼ General   │ │ │ [Selected Category Title]             │ │
│ │   Scenario  │ │ │                                       │ │
│ │   Theme     │ │ │ [Form Controls for Selected Category] │ │
│ │ ▼ Audio     │ │ │                                       │ │
│ │   Devices   │ │ │                                       │ │
│ │   Voice     │ │ │                                       │ │
│ │   Volume    │ │ │                                       │ │
│ │ ▼ Paths     │ │ │                                       │ │
│ │   Models    │ │ │                                       │ │
│ │   Data      │ │ │                                       │ │
│ │ ▼ Models    │ │ │                                       │ │
│ │   TTS       │ │ │                                       │ │
│ │   STT/ASR   │ │ │                                       │ │
│ │   AI/LLM    │ │ │                                       │ │
│ │ ▼ Discord   │ │ │                                       │ │
│ │   Bot       │ │ │                                       │ │
│ │   Voice     │ │ │                                       │ │
│ │ ▼ Advanced  │ │ │                                       │ │
│ │   Thresholds│ │ │                                       │ │
│ │   Telemetry │ │ │                                       │ │
│ │   Fusion    │ │ │                                       │ │
│ └─────────────┘ │ └───────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────┤
│        [Restore Defaults] [Cancel] [Apply] [Save & Close]   │
└─────────────────────────────────────────────────────────────┘
```

### Dimensions
- **Window Size**: 1000x700px (minimum 800x600px)
- **TreeView Width**: 250px (resizable with splitter)
- **Detail Panel**: Remaining width (minimum 550px)
- **Button Bar Height**: 50px

## Categories & Settings

### 1. General
**Purpose**: Core application behavior and appearance

#### Scenario
- **App Scenario** (ComboBox): Local | Discord | Kiosk
- **Description** (ReadOnly TextBlock): Dynamic description based on selection
- **Apply Scenario Defaults** (Button): Applies preset configurations

#### Theme
- **Dark Mode** (ToggleButton): Enable/disable dark theme
- **Window Settings** (Group):
  - Remember window position and size (CheckBox)
  - Start maximized (CheckBox)

### 2. Audio
**Purpose**: Audio input/output configuration and voice processing

#### Devices
- **STT Input Device** (ComboBox): Microphone selection for speech recognition
- **TTS Output Device** (ComboBox): Speaker selection for text-to-speech
- **Audio Input Mode** (RadioButtons): LocalMic | DiscordVoice | SystemLoopback
- **Test Audio Devices** (Button): Run audio device diagnostics

#### Voice Processing  
- **Voice Threshold** (Slider + TextBox): 0.0-1.0, default 0.5
- **Voice Activity Threshold** (Slider + TextBox): 0-1000, default 300
- **Discord Voice Activity Threshold** (Slider + TextBox): 0-1000, default 500
- **Voice Confidence Threshold** (Slider + TextBox): 0.0-1.0, default 0.7
- **High Confidence Threshold** (Slider + TextBox): 0.0-1.0, default 0.9
- **Confidence Buffer Size** (NumericUpDown): 5-50, default 10
- **Enable Confidence Logging** (CheckBox)

#### Volume
- **Local TTS Volume** (Slider): 0-100%, default 80%
- **Discord TTS Volume** (Slider): 0-100%, default 60%
- **Barge-in Enabled** (CheckBox): Allow ASR to interrupt TTS

#### VAD (Voice Activity Detection)
- **Silence Timeout** (NumericUpDown): 500-5000ms, default 2000ms
- **Debounce Timeout** (NumericUpDown): 50-1000ms, default 200ms

### 3. Paths
**Purpose**: File and folder locations for models and data

#### Models
- **TTS Model Folder** (FolderBrowser): Base folder for TTS models
- **TTS Model Path** (FileBrowser): Specific TTS model file
- **TTS Vocoder Path** (FileBrowser): Vocoder model file
- **TTS Symbols Path** (FileBrowser): Phoneme symbols file
- **TTS CMU Dict Path** (FileBrowser): Pronunciation dictionary
- **STT Model Path** (FileBrowser): Speech recognition model
- **Speaker Embedding Model** (FileBrowser): Speaker identification model

#### Data
- **Conversation History Path** (FileBrowser): Chat log storage location
- **System Prompt Path** (FileBrowser): AI system prompt file
- **Telemetry File** (FileBrowser): Log file for telemetry data

### 4. Models
**Purpose**: AI model configuration and GPU settings

#### TTS (Text-to-Speech)
- **TTS Enabled** (CheckBox): Enable/disable TTS globally
- **TTS Speaker** (ComboBox): Voice selection for TTS output
- **Use GPU** (CheckBox): Enable GPU acceleration
- **Prefer DirectML** (CheckBox): Use DirectML over CUDA
- **GPU Device ID** (NumericUpDown): Specific GPU selection (0-based)

#### STT/ASR (Speech Recognition)
- **Face Recognition Threshold** (Slider + TextBox): 0.0-1.0, default 0.6
- **Speaker Match Min Score** (Slider + TextBox): 0.0-1.0, default 0.7

#### AI/LLM (Ollama Integration)
- **Ollama Enabled** (CheckBox): Enable Ollama AI responses
- **Ollama Model** (ComboBox + TextBox): Model selection with custom entry
- **Memory Enabled** (CheckBox): Maintain conversation context
- **Max Messages Per Speaker** (NumericUpDown): 5-100, default 20
- **Max System Messages** (NumericUpDown): 1-50, default 10
- **Conversation Timeout** (NumericUpDown): 1-120 minutes, default 30

### 5. Discord
**Purpose**: Discord bot integration and voice channel settings

#### Bot Configuration
- **Discord Bot Enabled** (CheckBox): Enable/disable Discord integration
- **Bot Token** (PasswordBox): Discord bot authentication token
- **Command Prefix** (TextBox): Bot command prefix (default "!")
- **Auto-join Voice** (CheckBox): Automatically join voice channels
- **Test Connection** (Button): Verify bot token and connection

#### Voice Channel
- **Discord Voice Activity Threshold** (Slider + TextBox): Specific threshold for Discord
- **Discord TTS Volume** (Slider): Volume for Discord TTS output

### 6. Advanced
**Purpose**: Expert-level settings and diagnostic features

#### Identity Fusion
- **Voice Weight** (Slider + TextBox): 0.0-1.0, weight for voice recognition
- **Face Weight** (Slider + TextBox): 0.0-1.0, weight for face recognition  
- **Unknown Threshold** (Slider + TextBox): 0.0-1.0, threshold for unknown persons
- **Decay Half-life** (NumericUpDown): 1000-30000ms, default 5000ms

#### Telemetry & Diagnostics
- **Telemetry Enabled** (CheckBox): Enable usage data collection
- **Sampling Percentage** (Slider + TextBox): 0-100%, default 10%
- **Telemetry File Path** (FileBrowser): Log file location
- **View Diagnostics** (Button): Open diagnostics window

## Navigation & Interaction

### TreeView Behavior
- **Single Selection**: Only one category can be selected at a time
- **Expand/Collapse**: Categories with subcategories show/hide children
- **Keyboard Navigation**: 
  - Arrow keys navigate tree
  - Enter/Space selects category
  - Tab moves focus to detail panel
- **Visual States**:
  - Selected: Highlighted background
  - Focused: Focus rectangle
  - Hover: Subtle background change

### Detail Panel
- **Dynamic Loading**: Content changes based on TreeView selection
- **Responsive Layout**: Controls adapt to panel width
- **Validation**: Real-time input validation with error indicators
- **Help Text**: Tooltips and contextual help for complex settings
- **Grouped Controls**: Related settings visually grouped with borders/headers

### Form Controls
- **Sliders**: Include numeric TextBox for precise input
- **ComboBoxes**: Include refresh button for device lists
- **File/Folder Browsers**: "Browse..." button opens native dialogs
- **Validation**: Red border + error message for invalid inputs
- **Units**: Display units (%, ms, etc.) next to numeric inputs

## State Management

### Dirty State Detection
- **Change Tracking**: Monitor all control value changes
- **Visual Indicators**: 
  - Modified settings show asterisk (*) or different color
  - Category names in TreeView show indicator if any child settings changed
  - Window title shows "Settings*" when unsaved changes exist

### Validation
- **Real-time**: Validate input as user types/changes values
- **Cross-field**: Validate related settings (e.g., thresholds must be in logical order)
- **File Existence**: Verify paths exist and are accessible
- **Range Checking**: Ensure numeric values are within valid ranges

### Button Behaviors

#### Apply
- **Function**: Save all changes and apply immediately to running application
- **State**: Enabled only when dirty changes exist and all validation passes
- **Feedback**: Brief success message, clears dirty state but keeps dialog open

#### Save & Close
- **Function**: Apply changes and close the settings window
- **State**: Enabled only when validation passes (works with both dirty and clean states)
- **Feedback**: Closes window immediately after successful save

#### Cancel
- **Function**: Discard all unsaved changes and close window
- **Confirmation**: If dirty state exists, show "Discard changes?" dialog
- **Keyboard**: Escape key triggers Cancel behavior

#### Restore Defaults
- **Function**: Reset all settings to application defaults
- **Confirmation**: "Reset all settings to defaults?" with Yes/No dialog
- **Scope**: Resets ALL settings, not just the current category
- **Feedback**: Updates all form controls and marks as dirty state

## Keyboard Navigation & Accessibility

### Tab Order
1. TreeView → Detail Panel controls (top to bottom, left to right) → Action buttons
2. Within detail panel: Logical grouping order, skip disabled controls
3. Complex controls (sliders): Tab enters control, arrow keys adjust value

### Keyboard Shortcuts
- **Ctrl+S**: Save & Close
- **Ctrl+A**: Apply changes  
- **Ctrl+R**: Restore Defaults (with confirmation)
- **Escape**: Cancel/Close
- **F1**: Context-sensitive help

### Screen Reader Support
- **Labels**: All controls have descriptive labels
- **Groups**: Related controls grouped with proper headings
- **Live Regions**: Announce validation errors and status changes
- **Landmarks**: Proper navigation landmarks for main areas
- **Descriptions**: Extended descriptions for complex controls via aria-describedby

### Visual Accessibility
- **High Contrast**: Support Windows high contrast themes
- **Focus Indicators**: Clear focus rectangles for keyboard navigation
- **Color Independence**: Don't rely solely on color for state indication
- **Text Size**: Respect system font size settings
- **Minimum Targets**: Touch/click targets at least 44x44px

## Error Handling

### Input Validation Errors
- **Visual**: Red border around invalid control
- **Message**: Clear error text near the control
- **Accessibility**: Error announced to screen readers
- **Blocking**: Invalid settings prevent Apply/Save actions

### File Access Errors
- **Missing Files**: Show warning icon with "File not found" message
- **Permission Errors**: "Access denied" with suggestion to run as administrator
- **Recovery**: Offer to browse for alternative file location

### Connection Errors
- **Discord**: Test connection button shows specific error (invalid token, network issue)
- **Audio Devices**: Device enumeration failures with refresh option
- **Recovery**: Clear instructions for resolving connectivity issues

## Implementation Notes

### Data Binding
- Use WPF data binding to connect UI controls to settings properties
- Implement INotifyPropertyChanged for automatic UI updates
- Consider using ViewModels to separate UI logic from settings storage

### Performance
- Lazy-load detail panels to improve startup time
- Cache device enumeration results with refresh capability
- Debounce rapid setting changes to avoid excessive saves

### Extensibility
- Design category system to easily add new setting groups
- Use metadata-driven approach for auto-generating basic controls
- Plugin architecture for custom setting panels

This UX plan provides a comprehensive, accessible, and user-friendly interface for managing all Kinectv1 application settings while maintaining consistency with modern Windows application design principles.