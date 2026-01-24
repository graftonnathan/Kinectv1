# Settings Save Pipeline (Refactored)

This document describes the streamlined settings pipeline for the Kinectv1 project. It replaces the older deep‑merge/validation split with a simpler, layered approach while preserving all functionality.

---

## Core Principles

* **Single source of truth:** `%APPDATA%/{Company}/{Product}/settings.json`.
* **Immutable defaults:** shipped with the application as `Settings/default.json` (embedded or content file).
* **Overlay model:** Defaults ⊕ (optional) Machine Defaults ⊕ User Overrides ⊕ Secrets.
* **Strongly typed:** `AppSettings` record tree with `DataAnnotations`.
* **Atomic writes:** `File.Replace` ensures settings + backup consistency.
* **Validation first:** invalid settings never overwrite the user file.

---

## Components

* **Settings/default.json**

  * Embedded resource or content file.
  * Provides immutable baseline values.

* **User Settings**

  * `%APPDATA%/{Company}/{Product}/settings.json`
  * Contains per‑user overrides only.

* **Secrets (optional)**

  * `%APPDATA%/{Company}/{Product}/secrets.json`
  * Highest precedence overlay for sensitive values (tokens, passwords).

* **Settings/AppSettings.cs**

  * Immutable record tree (`AppSettings`, `AudioSettings`, `TtsSettings`, `OllamaSettings`, etc.).
  * Annotated with `[Required]`, `[Range]`, etc.
  * Optional `IValidatableObject` for cross‑field checks.
  * Legacy `VadSettings` has been removed; VAD RMS gating now derives from `Audio.VoiceThreshold` and timing from `AsrSettings`.

* **Settings/SettingsService.cs**

  * Loads all overlay layers and produces a single `Current` snapshot.
  * Methods:

    * `AppSettings Current` (effective)
    * `AppSettings GetDefaultsEffective()`
    * `void ValidateOrThrow(AppSettings candidate)`
    * `Task Save(AppSettings next)` (atomic write + backup)
    * `void Reload()` (rebuilds overlays)
    * `void ResetToDefaults()` (deletes user file)
  * Events:

    * `Changed` (fires when `Current` is updated).

* **UI: SettingsWindow + SettingsViewModel**

  * ViewModel mirrors `AppSettings` and implements validation via `INotifyDataErrorInfo`.
  * Window binds fields directly; validation errors surface inline.
  * Commands:

    * **Verify**: materialize `AppSettings` from ViewModel, call `ValidateOrThrow`.
    * **Save**: build candidate, validate, call `Save` → refresh `Current`.
    * **Defaults**: load `GetDefaultsEffective()` into the ViewModel, mark dirty.
    * **Reload**: call `Reload()`, repopulate ViewModel, clear dirty flag.

* **TTS Playback & Barge‑In (New)**

  * Local TTS output now flows through a lightweight `TtsPlaybackController` which guarantees single active utterance + atomic preemption.
  * `SpeakWithPreemptionAsync` (TtsService) always routes via the controller; legacy per‑call cancellation helpers removed.
  * `AsrSettings.BargeInEnabled` governs interaction between live speech and TTS:
    * When `true`: voice activation (VAD rising edge) cancels the current TTS utterance (barge‑in) and a short grace window is applied to trimming on the next synthesis.
    * When `false`: ongoing TTS suppresses ASR ingestion (frames are buffered as preroll but not recognized) — user speech waits until playback completes.
  * External cancellation (UI / hotkey) routes through `TtsPlaybackController.CancelCurrent()` and marks a cancel timestamp used to relax leading trim on immediate follow‑up speech.

* **Face / Speaker Settings**

  * `FaceSettings` now exposes three knobs that control how `SpeakerEmbedder` samples audio:
    * `speakerEmbeddingWindowMs` — duration of each embedding crop (default 1600 ms for ECAPA).
    * `speakerEmbeddingHopMs` — stride between crops (default 800 ms). Smaller hops give denser coverage but use more CPU.
    * `speakerEmbeddingSilenceDb` — dBFS gate for discarding windows that are mostly silence/room noise.
  * These values are respected live (no restart required) and should be persisted alongside other Face settings.

* **Enrolled Speaker Matching**

  * `audio.speakerMatchMinScore` — Minimum cosine similarity (0.0–1.0) required to accept an enrolled speaker match.
    * Default: 0.75 (raised from 0.6 to reduce false positives).
    * Recommended range: 0.70–0.85 for most use cases.
    * Lower values (0.50–0.65) are lenient and may match the wrong speaker.
    * Higher values (0.85–0.95) are strict and may reject valid matches.
  * This setting can be adjusted in the Transcription Settings UI under "Enrolled Speaker Matching".
  * Logs are emitted to console showing match scores vs threshold for diagnostics.

---

## Load Flow

1. **Startup:**

   * `SettingsService` loads defaults → overlays machine defaults (if present) → overlays user settings → overlays secrets.
   * Validates the effective config.
   * Exposes as `Current`.

2. **Open Settings Window:**

   * ViewModel is populated from `Current`.
   * User edits update ViewModel and validation state in real time.

---

## Save Flow

1. User clicks **Save**.
2. ViewModel builds a candidate `AppSettings`.
3. `SettingsService.ValidateOrThrow(candidate)` ensures integrity.
4. `SettingsService.Save(candidate)`:

   * Serializes candidate as JSON (user overrides only).
   * Writes to a temporary file.
   * Calls `File.Replace(temp, settings.json, settings.json.bak)` for atomic swap.
   * Updates `Current`.
   * Fires `Changed`.

---

## Error Handling

* Validation errors surface immediately in the UI.
* Save rejects invalid input and does not touch the file.
* Reload failure falls back to embedded defaults with an error message.

---

## Extending Settings

1. Add a property to `AppSettings` or nested records.
2. Update `default.json` with a baseline value.
3. Optional: add migration logic if schema version changes.
4. Update the UI ViewModel and bindings.

---

## Versioning & Migration

* JSON files include a `schemaVersion` field.
* If `User.schemaVersion < Current`, ordered `ISettingsMigration` steps apply.
* Migrations only touch user overrides; defaults stay intact.

---

## File Locations Summary

* Embedded defaults: `{AssemblyName}.Settings.default.json` or `Settings/default.json`.
* User settings: `%APPDATA%/{Company}/{Product}/settings.json`.
* Backup: `%APPDATA%/{Company}/{Product}/settings.json.bak`.
* Secrets (optional): `%APPDATA%/{Company}/{Product}/secrets.json`.

---

## Benefits of Refactor

* **Fewer moving parts:** no deep‑merge, no separate validation class.
* **Predictable overlays:** explicit precedence order.
* **Safer writes:** `File.Replace` handles both atomic update + backup.
* **Cleaner UI:** ViewModel handles field validation, no manual parse logic.
* **Future‑proof:** schema version + migrations prevent upgrade pain.
* **Simpler VAD:** single RMS threshold derived from `Audio.VoiceThreshold`, timing via `AsrSettings` (silence/debounce) – removed duplicate legacy threshold.
* **Deterministic TTS preemption:** controller centralizes cancellation; barge‑in behavior configurable via settings.
