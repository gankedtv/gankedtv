---
description: Keep CLAUDE.md and AGENTS.md in sync (one is a mirror of the other)
argument-hint: [--check]
---

Reconcile `CLAUDE.md` and `AGENTS.md` at the repo root. These files are maintained as a synced pair — this command detects drift and offers to fix it.

Arg (optional): `$ARGUMENTS` — if it contains `--check`, run in check-only mode: report drift and exit non-zero, do not edit anything.

## Synced-pair rule

The two files are byte-identical from line 2 onward. Line 1 is each file's own H1 title (`# CLAUDE.md` or `# AGENTS.md`) and stays intact in each. This is the only intentional difference — any other drift is the thing this command reconciles.

## Steps

1. **Preconditions:**
   - Both `CLAUDE.md` and `AGENTS.md` must exist at the repo root. If either is missing, abort with a clear message.

2. **Diff the files from line 2 onward.** `diff -u <(tail -n +2 CLAUDE.md) <(tail -n +2 AGENTS.md)`.
   - If identical → print `CLAUDE.md and AGENTS.md are in sync.` and exit 0.
   - In `--check` mode, if non-identical, print the diff and exit 1. Stop here.

3. **Determine the source of truth.** Query the last commit that touched each file:
   ```
   git log -1 --format=%ct -- CLAUDE.md
   git log -1 --format=%ct -- AGENTS.md
   ```
   The file with the higher (more recent) timestamp is the source. If tied, fall back to on-disk mtime to catch uncommitted edits (use `stat -f %m` on macOS, `stat -c %Y` on Linux — detect with `uname`). If still tied, abort: print the diff and tell the user to reconcile manually.

4. **Present the proposed change.** Show the user:
   - Which file is source, which is stale, and why (timestamp difference).
   - A unified diff of what would be written to the stale file. The write is: keep the stale file's existing line 1 (its H1 title), then append lines 2..N from the source verbatim.
   - Ask for confirmation before writing.

5. **Write** only on confirmation. One way:
   ```
   { head -n 1 <stale-file>; tail -n +2 <source-file>; } > <stale-file>.tmp && mv <stale-file>.tmp <stale-file>
   ```
   Never edit both files in one run.

6. **Report** the result in one line: `Synced AGENTS.md ← CLAUDE.md` (or vice versa).

## Notes

- This command does not stage or commit. The user handles git.
- If the synced-pair rule changes (e.g. the files intentionally diverge in more than the H1), update this command's policy before invoking.
