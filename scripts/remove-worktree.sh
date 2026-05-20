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
shift

dir="../gankedtv-issue-${issue}"
if [[ ! -d "$dir" ]]; then
  echo "error: no worktree at $dir" >&2
  exit 1
fi

# Tear down with the same --env-file flag the worktree's Makefile uses, so
# COMPOSE_PROJECT_NAME (auto-derived) and the port vars resolve identically
# to the `up`. -v removes the named volumes (gankedtv-issue-<n>_postgres_data,
# _minio_data) — dev data is intentionally throwaway.
if [[ -f "$dir/.env.worktree.local" ]]; then
  (cd "$dir" && docker-compose -f docker-compose.dev.yml --env-file .env.worktree.local down -v)
else
  echo "warning: $dir/.env.worktree.local missing — falling back to bare compose down" >&2
  (cd "$dir" && docker-compose -f docker-compose.dev.yml down -v)
fi

git worktree remove "$dir" "$@"
echo "✓ removed $dir"
