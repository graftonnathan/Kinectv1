#!/bin/bash
# daily-todo.sh - Execute daily tasks from TODO.md
# Run via: 0 9 * * * /home/molten/workspace/maggie/daily-todo.sh

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TODO_FILE="$SCRIPT_DIR/TODO.md"
LOG_FILE="$SCRIPT_DIR/logs/daily-$(date +%Y-%m-%d).log"

# Create logs directory
mkdir -p "$SCRIPT_DIR/logs"

log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $1" | tee -a "$LOG_FILE"
}

log "=== Daily Todo Execution ==="
log "Checking for high priority tasks..."

# Count incomplete high priority tasks
HIGH_COUNT=$(grep -c "\- \[ \].*Priority: High" "$TODO_FILE" 2>/dev/null || echo "0")
log "High priority tasks remaining: $HIGH_COUNT"

# Check Maggie health
HEALTH=$(curl -s http://localhost:18790/health 2>/dev/null | grep -o '"status":"ok"' || echo "DOWN")
if [ "$HEALTH" = '"status":"ok"' ]; then
    log "✅ Maggie is healthy"
else
    log "⚠️  Maggie appears to be down"
fi

# Check TTS health
TTS_HEALTH=$(curl -s http://localhost:7860/health 2>/dev/null | grep -o '"model_loaded":true' || echo "DOWN")
if [ "$TTS_HEALTH" = '"model_loaded":true' ]; then
    log "✅ Qwen3-TTS is healthy"
else
    log "⚠️  Qwen3-TTS appears to be down"
fi

# Generate report
log ""
log "=== TODO Summary ==="
TOTAL=$(grep -c "\- \[ \]" "$TODO_FILE" 2>/dev/null || echo "0")
COMPLETED=$(grep -c "\- \[x\]" "$TODO_FILE" 2>/dev/null || echo "0")
log "Total incomplete: $TOTAL"
log "Total completed: $COMPLETED"

# If running interactively, show the list
if [ -t 1 ]; then
    echo ""
    echo "=== High Priority Tasks ==="
    grep -A2 "Priority: High" "$TODO_FILE" | grep "\- \[ \]" | head -5
fi

log "=== Daily execution complete ==="
log ""
