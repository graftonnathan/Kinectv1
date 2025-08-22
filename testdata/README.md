# Test Data Directory

This directory contains golden reference data for ASR regression testing.

## Structure

```
testdata/
├── README.md               # This file
├── golden/                 # Golden reference results
│   └── asr/               # ASR transcription results
│       ├── sample1.json   # Golden result for "hello world this is a test"
│       ├── sample2.json   # Golden result for "the quick brown fox..."
│       └── ...            # Additional golden results
└── audio/                 # Test audio files (not included in repo)
    ├── sample1.wav        # Audio file corresponding to sample1.json
    ├── sample2.wav        # Audio file corresponding to sample2.json
    └── ...                # Additional test audio files
```

## Golden Result Format

Each golden result JSON file contains:

```json
{
  "text": "full transcribed text",
  "confidence": 0.85,
  "tokens": [
    {
      "word": "individual",
      "confidence": 0.89,
      "start": 0.0,
      "end": 0.5
    }
  ]
}
```

### Fields:
- `text`: Complete transcribed text (normalized)
- `confidence`: Overall transcription confidence (0.0-1.0)
- `tokens`: Array of word-level results with timing

### Token Fields:
- `word`: Individual word
- `confidence`: Word-level confidence (0.0-1.0)
- `start`: Start time in seconds
- `end`: End time in seconds

## Adding New Golden Results

1. Record or obtain test audio file
2. Run ASR tool to generate result:
   ```bash
   ./tools/AsrOffline.exe testdata/audio/new_test.wav > testdata/golden/asr/new_test.json
   ```
3. Review and verify the result manually
4. Commit both audio file (if appropriate) and golden result

## Test Categories

### Current Tests:
- `sample1.json`: Basic phrase "hello world this is a test"
- `sample2.json`: Pangram "the quick brown fox jumps over the lazy dog"

### Planned Test Categories:
- **Basic vocabulary**: Common words and phrases
- **Technical terms**: Domain-specific vocabulary
- **Different speakers**: Male/female voices, accents
- **Noise conditions**: Background noise, echo, etc.
- **Audio quality**: Different sample rates, compression
- **Sentence types**: Questions, commands, statements

## Notes

- Audio files are not included in the repository by default (too large)
- Golden results should be manually verified before committing
- Use descriptive filenames that indicate test content
- Confidence values may vary slightly between model versions
- Text should be normalized (lowercase, standard punctuation)