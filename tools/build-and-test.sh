#!/bin/bash
# Simple build and test script for ASR testing harness

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

echo "🔨 Building ASR Testing Harness"
echo "================================"

# Check if we're on Windows (can build .NET Framework) or Linux (need Mono)
if [[ "$OSTYPE" == "msys" ]] || [[ "$OSTYPE" == "win32" ]]; then
    echo "Windows detected - using dotnet build"
    BUILD_CMD="dotnet build"
    RUN_CMD=""
elif command -v mono >/dev/null 2>&1; then
    echo "Linux with Mono detected - using dotnet build + mono execution"
    BUILD_CMD="dotnet build"
    RUN_CMD="mono"
else
    echo "❌ Neither Windows nor Mono detected. ASR testing requires .NET Framework 4.8.1"
    echo "   Please install Mono or run on Windows"
    exit 1
fi

# Build the tool
echo "Building AsrOffline tool..."
cd "$SCRIPT_DIR"
$BUILD_CMD AsrOffline.csproj --configuration Release

if [[ $? -eq 0 ]]; then
    echo "✅ Build successful"
else
    echo "❌ Build failed"
    exit 1
fi

# Run basic tests
echo ""
echo "🧪 Running basic functionality tests..."

# Test help command
echo "Testing help command..."
if $RUN_CMD AsrOffline.exe --help >/dev/null 2>&1; then
    echo "✅ Help command works"
else
    echo "❌ Help command failed"
    exit 1
fi

# Test debounce logic
echo "Testing debounce logic..."
if $RUN_CMD AsrOffline.exe --test-debounce; then
    echo "✅ Debounce tests passed"
else
    echo "❌ Debounce tests failed"
    exit 1
fi

# Test synthetic Discord frames
echo "Testing synthetic Discord frames..."
if $RUN_CMD AsrOffline.exe --test-discord; then
    echo "✅ Synthetic Discord tests passed"
else
    echo "❌ Synthetic Discord tests failed"
    exit 1
fi

echo ""
echo "🎉 All basic tests passed!"
echo ""
echo "To run full ASR tests:"
echo "1. Ensure a Vosk model is available in ../models/"
echo "2. Add test WAV files to ../testdata/audio/"
echo "3. Run: $RUN_CMD AsrOffline.exe input.wav --golden ../testdata/golden/asr/expected.json"
echo ""
echo "To run in CI:"
echo "- Linux: ./run-asr-tests.sh"
echo "- Windows: run-asr-tests.bat"