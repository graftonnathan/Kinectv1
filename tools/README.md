# ASR Testing Harness

Lightweight offline testing tools for ASR (Automatic Speech Recognition) regression testing.

## Overview

This testing harness provides tools to:
- Run offline WAV → ASR transcription tests
- Compare results against golden reference data
- Catch regressions in speech recognition quality
- Validate ASR pipeline components

## Structure

```
tools/
├── AsrOffline.exe          # Main testing tool (built from AsrOffline.csproj)
├── AsrOffline.csproj       # Console app project file
├── Program.cs              # Tool implementation
├── DebounceTest.cs         # VAD debouncing logic tests
├── SyntheticDiscordTest.cs # Discord frame processing simulation
├── run-asr-tests.sh        # Linux/macOS test runner
├── run-asr-tests.bat       # Windows test runner
├── build-and-test.sh       # Build and basic test script
├── validate-structure.sh   # Structure validation (no build required)
└── README.md               # This file

testdata/
└── golden/
    └── asr/
        ├── sample1.json    # Golden result for test audio 1
        ├── sample2.json    # Golden result for test audio 2
        └── ...             # Additional golden results
```

## Usage

### Building the Tool

```bash
# From project root
dotnet build tools/AsrOffline.csproj --configuration Release
```

### Running ASR Tests

#### Basic Usage
```bash
# Process a WAV file and output JSON
./tools/AsrOffline.exe input.wav

# Compare against golden result
./tools/AsrOffline.exe input.wav --golden testdata/golden/asr/expected.json

# Specify model path
./tools/AsrOffline.exe input.wav --model path/to/vosk/model

# Verbose output
./tools/AsrOffline.exe input.wav --verbose
```

#### Running Test Suite
```bash
# Full build and test (requires .NET Framework 4.8.1)
./tools/build-and-test.sh

# Structure validation only (no build required)
./tools/validate-structure.sh

# CI test runners
# Linux/macOS
./tools/run-asr-tests.sh

# Windows
tools\run-asr-tests.bat
```

### Output Format

The tool outputs JSON with ASR results:

```json
{
  "text": "transcribed text here",
  "confidence": 0.85,
  "tokens": [
    {
      "word": "transcribed",
      "confidence": 0.89,
      "start": 0.0,
      "end": 0.8
    },
    {
      "word": "text",
      "confidence": 0.92,
      "start": 0.9,
      "end": 1.2
    }
  ]
}
```

## Exit Codes

- `0`: Success
- `1`: Error (file not found, processing failed, etc.)
- `2`: Golden result mismatch

## CI Integration

The test runner scripts are designed for CI environments:

```yaml
# Example GitHub Actions step
- name: Run ASR Tests
  run: ./tools/run-asr-tests.sh
```

## Requirements

- .NET Framework 4.8.1 or later
- Vosk model files (place in `models/` directory)
- NAudio for audio processing
- Test WAV files for full regression testing

## Adding New Tests

1. Add WAV test file to `testdata/`
2. Generate golden result:
   ```bash
   ./tools/AsrOffline.exe testdata/new_test.wav > testdata/golden/asr/new_test.json
   ```
3. Review and commit golden result
4. Update test runner script if needed

## Notes

- Tool auto-detects Vosk models in common locations
- Audio files are automatically converted to 16kHz mono
- Golden comparisons normalize whitespace and case
- Confidence differences > 0.2 generate warnings but don't fail tests