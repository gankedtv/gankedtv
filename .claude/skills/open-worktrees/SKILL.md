---
name: open-worktrees
description: Use when the user wants to open one iTerm2 tab per GankedTV worktree (`.worktrees/issue-<n>/`) with a 2×2 grid — narrow left column (web/server stacked), wide right column (claude code on top, misc shell with `make up` on bottom). Triggers on phrases like "open worktrees for X Y Z", "open worktree tabs", "spin up panes for these issues", "set up iTerm splits for these worktrees", or when `/worktree` is invoked with an `iterm` keyword. macOS + iTerm2 only.
---

# Open worktrees

## Layout

Per issue, one new iTerm2 tab with this 2×2 grid (left column narrow, right column wide):

```
+--------+----------------------+
|  web   |  claude code         |
+--------+----------------------+
| server |  misc (make up,      |
|        |  idle for `code .`)  |
+--------+----------------------+
```

- **web** — `make web` (vite dev server)
- **server** — `make server` (dotnet watch)
- **claude code** — `claude` (a Claude Code session scoped to the worktree)
- **misc** — runs `make up` once then sits idle in the worktree dir, intended for `code .` and other ad-hoc commands

Each pane `cd`s into `<repo_root>/.worktrees/issue-<n>/` first. `make up` is idempotent — even though `/worktree`'s bootstrap already ran it, re-running confirms postgres + minio are alive in case the machine was rebooted between sessions.

## Tab and pane names

iTerm2 derives the tab strip title from the currently active pane's name (there's no separate tab title in AppleScript). To keep the issue number always visible no matter which pane is focused, every pane in the tab gets the prefix `#<issue> [short title] •` followed by the role:

- `#103 hero clip • web`
- `#103 hero clip • server`
- `#103 hero clip • claude code`
- `#103 hero clip • misc`

The short title comes from `gh issue view <n> --json title -q .title`, sanitized to alphanumerics + a few safe chars and truncated to 40 chars. If `gh` fails (unauthenticated, network), the prefix falls back to just `#<n>`.

**Caveat:** zsh/bash with default prompt configs frequently rewrite the tab title on every prompt. If the AppleScript-set names disappear after the first command, either:
- disable shell title updates (zsh: `DISABLE_AUTO_TITLE=true` for oh-my-zsh, or remove `precmd`/`preexec` title hooks)
- in iTerm2 Preferences → Profiles → Terminal, **uncheck** "Terminal may set tab/window title"

## Preconditions

- Worktree dirs already exist (created via `/worktree <n>` or `scripts/new-worktree.sh`). This skill does **not** create them.
- macOS with iTerm2 running.
- Invoked from inside the GankedTV repo (so `git rev-parse --show-toplevel` resolves correctly).
- `gh` authenticated (only needed for the descriptive tab name — script still works without it).

## When NOT to use

- Worktrees not yet created → run `/worktree <n>` first.
- Another process is bound to the worktree's web/api ports (e.g. a leftover `make dev-all` tab) → `make web`/`make server` will fail to bind. Close the conflicting tab first.

## Sizing caveat

iTerm2's AppleScript `split` commands have **no size parameter** — every split is 50/50. To get the narrow left / wide right look, the script sends `Cmd+Opt+Shift+Left` via System Events after the splits to shrink the left column. This relies on iTerm2's default keybinding for "Resize Split Pane: move divider left". If you've rebound it, the layout falls back to even quarters — adjust manually or re-bind. The number of repeats (8 by default) determines how narrow the left column gets.

## Implementation

Replace `ISSUE_LIST` with the user's issue numbers. The outer `for` loop runs one `osascript` per issue so each tab can have its own title interpolated.

```bash
ISSUE_LIST="103 104 108"   # ← replace with the user's issue numbers
REPO_ROOT="$(git rev-parse --show-toplevel)" || { echo "not in a git repo" >&2; exit 1; }

for n in $ISSUE_LIST; do
  wt="$REPO_ROOT/.worktrees/issue-$n"

  # Short, AppleScript-safe title: alphanumerics + a few separators, max 40 chars.
  raw_title=$(gh issue view "$n" --json title -q .title 2>/dev/null || true)
  safe_title=$(printf '%s' "$raw_title" \
    | sed 's/[^[:alnum:] ._:#/-]//g' \
    | cut -c1-40 \
    | sed 's/[[:space:]]*$//')
  prefix="#$n"
  [ -n "$safe_title" ] && prefix="#$n $safe_title"

  osascript <<APPLESCRIPT
  tell application "iTerm2"
    activate
    tell current window
      set newTab to (create tab with default profile)
      set pWeb to (current session of newTab)
      -- Step 1: vertical split → pWeb (left half) | pClaude (right half)
      tell pWeb to set pClaude to (split vertically with default profile)
      -- Step 2: split each column horizontally to make the 2x2 grid
      tell pWeb to set pServer to (split horizontally with default profile)
      tell pClaude to set pMisc to (split horizontally with default profile)
      -- Name each pane (active pane's name flows into tab strip)
      tell pWeb to set name to "$prefix • web"
      tell pServer to set name to "$prefix • server"
      tell pClaude to set name to "$prefix • claude code"
      tell pMisc to set name to "$prefix • misc"
      -- Send commands. The `printf '\\033]0;TITLE\\007'` is the xterm OSC-0
      -- title escape — iTerm2 honors it and sets the tab/pane title to TITLE.
      -- Quadruple backslashes are needed because the string survives three
      -- layers (bash heredoc → AppleScript string → shell printf format) and
      -- each strips one level of escaping; AppleScript also can't parse \033
      -- in a string literal so we MUST send the literal four characters and
      -- let printf do the octal interpretation in the shell.
      tell pWeb to write text "printf '\\\\033]0;$prefix • web\\\\007'; cd $wt && make web"
      tell pServer to write text "printf '\\\\033]0;$prefix • server\\\\007'; cd $wt && make server"
      tell pClaude to write text "printf '\\\\033]0;$prefix • claude code\\\\007'; cd $wt && claude"
      tell pMisc to write text "printf '\\\\033]0;$prefix • misc\\\\007'; cd $wt && make up"
      tell pWeb to select
    end tell
  end tell
APPLESCRIPT
done
```

## Optional: shrink the left column

iTerm2's AppleScript `split` commands have no size parameter — splits are always 50/50. To get the narrow-left / wide-right look from the screenshot, append this block after the main `tell` block (still inside the heredoc) to send `Cmd+Opt+Shift+Left` via System Events, which triggers iTerm2's default "Resize Split Pane: move divider left" keybinding:

```applescript
delay 0.3
tell application "System Events"
  tell process "iTerm2"
    repeat 8 times
      key code 123 using {command down, option down, shift down}
    end repeat
  end tell
end tell
```

This is best-effort: it relies on the default keybinding and on iTerm2 being the frontmost app when the keystrokes fire. Each press moves the divider by ~5 cells. Tune the repeat count for your terminal width. If you've rebound the resize action the layout falls back to even quarters — adjust manually.

## Notes on iTerm split terminology

- **"split vertically"** → new pane appears to the RIGHT of the source (panes arranged left-to-right).
- **"split horizontally"** → new pane appears BELOW the source (panes stacked top-to-bottom).

If the user describes side-by-side panes as "horizontal splits", use `split vertically`. Confirm if ambiguous.

## Single-issue shortcut

For one issue, `ISSUE_LIST="900"` works — the loop runs once.
