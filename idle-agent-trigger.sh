#!/bin/bash
# idle-agent-trigger.sh - Trigger agent work when Nathan is idle
# Run every 5 minutes via cron

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IDLE_FILE="$SCRIPT_DIR/.last-activity"
IDLE_THRESHOLD_MINUTES=15

# Get current timestamp
CURRENT_TIME=$(date +%s)

# Check if idle file exists
if [ -f "$IDLE_FILE" ]; then
    LAST_ACTIVITY=$(cat "$IDLE_FILE")
    IDLE_SECONDS=$((CURRENT_TIME - LAST_ACTIVITY))
    IDLE_MINUTES=$((IDLE_SECONDS / 60))
    
    if [ "$IDLE_MINUTES" -ge "$IDLE_THRESHOLD_MINUTES" ]; then
        # Nathan has been idle - trigger agent via OpenClaw gateway
        # This will be handled by the cron job that runs every 15 minutes
        echo "Idle for $IDLE_MINUTES minutes - ready for agent work"
        exit 0
    else
        echo "Not idle enough ($IDLE_MINUTES minutes)"
        exit 1
    fi
else
    # First run - create idle file
    echo "$CURRENT_TIME" > "$IDLE_FILE"
    echo "Created idle tracking file"
    exit 1
fi
