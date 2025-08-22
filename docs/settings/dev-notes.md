# Settings Developer Notes

This document provides technical guidance for developers working with the Kinectv1 settings system. It explains how to add new settings categories, maintain consistency, and extend functionality.

## Architecture Overview

The settings system consists of several key components:

### 1. Settings Storage (`Settings.settings`)
- XML-based configuration file containing all user and application settings
- Auto-generates `Settings.Designer.cs` with strongly-typed property accessors
- Supports both `User` scope (per-user) and `Application` scope (global) settings

### 2. Settings Access Layer (`AppSettings.cs`) 
- Static class providing consistent API for settings access
- Thread-safe configuration operations with `_configLock`
- Organized Load*/Save* methods for each setting
- Grouped summary methods for related settings
- Built-in validation and error handling

### 3. Configuration Management
- Direct XML manipulation through `ConfigurationManager`  
- Migration support from legacy `app.config` settings
- Fallback mechanisms for missing or corrupted settings

## Adding New Setting Categories

Follow this step-by-step process to add a new settings category:

### Step 1: Add Settings to Settings.settings

Edit `/Properties/Settings.settings` in Visual Studio or manually:

```xml
<Setting Name="NewFeatureEnabled" Type="System.Boolean" Scope="User">
  <Value Profile="(Default)">True</Value>
</Setting>
<Setting Name="NewFeatureThreshold" Type="System.Double" Scope="User">
  <Value Profile="(Default)">0.5</Value>
</Setting>
<Setting Name="NewFeatureModelPath" Type="System.String" Scope="User">
  <Value Profile="(Default)">models\newfeature\model.onnx</Value>
</Setting>
```

**Important Notes:**
- Use descriptive, PascalCase names (e.g., `VoiceThreshold`, not `voicethreshold`)
- Choose appropriate data types: `System.Boolean`, `System.String`, `System.Double`, `System.Int32`
- Use `User` scope for user-configurable settings, `Application` scope for system defaults
- Provide sensible default values

### Step 2: Add Load/Save Methods

Add corresponding methods to `AppSettings.cs`:

```csharp
#region New Feature Settings

/// <summary>
/// Load new feature enabled setting
/// </summary>
public static bool LoadNewFeatureEnabled()
{
    try
    {
        return GetBool("NewFeatureEnabled", true); // fallback to true
    }
    catch (Exception ex)
    {
        LogSettingError("NewFeatureEnabled", $"READ FAILED: {ex.Message}");
        return true; // safe fallback
    }
}

/// <summary>
/// Save new feature enabled setting
/// </summary>
public static void SaveNewFeatureEnabled(bool enabled)
{
    try
    {
        SetBool("NewFeatureEnabled", enabled);
        Console.WriteLine($"🆕 New Feature: Saved enabled: {enabled}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: Error saving new feature enabled: {ex.Message}");
    }
}

/// <summary>
/// Load new feature threshold setting
/// </summary>
public static float LoadNewFeatureThreshold()
{
    try
    {
        return (float)GetDouble("NewFeatureThreshold", 0.5);
    }
    catch (Exception ex)
    {
        LogSettingError("NewFeatureThreshold", $"READ FAILED: {ex.Message}");
        return 0.5f;
    }
}

/// <summary>
/// Save new feature threshold setting  
/// </summary>
public static void SaveNewFeatureThreshold(float threshold)
{
    try
    {
        SetDouble("NewFeatureThreshold", threshold);
        Console.WriteLine($"🆕 New Feature: Saved threshold: {threshold:F2}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: Error saving new feature threshold: {ex.Message}");
    }
}

/// <summary>
/// Load new feature model path
/// </summary>
public static string LoadNewFeatureModelPath()
{
    try
    {
        return GetString("NewFeatureModelPath", @"models\newfeature\model.onnx");
    }
    catch (Exception ex)
    {
        LogSettingError("NewFeatureModelPath", $"READ FAILED: {ex.Message}");
        return @"models\newfeature\model.onnx";
    }
}

/// <summary>
/// Save new feature model path
/// </summary>
public static void SaveNewFeatureModelPath(string path)
{
    try
    {
        SetString("NewFeatureModelPath", path ?? string.Empty);
        Console.WriteLine($"🆕 New Feature: Saved model path: {path}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: Error saving new feature model path: {ex.Message}");
    }
}

#endregion
```

### Step 3: Add Batch Configuration Method

Create a convenience method for configuring all related settings:

```csharp
/// <summary>
/// Configure new feature settings all at once
/// </summary>
public static void ConfigureNewFeatureSettings(bool enabled, float threshold, string modelPath)
{
    try
    {
        SaveNewFeatureEnabled(enabled);
        SaveNewFeatureThreshold(threshold);
        SaveNewFeatureModelPath(modelPath);
        Console.WriteLine($"🆕 New Feature: Configured all settings successfully");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: Failed to configure new feature settings: {ex.Message}");
    }
}
```

### Step 4: Add Settings Summary Method

Create a summary method following the established pattern:

```csharp
/// <summary>
/// Grouped summary of new feature settings
/// </summary>
public static string GetNewFeatureSettingsSummary()
{
    try
    {
        var enabled = LoadNewFeatureEnabled();
        var threshold = LoadNewFeatureThreshold();
        var modelPath = LoadNewFeatureModelPath();

        return $"🆕 New Feature Settings:\n" +
               $"   Enabled: {enabled}\n" +
               $"   Threshold: {threshold:F2}\n" +
               $"   Model Path: {modelPath}";
    }
    catch (Exception ex)
    {
        return $"ERROR: Could not load new feature settings: {ex.Message}";
    }
}
```

### Step 5: Add Validation Logic

Extend the `Validate()` method to include your settings:

```csharp
// Add to Validate() method in AppSettings.cs
// New Feature Validation  
if (LoadNewFeatureEnabled())
{
    var modelPath = LoadNewFeatureModelPath();
    if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
    {
        issues.Add("❌ New Feature: Model file not found or not configured");
    }
    
    var threshold = LoadNewFeatureThreshold();
    if (threshold < 0.0f || threshold > 1.0f)
    {
        issues.Add("⚠️ New Feature: Threshold outside valid range (0.0-1.0)");
    }
}
```

### Step 6: Update Initialization

Add the summary to `InitializeSettingsOnStartup()`:

```csharp
// Add to InitializeSettingsOnStartup() method
Console.WriteLine(GetNewFeatureSettingsSummary());
```

### Step 7: Update Diagnostics Report

Add to the diagnostics report in `GetDiagnosticsReport()`:

```csharp
// Add to GetDiagnosticsReport() method  
report.AppendLine(GetNewFeatureSettingsSummary());
```

## Naming Conventions

### Setting Names
- **PascalCase**: `VoiceThreshold`, not `voiceThreshold` or `voice_threshold`
- **Descriptive**: `DiscordBotToken` instead of `Token`
- **Consistent Prefixes**: Group related settings (`Ollama*`, `Discord*`, `Tts*`)
- **Clear Intent**: `*Enabled` for booleans, `*Path` for file paths, `*Threshold` for numeric limits

### Method Names
- **Load Methods**: `LoadSettingName()` - always returns the setting value
- **Save Methods**: `SaveSettingName(value)` - always accepts the value to save
- **Configure Methods**: `ConfigureFeatureSettings(...)` - batch configuration
- **Summary Methods**: `GetFeatureSettingsSummary()` - human-readable summary

### Variable Names
- Use full descriptive names in summaries: `enabled`, `threshold`, `modelPath`
- Match the setting name closely: `LoadVoiceThreshold()` → `var voiceThreshold`

## Data Types and Helpers

### Available Helper Methods
```csharp
// String settings
GetString(name)                    // Returns string or empty
GetString(name, fallback)          // Returns string or fallback
SetString(name, value)             // Saves string value

// Boolean settings  
GetBool(name)                      // Returns bool or false
GetBool(name, fallback)            // Returns bool or fallback
SetBool(name, value)               // Saves boolean value

// Numeric settings
GetInt(name)                       // Returns int or 0
GetInt(name, fallback)             // Returns int or fallback  
SetInt(name, value)                // Saves integer value

GetDouble(name)                    // Returns double or 0.0
GetDouble(name, fallback)          // Returns double or fallback
SetDouble(name, value)             // Saves double value
```

### Type Mapping
| .NET Type | Settings.settings Type | Helper Methods |
|-----------|------------------------|----------------|
| `bool` | `System.Boolean` | `GetBool()`, `SetBool()` |
| `int` | `System.Int32` | `GetInt()`, `SetInt()` |
| `float`/`double` | `System.Double` | `GetDouble()`, `SetDouble()` |
| `string` | `System.String` | `GetString()`, `SetString()` |

**Note**: Always cast `GetDouble()` to `float` if your code uses float:
```csharp
public static float LoadThreshold()
{
    return (float)GetDouble("Threshold", 0.5);
}
```

## Error Handling Patterns

### Consistent Error Handling
Every Load/Save method should include try-catch with appropriate fallbacks:

```csharp
public static bool LoadFeatureEnabled()
{
    try
    {
        return GetBool("FeatureEnabled", true);
    }
    catch (Exception ex)
    {
        LogSettingError("FeatureEnabled", $"READ FAILED: {ex.Message}");
        return true; // Safe fallback
    }
}

public static void SaveFeatureEnabled(bool enabled)
{
    try
    {
        SetBool("FeatureEnabled", enabled);
        Console.WriteLine($"🔧 Feature: Saved enabled: {enabled}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: Error saving feature enabled: {ex.Message}");
    }
}
```

### Fallback Values
Choose safe defaults that won't break functionality:
- **Booleans**: `false` for dangerous features, `true` for safe features
- **Numeric**: Middle-ground values (e.g., `0.5` for thresholds)  
- **Strings**: Empty string or known-good default path
- **Paths**: Use relative paths with fallbacks to bundled assets

## Validation Best Practices

### File Path Validation
```csharp
// In Validate() method
var modelPath = LoadNewFeatureModelPath();
if (string.IsNullOrEmpty(modelPath))
{
    issues.Add("⚠️ New Feature: Model path not configured");
}
else if (!File.Exists(modelPath))
{
    issues.Add("❌ New Feature: Model file not found: " + modelPath);
}
```

### Range Validation
```csharp
// In Validate() method
var threshold = LoadNewFeatureThreshold();
if (threshold < 0.0f || threshold > 1.0f)
{
    issues.Add($"⚠️ New Feature: Threshold {threshold:F2} outside valid range (0.0-1.0)");
}
```

### Dependency Validation
```csharp
// In Validate() method
if (LoadNewFeatureEnabled())
{
    if (!LoadBaseFeatureEnabled())
    {
        issues.Add("⚠️ New Feature: Requires Base Feature to be enabled");
    }
}
```

## Integration Patterns

### UI Integration
When building UI controls, use the Load/Save methods:

```csharp
// Loading UI state
checkBoxEnabled.IsChecked = AppSettings.LoadNewFeatureEnabled();
sliderThreshold.Value = AppSettings.LoadNewFeatureThreshold();
textBoxPath.Text = AppSettings.LoadNewFeatureModelPath();

// Saving from UI
AppSettings.SaveNewFeatureEnabled(checkBoxEnabled.IsChecked == true);
AppSettings.SaveNewFeatureThreshold((float)sliderThreshold.Value);
AppSettings.SaveNewFeatureModelPath(textBoxPath.Text);
```

### Service Integration
Services should use settings at startup and react to changes:

```csharp
public class NewFeatureService
{
    private bool _enabled;
    private float _threshold;
    
    public void Initialize()
    {
        _enabled = AppSettings.LoadNewFeatureEnabled();
        _threshold = AppSettings.LoadNewFeatureThreshold();
        
        Console.WriteLine($"🆕 New Feature Service initialized: {_enabled}");
    }
    
    public void UpdateSettings()
    {
        _enabled = AppSettings.LoadNewFeatureEnabled();
        _threshold = AppSettings.LoadNewFeatureThreshold();
    }
}
```

## Migration Patterns

### Handling Legacy Settings
When migrating from older configurations:

```csharp
private static bool LoadNewFeatureEnabled()
{
    try
    {
        // Try new setting first
        var newValue = GetBool("NewFeatureEnabled");
        if (newValue != null) return newValue;
        
        // Fall back to legacy setting
        var legacyValue = GetBool("LegacyFeatureEnabled", false);
        if (legacyValue)
        {
            // Migrate to new setting
            SaveNewFeatureEnabled(legacyValue);
            Console.WriteLine("🔄 Migrated LegacyFeatureEnabled to NewFeatureEnabled");
        }
        
        return legacyValue;
    }
    catch (Exception ex)
    {
        LogSettingError("NewFeatureEnabled", $"READ FAILED: {ex.Message}");
        return false;
    }
}
```

## Testing Settings

### Unit Testing
Create tests for your settings methods:

```csharp
[Test]
public void TestNewFeatureSettings()
{
    // Test default values
    Assert.IsTrue(AppSettings.LoadNewFeatureEnabled());
    Assert.AreEqual(0.5f, AppSettings.LoadNewFeatureThreshold(), 0.01f);
    
    // Test save/load cycle
    AppSettings.SaveNewFeatureEnabled(false);
    Assert.IsFalse(AppSettings.LoadNewFeatureEnabled());
    
    AppSettings.SaveNewFeatureThreshold(0.8f);
    Assert.AreEqual(0.8f, AppSettings.LoadNewFeatureThreshold(), 0.01f);
}
```

### Manual Testing
- Test with missing settings file
- Test with corrupted XML
- Test with out-of-range values
- Verify fallback behavior
- Check validation messages

## Common Patterns

### Boolean Feature Flags
```csharp
public static bool LoadFeatureEnabled()
{
    return GetBool("FeatureEnabled", false);
}

public static void SaveFeatureEnabled(bool enabled)
{
    SetBool("FeatureEnabled", enabled);
}
```

### Threshold Values
```csharp
public static float LoadFeatureThreshold()
{
    return (float)GetDouble("FeatureThreshold", 0.5);
}

public static void SaveFeatureThreshold(float threshold)
{
    SetDouble("FeatureThreshold", Math.Clamp(threshold, 0.0, 1.0));
}
```

### File Paths
```csharp
public static string LoadFeatureModelPath()
{
    return GetString("FeatureModelPath", @"models\feature\default.onnx");
}

public static void SaveFeatureModelPath(string path)
{
    SetString("FeatureModelPath", path ?? string.Empty);
}
```

### Enumeration Values
```csharp
public enum FeatureMode { Auto, Manual, Disabled }

public static FeatureMode LoadFeatureMode()
{
    var modeStr = GetString("FeatureMode", "Auto");
    return Enum.TryParse<FeatureMode>(modeStr, out var mode) ? mode : FeatureMode.Auto;
}

public static void SaveFeatureMode(FeatureMode mode)
{
    SetString("FeatureMode", mode.ToString());
}
```

## Performance Considerations

### Caching
For frequently accessed settings, consider caching:

```csharp
private static float? _cachedThreshold;

public static float LoadFeatureThreshold()
{
    if (_cachedThreshold == null)
    {
        _cachedThreshold = (float)GetDouble("FeatureThreshold", 0.5);
    }
    return _cachedThreshold.Value;
}

public static void SaveFeatureThreshold(float threshold)
{
    _cachedThreshold = threshold;
    SetDouble("FeatureThreshold", threshold);
}
```

### Batch Operations
Use batch configuration methods for related settings:

```csharp
public static void ConfigureBatchSettings(Dictionary<string, object> settings)
{
    foreach (var kvp in settings)
    {
        switch (kvp.Value)
        {
            case bool b: SetBool(kvp.Key, b); break;
            case int i: SetInt(kvp.Key, i); break;
            case double d: SetDouble(kvp.Key, d); break;
            case string s: SetString(kvp.Key, s); break;
        }
    }
}
```

## Documentation Standards

### XML Documentation
Always include XML documentation for public methods:

```csharp
/// <summary>
/// Load new feature threshold setting
/// </summary>
/// <returns>Threshold value between 0.0 and 1.0</returns>
public static float LoadNewFeatureThreshold()
```

### Console Output Emojis
Use consistent emojis for different categories:
- 🔊 TTS settings
- 🎙️ STT settings  
- 🤖 Discord settings
- 🧠 Ollama/AI settings
- 🎧 Audio settings
- 👤 Face recognition
- 🎨 UI settings
- 📊 Telemetry
- 🆕 New features
- ⚙️ General settings
- 🔧 Configuration changes

This system provides a consistent, maintainable approach to settings management that scales well as the application grows.