---
description: Refresh the graphify knowledge graph for this repo
---

Run `graphify update .` to refresh `graphify-out/` after a coding session, per the graphify rule in `CLAUDE.md`. No arguments.

## Steps

1. **Preconditions:**
   - `graphify-out/` must exist at the repo root. If it doesn't, abort and print: `graphify not initialized here — run 'graphify init' first if you want a knowledge graph.` Do not init silently.
   - `command -v graphify` must resolve. If missing, abort and print an install hint: `graphify binary not found on PATH.`

2. **Run the update:** `graphify update .`.
   - This is AST-only (no API cost) per the project rule, so it's safe to run freely.

3. **Summarize the result.** Parse the tool's output for added/updated/removed node counts and print a one-line summary, e.g. `graphify: +3 / ~12 / -1 nodes`. If the output format doesn't expose those numbers, fall back to printing the tool's own final summary line verbatim.

4. **On non-zero exit:** print the tool's stderr and stop. Do not retry automatically.
