#!/usr/bin/env bash
#
# Tear down a worktree created by scripts/new-worktree.sh: stop containers,
# remove named volumes (data is throwaway), and remove the worktree.
#
# Usage: ./scripts/remove-worktree.sh <issue-number> [--force]
#
# Extra flags after the issue number pass through to `git worktree remove`
# (e.g. --force to discard uncommitted changes).

set -euo pipefail

issue="${1:-}"
if [[ -z "$issue" ]]; then
  echo "usage: $0 <issue-number> [--force]" >&2
  exit 1
fi
issue="${issue#\#}"
if ! [[ "$issue" =~ ^[0-9]+$ ]]; then
  echo "error: issue must be numeric (got '$issue')" >&2
  exit 1
fi
# Canonicalize to match scripts/new-worktree.sh: collapse "042" → "42" so the
# constructed path matches whichever form was used at create time. 10# forces
# base-10 interpretation (avoids bash treating leading-zero strings as octal).
issue=$((10#$issue))
if (( issue < 1 )); then
  echo "error: issue number must be positive (got '$issue')" >&2
  exit 1
fi
shift

# Anchor to repo root so the script works regardless of cwd, matching the path
# layout produced by scripts/new-worktree.sh.
repo_root=$(git rev-parse --show-toplevel)
rel_dir=".worktrees/issue-${issue}"
dir="${repo_root}/${rel_dir}"
if [[ ! -d "$dir" ]]; then
  echo "error: no worktree at ${rel_dir}" >&2
  exit 1
fi

# Capture the worktree's branch before removal so we can offer to delete it
# afterwards. Without this, the local branch sticks around after teardown and
# the next `new-worktree.sh <same-issue>` would pick it up via the script's
# refs/heads/* check — which bit us during testing when the stale branch
# pointed at an older commit than origin.
branch=$(git -C "$dir" branch --show-current 2>/dev/null || true)

# Detect --force in passthrough args (also forwarded to git worktree remove).
# When set, escalate the branch deletion from -d (safe, refuses unmerged) to
# -D (force).
force_branch_delete=0
for arg in "$@"; do
  if [[ "$arg" == "--force" ]]; then force_branch_delete=1; fi
done

# Tear down with the same --env-file flag the worktree's Makefile uses, so
# COMPOSE_PROJECT_NAME (now set explicitly in .env.worktree.local) and the port
# vars resolve identically to the `up`. -v removes the named volumes
# (gankedtv-issue-<n>_postgres_data, _minio_data) — dev data is intentionally
# throwaway.
if [[ -f "$dir/.env.worktree.local" ]]; then
  (cd "$dir" && docker-compose -f docker-compose.dev.yml --env-file .env.worktree.local down -v)
else
  echo "warning: ${rel_dir}/.env.worktree.local missing — falling back to bare compose down" >&2
  (cd "$dir" && docker-compose -f docker-compose.dev.yml down -v)
fi

git worktree remove "$dir" "$@"

# `git worktree remove` only deletes git-tracked files — gitignored build
# artifacts (server/*/obj/, server/*/bin/, web/node_modules/, dist/) survive and
# leave a half-empty directory behind. We just ran `docker compose down -v` so
# we're already in destructive mode; finish the cleanup so .worktrees/ doesn't
# accumulate stale tree skeletons across iterations.
rm -rf "$dir"

# Tidy up the empty .worktrees/ root once the last worktree is gone, so the
# main checkout doesn't carry an empty stub directory around.
rmdir "${repo_root}/.worktrees" 2>/dev/null || true

echo "✓ removed ${rel_dir}"

# Delete the local branch — without this, the next `new-worktree.sh <same-issue>`
# would check out the stale ref instead of taking the latest from origin/main.
# Use -d by default (safe, refuses unmerged). --force on the script call
# escalates to -D for the case where the user genuinely wants to drop the
# branch including unpushed work.
if [[ -n "$branch" ]] && git show-ref --verify --quiet "refs/heads/${branch}"; then
  if (( force_branch_delete )); then
    git branch -D "$branch"
    echo "✓ branch $branch deleted (forced)"
  elif git branch -d "$branch" 2>/dev/null; then
    echo "✓ branch $branch deleted"
  else
    echo "  branch $branch retained — it has unmerged commits"
    echo "  delete manually:  git branch -D $branch"
  fi
fi
