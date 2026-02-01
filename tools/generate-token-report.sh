#!/bin/bash
# Token usage report generator
# Runs hourly via cron

REPORT_DIR="$HOME/workspace/maggie/reports/token-usage"
mkdir -p "$REPORT_DIR"

TIMESTAMP=$(date '+%Y-%m-%d-%H%M')
REPORT_FILE="$REPORT_DIR/token-report-${TIMESTAMP}.md"

# Get Maggie's status
MAGGIE_STATUS=$(curl -s http://localhost:18790/api/status 2>&1 || echo "Maggie not responding")

# Get Jeff's session status from OpenClaw gateway
# Try the sessions endpoint first, fall back to status
JEFF_STATUS=$(curl -s -H "Accept: application/json" http://localhost:18789/api/sessions?format=json 2>&1)
if [ -z "$JEFF_STATUS" ] || [[ "$JEFF_STATUS" == *"<!doctype"* ]]; then
    JEFF_STATUS="Use '/status' command in chat for current token count"
fi

cat > "$REPORT_FILE" << EOF
# Token Usage Report

**Generated:** $(date '+%Y-%m-%d %H:%M:%S %Z')

---

## 🤖 Jeff (OpenClaw / Kimi K2.5)

**Status:** Active
**Channel:** webchat
**Model:** moonshot/kimi-k2.5

### Session Info
\`\`\`
${JEFF_STATUS}
\`\`\`

**To check current token count:** Type \`/status\` in this chat

**To compact context:** Type \`/compact\` when context grows large

---

## 🎯 Maggie (LMStudio Local)

**Status Endpoint:** http://localhost:18790/api/status

### Maggie's Response
\`\`\`json
${MAGGIE_STATUS}
\`\`\`

**Provider:** LMStudio at 100.119.229.73:1234
**Context Window:** ~4,000 tokens (local LLM)
**Token Policy:** Unlimited local inference

---

## Summary

| Metric | Jeff | Maggie |
|--------|------|--------|
| **Type** | Cloud API (Kimi) | Local LLM (LMStudio) |
| **Context Window** | Large (~128K) | Small (~4K) |
| **Token Cost** | Metered (API) | Unlimited (local) |
| **Best For** | Complex reasoning, coding | Quick queries, data tasks |

### Strategy Notes
- Offload data-heavy tasks to Maggie
- Reserve Jeff's tokens for code operations
- Run \`/compact\` when Jeff's context grows large
- Use Maggie for: file searches, summaries, simple analysis
- Use Jeff for: code generation, debugging, coordination

---

*Next report: $(date -d '+1 hour' '+%Y-%m-%d %H:%M' 2>/dev/null || echo 'in 1 hour')*
EOF

echo "Token report generated: $REPORT_FILE"
