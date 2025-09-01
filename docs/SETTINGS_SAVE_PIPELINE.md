# Settings Save Pipeline (Kinectv1)

This document explains the end-to-end pipeline for loading, editing, validating, and saving application settings in the Kinectv1 project.

## Components

- Settings/default.json (EmbeddedResource)
  - Project embeds Settings/default.json as an assembly resource under the logical name: {AssemblyName}.Settings.default.json
  - This file contains all default values used for first-run initialization and as a base for validation.

- Settings/SettingsPaths.cs
  - Computes resource name and the per-user settings location: %APPDATA%/{AssemblyCompany}/{AssemblyProduct}/settings.json
  - Requires AssemblyCompany and AssemblyProduct attributes; they are defined in AssemblyInfo.cs.

- Settings/SettingsService.cs
  - Primary API for settings: Current (snapshot), Reload(), Save(Func<...>), GetDefaults(), ValidateOrThrow(AppSettings)
  - On first run (no user file), initializes user settings.json from defaults (atomic write + .bak)
  - On every reload, deep-merges default.json with user settings.json; user values override defaults
  - Save(update) builds a new AppSettings instance, validates, then atomically writes settings.json and updates .bak

- Settings/AppSettings.cs
  - Strongly-typed immutable record types: AppSettings, AudioSettings, TtsSettings, VadSettings
  - TtsExecution enum values are serialized as strings ("CPU"|"GPU") via Newtonsoft Json settings

- Settings/SettingsValidation.cs
  - Thin façade that delegates to SettingsService.ValidateOrThrow for single source of truth

- UI: src/UI/Settings/SettingsWindow + ModelsSettingsView
  - Provides an editor surface for all settings.
  - SettingsWindow orchestrates copying values from/to SettingsService.

## Load Flow

1) Application startup (App.xaml.cs)
   - App.SettingsProvider = new SettingsService();
   - The service loads defaults, merges with the user's settings file if it exists, and exposes a snapshot via Current.

2) Open Settings Window
   - SettingsWindow.Loaded reads SettingsService.Current and populates ModelsSettingsView controls:
     - TTS: Enabled, Speaker, Execution, ModelFolder, ModelPath, VocoderPath
     - Audio: VoiceThreshold, VadThreshold, BufferSize
     - VAD: Threshold

3) Editing
   - As the user changes values, the window tracks HasUnsavedChanges (IsDirty) and enables the Save button.

## Verify

- Clicking Verify does not save. It:
  1. Builds a candidate Kinectv1.Settings.AppSettings from the UI fields (BuildFromUI)
  2. Calls SettingsValidation.ValidateOrThrow(candidate)
  3. Shows success or validation errors

## Save

- Clicking Save executes the JSON save pipeline:
  1. Build candidate AppSettings from UI (strict parsing for numeric fields; exceptions surface)
  2. Validate with SettingsValidation.ValidateOrThrow (ranges and required fields enforced)
  3. SettingsService.Save(_ => candidate) performs:
     - Atomic write to %APPDATA%/{Company}/{Product}/settings.json using a .tmp file
     - Creates/updates a .bak backup copy
     - Updates the in-memory snapshot (Current)
  4. UI refreshes snapshot and clears IsDirty

Notes:
- The JSON file is the single source of truth for these settings.
- No silent fallbacks: missing/invalid values will throw to aid debugging.

## Defaults (without saving)

- Clicking Defaults:
  - Fetches SettingsService.GetDefaults();
  - Loads those values into the editor controls (does not write to file);
  - Sets IsDirty to true so the user can Save to persist.

## Reload

- Clicking Reload:
  - Calls SettingsService.Reload(); merges defaults + user settings.json again
  - Repopulates the editor from the new snapshot
  - Clears IsDirty

## Error Handling

- BuildFromUI uses invariant culture and strict parsing; invalid numeric input raises exceptions which are shown to the user.
- Validation throws with descriptive messages if required fields are missing or ranges are violated.

## File Locations

- User settings: %APPDATA%/{AssemblyCompany}/{AssemblyProduct}/settings.json
- Backup: settings.json.bak (next to the user settings file)
- Defaults: embedded resource {AssemblyName}.Settings.default.json

## Extending Settings

- Add fields to Settings/AppSettings.cs records
- Update Settings/default.json with defaults
- SettingsService will merge new defaults with existing user files automatically
- Update SettingsWindow.Loaded, BuildFromUI, Defaults_Click, Reload_Click, and ModelsSettingsView XAML to expose the new fields
- Add validation rules in SettingsService.Validate if needed

## Telemetry and Consumers

- Consumers should read from App.SettingsProvider.Current for live snapshots
- After Save, the snapshot is updated; consumers may refresh their view/state accordingly
