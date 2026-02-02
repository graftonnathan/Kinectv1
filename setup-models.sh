#!/bin/bash
# Setup script for Maggie headless mode - downloads required models

set -e

echo "Setting up Maggie headless mode models..."
cd "$(dirname "$0")"

# Create models directory
mkdir -p models/speaker

# Download large Vosk model if not present
if [ ! -d "vosk-model-en-us-0.22" ]; then
    echo "Downloading Vosk English model (en-us-0.22)..."
    wget -q --show-progress https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip -O /tmp/vosk-model-en-us-0.22.zip
    echo "Extracting..."
    unzip -q /tmp/vosk-model-en-us-0.22.zip
    rm /tmp/vosk-model-en-us-0.22.zip
    echo "✓ Vosk model ready"
else
    echo "✓ Vosk model already present"
fi

# Download speaker embedding model if not present
if [ ! -f "models/speaker/voxceleb_ECAPA512_LM.onnx" ]; then
    echo "Downloading speaker embedding model (ECAPA-TDNN)..."
    # Using pyannote/embedding model (ECAPA-TDNN based)
    wget -q --show-progress https://github.com/pyannote/pyannote-audio/releases/download/2023.06/voxceleb_ECAPA512_LM.onnx -O models/speaker/voxceleb_ECAPA512_LM.onnx || {
        echo "⚠️  Could not download speaker model. Transcription mode will work without speaker diarization."
        echo "    You can manually download from: https://huggingface.co/pyannote/embedding"
    }
    echo "✓ Speaker model ready"
else
    echo "✓ Speaker model already present"
fi

echo ""
echo "Setup complete! Update Settings/default.json:"
echo '  "stt": {'
echo '    "modelPath": "vosk-model-en-us-0.22"'
echo '  },'
echo '  "transcription": {'
echo '    "speakerEmbeddingModelPath": "models/speaker/voxceleb_ECAPA512_LM.onnx"'
echo '  }'
