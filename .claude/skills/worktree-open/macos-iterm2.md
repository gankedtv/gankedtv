# Open worktrees — iTerm2 implementation

Read this only after [SKILL.md](SKILL.md) — it assumes the shared metadata-extraction block has already populated `NUMS`, `PREFIXES`, `WORKTREES`.

## Layout

Three tabs total, regardless of how many issues. Each tab has one vertical-split pane per issue (side-by-side columns):

```
Tab "claude":  | #103 claude | #104 claude | #108 claude |
Tab "server":  | #103 server | #104 server | #108 server |
Tab "web":     | #103 web    | #104 web    | #108 web    |
```

iTerm2 derives the tab strip title from the currently active pane's name (no separate tab title in AppleScript). When you switch panes inside a tab, the tab strip updates to show which issue is focused. When you switch tabs, the role changes.

## Sizing

All splits inside a tab are vertical (side-by-side). iTerm2's `split vertically` bisects the source pane 50/50, and each split targets the previously-created pane, so widths land at 50 / 25 / 25 for 3 issues and 50 / 25 / 12.5 / 12.5 for 4 — pane 1 always keeps half the tab. For 2–3 issues this is fine; for 4+ either live with the imbalance, manually `Cmd+Opt+Shift+Left` from pane 1 to push the divider, or batch larger sets across multiple runs.

## Implementation

```bash
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
