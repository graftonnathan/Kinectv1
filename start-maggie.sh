#!/bin/bash
# Start Maggie Headless with proper environment

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

# Set additional environment variables for Maggie
export MAGGIE_TTS_URL="http://localhost:7860"
export MAGGIE_VOSK_MODEL="/home/molten/.openclaw/workspace/maggie/vosk-model-small-en-us-0.15"

# Log file with timestamp
LOGFILE="$HOME/workspace/maggie/logs/maggie-$(date +%Y%m%d-%H%M%S).log"

# Change to Maggie directory
cd "$HOME/workspace/maggie"

# Start Maggie in background
echo "Starting Maggie Headless..."
nohup ./bin/Release/net8.0/MaggieHeadless > "$LOGFILE" 2>&1 &
MAGGIE_PID=$!
echo $MAGGIE_PID > /tmp/maggie.pid
echo "Maggie started with PID: $MAGGIE_PID"
echo "Log file: $LOGFILE"

# Wait and check status
sleep 5
if ps -p $MAGGIE_PID > /dev/null; then
    echo "✅ Maggie is running"
    echo "   Jeff API: http://localhost:18790"
    echo "   WebRTC:   http://localhost:8787 (if enabled)"
else
    echo "❌ Maggie failed to start"
    echo "Last 30 lines of log:"
    tail -30 "$LOGFILE"
fi
