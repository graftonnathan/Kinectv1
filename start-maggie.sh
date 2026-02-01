#!/bin/bash
# start-maggie.sh - Launch Maggie with Qwen3-TTS voice and web interface

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TTS_PORT=${TTS_PORT:-7860}
MAGGIE_PORT=${MAGGIE_PORT:-18790}

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}╔════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║  🦞 Maggie AI Launcher with Qwen3-TTS Voice           ║${NC}"
echo -e "${BLUE}╚════════════════════════════════════════════════════════╝${NC}"
echo ""

# Check if Qwen TTS service is running
check_tts() {
    if curl -s "http://localhost:${TTS_PORT}/health" > /dev/null 2>&1; then
        return 0
    fi
    return 1
}

# Setup Python virtual environment for TTS
setup_venv() {
    if [ ! -d "${SCRIPT_DIR}/tts_service/venv" ]; then
        echo -e "${YELLOW}📦 Creating Python virtual environment for TTS...${NC}"
        cd "${SCRIPT_DIR}/tts_service"
        python3 -m venv venv
        echo -e "${GREEN}✅ Virtual environment created${NC}"
    fi
    
    # Check if dependencies are installed
    if ! "${SCRIPT_DIR}/tts_service/venv/bin/python" -c "import qwen_tts" 2>/dev/null; then
        echo -e "${YELLOW}📦 Installing Qwen3-TTS dependencies (this may take a few minutes)...${NC}"
        "${SCRIPT_DIR}/tts_service/venv/bin/pip" install -q qwen-tts soundfile numpy fastapi uvicorn
        echo -e "${GREEN}✅ Dependencies installed${NC}"
    fi
}

# Start Qwen3-TTS service
start_tts() {
    echo -e "${YELLOW}🎙️  Starting Qwen3-TTS service on port ${TTS_PORT}...${NC}"
    
    # Check if Python is available
    if ! command -v python3 &> /dev/null; then
        echo -e "${RED}❌ Python 3 not found. Please install Python 3.12+${NC}"
        exit 1
    fi
    
    # Setup virtual environment
    setup_venv
    
    # Start TTS service in background using venv
    cd "${SCRIPT_DIR}/tts_service"
    QWEN_TTS_DEVICE="cuda:0" QWEN_TTS_MODEL="Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice" \
        ./venv/bin/python qwen_tts_service.py > /tmp/maggie-tts.log 2>&1 &
    TTS_PID=$!
    
    # Wait for TTS to be ready
    echo -n "⏳ Waiting for TTS service to start"
    for i in {1..120}; do
        if check_tts; then
            echo ""
            echo -e "${GREEN}✅ Qwen3-TTS service ready (PID: ${TTS_PID})${NC}"
            return 0
        fi
        echo -n "."
        sleep 1
    done
    
    echo ""
    echo -e "${RED}❌ TTS service failed to start. Check /tmp/maggie-tts.log${NC}"
    exit 1
}

# Main start logic
cd "$SCRIPT_DIR"

# Check if TTS is already running
if check_tts; then
    echo -e "${GREEN}✅ Qwen3-TTS service already running on port ${TTS_PORT}${NC}"
else
    start_tts
fi

echo ""
echo -e "${BLUE}🧠 Starting Maggie AI headless mode...${NC}"

# Set environment variables for Maggie
export MAGGIE_TTS_URL="http://localhost:${TTS_PORT}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# Get IP for LAN access
IP_ADDR=$(hostname -I | awk '{print $1}')

echo ""
echo -e "${GREEN}═══════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}  Maggie is starting up!${NC}"
echo ""
echo -e "  🌐 Voice Web UI:  ${YELLOW}http://${IP_ADDR}:${MAGGIE_PORT}/voice/${NC}"
echo -e "  🌐 WebRTC UI:     ${YELLOW}http://${IP_ADDR}:${MAGGIE_PORT}/webrtc/${NC}"
echo -e "  💬 Chat API:      ${YELLOW}http://${IP_ADDR}:${MAGGIE_PORT}/api/chat${NC}"
echo -e "  🎙️  TTS Service:   ${YELLOW}http://localhost:${TTS_PORT}${NC}"
echo ""
echo -e "  Press Ctrl+C to stop"
echo -e "${GREEN}═══════════════════════════════════════════════════════${NC}"
echo ""

# Start Maggie
cd "$SCRIPT_DIR"
exec dotnet run --project MaggieHeadless.csproj
