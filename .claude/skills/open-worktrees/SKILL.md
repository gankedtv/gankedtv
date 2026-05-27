---
name: open-worktrees
description: Use when the user wants to open GankedTV worktrees (`.worktrees/issue-<n>/`) in their terminal — iTerm2 on macOS (3 tabs × N side-by-side panes), Terminal.app on macOS (3 windows × N tabs), or Konsole on KDE/Linux (3 windows × N tabs). Each role (claude / server / web) groups all issues. Triggers on phrases like "open worktrees for X Y Z", "open worktree tabs", "spin up panes for these issues", "set up iTerm splits for these worktrees", or when `/worktree` is invoked with a terminal keyword.
---

# Open worktrees

This skill is a router. The shared logic (metadata extraction, preconditions, naming scheme) lives here; the AppleScript implementation lives in a sibling file picked per terminal.

## Pick the implementation

Detect the active terminal, then Read the matching sibling file. Check in order:

| Condition (first match wins)                          | File to read                                                   |
|-------------------------------------------------------|----------------------------------------------------------------|
| `$TERM_PROGRAM` = `iTerm.app`                         | [macos-iterm2.md](macos-iterm2.md) — 3 tabs × N vertical panes |
| `$TERM_PROGRAM` = `Apple_Terminal`                    | [macos-terminal.md](macos-terminal.md) — 3 windows × N tabs    |
| `$KONSOLE_VERSION` is set (Linux + KDE)               | [linux-konsole.md](linux-konsole.md) — 3 windows × N tabs      |
| anything else                                         | stop — bail; this terminal isn't supported yet                 |

If the user explicitly names a terminal ("open these in Terminal.app" / "use Konsole" / "use iTerm"), honor that over the detected value.

## Goal (both implementations)

For each issue `n`, open three things in the worktree at `<repo_root>/.worktrees/issue-<n>/`:

- **claude** — `claude --permission-mode plan "/issue <n>"` — boots a Claude Code session scoped to the worktree, in **plan mode**, with `/issue <n>` auto-submitted. Each cell immediately fetches+summarizes the issue, proposes a plan, and waits for approval before any writes.
- **server** — `make server` (dotnet watch).
- **web** — `make up && make web`. `make up` is idempotent and per-worktree (each worktree has its own `COMPOSE_PROJECT_NAME=gankedtv-issue-<n>`), so re-running confirms infra is alive between sessions.

Group by **role**, not by issue — so you can scan all claude plans side-by-side, then switch to all servers, then all webs. iTerm2 expresses this with 3 tabs × N panes; Terminal.app expresses it with 3 windows × N tabs (no native pane support).

## Cell naming scheme

Each cell (pane in iTerm, tab in Terminal.app) is named `#<issue> [short title] • <role>`, e.g. `#103 hero clip • claude`.

The short title comes from `gh issue view <n> --json title -q .title`, sanitized to alphanumerics + a few safe chars and truncated to 40 chars. If `gh` fails (unauthenticated, network), the prefix falls back to just `#<n>`.

## Shared metadata extraction

Both sub-files assume this block has already run, populating `NUMS`, `PREFIXES`, `WORKTREES`:

```bash
ISSUE_LIST="103 104 108"   # ← replace with the user's issue numbers
REPO_ROOT="$(git rev-parse --show-toplevel)" || { echo "not in a git repo" >&2; exit 1; }

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
```

## Preconditions

- Worktree dirs already exist (created via `/worktree <n>` or `scripts/new-worktree.sh`). This skill does **not** create them.
- The target terminal app installed and running (iTerm2 / Terminal.app on macOS; Konsole on Linux).
- Invoked from inside the GankedTV repo (so `git rev-parse --show-toplevel` resolves correctly).
- `gh` authenticated (only needed for the descriptive title — the script still works without it).

## When NOT to use

- Worktrees not yet created → run `/worktree <n>` first.
- Another process is bound to the worktree's web/api ports (e.g. a leftover `make dev-all` tab) → `make web` / `make server` will fail to bind. Close the conflicting tab first.

## Shell title rewriting (applies to all terminals)

zsh/bash with default prompt configs frequently rewrite the tab/window title on every prompt, which can stomp names set programmatically. If names disappear after the first command, either:

- disable shell title updates (zsh: `DISABLE_AUTO_TITLE=true` for oh-my-zsh, or remove `precmd`/`preexec` title hooks)
- iTerm2: Preferences → Profiles → Terminal → **uncheck** "Terminal may set tab/window title"
- Terminal.app: no equivalent setting; the AppleScript `custom title` is sticky against shell OSC-0 sequences in recent macOS versions, but YMMV.
- Konsole: Settings → Configure Konsole → Profiles → Edit Profile → Tabs → set "Tab title format" to a fixed string, or disable "Show shell-set title" — Konsole otherwise lets the shell win over the `--tabs-from-file` `title:` value.

See the sub-files for the per-terminal mechanism used to set names.
