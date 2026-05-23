---
name: gankedtv-worktree-tabs
description: Use when the user wants to open one iTerm2 tab per GankedTV worktree (`.worktrees/issue-<n>/`) with four side-by-side panes running `make web`, `make server`, `claude`, and `make up` (then idle so the user can `code .`). Triggers on phrases like "open worktree tabs", "spin up panes for issues X Y Z", "set up iTerm splits for these worktrees", or when `/worktree` is invoked with an `iterm` keyword. macOS + iTerm2 only.
---

# GankedTV worktree tabs

## What it does

For each issue number, opens a new tab in the current iTerm2 window with **four vertical panes side-by-side** (iTerm calls this "split vertically"). Left → right:

| # | Pane | Command |
|---|------|---------|
| 1 | leftmost | `make web` |
| 2 |          | `make server` |
| 3 |          | `claude` |
| 4 | rightmost | `make up` (then idle in the worktree dir for `code .`) |

Each pane `cd`s into `<repo_root>/.worktrees/issue-<n>/` first.

Pane 4 is for opening VS Code on that specific worktree (`code .`) once `make up` finishes. `make up` is idempotent — even though the worktree bootstrap already ran it, re-running confirms postgres + minio are alive in case the machine was rebooted between sessions.

## Preconditions

- Worktree dirs already exist (created via `/worktree <n>` or `scripts/new-worktree.sh`). This skill does **not** create them.
- macOS with iTerm2 running.
- Invoked from inside the GankedTV repo (so `git rev-parse --show-toplevel` resolves to the right root).

## When NOT to use

- Worktrees not yet created → run `/worktree <n>` first.
- Another process is bound to the worktree's web/api ports (e.g. a leftover `make dev-all` tab) → `make web`/`make server` will fail to bind. Close the conflicting tab first.

## Implementation

Replace `ISSUE_LIST` with the user's issue numbers (space-separated). The script computes the repo root once via `git rev-parse --show-toplevel`, then drives iTerm via AppleScript. The heredoc is **unquoted** so `$REPO_ROOT` and `$ISSUE_LIST` interpolate; AppleScript doesn't use `$` so there's no conflict with its `&` string concatenation operator.

```bash
ISSUE_LIST="103 104 108"   # ← replace with the user's issue numbers
REPO_ROOT="$(git rev-parse --show-toplevel)" || { echo "not in a git repo" >&2; exit 1; }

# Build the AppleScript list literal: "103", "104", "108"
APPLESCRIPT_LIST=$(printf '"%s", ' $ISSUE_LIST | sed 's/, $//')

osascript <<EOF
tell application "iTerm2"
  activate
  tell current window
    repeat with n in {$APPLESCRIPT_LIST}
      set wt to "$REPO_ROOT/.worktrees/issue-" & n
      set newTab to (create tab with default profile)
      set p1 to (current session of newTab)
      -- Build pane order p1 | p2 | p3 | p4 (left to right).
      -- Each `split vertically` puts the new pane to the right of the source,
      -- so splitting p1 first to make p3, then p1 again to inject p2 between
      -- p1 and p3, then p3 to add p4 on the far right, gives even quarters.
      tell p1 to set p3 to (split vertically with default profile)
      tell p1 to set p2 to (split vertically with default profile)
      tell p3 to set p4 to (split vertically with default profile)
      tell p1 to write text "cd " & wt & " && make web"
      tell p2 to write text "cd " & wt & " && make server"
      tell p3 to write text "cd " & wt & " && claude"
      tell p4 to write text "cd " & wt & " && make up"
    end repeat
  end tell
end tell
EOF
```

## Notes on iTerm split terminology

- **"split vertically"** → new pane appears to the RIGHT of the source (panes arranged left-to-right). This is what the user usually means by "horizontally next to each other."
- **"split horizontally"** → new pane appears BELOW the source (panes stacked top-to-bottom).

If the user says "horizontal splits" but describes side-by-side panes, use `split vertically`. Confirm if ambiguous.

## Single-issue shortcut

For one issue, `ISSUE_LIST="900"` works — the `printf` loop produces a single-element list literal.
