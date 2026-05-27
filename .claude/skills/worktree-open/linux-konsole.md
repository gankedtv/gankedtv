# Open worktrees — Konsole (KDE) implementation

Read this only after [SKILL.md](SKILL.md) — it assumes the shared metadata-extraction block has already populated `NUMS`, `PREFIXES`, `WORKTREES`.

## Layout

Three windows total, regardless of how many issues. Each window holds N tabs (one per issue), grouped by role:

```
Window "claude":  [ #103 claude | #104 claude | #108 claude ]
Window "server":  [ #103 server | #104 server | #108 server ]
Window "web":     [ #103 web    | #104 web    | #108 web    ]
```

Konsole 22.04+ has scriptable split panes via D-Bus, but the API only addresses the *focused* view (no way to target a specific split deterministically), so this skill sticks to tabs and uses three separate windows for role grouping — same shape as the Terminal.app sub-file. Switch windows with `Alt+Tab` and tabs within a window with `Shift+Left` / `Shift+Right` (default Konsole bindings).

## Mechanism

Konsole's `--tabs-from-file <file>` flag reads a small INI-style spec — one tab per line, fields delimited by `;;` — and opens one window with all of them at once. Per-tab keys we use:

- `title:` — sets the tab title. Konsole writes it directly to the tab strip; no `printf '\033]0;…\007'` escape trick needed.
- `workdir:` — the tab's starting directory (replaces the `cd <worktree>` step).
- `command:` — the program to run. Konsole parses this as argv with double-quote grouping (no shell), so wrap multi-command flows in `bash -c "..."`.

One `konsole --tabs-from-file` invocation per role → three separate windows. The `--separate` flag forces each invocation to spawn its own top-level Konsole process; without it the second and third calls would attach as extra tabs onto the first window and you'd end up with one window of 3N tabs.

## Implementation

```bash
open_window() {
  local role="$1"   # claude | server | web
  local count=${#NUMS[@]}
  local tabsfile
  tabsfile="$(mktemp)"

  local i num prefix wt cmd name
  for ((i=1; i<=count; i++)); do
    num="${NUMS[$((i-1))]}"
    prefix="${PREFIXES[$((i-1))]}"
    wt="${WORKTREES[$((i-1))]}"
    name="$prefix • $role"
    case "$role" in
      # Single quotes inside the outer double-quoted bash -c arg keep the slash
      # command intact when bash re-parses argv.
      claude) cmd="bash -c \"claude --permission-mode plan '/issue $num'\"" ;;
      server) cmd="bash -c \"make server\"" ;;
      web)    cmd="bash -c \"make up && make web\"" ;;
      *) echo "unknown role: $role" >&2; rm -f "$tabsfile"; return 1 ;;
    esac
    printf 'title: %s;; workdir: %s;; command: %s\n' "$name" "$wt" "$cmd" >> "$tabsfile"
  done

  # Spawn Konsole in the background; let it read the file before we clean up.
  konsole --separate --tabs-from-file "$tabsfile" >/dev/null 2>&1 &
  sleep 0.5
  rm -f "$tabsfile"
}

open_window claude
open_window server
open_window web
```

## Caveats

- **Konsole must be installed** (`konsole --version`). On CachyOS with KDE Plasma it's the default; on GNOME or other DEs install via `pacman -S konsole` or equivalent.
- **`--tabs-from-file` is line-based**, so the worktree path and tab title must not contain literal `;;`. The sanitizer in `SKILL.md` already strips Konsole-unfriendly characters, and worktree paths under `.worktrees/issue-<n>/` never contain `;;`.
- **No splits.** If you want side-by-side panes per issue, drive tmux from inside Konsole.
- **Single-issue shortcut**: for one issue, `ISSUE_LIST="900"` still works — each window gets exactly one tab, and the three windows become a simple claude / server / web triplet for that worktree.
