---
name: open-worktrees
description: Use when the user wants to open GankedTV worktrees (`.worktrees/issue-<n>/`) in iTerm2 with 3 tabs — one tab for all `claude` sessions, one for all `make server` processes, one for `make up && make web`. Each tab has one side-by-side pane per issue. Triggers on phrases like "open worktrees for X Y Z", "open worktree tabs", "spin up panes for these issues", "set up iTerm splits for these worktrees", or when `/worktree` is invoked with an `iterm` keyword. macOS + iTerm2 only.
---

# Open worktrees

## Layout

Three tabs total, regardless of how many issues. Each tab has one vertical-split pane per issue (side-by-side columns):

```
Tab "claude":  | #103 claude | #104 claude | #108 claude |
Tab "server":  | #103 server | #104 server | #108 server |
Tab "web":     | #103 web    | #104 web    | #108 web    |
```

Per-issue commands:

- **claude** pane — `claude --permission-mode plan "/issue <n>"`. Boots a Claude Code session scoped to the worktree, in **plan mode**, with the `/issue <n>` slash command auto-submitted as the first message. So each claude pane immediately fetches+summarizes the issue, proposes a plan, and waits for you to approve before any writes. Approving plan mode drops the session back to normal/auto, so it's "plan first, then execute" with no extra flags.
- **server** pane — `make server` (dotnet watch)
- **web** pane — `make up && make web` (brings up postgres+minio for that worktree, then vite). `make up` is idempotent and per-worktree (each worktree has its own `COMPOSE_PROJECT_NAME=gankedtv-issue-<n>`), so re-running confirms infra is alive in case the machine was rebooted between sessions.

Each pane `cd`s into `<repo_root>/.worktrees/issue-<n>/` first.

## Tab and pane names

iTerm2 derives the tab strip title from the currently active pane's name (no separate tab title in AppleScript). Each pane is named `#<issue> [short title] • <role>`, e.g.:

- Tab "claude": `#103 hero clip • claude`, `#104 foo bar • claude`, `#108 baz • claude`
- Tab "server": `#103 hero clip • server`, …
- Tab "web":    `#103 hero clip • web`, …

So when you switch panes inside a tab, the tab strip updates to show which issue is focused. When you switch tabs, the role changes.

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

## Sizing

All splits inside a tab are vertical (side-by-side). iTerm2's `split vertically` bisects the source pane 50/50, and each split targets the most-recently-created pane, so widths land at 50 / 25 / 25 for 3 issues and 50 / 25 / 12.5 / 12.5 for 4 — pane 1 always keeps half the tab. For 2 issues this is fine; for 3+ either live with the imbalance, manually `Cmd+Opt+Shift+Left` from pane 1 to push the divider, or batch larger sets across multiple runs.

## Implementation

Replace `ISSUE_LIST` with the user's issue numbers. The script collects per-issue prefixes once, then opens three tabs by calling `open_tab` with a different role each time.

```bash
ISSUE_LIST="103 104 108"   # ← replace with the user's issue numbers
REPO_ROOT="$(git rev-parse --show-toplevel)" || { echo "not in a git repo" >&2; exit 1; }

# Collect per-issue metadata in parallel arrays.
declare -a NUMS PREFIXES WORKTREES
for n in $ISSUE_LIST; do
  wt="$REPO_ROOT/.worktrees/issue-$n"
  raw_title=$(gh issue view "$n" --json title -q .title 2>/dev/null || true)
  safe_title=$(printf '%s' "$raw_title" \
    | sed 's/[^[:alnum:] ._:#/-]//g' \
    | cut -c1-40 \
    | sed 's/[[:space:]]*$//')
  prefix="#$n"
  [ -n "$safe_title" ] && prefix="#$n $safe_title"
  NUMS+=("$n")
  PREFIXES+=("$prefix")
  WORKTREES+=("$wt")
done

open_tab() {
  local role="$1"   # claude | server | web
  local count=${#NUMS[@]}
  local splits="" assigns=""

  # Build the AppleScript fragments dynamically: (count - 1) vertical splits
  # off the previously-created pane, then a name + write block per pane.
  local i num prefix wt cmd name
  for ((i=2; i<=count; i++)); do
    splits+="      tell pane$((i-1)) to set pane$i to (split vertically with default profile)"$'\n'
  done
  for ((i=1; i<=count; i++)); do
    num="${NUMS[$((i-1))]}"
    prefix="${PREFIXES[$((i-1))]}"
    wt="${WORKTREES[$((i-1))]}"
    name="$prefix • $role"
    case "$role" in
      # \\\" survives bash → AppleScript so the final shell sees: claude … "/issue 103"
      claude) cmd="cd $wt && claude --permission-mode plan \\\"/issue $num\\\"" ;;
      server) cmd="cd $wt && make server" ;;
      web)    cmd="cd $wt && make up && make web" ;;
      *) echo "unknown role: $role" >&2; return 1 ;;
    esac
    assigns+="      tell pane$i to set name to \"$name\""$'\n'
    # Four-backslash chain explained below the script.
    assigns+="      tell pane$i to write text \"printf '\\\\033]0;$name\\\\007'; $cmd\""$'\n'
  done

  osascript <<APPLESCRIPT
tell application "iTerm2"
  activate
  tell current window
    set newTab to (create tab with default profile)
    set pane1 to (current session of newTab)
$splits$assigns      tell pane1 to select
  end tell
end tell
APPLESCRIPT
}

open_tab claude
open_tab server
open_tab web
```

## Why four backslashes in the printf escape

The string `"printf '\\\\033]0;$name\\\\007'; …"` survives three passes:

1. **Bash double-quoted string** (when building `$assigns`) — `\\\\` → `\\`.
2. **Heredoc → AppleScript source** — variable expansion is verbatim, so AppleScript sees `\\033]0;…\\007`.
3. **AppleScript string literal** — `\\` → `\`. The shell finally receives `printf '\033]0;…\007'`, where `\033` is the octal escape for ESC, completing the OSC-0 "set window title" sequence.

AppleScript itself can't parse `\033` in a string literal (it only knows `\n`, `\t`, `\r`, `\"`, `\\`), which is why the octal interpretation has to happen in the shell via `printf`.

## Notes on iTerm split terminology

- **"split vertically"** → new pane appears to the RIGHT of the source (panes arranged left-to-right). This is what we use here.
- **"split horizontally"** → new pane appears BELOW the source (panes stacked top-to-bottom).

If the user describes side-by-side panes as "horizontal splits", use `split vertically`. Confirm if ambiguous.

## Single-issue shortcut

For one issue, `ISSUE_LIST="900"` still works — every tab gets exactly one pane (no splits), and the three tabs become a simple claude / server / web triplet for that worktree.
