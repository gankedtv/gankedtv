# Open worktrees — Terminal.app implementation

Read this only after [SKILL.md](SKILL.md) — it assumes the shared metadata-extraction block has already populated `NUMS`, `PREFIXES`, `WORKTREES`.

## Layout

Three windows total, regardless of how many issues. Each window holds N tabs (one per issue), grouped by role:

```text
Window "claude":  [ #103 claude | #104 claude | #108 claude ]
Window "server":  [ #103 server | #104 server | #108 server ]
Window "web":     [ #103 web    | #104 web    | #108 web    ]
```

Terminal.app's AppleScript has no `pane`/`split` verb — splits in the macOS Terminal UI are a recent shell-integration feature, not scriptable. So the role-grouping is expressed as separate windows instead of separate tabs-with-panes. Switch windows with `⌘\`` (next window) and switch tabs within a window with `⌘{` / `⌘}`.

## How tabs and titles work

- `do script "<cmd>"` (no `in` clause) opens a **new window** running `<cmd>`. Used for the first issue in each role.
- `do script "<cmd>" in window <ref>` opens a **new tab** in that window. Used for issues 2..N.
- Terminal.app supports `custom title` natively on tabs: `set custom title of selected tab of front window to "<name>"`. No `printf '\033]0;…\007'` escape trick needed — AppleScript writes the title directly, and (in recent macOS versions) it's stickier against shell prompt redraws than the OSC-0 sequence is.

## Implementation

```bash
open_window() {
  local role="$1"   # claude | server | web
  local count=${#NUMS[@]}

  # Build the AppleScript body. First issue creates the window; the rest open
  # additional tabs in that same window.
  local body=""
  local i num prefix wt cmd name
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
    if [ "$i" -eq 1 ]; then
      body+="  do script \"$cmd\""$'\n'
    else
      body+="  do script \"$cmd\" in front window"$'\n'
    fi
    body+="  set custom title of selected tab of front window to \"$name\""$'\n'
  done

  osascript <<APPLESCRIPT
tell application "Terminal"
  activate
$body
end tell
APPLESCRIPT
}

open_window claude
open_window server
open_window web
```

## Why each role gets its own window

`do script "<cmd>"` with no `in` clause is the only documented way to open a fresh Terminal.app window in one AppleScript call — there's no `make new window` verb that also accepts a command. So creating three separate windows (one per `open_window` call) is the natural shape. A bonus: when you `⌘Tab` to Terminal, you see three labeled windows in Mission Control / App Exposé instead of one window with 3N tabs.

If you'd rather have a single window with all 3N tabs grouped by role, replace the three `open_window` calls with one loop that calls `do script "$cmd" in front window` after the first — but you lose the role-grouping in the window switcher.

## Caveats

- **Older macOS versions** (≤ 10.14ish) may not preserve `custom title` across prompt redraws. If you see titles flicker back to the working directory, see the "Shell title rewriting" section in [SKILL.md](SKILL.md).
- **No automatic split layout.** If you want side-by-side panes per issue, either switch to iTerm2 (see [macos-iterm2.md](macos-iterm2.md)) or use tmux inside Terminal.app and have it manage the splits.
- **Single-issue shortcut**: for one issue, `ISSUE_LIST="900"` still works — each window gets exactly one tab, and the three windows become a simple claude / server / web triplet for that worktree.
