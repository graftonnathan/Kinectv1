# Guardrails
- Make only minimal, local edits unless I say otherwise.
- No refactors, renames, reformatting, or logging unless requested.
- If >20 lines change or a new file/dependency is needed: STOP and ask.
- Prefer “plan → wait for approval → apply diff”.
- When editing, output unified diff only.
- Every code change requires a comprehensive check of each call, class, helper involved as to maintain function.
- When other systems are involved, ask to refactor the affected system to support the code change.
- DO **NOT** CREATE FALL BACK HARDCODED SETTINGS. LET THE PROGRAM FAIL. WE ARE DEBUGGING!!
- DO **NOT** create shortcut code to solve issues. Write out the full function, build and debug.

