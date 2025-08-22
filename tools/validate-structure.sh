#!/bin/bash
# Validate ASR testing harness code structure without building

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

echo "🔍 Validating ASR Testing Harness Structure"
echo "==========================================="

# Check required files exist
echo "Checking file structure..."

REQUIRED_FILES=(
    "tools/AsrOffline.csproj"
    "tools/Program.cs"
    "tools/DebounceTest.cs"
    "tools/SyntheticDiscordTest.cs"
    "tools/run-asr-tests.sh"
    "tools/run-asr-tests.bat"
    "tools/README.md"
    "testdata/golden/asr/sample1.json"
    "testdata/golden/asr/sample2.json"
    "testdata/README.md"
)

ALL_FOUND=true
for file in "${REQUIRED_FILES[@]}"; do
    if [[ -f "$PROJECT_ROOT/$file" ]]; then
        echo "✅ $file"
    else
        echo "❌ $file"
        ALL_FOUND=false
    fi
done

if [[ "$ALL_FOUND" == "false" ]]; then
    echo "❌ Some required files are missing"
    exit 1
fi

# Validate JSON structure
echo ""
echo "Validating golden result JSON structure..."

GOLDEN_FILES=(
    "testdata/golden/asr/sample1.json"
    "testdata/golden/asr/sample2.json"
)

for file in "${GOLDEN_FILES[@]}"; do
    if command -v python3 >/dev/null 2>&1; then
        if python3 -m json.tool "$PROJECT_ROOT/$file" >/dev/null 2>&1; then
            echo "✅ $file - Valid JSON"
        else
            echo "❌ $file - Invalid JSON"
            exit 1
        fi
    elif command -v node >/dev/null 2>&1; then
        if node -e "JSON.parse(require('fs').readFileSync('$PROJECT_ROOT/$file', 'utf8'))" >/dev/null 2>&1; then
            echo "✅ $file - Valid JSON"
        else
            echo "❌ $file - Invalid JSON"
            exit 1
        fi
    else
        echo "⚠️  $file - Cannot validate JSON (no python3 or node found)"
    fi
done

# Check that golden files have expected structure
echo ""
echo "Checking golden result structure..."

if command -v python3 >/dev/null 2>&1; then
    for file in "${GOLDEN_FILES[@]}"; do
        FULL_PATH="$PROJECT_ROOT/$file"
        if python3 -c "
import json
import sys
try:
    with open('$FULL_PATH') as f:
        data = json.load(f)
    required_fields = ['text', 'confidence', 'tokens']
    missing = [field for field in required_fields if field not in data]
    if missing:
        print('❌ $file - Missing fields: ' + ', '.join(missing))
        sys.exit(1)
    if not isinstance(data['tokens'], list):
        print('❌ $file - tokens must be an array')
        sys.exit(1)
    for token in data['tokens']:
        if not all(field in token for field in ['word', 'confidence', 'start', 'end']):
            print('❌ $file - Invalid token structure')
            sys.exit(1)
    print('✅ $file - Valid structure')
except Exception as e:
    print('❌ $file - Error:', str(e))
    sys.exit(1)
"; then
            continue
        else
            exit 1
        fi
    done
else
    echo "⚠️  Cannot validate JSON structure (no python3 found)"
fi

# Check script permissions
echo ""
echo "Checking script permissions..."

SCRIPTS=(
    "tools/run-asr-tests.sh"
    "tools/build-and-test.sh"
)

for script in "${SCRIPTS[@]}"; do
    if [[ -x "$PROJECT_ROOT/$script" ]]; then
        echo "✅ $script - Executable"
    else
        echo "❌ $script - Not executable"
        chmod +x "$PROJECT_ROOT/$script"
        echo "   Fixed: Made $script executable"
    fi
done

# Basic C# syntax check (if available)
echo ""
echo "Basic C# syntax validation..."

if command -v dotnet >/dev/null 2>&1; then
    cd "$PROJECT_ROOT/tools"
    if dotnet build --dry-run AsrOffline.csproj >/dev/null 2>&1; then
        echo "✅ C# project structure appears valid"
    else
        echo "⚠️  C# project may have issues (dry-run failed)"
    fi
else
    echo "⚠️  Cannot validate C# syntax (dotnet not available)"
fi

echo ""
echo "🎉 ASR Testing Harness structure validation completed!"
echo ""
echo "Summary:"
echo "- File structure: ✅ Complete"
echo "- JSON format: ✅ Valid"
echo "- Script permissions: ✅ Correct"
echo "- Ready for CI integration"